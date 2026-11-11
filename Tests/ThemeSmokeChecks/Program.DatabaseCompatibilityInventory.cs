using FMSC_SMS.Core;

internal static partial class Program
{
    // A new schema compatibility JSON must be included in the executable
    // automatically, and the disposable SQL installer must validate it too.
    // Compare live source inventory and actual embedded bytes, not a frozen
    // list of historical schema numbers.
    private static void CheckCompatibilityManifestInventory()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null &&
               !File.Exists(Path.Combine(root.FullName, "FMSC_SMS.csproj")))
            root = root.Parent;
        if (root is null)
            throw new Exception("Cannot locate FMSC source for contract inventory check.");

        string[] sourceFiles = Directory.GetFiles(Path.Combine(root.FullName, "Core"),
            "DatabaseCompatibility*Requirements.json");
        string[] expected = sourceFiles.Select(file =>
                "FMSC_SMS.Core." + Path.GetFileName(file))
            .OrderBy(name => name, StringComparer.Ordinal).ToArray();
        var assembly = typeof(V2Page).Assembly;
        string[] embedded = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith("FMSC_SMS.Core.DatabaseCompatibility",
                    StringComparison.Ordinal) &&
                name.EndsWith("Requirements.json", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal).ToArray();

        Check(expected.Length > 0 && expected.Contains(
                "FMSC_SMS.Core.DatabaseCompatibilityRequirements.json"),
            "Core compatibility manifest remains present");
        Check(expected.SequenceEqual(embedded, StringComparer.Ordinal),
            "Every current and future SQL compatibility contract is embedded once");

        foreach (string file in sourceFiles)
        {
            string resource = "FMSC_SMS.Core." + Path.GetFileName(file);
            using Stream stream = assembly.GetManifestResourceStream(resource)
                ?? throw new Exception("Missing embedded SQL contract " + resource);
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            Check(File.ReadAllBytes(file).SequenceEqual(bytes.ToArray()),
                "Embedded SQL contract matches source bytes: " + Path.GetFileName(file));
        }
    }
}
