using System.Text.Json;
using FMSC_SMS.Core;
using FMSC_SMS.Core.Updates;

namespace FMSC_SMS.Services.Backup;

/// <summary>
/// Small, bounded operational event journal, separate from the SQL history
/// stored in the school database. Never stores passwords, tokens, student
/// records, SQL data or remote object URLs. Failure to log cannot change SQL.
/// </summary>
internal static class BackupActivityJournal
{
    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FMSC", "CloudBackups");
    private static readonly string FilePath = Path.Combine(Root, "activity.v1.json");
    private static readonly string LockPath = Path.Combine(Root, "activity.lock");
    private const int MaximumEntries = 250;
    internal sealed record Entry(DateTime Utc, string Operation, string Destination,
        string State, string Detail);

    internal static void TryRecord(string operation, string destination,
        string state, string detail = "")
    {
        try
        {
            Directory.CreateDirectory(Root);
            UpdateDeviceKey.RestrictDirectory(Root);
            using var lease = CloudBackupJobLock.TryAcquire(LockPath);
            if (lease is null) return; // Never race an independent job's journal write.
            var entries = ReadCore().ToList();
            entries.Add(new Entry(DateTime.UtcNow, Clean(operation, 60),
                Clean(destination, 40), Clean(state, 80), Clean(detail, 200)));
            var retained = entries.OrderByDescending(x => x.Utc)
                .Take(MaximumEntries).OrderBy(x => x.Utc).ToArray();
            AtomicTextFile.Write(FilePath, JsonSerializer.Serialize(retained));
        }
        catch (Exception ex)
        {
            AppExceptionHandler.LogNonFatal(ex, "Recording backup and restore activity");
        }
    }

    internal static IReadOnlyList<Entry> ReadRecent()
    {
        try { return ReadCore().OrderByDescending(x => x.Utc).ToArray(); }
        catch (Exception ex)
        {
            AppExceptionHandler.LogNonFatal(ex, "Reading cloud backup activity");
            return Array.Empty<Entry>();
        }
    }

    private static IReadOnlyList<Entry> ReadCore()
    {
        if (!File.Exists(FilePath)) return Array.Empty<Entry>();
        if (new FileInfo(FilePath).Length > 512 * 1024)
            throw new InvalidDataException("Backup activity history exceeds its safe storage bound.");
        return JsonSerializer.Deserialize<Entry[]>(File.ReadAllText(FilePath))
            ?? throw new InvalidDataException("Backup activity history was not readable.");
    }

    private static string Clean(string? value, int limit)
    {
        string normalized = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= limit ? normalized : normalized[..limit];
    }
}
