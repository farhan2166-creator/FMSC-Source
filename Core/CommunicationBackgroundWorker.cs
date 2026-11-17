using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace FMSC_SMS.Core;

/// <summary>
/// One short, unattended, authorized processing pass. Windows Task Scheduler
/// starts it under the enrolled user's DPAPI context; SQL controls single
/// worker ownership, school windows, due dates and every attempt claim.
/// </summary>
internal static class CommunicationBackgroundWorker
{
    internal static Task<int> RunScheduledAsync(CancellationToken token=default)
    {
        // Both the complete installer and updater first take the activation
        // mutex, then wait for this worker mutex to finish its current attempt.
        // No installer can activate files while an API POST is in flight.
        using var workerMutex=new Mutex(false,@"Global\FMSCCommunicationWorker");
        bool owns=false;
        try { owns=workerMutex.WaitOne(0); }
        catch(AbandonedMutexException) { owns=true; }
        if(!owns) return Task.FromResult(0);
        try
        {
            if(!IsUpdaterIdle()) return Task.FromResult(0);
            // OS mutexes are thread-owned. Keep this headless invocation
            // synchronous on the acquiring thread across awaited provider
            // operations; releasing a Mutex on an async continuation thread
            // would fail and could permit unsafe installer coordination.
            return Task.FromResult(RunAuthorizedAsync(token).GetAwaiter().GetResult());
        }
        finally { workerMutex.ReleaseMutex(); }
    }

