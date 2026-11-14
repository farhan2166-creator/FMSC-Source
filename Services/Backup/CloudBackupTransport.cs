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
        CancellationToken token = default, Action<long, long?>? reportTransfer = null)
    {
        if (!File.Exists(encryptedPackage))
            throw new FileNotFoundException("Encrypted FMSC backup was not found.", encryptedPackage);
        string sha;
        using (var hashStream = File.OpenRead(encryptedPackage))
            sha = Convert.ToHexString(await SHA256.HashDataAsync(hashStream, token)).ToLowerInvariant();
        long length = new FileInfo(encryptedPackage).Length;
        string name = MakeName(sqlBackupCreatedUtc, sha);
        string access = await CloudBackupOAuth.GetAccessTokenAsync(provider, token);
        return await DropboxUploadAsync(encryptedPackage, name, sha, sqlBackupCreatedUtc, length, access, token, reportTransfer);
    }

    internal static async Task<IReadOnlyList<CloudRemoteBackup>> ListAsync(
        CloudProvider provider, CancellationToken token = default)
    {
        string access = await CloudBackupOAuth.GetAccessTokenAsync(provider, token);
        return await DropboxListAsync(access, token);
    }

    internal static async Task DownloadAsync(CloudProvider provider, string remoteId,
        string destination, CancellationToken token = default, Action<long, long?>? reportTransfer = null)
    {
        if (File.Exists(destination)) throw new IOException("Cloud download staging target already exists.");
        string access = await CloudBackupOAuth.GetAccessTokenAsync(provider, token);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://content.dropboxapi.com/2/files/download");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        request.Headers.TryAddWithoutValidation("Dropbox-API-Arg",
            JsonSerializer.Serialize(new { path = remoteId }));
        await DownloadToFileAsync(request, destination, token, reportTransfer: reportTransfer);
    }

    internal static async Task DownloadToFileAsync(HttpRequestMessage request, string destination,
        CancellationToken token, HttpClient? client = null, TimeSpan? inactivityTimeout = null,
        Action<long, long?>? reportTransfer = null)
    {
        if (File.Exists(destination)) throw new IOException("Cloud download staging target already exists.");
        string temp = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using var response = await (client ?? Http).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            RequireSuccess(response, "Cloud download");
            long? total = response.Content.Headers.ContentLength;
            long received = 0;
            reportTransfer?.Invoke(received, total);
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
                    received += count;
                    reportTransfer?.Invoke(received, total);
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
        using var response = await DropboxRpcAsync("files/delete_v2", access,
            new { path = remoteId }, token);
    }

    private static async Task<CloudRemoteBackup> DropboxUploadAsync(
        string path, string name, string sha, DateTime createdUtc, long length,
        string access, CancellationToken token, Action<long, long?>? reportTransfer)
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
                reportTransfer is null ? new StreamContent(input) : new ReportingUploadContent(input, length, reportTransfer));
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
            reportTransfer?.Invoke(0, length);
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
                reportTransfer?.Invoke(offset, length);
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

    private sealed class ReportingUploadContent(Stream input, long length, Action<long, long?> report) : HttpContent
    {
        protected override bool TryComputeLength(out long contentLength)
        {
            contentLength = length;
            return true;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context,
            CancellationToken cancellationToken)
        {
            input.Position = 0;
            long sent = 0;
            byte[] buffer = new byte[81920];
            report(sent, length);
            while (true)
            {
                int count = await input.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (count == 0) break;
                await stream.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                sent += count;
                report(sent, length);
            }
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
                if (ParseSize(entry) <= 0) continue;
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
        // Dropbox sizes are JSON numbers; older metadata may contain strings.
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
