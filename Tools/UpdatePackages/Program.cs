using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using FMSC_SMS.Core.Updates;

try
{
    if (args.Length == 4 && args[0] == "--extract-release" && args[2] == "--output-dir")
    {
        Console.WriteLine("Verified baseline: " + UpdatePackageVerification.ExtractFull(args[1], args[3]));
        return;
    }
    if ((args.Length == 4 || args.Length == 6) && args[0] == "--verify-package" && args[2] == "--output-dir" &&
        (args.Length == 4 || args[4] == "--baseline-dir"))
    {
        UpdatePackageVerification.Verify(args[1], args.Length == 6 ? args[5] : null, args[3]);
        Console.WriteLine("PASS: package chunks, full ZIP, complete target and incremental reconstruction verified.");
        return;
    }
    if (args.Length == 1 && args[0] == "--self-test")
    {
        SelfTest.Run();
        UpdatePreparationTests.Run();
        UpdateDownloadTests.Run();
        Console.WriteLine("PASS: SHA-256 verified incremental/full update packaging and fallback checks.");
        return;
    }

    var options = Parse(args);
    var current = Required(options, "current-dir");
    var output = Required(options, "output-dir");
    var version = Required(options, "version");
    var schema = int.Parse(Required(options, "schema"));
    options.TryGetValue("previous-dir", out string? previous);
    options.TryGetValue("previous-version", out string? previousVersion);
    if ((previous is null) != (previousVersion is null))
        throw new ArgumentException("previous-dir and previous-version must be supplied together.");
    ValidateReleaseInput(current, version, schema);
    var manifest = PackageBuilder.Build(current, output, version, schema, previous, previousVersion);
    Console.WriteLine($"Prepared application update {manifest.Version}; " +
        $"delta: {(manifest.Incremental is null ? "unavailable" : manifest.ChangedFiles.Count + " changed files")}. " +
        "Publishing, authorization and application activation are separate release gates.");
}
catch (Exception error)
{
    Console.Error.WriteLine("UPDATE PACKAGING FAILED: " + error.Message);
    Environment.ExitCode = 1;
}

static Dictionary<string, string> Parse(string[] args)
{
    if (args.Length == 0 || args.Length % 2 != 0)
        throw new ArgumentException("Expected --current-dir, --output-dir, --version, --schema; " +
            "optional --previous-dir and --previous-version. Or --self-test.");
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    for (int i = 0; i < args.Length; i += 2)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal) ||
            args[i].Length < 3 || !result.TryAdd(args[i][2..], args[i + 1]))
            throw new ArgumentException("Invalid or duplicate update packaging option.");
    }
    var known = new HashSet<string>(new[] { "current-dir", "output-dir", "version",
        "schema", "previous-dir", "previous-version" }, StringComparer.Ordinal);
    if (result.Keys.Any(key => !known.Contains(key)))
        throw new ArgumentException("Unknown update packaging option.");
    return result;
}

static string Required(Dictionary<string, string> args, string key) =>
    args.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value : throw new ArgumentException("Missing --" + key);

static void ValidateReleaseInput(string current, string version, int schema)
{
    string script = File.ReadAllText(Path.Combine(current, "UpdateSupport", "core.ps1"));
    string declaredVersion = System.Text.RegularExpressions.Regex.Match(script,
        @"(?m)^\$AppVersion = '([^']+)'\r?$").Groups[1].Value;
    string declaredSchema = System.Text.RegularExpressions.Regex.Match(script,
        @"(?m)^\$DatabaseVersion = '([0-9]+)'\r?$").Groups[1].Value;
    string actualVersion = System.Diagnostics.FileVersionInfo.GetVersionInfo(Path.Combine(current, "FMSC_SMS.dll"))
        .ProductVersion?.Split('+')[0] ?? "";
    if (version != actualVersion || version != declaredVersion || schema.ToString() != declaredSchema)
        throw new InvalidDataException("Requested version/schema does not match the published application and installer.");
    string migrations = Path.Combine(current, "UpdateSupport", "Migrations");
    if (schema > 1 && (!Directory.Exists(migrations) || !Directory.EnumerateFiles(migrations, "*.sql").Any()))
        throw new InvalidDataException("Numbered installer migrations are missing from the published update.");
}

