namespace FMSC_SMS.Services.Backup;

/// <summary>
/// A remote copy is not automatically a trustworthy recovery point.
/// SqlBackupCreatedUtc must originate from the SQL backup header at creation,
/// and is rechecked against the downloaded .bak before any actual restore.
/// UploadedAtUtc is used for transport diagnostics ONLY, never ordering.
/// </summary>
internal sealed record CloudBackupCopy(
    string Provider,
    string RemoteId,
    string PackageSha256,
    DateTime SqlBackupCreatedUtc,
    DateTime UploadedAtUtc,
    long EncryptedSizeBytes,
    bool Verified);

internal sealed record CloudRecoveryPoint(
    DateTime SqlBackupCreatedUtc,
    string PackageSha256,
    IReadOnlyList<CloudBackupCopy> Copies,
    bool Verified);

internal static class CloudBackupCatalog
{
    internal static IReadOnlyList<CloudRecoveryPoint> GroupRecoveryPoints(
        IEnumerable<CloudBackupCopy> copies)
    {
        ArgumentNullException.ThrowIfNull(copies);
        return copies
            .Where(x => x.EncryptedSizeBytes > 0 && x.PackageSha256.Length == 64 &&
                x.PackageSha256.All(Uri.IsHexDigit) &&
                x.SqlBackupCreatedUtc != default && x.Provider is "GoogleDrive" or "Dropbox" or "Local")
            .GroupBy(x => x.PackageSha256, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var all = group.ToArray();
                // Refuse to present mismatched timestamp metadata from a
                // duplicate remote object as a single verified recovery point.
                bool consistent = all.All(copy => copy.SqlBackupCreatedUtc == all[0].SqlBackupCreatedUtc);
                return new CloudRecoveryPoint(
                    all[0].SqlBackupCreatedUtc, group.Key, all, consistent && all.Any(copy => copy.Verified));
            })
            .OrderByDescending(x => x.SqlBackupCreatedUtc)
            .ThenBy(x => x.PackageSha256, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Pruning is allowed ONLY after a confirmed new, verified copy exists.
    /// The caller must confirm remote deletion independently; safety/migration
    /// backup files never belong to this catalog.
    /// </summary>
    internal static IReadOnlyList<CloudBackupCopy> CopiesEligibleForDeletion(
        IEnumerable<CloudBackupCopy> allCopies,
        string provider,
        int maxRetained,
        string newlyConfirmedPackageSha256)
    {
        if (maxRetained < 1 || maxRetained > 365)
            throw new ArgumentOutOfRangeException(nameof(maxRetained));
        var providerCopies = allCopies
            .Where(x => x.Provider == provider && x.Verified)
            .GroupBy(x => x.PackageSha256, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.SqlBackupCreatedUtc).First())
            .OrderByDescending(x => x.SqlBackupCreatedUtc)
            .ToArray();
        if (!providerCopies.Any(x => string.Equals(x.PackageSha256, newlyConfirmedPackageSha256,
                StringComparison.OrdinalIgnoreCase)))
            return Array.Empty<CloudBackupCopy>();
        // Retain the newest N VALID recovery points. A provider containing no
        // valid recent copy cannot be pruned based on upload-time metadata.
        return providerCopies.Skip(maxRetained).ToArray();
    }
}
