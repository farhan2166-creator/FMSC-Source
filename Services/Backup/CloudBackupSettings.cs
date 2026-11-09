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
    public bool GoogleDriveEnabled { get; set; }
    public bool DropboxEnabled { get; set; }
    public int MaximumLocalBackups { get; set; } = 7;
    public int MaximumGoogleDriveBackups { get; set; } = 7;
    public int MaximumDropboxBackups { get; set; } = 7;

    public void Validate()
    {
        if (Format != CurrentFormat || !Enum.IsDefined(Frequency) || !Enum.IsDefined(WeeklyDay)
            || MaximumLocalBackups is < 1 or > 365
            || MaximumGoogleDriveBackups is < 1 or > 365
            || MaximumDropboxBackups is < 1 or > 365)
            throw new InvalidDataException("Saved cloud backup schedule or retention settings are invalid.");
    }

    public bool IsDue(DateTimeOffset now, DateTimeOffset? lastSuccessfulBackup)
    {
        Validate();
        if (!AutomaticEnabled || Frequency == CloudBackupFrequency.Manual)
            return false;
        if (lastSuccessfulBackup is null)
            return now.TimeOfDay >= LocalScheduledTime.ToTimeSpan() &&
                (Frequency != CloudBackupFrequency.Weekly || now.DayOfWeek == WeeklyDay);
        DateTimeOffset last = lastSuccessfulBackup.Value.ToLocalTime();
        DateTimeOffset current = now.ToLocalTime();
        if (Frequency == CloudBackupFrequency.Every12Hours)
            return current - last >= TimeSpan.FromHours(12);
        if (Frequency == CloudBackupFrequency.Daily)
            return current.Date > last.Date &&
                current.TimeOfDay >= LocalScheduledTime.ToTimeSpan();
        // At most one scheduled weekly backup per day; a missed backup runs at
        // the first eligible check after the configured weekly day/time.
        DateTime scheduled = current.Date.AddDays(-((7 + (int)current.DayOfWeek - (int)WeeklyDay) % 7))
            .Add(LocalScheduledTime.ToTimeSpan());
        return current.DateTime >= scheduled && last.DateTime < scheduled;
    }
}

internal enum CloudBackupFrequency
{
    Manual = 0,
    Every12Hours = 1,
    Daily = 2,
    Weekly = 3
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
        byte[] data = File.ReadAllBytes(SettingsPath);
        if (data.Length is < 2 or > 64 * 1024)
            throw new InvalidDataException("Cloud backup settings file has an invalid size.");
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
        AtomicTextFile.Write(SettingsPath, JsonSerializer.Serialize(settings), Encoding.UTF8);
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
