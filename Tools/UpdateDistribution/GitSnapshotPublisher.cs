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
        // Fetch only the live tip; historic binary objects are not publication inputs.
        Git(workspace, "fetch", "--quiet", "--depth=1", "origin", "main");
        string parent = Git(workspace, "rev-parse", "FETCH_HEAD").Trim();
        RequireExclusiveMainRef(workspace, parent);
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
            // An existing version is immutable even when its historic Git commit is retired.
            string manifest = Path.Combine("versions", oldVersion, "update-manifest.json");
            if (!File.ReadAllBytes(Path.Combine(workspace, manifest)).AsSpan()
                .SequenceEqual(File.ReadAllBytes(Path.Combine(snapshot, manifest))))
                throw new InvalidDataException("Publication changed the retained release manifest.");
        }
        // Prepare only the validated two-version tree in a disposable workspace.
        // The eventual publication will make a new root commit, not extend old history.
        foreach (string entry in Directory.GetFileSystemEntries(workspace))
        {
            if (Path.GetFileName(entry) == ".git") continue;
            if (Directory.Exists(entry)) Directory.Delete(entry, true);
            else File.Delete(entry);
        }
        Copy(snapshot, workspace);
        DistributionSnapshot.ValidateSnapshot(workspace);
        Git(workspace, "add", "--all");
        // Measure only retained release files. The fetched, obsolete Git objects
        // are not part of the new root commit and must not block safe releases.
        string tree = Git(workspace, "write-tree").Trim();
        long bytes = SnapshotBytes(workspace);
        if (bytes > 1024L * 1024 * 1024)
            throw new IOException("The latest two verified release snapshots exceed the 1 GiB publication budget.");
        File.WriteAllText(Path.Combine(workspace, ".git", "fmsc-expected-parent"), parent);
        File.WriteAllText(Path.Combine(workspace, ".git", "fmsc-expected-tree"), tree);
        return Git(workspace, "diff", "--cached", "--name-status") + "\nExpected remote main: " + parent +
            "\nVerified snapshot tree: " + tree +
            "\nRetained release files: " + bytes + " bytes. Root-history rotation prepared; no push performed.";
    }

    internal static void Publish(string workspace, string expectedParent, string? remote = null)
    {
        remote ??= ResolveRemote();
        if (Git(workspace, "remote", "get-url", "origin").Trim() != remote ||
            File.ReadAllText(Path.Combine(workspace, ".git", "fmsc-expected-parent")) != expectedParent ||
            Git(workspace, "rev-parse", "HEAD").Trim() != expectedParent)
            throw new InvalidDataException("Publication does not match the reviewed remote and parent.");
        RequireExclusiveMainRef(workspace, expectedParent);
        DistributionSnapshot.ValidateSnapshot(workspace);
        // Do not publish worktree changes, hidden files or a modified staged index.
        Git(workspace, "diff", "--exit-code");
        if (Git(workspace, "ls-files", "--others", "--exclude-standard").Length != 0)
            throw new InvalidDataException("Unreviewed files in publication workspace.");
        string reviewedTree = File.ReadAllText(Path.Combine(workspace, ".git", "fmsc-expected-tree")).Trim();
        string currentTree = Git(workspace, "write-tree").Trim();
        if (reviewedTree != currentTree)
            throw new InvalidDataException("The staged publication changed since review.");
        if (SnapshotBytes(workspace) > 1024L * 1024 * 1024)
            throw new IOException("Retained releases exceed the 1 GiB publication budget.");
        // A root commit includes only the two-version verified tree; old Git
        // commits and deleted versions do not remain in the published ancestry.
        string commit = Git(workspace, "commit-tree", currentTree, "-m",
            "Publish verified FMSC update snapshot (bounded history)").Trim();
        if (Git(workspace, "rev-list", "--parents", commit).Trim() != commit)
            throw new InvalidDataException("Distribution rotation unexpectedly retained Git parents.");
        // This is a *guarded* non-fast-forward update: --force-with-lease compares
        // the exact reviewed remote SHA, preventing an overwrite if another
        // owner has published since preparation. Git updates one ref atomically.
        Git(workspace, "-c", "http.version=HTTP/1.1", "-c", "http.postBuffer=134217728",
            "push", "--force-with-lease=refs/heads/main:" + expectedParent,
            "origin", commit + ":refs/heads/main");
        // Keep the local publication receipt aligned with the now-published SHA.
        Git(workspace, "update-ref", "refs/heads/publication", commit, expectedParent);
    }

    private static long SnapshotBytes(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(root, path).StartsWith(
                ".git" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Sum(path => new FileInfo(path).Length);

    private static void RequireExclusiveMainRef(string workspace, string expectedMain)
    {
        // Another branch or tag would anchor retired release objects indefinitely.
        string[] refs = Git(workspace, "ls-remote", "--heads", "--tags", "origin")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string[] main = refs.Length == 1 ? refs[0].Trim().Split('\t') : Array.Empty<string>();
        if (main.Length != 2 || main[1] != "refs/heads/main" ||
            !string.Equals(main[0], expectedMain, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "History rotation requires exactly one remote branch (main), no tags, and the reviewed main commit.");
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
