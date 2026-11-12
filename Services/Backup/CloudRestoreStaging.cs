using System.Security.AccessControl;
using System.Security.Principal;
using FMSC_SMS.Core;

namespace FMSC_SMS.Services.Backup;

/// <summary>
/// Cloud restore is a two-stage operation: download and decrypt to a private
/// local folder, then hand the .bak to the EXISTING SQL restore manager.
/// Nothing in the cloud transport may modify the live database.
/// </summary>
internal static class CloudRestoreStaging
{
    internal sealed record DownloadCandidate(
        Func<string, CancellationToken, Task> Download,
        string HistoryPath,
        string Label);

    internal static Task<DatabaseBackupManager.BackupResult> RestoreAsync(
        Func<string, CancellationToken, Task> downloadPackage,
        string recoveryPassphrase,
        string cloudHistoryPath,
        CancellationToken cancellationToken = default)
        => RestoreWithFallbackAsync(
            new[] { new DownloadCandidate(downloadPackage, cloudHistoryPath, "Cloud") },
            recoveryPassphrase, cancellationToken);

    /// <summary>
    /// Each candidate is an alternate physical copy of exactly the same
    /// recovery point. Fallback is permitted ONLY while acquiring, decrypting
    /// and validating a candidate, never after SQL restoration begins.
    /// </summary>
    internal static async Task<DatabaseBackupManager.BackupResult> RestoreWithFallbackAsync(
        IReadOnlyList<DownloadCandidate> candidates, string recoveryPassphrase,
        CancellationToken cancellationToken = default,
        IProgress<BackupProgressUpdate>? progress = null)
    {
        if (candidates.Count == 0)
            return new DatabaseBackupManager.BackupResult {
                Success = false, Message = "No eligible backup copies were found for this recovery point." };
        string root = Path.Combine(DatabaseHelper.DatabaseFolder, "CloudRestoreStaging");
        var errors = new List<string>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string folder = Path.Combine(root, Guid.NewGuid().ToString("N"));
            string cloudPackage = Path.Combine(folder, "selected.fmscbackup");
            string bak = Path.Combine(folder, "verified-download.bak");
            bool sqlRestoreStarted = false;
            try
            {
                Directory.CreateDirectory(root);
                // Protect evidence from past SQL-stage failures. Earlier failed
                // acquisitions are immediately cleaned and do not count here.
                if (Directory.EnumerateDirectories(root).Take(5).Count() >= 5)
                    throw new InvalidOperationException(
                        "Five previous cloud restore attempts require administrator review. " +
                        "Preserve recovery evidence before clearing CloudRestoreStaging.");
                Directory.CreateDirectory(folder);
                RestrictToCurrentWindowsUser(folder);
                progress?.Report(new BackupProgressUpdate("Downloading encrypted backup", "Source " + candidate.Label));
                await candidate.Download(cloudPackage, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(cloudPackage) || new FileInfo(cloudPackage).Length == 0)
                    throw new InvalidDataException("The cloud download was incomplete or empty.");
                progress?.Report(new BackupProgressUpdate("Decrypting downloaded backup", "Authenticating the complete encrypted package."));
                await Task.Run(() => CloudBackupCrypto.Decrypt(cloudPackage, bak, recoveryPassphrase),
                    cancellationToken).ConfigureAwait(false);
                progress?.Report(new BackupProgressUpdate("Validating SQL backup header", "Checking the authenticated backup identity."));
                var info = DatabaseBackupManager.GetBackupInfo(bak);
                if (info is null || !info.IsForFmscDatabase)
                    throw new InvalidDataException("The downloaded file is not a readable FMSC SQL backup.");
                DateTime packagedSqlTime = CloudBackupCrypto.ReadSqlHeaderLocalAfterVerification(cloudPackage);
                if ((packagedSqlTime - info.BackupStartDate).Duration() > TimeSpan.FromSeconds(1))
                    throw new InvalidDataException("The authenticated package timestamp does not match the SQL backup header.");
                cancellationToken.ThrowIfCancellationRequested();
                // Full isolated SQL preflight is still before live restoration.
                // An unusable downloaded copy can safely fall back to another
                // physical copy of exactly this recovery point at this stage.
                progress?.Report(new BackupProgressUpdate("Testing isolated SQL recovery",
                    "Checking SQL integrity, school identity and schema without modifying live data."));
                await Task.Run(() => DatabaseBackupManager.VerifyRecoveryCandidate(bak),
                    cancellationToken).ConfigureAwait(false);
                string safetyFolder = BackupSettings.GetBackupFolder();
                if (safetyFolder.Length == 0)
                    safetyFolder = Path.Combine(DatabaseHelper.DatabaseFolder, "Backups");
                string stagePath = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string safetyPath = Path.GetFullPath(safetyFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (safetyPath.StartsWith(stagePath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The safety-backup folder cannot be inside cloud staging.");
                // No second candidate is considered after this line, even if
                // SQL's preflight or mandatory rollback later fails.
                sqlRestoreStarted = true;
                progress?.Report(new BackupProgressUpdate("Checking school identity and taking a safety backup",
                    "No alternate copy will be attempted after SQL restore processing begins."));
                var stageReporter = new Progress<string>(message =>
                    progress?.Report(new BackupProgressUpdate(message, "SQL restore security and safety stages.")));
                var result = await Task.Run(() => DatabaseBackupManager.RestoreBackup(bak,
                    progress: stageReporter,
                    safetyBackupFolderOverride: safetyFolder,
                    historyPathOverride: candidate.HistoryPath)).ConfigureAwait(false);
                if (result.Success)
                {
                    try { Directory.Delete(folder, recursive: true); }
                    catch (Exception cleanupError)
                    {
                        AppExceptionHandler.LogNonFatal(cleanupError, "Completed cloud restore staging cleanup");
                        return new DatabaseBackupManager.BackupResult {
                            Success = true,
                            Message = result.Message + "\n\nProtected download cleanup needs administrator attention." };
                    }
                    progress?.Report(new BackupProgressUpdate("Restore completed", "Recovery from " + candidate.Label + " passed SQL verification."));
                    return new DatabaseBackupManager.BackupResult {
                        Success = true, Message = result.Message + "\nRecovered from " + candidate.Label + "." };
                }
                return result;
            }
            catch (Exception ex)
            {
                AppExceptionHandler.LogNonFatal(ex, "Cloud restore failed from " + candidate.Label);
                if (sqlRestoreStarted)
                    return new DatabaseBackupManager.BackupResult {
                        Success = false, Message = "Cloud SQL restoration failed. No alternate provider was attempted after SQL restoration started. " +
                            DbMessage.UserMessage(ex) };
                progress?.Report(new BackupProgressUpdate("Source rejected before SQL restore",
                    DbMessage.UserMessage(ex) + " Trying another copy of the same recovery point if available.", candidate.Label, true));
                errors.Add(candidate.Label + ": " + DbMessage.UserMessage(ex));
            }
            finally
            {
                // All pre-SQL failures remove incomplete downloads and plaintext.
                // Preserve the stage of an unsuccessful SQL-stage attempt.
                if (!sqlRestoreStarted && Directory.Exists(folder))
                {
                    try { Directory.Delete(folder, recursive: true); }
                    catch (Exception cleanupError)
                    {
                        AppExceptionHandler.LogNonFatal(cleanupError, "Cleaning rejected cloud restore candidate");
                    }
                }
            }
        }
        return new DatabaseBackupManager.BackupResult {
            Success = false,
            Message = "No source of the selected cloud recovery point passed acquisition and verification. " +
                "The live school database was not changed.\n" + string.Join("\n", errors) };
    }

    /// <summary>
    /// Recovery-test the actual remote object (not the pre-upload local source).
    /// SQL is restored only into the disposable candidate catalog; the live
    /// FMSC_DB database is never changed. All downloaded staging data is removed.
    /// </summary>
    internal static async Task VerifyRemoteCopyAsync(
        Func<string, CancellationToken, Task> downloadPackage,
        string recoveryPassphrase, DateTime expectedSqlCreatedUtc,
        CancellationToken token = default)
    {
        string root = Path.Combine(DatabaseHelper.DatabaseFolder, "CloudVerificationStaging");
        string folder = Path.Combine(root, Guid.NewGuid().ToString("N"));
        string package = Path.Combine(folder, "remote.fmscbackup");
        string bak = Path.Combine(folder, "verified-remote.bak");
        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(folder);
            RestrictToCurrentWindowsUser(folder);
            await downloadPackage(package, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            await Task.Run(() =>
            {
                CloudBackupCrypto.Decrypt(package, bak, recoveryPassphrase);
                var info = DatabaseBackupManager.GetBackupInfo(bak);
                if (info is null || !info.IsForFmscDatabase || info.BackupStartDate == default)
                    throw new InvalidDataException("The remotely downloaded backup has no valid FMSC SQL header.");
                DateTime originalSqlTime = CloudBackupCrypto.ReadSqlHeaderLocalAfterVerification(package);
                if ((originalSqlTime - info.BackupStartDate).Duration() > TimeSpan.FromSeconds(1))
                    throw new InvalidDataException("Downloaded cloud package and SQL header creation times differ.");
                DateTime authenticatedUtc = CloudBackupCrypto.ReadUnverifiedCreationUtc(package);
                if ((authenticatedUtc - expectedSqlCreatedUtc).Duration() > TimeSpan.FromSeconds(1))
                    throw new InvalidDataException("Downloaded cloud recovery point identity differs from the uploaded backup.");
                // VerifyRecoveryCandidate checks RESTORE VERIFYONLY, school identity,
                // schema compatibility and a disposable isolated SQL restore.
                DatabaseBackupManager.VerifyRecoveryCandidate(bak);
            }, token).ConfigureAwait(false);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                try { Directory.Delete(folder, recursive: true); }
                catch (Exception ex)
                {
                    AppExceptionHandler.LogNonFatal(ex, "Cleaning isolated cloud verification staging");
                    // Do not report a clean verification if plaintext staging
                    // could not be removed; administrator review is required.
                    throw new IOException("Cloud verification completed but protected temporary files require cleanup.", ex);
                }
            }
        }
    }

    private static void RestrictToCurrentWindowsUser(string path)
    {
        var user = WindowsIdentity.GetCurrent().User
            ?? throw new UnauthorizedAccessException("Windows backup owner could not be identified.");
        var acl = new DirectorySecurity();
        acl.SetOwner(user);
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        acl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(acl);
    }
}
