using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using FMSC_SMS.Core;

namespace FMSC_SMS.Services.Backup;

internal enum CloudProvider { GoogleDrive, Dropbox }

/// <summary>
/// OAuth registration is publisher-controlled. Google accepts only installed-app
/// metadata; confidential web client credentials are never supported.
/// Connecting a school account is impossible until both provider applications
/// are registered by the FMSC publisher.
/// </summary>
internal sealed class CloudProviderApplicationIds
{
    public string GoogleClientId { get; set; } = "";
    public string DropboxAppKey { get; set; } = "";
    internal static CloudProviderApplicationIds Load()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "CloudBackupProviders.json");
        if (!File.Exists(path)) return new CloudProviderApplicationIds();
        var ids = JsonSerializer.Deserialize<CloudProviderApplicationIds>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Cloud provider application registration is invalid.");
        if (ids.GoogleClientId.Length > 250 || ids.DropboxAppKey.Length > 150)
            throw new InvalidDataException("Cloud provider application identifier is too long.");
        return ids;
    }
    internal string? GetDesktopClientSecret(CloudProvider provider)
    {
        if (provider != CloudProvider.GoogleDrive) return null;
        string path = Path.Combine(AppContext.BaseDirectory, "CloudBackupGoogleDesktop.json");
        if (!File.Exists(path))
            throw new InvalidOperationException("Google Drive requires the publisher's Google Desktop app registration (CloudBackupGoogleDesktop.json). No sign-in was started. Web application credentials are not supported.");
        if (new FileInfo(path).Length > 16 * 1024)
            throw new InvalidDataException("Google Desktop registration is too large.");
        return GoogleDesktopRegistration.Parse(File.ReadAllText(path), GoogleClientId.Trim()).ClientSecret;
    }
    internal string Get(CloudProvider provider) => provider switch
    {
        CloudProvider.GoogleDrive => GoogleClientId.Trim(),
        CloudProvider.Dropbox => DropboxAppKey.Trim(),
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };
}

