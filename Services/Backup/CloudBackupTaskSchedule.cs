using System.Diagnostics;
using System.Security.Principal;
using FMSC_SMS.Core;

namespace FMSC_SMS.Services.Backup;

/// <summary>
/// Uses Windows Task Scheduler rather than an in-app timer: closing the FMSC
/// window does not cancel backups while the enrolled user remains logged on. Runs ONLY under the enrolled Windows user
/// (same DPAPI token vault and same LocalDB instance). The 30-minute trigger
/// invokes CloudBackupOptions.IsDue before any SQL backup occurs.
/// </summary>
internal static class CloudBackupTaskSchedule
{
    private static string TaskName => "FMSC Cloud Backup " +
        (WindowsIdentity.GetCurrent().User?.Value
         ?? throw new UnauthorizedAccessException("Cannot identify scheduled backup owner."));

    internal static async Task ConfigureAsync(bool enabled, CancellationToken token = default)
    {
        if (!CurrentUser.HasPermission("Backup.Restore"))
            throw new UnauthorizedAccessException("Only a recovery administrator may configure unattended backups.");
        var arguments = enabled
            ? new List<string> {
                "/Create", "/F", "/TN", TaskName, "/SC", "MINUTE", "/MO", "30",
                "/RL", "LIMITED", "/IT", "/TR",
                "\"" + Path.Combine(AppContext.BaseDirectory, "FMSC_SMS.exe") + "\" --cloud-backup-scheduled"
              }
            : new List<string> { "/Delete", "/F", "/TN", TaskName };
        var start = new ProcessStartInfo {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"),
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
        };
        foreach (string part in arguments) start.ArgumentList.Add(part);
        using var process = Process.Start(start)
            ?? throw new IOException("Windows Task Scheduler could not start.");
        // No interactive Windows-password prompts may block FMSC settings.
        process.StandardInput.Close();
        // Read both outputs so a noisy failure cannot deadlock the child process.
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("Windows scheduled-task registration did not finish without prompting for credentials.");
        }
        string output = await stdout, error = await stderr;
        if (process.ExitCode != 0)
        {
            // Delete is idempotent; a missing task is already disabled.
            if (!enabled && (output + error).Contains("cannot find", StringComparison.OrdinalIgnoreCase))
                return;
            throw new InvalidOperationException(
                "Windows could not " + (enabled ? "enable" : "disable") +
                " automatic FMSC cloud backups. Check Task Scheduler access. " +
                "No password or account secrets are needed.");
        }
    }
}
