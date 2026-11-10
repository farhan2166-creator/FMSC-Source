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
    internal static async Task<DatabaseBackupManager.BackupResult> RestoreAsync(
        Func<string, CancellationToken, Task> downloadPackage,
        string recoveryPassphrase,
        string cloudHistoryPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(downloadPackage);
        // A new unique directory for each attempt prevents stale files from
        // being mistaken for a completed download or a current restore source.
        string root = Path.Combine(DatabaseHelper.DatabaseFolder, "CloudRestoreStaging");
        string folder = Path.Combine(root, Guid.NewGuid().ToString("N"));
        string cloudPackage = Path.Combine(folder, "selected.fmscbackup");
        string bak = Path.Combine(folder, "verified-download.bak");
        bool databaseRestored = false;
        bool sqlRestoreStarted = false;
        try
        {
            Directory.CreateDirectory(root);
            // Never silently delete a post-SQL failure's recovery evidence.
            // Cap those retained attempts so repeated failures cannot fill
            // the same volume as the school's live database.
            if (Directory.EnumerateDirectories(root).Take(5).Count() >= 5)
                throw new InvalidOperationException(
                    "Five previous cloud restore attempts require administrator review. " +
                    "Inspect and clear the protected CloudRestoreStaging folders after preserving recovery evidence.");
            Directory.CreateDirectory(folder);
            RestrictToCurrentWindowsUser(folder);
            await downloadPackage(cloudPackage, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(cloudPackage) || new FileInfo(cloudPackage).Length == 0)
                throw new InvalidDataException("The cloud download was incomplete or empty.");
            CloudBackupCrypto.Decrypt(cloudPackage, bak, recoveryPassphrase);
            // This is only a preliminary header check. RestoreBackup performs
            // the checksum, disposable-restore identity/compatibility checks,
            // mandatory safety backup, and automatic rollback on failure.
            var info = DatabaseBackupManager.GetBackupInfo(bak);
            if (info is null || !info.IsForFmscDatabase)
                throw new InvalidDataException("The downloaded file is not a readable FMSC SQL backup.");
            // GCM decryption above authenticated the entire package header.
            // Compare SQL Server's raw local header timestamp, NOT its value
            // reinterpreted in a possibly different replacement PC timezone.
            DateTime packagedSqlTime = CloudBackupCrypto.ReadSqlHeaderLocalAfterVerification(cloudPackage);
            if ((packagedSqlTime - info.BackupStartDate).Duration() > TimeSpan.FromSeconds(1))
                throw new InvalidDataException("The authenticated package timestamp does not match the actual SQL backup header.");

            // Cancellation is permitted before this point only: do not abandon
            // an in-progress SQL restore or its rollback in a cancellation path.
            cancellationToken.ThrowIfCancellationRequested();
            // Cloud staging is temporary. Even when no regular backup folder
            // is configured, the mandatory pre-restore safety copy must live
            // in a distinct persistent directory and survive staging cleanup.
            string permanentSafetyFolder = BackupSettings.GetBackupFolder();
            if (permanentSafetyFolder.Length == 0)
                permanentSafetyFolder = Path.Combine(DatabaseHelper.DatabaseFolder, "Backups");
            string stagePath = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string safetyPath = Path.GetFullPath(permanentSafetyFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (safetyPath.StartsWith(stagePath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The recovery safety-backup folder cannot be inside the temporary cloud download folder.");
            sqlRestoreStarted = true; // Preserve stage if any SQL work may have begun.
            var result = await Task.Run(() => DatabaseBackupManager.RestoreBackup(
                bak, safetyBackupFolderOverride: permanentSafetyFolder, historyPathOverride: cloudHistoryPath))
                .ConfigureAwait(false);
            databaseRestored = result.Success;
            if (databaseRestored)
            {
                try { Directory.Delete(folder, recursive: true); }
                catch (Exception cleanupError)
                {
                    AppExceptionHandler.LogNonFatal(cleanupError,
                        "Verified cloud restore succeeded but temporary download cleanup failed");
                    return new DatabaseBackupManager.BackupResult {
                        Success = true,
                        Message = result.Message +
                            "\n\nWarning: temporary cloud download cleanup failed. The protected staging files require administrator cleanup."
                    };
                }
            }
            return result;
        }
        catch (Exception ex)
        {
            AppExceptionHandler.LogNonFatal(ex, "Cloud restore staging failed before or during verified SQL restore");
            return new DatabaseBackupManager.BackupResult
            {
                Success = false,
                Message = "Cloud restore was not completed. " +
                    "The existing database restore safeguards remain in effect. " +
                    DbMessage.UserMessage(ex)
            };
        }
        finally
        {
            // An invalid passphrase, corrupt download, canceled request or
            // incompatible SQL header never touched SQL: remove ALL temporary
            // ciphertext/plaintext so repeated attempts cannot fill the drive.
            // Once SQL begins, preserve the stage if restore fails, for
            // supervised recovery. Successful restores clean above.
            if (!sqlRestoreStarted && Directory.Exists(folder))
            {
                try { Directory.Delete(folder, recursive: true); }
                catch (Exception cleanupError)
                {
                    AppExceptionHandler.LogNonFatal(cleanupError,
                        "Pre-SQL cloud restore temporary staging cleanup failed");
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
