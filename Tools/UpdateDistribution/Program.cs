using System.Security.Cryptography;
using System.Text.Json;
using FMSC_SMS.Core.Updates;

try
{
    if (args.Length == 4 && args[0] == "--prepare-publication" && args[2] == "--workspace")
    {
        Console.WriteLine(GitSnapshotPublisher.Prepare(args[1], args[3]));
        return;
    }
    if (args.Length == 4 && args[0] == "--publish-reviewed" && args[2] == "--expected-parent")
    {
        GitSnapshotPublisher.Publish(args[1], args[3]);
        Console.WriteLine("Verified two-version snapshot published atomically with bounded root history.");
        return;
    }
    if (args.SequenceEqual(new[] { "--self-test" }, StringComparer.Ordinal))
    {
        SnapshotTests.Run();
        Console.WriteLine("PASS: two-version SHA-256 verified distribution snapshot and retention tests.");
        return;
    }

    var options = Parse(args);
    options.TryGetValue("previous-snapshot-dir", out var previous);
    var result = DistributionSnapshot.Prepare(
        Required(options, "new-package-dir"),
        previous,
        Required(options, "output-dir"));
    Console.WriteLine("Verified private Git distribution snapshot: latest " + result.LatestVersion +
        ", previous " + (result.PreviousVersion ?? "(none)") +
        ". Source packages remain untouched. Nothing was published or pushed.");
}
catch (Exception exception)
{
    Console.Error.WriteLine("UPDATE SNAPSHOT REJECTED: " + exception.Message);
    Environment.ExitCode = 1;
}

static Dictionary<string, string> Parse(string[] args)
{
    if (args.Length == 0 || args.Length % 2 != 0)
        throw new ArgumentException("Use --new-package-dir --output-dir " +
            "[--previous-snapshot-dir] or --self-test.");
    var found = new Dictionary<string, string>(StringComparer.Ordinal);
    for (int i = 0; i < args.Length; i += 2)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal) ||
            !new[] { "new-package-dir", "previous-snapshot-dir", "output-dir" }.Contains(args[i][2..], StringComparer.Ordinal) ||
            !found.TryAdd(args[i][2..], args[i + 1]))
            throw new ArgumentException("Invalid or duplicate distribution option.");
    }
    return found;
}

static string Required(Dictionary<string, string> args, string name) =>
    args.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value : throw new ArgumentException("Missing --" + name);

internal sealed class UpdateChannel
{
    public string Format { get; set; } = "fmsc-git-updates/v1";
    public string LatestVersion { get; set; } = "";
    public string? PreviousVersion { get; set; }
}

