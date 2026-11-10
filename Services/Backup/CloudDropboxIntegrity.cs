using System.Security.Cryptography;
using System.Text.Json;

namespace FMSC_SMS.Services.Backup;

/// <summary>
/// Dropbox uses SHA-256 over the concatenated SHA-256 digests of 4-MiB
/// blocks, not ordinary whole-file SHA-256. This value comes independently
/// from Dropbox's FileMetadata.content_hash, never from our local filename.
/// </summary>
internal static class CloudDropboxIntegrity
{
    private const int BlockSize = 4 * 1024 * 1024;

    internal static string ComputeContentHash(string path)
    {
        using var input = File.OpenRead(path);
        using var combined = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] block = new byte[BlockSize];
        while (true)
        {
            int count = 0;
            while (count < block.Length)
            {
                int read = input.Read(block, count, block.Length - count);
                if (read == 0) break;
                count += read;
            }
            if (count == 0) break;
            combined.AppendData(SHA256.HashData(block.AsSpan(0, count)));
        }
        return Convert.ToHexString(combined.GetHashAndReset()).ToLowerInvariant();
    }

    internal static void RequireMatchingMetadata(JsonElement metadata, string name,
        long size, string expectedContentHash)
    {
        if (metadata.ValueKind != JsonValueKind.Object ||
            !metadata.TryGetProperty("id", out var id) ||
            string.IsNullOrWhiteSpace(id.GetString()) ||
            !metadata.TryGetProperty("name", out var remoteName) ||
            !string.Equals(remoteName.GetString(), name, StringComparison.Ordinal) ||
            !metadata.TryGetProperty("size", out var remoteSize) ||
            !remoteSize.TryGetInt64(out long confirmedSize) || confirmedSize != size ||
            !metadata.TryGetProperty("content_hash", out var remoteHash) ||
            !string.Equals(remoteHash.GetString(), expectedContentHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Dropbox did not independently confirm the complete encrypted backup content.");
        }
    }

    internal static bool IsPathNotFound(JsonElement result)
    {
        if (!result.TryGetProperty("error", out var error) ||
            !error.TryGetProperty(".tag", out var errorTag) ||
            errorTag.GetString() != "path" ||
            !error.TryGetProperty("path", out var path) ||
            !path.TryGetProperty(".tag", out var pathTag))
            return false;
        return pathTag.GetString() == "not_found";
    }
}
