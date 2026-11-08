using System.Diagnostics;
using System.Text.Json;
using FMSC_SMS.Pages.Help;

int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    checks++;
}
string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../")).TrimEnd(Path.DirectorySeparatorChar);
// The root fmsc.dacpac is an optional manual inspection file. CI schema
// evidence comes from the production structural compatibility manifest only.
using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "Core", "DatabaseCompatibilityRequirements.json")));
var sqlNames = new HashSet<string>(StringComparer.Ordinal);
foreach (var key in new[] { "objects", "uniqueIndexes", "checkConstraints", "triggers" })
    foreach (var entry in schema.RootElement.GetProperty(key).EnumerateArray())
    {
        var name = entry.GetProperty("name").GetString()!;
        var owner = entry.GetProperty("schema").GetString()!;
        sqlNames.Add(owner + "." + name);
    }
// Runtime validates the FK by relationship/columns, not its SQL Server name.
if (schema.RootElement.GetProperty("foreignKeys").EnumerateArray().Any(fk =>
    fk.GetProperty("schema").GetString() == "dbo" &&
    fk.GetProperty("objectName").GetString() == "FamilyMembers" &&
    fk.GetProperty("referencedObject").GetString() == "Students" &&
    fk.GetProperty("columns").GetString() == "AdmissionId" &&
    fk.GetProperty("referencedColumns").GetString() == "AdmissionId"))
    sqlNames.Add("dbo.FK_FamilyMembers_Student");
var entries = HelpCatalog.Entries;
Check(entries.Count >= 60, "Preserve the retained help questions");
Check(entries.Select(e => e.Id).Distinct().Count() == entries.Count, "Unique stable IDs");
Check(entries.Select(e => e.Question).Distinct(StringComparer.OrdinalIgnoreCase).Count() == entries.Count, "No duplicate questions");
Check(entries.Select(e => e.Category).Distinct().Count() == 14, "Retained topic coverage");
for (int i = 1; i <= 77; i++) if (i < 68 || i > 74) Check(entries.Any(e => e.Id == $"FAQ-{i:000}"), "Preserve retained question ID " + i);
foreach (var entry in entries)
{
    Check(new[] { entry.Id, entry.Question, entry.Answer, entry.Category, entry.Keywords, entry.WhereToGo }.All(s => !string.IsNullOrWhiteSpace(s)), entry.Id + " complete content");
    Check(entry.Rules.Count > 0 && entry.Rules.Distinct().Count() == entry.Rules.Count && entry.Rules.All(Enum.IsDefined), entry.Id + " rule metadata");
    Check(entry.Owners != 0 && entry.Sources.Count > 0, entry.Id + " owner/evidence");
    foreach (string source in entry.Sources)
    {
        string[] parts = source.Split('#');
        string path = Path.GetFullPath(Path.Combine(root, parts[0]));
        Check(path.StartsWith(root + Path.DirectorySeparatorChar) &&
            (parts[0] == "fmsc.dacpac" || File.Exists(path)),
            entry.Id + " existing bounded source " + source);
        if (parts.Length == 2) Check(parts[0] == "fmsc.dacpac"
            ? sqlNames.Contains("dbo." + parts[1]) || sqlNames.Contains(parts[1]) || sqlNames.Any(name => name.EndsWith("." + parts[1], StringComparison.Ordinal))
            : File.ReadAllText(path).Contains(parts[1], StringComparison.Ordinal), entry.Id + " source symbol " + source);
    }
    if (entry.Owners.HasFlag(HelpOwner.SQL)) Check(entry.Sources.Any(s => s.Contains(".sql") || s.StartsWith("fmsc.dacpac#", StringComparison.Ordinal)), entry.Id + " SQL ownership evidence");
    if (entry.Owners.HasFlag(HelpOwner.CSharp)) Check(entry.Sources.Any(s => s.Contains(".cs")), entry.Id + " C# ownership evidence");
    if (entry.Rules.Contains(HelpRule.Setting)) Check(entry.Owners.HasFlag(HelpOwner.ConfigurableData), entry.Id + " supported setting metadata");
    Check(entry.RelatedIds.Distinct().Count() == entry.RelatedIds.Count && !entry.RelatedIds.Contains(entry.Id), entry.Id + " no duplicate/self links");
    Check(entry.RelatedIds.All(id => entries.Any(e => e.Id == id)), entry.Id + " no broken links");
    var visited = new HashSet<string>();
    bool Acyclic(HelpEntry current, HashSet<string> path)
    {
        if (!path.Add(current.Id)) return false;
        foreach (var next in HelpCatalog.Related(current)) if (!Acyclic(next, path)) return false;
        path.Remove(current.Id); return true;
    }
    Check(Acyclic(entry, visited), entry.Id + " meaningful directed links without cycles");
    Check(HelpCatalog.Find(entry.Question, null).First().Id == entry.Id, entry.Id + " exact question ranks first");
}
void Contains(string query, params string[] ids)
{
    var results = HelpCatalog.Find(query, null).ToArray();
    foreach (string id in ids) Check(results.Any(e => e.Id == id), query + " finds " + id);
}
Contains("dues", "FAQ-043", "FAQ-125");
Contains("unpaid fees", "FAQ-043");
Contains("admisison number", "FAQ-022");
Contains("tution", "FAQ-085");
Contains("enrolment", "FAQ-091");
Contains("pending siblings", "FAQ-083");
Contains("family payment", "FAQ-101");
Contains("payment refresh", "FAQ-044");
Contains("correction unknown", "FAQ-103");
Contains("academic year dates", "FAQ-009");
Contains("stop transport", "FAQ-095");
Contains("school setting", "FAQ-009");
Contains("permission required", "FAQ-057");
Contains("LocalDB", "FAQ-065");
Contains("photo zoom", "FAQ-088");
Check(HelpCatalog.Find("stop transport", null).First().Id == "FAQ-095", "Title/keywords outrank incidental body matches");
Check(!HelpCatalog.Find("controller", null).Any(), "No role/controller substring confusion");
Check(!HelpCatalog.Find("zxqv school", null).Any(), "Unknown meaningful term is not discarded");
Check(!HelpCatalog.Find("", "Nonexistent topic").Any(), "Unknown category gives no results");
Check(HelpCatalog.Find(null, null).Count() == entries.Count, "Empty query has all entries");
Check(HelpCatalog.Find("  \t ", "All topics").Count() == entries.Count, "Whitespace query");
Check(HelpCatalog.Find("the and", null).Count() == entries.Count, "Stopwords alone do not invent relevance");
Check(HelpCatalog.Find("ＦＥＥ", null).Count() == 0, "No arbitrary fuzzy matching");
Check(!HelpCatalog.Find("fees", "Communication").Any(), "Retired communication topic is unavailable");
var recovery = HelpCatalog.Find("payment", "Collection", HelpRule.Recovery).ToArray();
Check(recovery.Length > 0 && recovery.All(e => e.Category == "Collection" && e.Rules.Contains(HelpRule.Recovery)), "Category, query and rule combine");
foreach (var rule in Enum.GetValues<HelpRule>())
    Check(HelpCatalog.Find(null, null, rule).All(e => e.Rules.Contains(rule)), "Rule filter " + rule);
