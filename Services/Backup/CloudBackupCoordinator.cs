using System.Globalization;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using FMSC_SMS.Core;
using Microsoft.Data.SqlClient;

namespace FMSC_SMS.Services.Backup;

/// <summary>
/// Creates verified local backups before any cloud operation. Each provider
/// is independent, and successful provider uploads are journalled immediately
/// so a network failure cannot cause the other provider's success to be lost.
/// </summary>
internal static class CloudBackupCoordinator
{
    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FMSC", "CloudBackups");
    private static readonly string Queue = Path.Combine(Root, "queue");
    private static readonly string Journal = Path.Combine(Root, "journal");
    private static readonly string ScheduleState = Path.Combine(Root, "schedule-state.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private sealed class Pending
    {
        public string Id { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public string PackageFile { get; set; } = "";
        public DateTime SqlCreatedUtc { get; set; }
        public string GoogleDriveId { get; set; } = "";
        public string DropboxId { get; set; } = "";
        public long Size { get; set; }
    }
    private sealed class State { public DateTimeOffset? LastSuccessfulLocalBackup { get; set; } }
    internal sealed record BackupOutcome(bool LocalSucceeded, bool GoogleSucceeded,
        bool DropboxSucceeded, string Message);
    internal sealed record CloudSelection(CloudRemoteBackup Remote, bool KnownVerifiedUpload);

    internal static async Task<BackupOutcome> CreateAndUploadAsync(
        bool scheduled = false, CancellationToken token = default)
    {
        var settings = CloudBackupSettings.Load();
        settings.Validate();
        if (!scheduled && !CurrentUser.HasPermission("Backup.Create"))
            throw new UnauthorizedAccessException("Creating backups requires Backup.Create permission.");
        if (!(settings.GoogleDriveEnabled || settings.DropboxEnabled))
            throw new InvalidOperationException("Enable Google Drive or Dropbox in the existing Backup / Restore settings first.");
        if (!CloudBackupSettings.HasLocalRecoveryPassphrase)
            throw new InvalidOperationException("Enroll and independently retain a recovery passphrase before cloud backups can run.");

        using var mutex = new Mutex(false, "Local\\FMSC-CloudBackup-Job");
        if (!mutex.WaitOne(0)) throw new InvalidOperationException("Another FMSC cloud backup or upload is already running.");
        try
        {
            EnsurePrivateDirectories();
            await RetryPendingUploadsCoreAsync(settings, token);
            if (scheduled && !settings.IsDue(DateTimeOffset.Now, LoadState().LastSuccessfulLocalBackup))
                return new BackupOutcome(false, false, false, "No scheduled backup is due.");

            string folder = BackupSettings.GetBackupFolder();
            if (folder.Length == 0) throw new InvalidOperationException("Choose an existing local backup folder before enabling automatic cloud backups.");
            string bak = DatabaseBackupManager.CreateUniqueBackupPath(folder,
                "FMSC_CloudAuto_" + DateTime.Now.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture) +
                "_" + Guid.NewGuid().ToString("N")[..6] + ".bak");
            // The unattended mode has no signed-in FMSC user. It uses Windows
            // integrated SQL rights ONLY after DatabaseHelper validates the
            // fixed, attached local DB path, identity and schema. Manual mode
            // keeps using the existing permission-checked backup service.
            if (scheduled)
                await Task.Run(() => CreateValidatedSqlBackupHeadless(bak), token);
            else
            {
                var result = await Task.Run(() => DatabaseBackupManager.CreateBackup(bak), token);
                if (!result.Success) throw new IOException(result.Message);
            }
            // Only a verified SQL header is used to create the remote recovery timestamp.
            var info = DatabaseBackupManager.GetBackupInfo(bak);
            if (info is null || !info.IsForFmscDatabase || info.BackupStartDate == default)
                throw new InvalidDataException("Verified SQL backup metadata is not readable.");
            DateTime sqlCreated = DateTime.SpecifyKind(info.BackupStartDate, DateTimeKind.Local).ToUniversalTime();
            string id = Guid.NewGuid().ToString("N");
            string packaged = Path.Combine(Queue, id + ".fmscbackup");
            CloudBackupCrypto.Encrypt(bak, packaged, CloudBackupSettings.OpenRecoveryPassphrase(), sqlCreated);
            string digest;
            await using (var content = File.OpenRead(packaged))
                digest = Convert.ToHexString(await SHA256.HashDataAsync(content, token)).ToLowerInvariant();
            var pending = new Pending {
                Id = id, PackageFile = packaged, Sha256 = digest,
                SqlCreatedUtc = sqlCreated, Size = new FileInfo(packaged).Length
            };
            SavePending(pending);
            // A successful local backup is not re-created just because one
            // provider was temporarily unavailable.
            SaveState(new State { LastSuccessfulLocalBackup = DateTimeOffset.Now });
            bool google = false, dropbox = false;
            var issues = new List<string>();
            if (settings.GoogleDriveEnabled)
            {
                try { google = await UploadOneAsync(pending, CloudProvider.GoogleDrive, settings, token); }
                catch (Exception ex) { issues.Add("Google Drive: " + SafeTransportError(ex)); }
            }
            if (settings.DropboxEnabled)
            {
                try { dropbox = await UploadOneAsync(pending, CloudProvider.Dropbox, settings, token); }
                catch (Exception ex) { issues.Add("Dropbox: " + SafeTransportError(ex)); }
            }
            RemoveFullyDeliveredPayload(pending, settings);
            PruneLocalAutomaticBackups(folder, settings.MaximumLocalBackups);
            string summary = issues.Count == 0
                ? "Verified local backup uploaded to the selected cloud destinations."
                : "Verified local backup succeeded. Cloud upload is pending retry: " + string.Join("; ", issues);
            return new BackupOutcome(true, google, dropbox, summary);
        }
        finally { mutex.ReleaseMutex(); }
    }

    internal static async Task<int> RunScheduledAsync(CancellationToken token = default)
    {
        // This command is never exposed as an interactive UI entry point.
        // If the user has not enabled/enrolled the feature, do nothing.
        var settings = CloudBackupSettings.Load();
        if (!settings.AutomaticEnabled) return 0;
        if (!CloudBackupSettings.HasLocalRecoveryPassphrase) return 2;
        // Never let a scheduled backup race a pending database migration,
        // interrupted update, or installer activation. Unlike the interactive
        // startup guard this probe never opens dialogs or starts recovery.
        if (!IsUpdaterIdleForBackup()) return 3;
        try
        {
            await CreateAndUploadAsync(scheduled: true, token);
            return 0;
        }
        catch (Exception ex)
        {
            AppExceptionHandler.LogNonFatal(ex, "Unattended encrypted cloud backup failed");
            return 1;
        }
    }

    private static bool IsUpdaterIdleForBackup()
    {
        try
        {
            if (Mutex.TryOpenExisting("Global\\\\FMSCInstallerActivation", out var activation))
            {
                using (activation)
                {
                    bool acquired;
                    try { acquired = activation.WaitOne(0); }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) return false;
                    activation.ReleaseMutex();
                }
            }
            string journal = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "FMSC_Updater", "recovery.json");
            if (!File.Exists(journal)) return true;
            byte[] bytes = File.ReadAllBytes(journal);
            return bytes.Length <= 32768 && !FMSC_SMS.Core.Updates.UpdateStartupGuard.RequiresRecovery(bytes);
        }
        catch (Exception ex)
        {
            AppExceptionHandler.LogNonFatal(ex, "Scheduled cloud backup deferred: installer/update recovery state uncertain");
            return false;
        }
    }

    internal static async Task RetryPendingUploadsAsync(CancellationToken token = default)
    {
        var settings = CloudBackupSettings.Load();
        using var mutex = new Mutex(false, "Local\\FMSC-CloudBackup-Job");
        if (!mutex.WaitOne(0)) return;
        try { await RetryPendingUploadsCoreAsync(settings, token); }
        finally { mutex.ReleaseMutex(); }
    }

    internal static async Task<IReadOnlyList<CloudSelection>> BrowseCloudAsync(CancellationToken token = default)
    {
        var list = new List<CloudSelection>();
        foreach (var provider in new[] { CloudProvider.GoogleDrive, CloudProvider.Dropbox })
        {
            if (!CloudBackupOAuth.IsConnected(provider)) continue;
            var known = ReadPending().Where(x =>
                (provider == CloudProvider.GoogleDrive ? x.GoogleDriveId : x.DropboxId).Length > 0).ToArray();
            try
            {
                var remote = await CloudBackupTransport.ListAsync(provider, token);
                foreach (var item in remote)
                {
                    bool isKnown = known.Any(x =>
                        (provider == CloudProvider.GoogleDrive ? x.GoogleDriveId : x.DropboxId) == item.RemoteId &&
                        string.Equals(x.Sha256, item.PackageSha256, StringComparison.OrdinalIgnoreCase) &&
                        (x.SqlCreatedUtc - item.SqlBackupCreatedUtc).Duration() < TimeSpan.FromSeconds(1));
                    list.Add(new CloudSelection(item, isKnown));
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException)
            {
                // One provider's outage cannot conceal the other provider's
                // available recovery points.
                AppExceptionHandler.LogNonFatal(ex, provider + " cloud recovery listing failed");
            }
        }
        return list.OrderByDescending(x => x.Remote.SqlBackupCreatedUtc).ToArray();
    }

    internal static async Task<DatabaseBackupManager.BackupResult> RestoreFromCloudAsync(
        CloudRemoteBackup source, string recoveryPassphrase, CancellationToken token = default)
    {
        if (!CurrentUser.HasPermission("Backup.Restore"))
            throw new UnauthorizedAccessException("Restoring a cloud backup requires Backup.Restore permission.");
        return await CloudRestoreStaging.RestoreAsync(
            (destination, ct) => DownloadAndCheckAsync(source, destination, ct),
            recoveryPassphrase,
            "cloud://" + source.Provider + "/" + Uri.EscapeDataString(source.RemoteId), token);
    }

    private static async Task DownloadAndCheckAsync(CloudRemoteBackup source, string destination, CancellationToken token)
    {
        await CloudBackupTransport.DownloadAsync(source.Provider, source.RemoteId, destination, token);
        await using var check = File.OpenRead(destination);
        string actual = Convert.ToHexString(await SHA256.HashDataAsync(check, token)).ToLowerInvariant();
        if (!string.Equals(actual, source.PackageSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Cloud backup SHA-256 differs from its recorded package identity.");
    }

    private static async Task RetryPendingUploadsCoreAsync(CloudBackupOptions settings, CancellationToken token)
    {
        if (!Directory.Exists(Journal)) return;
        foreach (var pending in ReadPending().Where(p => File.Exists(p.PackageFile)))
        {
            token.ThrowIfCancellationRequested();
            if (settings.GoogleDriveEnabled && pending.GoogleDriveId.Length == 0)
            {
                try { await UploadOneAsync(pending, CloudProvider.GoogleDrive, settings, token); }
                catch (Exception ex) { AppExceptionHandler.LogNonFatal(ex, "Retrying Google Drive encrypted backup upload"); }
            }
            if (settings.DropboxEnabled && pending.DropboxId.Length == 0)
            {
                try { await UploadOneAsync(pending, CloudProvider.Dropbox, settings, token); }
                catch (Exception ex) { AppExceptionHandler.LogNonFatal(ex, "Retrying Dropbox encrypted backup upload"); }
            }
            RemoveFullyDeliveredPayload(pending, settings);
        }
    }

    private static async Task<bool> UploadOneAsync(
        Pending p, CloudProvider provider, CloudBackupOptions settings, CancellationToken token)
    {
        if (!CloudBackupOAuth.IsConnected(provider))
            throw new InvalidOperationException(provider + " account is not connected.");
        CloudRemoteBackup copy = await CloudBackupTransport.UploadAsync(provider, p.PackageFile, p.SqlCreatedUtc, token);
        if (!string.Equals(copy.PackageSha256, p.Sha256, StringComparison.OrdinalIgnoreCase) ||
            copy.Size != p.Size)
            throw new IOException(provider + " did not confirm an intact encrypted upload.");
        if (provider == CloudProvider.GoogleDrive) p.GoogleDriveId = copy.RemoteId;
        else p.DropboxId = copy.RemoteId;
        SavePending(p); // commit this provider result before touching the next provider
        try { await PruneRemoteAsync(provider, settings, p.Sha256, token); }
        catch (Exception ex) { AppExceptionHandler.LogNonFatal(ex, "Cloud retention failed after successful upload"); }
        return true;
    }

    private static async Task PruneRemoteAsync(CloudProvider provider,
        CloudBackupOptions settings, string newlyConfirmedSha, CancellationToken token)
    {
        int limit = provider == CloudProvider.GoogleDrive
            ? settings.MaximumGoogleDriveBackups : settings.MaximumDropboxBackups;
        var known = ReadPending().Where(p =>
            (provider == CloudProvider.GoogleDrive ? p.GoogleDriveId : p.DropboxId).Length > 0).ToArray();
        var cloud = await CloudBackupTransport.ListAsync(provider, token);
        var copies = known.Select(p =>
        {
            string id = provider == CloudProvider.GoogleDrive ? p.GoogleDriveId : p.DropboxId;
            var remote = cloud.FirstOrDefault(x => x.RemoteId == id &&
                string.Equals(x.PackageSha256, p.Sha256, StringComparison.OrdinalIgnoreCase));
            return remote is null ? null : new CloudBackupCopy(
                provider.ToString(), id, p.Sha256, p.SqlCreatedUtc, remote.UploadedUtc, remote.Size, true);
        }).Where(p => p is not null).Select(p => p!).ToArray();
        foreach (var old in CloudBackupCatalog.CopiesEligibleForDeletion(copies,
            provider.ToString(), limit, newlyConfirmedSha))
        {
            // The server returned the matching remote file ID, and a locally
            // recorded successful upload exists. Never delete unknown files.
            await CloudBackupTransport.DeleteAsync(provider, old.RemoteId, token);
            var original = known.FirstOrDefault(p => (provider == CloudProvider.GoogleDrive
                ? p.GoogleDriveId : p.DropboxId) == old.RemoteId);
            if (original is null) continue;
            if (provider == CloudProvider.GoogleDrive) original.GoogleDriveId = "";
            else original.DropboxId = "";
            SavePending(original);
        }
    }

    private static void RemoveFullyDeliveredPayload(Pending p, CloudBackupOptions settings)
    {
        if ((!settings.GoogleDriveEnabled || p.GoogleDriveId.Length > 0) &&
            (!settings.DropboxEnabled || p.DropboxId.Length > 0) &&
            File.Exists(p.PackageFile))
            File.Delete(p.PackageFile);
        // Keep the small journal: it proves what we uploaded and protects
        // retention decisions even after the queued ciphertext is cleaned up.
    }

    private static void PruneLocalAutomaticBackups(string folder, int limit)
    {
        var files = new DirectoryInfo(folder).EnumerateFiles("FMSC_CloudAuto_*.bak")
            .OrderByDescending(file => file.CreationTimeUtc).ToArray();
        // A configured limit never permits deletion of the newest verified
        // backup, and cannot touch pre-restore/migration safety backups or
        // operator-made local backup files.
        foreach (var older in files.Skip(Math.Max(1, limit)))
        {
            try { older.Delete(); }
            catch (Exception ex) { AppExceptionHandler.LogNonFatal(ex, "Old automatic local backup cleanup failed"); }
        }
    }

    private static void CreateValidatedSqlBackupHeadless(string backupPath)
    {
        if (!DatabaseHelper.TestConnection(out string message))
            throw new InvalidOperationException("The school database failed its identity/compatibility checks: " + message);
        using var connection = DatabaseHelper.GetMasterConnection();
        DatabaseHelper.OpenConnection(connection);
        using (var backup = new SqlCommand(
            "BACKUP DATABASE [FMSC_DB] TO DISK=@Path WITH INIT, COPY_ONLY, CHECKSUM;", connection))
        {
            backup.CommandTimeout = 0;
            backup.Parameters.Add("@Path", System.Data.SqlDbType.NVarChar, 1000).Value = backupPath;
            backup.ExecuteNonQuery();
        }
        using (var verify = new SqlCommand(
            "RESTORE VERIFYONLY FROM DISK=@Path WITH CHECKSUM;", connection))
        {
            verify.CommandTimeout = 0;
            verify.Parameters.Add("@Path", System.Data.SqlDbType.NVarChar, 1000).Value = backupPath;
            verify.ExecuteNonQuery();
        }
        if (!File.Exists(backupPath) || new FileInfo(backupPath).Length <= 0)
            throw new IOException("Scheduled SQL backup did not produce a readable non-empty file.");
    }

    private static IReadOnlyList<Pending> ReadPending()
    {
        if (!Directory.Exists(Journal)) return Array.Empty<Pending>();
        var items = new List<Pending>();
        foreach (string path in Directory.EnumerateFiles(Journal, "*.json"))
        {
            try
            {
                if (new FileInfo(path).Length > 4096) continue;
                var p = JsonSerializer.Deserialize<Pending>(File.ReadAllText(path));
                if (p is not null && Guid.TryParseExact(p.Id, "N", out _) &&
                    p.Sha256.Length == 64 && p.Sha256.All(Uri.IsHexDigit) &&
                    p.PackageFile == Path.Combine(Queue, p.Id + ".fmscbackup"))
                    items.Add(p);
            }
            catch (Exception ex) { AppExceptionHandler.LogNonFatal(ex, "Unreadable cloud backup journal entry"); }
        }
        return items;
    }

    private static void SavePending(Pending p)
    {
        EnsurePrivateDirectories();
        AtomicTextFile.Write(Path.Combine(Journal, p.Id + ".json"),
            JsonSerializer.Serialize(p, Json), Encoding.UTF8);
    }
    private static State LoadState()
    {
        if (!File.Exists(ScheduleState)) return new State();
        return JsonSerializer.Deserialize<State>(File.ReadAllText(ScheduleState)) ?? new State();
    }
    private static void SaveState(State state)
    {
        EnsurePrivateDirectories();
        AtomicTextFile.Write(ScheduleState, JsonSerializer.Serialize(state), Encoding.UTF8);
    }
    private static void EnsurePrivateDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Journal);
        Directory.CreateDirectory(Queue);
        var user = WindowsIdentity.GetCurrent().User
            ?? throw new UnauthorizedAccessException("Cannot identify scheduled backup owner.");
        foreach (string path in new[] { Root, Journal, Queue })
        {
            var acl = new DirectorySecurity();
            acl.SetOwner(user);
            acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            acl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(acl);
        }
    }
    private static string SafeTransportError(Exception ex) =>
        ex is HttpRequestException ? ex.Message : ex is InvalidOperationException ? ex.Message :
            "Upload failed; the encrypted package has been kept for retry.";
}