internal static class PackageBuilder
{
    internal static UpdateManifest Build(string currentDir, string outputDir, string version,
        int schema, string? previousDir, string? previousVersion)
    {
        var current = Path.GetFullPath(currentDir);
        var output = Path.GetFullPath(outputDir);
        if (!Directory.Exists(current))
            throw new DirectoryNotFoundException("Current published application folder was not found.");
        if (IsWithin(output, current) || IsWithin(current, output))
            throw new ArgumentException("The output folder cannot overlap the application input.");
        if (previousDir is not null && IsWithin(output, Path.GetFullPath(previousDir)))
            throw new ArgumentException("The output folder cannot overlap the previous application.");
        var currentFiles = Inventory(current);
        var baseFiles = previousDir is null ? new List<UpdateFile>() : Inventory(previousDir);
        var changed = previousDir is null ? new List<string>() :
            UpdateManifestPolicy.FindChanged(baseFiles, currentFiles);
        var manifest = new UpdateManifest
        {
            Version = version,
            BaseVersion = previousVersion,
            RequiredDatabaseSchema = schema,
            Files = currentFiles,
            BaseFiles = baseFiles,
            ChangedFiles = changed
        };
        Directory.CreateDirectory(output);
        const string fullName = "FMSC_SMS-app-full.zip";
        CreateZip(current, currentFiles.Select(f => f.Path), Path.Combine(output, fullName));
        manifest.Full = Archive(output, fullName);
        manifest.Full.Parts = UpdateChunks.SplitArchive(Path.Combine(output, fullName));

        if (previousDir is not null)
        {
            const string deltaName = "FMSC_SMS-app-incremental.zip";
            CreateZip(current, changed, Path.Combine(output, deltaName));
            manifest.Incremental = Archive(output, deltaName);
            manifest.Incremental.Parts = UpdateChunks.SplitArchive(Path.Combine(output, deltaName));
        }
        byte[] data = UpdateManifestPolicy.Serialize(manifest);
        File.WriteAllBytes(Path.Combine(output, "update-manifest.json"), data);
        return manifest;
    }

