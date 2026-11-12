using FMSC_SMS.Services.Backup;
using System.Security.Cryptography;

await ReleaseReviewChecks.RunAsync();
await AuditFollowupChecks.RunAsync();
if (args.SequenceEqual(new[] { "--release-review" })) return;

string root = Path.Combine(Path.GetTempPath(), "FMSC-CloudCryptoChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    CloudSettingsChecks.Run(root);
    if (args.SequenceEqual(new[] { "--settings" })) return;
    // DSA-01: exclusive lease must survive await/thread switches, block another
    // process/attempt, and become available when the handle is disposed.
    string lockFile = Path.Combine(root, "cloud-job.lock");
    using (var lease = CloudBackupJobLock.TryAcquire(lockFile)
        ?? throw new Exception("First cloud job lease was refused."))
    {
        await Task.Run(async () =>
        {
            await Task.Yield();
            using var competing = CloudBackupJobLock.TryAcquire(lockFile);
            if (competing != null) throw new Exception("Cloud job exclusion failed.");
        });
    }
    using (var retry = await Task.Run(() => CloudBackupJobLock.TryAcquire(lockFile)))
    {
        if (retry is null) throw new Exception("Cloud job lease was not released after async work.");
    }
    // A failed lock path is NOT mistaken for another job in progress.
    try
    {
        using var invalidLock = CloudBackupJobLock.TryAcquire(Path.Combine(root, "missing-folder", "job.lock"));
        throw new Exception("Cloud job lock failure was incorrectly accepted.");
    }
    catch (DirectoryNotFoundException) { }

    string source = Path.Combine(root, "source.bak");
    string package = Path.Combine(root, "source.fmscbackup");
    string restored = Path.Combine(root, "restored.bak");
    string passphrase = "test-only-long-recovery-passphrase";
    var random = RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 137);
    File.WriteAllBytes(source, random);
    // DSA-08: Dropbox's server-generated content hash is SHA-256 of the
    // concatenation of individual 4-MiB block hashes, not the local SHA-256.
    string oneBlockHash = Convert.ToHexString(SHA256.HashData(SHA256.HashData(random)))
        .ToLowerInvariant();
    if (CloudDropboxIntegrity.ComputeContentHash(source) != oneBlockHash)
        throw new Exception("Dropbox one-block content hash calculation failed.");
    byte[] block = RandomNumberGenerator.GetBytes(4 * 1024 * 1024);
    byte[] tail = RandomNumberGenerator.GetBytes(17);
    string twoBlock = Path.Combine(root, "dropbox-two-blocks.bin");
    using (var output = File.Create(twoBlock))
    {
        output.Write(block);
        output.Write(tail);
    }
    string expectedContentHash = Convert.ToHexString(SHA256.HashData(
        SHA256.HashData(block).Concat(SHA256.HashData(tail)).ToArray())).ToLowerInvariant();
    if (CloudDropboxIntegrity.ComputeContentHash(twoBlock) != expectedContentHash)
        throw new Exception("Dropbox multi-block remote content hash calculation failed.");
    using (var remoteReceipt = System.Text.Json.JsonDocument.Parse(
        System.Text.Json.JsonSerializer.Serialize(new {
            id = "id:verified-remote", name = "verified.fmscbackup",
            size = new FileInfo(twoBlock).Length, content_hash = expectedContentHash
        })))
    {
        CloudDropboxIntegrity.RequireMatchingMetadata(remoteReceipt.RootElement,
            "verified.fmscbackup", new FileInfo(twoBlock).Length, expectedContentHash);
        bool rejected = false;
        try {
            CloudDropboxIntegrity.RequireMatchingMetadata(remoteReceipt.RootElement,
                "verified.fmscbackup", new FileInfo(twoBlock).Length, new string('0',64));
        } catch (IOException) { rejected = true; }
        if (!rejected) throw new Exception("Dropbox mismatched provider content hash accepted.");
    }
    using (var notFound = System.Text.Json.JsonDocument.Parse(
        """{"error":{".tag":"path","path":{".tag":"not_found"}}}"""))
    {
        if (!CloudDropboxIntegrity.IsPathNotFound(notFound.RootElement))
            throw new Exception("Dropbox missing-file response not recognized.");
    }
    using (var denied = System.Text.Json.JsonDocument.Parse(
        """{"error":{".tag":"path","path":{".tag":"restricted_content"}}}"""))
    {
        if (CloudDropboxIntegrity.IsPathNotFound(denied.RootElement))
            throw new Exception("Dropbox permission error treated as a missing backup.");
    }

    var originalTime = new DateTime(2026, 11, 9, 13, 0, 0, DateTimeKind.Utc);
    // Simulate a school in UTC+5: SQL backup header contains a local
    // timestamp distinct from the cloud catalog's absolute UTC time.
    var originalSqlHeaderTime = new DateTime(2026, 11, 9, 18, 0, 0);
    CloudBackupCrypto.Encrypt(source, package, passphrase, originalTime, originalSqlHeaderTime);
    if (CloudBackupCrypto.ReadUnverifiedCreationUtc(package) != originalTime)
        throw new Exception("Package recovery point timestamp changed.");
    CloudBackupCrypto.Decrypt(package, restored, passphrase);
    if (CloudBackupCrypto.ReadSqlHeaderLocalAfterVerification(package).Ticks
        != originalSqlHeaderTime.Ticks)
        throw new Exception("Authenticated original SQL backup time was altered by local time zone.");
    if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(File.ReadAllBytes(restored)), SHA256.HashData(random)))
        throw new Exception("Cloud backup did not survive round trip.");
    // DSA-11: exercise many encryption/decryption chunks and compare streaming
    // digests without loading the expanded fixture into memory.
    string largeSql = Path.Combine(root, "many-chunks.bak");
    string largePackage = Path.Combine(root, "many-chunks.fmscbackup");
    string largeRestore = Path.Combine(root, "many-chunks-verified.bak");
    using (var output = File.Create(largeSql))
    {
        byte[] payload = RandomNumberGenerator.GetBytes(1024 * 1024);
        for (int i = 0; i < 24; i++) output.Write(payload);
    }
    CloudBackupCrypto.Encrypt(largeSql, largePackage, passphrase, originalTime, originalSqlHeaderTime);
    CloudBackupCrypto.Decrypt(largePackage, largeRestore, passphrase);
    using (var expected = File.OpenRead(largeSql))
    using (var actual = File.OpenRead(largeRestore))
    {
        if (!CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(expected), SHA256.HashData(actual)))
            throw new Exception("Multi-chunk encrypted SQL backup roundtrip failed.");
    }
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
    // DSA-03: a retained local ciphertext pending to another provider must
    // never re-upload its intentionally pruned Google/Dropbox remote copy.
    if (CloudBackupDeliveryPolicy.RequiresUpload(true, "", true) ||
        !CloudBackupDeliveryPolicy.RequiresUpload(true, "", false) ||
        CloudBackupDeliveryPolicy.RequiresUpload(true, "id:present", false) ||
        CloudBackupDeliveryPolicy.RequiresUpload(false, "", false) ||
        !CloudBackupDeliveryPolicy.HasCompletedProvider(true, "", true) ||
        CloudBackupDeliveryPolicy.HasCompletedProvider(true, "", false) ||
        !CloudBackupDeliveryPolicy.HasCompletedProvider(true, "id:present", false) ||
        !CloudBackupDeliveryPolicy.HasCompletedProvider(false, "", false))
        throw new Exception("Cloud provider retention incorrectly requeued a pruned backup.");

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
    // Browser OAuth should require neither an elevated HTTP.sys URLACL nor
    // a public listener. Unrelated callback requests cannot hijack the flow.
    using (var oauth = new CloudOAuthLoopback())
    using (var browser = new HttpClient())
    {
        string localState = "unit-test-state-random-1234";
        var authCodeTask = oauth.ReceiveCodeAsync(localState, CancellationToken.None);
        using var rejected = await browser.GetAsync(oauth.CallbackUrl +
            "?state=wrong-state&code=invalid-callback-code");
        if (rejected.StatusCode != System.Net.HttpStatusCode.BadRequest)
            throw new Exception("Wrong-state browser callback was not rejected.");
        if (authCodeTask.IsCompleted)
            throw new Exception("Unrelated OAuth callback ended the real sign-in session.");
        using var accepted = await browser.GetAsync(oauth.CallbackUrl +
            "?state=" + localState + "&code=valid-oauth-code-12345");
        if (accepted.StatusCode != System.Net.HttpStatusCode.OK ||
            await authCodeTask != "valid-oauth-code-12345")
            throw new Exception("Valid localhost OAuth callback was not verified.");
    }
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
