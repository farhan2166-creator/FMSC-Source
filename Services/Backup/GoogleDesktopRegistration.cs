using System.Text.Json;

namespace FMSC_SMS.Services.Backup;

// Google Desktop clients require their native client metadata in token requests.
// This is not a confidential web-app secret and never replaces PKCE or user consent.
internal sealed class GoogleDesktopRegistration(string clientId, string clientSecret)
{
    internal string ClientId { get; } = clientId;
    internal string ClientSecret { get; } = clientSecret;
    internal static GoogleDesktopRegistration Parse(string json, string expectedClientId)
    {
        if (json.Length > 16 * 1024) throw new InvalidDataException("Google Desktop registration is too large.");
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("web", out _) || !root.TryGetProperty("installed", out var installed))
                throw new InvalidDataException("Use the Google Desktop app OAuth JSON, never a Web application credential.");
            string id = installed.GetProperty("client_id").GetString() ?? "";
            string secret = installed.GetProperty("client_secret").GetString() ?? "";
            if (!string.Equals(id, expectedClientId, StringComparison.Ordinal) || id.Length is < 3 or > 250
                || string.IsNullOrWhiteSpace(secret) || secret.Length > 250)
                throw new InvalidDataException("Google Desktop client metadata is missing or does not match the registered client ID.");
            return new(id, secret);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // Do not include credential JSON or provider responses in diagnostics.
            throw new InvalidDataException("Google Desktop registration is invalid. Obtain the Desktop app JSON from the publisher.");
        }
    }

    internal static Dictionary<string,string> TokenFields(string clientId, string? desktopClientSecret,
        string grantType)
    {
        var fields = new Dictionary<string,string> { ["client_id"] = clientId, ["grant_type"] = grantType };
        if (desktopClientSecret is not null) fields["client_secret"] = desktopClientSecret;
        return fields;
    }
}
