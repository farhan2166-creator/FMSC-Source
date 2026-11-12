namespace FMSC_SMS.Services.Backup;

/// <summary>
/// An available recovery point is a SQL backup, not an upload attempt.
/// Cloud copies can be grouped by equal encrypted-package SHA-256 and SQL
/// creation time. A local .bak is NEVER associated with a cloud copy by name
/// or timestamp alone: both need an independently checked plaintext SHA-256
/// recorded when the original backup was made.
/// </summary>
internal sealed record RecoveryLocalCopy(
    string FilePath, DateTime SqlBackupCreatedUtc, string SourceBakSha256 = "");

internal sealed record RecoveryCloudCopy(
    CloudRemoteBackup Remote, bool RecoveryTested, string SourceBakSha256 = "");

internal sealed record UnifiedRecoveryPoint(
    string RecoveryId,
    DateTime SqlBackupCreatedUtc,
    IReadOnlyList<RecoveryLocalCopy> LocalCopies,
    IReadOnlyList<RecoveryCloudCopy> CloudCopies)
{
    internal bool HasLocal => LocalCopies.Count > 0;
    internal bool HasRecoveryTestedCloud => CloudCopies.Any(x => x.RecoveryTested);
    internal string AvailableSources => string.Join(", ",
        (HasLocal ? new[] { "Local" } : Array.Empty<string>())
        .Concat(CloudCopies.Select(x => x.Remote.Provider.ToString()).Distinct()));
    internal string VerificationSummary => HasLocal
        ? (CloudCopies.Count == 0 ? "Local SQL header verified" :
            HasRecoveryTestedCloud ? "Local + cloud recovery-tested" : "Local verified; cloud not recovery-tested")
        : HasRecoveryTestedCloud
            ? "Cloud recovery-tested"
            : "Cloud copy — verify before restore";
}

/// <summary>
/// Pure, testable grouping policy. File header checks and digest computation
/// happen at the inventory boundary, never from unauthenticated listing data.
/// </summary>
internal static class UnifiedRecoveryCatalog
{
    internal static IReadOnlyList<UnifiedRecoveryPoint> Group(
        IEnumerable<RecoveryLocalCopy> localCopies,
        IEnumerable<RecoveryCloudCopy> cloudCopies)
    {
        ArgumentNullException.ThrowIfNull(localCopies);
        ArgumentNullException.ThrowIfNull(cloudCopies);
        var local = localCopies
            .Where(x => !string.IsNullOrWhiteSpace(x.FilePath) &&
                x.SqlBackupCreatedUtc != default)
            .DistinctBy(x => x.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var cloud = cloudCopies
            .Where(x => x.Remote.SqlBackupCreatedUtc != default &&
                IsSha256(x.Remote.PackageSha256))
            .ToArray();
        var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<UnifiedRecoveryPoint>();
        foreach (var group in cloud.GroupBy(x =>
            (Hash: x.Remote.PackageSha256.ToLowerInvariant(),
                Time: x.Remote.SqlBackupCreatedUtc.Ticks)))
        {
            var copies = group
                .DistinctBy(x => (x.Remote.Provider, x.Remote.RemoteId))
                .ToArray();
            var linkedDigests = copies.Select(x => x.SourceBakSha256)
                .Where(IsSha256).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            // A conflict in local journal identities means no automatic link.
            var matching = linkedDigests.Length == 1
                ? local.Where(x => x.SqlBackupCreatedUtc.Ticks == group.Key.Time &&
                      string.Equals(x.SourceBakSha256, linkedDigests[0],
                          StringComparison.OrdinalIgnoreCase)).ToArray()
                : Array.Empty<RecoveryLocalCopy>();
            foreach (var item in matching) assigned.Add(item.FilePath);
            result.Add(new UnifiedRecoveryPoint(
                "cloud:" + group.Key.Hash + ":" + group.Key.Time,
                copies[0].Remote.SqlBackupCreatedUtc, matching, copies));
        }

        // Unlinked legacy .bak files are still restorable. Deliberately keep
        // them as separate points rather than guessing they equal a cloud copy.
        foreach (var localCopy in local.Where(x => !assigned.Contains(x.FilePath)))
        {
            result.Add(new UnifiedRecoveryPoint(
                "local:" + localCopy.FilePath.ToUpperInvariant(),
                localCopy.SqlBackupCreatedUtc,
                new[] { localCopy },
                Array.Empty<RecoveryCloudCopy>()));
        }
        return result.OrderByDescending(x => x.SqlBackupCreatedUtc)
            .ThenBy(x => x.RecoveryId, StringComparer.Ordinal).ToArray();
    }

    internal static bool IsSha256(string? hash) =>
        hash is { Length: 64 } && hash.All(Uri.IsHexDigit);
}
