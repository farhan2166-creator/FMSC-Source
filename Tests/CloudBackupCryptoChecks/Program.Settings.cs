using System.Text;
using System.Text.Json;
using FMSC_SMS.Core;
using FMSC_SMS.Services.Backup;

internal static class CloudSettingsChecks
{
    internal static void Run(string root)
    {
        var original = new CloudBackupOptions {
            DropboxEnabled = true, GoogleDriveEnabled = false,
            AutomaticEnabled = true, Frequency = CloudBackupFrequency.Weekly,
            LocalScheduledTime = new TimeOnly(13, 17), WeeklyDay = DayOfWeek.Thursday,
            MaximumLocalBackups = 11, MaximumDropboxBackups = 23
        };
        string json = JsonSerializer.Serialize(original);
        string path = Path.Combine(root, "schedule.json");
        // Reproduce the original writer, including its UTF-8 preamble.
        AtomicTextFile.Write(path, json, Encoding.UTF8);
        byte[] legacy = File.ReadAllBytes(path);
        if (!legacy.AsSpan().StartsWith(Encoding.UTF8.Preamble))
            throw new Exception("Legacy settings fixture is missing its preamble.");
        try {
            JsonSerializer.Deserialize<CloudBackupOptions>(legacy);
            throw new Exception("Legacy fixture did not reproduce the original JSON reader failure.");
        } catch (JsonException) { }
        RequireUnchanged(CloudBackupSettings.Deserialize(legacy), json);

        AtomicTextFile.Write(path, json);
        byte[] current = File.ReadAllBytes(path);
        if (current.AsSpan().StartsWith(Encoding.UTF8.Preamble))
            throw new Exception("New cloud settings should not contain a preamble.");
        RequireUnchanged(CloudBackupSettings.Deserialize(current), json);

        try {
            CloudBackupSettings.Deserialize(Encoding.UTF8.GetBytes("{broken"));
            throw new Exception("Corrupt settings were silently accepted.");
        } catch (JsonException) { }
        original.MaximumDropboxBackups = 0;
        try {
            CloudBackupSettings.Deserialize(JsonSerializer.SerializeToUtf8Bytes(original));
            throw new Exception("Invalid retention was silently accepted.");
        } catch (InvalidDataException) { }
        Console.WriteLine("Cloud settings: legacy BOM, current format and validation passed.");
    }

    private static void RequireUnchanged(CloudBackupOptions loaded, string expected)
    {
        if (JsonSerializer.Serialize(loaded) != expected)
            throw new Exception("Loading cloud settings changed the saved enrollment or schedule.");
    }
}
