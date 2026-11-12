using System.Data;
using System.Security.Cryptography;
using FMSC_SMS.Core;

namespace FMSC_SMS.Services.Backup;

internal sealed record UnifiedRecoveryInventoryResult(
    IReadOnlyList<UnifiedRecoveryPoint> Points, IReadOnlyList<string> Warnings);

/// <summary>
/// Enumerates verified SQL backup headers and currently listed cloud objects.
/// Cloud failures do not hide local copies; missing SQL history does not hide
/// cloud copies. No destructive cleanup is performed by inventory refresh.
/// </summary>
internal static class UnifiedRecoveryInventory
{
    internal static async Task<UnifiedRecoveryInventoryResult> LoadAsync(
        CancellationToken token = default)
    {
        var localTask = Task.Run(() => ReadLocalCopies(token), token);
        CloudBackupCoordinator.CloudBrowseResult cloud;
        try
        {
            cloud = await CloudBackupCoordinator.BrowseCloudAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            AppExceptionHandler.LogNonFatal(ex, "Refreshing unified cloud recovery inventory");
            cloud = new CloudBackupCoordinator.CloudBrowseResult(
                Array.Empty<CloudBackupCoordinator.CloudSelection>(),
                new[] { "Cloud recovery points could not be refreshed. Local recovery points remain available; reconnect and retry." });
        }
        var local = await localTask;
        token.ThrowIfCancellationRequested();
        var remote = cloud.Copies.Select(x =>
            new RecoveryCloudCopy(x.Remote, x.KnownVerifiedUpload, x.SourceBakSha256));
        return new UnifiedRecoveryInventoryResult(
            UnifiedRecoveryCatalog.Group(local.Copies, remote),
            local.Warnings.Concat(cloud.Warnings).ToArray());
    }

    private static (IReadOnlyList<RecoveryLocalCopy> Copies, IReadOnlyList<string> Warnings)
        ReadLocalCopies(CancellationToken token)
    {
        var warnings = new List<string>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            DataTable history = DatabaseBackupManager.GetHistory();
            foreach (DataRow row in history.Rows)
            {
                if (history.Columns.Contains("Operation") &&
                    !string.Equals(Convert.ToString(row["Operation"]), "BACKUP",
                        StringComparison.OrdinalIgnoreCase)) continue;
                if (history.Columns.Contains("Status") &&
                    !string.Equals(Convert.ToString(row["Status"]), "Success",
                        StringComparison.OrdinalIgnoreCase)) continue;
                string? path = Convert.ToString(row["FilePath"]);
                if (!string.IsNullOrWhiteSpace(path) &&
                    !path.StartsWith("cloud://", StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(path)) paths.Add(path);
            }
        }
        catch (Exception ex)
        {
            AppExceptionHandler.LogNonFatal(ex, "Local backup history unavailable while building recovery catalogue");
            warnings.Add("The database backup history could not be read; available local files will still be checked.");
        }
        try
        {
            string folder = BackupSettings.GetBackupFolder();
            if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
                foreach (string path in Directory.EnumerateFiles(folder, "*.bak", SearchOption.TopDirectoryOnly))
                    paths.Add(path);
        }
        catch (Exception ex)
        {
            AppExceptionHandler.LogNonFatal(ex, "Scanning local backups for unified recovery catalogue");
            warnings.Add("Some local backup files could not be listed. Use Choose Backup File to open one directly.");
        }
        var links = CloudBackupCoordinator.ReadLocalLinks();
        var copies = new List<RecoveryLocalCopy>();
        foreach (string path in paths)
        {
            token.ThrowIfCancellationRequested();
            if (!File.Exists(path)) continue;
            var header = DatabaseBackupManager.GetBackupInfo(path);
            if (header?.IsForFmscDatabase != true || header.BackupStartDate == default)
                continue; // Do not offer unverified or unrelated SQL files to restore.
            DateTime createdUtc = DateTime.SpecifyKind(header.BackupStartDate, DateTimeKind.Local)
                .ToUniversalTime();
            string sourceDigest = "";
            var candidates = links.Where(x =>
                string.Equals(x.FilePath, path, StringComparison.OrdinalIgnoreCase) &&
                (x.SqlCreatedUtc - createdUtc).Duration() <= TimeSpan.FromSeconds(1) &&
                UnifiedRecoveryCatalog.IsSha256(x.SourceBakSha256))
                .Select(x => x.SourceBakSha256)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (candidates.Length == 1)
            {
                try
                {
                    using var input = File.OpenRead(path);
                    string actual = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
                    if (string.Equals(actual, candidates[0], StringComparison.OrdinalIgnoreCase))
                        sourceDigest = actual;
                }
                catch (Exception ex)
                {
                    AppExceptionHandler.LogNonFatal(ex, "Checking original local backup digest for unified catalogue");
                }
            }
            copies.Add(new RecoveryLocalCopy(path, createdUtc, sourceDigest));
        }
        return (copies, warnings);
    }
}
