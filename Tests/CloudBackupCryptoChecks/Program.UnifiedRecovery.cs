using FMSC_SMS.Services.Backup;

internal static class UnifiedRecoveryChecks
{
    internal static void Run()
    {
        var when = new DateTime(2026, 11, 12, 9, 15, 0, DateTimeKind.Utc);
        const string localPath = @"C:\Backups\linked.bak";
        const string oldPath = @"C:\Backups\legacy.bak";
        string source = new string('b', 64), package = new string('a', 64);
        var local = new[] {
            new RecoveryLocalCopy(localPath, when, source),
            new RecoveryLocalCopy(oldPath, when) // same time is NOT proof of equality
        };
        var remote = new[] {
            new RecoveryCloudCopy(
                new CloudRemoteBackup(CloudProvider.GoogleDrive, "drive1", "backup", package,
                    when, when.AddMinutes(3), 12345), true, source),
            new RecoveryCloudCopy(
                new CloudRemoteBackup(CloudProvider.Dropbox, "dropbox1", "backup", package,
                    when, when.AddMinutes(4), 12345), false, source)
        };
        var grouped = UnifiedRecoveryCatalog.Group(local, remote);
        Require(grouped.Count == 2 && grouped[0].CloudCopies.Count == 2 &&
            grouped[0].LocalCopies.Count == 1 && grouped[0].LocalCopies[0].FilePath == localPath &&
            grouped[0].HasRecoveryTestedCloud, "Same authenticated SQL source was not grouped.");
        Require(grouped[0].RecoveryTestedCloudCount == 1,
            "A recovery test for one provider must not count toward another provider's copy.");
        Require(grouped.Single(x => x.LocalCopies.Any(y => y.FilePath == oldPath)).CloudCopies.Count == 0,
            "Legacy file was merged with cloud using only the SQL creation timestamp.");
        var wrongDigest = new[] { local[0] with { SourceBakSha256 = new string('c', 64) } };
        Require(UnifiedRecoveryCatalog.Group(wrongDigest, remote).Count == 2,
            "Different local backup digest was silently treated as the same recovery point.");
        var wrongTime = new[] { local[0] with { SqlBackupCreatedUtc = when.AddMinutes(1) } };
        Require(UnifiedRecoveryCatalog.Group(wrongTime, remote).Count == 2,
            "Matching digests with mismatched SQL backup time were silently merged.");
        var conflicting = new[] { remote[0], remote[1] with { SourceBakSha256 = new string('c',64) } };
        Require(UnifiedRecoveryCatalog.Group(new[] { local[0] }, conflicting).Count == 2,
            "Conflicting cloud journal source identities must not attach a local copy.");
        var unverified = remote.Select(x => x with { RecoveryTested = false }).ToArray();
        Require(!UnifiedRecoveryCatalog.Group(Array.Empty<RecoveryLocalCopy>(), unverified)
            .Single().HasRecoveryTestedCloud,
            "A cloud listing or upload receipt incorrectly established recovery verification.");
        var differentTimestamp = remote[1] with {
            Remote = remote[1].Remote with { SqlBackupCreatedUtc = when.AddHours(1) }
        };
        Require(UnifiedRecoveryCatalog.Group(Array.Empty<RecoveryLocalCopy>(),
            new[] { remote[0], differentTimestamp }).Count == 2,
            "Encrypted package copies with contradictory SQL creation time were merged.");
        Require(UnifiedRecoveryCatalog.Group(Array.Empty<RecoveryLocalCopy>(),
            new[] { remote[0] with { Remote = remote[0].Remote with { Size = 0 } } }).Count == 0,
            "Empty upload placeholders must not be offered as recovery points.");
        Console.WriteLine("PASS: local/cloud catalogue digest linkage, legacy isolation and verification provenance.");
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new Exception(reason);
    }
}
