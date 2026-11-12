using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using FMSC_SMS.Core;

namespace FMSC_SMS.Services.Backup;

/// <summary>
/// Configuration only: never contains OAuth tokens, SQL contents, or the
/// recovery passphrase. Cloud operations must remain disabled until explicitly
/// enrolled and tested. The database and updater are not changed by this file.
/// </summary>
internal sealed class CloudBackupOptions
{
    internal const int CurrentFormat = 1;
    public int Format { get; set; } = CurrentFormat;
    public bool AutomaticEnabled { get; set; }
    public CloudBackupFrequency Frequency { get; set; } = CloudBackupFrequency.Daily;
    public TimeOnly LocalScheduledTime { get; set; } = new(18, 0);
    public DayOfWeek WeeklyDay { get; set; } = DayOfWeek.Friday;
    // Absent in existing schedule.json files: preserve their historical local-backup behavior.
    public bool LocalEnabled { get; set; } = true;
    public bool GoogleDriveEnabled { get; set; }
    public bool DropboxEnabled { get; set; }
    public int MaximumLocalBackups { get; set; } = 7;
    public int MaximumGoogleDriveBackups { get; set; } = 7;
    public int MaximumDropboxBackups { get; set; } = 7;

    public void Validate()
    {
        if ((!LocalEnabled && !GoogleDriveEnabled && !DropboxEnabled) ||
            Format != CurrentFormat || !Enum.IsDefined(Frequency) || !Enum.IsDefined(WeeklyDay)
            || MaximumLocalBackups is < 1 or > 365
            || MaximumGoogleDriveBackups is < 1 or > 365
            || MaximumDropboxBackups is < 1 or > 365)
            throw new InvalidDataException("Saved cloud backup schedule or retention settings are invalid.");
    }

    public bool IsDue(DateTimeOffset now, DateTimeOffset? lastSuccessfulBackup)
    {
        Validate();
        return CloudBackupSchedulePolicy.IsDue(
            AutomaticEnabled, Frequency, LocalScheduledTime, WeeklyDay,
            now.ToLocalTime().DateTime,
            lastSuccessfulBackup?.ToLocalTime().DateTime);
    }
}

internal static class CloudBackupSettings
{
    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FMSC", "CloudBackups");
    private static readonly string SettingsPath = Path.Combine(Root, "schedule.json");
    private static readonly string RecoveryKeyPath = Path.Combine(Root, "recovery-key.v1.dpapi");
    private static readonly byte[] Entropy = SHA256.HashData(Encoding.UTF8.GetBytes("FMSC/cloud-backups/recovery-key/v1"));

    internal static CloudBackupOptions Load()
    {
        if (!File.Exists(SettingsPath)) return new CloudBackupOptions();
        return Deserialize(File.ReadAllBytes(SettingsPath));
    }

    // Editing must remain accessible if a saved schedule is damaged. This read-only
    // fallback never silently overwrites the original or enables unattended jobs.
    internal static CloudBackupOptions LoadForEditing(out string? warning)
    {
        try
        {
            var existing = Load();
            warning = null;
            return existing;
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            warning = "The saved backup settings could not be read. Their original file will be preserved before you explicitly save repaired settings. Automatic backups will not use the invalid configuration.";
            AppExceptionHandler.LogNonFatal(ex, "Opening damaged cloud backup settings for safe repair");
            return new CloudBackupOptions();
        }
    }

    internal static void PreserveUnreadableSettings()
    {
        if (!File.Exists(SettingsPath)) return;
        // A valid file does not need a snapshot. Never delete or rewrite a broken
        // file before preserving its exact bytes in a separate protected path.
        try { _ = Load(); return; }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            EnsurePrivateDirectory();
            string copy = Path.Combine(Root, "schedule.unreadable." +
                DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "." + Guid.NewGuid().ToString("N") + ".json");
            File.Copy(SettingsPath, copy, overwrite: false);
        }
    }

    internal static CloudBackupOptions Deserialize(ReadOnlySpan<byte> data)
    {
        if (data.Length is < 2 or > 64 * 1024)
            throw new InvalidDataException("Cloud backup settings file has an invalid size.");
        // Earlier saves used Encoding.UTF8, whose StreamWriter preamble is
        // rejected by the byte-based JSON reader. Keep those enrolled settings
        // readable without resetting providers, scheduling or recovery keys.
        if (data.StartsWith(Encoding.UTF8.Preamble))
            data = data[Encoding.UTF8.Preamble.Length..];
        var settings = JsonSerializer.Deserialize<CloudBackupOptions>(data)
            ?? throw new InvalidDataException("Cloud backup settings are empty.");
        settings.Validate();
        return settings;
    }

    internal static void Save(CloudBackupOptions settings)
    {
        if (!CurrentUser.HasPermission("Backup.Restore"))
            throw new UnauthorizedAccessException("Changing cloud backup settings requires backup recovery permission.");
        settings.Validate();
        EnsurePrivateDirectory();
        AtomicTextFile.Write(SettingsPath, JsonSerializer.Serialize(settings));
    }

    /// <summary>
    /// One-time school-controlled recovery passphrase. DPAPI permits unattended
    /// backup on THIS Windows account; it must not be the only surviving copy
    /// of the passphrase, as another PC cannot decrypt this local credential.
    /// </summary>
    internal static void SaveRecoveryPassphrase(string passphrase)
    {
        if (!CurrentUser.HasPermission("Backup.Restore"))
            throw new UnauthorizedAccessException("Only a backup recovery administrator may configure encryption.");
        if (string.IsNullOrWhiteSpace(passphrase) || passphrase.Length < 12)
            throw new ArgumentException("The recovery passphrase must contain at least 12 characters.");
        EnsurePrivateDirectory();
        byte[] clear = Encoding.UTF8.GetBytes(passphrase);
        byte[] protectedBytes = Array.Empty<byte>();
        try
        {
            protectedBytes = ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser);
            string temp = RecoveryKeyPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    output.Write(protectedBytes);
                    output.Flush(flushToDisk: true);
                }
                File.Move(temp, RecoveryKeyPath, overwrite: true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    internal static string OpenRecoveryPassphrase()
    {
        byte[] protectedBytes = File.ReadAllBytes(RecoveryKeyPath);
        if (protectedBytes.Length is < 32 or > 8192)
            throw new InvalidDataException("Saved cloud recovery credential is invalid.");
        byte[] clear = Array.Empty<byte>();
        try
        {
            clear = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            string value = Encoding.UTF8.GetString(clear);
            if (value.Length < 12)
                throw new InvalidDataException("Cloud recovery credential is incomplete.");
            return value;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    internal static bool HasLocalRecoveryPassphrase => File.Exists(RecoveryKeyPath);

    private static void EnsurePrivateDirectory()
    {
        Directory.CreateDirectory(Root);
        var user = WindowsIdentity.GetCurrent().User
            ?? throw new UnauthorizedAccessException("Cannot identify the Windows backup account.");
        var acl = new DirectorySecurity();
        acl.SetOwner(user);
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        acl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(Root).SetAccessControl(acl);
    }
}