Check(HelpCatalog.Find("REFUND", null).Select(e => e.Id).SequenceEqual(HelpCatalog.Find("refund", null).Select(e => e.Id)), "Case-insensitive stable ranking");
Check(HelpCatalog.Find("fees fees", null).Count() == HelpCatalog.Find("fees", null).Count(), "Repeated words do not change relevance");
foreach (string path in Directory.GetFiles(Path.Combine(root, "Pages/Help"), "*.cs"))
{
    string source = File.ReadAllText(path);
    Check(!System.Text.RegularExpressions.Regex.IsMatch(source, @"DatabaseHelper\.(Query|Execute|GetConnection)|new\s+(SqlConnection|HttpClient)|Process\.Start\("), "No database/network/executable help dependency: " + path);
}
string dashboard = File.ReadAllText(Path.Combine(root, "MainDashboardForm.cs"));
Check(dashboard.Contains("new HelpCenterPage()"), "Existing navigation integration retained");
Check(dashboard.Contains("(_, _) => OpenHelpCenterAfterAccountMenuCloses()") &&
    dashboard.Contains("NavigateTo(() => new HelpCenterPage(), \"Account\", \"Help & FAQ\")"),
    "Help remains available through the existing account menu");
Check(File.ReadAllText(Path.Combine(root, "Docs/HELP_CENTER.md")).Contains($"{entries.Count} questions"), "Documented catalog count stays aligned");
string matrix = File.ReadAllText(Path.Combine(root, "Docs/FAQ_COVERAGE_MATRIX.md"));
foreach (var entry in entries) Check(matrix.Contains($"| {entry.Id} | {entry.Question} |"), entry.Id + " coverage inventory matches current question");
Check(typeof(HelpCatalog).Assembly.GetReferencedAssemblies().All(a => a.Name is not null && !a.Name.Contains("SqlClient") && !a.Name.Contains("Windows.Forms")), "Portable catalog has no SQL or UI assembly dependency");
var watch = Stopwatch.StartNew();
for (int i = 0; i < 2000; i++) _ = HelpCatalog.Find("payment", "Collection", HelpRule.Recovery).ToArray();
watch.Stop();
Check(watch.Elapsed < TimeSpan.FromSeconds(20), "Bounded search smoke performance, not production benchmark");
if (args.Length == 2 && args[0] == "--inventory")
    File.WriteAllText(args[1], JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"PASS: {checks} offline help assertions; {entries.Count} questions, {entries.Select(e => e.Category).Distinct().Count()} categories. 2000 searches: {watch.ElapsedMilliseconds} ms.");
