using System.Diagnostics;
using System.Text.Json;
using FMSC_SMS.Core.Updates;

// Owner-only tool. Uses the owner's Git credentials, never a school deploy key.
internal static class GitSnapshotPublisher
{
    private static string ResolveRemote() => GitHubRepositoryIdentity.ResolveUpdatesHttpsRemoteAsync(
        Environment.GetEnvironmentVariable("FMSC_UPDATES_PUBLISH_TOKEN") ?? "").GetAwaiter().GetResult();

    internal static string Prepare(string snapshot, string workspace, string? remote = null)
    {
        remote ??= ResolveRemote();
        DistributionSnapshot.ValidateSnapshot(snapshot);
        if (Directory.Exists(workspace) || File.Exists(workspace))
            throw new IOException("Publication workspace must be new.");
        Directory.CreateDirectory(workspace);
        Git(workspace, "init", "--quiet");
        Git(workspace, "config", "core.autocrlf", "false");
        Git(workspace, "remote", "add", "origin", remote);
        Git(workspace, "fetch", "--quiet", "origin", "main");
        string parent = Git(workspace, "rev-parse", "FETCH_HEAD").Trim();
        Git(workspace, "checkout", "--quiet", "-b", "publication", parent);
        string pointer = Path.Combine(workspace, "channel", "stable.json");
        if (File.Exists(pointer))
        {
            using var old = JsonDocument.Parse(File.ReadAllBytes(pointer));
            using var next = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(snapshot, "channel", "stable.json")));
            string oldVersion = old.RootElement.GetProperty("latestVersion").GetString()!;
            if (next.RootElement.GetProperty("previousVersion").GetString() != oldVersion ||
                UpdateManifestPolicy.CompareVersions(next.RootElement.GetProperty("latestVersion").GetString()!, oldVersion) <= 0)
                throw new InvalidDataException("Snapshot must retain the currently published latest version and advance it.");
        }
        // Work only in the newly created disposable clone. Preserve all remote history.
        foreach (string entry in Directory.GetFileSystemEntries(workspace))
        {
            if (Path.GetFileName(entry) == ".git") continue;
            if (Directory.Exists(entry)) Directory.Delete(entry, true);
            else File.Delete(entry);
        }
        Copy(snapshot, workspace);
        DistributionSnapshot.ValidateSnapshot(workspace);
        Git(workspace, "add", "--all");
        // Bound history growth conservatively. Refuse rather than rewrite history.
        // count-objects counts compressed packs plus loose objects; the push also
        // includes newly indexed blobs, so write the tree before measuring.
        Git(workspace, "write-tree");
        long bytes = Directory.EnumerateFiles(Path.Combine(workspace, ".git", "objects"), "*", SearchOption.AllDirectories)
            .Sum(path => new FileInfo(path).Length);
        if (bytes > 1024L * 1024 * 1024)
            throw new IOException("Distribution history exceeds the 1 GiB publication budget. Review archival/rotation with the owner; history was not rewritten.");
        File.WriteAllText(Path.Combine(workspace, ".git", "fmsc-expected-parent"), parent);
        return Git(workspace, "diff", "--cached", "--name-status") + "\nExpected remote main: " + parent +
            "\nStored Git objects: " + bytes + " bytes. No push performed.";
    }

    internal static void Publish(string workspace, string expectedParent, string? remote = null)
    {
        remote ??= ResolveRemote();
        if (Git(workspace, "remote", "get-url", "origin").Trim() != remote ||
            File.ReadAllText(Path.Combine(workspace, ".git", "fmsc-expected-parent")) != expectedParent ||
            Git(workspace, "rev-parse", "HEAD").Trim() != expectedParent)
            throw new InvalidDataException("Publication does not match the reviewed remote and parent.");
        DistributionSnapshot.ValidateSnapshot(workspace);
        // Require the exact reviewed index and working tree; no hidden changes.
        Git(workspace, "diff", "--exit-code");
        if (Git(workspace, "ls-files", "--others", "--exclude-standard").Length != 0)
            throw new InvalidDataException("Unreviewed files in publication workspace.");
        Git(workspace, "commit", "-m", "Publish verified FMSC update snapshot");
        // A normal fast-forward push updates one ref atomically. Concurrent owner
        // publication is rejected by Git; never force and never expose partial files.
        Git(workspace, "-c", "http.version=HTTP/1.1", "-c", "http.postBuffer=134217728",
            "push", "origin", "HEAD:refs/heads/main");
    }

    private static void Copy(string source, string destination)
    {
        foreach (string path in Directory.GetFileSystemEntries(source))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || Path.GetFileName(path) == ".git")
                throw new InvalidDataException("Unexpected link or Git metadata in snapshot.");
            string target = Path.Combine(destination, Path.GetFileName(path));
            if (Directory.Exists(path)) { Directory.CreateDirectory(target); Copy(path, target); }
            else File.Copy(path, target);
        }
    }

    internal static string Git(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory,
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true };
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Cannot start Git.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        bool transfer = arguments.Any(argument => argument is "push" or "fetch" or "clone");
        int timeout = transfer ? 15 * 60 * 1000 : 120000;
        if (!process.WaitForExit(timeout)) { process.Kill(true); throw new IOException("Git publication timed out."); }
        Task.WaitAll(output, error);
        if (process.ExitCode != 0) throw new IOException("Git publication failed: " + error.Result);
        return output.Result;
    }
}
