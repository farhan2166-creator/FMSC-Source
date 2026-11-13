using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FMSC_SMS.Services.Backup;

internal static class GoogleRegistrationDelivery
{
    internal const string FileName = "CloudBackupGoogleDesktop.json";

    internal static void Prepare(string application, string? suppliedJson)
    {
        // The caller uses a private copy AFTER verifying the sealed public build.
        // Never place publisher registration in public artifacts or alter code bytes.
        string id = ReadClientId(application);
        string target = Path.Combine(application, FileName);
        if (!string.IsNullOrWhiteSpace(suppliedJson))
        {
            Validate(suppliedJson, id);
            string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, suppliedJson, new UTF8Encoding(false));
                File.Move(temporary, target, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        Require(application);
        // The original artifact inventory stays intact in the original directory.
        // The private payload's inventory must include its additional configuration.
        string inventory = Path.Combine(application, "SHA256SUMS.txt");
        if (File.Exists(inventory))
        {
            var lines = Directory.EnumerateFiles(application, "*", SearchOption.AllDirectories)
                .Where(path => !string.Equals(Path.GetFullPath(path), Path.GetFullPath(inventory), StringComparison.OrdinalIgnoreCase))
                .Select(path => (Path: path, Relative: Path.GetRelativePath(application, path).Replace('\\', '/')))
                .OrderBy(x => x.Relative, StringComparer.Ordinal)
                .Select(x => {
                    using var stream = File.OpenRead(x.Path);
                    return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant() + "  " + x.Relative;
                });
            File.WriteAllLines(inventory, lines, new UTF8Encoding(false));
        }
    }

    internal static void Require(string application)
    {
        string id = ReadClientId(application);
        string path = Path.Combine(application, FileName);
        if (id.Length == 0 && !File.Exists(path)) return;
        if (!File.Exists(path))
            throw new InvalidDataException("Private update is missing Google Desktop registration. Configure FMSC_GOOGLE_DESKTOP_JSON in Source Actions secrets, or include CloudBackupGoogleDesktop.json in the local published application. Nothing was published.");
        if (new FileInfo(path).Length > 16 * 1024)
            throw new InvalidDataException("Google Desktop registration exceeds its size limit.");
        Validate(File.ReadAllText(path), id);
    }

    private static void Validate(string json, string id)
    {
        if (Encoding.UTF8.GetByteCount(json) > 16 * 1024)
            throw new InvalidDataException("Google Desktop registration exceeds its size limit.");
        _ = GoogleDesktopRegistration.Parse(json, id);
    }

    private static string ReadClientId(string application)
    {
        try
        {
            string path = Path.Combine(application, "CloudBackupProviders.json");
            if (new FileInfo(path).Length > 16 * 1024) throw new InvalidDataException("Provider configuration is too large.");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var clientId = document.RootElement.GetProperty("GoogleClientId");
            if (clientId.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Google client ID must be a string.");
            string id = clientId.GetString()!.Trim();
            if (id.Length > 250) throw new InvalidDataException("Google client ID is too long.");
            return id;
        }
        catch (Exception error) when (error is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidDataException("Private update requires valid CloudBackupProviders.json from the verified application.");
        }
    }
}
