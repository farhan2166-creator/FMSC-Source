namespace FMSC_SMS.Services.Backup;

/// <summary>
/// Pure, non-destructive decisions for a protected encrypted upload queue.
/// Only a verified *newer* remote recovery point on the same destination can
/// supersede an older undelivered copy. Never compare upload timestamps.
/// </summary>
internal static class CloudBackupQueuePolicy
{
    internal static bool MaySupersede(DateTime olderSqlUtc, DateTime newerSqlUtc,
        bool newerRecoveryTested, bool newerStillAvailable, bool originallyRequested,
        bool alreadyComplete)
        => originallyRequested && !alreadyComplete && newerRecoveryTested &&
           newerStillAvailable && olderSqlUtc != default && newerSqlUtc != default &&
           newerSqlUtc > olderSqlUtc;

    internal static bool CanRelease(bool googleRequested, bool googleVerified,
        bool googlePruned, bool googleSuperseded, bool dRequested,
        bool dVerified, bool dPruned, bool dSuperseded)
    {
        bool googleDone = !googleRequested || googleVerified || googlePruned || googleSuperseded;
        bool dropboxDone = !dRequested || dVerified || dPruned || dSuperseded;
        return googleDone && dropboxDone &&
               (googleVerified || googleSuperseded || dVerified || dSuperseded);
    }

    internal static bool ShouldPauseCreation(long queueBytes, long maximumBytes,
        long availableDiskBytes, long expectedSqlBytes)
    {
        if (queueBytes < 0 || maximumBytes <= 0 || availableDiskBytes < 0 ||
            expectedSqlBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(queueBytes));
        // SQL .bak and encrypted staging coexist transiently. Retain a 1 GiB
        // safety reserve and the footprint of both copies.
        long reserve = Math.Max(1024L * 1024 * 1024,
            expectedSqlBytes > (long.MaxValue / 2) ? long.MaxValue : expectedSqlBytes * 2);
        return queueBytes >= maximumBytes ||
               availableDiskBytes <= reserve ||
               expectedSqlBytes > maximumBytes - queueBytes;
    }

    internal static DateTime NextUnattendedAttemptUtc(DateTime nowUtc, int attempts)
    {
        // 15m, 30m, 60m, 120m; cap at 24h if attempt budget changes.
        int exponent = Math.Clamp(attempts - 1, 0, 7);
        return nowUtc.AddMinutes(Math.Min(1440, 15 * (1 << exponent)));
    }

    internal static bool RetryIsDue(DateTime? nextUtc, DateTime nowUtc) =>
        nextUtc is null || nowUtc >= nextUtc;
}
