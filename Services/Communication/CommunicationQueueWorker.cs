using System.Data;
using System.Security.Cryptography;
using FMSC_SMS.Core;
using FMSC_SMS.Pages.Security;
using FMSC_SMS.Services.Backup;
using Microsoft.Data.SqlClient;

namespace FMSC_SMS.Services.Communication;

/// <summary>
/// Bounded, already-reviewed API dispatch only. Nothing is sent merely because
/// a message exists in SQL. A worker never approves drafts or retries Unknown
/// provider outcomes and never opens manual WhatsApp chats.
/// </summary>
internal static class CommunicationQueueWorker
{
    internal static async Task<int> RunAsync(CancellationToken token = default)
    {
        if (!CommunicationWorkerEnrollment.HasLocalCredential) return 0;
        if (!CloudBackupCoordinator.IsUpdaterIdleForBackup()) return 3;
        CommunicationWorkerEnrollment.Credential? credential = null;
        SqlConnection? lease = null;
        try
        {
            credential = CommunicationWorkerEnrollment.Load();
            lease = DatabaseHelper.GetConnection();
            DatabaseHelper.OpenConnection(lease);
            DatabaseCompatibility.Validate(lease);
            using (var acquire = new SqlCommand("""
                DECLARE @lease int;
                EXEC @lease=sys.sp_getapplock @Resource=N'FMSC_CommunicationWorker',
                    @LockMode=N'Exclusive',@LockOwner=N'Session',@LockTimeout=0;
                SELECT @lease;
                """, lease) { CommandTimeout = 10 })
            {
                if (Convert.ToInt32(await acquire.ExecuteScalarAsync(token)) < 0) return 0;
            }
            using var identityRows = await CommunicationManagementStore.ReadWorkerIdentityAsync(
                credential.WorkerId, credential.Token, credential.WindowsProof, credential.DeviceProof);
            if (identityRows.Rows.Count != 1)
                throw new UnauthorizedAccessException("The worker identity is not registered.");
            DataRow identity = identityRows.Rows[0];
            int actor = Convert.ToInt32(identity["UserId"]);
            bool mustChange = Convert.ToBoolean(identity["MustChangePassword"]);
            if (actor <= 0 || mustChange)
                throw new UnauthorizedAccessException("Interactive administrator verification is required.");
            using var permissionRows = DatabaseHelper.Query(
                "GetUserPermissions", DatabaseHelper.Int("@UserId", actor));
            string[] permissions = permissionRows.AsEnumerable()
                .Select(row => Convert.ToString(row["PermissionCode"]) ?? "")
                .Where(p => p.Length > 0).ToArray();
            CurrentUser.SignIn(actor, Convert.ToString(identity["Username"]) ?? "",
                Convert.ToString(identity["RoleName"]) ?? "", mustChange, permissions);
            if (!CurrentUser.HasPermission("Communication.Send"))
                throw new UnauthorizedAccessException("Communication sending permission was revoked.");

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(45));
            using var ready = await CommunicationManagementStore.ReadReadyWorkerRecipientsAsync(
                credential.WorkerId, credential.Token, credential.WindowsProof, credential.DeviceProof);
            foreach (DataRow item in ready.Rows)
            {
                deadline.Token.ThrowIfCancellationRequested();
                // Revalidate revocation and account credential generation on
                // every item rather than trusting the initial SQL projection.
                using var live = await CommunicationManagementStore.ReadWorkerIdentityAsync(
                    credential.WorkerId, credential.Token, credential.WindowsProof, credential.DeviceProof);
                if (live.Rows.Count != 1 || Convert.ToInt32(live.Rows[0]["UserId"]) != actor)
                    throw new UnauthorizedAccessException("The worker authorization was revoked.");
                long recipientId = Convert.ToInt64(item["RecipientId"]);
                string channel = Convert.ToString(item["Channel"]) ?? "";
                try
                {
                    var result = await CommunicationDispatchStore.SendOneReviewedAsync(
                        recipientId, channel, deadline.Token);
                    // A possibly-submitted message must never trigger a retry.
                    if (result.State == "Unknown") break;
                }
                catch (CommunicationValidationException error)
                {
                    // Changes in contact, consent, provider account or message
                    // must never silently discard an approved batch. Pause the
                    // complete schedule for an authorized human review.
                    AppExceptionHandler.LogNonFatal(error, "Scheduled batch requires review");
                    await CommunicationManagementStore.PauseScheduledBatchAsync(
                        (Guid)item["BatchId"], "Recipient, provider or reviewed message changed");
                    break;
                }
            }
            return 0;
        }
        catch (OperationCanceledException) { return 3; }
        catch (Exception error)
        {
            AppExceptionHandler.LogNonFatal(error, "Unattended communication queue stopped");
            return 1;
        }
        finally
        {
            CurrentUser.SignOut();
            if (lease is not null)
            {
                try
                {
                    if (lease.State == ConnectionState.Open)
                    {
                        using var release = new SqlCommand("""
                            EXEC sys.sp_releaseapplock
                                @Resource=N'FMSC_CommunicationWorker',@LockOwner=N'Session';
                            """, lease);
                        await release.ExecuteNonQueryAsync(CancellationToken.None);
                    }
                }
                catch (Exception error)
                {
                    AppExceptionHandler.LogNonFatal(error, "Communication worker lease release failed");
                }
                lease.Dispose();
            }
            if (credential is not null) CryptographicOperations.ZeroMemory(credential.Token);
        }
    }
}
