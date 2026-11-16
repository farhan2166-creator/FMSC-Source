using System.Data;
using Microsoft.Data.SqlClient;

namespace FMSC_SMS.Core;

/// <summary>
/// One short, unattended, authorized processing pass. Windows Task Scheduler
/// starts it under the enrolled user's DPAPI context; SQL controls single
/// worker ownership, school windows, due dates and every attempt claim.
/// </summary>
internal static class CommunicationBackgroundWorker
{
    internal static async Task<int> RunScheduledAsync(CancellationToken token=default)
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

            var started=DateTime.UtcNow;
            // Bounded processor invocation: other logon-bound task invocations
            // cannot overtake this one while its lease is healthy.
            while(DateTime.UtcNow-started<TimeSpan.FromSeconds(45))
            {
                token.ThrowIfCancellationRequested();
                await DatabaseHelper.ExecuteUserAsync("msg.RenewBackgroundLease",token,
                    CommunicationBackgroundEnrollment.Proof(enrollment,leaseNonce));
                using var due=await DatabaseHelper.QueryUserAsync("msg.ReadNextScheduledRecipient",token,
                    DatabaseHelper.P("@EnrollmentId",enrollment.EnrollmentId),
                    DatabaseHelper.P("@LeaseNonce",leaseNonce));
                if(due.Rows.Count==0) break;
                DataRow next=due.Rows[0];
                long recipientId=Convert.ToInt64(next["RecipientId"]);
                Guid batchId=(Guid)next["BatchId"];
                string channel=Convert.ToString(next["Channel"]) ??
                    throw new InvalidDataException("Scheduled recipient has no approved channel.");
                int author=Convert.ToInt32(next["CreatedBy"]);
                try
                {
                    // Verify original sender remains authorized. The SQL
                    // claim independently checks again before any HTTP call.
                    using var sender=await DatabaseHelper.QuerySqlAsync(
                        "EXEC dbo.RequirePermission @UserId,N'Communication.Send'; SELECT CONVERT(bit,1) Authorized;",
                        token,DatabaseHelper.Int("@UserId",author));
                    if(sender.Rows.Count!=1) throw new UnauthorizedAccessException(
                        "Original sender is no longer authorized.");
                }
                catch(SqlException ex) when(ex.Number==50003)
                {
                    await DatabaseHelper.ExecuteUserAsync("msg.HoldScheduledBatch",
                        DatabaseHelper.P("@BatchId",batchId));
                    continue;
                }

                // The SQL attempt moves to Unknown before the external POST.
                // Persist a higher disk watermark synchronously before that
                // network request can start, without blindly retrying Unknown.
                var result=await CommunicationDispatchStore.SendOneWithClaimJournalAsync(
                    recipientId,channel,CommunicationBackgroundEnrollment.JournalClaim,token);
                if(result.State=="Unknown") break;
            }
            return 0;
        }
        catch(SqlException ex) when(ex.Number is 51261 or 51262 or 51263 or 51240 or 51241 or 51249 or 51250 or 51251)
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
