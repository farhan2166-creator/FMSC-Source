using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace FMSC_SMS.Core;

// Installed machine tools survive app activation/rollback and setup-folder removal.
// The shared resolver verifies complete bundles and bootstraps over HTTPS, never Git.
internal static class MaintenanceTools
{
    internal sealed record Tool(string Root, string File);

    internal static async Task<Tool> EnsureAsync(string name, CancellationToken token = default)
    {
        if (name is not ("Git" or "SqlCmd" or "LocalDb" or "VcRedist"))
            throw new ArgumentOutOfRangeException(nameof(name));

        string script = Path.Combine(AppContext.BaseDirectory, "UpdateSupport", "MaintenanceTools.ps1");
        if (!System.IO.File.Exists(script))
            script = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "FMSC_Updater", "MaintenanceTools.ps1");
        if (!System.IO.File.Exists(script))
            throw new FileNotFoundException("FMSC maintenance support is missing from the installed application.", script);

        string shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        ProcessStartInfo Start(string action, bool elevate)
        {
            var start = new ProcessStartInfo(shell) { UseShellExecute = elevate, CreateNoWindow = !elevate };
            if (elevate)
                start.Verb = "runas";
            else
            {
                start.RedirectStandardOutput = true;
                start.RedirectStandardError = true;
            }
            foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
                "-Action", action, "-Tool", name, "-ApplicationDirectory", AppContext.BaseDirectory })
                start.ArgumentList.Add(argument);
            return start;
        }

        async Task<(int Code, string Output)> Resolve()
        {
            using var process = Process.Start(Start("Resolve", false))
                ?? throw new IOException("Could not start FMSC tool verification.");
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                throw;
            }
            string text = await output, diagnostic = await errors;
            if (process.ExitCode is not (0 or 20))
                throw new IOException("FMSC tool verification failed. " + diagnostic);
            return (process.ExitCode, text);
        }

        token.ThrowIfCancellationRequested();
        var found = await Resolve();
        if (found.Code == 20)
        {
            // UAC authorizes machine-tool repair only, with no database action.
            // Once started, wait for its result even if the calling dialog closes.
            try
            {
                using var repair = Process.Start(Start("Install", true))
                    ?? throw new IOException("Could not start FMSC tool repair.");
                await repair.WaitForExitAsync();
                if (repair.ExitCode != 0)
                    throw new IOException("FMSC could not repair its " + name + " tools. Check the connection and retry.");
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                throw new OperationCanceledException("Tool repair was cancelled.", ex, token);
            }
            token.ThrowIfCancellationRequested();
            found = await Resolve();
        }
        if (found.Code != 0)
            throw new IOException("No verified " + name + " maintenance tool is available.");
        string json = found.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        using var result = JsonDocument.Parse(json);
        return new(result.RootElement.GetProperty("root").GetString()!, result.RootElement.GetProperty("file").GetString()!);
    }
}
