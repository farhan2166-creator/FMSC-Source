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
    Console.WriteLine("PASS: encrypted cloud backups round-trip, detect tampering/truncation/wrong keys, and leave no committed .bak on failure.");
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
