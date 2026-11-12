using FMSC_SMS.Services.Backup;

internal static class CloudQueueSafetyChecks
{
    internal static void Run()
    {
        var oldest = new DateTime(2026, 11, 8, 18, 0, 0, DateTimeKind.Utc);
        var newest = oldest.AddDays(1);
        Require(!CloudBackupQueuePolicy.MaySupersede(oldest, newest, false, true, true, false),
            "Unverified remote upload replaced pending ciphertext.");
        Require(!CloudBackupQueuePolicy.MaySupersede(oldest, newest, true, false, true, false),
            "Already deleted remote copy replaced pending ciphertext.");
        Require(!CloudBackupQueuePolicy.MaySupersede(oldest, oldest, true, true, true, false),
            "Equal-time recovery points superseded each other.");
        Require(!CloudBackupQueuePolicy.MaySupersede(oldest, newest, true, true, false, false),
            "Unrequested destination superseded another destination.");
        Require(!CloudBackupQueuePolicy.MaySupersede(oldest, newest, true, true, true, true),
            "Completed provider was marked superseded.");
        Require(CloudBackupQueuePolicy.MaySupersede(oldest, newest, true, true, true, false),
            "Verified newer provider was unable to safely retire outdated pending upload.");
        Require(!CloudBackupQueuePolicy.CanRelease(false, false, false, false,
            false, false, false, false),
            "Queue discarded its sole encrypted copy after both clouds were disabled.");
        Require(!CloudBackupQueuePolicy.CanRelease(true, false, false, true,
            true, false, false, false),
            "Package still required by the other provider was discarded.");
        Require(CloudBackupQueuePolicy.CanRelease(true, false, false, true,
            true, true, false, false),
            "Completed or safely superseded deliveries were never released.");
        Require(CloudBackupQueuePolicy.ShouldPauseCreation(10_000, 10_000, 50_000_000_000, 100),
            "Full encrypted queue did not block additional backup creation.");
        Require(CloudBackupQueuePolicy.ShouldPauseCreation(0, 100_000_000_000,
            500_000_000, 10_000),
            "Low disk reserve did not stop automatic backup creation.");
        Require(!CloudBackupQueuePolicy.ShouldPauseCreation(0, 100_000_000_000,
            50_000_000_000, 100_000),
            "Healthy empty queue was blocked.");
        Require(CloudBackupQueuePolicy.ShouldPauseCreation(9_000, 10_000,
            50_000_000_000, 2_000),
            "Insufficient remaining capacity did not block encryption.");
        DateTime anchor = new DateTime(2026, 11, 12, 9, 0, 0, DateTimeKind.Utc);
        foreach (int n in new[] { 1, 2, 3, 4, 5, 20 })
        {
            DateTime next = CloudBackupQueuePolicy.NextUnattendedAttemptUtc(anchor, n);
            Require(next > anchor && next <= anchor.AddDays(1),
                "Retry backoff lacked a bounded future delay.");
            Require(!CloudBackupQueuePolicy.RetryIsDue(next, anchor) &&
                CloudBackupQueuePolicy.RetryIsDue(next, next),
                "Scheduled retry ran before its backoff elapsed.");
        }
        Console.WriteLine("PASS: encrypted queue capacity, verified supersession and bounded retry backoff.");
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new Exception(reason);
    }
}