    private static bool IsWithin(string path, string folder)
    {
        string prefix = Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar;
        return string.Equals(path, folder, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static List<UpdateFile> Inventory(string directory)
    {
        string root = Path.GetFullPath(directory);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var list = new List<UpdateFile>();
        foreach (string path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Symbolic links and reparse points are prohibited in update packages.");
            if (!File.Exists(path)) continue;
            string name = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (!UpdateManifestPolicy.IsSafeRelativePath(name))
                throw new InvalidDataException("Unsafe application file path: " + name);
            string ext = Path.GetExtension(name);
            bool forbidden = new[] { ".mdf", ".ldf", ".bak", ".pfx", ".p12", ".pem", ".key" }
                .Contains(ext, StringComparer.OrdinalIgnoreCase) ||
                Path.GetFileName(name).StartsWith(".env", StringComparison.OrdinalIgnoreCase);
            // The pinned MinGit transport contains a public CA bundle, not a key.
            // Never relax the denylist for any other PEM file or non-certificate data.
            if (forbidden && !(ext.Equals(".pem", StringComparison.OrdinalIgnoreCase) &&
                IsAllowedBundledGitCaBundle(name, path)))
                throw new InvalidDataException("A database or secret-like file was found in the application package: " + name);
            var file = new FileInfo(path);
            list.Add(new UpdateFile { Path = name, Length = file.Length, Sha256 = Hash(path) });
        }
        list.Sort((a, b) => StringComparer.Ordinal.Compare(a.Path, b.Path));
        if (list.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != list.Count)
            throw new InvalidDataException("Application contains paths that collide on Windows.");
        return list;
    }

    // Only MinGit's known CA trust-store path can contain public certificates.
    // Parse every certificate rather than trusting the extension or filename.
    private static bool IsAllowedBundledGitCaBundle(string name, string file)
    {
        if (!string.Equals(name, "UpdateTransport/ucrt64/etc/ssl/cert.pem", StringComparison.Ordinal))
            return false;
        long size = new FileInfo(file).Length;
        if (size <= 0 || size > 2 * 1024 * 1024)
            return false;
        bool insideCertificate = false;
        bool foundCertificate = false;
        var base64 = new StringBuilder();
        try
        {
            foreach (string raw in File.ReadLines(file))
            {
                string line = raw.Trim();
                if (!insideCertificate)
                {
                    if (line.Length == 0 || line.StartsWith('#')) continue;
                    if (line != "-----BEGIN CERTIFICATE-----") return false;
                    insideCertificate = true;
                    base64.Clear();
                }
                else if (line == "-----END CERTIFICATE-----")
                {
                    using var certificate = new X509Certificate2(Convert.FromBase64String(base64.ToString()));
                    if (certificate.HasPrivateKey) return false;
                    foundCertificate = true;
                    insideCertificate = false;
                }
                else
                {
                    if (line.Length == 0 || line.Any(c =>
                        !(char.IsAsciiLetterOrDigit(c) || c == '+' || c == '/' || c == '=')))
                        return false;
                    base64.Append(line);
                }
            }
            return foundCertificate && !insideCertificate;
        }
        catch (CryptographicException) { return false; }
        catch (FormatException) { return false; }
        catch (IOException) { return false; }
    }

    private static void CreateZip(string root, IEnumerable<string> paths, string destination)
    {
        using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (string path in paths.OrderBy(path => path, StringComparer.Ordinal))
        {
            var item = zip.CreateEntry(path, CompressionLevel.Optimal);
            item.LastWriteTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var source = File.OpenRead(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
            using var target = item.Open();
            source.CopyTo(target);
        }
    }

    private static UpdateArchive Archive(string root, string name)
    {
        var path = Path.Combine(root, name);
        return new UpdateArchive { Name = name, Length = new FileInfo(path).Length, Sha256 = Hash(path) };
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

internal static class SelfTest
{
    internal static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "fmsc-update-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string oldDir = Path.Combine(root, "old");
            string newDir = Path.Combine(root, "new");
            string output = Path.Combine(root, "out");
            Directory.CreateDirectory(oldDir);
            Directory.CreateDirectory(newDir);
            File.WriteAllText(Path.Combine(oldDir, "FMSC_SMS.exe"), "exe");
            File.WriteAllText(Path.Combine(newDir, "FMSC_SMS.exe"), "exe");
            File.WriteAllText(Path.Combine(oldDir, "changed.dll"), "before");
            File.WriteAllText(Path.Combine(newDir, "changed.dll"), "after");
            File.WriteAllText(Path.Combine(oldDir, "removed.dll"), "remove me");
            File.WriteAllText(Path.Combine(newDir, "added.dll"), "new file");
            var manifest = PackageBuilder.Build(newDir, output, "1.1.0", 75, oldDir, "1.1.0-rc.2");
            byte[] bytes = File.ReadAllBytes(Path.Combine(output, "update-manifest.json"));
            var valid = UpdateManifestPolicy.ReadValidated(bytes);
            // The manifest records the length and digest of every Git-compatible part and the ZIP.
            Require(valid.Full.Parts.Count == 1 && valid.Incremental!.Parts.Count == 1,
                "Ordinary small updates must still carry individually hashed part inventories.");
            string tinyPartsFolder = Path.Combine(root, "tiny-parts");
            Directory.CreateDirectory(tinyPartsFolder);
            string smallZip = Path.Combine(tinyPartsFolder, "test.zip");
            File.WriteAllBytes(smallZip, RandomNumberGenerator.GetBytes(177));
            var manyParts = UpdateChunks.SplitArchive(smallZip, maximumPartBytes: 40);
            Require(manyParts.Count == 5 && manyParts.All(part => part.Length <= 40),
                "Larger archives must be split into Git-compatible bounded parts.");
            var descriptor = new UpdateArchive
            {
                Name = "test.zip",
                Length = new FileInfo(smallZip).Length,
                Sha256 = UpdateChunks.Sha256(smallZip),
                Parts = manyParts
            };
            string rebuilt = Path.Combine(root, "rebuilt.zip");
            UpdateChunks.ReassembleVerified(descriptor, tinyPartsFolder, rebuilt);
            Require(File.ReadAllBytes(rebuilt).SequenceEqual(File.ReadAllBytes(smallZip)),
                "Hash-checked archive parts must reconstruct a byte-identical download.");
            string damagedPart = Path.Combine(tinyPartsFolder, manyParts[2].Name);
            File.WriteAllBytes(damagedPart, RandomNumberGenerator.GetBytes((int)manyParts[2].Length));
            try
            {
                UpdateChunks.ReassembleVerified(descriptor, tinyPartsFolder,
                    Path.Combine(root, "tampered-rebuild.zip"));
                throw new Exception("Tampered chunk was accepted.");
            }
            catch (InvalidDataException) { }
            Require(!File.Exists(Path.Combine(root, "tampered-rebuild.zip")),
                "Reconstruction must not publish an unverified target ZIP.");
            Require(valid.ChangedFiles.SequenceEqual(new[] { "added.dll", "changed.dll" }), "Only changed files belong in the delta.");
            using (var zip = ZipFile.OpenRead(Path.Combine(output, valid.Incremental!.Name)))
                Require(zip.Entries.Select(x => x.FullName).SequenceEqual(valid.ChangedFiles), "Delta ZIP contents mismatch.");
            using (var zip = ZipFile.OpenRead(Path.Combine(output, valid.Full.Name)))
                Require(zip.Entries.Count == 3, "Full ZIP must contain the complete new application.");
            string stagedDelta = Path.Combine(root, "staged-delta");
            UpdateStager.Prepare(valid, Path.Combine(output, valid.Incremental!.Name),
                oldDir, stagedDelta, incremental: true);
            Require(File.ReadAllText(Path.Combine(stagedDelta, "changed.dll")) == "after" &&
                File.ReadAllText(Path.Combine(stagedDelta, "FMSC_SMS.exe")) == "exe" &&
                File.Exists(Path.Combine(stagedDelta, "added.dll")) &&
                !File.Exists(Path.Combine(stagedDelta, "removed.dll")),
                "Incremental staging must reconstruct the complete target without obsolete files.");
            Require(File.ReadAllText(Path.Combine(oldDir, "changed.dll")) == "before",
                "The running installation must remain unchanged during staging.");
            string stagedFull = Path.Combine(root, "staged-full");
            UpdateStager.Prepare(valid, Path.Combine(output, valid.Full.Name),
                oldDir, stagedFull, incremental: false);
            Require(File.ReadAllText(Path.Combine(stagedFull, "changed.dll")) == "after",
                "Full fallback must reconstruct the target application.");
            File.WriteAllText(Path.Combine(oldDir, "changed.dll"), "tampered base");
            try
            {
                UpdateStager.Prepare(valid, Path.Combine(output, valid.Incremental.Name),
                    oldDir, Path.Combine(root, "refused-stage"), incremental: true);
                throw new Exception("Tampered installed base was accepted for delta staging.");
            }
            catch (InvalidDataException) { }
            Require(!Directory.Exists(Path.Combine(root, "refused-stage")),
                "Failed preflight must not create an activation directory.");
            Require(UpdateManifestPolicy.Choose(valid, "1.1.0-rc.2", 75,
                valid.BaseFiles.ToDictionary(f => f.Path, f => f.Sha256, StringComparer.OrdinalIgnoreCase))
                == UpdateDownloadChoice.Incremental, "Clean base must select delta.");
            var corrupted = valid.BaseFiles.ToDictionary(f => f.Path, f => f.Sha256, StringComparer.OrdinalIgnoreCase);
            corrupted["changed.dll"] = new string('0', 64);
            Require(UpdateManifestPolicy.Choose(valid, "1.1.0-rc.2", 75, corrupted)
                == UpdateDownloadChoice.Full, "Changed installed files must trigger full fallback.");
            Require(UpdateManifestPolicy.Choose(valid, "1.1.0-rc.2", 74, corrupted)
                == UpdateDownloadChoice.RequiresDatabaseInstaller, "Schema changes require the existing safe installer.");
            Require(UpdateManifestPolicy.Choose(valid, "1.1.0", 75, corrupted)
                == UpdateDownloadChoice.NotNewer, "Already updated installations must not update again.");
            // Invalid or truncated repository metadata must be rejected.
            // A repository writer could replace BOTH a manifest and its SHA-256
            // hashes: these checks provide integrity, not publisher authenticity.
            byte[] malformed = (byte[])bytes.Clone();
            malformed[0] = (byte)'!';
            try
            {
                _ = UpdateManifestPolicy.ReadValidated(malformed);
                throw new Exception("Malformed update metadata was accepted.");
            }
            catch (System.Text.Json.JsonException) { }
            foreach (string unsafePath in new[] { "../FMSC_SMS.exe", "/absolute", "C:/evil",
                "folder\\evil.exe", "CON.txt", "dir//evil" })
                Require(!UpdateManifestPolicy.IsSafeRelativePath(unsafePath),
                    "Unsafe archive path passed validation: " + unsafePath);

            // The transport's public CA bundle is not a private key. Only this
            // exact path with parseable certificates can bypass the PEM denylist.
            string gitCaFolder = Path.Combine(newDir, "UpdateTransport", "ucrt64", "etc", "ssl");
            Directory.CreateDirectory(gitCaFolder);
            string caFile = Path.Combine(gitCaFolder, "cert.pem");
            using var key = RSA.Create(2048);
            var certificateRequest = new CertificateRequest("CN=FMSC package test",
                key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var certificate = certificateRequest.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            string caBundle = "# Synthetic public trust store\n" + certificate.ExportCertificatePem();
            File.WriteAllText(caFile, caBundle);
            _ = PackageBuilder.Build(newDir, Path.Combine(root, "valid-git-ca"),
                "1.1.0", 75, null, null);
            void RejectUnsafePackage(string suffix, string reason)
            {
                bool rejected = false;
                try { _ = PackageBuilder.Build(newDir, Path.Combine(root, suffix),
                    "1.1.0", 75, null, null); }
                catch (InvalidDataException) { rejected = true; }
                Require(rejected, reason);
            }
            File.WriteAllText(caFile, caBundle + "-----BEGIN PRIVATE KEY-----\nYWJj\n-----END PRIVATE KEY-----\n");
            RejectUnsafePackage("reject-key-in-trust-store",
                "Private-key material must be refused even at the Git CA path.");
            File.WriteAllText(caFile, caBundle);
            File.WriteAllText(Path.Combine(newDir, "other.pem"), caBundle);
            RejectUnsafePackage("reject-other-pem", "Unapproved PEM files must be refused.");
            File.Delete(Path.Combine(newDir, "other.pem"));
            File.WriteAllText(Path.Combine(newDir, "operator.key"), "secret");
            RejectUnsafePackage("reject-key-file", "Private-key files must remain forbidden.");

        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Require(bool pass, string message)
    {
        if (!pass) throw new Exception(message);
    }
}
