using System.Diagnostics;
using System.Security.Principal;

namespace FMSC_SMS.Services.Communication;

/// <summary>
/// One short-lived dispatch process per minute while the enrolled Windows user
/// is signed in. No Windows service or FMSC login window is required.
/// </summary>
internal static class CommunicationWorkerTask
{
    private static string TaskName => "FMSC Communication " +
        (WindowsIdentity.GetCurrent().User?.Value
         ?? throw new UnauthorizedAccessException("The Windows account could not be identified."));

    internal static async Task ConfigureAsync(bool enabled, CancellationToken token = default)
    {
        var arguments = enabled
            ? new List<string> {
                "/Create", "/F", "/TN", TaskName, "/SC", "MINUTE", "/MO", "1",
                "/RL", "LIMITED", "/IT", "/TR",
                "\"" + Path.Combine(AppContext.BaseDirectory, "FMSC_SMS.exe") + "\" --communication-queue-worker"
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
        process.StandardInput.Close();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("Communication task registration did not complete.");
        }
        string output = await stdout, error = await stderr;
        if (process.ExitCode != 0)
        {
            if (!enabled && (output + error).Contains("cannot find", StringComparison.OrdinalIgnoreCase))
                return;
            throw new InvalidOperationException(
                "Windows could not " + (enabled ? "enable" : "disable") +
                " the communication task. Check Task Scheduler permissions for the current Windows account.");
        }
    }
}
