using System.Diagnostics;

internal static class FixtureFileAccess
{
    internal static void MakeReadable(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        string owner = Environment.GetEnvironmentVariable("FMSC_SAFETY_OWNER")!;
        string container = Environment.GetEnvironmentVariable("FMSC_SAFETY_CONTAINER")!;
        string root = Environment.GetEnvironmentVariable("FMSC_SAFETY_ROOT")!;
        if (!Guid.TryParseExact(owner,"N",out _) || container != "fmsc-level1-" + owner ||
            !Path.GetFullPath(path).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new Exception("File access adjustment escaped the owned fixture.");
        var start = new ProcessStartInfo("docker") { UseShellExecute=false, RedirectStandardOutput=true, RedirectStandardError=true };
        foreach (string argument in new[] { "--host=unix:///var/run/docker.sock", "exec", "--user", "root", container, "chmod", "0644", path })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        if (!process.WaitForExit(15000)) { process.Kill(); throw new Exception("Owned SQL file access adjustment timed out."); }
        if (process.ExitCode != 0) throw new Exception("Owned SQL file access adjustment failed.");
    }
}
