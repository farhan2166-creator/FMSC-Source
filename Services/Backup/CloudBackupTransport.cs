using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FMSC_SMS.Services.Backup;

/// <summary>Cloud objects are encrypted packages; never plaintext SQL backups.</summary>
internal sealed record CloudRemoteBackup(
    CloudProvider Provider, string RemoteId, string Name, string PackageSha256,
    DateTime SqlBackupCreatedUtc, DateTime UploadedUtc, long Size);

internal static class CloudBackupTransport
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };
    private const int ChunkSize = 8 * 1024 * 1024;

    internal static async Task<CloudRemoteBackup> UploadAsync(
        CloudProvider provider, string encryptedPackage, DateTime sqlBackupCreatedUtc,
        CancellationToken token = default)
    {
        if (!File.Exists(encryptedPackage))
            throw new FileNotFoundException("Encrypted FMSC backup was not found.", encryptedPackage);
        string sha;
        using (var hashStream = File.OpenRead(encryptedPackage))
            sha = Convert.ToHexString(await SHA256.HashDataAsync(hashStream, token)).ToLowerInvariant();
        long length = new FileInfo(encryptedPackage).Length;
        string name = MakeName(sqlBackupCreatedUtc, sha);
        string access = await CloudBackupOAuth.GetAccessTokenAsync(provider, token);
        return provider == CloudProvider.GoogleDrive
            ? await DriveUploadAsync(encryptedPackage, name, sha, sqlBackupCreatedUtc, length, access, token)
            : await DropboxUploadAsync(encryptedPackage, name, sha, sqlBackupCreatedUtc, length, access, token);
    }

    internal static async Task<IReadOnlyList<CloudRemoteBackup>> ListAsync(
        CloudProvider provider, CancellationToken token = default)
    {
        string access = await CloudBackupOAuth.GetAccessTokenAsync(provider, token);
        return provider == CloudProvider.GoogleDrive
            ? await DriveListAsync(access, token)
            : await DropboxListAsync(access, token);
    }

    internal static async Task DownloadAsync(CloudProvider provider, string remoteId,
        string destination, CancellationToken token = default)
    {
        if (File.Exists(destination)) throw new IOException("Cloud download staging target already exists.");
        string access = await CloudBackupOAuth.GetAccessTokenAsync(provider, token);
        string temp = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using var request = provider == CloudProvider.GoogleDrive
                ? new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/drive/v3/files/" +
                    Uri.EscapeDataString(remoteId) + "?alt=media")
                : new HttpRequestMessage(HttpMethod.Post, "https://content.dropboxapi.com/2/files/download");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            if (provider == CloudProvider.Dropbox)
                request.Headers.TryAddWithoutValidation("Dropbox-API-Arg",
                    JsonSerializer.Serialize(new { path = remoteId }));
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            RequireSuccess(response, "Cloud download");
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await using var cloud = await response.Content.ReadAsStreamAsync(token);
                await cloud.CopyToAsync(output, token);
                await output.FlushAsync(token);
            }
            if (new FileInfo(temp).Length == 0) throw new InvalidDataException("Cloud backup download was empty.");
            File.Move(temp, destination, overwrite: false);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    internal static async Task DeleteAsync(CloudProvider provider, string remoteId,
        CancellationToken token = default)
    {
        string access = await CloudBackupOAuth.GetAccessTokenAsync(provider, token);
        if (provider == CloudProvider.GoogleDrive)
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete,
                "https://www.googleapis.com/drive/v3/files/" + Uri.EscapeDataString(remoteId));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            using var response = await Http.SendAsync(request, token);
            RequireSuccess(response, "Cloud backup retention");
        }
        else
        {
            using var response = await DropboxRpcAsync("files/delete_v2", access,
                new { path = remoteId }, token);
        }
    }

    private static async Task<CloudRemoteBackup> DriveUploadAsync(
        string path, string name, string sha, DateTime createdUtc, long length,
        string access, CancellationToken token)
    {
        var metadata = new {
            name, mimeType = "application/octet-stream",
            appProperties = new Dictionary<string,string> {
                ["fmscSqlBackupUtc"] = createdUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                ["fmscPackageSha256"] = sha
            }
        };
        using var begin = new HttpRequestMessage(HttpMethod.Post,
            "https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&fields=id,name,size,md5Checksum,createdTime");
        begin.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        begin.Headers.Add("X-Upload-Content-Type", "application/octet-stream");
        begin.Headers.Add("X-Upload-Content-Length", length.ToString(CultureInfo.InvariantCulture));
        begin.Content = new StringContent(JsonSerializer.Serialize(metadata), Encoding.UTF8, "application/json");
        using var started = await Http.SendAsync(begin, token);
        RequireSuccess(started, "Google Drive upload initiation");
        Uri session = started.Headers.Location
            ?? throw new InvalidDataException("Google Drive did not provide an upload session.");
        if (session.Scheme != Uri.UriSchemeHttps ||
            !(session.Host.Equals("www.googleapis.com", StringComparison.OrdinalIgnoreCase)
              || session.Host.EndsWith(".googleapis.com", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Unexpected Google Drive upload endpoint.");
        using var input = File.OpenRead(path);
        long offset = 0;
        int stalledResponses = 0;
        JsonDocument? receipt = null;
        byte[] buffer = new byte[ChunkSize];
        while (offset < length)
        {
            // A 308 response acknowledges the server's actual committed range,
            // not necessarily the bytes we sent in the last request. Always
            // resume from THAT position rather than skipping partial chunks.
            input.Position = offset;
            int count = (int)Math.Min(buffer.Length, length - offset);
            await input.ReadExactlyAsync(buffer.AsMemory(0, count), token);
            using var put = new HttpRequestMessage(HttpMethod.Put, session);
            put.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            put.Content = new ByteArrayContent(buffer, 0, count);
            put.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            put.Content.Headers.TryAddWithoutValidation("Content-Range",
                $"bytes {offset}-{offset + count - 1}/{length}");
            using var response = await Http.SendAsync(put, token);
            if ((int)response.StatusCode == 308)
            {
                long acknowledged = 0; // No Range header means NO bytes committed.
                if (response.Headers.TryGetValues("Range", out var values))
                {
                    string range = values.FirstOrDefault() ?? "";
                    if (!range.StartsWith("bytes=0-", StringComparison.Ordinal) ||
                        !long.TryParse(range["bytes=0-".Length..],
                            NumberStyles.None, CultureInfo.InvariantCulture, out long last))
                        throw new IOException("Google Drive returned an invalid upload range.");
                    acknowledged = checked(last + 1);
                }
                if (acknowledged < offset || acknowledged > offset + count ||
                    acknowledged > length)
                    throw new IOException("Google Drive acknowledged bytes outside the requested range.");
                if (acknowledged == offset)
                {
                    if (++stalledResponses >= 3)
                        throw new IOException("Google Drive upload stopped making progress; the full encrypted copy remains locally queued.");
                }
                else stalledResponses = 0;
                offset = acknowledged;
                continue;
            }
            RequireSuccess(response, "Google Drive upload");
            if (offset + count != length)
                throw new IOException("Google Drive signalled completion before receiving the entire encrypted file.");
            receipt = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            offset += count;
        }
        if (receipt is null) throw new IOException("Google Drive upload did not finish.");
        using (receipt)
        {
            string id = receipt.RootElement.GetProperty("id").GetString()
                ?? throw new InvalidDataException("Drive returned no backup file ID.");
            if (!receipt.RootElement.TryGetProperty("size", out var sizeValue) ||
                !long.TryParse(sizeValue.GetString(), out long remoteSize) || remoteSize != length)
                throw new IOException("Google Drive backup size does not match the encrypted package.");
            if (receipt.RootElement.TryGetProperty("md5Checksum", out var checksum))
            {
                using var inputMd5 = File.OpenRead(path);
                string localMd5 = Convert.ToHexString(MD5.HashData(inputMd5)).ToLowerInvariant();
                if (!string.Equals(localMd5, checksum.GetString(), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Google Drive uploaded package checksum mismatch.");
            }
            return new CloudRemoteBackup(CloudProvider.GoogleDrive, id, name, sha, createdUtc,
                DateTime.UtcNow, remoteSize);
        }
    }

    private static async Task<IReadOnlyList<CloudRemoteBackup>> DriveListAsync(
        string access, CancellationToken token)
    {
        var items = new List<CloudRemoteBackup>();
        string? pageToken = null;
        do
        {
            string url = "https://www.googleapis.com/drive/v3/files?pageSize=1000" +
                "&q=" + Uri.EscapeDataString("name contains 'FMSC_' and trashed = false") +
                "&fields=" + Uri.EscapeDataString("nextPageToken,files(id,name,size,createdTime,appProperties)");
            if (!string.IsNullOrEmpty(pageToken)) url += "&pageToken=" + Uri.EscapeDataString(pageToken);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            using var response = await Http.SendAsync(request, token);
            RequireSuccess(response, "Google Drive backup list");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            foreach (var entry in json.RootElement.GetProperty("files").EnumerateArray())
            {
                string name = GetString(entry, "name"), id = GetString(entry, "id");
                if (!ParseName(name, out DateTime created, out string sha)) continue;
                if (entry.TryGetProperty("appProperties", out var props))
                {
                    // Metadata is advisory until a downloaded .bak header
                    // confirms the time and GCM validates the package.
                    if (GetString(props, "fmscPackageSha256") != sha) continue;
                    if (DateTime.TryParse(GetString(props, "fmscSqlBackupUtc"), CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var registered) &&
                        (registered - created).Duration() > TimeSpan.FromSeconds(1))
                        continue;
                }
                items.Add(new CloudRemoteBackup(CloudProvider.GoogleDrive, id, name, sha, created,
                    ParseUtc(GetString(entry, "createdTime")), ParseSize(entry)));
            }
            pageToken = GetString(json.RootElement, "nextPageToken");
        } while (!string.IsNullOrEmpty(pageToken));
        return items;
    }

    private static async Task<CloudRemoteBackup> DropboxUploadAsync(
        string path, string name, string sha, DateTime createdUtc, long length,
        string access, CancellationToken token)
    {
        string destination = "/" + name;
        // Dropbox's content_hash is independently computed by the provider.
        // Reconcile a previous successful commit whose HTTP response was lost
        // before attempting add/autorename=false again.
        string contentHash = CloudDropboxIntegrity.ComputeContentHash(path);
        var existing = await ReconcileDropboxUploadAsync(
            access, destination, name, sha, createdUtc, length, contentHash, token);
        if (existing is not null) return existing;
        JsonDocument? metadata = null;
        if (length <= 140 * 1024 * 1024)
        {
            using var input = File.OpenRead(path);
            using var request = DropboxContentRequest("files/upload", access,
                new { path = destination, mode = "add", autorename = false, mute = true },
                new StreamContent(input));
            using var response = await Http.SendAsync(request, token);
            RequireSuccess(response, "Dropbox upload");
            metadata = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        }
        else
        {
            using var startRequest = DropboxContentRequest("files/upload_session/start", access,
                new { close = false }, new ByteArrayContent(Array.Empty<byte>()));
            using var start = await Http.SendAsync(startRequest, token);
            RequireSuccess(start, "Dropbox upload session");
            using var started = JsonDocument.Parse(await start.Content.ReadAsStringAsync(token));
            string session = GetString(started.RootElement, "session_id");
            if (session.Length < 10) throw new InvalidDataException("Dropbox returned an invalid upload session.");
            await using var input = File.OpenRead(path);
            byte[] buffer = new byte[ChunkSize];
            long offset = 0;
            while (offset < length)
            {
                int count = (int)Math.Min(buffer.Length, length - offset);
                await input.ReadExactlyAsync(buffer.AsMemory(0, count), token);
                bool final = offset + count == length;
                // Dropbox requires the final bytes in append_v2 (close=true).
                // upload_session/finish is the COMMIT and has an empty body.
                // Its documented API must not receive file bytes.
                using var request = DropboxContentRequest(
                    "files/upload_session/append_v2", access,
                    new { cursor = new { session_id = session, offset }, close = final },
                    new ByteArrayContent(buffer, 0, count));
                using var response = await Http.SendAsync(request, token);
                RequireSuccess(response, "Dropbox chunk upload");
                offset += count;
            }
            if (offset != length) throw new IOException("Dropbox upload did not transmit the complete package.");
            using var finish = DropboxContentRequest("files/upload_session/finish", access,
                new { cursor = new { session_id = session, offset = length },
                    commit = new { path = destination, mode = "add", autorename = false, mute = true } },
                new ByteArrayContent(Array.Empty<byte>()));
            using var completed = await Http.SendAsync(finish, token);
            RequireSuccess(completed, "Dropbox upload commit");
            metadata = JsonDocument.Parse(await completed.Content.ReadAsStringAsync(token));
        }
        if (metadata is null) throw new IOException("Dropbox upload did not finish.");
        using (metadata)
        {
            string id = GetString(metadata.RootElement, "id");
            CloudDropboxIntegrity.RequireMatchingMetadata(
                metadata.RootElement, name, length, contentHash);
            return new CloudRemoteBackup(CloudProvider.Dropbox, id, name, sha, createdUtc,
                ParseUtc(GetString(metadata.RootElement, "server_modified")), length);
        }
    }

    private static async Task<CloudRemoteBackup?> ReconcileDropboxUploadAsync(
        string access, string destination, string name, string packageSha256,
        DateTime createdUtc, long length, string localContentHash,
        CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "https://api.dropboxapi.com/2/files/get_metadata");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        request.Content = new StringContent(JsonSerializer.Serialize(new { path = destination }),
            Encoding.UTF8, "application/json");
        using var response = await Http.SendAsync(request, token);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            if (CloudDropboxIntegrity.IsPathNotFound(error.RootElement))
                return null;
        }
        RequireSuccess(response, "Dropbox backup lookup");
        using var metadata = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        // If another file occupies this name, refuse rather than overwrite it
        // or incorrectly marking our queued ciphertext as delivered.
        CloudDropboxIntegrity.RequireMatchingMetadata(
            metadata.RootElement, name, length, localContentHash);
        return new CloudRemoteBackup(CloudProvider.Dropbox,
            GetString(metadata.RootElement, "id"), name, packageSha256, createdUtc,
            ParseUtc(GetString(metadata.RootElement, "server_modified")), length);
    }

    private static async Task<IReadOnlyList<CloudRemoteBackup>> DropboxListAsync(
        string access, CancellationToken token)
    {
        var items = new List<CloudRemoteBackup>();
        bool more;
        string cursor = "";
        do
        {
            string action = cursor.Length == 0 ? "files/list_folder" : "files/list_folder/continue";
            object arg = cursor.Length == 0
                ? new { path = "", recursive = false, limit = 1000 }
                : new { cursor };
            using var response = await DropboxRpcAsync(action, access, arg, token);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            foreach (var entry in json.RootElement.GetProperty("entries").EnumerateArray())
            {
                if (GetString(entry, ".tag") != "file") continue;
                string name = GetString(entry, "name"), id = GetString(entry, "id");
                if (!ParseName(name, out var created, out var sha)) continue;
                items.Add(new CloudRemoteBackup(CloudProvider.Dropbox, id, name, sha, created,
                    ParseUtc(GetString(entry, "server_modified")), ParseSize(entry)));
            }
            more = json.RootElement.TryGetProperty("has_more", out var hasMore) && hasMore.GetBoolean();
            cursor = GetString(json.RootElement, "cursor");
        } while (more && cursor.Length != 0);
        return items;
    }

    private static HttpRequestMessage DropboxContentRequest(string action, string token,
        object arg, HttpContent content)
    {
        var request = new HttpRequestMessage(HttpMethod.Post,
            "https://content.dropboxapi.com/2/" + action);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("Dropbox-API-Arg", JsonSerializer.Serialize(arg));
        request.Content = content;
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return request;
    }
    private static async Task<HttpResponseMessage> DropboxRpcAsync(string action, string token,
        object arg, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.dropboxapi.com/2/" + action);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(JsonSerializer.Serialize(arg), Encoding.UTF8, "application/json");
        HttpResponseMessage result = await Http.SendAsync(request, cancellationToken);
        if (!result.IsSuccessStatusCode)
        {
            result.Dispose();
            throw new HttpRequestException("Dropbox " + action + " failed. Check connection, quota or account permissions.");
        }
        return result;
    }
    private static string MakeName(DateTime time, string hash) =>
        "FMSC_" + time.ToUniversalTime().ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture) +
        "_" + hash + ".fmscbackup";
    private static bool ParseName(string name, out DateTime utc, out string sha)
    {
        utc = default; sha = "";
        if (!name.StartsWith("FMSC_", StringComparison.Ordinal) ||
            !name.EndsWith(".fmscbackup", StringComparison.OrdinalIgnoreCase)) return false;
        string[] parts = name[..^".fmscbackup".Length].Split('_');
        if (parts.Length != 3 || parts[2].Length != 64 || !parts[2].All(Uri.IsHexDigit)) return false;
        if (!DateTime.TryParseExact(parts[1], "yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc)) return false;
        sha = parts[2].ToLowerInvariant();
        return true;
    }
    private static string GetString(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(key, out var child) && child.ValueKind == JsonValueKind.String
            ? child.GetString() ?? "" : "";
    private static long ParseSize(JsonElement element) =>
        element.TryGetProperty("size", out var size) && size.TryGetInt64(out var n) ? n : 0;
    private static DateTime ParseUtc(string value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var result)
            ? result : DateTime.MinValue;
    private static void RequireSuccess(HttpResponseMessage response, string action)
    {
        if (response.IsSuccessStatusCode) return;
        throw new HttpRequestException(action + " failed (HTTP " + (int)response.StatusCode +
            "). School data is unchanged; verify account authorization, network and quota.");
    }
}
