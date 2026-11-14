using System.Security.Cryptography;
using System.Text;

namespace FMSC_SMS.Core;

internal static partial class CommunicationProviderCredentialStore
{
    internal sealed record Credential(
        int Format, string Channel, string ProviderCode, string AccountId,
        string SenderId, string ApiToken, string CallbackSigningSecret)
    {
        public string GraphVersion { get; init; } = "v23.0";
        // Diagnostics must never stringify tokens through the generated record formatter.
        public override string ToString() => Channel + "/" + ProviderCode + " (protected credentials)";
    }

    internal static string IdentityHash(Credential c) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(string.Join("\n",c.Channel,c.ProviderCode,c.AccountId,c.SenderId,
            c.ProviderCode == "MetaCloud" ? c.GraphVersion : ""))));

    internal static void Validate(Credential value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if(value.Channel is not ("Sms" or "WhatsApp")) throw new ArgumentException("Unknown communication channel.");
        if (value.Format != 1
            || string.IsNullOrWhiteSpace(value.ProviderCode)
            || value.ProviderCode.Length > 40
            || !value.ProviderCode.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
            || string.IsNullOrWhiteSpace(value.AccountId) || value.AccountId.Length > 128
            || string.IsNullOrWhiteSpace(value.SenderId) || value.SenderId.Length > 128
            || string.IsNullOrWhiteSpace(value.ApiToken) || value.ApiToken.Length is < 16 or > 4096
            || value.CallbackSigningSecret is null
            || (value.ProviderCode != "MetaCloud" && (string.IsNullOrWhiteSpace(value.CallbackSigningSecret)
                || value.CallbackSigningSecret.Length is < 16 or > 4096))
            || value.AccountId.ContainsAnyNewline() || value.SenderId.ContainsAnyNewline()
            || value.ApiToken.ContainsAnyNewline() || value.CallbackSigningSecret.ContainsAnyNewline())
            throw new ArgumentException("Provider credential metadata or format is invalid.");
    }

    private static bool ContainsAnyNewline(this string value) =>
        value.Contains('\r') || value.Contains('\n');
}
