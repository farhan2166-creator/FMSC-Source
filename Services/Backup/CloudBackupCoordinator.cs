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
    private static readonly string Staging = Path.Combine(Root, "staging");
    private static readonly string Journal = Path.Combine(Root, "journal");
    private static readonly string ScheduleState = Path.Combine(Root, "schedule-state.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private const int MaximumAutomaticDeliveryAttempts = 4;

    private sealed class Pending
    {
        public string Id { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public string PackageFile { get; set; } = "";
        public DateTime SqlCreatedUtc { get; set; }
        public string GoogleDriveId { get; set; } = "";
        public string GoogleDriveUploadId { get; set; } = "";
        public string DropboxId { get; set; } = "";
        // Old journals intentionally deserialize as unverified; they remain
        // discoverable without being incorrectly called recovery-tested.
        public bool GoogleRecoveryVerified { get; set; }
        public bool DropboxRecoveryVerified { get; set; }
        public int GoogleDeliveryAttempts { get; set; }
        public int DropboxDeliveryAttempts { get; set; }
        // Missing properties in old journal JSON default false.
        public bool GoogleDrivePruned { get; set; }
        public bool DropboxPruned { get; set; }
        public long Size { get; set; }
    }
    private sealed class State { public DateTimeOffset? LastSuccessfulLocalBackup { get; set; } }
    private sealed record RunStatus(DateTime CheckedUtc, bool UploadsPending, string Message);
    internal sealed record CloudBrowseResult(IReadOnlyList<CloudSelection> Copies, IReadOnlyList<string> Warnings);
    internal sealed record CloudSelection(CloudRemoteBackup Remote, bool KnownVerifiedUpload);

    internal static async Task<CloudBackupOutcome> CreateAndUploadAsync(
        bool scheduled = false, CancellationToken token = default)
    {
        var settings = CloudBackupSettings.Load();
        settings.Validate();
        if (!scheduled && !CurrentUser.HasPermission("Backup.Create"))
            throw new UnauthorizedAccessException("Creating backups requires Backup.Create permission.");
        if (!(settings.LocalEnabled || settings.GoogleDriveEnabled || settings.DropboxEnabled))
            throw new InvalidOperationException("Enable at least one backup destination.");
        bool needsCloud = settings.GoogleDriveEnabled || settings.DropboxEnabled;
        if (needsCloud && !CloudBackupSettings.HasLocalRecoveryPassphrase)
            throw new InvalidOperationException("Enroll and independently retain a recovery passphrase before cloud backups can run.");

        EnsurePrivateDirectories();
        // The lease survives awaits and may be disposed by any continuation thread.
        // The OS releases it after a crash, so no stale flag can block a restart.
        using var jobLease = CloudBackupJobLock.TryAcquire(Path.Combine(Root, "cloud-job.lock"))
            ?? throw new InvalidOperationException("Another FMSC cloud backup or upload is already running.");
        try
        {
            var retryIssues = await RetryPendingUploadsCoreAsync(settings, token).ConfigureAwait(false);
            if (scheduled && !settings.IsDue(DateTimeOffset.Now, LoadState().LastSuccessfulLocalBackup))
                return RecordOutcome(new CloudBackupOutcome(false, false, false,
                    retryIssues.Count == 0 ? "No scheduled backup is due. Pending uploads are complete."
                        : "Cloud upload is pending retry: " + string.Join("; ", retryIssues), retryIssues.Count > 0));

            string folder = settings.LocalEnabled ? BackupSettings.GetBackupFolder() : Staging;
            if (folder.Length == 0) throw new InvalidOperationException("Choose a local backup folder or disable the local destination in Backup Settings.");
            Directory.CreateDirectory(folder);
            string bak = DatabaseBackupManager.CreateUniqueBackupPath(folder,
                "FMSC_Managed_" + DateTime.Now.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture) +
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
            var info = await Task.Run(() => DatabaseBackupManager.GetBackupInfo(bak), token)
                .ConfigureAwait(false);
            if (info is null || !info.IsForFmscDatabase || info.BackupStartDate == default)
                throw new InvalidDataException("Verified SQL backup metadata is not readable.");
            DateTime sqlCreated = DateTime.SpecifyKind(info.BackupStartDate, DateTimeKind.Local).ToUniversalTime();
            if (!needsCloud)
            {
                SaveState(new State { LastSuccessfulLocalBackup = DateTimeOffset.Now });
                await Task.Run(() => PruneLocalManagedBackups(folder, settings.MaximumLocalBackups), token)
                    .ConfigureAwait(false);
                return RecordOutcome(new CloudBackupOutcome(true, false, false,
                    "Verified local SQL backup saved: " + bak));
            }
            string id = Guid.NewGuid().ToString("N");
            string packaged = Path.Combine(Queue, id + ".fmscbackup");
            // Large encrypted SQL backups must never block the WinForms message pump.
            await Task.Run(() => CloudBackupCrypto.Encrypt(
                bak, packaged, CloudBackupSettings.OpenRecoveryPassphrase(),
                sqlCreated, info.BackupStartDate), token).ConfigureAwait(false);
            string digest;
            await using (var content = File.OpenRead(packaged))
                digest = Convert.ToHexString(await SHA256.HashDataAsync(content, token)).ToLowerInvariant();
            var pending = new Pending {
                Id = id, PackageFile = packaged, Sha256 = digest,
                SqlCreatedUtc = sqlCreated, Size = new FileInfo(packaged).Length
            };
            SavePending(pending);
            // Cloud-only delivery retains the encrypted retry payload, not a
            // permanent plaintext local copy. Leave the .bak for diagnosis if
            // encryption or durable journalling failed before this point.
            if (!settings.LocalEnabled)
            {
                try { File.Delete(bak); }
                catch (Exception ex) { AppExceptionHandler.LogNonFatal(ex, "Removing transient cloud-only SQL backup"); }
            }
            // A successful local backup is not re-created just because one
            // provider was temporarily unavailable.
            SaveState(new State { LastSuccessfulLocalBackup = DateTimeOffset.Now });
            bool google = false, dropbox = false;
            var issues = new List<string>(retryIssues);
            if (settings.GoogleDriveEnabled)
            {
                try { google = await UploadOneAsync(pending, CloudProvider.GoogleDrive, settings, token); }
                catch (Exception ex) { token.ThrowIfCancellationRequested(); issues.Add("Google Drive: " + SafeTransportError(ex)); }
            }
            if (settings.DropboxEnabled)
            {
                try { dropbox = await UploadOneAsync(pending, CloudProvider.Dropbox, settings, token); }
                catch (Exception ex) { token.ThrowIfCancellationRequested(); issues.Add("Dropbox: " + SafeTransportError(ex)); }
            }
            RemoveFullyDeliveredPayload(pending, settings);
            if (settings.LocalEnabled)
                await Task.Run(() => PruneLocalManagedBackups(folder, settings.MaximumLocalBackups), token)
                    .ConfigureAwait(false);
            string summary = issues.Count == 0
                ? (settings.LocalEnabled ? "Local backup saved. " : "") + "Encrypted backup uploaded to the selected cloud destinations."
                : (settings.LocalEnabled ? "Local backup saved. " : "Encrypted backup safely queued. ") +
                    "Some cloud destinations still require delivery: " + string.Join("; ", issues);
            return RecordOutcome(new CloudBackupOutcome(settings.LocalEnabled, google, dropbox, summary, issues.Count > 0));
        }
        finally { /* jobLease is disposed on exit, including exceptions */ }
    }

    internal static async Task<int> RunScheduledAsync(CancellationToken token = default)
    {
        // This command is never exposed as an interactive UI entry point.
        // If the user has not enabled/enrolled the feature, do nothing.
        var settings = CloudBackupSettings.Load();
        if (!settings.AutomaticEnabled) return 0;
        if ((settings.GoogleDriveEnabled || settings.DropboxEnabled) &&
            !CloudBackupSettings.HasLocalRecoveryPassphrase) return 2;
        // Never let a scheduled backup race a pending database migration,
        // interrupted update, or installer activation. Unlike the interactive
        // startup guard this probe never opens dialogs or starts recovery.
        if (!IsUpdaterIdleForBackup()) return 3;
        try
        {
            var outcome = await CreateAndUploadAsync(scheduled: true, token);
            return CloudBackupOperations.ScheduledExitCode(outcome);
        }
        catch (Exception ex)
        {
            AppExceptionHandler.LogNonFatal(ex, "Unattended encrypted cloud backup failed");
            try { RecordOutcome(new CloudBackupOutcome(false, false, false, "Cloud backup failed. Check the connection, recovery settings and local backup folder.", true)); }
            catch (Exception statusError) { AppExceptionHandler.LogNonFatal(statusError, "Could not retain cloud backup failure status"); }
            return 1;
        }
    }

    private static bool IsUpdaterIdleForBackup()
    {
        try
        {
            if (Mutex.TryOpenExisting("Global\\FMSCInstallerActivation", out var activation))
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
        EnsurePrivateDirectories();
        using var jobLease = CloudBackupJobLock.TryAcquire(Path.Combine(Root, "cloud-job.lock"));
        if (jobLease is null) return;
        await RetryPendingUploadsCoreAsync(settings, token).ConfigureAwait(false);
    }

    // Explicit user-controlled retry, separate from creating a new SQL backup.
    internal static async Task<string> RetryPendingUploadsNowAsync(CancellationToken token = default)
    {
        if (!CurrentUser.HasPermission("Backup.Create"))
            throw new UnauthorizedAccessException("Retrying pending deliveries requires Backup.Create permission.");
        var settings = CloudBackupSettings.Load();
        EnsurePrivateDirectories();
        using var jobLease = CloudBackupJobLock.TryAcquire(Path.Combine(Root, "cloud-job.lock"))
            ?? throw new InvalidOperationException("Another FMSC backup or upload is already running.");
        // A deliberate operator retry permits one more attempt even after the
        // bounded unattended retry budget was exhausted.
        foreach (var p in ReadPending().Where(p => File.Exists(p.PackageFile)))
        {
            p.GoogleDeliveryAttempts = Math.Min(p.GoogleDeliveryAttempts, MaximumAutomaticDeliveryAttempts - 1);
            p.DropboxDeliveryAttempts = Math.Min(p.DropboxDeliveryAttempts, MaximumAutomaticDeliveryAttempts - 1);
            SavePending(p);
        }
        var issues = await RetryPendingUploadsCoreAsync(settings, token).ConfigureAwait(false);
        int waiting = ReadPending().Count(p => File.Exists(p.PackageFile) &&
            (CloudBackupDeliveryPolicy.RequiresRecoveryVerification(settings.GoogleDriveEnabled, p.GoogleRecoveryVerified, p.GoogleDrivePruned) ||
             CloudBackupDeliveryPolicy.RequiresRecoveryVerification(settings.DropboxEnabled, p.DropboxRecoveryVerified, p.DropboxPruned)));
        string message = waiting == 0
            ? "No enabled cloud deliveries are waiting for retry."
            : waiting + " encrypted backup package(s) still await upload.";
        if (issues.Count > 0) message += " " + string.Join("; ", issues);
        RecordOutcome(new CloudBackupOutcome(false, false, false, message, waiting > 0));
        return message;
    }

    internal static async Task<CloudBrowseResult> BrowseCloudAsync(CancellationToken token = default)
    {
        var inventory = await CloudBackupOperations.BrowseAsync(CloudBackupOAuth.IsConnected,
            CloudBackupTransport.ListAsync,
            (provider, ex) => AppExceptionHandler.LogNonFatal(ex, provider + " cloud recovery listing failed"), token);
        var known = ReadPending();
        var copies = inventory.Copies.Select(item => new CloudSelection(item, known.Any(p =>
            (item.Provider == CloudProvider.GoogleDrive ? p.GoogleDriveId : p.DropboxId) == item.RemoteId &&
            (item.Provider == CloudProvider.GoogleDrive ? p.GoogleRecoveryVerified : p.DropboxRecoveryVerified) &&
            string.Equals(p.Sha256, item.PackageSha256, StringComparison.OrdinalIgnoreCase) &&
            (p.SqlCreatedUtc - item.SqlBackupCreatedUtc).Duration() < TimeSpan.FromSeconds(1)))).ToArray();
        return new(copies, inventory.Warnings);
    }

    internal static async Task<DatabaseBackupManager.BackupResult> RestoreFromCloudAsync(
        CloudRemoteBackup source, string recoveryPassphrase,
        IReadOnlyList<CloudRemoteBackup>? alternatives = null, CancellationToken token = default)
    {
        if (!CurrentUser.HasPermission("Backup.Restore"))
            throw new UnauthorizedAccessException("Restoring a cloud backup requires Backup.Restore permission.");
        // Never switch backup dates or packages during automatic source fallback.
        var matching = new[] { source }.Concat(alternatives ?? Array.Empty<CloudRemoteBackup>())
            .Where(copy => string.Equals(copy.PackageSha256, source.PackageSha256,
                       StringComparison.OrdinalIgnoreCase) &&
                   (copy.SqlBackupCreatedUtc - source.SqlBackupCreatedUtc).Duration() < TimeSpan.FromSeconds(1))
            .DistinctBy(copy => (copy.Provider, copy.RemoteId)).ToArray();
        var candidates = matching.Select(copy => new CloudRestoreStaging.DownloadCandidate(
            (destination, ct) => DownloadAndCheckAsync(copy, destination, ct),
            "cloud://" + copy.Provider + "/" + Uri.EscapeDataString(copy.RemoteId),
            copy.Provider.ToString())).ToArray();
        return await CloudRestoreStaging.RestoreWithFallbackAsync(candidates, recoveryPassphrase, token);
    }

    private static async Task DownloadAndCheckAsync(CloudRemoteBackup source, string destination, CancellationToken token)
    {
        await CloudBackupTransport.DownloadAsync(source.Provider, source.RemoteId, destination, token);
        await using var check = File.OpenRead(destination);
        string actual = Convert.ToHexString(await SHA256.HashDataAsync(check, token)).ToLowerInvariant();
        if (!string.Equals(actual, source.PackageSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Cloud backup SHA-256 differs from its recorded package identity.");
    }

    private static async Task<List<string>> RetryPendingUploadsCoreAsync(CloudBackupOptions settings, CancellationToken token)
    {
        var issues = new List<string>();
        if (!Directory.Exists(Journal)) return issues;
        foreach (var pending in ReadPending().Where(p => File.Exists(p.PackageFile)))
        {
            token.ThrowIfCancellationRequested();
            if (CloudBackupDeliveryPolicy.RequiresRecoveryVerification(settings.GoogleDriveEnabled, pending.GoogleRecoveryVerified, pending.GoogleDrivePruned))
            {
                if (pending.GoogleDeliveryAttempts >= MaximumAutomaticDeliveryAttempts)
                    issues.Add("Google Drive: automatic retries exhausted; use Retry Pending Uploads.");
                else
                {
                    try { await UploadOneAsync(pending, CloudProvider.GoogleDrive, settings, token); }
                    catch (Exception ex)
                    {
                        token.ThrowIfCancellationRequested();
                        issues.Add("Google Drive: " + SafeTransportError(ex));
                        AppExceptionHandler.LogNonFatal(ex, "Retrying Google Drive backup delivery or recovery test");
                    }
                }
            }
            if (CloudBackupDeliveryPolicy.RequiresRecoveryVerification(settings.DropboxEnabled, pending.DropboxRecoveryVerified, pending.DropboxPruned))
            {
                if (pending.DropboxDeliveryAttempts >= MaximumAutomaticDeliveryAttempts)
                    issues.Add("Dropbox: automatic retries exhausted; use Retry Pending Uploads.");
                else
                {
                    try { await UploadOneAsync(pending, CloudProvider.Dropbox, settings, token); }
                    catch (Exception ex)
                    {
                        token.ThrowIfCancellationRequested();
                        issues.Add("Dropbox: " + SafeTransportError(ex));
                        AppExceptionHandler.LogNonFatal(ex, "Retrying Dropbox backup delivery or recovery test");
                    }
                }
            }
            RemoveFullyDeliveredPayload(pending, settings);
        }
        return issues;
    }

    private static async Task<bool> UploadOneAsync(
        Pending p, CloudProvider provider, CloudBackupOptions settings, CancellationToken token)
    {
        if (!CloudBackupOAuth.IsConnected(provider))
            throw new InvalidOperationException(provider + " account is not connected.");
        if (provider == CloudProvider.GoogleDrive) p.GoogleDeliveryAttempts++;
        else p.DropboxDeliveryAttempts++;
        SavePending(p);
        CloudRemoteBackup copy = await CloudBackupTransport.UploadAsync(provider, p.PackageFile, p.SqlCreatedUtc, token,
            p.GoogleDriveUploadId, id => { p.GoogleDriveUploadId = id; SavePending(p); });
        if (!string.Equals(copy.PackageSha256, p.Sha256, StringComparison.OrdinalIgnoreCase) ||
            copy.Size != p.Size)
            throw new IOException(provider + " did not confirm an intact encrypted upload.");
        if (provider == CloudProvider.GoogleDrive)
        {
            p.GoogleDriveId = copy.RemoteId;
            p.GoogleDrivePruned = false;
        }
        else
        {
            p.DropboxId = copy.RemoteId;
            p.DropboxPruned = false;
        }
        SavePending(p); // commit the upload receipt before recovery testing
        // A provider's upload receipt is NOT evidence that the remote package
        // can be decrypted and restored. Test the actual downloaded remote bytes
        // with an isolated SQL restore before any retention is allowed.
        await CloudRestoreStaging.VerifyRemoteCopyAsync(
            (destination, ct) => DownloadAndCheckAsync(copy, destination, ct),
            CloudBackupSettings.OpenRecoveryPassphrase(), p.SqlCreatedUtc, token);
        if (provider == CloudProvider.GoogleDrive) p.GoogleRecoveryVerified = true;
        else p.DropboxRecoveryVerified = true;
        SavePending(p);
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
            (provider == CloudProvider.GoogleDrive ? p.GoogleRecoveryVerified : p.DropboxRecoveryVerified) &&
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
            if (provider == CloudProvider.GoogleDrive)
            {
                original.GoogleDriveId = "";
                original.GoogleDrivePruned = true;
            }
            else
            {
                original.DropboxId = "";
                original.DropboxPruned = true;
            }
            SavePending(original);
        }
    }

    private static void RemoveFullyDeliveredPayload(Pending p, CloudBackupOptions settings)
    {
        if (CloudBackupDeliveryPolicy.MayReleaseQueuedPackage(
                settings.GoogleDriveEnabled, p.GoogleRecoveryVerified, p.GoogleDrivePruned,
                settings.DropboxEnabled, p.DropboxRecoveryVerified, p.DropboxPruned) &&
            File.Exists(p.PackageFile))
            File.Delete(p.PackageFile);
        // Keep the small journal: it proves what we uploaded and protects
        // retention decisions even after the queued ciphertext is cleaned up.
    }

    private static void PruneLocalManagedBackups(string folder, int limit)
    {
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        // Local file creation/modified times can change when a .bak is copied.
        // Retention chronology, like Restore, is based on the actual SQL
        // backup header. Files with unreadable headers are retained for review.
        var valid = new List<(FileInfo File, DateTime SqlCreated)>();
        // Only prune newly managed files. Existing manual, pre-restore and
        // historical FMSC_CloudAuto backups remain protected during migration.
        foreach (FileInfo file in new DirectoryInfo(folder).EnumerateFiles("FMSC_Managed_*.bak"))
        {
            var info = DatabaseBackupManager.GetBackupInfo(file.FullName);
            if (info?.IsForFmscDatabase == true && info.BackupStartDate != default)
                valid.Add((file, info.BackupStartDate));
        }
        foreach (var old in valid.OrderByDescending(x => x.SqlCreated)
                     .Skip(Math.Max(1, limit)))
        {
            try { old.File.Delete(); }
            catch (Exception ex) { AppExceptionHandler.LogNonFatal(ex, "Old automatic local backup cleanup failed"); }
        }
        // No pre-restore, updater migration, or manually named backup matches
        // the FMSC_CloudAuto prefix; those recovery files remain untouched.
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
    private static CloudBackupOutcome RecordOutcome(CloudBackupOutcome outcome)
    {
        // Headless tasks have no status bar: retain sanitized delivery status
        // separately from the successful-local-backup scheduling timestamp.
        AtomicTextFile.Write(Path.Combine(Root, "last-run.json"),
            JsonSerializer.Serialize(new RunStatus(DateTime.UtcNow, outcome.UploadsPending, outcome.Message)), Encoding.UTF8);
        if (outcome.UploadsPending)
            AppExceptionHandler.LogNonFatal(new IOException(outcome.Message), "Cloud backup delivery pending");
        return outcome;
    }

    internal static (string Message, bool Warning) ReadLastRunStatus()
    {
        string path = Path.Combine(Root, "last-run.json");
        if (!File.Exists(path)) return ("", false);
        try
        {
            if (new FileInfo(path).Length > 16384) throw new InvalidDataException("Cloud backup status is too large.");
            var status = JsonSerializer.Deserialize<RunStatus>(File.ReadAllText(path))
                ?? throw new InvalidDataException("Cloud backup status is missing.");
            return ($"Cloud backup checked {status.CheckedUtc.ToLocalTime():dd-MMM-yyyy HH:mm}: {status.Message}", status.UploadsPending);
        }
        catch (Exception ex)
        {
            AppExceptionHandler.LogNonFatal(ex, "Reading cloud backup delivery status");
            return ("The last cloud backup status could not be read. Check the backup accounts and retry.", true);
        }
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
        Directory.CreateDirectory(Staging);
        var user = WindowsIdentity.GetCurrent().User
            ?? throw new UnauthorizedAccessException("Cannot identify scheduled backup owner.");
        foreach (string path in new[] { Root, Journal, Queue, Staging })
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
            "Cloud delivery or recovery verification failed; the encrypted package has been retained for retry.";
}
