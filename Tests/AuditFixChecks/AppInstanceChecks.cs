using System.Diagnostics;
using FMSC_SMS.Core;

internal static class AppInstanceChecks
{
    internal static bool TryRunChild(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length != 3 || args[0] != "--instance-child") return false;
        if (!args[1].StartsWith(@"Global\FMSC_SMS.InstanceTest.", StringComparison.Ordinal)
            || args[2] is not ("hold" or "once")) { exitCode = 2; return true; }
        using var lease = AppInstanceLease.TryAcquire(args[1]);
        if (lease is null) { exitCode = 23; return true; }
        Console.WriteLine("ACQUIRED"); Console.Out.Flush();
        if (args[2] == "hold") Console.ReadLine();
        return true;
    }

    internal static void Run(Action<bool, string> check)
    {
        string name = @"Global\FMSC_SMS.InstanceTest." + Guid.NewGuid().ToString("N");
        using (var first = Start(name, "hold"))
        {
            try
            {
                Ready(first, check);
                using (var second = Start(name, "once"))
                    check(Exit(second) == 23, "Another process cannot acquire the interactive app gate while it is owned");
                using (var third = Start(name, "once"))
                    check(Exit(third) == 23, "Repeated attempts never release or replace the first process's gate");
                first.StandardInput.WriteLine("close"); first.StandardInput.Flush();
                check(Exit(first) == 0, "Normal shutdown releases the interactive app gate");
            }
            finally { Stop(first); }
        }
        using (var afterClose = Start(name, "once"))
            check(Exit(afterClose) == 0, "A new process can reopen after normal shutdown");
        // Keep an observer handle so the kernel object survives the crash and
        // the next owner must handle abandoned ownership, not just create anew.
        using var observer = new Mutex(false, name);
        using (var crashed = Start(name, "hold"))
        {
            try { Ready(crashed, check); crashed.Kill(); check(crashed.WaitForExit(10000), "The synthetic crashed owner exited"); }
            finally { Stop(crashed); }
        }
        using var afterCrash = Start(name, "once");
        check(Exit(afterCrash) == 0, "An unexpectedly terminated process leaves no stale lock blocking reopening");
    }

    private static Process Start(string name, string mode)
    {
        string path = Environment.ProcessPath ?? throw new InvalidOperationException("Test process path is unavailable.");
        var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(AppInstanceChecks).Assembly.Location);
        foreach (string value in new[] { "--instance-child", name, mode }) start.ArgumentList.Add(value);
        return Process.Start(start) ?? throw new InvalidOperationException("Synthetic instance could not start.");
    }
    private static void Ready(Process process, Action<bool, string> check)
    {
        Task<string?> ready = process.StandardOutput.ReadLineAsync();
        check(ready.Wait(10000) && ready.Result == "ACQUIRED", "The first process acquires its isolated machine-wide test gate");
    }
    private static int Exit(Process process)
    {
        try
        {
            if (!process.WaitForExit(10000)) throw new TimeoutException("Synthetic instance did not exit.");
            return process.ExitCode;
        }
        finally { Stop(process); }
    }
    private static void Stop(Process process)
    {
        if (!process.HasExited) { process.Kill(); process.WaitForExit(10000); }
    }
}