internal static class DistributionSnapshot
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true
    };

    internal static UpdateChannel Prepare(string newPackageDirectory,
        string? previousSnapshotDirectory, string outputDirectory)
    {
        string package = Path.GetFullPath(newPackageDirectory);
        string output = Path.GetFullPath(outputDirectory);
        string? previous = previousSnapshotDirectory is null ? null : Path.GetFullPath(previousSnapshotDirectory);
        if (!Directory.Exists(package) || (previous is not null && !Directory.Exists(previous)))
            throw new DirectoryNotFoundException("Release package or previously published snapshot directory is missing.");
        if (Directory.Exists(output) || File.Exists(output) ||
            Overlaps(output, package) || (previous is not null && Overlaps(output, previous)))
            throw new InvalidOperationException("Output must be a new independent snapshot directory.");
        var current = ReadCheckedPackage(package);
        string? priorVersion = null;
        string? priorPackage = null;
        if (previous is not null)
        {
            string pointer = Path.Combine(previous, "channel", "stable.json");
            if (!File.Exists(pointer))
                throw new InvalidDataException("Previous distribution snapshot has no stable channel pointer.");
            byte[] pointerBytes = File.ReadAllBytes(pointer);
            if (pointerBytes.Length is < 1 or > 4096)
                throw new InvalidDataException("Invalid previous stable channel pointer.");
            var oldPointer = JsonSerializer.Deserialize<UpdateChannel>(pointerBytes, JsonOptions)
                ?? throw new InvalidDataException("Invalid previous update pointer.");
            if (oldPointer.Format != "fmsc-git-updates/v1" ||
                !IsSimpleVersion(oldPointer.LatestVersion) ||
                (oldPointer.PreviousVersion is not null &&
                 (!IsSimpleVersion(oldPointer.PreviousVersion) ||
                  UpdateManifestPolicy.CompareVersions(oldPointer.LatestVersion, oldPointer.PreviousVersion) <= 0)))
                throw new InvalidDataException("Previous distribution pointer is invalid.");
            priorVersion = oldPointer.LatestVersion;
            priorPackage = Path.Combine(previous, "versions", priorVersion);
            var prior = ReadCheckedPackage(priorPackage);
            if (prior.Version != priorVersion ||
                UpdateManifestPolicy.CompareVersions(current.Version, priorVersion) <= 0)
                throw new InvalidDataException("New approved version must be newer than the previous published version.");
            if (current.Incremental is not null &&
                UpdateManifestPolicy.CompareVersions(current.BaseVersion!, priorVersion) != 0)
                throw new InvalidDataException("Incremental release must be built against the retained previous version.");
        }

        var snapshot = new UpdateChannel
        {
            LatestVersion = current.Version,
            PreviousVersion = priorVersion
        };
        bool created = false;
        try
        {
            Directory.CreateDirectory(output);
            created = true;
            string versions = Path.Combine(output, "versions");
            Directory.CreateDirectory(versions);
            if (priorPackage is not null && priorVersion is not null)
                CopyVerifiedPackage(priorPackage, Path.Combine(versions, priorVersion));
            CopyVerifiedPackage(package, Path.Combine(versions, current.Version));
            string channelDir = Path.Combine(output, "channel");
            Directory.CreateDirectory(channelDir);
            // A distribution publisher must switch the Git branch in one atomic commit.
            // Never upload a half-complete manifest or promote a partial snapshot.
            File.WriteAllBytes(Path.Combine(channelDir, "stable.json"),
                JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions));
            File.WriteAllText(Path.Combine(output, "README.md"),
                "# FMSC private update distribution\n\n" +
                "Private Git distribution. Current tree contains at most two approved versions.\n" +
                "Do not place school databases, application source or private credentials here.\n");
            ValidateSnapshot(output);
            return snapshot;
        }
        catch
        {
            if (created && Directory.Exists(output))
                Directory.Delete(output, recursive: true);
            throw;
        }
    }

    internal static void ValidateSnapshot(string root)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "README.md", "channel/stable.json" };
        string pointer = Path.Combine(root, "channel", "stable.json");
        var snapshot = JsonSerializer.Deserialize<UpdateChannel>(
            File.ReadAllBytes(pointer), JsonOptions)
            ?? throw new InvalidDataException("Snapshot pointer missing.");
        if (snapshot.Format != "fmsc-git-updates/v1" ||
            !IsSimpleVersion(snapshot.LatestVersion))
            throw new InvalidDataException("Unrecognized snapshot format.");
        string versionsDir = Path.Combine(root, "versions");
        var versions = Directory.GetDirectories(versionsDir)
            .Select(path => Path.GetFileName(path) ?? throw new InvalidDataException("Invalid version directory.")).ToArray();
        if (versions.Length is < 1 or > 2 ||
            !versions.Contains(snapshot.LatestVersion, StringComparer.Ordinal) ||
            (snapshot.PreviousVersion is null
                ? versions.Length != 1
                : versions.Length != 2 || !versions.Contains(snapshot.PreviousVersion, StringComparer.Ordinal)))
            throw new InvalidDataException("Only the newest one or two approved versions may appear in the snapshot.");
        foreach (string version in versions)
        {
            var manifest = ReadCheckedPackage(Path.Combine(versionsDir, version));
            if (manifest.Version != version)
                throw new InvalidDataException("Release version does not match its snapshot folder.");
            allowed.Add("versions/" + version + "/update-manifest.json");
            foreach (var archive in new[] { manifest.Full, manifest.Incremental }.OfType<UpdateArchive>())
                foreach (var part in archive.Parts) allowed.Add("versions/" + version + "/" + part.Name);
        }
        foreach (string path in Directory.GetFileSystemEntries(root))
            if (Path.GetFileName(path) != ".git") RejectUnexpected(path);
        void RejectUnexpected(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Links are prohibited in snapshots.");
            if (Directory.Exists(path))
                foreach (string child in Directory.GetFileSystemEntries(path)) RejectUnexpected(child);
            else if (!allowed.Contains(Path.GetRelativePath(root, path).Replace('\\', '/')))
                throw new InvalidDataException("Unexpected file in distribution snapshot: " + Path.GetFileName(path));
        }
        if (snapshot.PreviousVersion is not null &&
            UpdateManifestPolicy.CompareVersions(snapshot.LatestVersion, snapshot.PreviousVersion) <= 0)
            throw new InvalidDataException("Stable channel version order is invalid.");
    }

    private static UpdateManifest ReadCheckedPackage(string directory)
    {
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException("Update release is missing.");
        foreach (var path in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories))
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Symbolic links are prohibited in release packages.");

        string manifestPath = Path.Combine(directory, "update-manifest.json");
        if (!File.Exists(manifestPath))
            throw new InvalidDataException("Update manifest is missing.");
        var manifest = UpdateManifestPolicy.ReadValidated(File.ReadAllBytes(manifestPath));
        VerifyParts(directory, manifest.Full);
        if (manifest.Incremental is not null) VerifyParts(directory, manifest.Incremental);
        return manifest;
    }

    private static void VerifyParts(string directory, UpdateArchive archive)
    {
        UpdateManifestPolicy.ValidateArchive(archive);
        using var fullArchiveHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        foreach (var part in archive.Parts)
        {
            string path = Path.Combine(directory, part.Name);
            var info = new FileInfo(path);
            if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                info.Length != part.Length ||
                !StringComparer.OrdinalIgnoreCase.Equals(UpdateChunks.Sha256(path), part.Sha256))
                throw new InvalidDataException("Update archive part failed SHA-256 verification: " + part.Name);
            using var source = info.OpenRead();
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                fullArchiveHash.AppendData(buffer, 0, read);
        }
        string reconstructedDigest = Convert.ToHexString(fullArchiveHash.GetHashAndReset());
        if (!StringComparer.OrdinalIgnoreCase.Equals(reconstructedDigest, archive.Sha256))
            throw new InvalidDataException("The combined ZIP hash does not match the manifest's full-archive digest.");
    }

    private static void CopyVerifiedPackage(string inputDir, string destination)
    {
        var manifest = ReadCheckedPackage(inputDir);
        Directory.CreateDirectory(destination);
        foreach (string metadata in new[] { "update-manifest.json" })
            File.Copy(Path.Combine(inputDir, metadata), Path.Combine(destination, metadata));
        foreach (var archive in new[] { manifest.Full, manifest.Incremental }.OfType<UpdateArchive>())
            foreach (var part in archive.Parts)
                File.Copy(Path.Combine(inputDir, part.Name), Path.Combine(destination, part.Name));
        // Prevent the unchunked, potentially >100MiB ZIP being committed to Git.
        // Re-verify the copied metadata and every new part before promotion.
        ReadCheckedPackage(destination);
    }

    private static bool IsSimpleVersion(string? version)
    {
        if (string.IsNullOrEmpty(version) || version.Length > 100 ||
            version.Contains('/') || version.Contains('\\') || version.Contains(':') ||
            version == "." || version == "..")
            return false;
        try
        {
            return UpdateManifestPolicy.CompareVersions(version, version) == 0;
        }
        catch (FormatException) { return false; }
        catch (OverflowException) { return false; }
    }

    private static bool Overlaps(string first, string second)
    {
        string a = Path.TrimEndingDirectorySeparator(first);
        string b = Path.TrimEndingDirectorySeparator(second);
        return a.Equals(b, StringComparison.OrdinalIgnoreCase) ||
            a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            b.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}

