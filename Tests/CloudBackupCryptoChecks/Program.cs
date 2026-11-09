using FMSC_SMS.Services.Backup;
using System.Security.Cryptography;

string root = Path.Combine(Path.GetTempPath(), "FMSC-CloudCryptoChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    string source = Path.Combine(root, "source.bak");
    string package = Path.Combine(root, "source.fmscbackup");
    string restored = Path.Combine(root, "restored.bak");
    string passphrase = "test-only-long-recovery-passphrase";
    var random = RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 137);
    File.WriteAllBytes(source, random);
    var originalTime = new DateTime(2026, 11, 9, 13, 0, 0, DateTimeKind.Utc);
    CloudBackupCrypto.Encrypt(source, package, passphrase, originalTime);
    if (CloudBackupCrypto.ReadUnverifiedCreationUtc(package) != originalTime)
        throw new Exception("Package recovery point timestamp changed.");
    CloudBackupCrypto.Decrypt(package, restored, passphrase);
    if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(File.ReadAllBytes(restored)), SHA256.HashData(random)))
        throw new Exception("Cloud backup did not survive round trip.");
    File.Delete(restored);
    MustReject(() => CloudBackupCrypto.Decrypt(package, restored, "wrong-but-valid-recovery-key"), "incorrect passphrase");
    if (File.Exists(restored)) throw new Exception("Wrong-key attempt left a restorable output.");

    string tampered = Path.Combine(root, "tampered.fmscbackup");
    var modified = File.ReadAllBytes(package);
    modified[90] ^= 0x40;
    File.WriteAllBytes(tampered, modified);
    MustReject(() => CloudBackupCrypto.Decrypt(tampered, restored, passphrase), "tampered ciphertext");
    if (File.Exists(restored)) throw new Exception("Tampering left a restorable output.");

    string truncated = Path.Combine(root, "truncated.fmscbackup");
    File.WriteAllBytes(truncated, modified.AsSpan(0, modified.Length - 27).ToArray());
    MustReject(() => CloudBackupCrypto.Decrypt(truncated, restored, passphrase), "truncated package");
    if (File.Exists(restored)) throw new Exception("Truncated download left a restorable output.");

    string invalid = Path.Combine(root, "invalid.fmscbackup");
    File.WriteAllBytes(invalid, Array.Empty<byte>());
    MustReject(() => CloudBackupCrypto.Decrypt(invalid, restored, passphrase), "empty package");
    var h1 = new string('a', 64);
    var h2 = new string('b', 64);
    var earliestCreated = new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);
    var latestCreated = earliestCreated.AddHours(4);
    var laterUploaded = earliestCreated.AddHours(10);
    var copies = new[] {
        new CloudBackupCopy("GoogleDrive", "old", h1, earliestCreated, laterUploaded, 123, true),
        new CloudBackupCopy("Dropbox", "old-copy", h1, earliestCreated, laterUploaded.AddHours(1), 123, true),
        new CloudBackupCopy("Local", "new", h2, latestCreated, latestCreated.AddMinutes(1), 125, true)
    };
    var recovery = CloudBackupCatalog.GroupRecoveryPoints(copies);
    if (recovery.Count != 2 || recovery[0].PackageSha256 != h2 || recovery[1].Copies.Count != 2)
        throw new Exception("Recovery comparison used upload time or did not group matching copies.");
    if (CloudBackupCatalog.CopiesEligibleForDeletion(copies, "GoogleDrive", 1, h2).Count != 0)
        throw new Exception("Retention deleted data before upload confirmation.");
    if (CloudBackupCatalog.CopiesEligibleForDeletion(copies, "Dropbox", 1, h1).Count != 0)
        throw new Exception("Retention should not delete the only verified copy.");
    // Daily: a powered-off PC must retry yesterday's missed 6 PM backup
    // when it comes back online at 9 AM, not wait until tonight.
    var daily9Am = new DateTime(2026, 11, 10, 9, 0, 0);
    var dailyLast = new DateTime(2026, 11, 8, 18, 4, 0);
    var at6Pm = new TimeOnly(18, 0);
    if (!CloudBackupSchedulePolicy.IsDue(true, CloudBackupFrequency.Daily, at6Pm,
            DayOfWeek.Friday, daily9Am, dailyLast))
        throw new Exception("Missed daily backup was not retried on the next PC startup.");
    if (CloudBackupSchedulePolicy.IsDue(true, CloudBackupFrequency.Daily, at6Pm,
            DayOfWeek.Friday, daily9Am, new DateTime(2026, 11, 9, 18, 2, 0)))
        throw new Exception("Daily backup reran without a missed scheduled recovery point.");
    // Weekly: selecting Friday 6 PM must catch up on Saturday after shutdown.
    var saturday = new DateTime(2026, 11, 14, 11, 0, 0);
    if (!CloudBackupSchedulePolicy.IsDue(true, CloudBackupFrequency.Weekly, at6Pm,
            DayOfWeek.Friday, saturday, new DateTime(2026, 11, 6, 18, 5, 0)))
        throw new Exception("Missed Friday weekly backup was not retried Saturday.");
    if (CloudBackupSchedulePolicy.IsDue(true, CloudBackupFrequency.Weekly, at6Pm,
            DayOfWeek.Friday, saturday, new DateTime(2026, 11, 13, 18, 2, 0)))
        throw new Exception("Weekly backup repeated despite successful Friday run.");
    if (!CloudBackupSchedulePolicy.IsDue(true, CloudBackupFrequency.Every12Hours, at6Pm,
            DayOfWeek.Friday, new DateTime(2026, 11, 10, 10, 0, 0),
            new DateTime(2026, 11, 9, 18, 5, 0)))
        throw new Exception("12-hour schedule skipped the morning run.");
    if (!CloudBackupSchedulePolicy.IsDue(true, CloudBackupFrequency.Daily, at6Pm,
            DayOfWeek.Friday, daily9Am, null))
        throw new Exception("First enrolled school backup must run without waiting a week.");
    if (CloudBackupSchedulePolicy.IsDue(false, CloudBackupFrequency.Daily, at6Pm,
            DayOfWeek.Friday, daily9Am, null))
        throw new Exception("Disabled backup schedule ran without operator enrollment.");
    Console.WriteLine("PASS: encrypted backup corruption checks and recovery-point creation-time ordering.");
}
finally
{
    Directory.Delete(root, recursive: true);
}

static void MustReject(Action action, string scenario)
{
    try { action(); }
    catch (Exception ex) when (ex is CryptographicException or InvalidDataException or EndOfStreamException)
    {
        return;
    }
    throw new Exception("Cloud backup must reject " + scenario);
}
