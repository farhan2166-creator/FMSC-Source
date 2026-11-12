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
        CancellationToken token = default, string? driveFileId = null, Action<string>? rememberDriveFileId = null)
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
            ? await DriveUploadAsync(encryptedPackage, name, sha, sqlBackupCreatedUtc, length, access, token, driveFileId, rememberDriveFileId)
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
        using var request = provider == CloudProvider.GoogleDrive
            ? new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/drive/v3/files/" +
                Uri.EscapeDataString(remoteId) + "?alt=media")
            : new HttpRequestMessage(HttpMethod.Post, "https://content.dropboxapi.com/2/files/download");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        if (provider == CloudProvider.Dropbox)
            request.Headers.TryAddWithoutValidation("Dropbox-API-Arg",
                JsonSerializer.Serialize(new { path = remoteId }));
        await DownloadToFileAsync(request, destination, token);
    }

    internal static async Task DownloadToFileAsync(HttpRequestMessage request, string destination,
        CancellationToken token, HttpClient? client = null, TimeSpan? inactivityTimeout = null)
    {
        if (File.Exists(destination)) throw new IOException("Cloud download staging target already exists.");
        string temp = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using var response = await (client ?? Http).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            RequireSuccess(response, "Cloud download");
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await using var cloud = await response.Content.ReadAsStreamAsync(token);
                // HttpClient.Timeout ends when ResponseHeadersRead returns.
                // Give each streaming read/write its own progress deadline;
                // cancellation here is still before any SQL restore begins.
                byte[] buffer = new byte[81920];
                while (true)
                {
                    using var progress = CancellationTokenSource.CreateLinkedTokenSource(token);
                    progress.CancelAfter(inactivityTimeout ?? TimeSpan.FromMinutes(2));
                    int count;
                    try
                    {
                        count = await cloud.ReadAsync(buffer.AsMemory(), progress.Token);
                        if (count > 0) await output.WriteAsync(buffer.AsMemory(0, count), progress.Token);
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        throw new TimeoutException("Cloud download stopped making progress. The incomplete download was discarded; try again when the connection is available.");
                    }
                    if (count == 0) break;
                }
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

    internal static async Task<CloudRemoteBackup> DriveUploadAsync(
        string path, string name, string sha, DateTime createdUtc, long length,
        string access, CancellationToken token, string? reservedId = null,
        Action<string>? rememberId = null, HttpClient? client = null)
    {
        var http = client ?? Http;
        using var digestStream = File.OpenRead(path);
        string md5 = Convert.ToHexString(await MD5.HashDataAsync(digestStream, token)).ToLowerInvariant();
        if (string.IsNullOrEmpty(reservedId))
        {
            // Reconcile queues created before reserved IDs were introduced.
            var existing = await DriveListAsync(access, token, http);
            foreach (var item in existing.Where(x => x.Name == name && x.PackageSha256 == sha))
            {
                var found = await ReadDriveFileAsync(http, access, item.RemoteId, name, sha, createdUtc, length, md5, token);
                if (found.Backup is not null) return found.Backup;
            }
            using var reserve = new HttpRequestMessage(HttpMethod.Get,
                "https://www.googleapis.com/drive/v3/files/generateIds?count=1&space=drive&type=files");
            reserve.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            using var generated = await http.SendAsync(reserve, token);
            RequireSuccess(generated, "Google Drive backup ID reservation");
            using var ids = JsonDocument.Parse(await generated.Content.ReadAsStringAsync(token));
            reservedId = ids.RootElement.GetProperty("ids")[0].GetString();
            if (string.IsNullOrWhiteSpace(reservedId)) throw new IOException("Google Drive returned no reserved file ID.");
            // Journal before files.create. A lost receipt must reuse this ID.
            if (rememberId is null) throw new InvalidOperationException("Google Drive backup upload requires a durable ID journal.");
            rememberId(reservedId);
        }
        var completed = await ReadDriveFileAsync(http, access, reservedId, name, sha, createdUtc, length, md5, token);
        if (completed.Backup is not null) return completed.Backup;
        bool updateIncomplete = completed.Incomplete;
        var metadata = new Dictionary<string, object> {
            ["name"] = name, ["mimeType"] = "application/octet-stream",
            ["appProperties"] = new Dictionary<string,string> {
                ["fmscSqlBackupUtc"] = createdUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                ["fmscPackageSha256"] = sha
            }
        };
        if (!updateIncomplete) metadata["id"] = reservedId;
        using var begin = new HttpRequestMessage(updateIncomplete ? HttpMethod.Patch : HttpMethod.Post,
            "https://www.googleapis.com/upload/drive/v3/files" +
            (updateIncomplete ? "/" + Uri.EscapeDataString(reservedId) : "") +
            "?uploadType=resumable&fields=id,name,size,md5Checksum,createdTime");
        begin.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        begin.Headers.Add("X-Upload-Content-Type", "application/octet-stream");
        begin.Headers.Add("X-Upload-Content-Length", length.ToString(CultureInfo.InvariantCulture));
        begin.Content = new StringContent(JsonSerializer.Serialize(metadata), Encoding.UTF8, "application/json");
        using var started = await http.SendAsync(begin, token);
        if (started.StatusCode == HttpStatusCode.Conflict)
        {
            var current = await ReadDriveFileAsync(http, access, reservedId, name, sha, createdUtc, length, md5, token);
            if (current.Backup is not null) return current.Backup;
            if (current.Incomplete && !updateIncomplete)
                return await DriveUploadAsync(path,name,sha,createdUtc,length,access,token,reservedId,rememberId,http);
            throw new IOException("Google Drive backup ID exists but cannot be verified; no second copy was created.");
        }
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
            using var response = await http.SendAsync(put, token);
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
            return RequireDriveFile(receipt.RootElement, reservedId, name, sha, createdUtc, length, md5);
        }
    }

    private sealed record DriveLookup(CloudRemoteBackup? Backup, bool Incomplete);

    private static async Task<DriveLookup> ReadDriveFileAsync(HttpClient http, string access,
        string id, string name, string sha, DateTime createdUtc, long length, string md5, CancellationToken token)
    {
        using var query = new HttpRequestMessage(HttpMethod.Get,
            "https://www.googleapis.com/drive/v3/files/" + Uri.EscapeDataString(id) +
            "?fields=id,name,size,md5Checksum,createdTime,appProperties");
        query.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        using var response = await http.SendAsync(query, token);
        if (response.StatusCode == HttpStatusCode.NotFound) return new(null,false);
        RequireSuccess(response, "Google Drive backup reconciliation");
        using var metadata = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var file = metadata.RootElement;
        // A failed upload may leave its reserved file as a zero-byte placeholder.
        // Update only that exact, app-owned placeholder, never a different file.
        if (GetString(file,"id") == id && GetString(file,"name") == name &&
            ParseSize(file) == 0 && GetString(file,"md5Checksum").Length == 0 &&
            file.TryGetProperty("appProperties",out var properties) &&
            GetString(properties,"fmscPackageSha256") == sha)
            return new(null,true);
        return new(RequireDriveFile(file, id, name, sha, createdUtc, length, md5),false);
    }

    private static CloudRemoteBackup RequireDriveFile(JsonElement metadata, string id, string name,
        string sha, DateTime createdUtc, long length, string md5)
    {
        if (GetString(metadata,"id") != id || GetString(metadata,"name") != name ||
            ParseSize(metadata) != length ||
            !string.Equals(GetString(metadata,"md5Checksum"),md5,StringComparison.OrdinalIgnoreCase))
            throw new IOException("Google Drive backup identity, size or checksum differs; the local copy remains queued.");
        return new CloudRemoteBackup(CloudProvider.GoogleDrive,id,name,sha,createdUtc,
            ParseUtc(GetString(metadata,"createdTime")),length);
    }

    private static async Task<IReadOnlyList<CloudRemoteBackup>> DriveListAsync(
        string access, CancellationToken token, HttpClient? client = null)
    {
        var http = client ?? Http;
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
            using var response = await http.SendAsync(request, token);
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
    private static long ParseSize(JsonElement element)
    {
        if (!element.TryGetProperty("size", out var size)) return 0;
        // Drive encodes int64 sizes as JSON strings; Dropbox uses numbers.
        return size.ValueKind == JsonValueKind.Number && size.TryGetInt64(out var n) ? n
            : size.ValueKind == JsonValueKind.String && long.TryParse(size.GetString(),
                NumberStyles.None, CultureInfo.InvariantCulture, out n) ? n : 0;
    }
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
