using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using System.Text.RegularExpressions;

namespace FMSC_SMS.Core.Updates;

/// <summary>
/// One temporary, read-only Git connection to the private FMSC distribution.
/// Fetches the update channel and manifest first; individual ZIP chunks are
/// materialized only on explicit request. No source repo or school database
/// is accessed and no Git credential is embedded in the application.
/// </summary>
internal sealed class UpdateGitSession : IUpdateArtifactSource, IAsyncDisposable
{

    // Pin the published GitHub ed25519 host key, not a host key acquired from
    // the network at enrollment time.
    internal const string GitHubKnownHosts = "github.com ssh-ed25519 " +
        "AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl\n";

    private readonly string _workspace;
    private (string GitExecutable, string SshExecutable)? _transport;
    private bool _isOpen;
    private bool _disposed;

    private UpdateGitSession(string workspace) => _workspace = workspace;

    // Complete releases bundle Git, OpenSSH, DLLs and their supporting tools.
    // School machines must not need a separate install or PATH configuration.
    internal static (string GitExecutable, string SshExecutable) ResolveTransport(string root)
    {
        string git = Path.Combine(root, "cmd", "git.exe");
        string ssh = Path.Combine(root, "usr", "bin", "ssh.exe");
        if (!File.Exists(git) || !File.Exists(ssh))
            throw new FileNotFoundException(
                "The bundled FMSC update tools (Git/SSH) are missing. " +
                "FMSC could not restore its verified Git/SSH maintenance bundle.");
        return (git, ssh);
    }