internal static class CloudBackupOAuth
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };
    private static readonly byte[] Entropy = SHA256.HashData(Encoding.UTF8.GetBytes("FMSC/cloud-backups/oauth/v1"));
    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FMSC", "CloudBackups");
    private sealed record Credential(int Version, string Provider, string ClientId, string RefreshToken);
    private sealed record TokenReply(string AccessToken, string? RefreshToken);

    internal static bool IsConnected(CloudProvider provider) => File.Exists(TokenFile(provider));

    internal static async Task ConnectAsync(CloudProvider provider, CancellationToken cancellationToken = default)
    {
        RequireAdministrator();
        await ConnectCoreAsync(provider, cancellationToken);
    }

    /// <summary>
    /// Renew an existing disconnected or expired provider. Retain the old
    /// credential until the new PKCE browser flow succeeds and writes atomically.
    /// </summary>
    internal static async Task ReconnectAsync(CloudProvider provider,
        CancellationToken cancellationToken = default)
    {
        RequireAdministrator();
        await ConnectCoreAsync(provider, cancellationToken);
    }

    internal static async Task CheckConnectionAsync(CloudProvider provider,
        CancellationToken cancellationToken = default)
    {
        _ = await GetAccessTokenAsync(provider, cancellationToken);
    }

    /// <summary>Cloud connection before normal school login is possible on a
    /// replacement computer. Uses the stricter online-approved Windows gate,
    /// and must not work when any existing FMSC_DB could be overwritten.</summary>
    internal static async Task ConnectForReplacementRecoveryAsync(
        CloudProvider provider, CancellationToken cancellationToken = default)
    {
        ReplacementSchoolRecovery.RequireAbsentOrVirginSchool();
        await ReplacementSchoolRecovery.CheckComputerAuthorizationAsync(cancellationToken);
        ReplacementSchoolRecovery.RequireAbsentOrVirginSchool();
        await ConnectCoreAsync(provider, cancellationToken);
    }

    private static async Task ConnectCoreAsync(CloudProvider provider, CancellationToken cancellationToken)
    {
        var registration = CloudProviderApplicationIds.Load();
        string clientId = registration.Get(provider);
        if (string.IsNullOrWhiteSpace(clientId))
            throw new InvalidOperationException(provider + " cannot be connected until the FMSC publisher registers its OAuth application.");
        string? desktopClientSecret = registration.GetDesktopClientSecret(provider);
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string state = Base64Url(RandomNumberGenerator.GetBytes(24));
        using var listener = new CloudOAuthLoopback(
            provider == CloudProvider.Dropbox ? 41847 : 0);
        string callback = listener.CallbackUrl;
        string authorization = provider == CloudProvider.GoogleDrive
            ? "https://accounts.google.com/o/oauth2/v2/auth?" + Query(new Dictionary<string,string> {
                ["client_id"] = clientId, ["redirect_uri"] = callback, ["response_type"] = "code",
                ["scope"] = "https://www.googleapis.com/auth/drive.file", ["access_type"] = "offline",
                ["prompt"] = "consent", ["state"] = state, ["code_challenge"] = challenge,
                ["code_challenge_method"] = "S256"
              })
            : "https://www.dropbox.com/oauth2/authorize?" + Query(new Dictionary<string,string> {
                ["client_id"] = clientId, ["redirect_uri"] = callback, ["response_type"] = "code",
                ["token_access_type"] = "offline", ["scope"] = "files.content.read files.content.write files.metadata.read",
                ["state"] = state, ["code_challenge"] = challenge, ["code_challenge_method"] = "S256"
              });
        Process.Start(new ProcessStartInfo(authorization) { UseShellExecute = true });
        string code;
        try { code = await listener.ReceiveCodeAsync(state, cancellationToken); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Cloud sign-in was cancelled or timed out.");
        }
        string tokenUri = provider == CloudProvider.GoogleDrive
            ? "https://oauth2.googleapis.com/token" : "https://api.dropboxapi.com/oauth2/token";
        var fields = GoogleDesktopRegistration.TokenFields(clientId, desktopClientSecret, "authorization_code");
        fields["code"] = code;
        fields["code_verifier"] = verifier;
        fields["redirect_uri"] = callback;
        using var payload = new FormUrlEncodedContent(fields);
        using var exchange = await Http.PostAsync(tokenUri, payload, cancellationToken);
        await RequireSuccess(exchange, "Account authorization", cancellationToken);
        using var json = JsonDocument.Parse(await exchange.Content.ReadAsStringAsync(cancellationToken));
        string refresh = json.RootElement.TryGetProperty("refresh_token", out var refreshValue)
            ? refreshValue.GetString() ?? "" : "";
        if (refresh.Length < 10) throw new InvalidDataException("Provider did not grant an offline refresh token; connection was not saved.");
        cancellationToken.ThrowIfCancellationRequested();
        SaveCredential(provider, new Credential(1, provider.ToString(), clientId, refresh));
    }

    internal static async Task<string> GetAccessTokenAsync(CloudProvider provider, CancellationToken cancellationToken = default)
    {
        var credential = ReadCredential(provider);
        var registration = CloudProviderApplicationIds.Load();
        string registeredId = registration.Get(provider);
        if (!string.Equals(credential.ClientId, registeredId, StringComparison.Ordinal))
            throw new InvalidOperationException("Cloud provider registration changed. Reconnect this account.");
        string tokenUri = provider == CloudProvider.GoogleDrive
            ? "https://oauth2.googleapis.com/token" : "https://api.dropboxapi.com/oauth2/token";
        var fields = GoogleDesktopRegistration.TokenFields(credential.ClientId,
            registration.GetDesktopClientSecret(provider), "refresh_token");
        fields["refresh_token"] = credential.RefreshToken;
        using var form = new FormUrlEncodedContent(fields);
        using var response = await Http.PostAsync(tokenUri, form, cancellationToken);
        await RequireSuccess(response, "Cloud account refresh", cancellationToken);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var token = json.RootElement.GetProperty("access_token").GetString();
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidDataException("Cloud provider returned no access token.");
        return token;
    }

    internal static async Task DisconnectAsync(CloudProvider provider, CancellationToken cancellationToken = default)
    {
        RequireAdministrator();
        if (!IsConnected(provider)) return;
        try
        {
            string token = await GetAccessTokenAsync(provider, cancellationToken);
            if (provider == CloudProvider.GoogleDrive)
            {
                using var form = new FormUrlEncodedContent(new Dictionary<string,string> { ["token"] = token });
                using var result = await Http.PostAsync("https://oauth2.googleapis.com/revoke", form, cancellationToken);
                // A revoked/expired token is already no longer useful.
            }
            else
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.dropboxapi.com/2/auth/token/revoke");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var result = await Http.SendAsync(request, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            AppExceptionHandler.LogNonFatal(ex, "Cloud token revocation failed; local credential is being disconnected");
        }
        finally
        {
            // Local disconnect is always honored; remote revoke errors are
            // logged and the user should revoke FMSC from account security.
            File.Delete(TokenFile(provider));
        }
    }

    private static Credential ReadCredential(CloudProvider provider)
    {
        byte[] cipher = File.ReadAllBytes(TokenFile(provider));
        if (cipher.Length is < 32 or > 16 * 1024) throw new InvalidDataException("Protected cloud account credential is invalid.");
        byte[] clear = Array.Empty<byte>();
        try
        {
            clear = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
            var item = JsonSerializer.Deserialize<Credential>(clear)
                ?? throw new InvalidDataException("Cloud account credential is missing.");
            if (item.Version != 1 || item.Provider != provider.ToString() ||
                item.ClientId.Length < 3 || item.RefreshToken.Length < 10)
                throw new InvalidDataException("Cloud account credential metadata is invalid.");
            return item;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(cipher);
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    private static void SaveCredential(CloudProvider provider, Credential credential)
    {
        EnsurePrivateRoot();
        byte[] clear = JsonSerializer.SerializeToUtf8Bytes(credential), cipher = Array.Empty<byte>();
        string target = TokenFile(provider), temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            cipher = ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser);
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(cipher);
                output.Flush(flushToDisk: true);
            }
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
            CryptographicOperations.ZeroMemory(cipher);
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static void EnsurePrivateRoot()
    {
        Directory.CreateDirectory(Root);
        var user = WindowsIdentity.GetCurrent().User
            ?? throw new UnauthorizedAccessException("Cannot determine cloud credential owner.");
        var acl = new DirectorySecurity();
        acl.SetOwner(user);
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        acl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(Root).SetAccessControl(acl);
    }
    private static string TokenFile(CloudProvider provider) =>
        Path.Combine(Root, "oauth-" + provider.ToString().ToLowerInvariant() + ".v1.dpapi");
    private static void RequireAdministrator()
    {
        if (!CurrentUser.HasPermission("Backup.Restore"))
            throw new UnauthorizedAccessException("Only an authorized recovery administrator may connect a cloud backup account.");
    }
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Query(Dictionary<string,string> pairs) =>
        string.Join("&", pairs.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
    private static async Task RequireSuccess(HttpResponseMessage response, string operation, CancellationToken token)
    {
        if (response.IsSuccessStatusCode) return;
        string body = await response.Content.ReadAsStringAsync(token);
        // Never log the raw OAuth response; token and student information may
        // be embedded in provider error payloads or redirect URLs.
        throw new HttpRequestException(operation + " failed (HTTP " + (int)response.StatusCode + "). Reconnect or check provider settings.");
    }
}
