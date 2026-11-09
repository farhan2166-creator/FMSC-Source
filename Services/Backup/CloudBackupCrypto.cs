using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace FMSC_SMS.Services.Backup;

/// <summary>
/// Versioned, streaming, authenticated backup package. The recovery passphrase
/// is never included in the package or derived from the Windows installation.
/// A school must retain that passphrase outside the computer being protected.
/// File timestamps are advisory UNTIL authenticated decryption and the SQL
/// backup header have both been checked.
/// </summary>
internal static class CloudBackupCrypto
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("FMSC-CLD-02\0");
    private const int ChunkBytes = 1024 * 1024;
    private const int TagBytes = 16;
    private const int SaltBytes = 16;
    private const int NoncePrefixBytes = 8;
    private const int KdfIterations = 310_000;
    private const int HeaderBytes = 12 + SaltBytes + NoncePrefixBytes + 8 + 8 + 8;

    internal static void Encrypt(string backupFile, string destination, string passphrase,
        DateTime backupCreatedUtc, DateTime? originalSqlHeaderLocal = null)
    {
        CheckPassphrase(passphrase);
        using var input = new FileStream(backupFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length == 0) throw new InvalidDataException("An empty SQL backup cannot be uploaded.");
        byte[] header = new byte[HeaderBytes];
        Magic.CopyTo(header, 0);
        RandomNumberGenerator.Fill(header.AsSpan(12, SaltBytes + NoncePrefixBytes));
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(36, 8), backupCreatedUtc.ToUniversalTime().Ticks);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(44, 8), input.Length);
        // Store the ORIGINAL SQL RESTORE HEADERONLY timestamp without applying
        // this Windows machine's time zone. The field participates in GCM
        // additional authenticated data and is checked after decrypting.
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(52, 8),
            (originalSqlHeaderLocal ?? backupCreatedUtc.ToLocalTime()).Ticks);
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(passphrase, header.AsSpan(12, SaltBytes),
            KdfIterations, HashAlgorithmName.SHA256, 32);
        byte[] plain = new byte[ChunkBytes], cipher = new byte[ChunkBytes], tag = new byte[TagBytes];
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
            output.Write(header);
            using var aes = new AesGcm(key, TagBytes);
            int index = 0;
            long remaining = input.Length;
            while (remaining > 0)
            {
                int count = (int)Math.Min(ChunkBytes, remaining);
                input.ReadExactly(plain.AsSpan(0, count));
                Span<byte> nonce = stackalloc byte[12];
                header.AsSpan(28, NoncePrefixBytes).CopyTo(nonce);
                BinaryPrimitives.WriteInt32LittleEndian(nonce[8..], index);
                Span<byte> aad = stackalloc byte[HeaderBytes + 8];
                header.AsSpan().CopyTo(aad);
                BinaryPrimitives.WriteInt32LittleEndian(aad[HeaderBytes..], index);
                BinaryPrimitives.WriteInt32LittleEndian(aad[(HeaderBytes + 4)..], count);
                aes.Encrypt(nonce, plain.AsSpan(0, count), cipher.AsSpan(0, count), tag, aad);
                output.Write(cipher, 0, count);
                output.Write(tag);
                index = checked(index + 1);
                remaining -= count;
            }
            output.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(cipher);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static void Decrypt(string package, string destinationBak, string passphrase)
    {
        CheckPassphrase(passphrase);
        using var input = new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] header = new byte[HeaderBytes];
        input.ReadExactly(header);
        if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("Unsupported or invalid FMSC encrypted backup package.");
        long length = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(44, 8));
        if (length <= 0) throw new InvalidDataException("Invalid encrypted backup length.");
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(passphrase, header.AsSpan(12, SaltBytes),
            KdfIterations, HashAlgorithmName.SHA256, 32);
        byte[] plain = new byte[ChunkBytes], cipher = new byte[ChunkBytes], tag = new byte[TagBytes];
        string temporary = destinationBak + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
            using var aes = new AesGcm(key, TagBytes);
            int index = 0;
            long remaining = length;
            while (remaining > 0)
            {
                int count = (int)Math.Min(ChunkBytes, remaining);
                input.ReadExactly(cipher.AsSpan(0, count));
                input.ReadExactly(tag);
                Span<byte> nonce = stackalloc byte[12];
                header.AsSpan(28, NoncePrefixBytes).CopyTo(nonce);
                BinaryPrimitives.WriteInt32LittleEndian(nonce[8..], index);
                Span<byte> aad = stackalloc byte[HeaderBytes + 8];
                header.AsSpan().CopyTo(aad);
                BinaryPrimitives.WriteInt32LittleEndian(aad[HeaderBytes..], index);
                BinaryPrimitives.WriteInt32LittleEndian(aad[(HeaderBytes + 4)..], count);
                aes.Decrypt(nonce, cipher.AsSpan(0, count), tag, plain.AsSpan(0, count), aad);
                output.Write(plain, 0, count);
                remaining -= count;
                index = checked(index + 1);
            }
            if (input.Position != input.Length)
                throw new InvalidDataException("The encrypted backup has unexpected trailing data.");
            output.Flush(flushToDisk: true);
            }
            File.Move(temporary, destinationBak, overwrite: false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(cipher);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>For listing only. NEVER treat this as a verified recovery point.</summary>
    internal static DateTime ReadUnverifiedCreationUtc(string package)
    {
        using var input = File.OpenRead(package);
        byte[] header = new byte[HeaderBytes];
        input.ReadExactly(header);
        if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("Not an FMSC cloud backup.");
        return new DateTime(BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(36, 8)), DateTimeKind.Utc);
    }

    /// <summary>
    /// Call ONLY after successful Decrypt for this same package. Before GCM
    /// validation this header value is not authenticated recovery metadata.
    /// </summary>
    internal static DateTime ReadSqlHeaderLocalAfterVerification(string package)
    {
        using var input = File.OpenRead(package);
        byte[] header = new byte[HeaderBytes];
        input.ReadExactly(header);
        if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("Unsupported encrypted SQL recovery package.");
        return new DateTime(BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(52, 8)),
            DateTimeKind.Unspecified);
    }

    private static void CheckPassphrase(string passphrase)
    {
        if (string.IsNullOrWhiteSpace(passphrase) || passphrase.Length < 12)
            throw new ArgumentException("A cloud recovery passphrase of at least 12 characters is required.");
    }
}
