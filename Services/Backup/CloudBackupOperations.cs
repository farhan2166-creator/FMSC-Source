namespace FMSC_SMS.Services.Backup;

internal sealed record CloudBackupOutcome(bool LocalSucceeded, bool GoogleSucceeded,
    bool DropboxSucceeded, string Message, bool UploadsPending = false);

internal sealed record CloudBackupInventory(IReadOnlyList<CloudRemoteBackup> Copies,
    IReadOnlyList<string> Warnings);

internal static class CloudBackupOperations
{
    // Local success and a healthy no-due run are distinct from undelivered
    // cloud copies. Keep the queue and expose incomplete delivery to Windows.
    internal static int ScheduledExitCode(CloudBackupOutcome outcome) => outcome.UploadsPending ? 4 : 0;

    internal static async Task<CloudBackupInventory> BrowseAsync(
        Func<CloudProvider, bool> connected,
        Func<CloudProvider, CancellationToken, Task<IReadOnlyList<CloudRemoteBackup>>> list,
        Action<CloudProvider, Exception> logFailure, CancellationToken token)
    {
        var copies = new List<CloudRemoteBackup>();
        var warnings = new List<string>();
        foreach (var provider in new[] { CloudProvider.GoogleDrive, CloudProvider.Dropbox })
        {
            token.ThrowIfCancellationRequested();
            if (!connected(provider)) continue;
            try { copies.AddRange((await list(provider, token).ConfigureAwait(false)).Where(copy => copy.Size > 0)); }
            catch (Exception ex) when (CloudRecoverySourceFailure.CanTryAnotherCopy(ex, token)
                || ex is InvalidOperationException)
            {
                logFailure(provider, ex);
                warnings.Add(provider + " could not be checked. Check the connection or reconnect the account; its backups may still be available.");
            }
        }
        return new(copies.OrderByDescending(copy => copy.SqlBackupCreatedUtc).ToArray(), warnings);
    }
}
