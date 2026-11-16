using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.SqlClient;

internal sealed class AssertionFailure(string assertion) : Exception(assertion);
internal sealed record Scenario(string Name, string Result, int Assertions, double Seconds, string? Assertion = null, string? Category = null, int? SqlError = null);

internal sealed class Report
{
    internal readonly List<Scenario> Scenarios = new();
    internal int Assertions;
    internal int ExpectedRefusals;
    internal string? Engine;
    internal readonly Dictionary<string, double> Measurements = new();
    internal void Check(bool condition, string assertion)
    {
        if (!condition) throw new AssertionFailure(assertion);
        Assertions++;
    }
    internal async Task<bool> Stage(string name, Func<Task> action, bool eligible = true)
    {
        if (!eligible) { Scenarios.Add(new(name, "BLOCKED", 0, 0, "A prerequisite stage failed.")); return false; }
        var watch = Stopwatch.StartNew(); int before = Assertions;
        try { await action(); Scenarios.Add(new(name, "PASS", Assertions - before, watch.Elapsed.TotalSeconds)); }
        catch (Exception ex)
        {
            int? code = ex is SqlException sql ? sql.Number : ex.InnerException is SqlException inner ? inner.Number : null;
            var sqlError = ex as SqlException ?? ex.InnerException as SqlException;
            string context = sqlError is null ? "" :
                " in " + (Convert.ToString(sqlError.Data["FMSC_TestOperation"]) ??
                    (string.IsNullOrWhiteSpace(sqlError.Procedure) ? "SQL" : sqlError.Procedure)) +
                " at SQL line " + sqlError.LineNumber;
            string assertion = ex is AssertionFailure ? ex.Message : "Unexpected " +
                ex.GetType().Name + (code.HasValue ? " (SQL " + code + context + ")" : "");
            Scenarios.Add(new(name, "FAIL", Assertions - before, watch.Elapsed.TotalSeconds, assertion,
                name.StartsWith("Fixture", StringComparison.Ordinal) ? "Missing synthetic fixture dependency or provisioning issue" :
                ex is AssertionFailure ? "Persisted-state assertion failure; investigate expectation versus production behavior" : "Unexpected production operation/constraint error", code));
        }
        var result = Scenarios[^1]; Console.WriteLine(result.Result + ": " + name + (result.Assertion is null ? "" : " — " + result.Assertion));
        return result.Result == "PASS";
    }
    internal async Task Refused(string assertion, Func<Task> action, params int[] expectedCodes)
    {
        try { await action(); }
        catch (SqlException ex) when (expectedCodes.Contains(ex.Number)) { ExpectedRefusals++; Check(true, assertion); return; }
        catch (SqlException ex) { throw new AssertionFailure(assertion + ": expected SQL " + string.Join('/', expectedCodes) + ", got " + ex.Number); }
        throw new AssertionFailure(assertion + ": operation was accepted unexpectedly.");
    }
    internal void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(new {
            schemaVersion = DatabaseHelper.RequiredDatabaseSchemaVersion, engine = Engine,
            assertionCount = Assertions, expectedRefusals = ExpectedRefusals, scenarios = Scenarios,
            measurements = Measurements,
            limitations = new[] {
                "Real SQL and linked production writers; no interactive desktop/UI acceptance.",
                "Schema-only approved DACPAC: exported row data is excluded.",
                "Disposable fixture seeds version/reference rows and corrects DacFx recovery-scope varchar round-trip to uniqueidentifier.",
                "Collection ambiguous-outcome protection uses the existing local journal and deliberate read-only review; no server-side collection request ID is claimed.",
                "Physical printing, operator-account LocalDB attachment, installer and real network-failure behavior are outside Level 1."
            }
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