internal static class SnapshotTests
{
    internal static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "fmsc-snapshot-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string first = CreateDummyPackage(root, "1.0.0");
            var initial = DistributionSnapshot.Prepare(first, null, Path.Combine(root, "snapshot1"));
            Require(initial.LatestVersion == "1.0.0" && initial.PreviousVersion is null, "First snapshot must retain one release.");
            string next = CreateDummyPackage(root, "1.1.0");
            var second = DistributionSnapshot.Prepare(next, Path.Combine(root, "snapshot1"),
                Path.Combine(root, "snapshot2"));
            Require(second.PreviousVersion == "1.0.0", "Second snapshot must retain the previous release.");
            string third = CreateDummyPackage(root, "1.2.0");
            var thirdResult = DistributionSnapshot.Prepare(third, Path.Combine(root, "snapshot2"),
                Path.Combine(root, "snapshot3"));
            Require(thirdResult.LatestVersion == "1.2.0" && thirdResult.PreviousVersion == "1.1.0",
                "Third snapshot must keep only the newest two versions.");
            Require(!Directory.Exists(Path.Combine(root, "snapshot3", "versions", "1.0.0")),
                "Third version must prune the first version from the published tree.");
            Require(Directory.GetDirectories(Path.Combine(root, "snapshot3", "versions")).Length == 2,
                "Published Git snapshot must contain exactly two versions.");
            Require(!Directory.EnumerateFiles(Path.Combine(root, "snapshot3"), "*.zip", SearchOption.AllDirectories).Any(),
                "Never include unchunked full ZIPs in Git distribution.");
            TestPublication(root);
            string corrupt = Path.Combine(root, "snapshot3", "versions", "1.2.0",
                UpdateChunks.MakeName("FMSC_SMS-app-full.zip", 1));
            File.WriteAllText(corrupt, "tampered download");
            try
            {
                DistributionSnapshot.ValidateSnapshot(Path.Combine(root, "snapshot3"));
                throw new Exception("Tampered published package passed validation.");
            }
            catch (InvalidDataException) { }
            Require(File.Exists(Path.Combine(root, "snapshot2", "versions", "1.0.0", "update-manifest.json")),
                "Snapshot preparation must not modify its predecessor.");
        }
        finally
        {
            // Git loose objects are read-only on Windows, including in disposable fixtures.
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, recursive: true);
        }
    }

    private static void TestPublication(string root)
    {
        string remote = Path.Combine(root, "remote.git");
        Directory.CreateDirectory(remote);
        GitSnapshotPublisher.Git(remote, "init", "--bare", "--initial-branch=main");
        string seed = Path.Combine(root, "seed");
        Directory.CreateDirectory(seed);
        GitSnapshotPublisher.Git(seed, "init", "--initial-branch=main");
        Identity(seed);
        File.WriteAllText(Path.Combine(seed, "README.md"), "Disposable distribution");
        GitSnapshotPublisher.Git(seed, "add", ".");
        GitSnapshotPublisher.Git(seed, "commit", "-m", "Initialize fixture");
        GitSnapshotPublisher.Git(seed, "push", remote, "HEAD:main");
        string expected = GitSnapshotPublisher.Git(seed, "rev-parse", "HEAD").Trim();
        string publication = Path.Combine(root, "publication");
        GitSnapshotPublisher.Prepare(Path.Combine(root, "snapshot1"), publication, remote);
        Require(GitSnapshotPublisher.Git(remote, "rev-parse", "main").Trim() == expected,
            "Review preparation must not publish.");
        Identity(publication);
        GitSnapshotPublisher.Publish(publication, expected, remote);
        Require(GitSnapshotPublisher.Git(remote, "show", "main:channel/stable.json") ==
            File.ReadAllText(Path.Combine(root, "snapshot1", "channel", "stable.json")),
            "Publication must preserve the exact verified metadata bytes without newline conversion.");
        string published = GitSnapshotPublisher.Git(remote, "rev-parse", "main").Trim();
        Require(published != expected && GitSnapshotPublisher.Git(remote, "rev-list", "--count", "main").Trim() == "1",
            "Publication must advance atomically with no accumulated Git ancestry.");
        string stale = Path.Combine(root, "stale-publication");
        GitSnapshotPublisher.Prepare(Path.Combine(root, "snapshot2"), stale, remote);
        Identity(stale);
        string winner = Path.Combine(root, "winner-publication");
        GitSnapshotPublisher.Prepare(Path.Combine(root, "snapshot2"), winner, remote);
        Identity(winner);
        GitSnapshotPublisher.Publish(winner, published, remote);
        string winnerHead = GitSnapshotPublisher.Git(remote, "rev-parse", "main").Trim();
        // Make a different competing commit (otherwise identical trees/messages can
        // yield the same SHA when tests execute in the same second).
        GitSnapshotPublisher.Git(stale, "config", "user.name", "Competing owner");
        try { GitSnapshotPublisher.Publish(stale, published, remote); throw new Exception("Concurrent publication accepted."); }
        catch (Exception error) when (error is IOException or InvalidDataException) { }
        Require(GitSnapshotPublisher.Git(remote, "rev-parse", "main").Trim() == winnerHead,
            "Rejected concurrent publication must leave the winning feed intact.");
        Require(GitSnapshotPublisher.Git(remote, "rev-list", "--count", "main").Trim() == "1",
            "Repeated publications must not accumulate historical commits.");
        string nextPublication = Path.Combine(root, "third-publication");
        GitSnapshotPublisher.Prepare(Path.Combine(root, "snapshot3"), nextPublication, remote);
        Identity(nextPublication);
        GitSnapshotPublisher.Publish(nextPublication, winnerHead, remote);
        Require(GitSnapshotPublisher.Git(remote, "rev-list", "--count", "main").Trim() == "1",
            "Third publication must preserve a bounded root history.");
        string tree = GitSnapshotPublisher.Git(remote, "ls-tree", "-r", "--name-only", "main");
        Require(!tree.Contains("versions/1.0.0/", StringComparison.Ordinal) &&
            tree.Contains("versions/1.1.0/update-manifest.json", StringComparison.Ordinal) &&
            tree.Contains("versions/1.2.0/update-manifest.json", StringComparison.Ordinal),
            "Published branch must contain exactly the two latest release trees.");
        // A pre-rotation school still reads the latest full release using its
        // existing shallow-clone transport, without needing old commit IDs.
        string school = Path.Combine(root, "school-readonly");
        GitSnapshotPublisher.Git(root, "clone", "--quiet", "--no-checkout", "--depth=1",
            "--single-branch", "--branch", "main", remote, school);
        Require(GitSnapshotPublisher.Git(school, "show", "HEAD:channel/stable.json") ==
            File.ReadAllText(Path.Combine(root, "snapshot3", "channel", "stable.json")),
            "Existing shallow-clone clients must discover the newest channel.");
        string part = UpdateChunks.MakeName("FMSC_SMS-app-full.zip", 1);
        Require(GitSnapshotPublisher.Git(school, "cat-file", "-s", "HEAD:versions/1.2.0/" + part).Trim() == "512",
            "Older installed clients must be able to fetch the latest full update.");
        // History compaction cannot succeed while an extra ref still pins old objects.
        GitSnapshotPublisher.Git(remote, "tag", "unreviewed-history", "main");
        try
        {
            GitSnapshotPublisher.Prepare(Path.Combine(root, "snapshot3"),
                Path.Combine(root, "tagged-publication"), remote);
            throw new Exception("An unreviewed distribution tag was accepted.");
        }
        catch (InvalidDataException) { }
        File.WriteAllText(Path.Combine(root, "snapshot2", "unexpected.secret"), "fixture");
        try { DistributionSnapshot.ValidateSnapshot(Path.Combine(root, "snapshot2")); throw new Exception("Unlisted file accepted."); }
        catch (InvalidDataException) { }
        File.Delete(Path.Combine(root, "snapshot2", "unexpected.secret"));
        static void Identity(string directory)
        {
            GitSnapshotPublisher.Git(directory, "config", "user.name", "FMSC offline test");
            GitSnapshotPublisher.Git(directory, "config", "user.email", "fixture@example.invalid");
        }
    }

    private static string CreateDummyPackage(string root, string version)
    {
        string folder = Path.Combine(root, "package-" + version);
        Directory.CreateDirectory(folder);
        string zip = Path.Combine(folder, "FMSC_SMS-app-full.zip");
        File.WriteAllBytes(zip, RandomNumberGenerator.GetBytes(2048));
        var manifest = new UpdateManifest
        {
            Version = version,
            RequiredDatabaseSchema = 75,
            Files = new List<UpdateFile>
            {
                new() { Path = "FMSC_SMS.exe", Length = 4, Sha256 = new string('a', 64) }
            },
            Full = new UpdateArchive
            {
                Name = "FMSC_SMS-app-full.zip",
                Length = new FileInfo(zip).Length,
                Sha256 = UpdateChunks.Sha256(zip),
                Parts = UpdateChunks.SplitArchive(zip, maximumPartBytes: 512)
            }
        };
        byte[] raw = UpdateManifestPolicy.Serialize(manifest);
        File.WriteAllBytes(Path.Combine(folder, "update-manifest.json"), raw);
        return folder;
    }

    private static void Require(bool assertion, string reason)
    {
        if (!assertion) throw new Exception(reason);
    }
}
