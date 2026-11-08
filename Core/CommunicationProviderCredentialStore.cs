using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace FMSC_SMS.Core;

/// <summary>
/// Local provider secrets are Windows-user-bound DPAPI data, never SQL data,
/// project configuration, logs or application binaries. This vault does not
/// enable a provider or send a message. Adapters must validate live SQL channel
/// state and the selected provider immediately before using a loaded secret.
/// </summary>
internal static class CommunicationProviderCredentialStore
{
    private const int FormatVersion = 1;
    private static readonly byte[] Entropy = SHA256.HashData(
        Encoding.UTF8.GetBytes("FMSC/Communication/provider-credential/v1"));

    internal static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FMSC", "Communication");

    internal sealed record Credential(
        int Format, string Channel, string ProviderCode, string AccountId,
        string SenderId, string ApiToken, string CallbackSigningSecret);

    internal sealed record Metadata(string Channel, string ProviderCode, string SenderId);

    /// <summary>Only administrators may install/replace a credential. Replacing
    /// a credential does not activate sending; SQL channels remain disabled.</summary>
    internal static void Save(Credential value)
    {
        Require("Communication.Configure");
        Validate(value);
        string root = DirectoryPath;
        string destination = Path.Combine(root, FileName(value.Channel));
        Directory.CreateDirectory(root);
        RestrictToWindowsUser(root);

        byte[] clear = Array.Empty<byte>(), cipher = Array.Empty<byte>();
        string temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            clear = JsonSerializer.SerializeToUtf8Bytes(value);
            cipher = Seal(clear);
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(cipher);
                stream.Flush(flushToDisk: true);
            }
            // Same-directory replacement avoids a partially written live profile.
            File.Move(temp, destination, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
            CryptographicOperations.ZeroMemory(cipher);
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>Metadata view never exposes API tokens or webhook secrets.</summary>
    internal static Metadata ReadMetadata(string channel)
    {
        Require("Communication.Configure");
        var credential = Read(channel);
        return new Metadata(credential.Channel, credential.ProviderCode, credential.SenderId);
    }

    /// <summary>Dispatcher access is separately gated. The caller must never
    /// log or serialize the returned record and must match approved SQL profile.</summary>
    internal static Credential LoadForDispatch(string channel, string expectedProvider)
    {
        Require("Communication.Send");
        var credential = Read(channel);
        if (!string.Equals(credential.ProviderCode, expectedProvider, StringComparison.Ordinal))
            throw new InvalidOperationException("Approved provider and protected local credentials do not match.");
        return credential;
    }

    internal static void Remove(string channel)
    {
        Require("Communication.Configure");
        string destination = Path.Combine(DirectoryPath, FileName(channel));
        if (File.Exists(destination)) File.Delete(destination);
    }

    internal static void Validate(Credential value)
    {
        ArgumentNullException.ThrowIfNull(value);
        FileName(value.Channel);
        if (value.Format != FormatVersion
            || string.IsNullOrWhiteSpace(value.ProviderCode)
            || value.ProviderCode.Length > 40
            || !value.ProviderCode.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
            || string.IsNullOrWhiteSpace(value.AccountId) || value.AccountId.Length > 128
            || string.IsNullOrWhiteSpace(value.SenderId) || value.SenderId.Length > 128
            || string.IsNullOrWhiteSpace(value.ApiToken) || value.ApiToken.Length is < 16 or > 4096
            || string.IsNullOrWhiteSpace(value.CallbackSigningSecret)
            || value.CallbackSigningSecret.Length is < 16 or > 4096
            || value.AccountId.ContainsAnyNewline() || value.SenderId.ContainsAnyNewline()
            || value.ApiToken.ContainsAnyNewline() || value.CallbackSigningSecret.ContainsAnyNewline())
            throw new ArgumentException("Provider credential metadata or format is invalid.");
    }

    private static Credential Read(string channel)
    {
        string path = Path.Combine(DirectoryPath, FileName(channel));
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is < 32 or > 65536)
            throw new InvalidOperationException("A valid protected messaging provider profile is not installed.");
        byte[] cipher = File.ReadAllBytes(path), clear = Array.Empty<byte>();
        try
        {
            clear = Open(cipher);
            Credential value = JsonSerializer.Deserialize<Credential>(clear)
                ?? throw new InvalidDataException("Protected provider profile is empty.");
            Validate(value);
            if (!string.Equals(value.Channel, channel, StringComparison.Ordinal))
                throw new InvalidDataException("Protected provider channel does not match.");
            return value;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
            CryptographicOperations.ZeroMemory(cipher);
        }
    }

    private static byte[] Seal(byte[] data) =>
        ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);

    private static byte[] Open(byte[] data) =>
        ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);

    private static string FileName(string channel) => channel switch
    {
        "Sms" => "sms.v1.dpapi",
        "WhatsApp" => "whatsapp.v1.dpapi",
        _ => throw new ArgumentException("Unknown communication channel.", nameof(channel))
    };

    private static void Require(string permission)
    {
        if (!CurrentUser.HasPermission(permission))
            throw new UnauthorizedAccessException("Messaging provider access was not authorized.");
    }

    private static void RestrictToWindowsUser(string directory)
    {
        SecurityIdentifier user = WindowsIdentity.GetCurrent().User
            ?? throw new UnauthorizedAccessException("Windows credential owner could not be identified.");
        var permissions = new DirectorySecurity();
        permissions.SetOwner(user);
        permissions.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        permissions.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(permissions);
    }

    private static bool ContainsAnyNewline(this string value) =>
        value.Contains('\r') || value.Contains('\n');
}