    internal static async Task<UpdateGitSession> OpenAsync(
        CancellationToken cancellationToken = default)
    {
        if (!UpdateDeviceKey.IsEnrolled)
            throw new InvalidOperationException("This computer has not yet generated an update authorization key.");
        // Resolve the current owner by durable GitHub account ID before using the
        // existing read-only SSH deploy key; never depend on old URL redirects.
        string distributionRemote;
        try { distributionRemote = await GitHubRepositoryIdentity.ResolveUpdatesSshRemoteAsync(
            cancellationToken: cancellationToken); }
        catch (HttpRequestException) { throw new UpdateGitCommandException("GitHub identity lookup is unavailable."); }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new UpdateGitCommandException("GitHub identity lookup timed out."); }
        catch (InvalidDataException) { throw new UpdateGitCommandException("GitHub owner identity could not be verified.", false); }
        // Reclaim abandoned private-Git workspaces from earlier interrupted checks.
        // Only strictly named, >24h-old directories can be pruned; active
        // download sessions and device-key.v1.dpapi remain untouched.
        try { PruneAbandonedWorkspaces(UpdateDeviceKey.StateDirectory, DateTime.UtcNow); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or InvalidDataException) { /* Stale cache must not block a new update check. */ }
        string root = Path.Combine(UpdateDeviceKey.StateDirectory,
            "git-read-" + Guid.NewGuid().ToString("N"));
        var session = new UpdateGitSession(root);
        byte[] privateKey = Array.Empty<byte>();
        try
        {
            var tool = await MaintenanceTools.EnsureAsync("Git", cancellationToken);
            // Resolve once per session, after complete-bundle verification.
            session._transport = ResolveTransport(tool.Root);
            Directory.CreateDirectory(root);
            // Set a user-only ACL BEFORE materializing the short-lived SSH PEM.
            UpdateDeviceKey.RestrictDirectory(root);
            privateKey = UpdateDeviceKey.OpenPrivateKey();
            string pemPath = Path.Combine(root, "device.pem");
            using (var file = new FileStream(pemPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await file.WriteAsync(privateKey, cancellationToken);
                file.Flush(flushToDisk: true);
            }
            File.WriteAllText(Path.Combine(root, "known_hosts"), GitHubKnownHosts, Encoding.ASCII);
            // This never checks out the release ZIPs or source. Git will fetch
            // individual lazy blobs only when ReadSmall/FetchPart requests them.
            await session.RunGitAsync(
                new[] { "clone", "--quiet", "--no-checkout", "--depth=1",
                    "--filter=blob:none", "--single-branch", "--branch", "main",
                    distributionRemote, "feed" },
                isRepoCommand: false, destination: Stream.Null,
                maxOutputBytes: 65536, cancellationToken,
                TimeSpan.FromSeconds(60));
            session._isOpen = true;
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }

    internal async Task<bool> HasStableFeedAsync(CancellationToken token)
    {
        EnsureOpen();
        // A missing feed means no release has been published yet, not a broken
        // school database or a reason to block application startup.
        var result = await RunGitAsync(
            new[] { "-C", "feed", "cat-file", "-e", "HEAD:channel/stable.json" },
            isRepoCommand: true, destination: Stream.Null, maxOutputBytes: 4096,
            token, TimeSpan.FromSeconds(30), allowFailure: true);
        return result.ExitCode == 0;
    }

    public async Task<byte[]> ReadSmallAsync(string relativePath, int maximumBytes,
        CancellationToken cancellationToken)
    {
        EnsureOpen();
        if (maximumBytes is <= 0 or > 4 * 1024 * 1024 ||
            !IsAllowedMetadataPath(relativePath))
            throw new InvalidDataException("Unexpected or excessive update metadata request.");
        using var output = new MemoryStream();
        await RunGitAsync(
            new[] { "-C", "feed", "show", "HEAD:" + relativePath },
            isRepoCommand: true, destination: output, maxOutputBytes: maximumBytes,
            cancellationToken, TimeSpan.FromSeconds(40));
        return output.ToArray();
    }

    public async Task FetchPartAsync(string relativePath, string destinationFile,
        long expectedBytes, CancellationToken cancellationToken, IProgress<long>? progress = null)
    {
        EnsureOpen();
        if (!IsAllowedPartPath(relativePath) ||
            expectedBytes is < 1 or > UpdateChunks.DefaultMaximumPartBytes)
            throw new InvalidDataException("Invalid update chunk request.");
        if (File.Exists(destinationFile) || Directory.Exists(destinationFile))
            throw new IOException("The destination update chunk must be a new file.");
        try
        {
            using var objectId = new MemoryStream();
            await RunGitAsync(new[] { "-C", "feed", "rev-parse", "HEAD:" + relativePath },
                true, objectId, 128, cancellationToken, TimeSpan.FromSeconds(30));
            string oid = Encoding.ASCII.GetString(objectId.ToArray()).Trim();
            if (oid.Length is not (40 or 64) || !oid.All(Uri.IsHexDigit))
                throw new InvalidDataException("Update part is not a valid Git object.");
            // Fetch only this blob with progress enabled. Lazy `git show` otherwise
            // hides the transfer, then emits local disk bytes at misleading speeds.
            await RunGitAsync(new[] { "-C", "feed", "-c", "fetch.negotiationAlgorithm=noop", "fetch", "origin", "--progress",
                "--no-tags", "--no-write-fetch-head", "--recurse-submodules=no", "--filter=blob:none", "--stdin" },
                true, Stream.Null, 65536, cancellationToken, TimeSpan.FromMinutes(30), networkProgress: progress, input: oid);
            using (var output = new FileStream(destinationFile, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                await RunGitAsync(
                    new[] { "-C", "feed", "show", "HEAD:" + relativePath },
                    isRepoCommand: true, destination: output,
                    maxOutputBytes: expectedBytes, cancellationToken,
                    TimeSpan.FromMinutes(12));
                await output.FlushAsync(cancellationToken);
            }
            if (new FileInfo(destinationFile).Length != expectedBytes)
                throw new InvalidDataException("Private Git returned an incomplete chunk.");
        }
        catch
        {
            if (File.Exists(destinationFile)) File.Delete(destinationFile);
            throw;
        }
    }

    internal static bool IsAllowedMetadataPath(string value)
    {
        if (value == "channel/stable.json") return true;
        var pieces = value.Split('/');
        return pieces.Length == 3 && pieces[0] == "versions" &&
            UpdateManifestPolicy.IsPublishedVersion(pieces[1]) &&
            pieces[2] == "update-manifest.json";
    }

    internal static bool IsAllowedPartPath(string value)
    {
        var pieces = value.Split('/');
        if (pieces.Length != 3 || pieces[0] != "versions" ||
            !UpdateManifestPolicy.IsPublishedVersion(pieces[1]))
            return false;
        const string full = "FMSC_SMS-app-full.zip.part";
        const string incremental = "FMSC_SMS-app-incremental.zip.part";
        string name = pieces[2];
        string? prefix = name.StartsWith(full, StringComparison.Ordinal) ? full :
            name.StartsWith(incremental, StringComparison.Ordinal) ? incremental : null;
        return prefix is not null &&
            name.Length == prefix.Length + 4 &&
            name.AsSpan(prefix.Length).ToArray().All(char.IsAsciiDigit) &&
            UpdateManifestPolicy.IsSafeRelativePath(name);
    }

    private void EnsureOpen()
    {
        if (_disposed || !_isOpen)
            throw new ObjectDisposedException(nameof(UpdateGitSession));
    }

    private async Task<(int ExitCode, long Bytes)> RunGitAsync(
        string[] arguments, bool isRepoCommand, Stream destination,
        long maxOutputBytes, CancellationToken cancellationToken,
        TimeSpan maximumDuration, bool allowFailure = false, IProgress<long>? networkProgress = null, string? input = null)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(maximumDuration);
        var transport = _transport ?? throw new InvalidOperationException("Update transport has not been verified.");
        var start = new ProcessStartInfo(transport.GitExecutable)
        {
            WorkingDirectory = _workspace,
            RedirectStandardInput = input is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_LFS_SKIP_SMUDGE"] = "1";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["LC_ALL"] = "C";
        start.Environment["GIT_PROGRESS_DELAY"] = "0";
        start.Environment["GIT_SSH_COMMAND"] = isRepoCommand
            ? GitSsh(transport.SshExecutable, "../device.pem", "../known_hosts")
            : GitSsh(transport.SshExecutable, "device.pem", "known_hosts");
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        if (input is not null) start.StandardInputEncoding = new UTF8Encoding(false);
        using var process = new Process { StartInfo = start };
        process.Start();
        try
        {
            var errorTask = ReadErrorsAsync(process.StandardError, networkProgress, linked.Token);
            if (input is not null)
            {
                // Git's missing-object stdin protocol uses LF, including on Windows.
                await process.StandardInput.WriteAsync((input + "\n").AsMemory(), linked.Token);
                process.StandardInput.Close();
            }
            long bytes = 0;
            var buffer = new byte[65536];
            int read;
            while (true)
            {
                var reading = process.StandardOutput.BaseStream.ReadAsync(buffer.AsMemory(), linked.Token).AsTask();
                if (await Task.WhenAny(reading, errorTask) == errorTask && errorTask.IsFaulted) await errorTask;
                read = await reading;
                if (read == 0) break;
                bytes += read;
                if (bytes > maxOutputBytes)
                    throw new InvalidDataException("Git emitted data larger than the allowed update file.");
                await destination.WriteAsync(buffer.AsMemory(0, read), linked.Token);
            }
            await process.WaitForExitAsync(linked.Token);
            string errors = await errorTask; // Never display raw stderr or key paths.
            if (process.ExitCode != 0 && !allowFailure)
            {
                var failure = ClassifyFailure(errors);
                failure.Data["GitDiagnostic"] = errors;
                throw failure;
            }
            return (process.ExitCode, bytes);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new UpdateGitCommandException("The private Git operation timed out.");
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
    }

    private static string GitSsh(string sshExecutable, string pem, string knownHosts) =>
        "\"" + sshExecutable.Replace('\\', '/') + "\" -F none -o BatchMode=yes -o IdentitiesOnly=yes " +
        "-o ConnectTimeout=15 -o ServerAliveInterval=10 -o ServerAliveCountMax=3 " +
        "-o StrictHostKeyChecking=yes -o PasswordAuthentication=no " +
        "-o UserKnownHostsFile=" + knownHosts + " -i " + pem;

    internal static long? ParseNetworkBytes(string line)
    {
        var match = Regex.Match(line, @"Receiving objects:.*?,\s*([0-9.]+)\s+(bytes|KiB|MiB|GiB)\b",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out double amount)) return null;
        double scale = match.Groups[2].Value switch { "KiB" => 1024d, "MiB" => 1048576d, "GiB" => 1073741824d, _ => 1d };
        double result = amount * scale;
        return result is >= 0 and < long.MaxValue ? (long)result : null;
    }

    internal static UpdateGitCommandException ClassifyFailure(string errors)
    {
        if (errors.Contains("Permission denied (publickey)", StringComparison.OrdinalIgnoreCase) ||
            errors.Contains("Repository not found", StringComparison.OrdinalIgnoreCase))
            return new("This computer's update authorization was refused. Ask the owner to review its read-only key.", false);
        if (errors.Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase) ||
            errors.Contains("REMOTE HOST IDENTIFICATION HAS CHANGED", StringComparison.OrdinalIgnoreCase))
            return new("GitHub's host identity could not be verified. Update stopped.", false);
        if (errors.Contains("not our ref", StringComparison.OrdinalIgnoreCase))
            return new("The requested update part is unavailable. Check for updates again.", false);
        return new("The update connection was interrupted or the network is unavailable.");
    }

    private static async Task<string> ReadErrorsAsync(StreamReader reader, IProgress<long>? progress, CancellationToken token)
    {
        var recent = new StringBuilder();
        var line = new StringBuilder();
        var buffer = new char[1024];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            recent.Append(buffer, 0, read);
            if (recent.Length > 16384) recent.Remove(0, recent.Length - 16384);
            for (int i = 0; i < read; i++)
            {
                if (buffer[i] is '\r' or '\n')
                {
                    if (progress is not null && ParseNetworkBytes(line.ToString()) is { } bytes) progress.Report(bytes);
                    line.Clear();
                }
                else if (line.Length < 8192) line.Append(buffer[i]);
            }
        }
        if (progress is not null && ParseNetworkBytes(line.ToString()) is { } final) progress.Report(final);
        return recent.ToString();
    }