    private static bool IsUpdaterIdle()
    {
        try
        {
            if(Mutex.TryOpenExisting(@"Global\FMSCInstallerActivation",out var activation))
            {
                using(activation)
                {
                    bool acquired;
                    try { acquired=activation.WaitOne(0); }
                    catch(AbandonedMutexException) { acquired=true; }
                    if(!acquired)return false;
                    activation.ReleaseMutex();
                }
            }
            string root=Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "FMSC_Updater","recovery.json");
            if(!File.Exists(root))return true;
            byte[] data=File.ReadAllBytes(root);
            return data.Length<=32768 && !Updates.UpdateStartupGuard.RequiresRecovery(data);
        }
        catch(Exception ex)
        {
            AppExceptionHandler.LogNonFatal(ex,"Communication background processor deferred during update or recovery");
            return false;
        }
    }

    private static async Task<int> RunAuthorizedAsync(CancellationToken token)
    {
        if (!CommunicationBackgroundEnrollment.HasLocalEnrollment) return 0;
        Guid leaseNonce=Guid.NewGuid();
        CommunicationBackgroundEnrollment.Enrollment? enrollment=null;
        bool leaseAcquired=false, signedIn=false;
        try
        {
            enrollment=CommunicationBackgroundEnrollment.Read();
            long databaseWatermark=await CommunicationBackgroundEnrollment.VerifyAsync(enrollment,token);
            // This is a narrowly scoped internal worker identity. The SQL
            // enrollment and underlying school role are rechecked each run,
            // and SQL itself validates staff/guardian permissions at claim.
            CurrentUser.SignIn(enrollment.UserId,"communication-background","Communication Worker",
                false,new[] { "Communication.Send","Communication.View",
                    "Students.View","Fees.View","Staff.View" });
            signedIn=true;
            using(var claimed=await DatabaseHelper.QueryUserAsync("msg.AcquireBackgroundLease",token,
                CommunicationBackgroundEnrollment.Proof(enrollment,leaseNonce)))
            {
                leaseAcquired=claimed.Rows.Count==1 &&
                    claimed.Rows[0]["Acquired"]!=DBNull.Value &&
                    Convert.ToBoolean(claimed.Rows[0]["Acquired"]);
            }
            if (!leaseAcquired) return 0;

            // Never run against an older SQL history than the independently
            // protected, durable pre-HTTP attempt journal.
            if(databaseWatermark<enrollment.MaxObservedAttemptId)
            {
                await DatabaseHelper.ExecuteUserAsync("msg.ReportWorkerStateDivergence",
                    DatabaseHelper.P("@EnrollmentId",enrollment.EnrollmentId),
                    DatabaseHelper.P("@LeaseNonce",leaseNonce));
                return 21;
            }
            CommunicationBackgroundEnrollment.JournalObserved(databaseWatermark);

            // Prepare-for-Review rules discover source events during the
            // authorized worker's periodic pass, even when the WinForms UI is
            // closed. The scanner only stores idempotent review suggestions:
            // it cannot confirm batches, enqueue dispatch or contact APIs.
            // Failure to read a fee/student source must not prevent an
            // otherwise authorized, already reviewed API batch from sending.
            try
            {
                using var slot=await DatabaseHelper.QuerySqlAsync(
                    "EXEC msg.ClaimAutomationReviewScan @UserId;",
                    token,DatabaseHelper.Int("@UserId",enrollment.UserId));
                if(slot.Rows.Count!=1)
                    throw new InvalidDataException("School event scan schedule is unavailable.");
                if(Convert.ToBoolean(slot.Rows[0]["ShouldScan"]))
                {
                    using var suggestions = await DatabaseHelper.QuerySqlAsync(
                        "EXEC msg.ScanAutomationCandidates @UserId;",
                        token, DatabaseHelper.Int("@UserId",enrollment.UserId));
                    using var feeSuggestions = await DatabaseHelper.QuerySqlAsync(
                        "EXEC msg.ScanFeeReviewCandidates @UserId;",
                        token, DatabaseHelper.Int("@UserId",enrollment.UserId));
                }
            }
            catch(SqlException ex) when(ex.Number==50003)
            {
                AppExceptionHandler.LogNonFatal(ex,
                    "Communication event review scan needs renewed school permissions");
            }

            // Current computer authorization is also required before preparing
            // unattended approvals; enrollment alone is not machine approval.
            var planAuthorization=await Updates.UpdateGitReader.CheckAsync(token);
            if(!planAuthorization.Authorized)return 22;
            await CommunicationPlannedWorkStore.PrepareDueAsync(enrollment,leaseNonce,token);

            var started=DateTime.UtcNow;
            bool computerAuthorizationChecked=true; // Verified before unattended preparation above.
            bool skipSms=false, skipWhatsApp=false;
            var deferredBatches=new HashSet<Guid>();
            // Bounded processor invocation: other logon-bound task invocations
            // cannot overtake this one while its lease is healthy.
            while(DateTime.UtcNow-started<TimeSpan.FromSeconds(45))
            {
                token.ThrowIfCancellationRequested();
                await DatabaseHelper.ExecuteUserAsync("msg.RenewBackgroundLease",token,
                    CommunicationBackgroundEnrollment.Proof(enrollment,leaseNonce));
                using var due=await DatabaseHelper.QueryUserAsync("msg.ReadNextScheduledRecipient",token,
                    DatabaseHelper.P("@EnrollmentId",enrollment.EnrollmentId),
                    DatabaseHelper.P("@LeaseNonce",leaseNonce),
                    DatabaseHelper.Bit("@SkipSms",skipSms),
                    DatabaseHelper.Bit("@SkipWhatsApp",skipWhatsApp),
                    DatabaseHelper.P("@DeferredBatches",JsonSerializer.Serialize(deferredBatches)));
                if(due.Rows.Count==0) break;
                DataRow next=due.Rows[0];
                long recipientId=Convert.ToInt64(next["RecipientId"]);
                Guid batchId=(Guid)next["BatchId"];
                string channel=Convert.ToString(next["Channel"]) ??
                    throw new InvalidDataException("Scheduled recipient has no approved channel.");
                int author=Convert.ToInt32(next["CreatedBy"]);
                string audience=Convert.ToString(next["Audience"]) ?? "";
                try
                {
                    // Verify original sender retains permissions for the
                    // specific audience, not just generic sending access.
                    // The SQL claim independently checks again before HTTP.
                    string permissionSql =
                        "EXEC dbo.RequirePermission @UserId,N'Communication.Send'; " +
                        (audience=="Staff"
                            ? "EXEC dbo.RequirePermission @UserId,N'Staff.View'; "
                            : "EXEC dbo.RequirePermission @UserId,N'Students.View'; ") +
                        (audience is "OutstandingFees" or "FeeStudent" or "FeeFamily"
                            ? "EXEC dbo.RequirePermission @UserId,N'Fees.View'; "
                            : "") +
                        "SELECT CONVERT(bit,1) Authorized;";
                    using var sender=await DatabaseHelper.QuerySqlAsync(
                        permissionSql,token,DatabaseHelper.Int("@UserId",author));
                    if(sender.Rows.Count!=1) throw new UnauthorizedAccessException(
                        "Original sender is no longer authorized.");
                }
                catch(SqlException ex) when(ex.Number==50003)
                {
                    await DatabaseHelper.ExecuteUserAsync("msg.HoldScheduledBatch",
                        DatabaseHelper.P("@BatchId",batchId));
                    continue;
                }

                if(!computerAuthorizationChecked)
                {
                    // A DPAPI enrollment alone is not evidence that this
                    // computer's existing read-only GitHub authorization is
                    // still approved. Fail closed on revocation or offline
                    // status, before reserving any chargeable SQL attempt.
                    var authorization=await Updates.UpdateGitReader.CheckAsync(token);
                    if(!authorization.Authorized)
                    {
                        AppExceptionHandler.LogNonFatal(
                            new UnauthorizedAccessException("Scheduled sending requires current computer approval."),
                            "Communication background worker paused: authorization not verifiable");
                        return 22;
                    }
                    computerAuthorizationChecked=true;
                }

                // The SQL attempt moves to Unknown before the external POST.
                // Persist a higher disk watermark synchronously before that
                // network request can start, without blindly retrying Unknown.
                try
                {
                    var result=await CommunicationDispatchStore.SendOneWithClaimJournalAsync(
                        recipientId,channel,CommunicationBackgroundEnrollment.JournalClaim,
                        CommunicationBackgroundEnrollment.Proof(enrollment,leaseNonce),token);
                    if(result.State=="Unknown") break;
                }
                catch(SqlException ex) when(ex.Number is 51092 or 51249 or 51250)
                {
                    // Channel pause/quota/speed changes defer only this channel.
                    // No failed or uncertain attempt is returned to Pending.
                    if(channel=="Sms") skipSms=true; else skipWhatsApp=true;
                }
                catch(SqlException ex) when(ex.Number is 51293 or 51294 or 51420)
                {
                    deferredBatches.Add(batchId);
                }
                catch(CommunicationValidationException ex) when(ex.IsTemporary)
                {
                    if(channel=="Sms") skipSms=true; else skipWhatsApp=true;
                }
                catch(Exception ex) when(ex is CommunicationValidationException ||
                    ex is SqlException { Number: 51133 or 51248 })
                {
                    // Stale review, unavailable credentials or missing allowance
                    // needs operator attention. Do not let it block other batches.
                    await DatabaseHelper.ExecuteUserAsync("msg.HoldScheduledBatch",
                        DatabaseHelper.P("@BatchId",batchId));
                }
                if(deferredBatches.Count>=80) break;
            }
            return 0;
        }
        catch(SqlException ex) when(ex.Number is 51261 or 51262 or 51263 or 51240 or 51241 or 51249 or 51250 or 51251 or 51293 or 51294)
        {
            AppExceptionHandler.LogNonFatal(ex,"Communication processor paused or restricted");
            return 20;
        }
        catch(OperationCanceledException)
        {
            return 20;
        }
        catch(Exception ex)
        {
            AppExceptionHandler.LogNonFatal(ex,"Communication background worker blocked");
            return 23;
        }
        finally
        {
            if(leaseAcquired && enrollment is not null && signedIn)
            {
                try
                {
                    await DatabaseHelper.ExecuteUserAsync("msg.ReleaseBackgroundLease",
                        DatabaseHelper.P("@EnrollmentId",enrollment.EnrollmentId),
                        DatabaseHelper.P("@LeaseNonce",leaseNonce));
                }
                catch(Exception ex)
                {
                    AppExceptionHandler.LogNonFatal(ex,"Releasing communication processor lease");
                }
            }
            if(signedIn)CurrentUser.SignOut();
        }
    }
}
