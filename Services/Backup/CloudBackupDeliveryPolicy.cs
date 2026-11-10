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
}
