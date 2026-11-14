using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using FMSC_SMS.Core;
using Microsoft.Data.SqlClient;

namespace FMSC_SMS.Services.Backup;

/// <summary>
/// Optional, read-only counts taken from the RESTORED CONTENTS of a particular
/// SQL .bak, not the live database. A digest-keyed local cache never affects
/// backup validity, cloud retention, encryption or restore eligibility.
/// </summary>
internal static class BackupContentsSummary
{
    internal sealed record Snapshot(
        int FormatVersion, string SourceBakSha256, DateTime SqlBackupCreatedUtc,
        long Students, long ActiveStudents, long FeePayments, long BillingCharges,
        int SchemaVersion);

    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FMSC", "BackupContents");
    private const int MaximumSummaries = 512;

    internal static Snapshot CaptureVerifiedBackup(string backupPath, DateTime expectedSqlUtc)
    {
        // A shared read handle protects the exact backup contents against being
        // overwritten while we hash and inspect them in a disposable SQL catalog.
        using var original = new FileStream(backupPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        string sha = Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant();
        var header = DatabaseBackupManager.GetBackupInfo(backupPath);
        if (header?.IsForFmscDatabase != true || header.BackupStartDate == default)
            throw new InvalidDataException("Cannot inspect a backup without its original FMSC SQL header.");
        DateTime sqlUtc = DateTime.SpecifyKind(header.BackupStartDate, DateTimeKind.Local).ToUniversalTime();
        if ((sqlUtc - expectedSqlUtc).Duration() > TimeSpan.FromSeconds(1))
            throw new InvalidDataException("The SQL backup timestamp changed before its contents were inspected.");

        Snapshot? captured = null;
        using var current = new SqlConnection(DatabaseHelper.GetConnectionString("FMSC_DB"));
        DatabaseHelper.OpenConnection(current);
        Guid expectedSchool = BackupRestoreIdentity.ReadScope(current);
        using var master = DatabaseHelper.GetMasterConnection();
        BackupRestoreIdentity.ValidateCandidate(master.ConnectionString, backupPath,
            Path.Combine(DatabaseHelper.DatabaseFolder, "RestoreChecks"), expectedSchool,
            candidate =>
            {
                DatabaseCompatibility.Validate(candidate);
                // All four counts come from the disposable RESTORED backup,
                // including data committed while the original BACKUP ran.
                using var command = new SqlCommand("""
                    SELECT
                        (SELECT COUNT_BIG(*) FROM dbo.Students),
                        (SELECT COUNT_BIG(DISTINCT AdmissionId) FROM dbo.Enrollments WHERE Status=N'Active'),
                        (SELECT COUNT_BIG(*) FROM dbo.FeePayments),
                        (SELECT COUNT_BIG(*) FROM dbo.BillingCharges),
                        (SELECT TOP(1) TRY_CONVERT(int, VersionNumber)
                           FROM dbo.DatabaseVersion ORDER BY VersionId DESC);
                    """, candidate) { CommandTimeout = 30 };
                using var reader = command.ExecuteReader(CommandBehavior.SingleRow);
                if (!reader.Read() || reader.IsDBNull(4))
                    throw new InvalidDataException("The restored database summary could not be read.");
                captured = new Snapshot(1, sha, sqlUtc,
                    reader.GetInt64(0), reader.GetInt64(1),
                    reader.GetInt64(2), reader.GetInt64(3),
                    Convert.ToInt32(reader.GetValue(4)));
            });
        var result = captured ?? throw new InvalidDataException("The restored backup has no readable summary.");
        Directory.CreateDirectory(Root);
        AtomicTextFile.Write(PathFor(sha), JsonSerializer.Serialize(result));
        PruneOldSummaries();
        return result;
    }

    /// <summary>
    /// Cloud points can use their already checked source SHA. Local-only
    /// backups are hashed before reusing cached numbers; old local backups
    /// are inspected on first expansion without changing the .bak.
    /// </summary>
    internal static Snapshot? FindOrInspect(UnifiedRecoveryPoint point)
    {
        foreach (var cloud in point.CloudCopies)
        {
            var stored = ReadForDigest(cloud.SourceBakSha256, point.SqlBackupCreatedUtc);
            if (stored is not null) return stored;
        }
        foreach (var local in point.LocalCopies)
        {
            if (!File.Exists(local.FilePath)) continue;
            try
            {
                using var input = new FileStream(local.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                string sha = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
                var stored = ReadForDigest(sha, point.SqlBackupCreatedUtc);
                if (stored is not null) return stored;
                // Verify/inspect the actual historical .bak in a disposable
                // database. No legacy file is modified or automatically migrated.
                return CaptureVerifiedBackup(local.FilePath, point.SqlBackupCreatedUtc);
            }
            catch (Exception ex)
            {
                AppExceptionHandler.LogNonFatal(ex,
                    "Reading historical backup contents without changing the original");
            }
        }
        return null;
    }

    private static Snapshot? ReadForDigest(string? sha, DateTime timeUtc)
    {
        if (!UnifiedRecoveryCatalog.IsSha256(sha)) return null;
        try
        {
            string path = PathFor(sha!);
            if (!File.Exists(path) || new FileInfo(path).Length > 4096) return null;
            var summary = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(path));
            return summary is { FormatVersion: 1, Students: >= 0, ActiveStudents: >= 0,
                FeePayments: >= 0, BillingCharges: >= 0, SchemaVersion: > 0 } &&
                string.Equals(summary.SourceBakSha256, sha, StringComparison.OrdinalIgnoreCase) &&
                (summary.SqlBackupCreatedUtc - timeUtc).Duration() <= TimeSpan.FromSeconds(1)
                ? summary : null;
        }
        catch (Exception ex)
        {
            AppExceptionHandler.LogNonFatal(ex, "Reading cached backup snapshot metadata");
            return null;
        }
    }

    private static string PathFor(string sha) =>
        Path.Combine(Root, sha.ToLowerInvariant() + ".json");

    private static void PruneOldSummaries()
    {
        try
        {
            var summaries = new DirectoryInfo(Root).EnumerateFiles("*.json")
                .OrderByDescending(file => file.LastWriteTimeUtc).ToArray();
            foreach (var old in summaries.Skip(MaximumSummaries))
                old.Delete();
        }
        catch (Exception ex)
        {
            // Metadata retention must never turn a valid verified SQL backup into
            // a failed operation, and must not delete any .bak or cloud package.
            AppExceptionHandler.LogNonFatal(ex, "Pruning optional backup summary metadata");
        }
    }
}
