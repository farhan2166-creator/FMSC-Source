using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FMSC_SMS.Core.Updates;

/// <summary>
/// Stable GitHub identities. An account rename changes only the resolved login,
/// never a machine's DPAPI-sealed SSH deploy key or the authorized repository.
/// SSH deploy keys are Git credentials, NOT GitHub REST credentials.
/// </summary>
internal static class GitHubRepositoryIdentity
{
    internal const long OwnerId = 316891609;
    internal const long SourceId = 1412760928;
    internal const long SandboxId = 1449162585;
    internal const long UpdatesId = 1445675367;
    internal const string UpdatesName = "FMSC-Updates";

    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    internal static async Task<string> ResolveOwnerLoginAsync(
        HttpClient? client = null, CancellationToken cancellationToken = default)
    {
        using var account = await GetJsonAsync(
            client ?? Client, $"https://api.github.com/user/{OwnerId}", null, cancellationToken);
        var root = account.RootElement;
        if (root.GetProperty("id").GetInt64() != OwnerId ||
            root.GetProperty("type").GetString() != "User")
            throw new InvalidDataException("GitHub owner account ID did not match the authorized identity.");
        string login = root.GetProperty("login").GetString() ?? "";
        ValidateLogin(login);
        return login;
    }

    internal static string UpdatesSshRemote(string verifiedLogin)
    {
        ValidateLogin(verifiedLogin);
        return $"git@github.com:{verifiedLogin}/{UpdatesName}.git";
    }

    internal static async Task<string> ResolveUpdatesSshRemoteAsync(
        HttpClient? client = null, CancellationToken cancellationToken = default)
    {
        // This public account-ID endpoint requires no token. The subsequent
        // Git clone still requires this machine's *existing* read-only deploy key,
        // StrictHostKeyChecking and the pinned github.com SSH host key.
        string owner = await ResolveOwnerLoginAsync(client, cancellationToken);
        return UpdatesSshRemote(owner);
    }

    internal static async Task<string> ResolveUpdatesHttpsRemoteAsync(
        string token, HttpClient? client = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("A private Updates metadata token is required for publication.");
        var http = client ?? Client;
        string login = await ResolveOwnerLoginAsync(http, cancellationToken);
        string name = $"{login}/{UpdatesName}";
        using var repository = await GetJsonAsync(http,
            $"https://api.github.com/repos/{name}", token, cancellationToken);
        var root = repository.RootElement;
        if (root.GetProperty("id").GetInt64() != UpdatesId ||
            root.GetProperty("owner").GetProperty("id").GetInt64() != OwnerId ||
            root.GetProperty("name").GetString() != UpdatesName ||
            root.GetProperty("full_name").GetString() != name ||
            !root.GetProperty("private").GetBoolean())
            throw new InvalidDataException("Updates repository identity, owner or private visibility changed.");
        return $"https://github.com/{name}.git";
    }

    private static void ValidateLogin(string login)
    {
        if (login.Length > 39 || !Regex.IsMatch(login,
                @"\A[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?\z",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            throw new InvalidDataException("GitHub returned an invalid account login.");
    }

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, string uri,
        string? token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("FMSC-School-Management/1");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        // Never accept a redirect to a legacy account URL or another API host.
        if (response.RequestMessage?.RequestUri?.AbsoluteUri != uri)
            throw new InvalidDataException("GitHub identity lookup was redirected.");
        if (response.Content.Headers.ContentLength is > 65536)
            throw new InvalidDataException("GitHub identity metadata exceeded its size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        int n;
        while ((n = await stream.ReadAsync(bytes, cancellationToken)) != 0)
        {
            if (buffer.Length + n > 65536)
                throw new InvalidDataException("GitHub identity metadata exceeded its size limit.");
            buffer.Write(bytes, 0, n);
        }
        buffer.Position = 0;
        return await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken);
    }
}
