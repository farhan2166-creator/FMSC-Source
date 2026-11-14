namespace FMSC_SMS.Services.Backup;

/// <summary>
/// Retention is not a transport failure. A previously uploaded backup that
/// was intentionally pruned must never be scheduled for upload again solely
/// because the remote file ID was cleared.
/// </summary>
internal static class CloudBackupDeliveryPolicy
{
    internal static bool RequiresUpload(bool enabled, string remoteId, bool pruned)
        => enabled && remoteId.Length == 0 && !pruned;

    internal static bool HasCompletedProvider(bool enabled, string remoteId, bool pruned)
        => !enabled || remoteId.Length > 0 || pruned;

    // New delivery states distinguish a provider upload receipt from a fully
    // downloaded, decrypted and SQL recovery-tested copy. Historic receipts
    // must not be promoted into new verification statuses.
    internal static bool RequiresRecoveryVerification(bool enabled, bool verified, bool pruned)
        => enabled && !verified && !pruned;

    internal static bool MayReleaseQueuedPackage(bool enabled, bool verified, bool pruned)
    {
        // A disabled destination or an upload receipt alone is never evidence
        // that a queued ciphertext is safe to delete.
        return (!enabled || verified || pruned) && verified;
    }
}