    // For regression tests, pass an isolated root and deterministic clock.
    // Refuse to traverse links or prune unknown directories, even under an
    // otherwise correctly named workspace.
    internal static int PruneAbandonedWorkspaces(string stateRoot, DateTime utcNow)
    {
        if (!Directory.Exists(stateRoot)) return 0;
        UpdatePartDownload.RejectLinks(stateRoot);
        int removed = 0;
        foreach (string folder in Directory.EnumerateDirectories(
            stateRoot, "git-read-*", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(folder);
            if (!Regex.IsMatch(name, @"\Agit-read-[a-f0-9]{32}\z", RegexOptions.CultureInvariant) ||
                Directory.GetLastWriteTimeUtc(folder) > utcNow.AddHours(-24))
                continue;
            try
            {
                RemovePrivateGitWorkspace(folder);
                removed++;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                or InvalidDataException)
            {
                // A still-locked or suspect workspace is never forcibly removed.
            }
        }
        return removed;
    }

    private static void RemovePrivateGitWorkspace(string folder)
    {
        if (!Directory.Exists(folder)) return;
        UpdatePartDownload.RejectLinks(folder);
        var stack = new Stack<string>();
        var entries = new List<(string Path, FileAttributes Attributes)>();
        stack.Push(folder);
        while (stack.Count > 0)
        {
            string current = stack.Pop();
            foreach (string entry in Directory.EnumerateFileSystemEntries(current))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException("Linked private update workspace refused.");
                entries.Add((entry, attributes));
                if (attributes.HasFlag(FileAttributes.Directory)) stack.Push(entry);
            }
        }
        // All descendants have passed link checks before any mutation.
        foreach (var (entry, attributes) in entries)
            if (attributes.HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
        FileAttributes rootAttributes = File.GetAttributes(folder);
        if (rootAttributes.HasFlag(FileAttributes.ReadOnly))
            File.SetAttributes(folder, rootAttributes & ~FileAttributes.ReadOnly);
        Directory.Delete(folder, recursive: true);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _isOpen = false;
        try
        {
            string pem = Path.Combine(_workspace, "device.pem");
            if (File.Exists(pem)) File.Delete(pem); // Remove plaintext key before any optional Git cleanup can fail.
            if (Directory.Exists(_workspace)) RemovePrivateGitWorkspace(_workspace);
        }
        catch (IOException) { /* Never delete a live application file. */ }
        catch (UnauthorizedAccessException) { /* Temporary user-only folder. */ }
        catch (InvalidDataException) { /* Refuse linked or suspicious remnants. */ }
        return ValueTask.CompletedTask;
    }
}
