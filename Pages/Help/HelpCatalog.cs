using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace FMSC_SMS.Pages.Help;

internal enum HelpRule { System, Setting, Choice, Permission, Recovery }
[Flags]
internal enum HelpOwner { SQL = 1, CSharp = 2, ConfigurableData = 4, Environment = 8 }

internal sealed record HelpEntry(
    string Id, string Category, string Question, string Answer, string Keywords, string WhereToGo,
    IReadOnlyList<HelpRule> Rules, HelpOwner Owners, IReadOnlyList<string> Sources,
    IReadOnlyList<string> RelatedIds, string Warning);

/// <summary>Offline, read-only catalog. Source references are maintainer evidence, not executable links.</summary>
internal static partial class HelpCatalog
{
    internal static string RuleLabel(HelpRule rule) => rule switch
    {
        HelpRule.System => "System rule", HelpRule.Setting => "School setting",
        HelpRule.Choice => "User choice", HelpRule.Permission => "Permission required",
        HelpRule.Recovery => "Recovery guidance", _ => throw new ArgumentOutOfRangeException(nameof(rule))
    };

    internal static IReadOnlyList<HelpEntry> Entries { get; } = Array.AsReadOnly(
        BasicsEntries().Concat(StudentsEntries()).Concat(FinanceEntries())
            .OrderBy(e => e.Id, StringComparer.Ordinal).ToArray());

    private sealed record SearchEntry(HelpEntry Entry, HashSet<string> Title, HashSet<string> Keywords,
        HashSet<string> Context, HashSet<string> Body);

    // Explicit equivalences, not fuzzy substring matching: "role" must not match "controller".
    private static readonly Dictionary<string, string> Aliases = BuildAliases();
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "i", "my", "me", "we", "our", "you", "your", "do", "does", "did", "is", "are", "was",
        "be", "been", "can", "cant", "cannot", "why", "how", "what", "where", "when", "to", "of", "for", "and", "or",
        "in", "on", "at", "it", "its", "this", "that", "with", "have", "has", "should", "will", "not", "after", "before"
    };
    private static readonly SearchEntry[] Index = Entries.Select(e => new SearchEntry(e,
        Tokens(e.Question), Tokens(e.Keywords), Tokens(e.Category + " " + e.WhereToGo + " " + string.Join(" ", e.Rules.Select(RuleLabel))),
        Tokens(e.Answer + " " + e.Warning))).ToArray();

    internal static IEnumerable<HelpEntry> Find(string? term, string? category, HelpRule? rule = null)
    {
        string[] words = Tokens(term ?? "").Where(w => !StopWords.Contains(w)).ToArray();
        string phrase = Normalize(term ?? "");
        return Index.Where(x => (string.IsNullOrWhiteSpace(category) || category == "All topics" ||
                string.Equals(x.Entry.Category, category, StringComparison.OrdinalIgnoreCase)) &&
                (!rule.HasValue || x.Entry.Rules.Contains(rule.Value)))
            .Select(x => (x.Entry, Score: Score(x, words, phrase)))
            .Where(x => x.Score >= 0).OrderByDescending(x => x.Score).ThenBy(x => x.Entry.Id, StringComparer.Ordinal)
            .Select(x => x.Entry);
    }

    private static int Score(SearchEntry entry, string[] words, string phrase)
    {
        int score = 0;
        foreach (string word in words)
        {
            int weight = entry.Title.Contains(word) ? 12 : entry.Keywords.Contains(word) ? 9 :
                entry.Context.Contains(word) ? 5 : entry.Body.Contains(word) ? 1 : -1;
            if (weight < 0) return -1; // Every meaningful term must match; never silently drop a filter word.
            score += weight;
        }
        if (words.Length > 0 && Normalize(entry.Entry.Question) == phrase) score += 100;
        return score;
    }

    private static string Normalize(string value) => value.ToLowerInvariant().Replace("’", "").Replace("'", "");
    private static HashSet<string> Tokens(string value) => Regex.Matches(Normalize(value), @"[\p{L}\p{Nd}]+")
        .Select(m => Aliases.TryGetValue(m.Value, out string? canonical) ? canonical : m.Value).ToHashSet(StringComparer.Ordinal);

    private static Dictionary<string, string> BuildAliases()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string group in new[]
        {
            "outstanding dues unpaid balance balances", "admission admissions admit admision admisison",
            "registration registrations register", "enrollment enrolment enrollments enrolments",
            "fee fees", "charge charges", "billing posting post", "payment payments collection collect",
            "receipt receipts", "parent parents guardian guardians", "family families sibling siblings",
            "permission permissions access", "role roles", "database localdb sql", "cancel cancelled canceled cancellation",
            "reverse reversed reversal undo", "refund refunds", "left withdraw withdrawal", "student students",
            "class classes", "section sections", "academic academics", "year years", "photo photos image images",
            "tuition tution", "save saved saving", "filter filters filtering", "search searching", "confirm confirmed confirmation",
            "promotion promote promoted", "discount discounts", "report reports", "rate rates", "template templates",
            "schedule scheduled scheduling", "message messages messaging", "staff employee employees", "link linking linked",
            "unlink unlinking delink", "configure configuration settings setting", "backup backups", "restore restoring",
            "print printing", "number numbers", "date dates", "correction corrections", "partial installment instalment",
            "authorize authorized authorised", "program programme programs programmes", "color colour", "center centre"
        })
        {
            string[] words = group.Split(' ');
            foreach (string word in words) result.Add(word, words[0]);
        }
        return result;
    }

    internal static IReadOnlyList<HelpEntry> Related(HelpEntry entry) => entry.RelatedIds
        .Select(id => Entries.First(e => e.Id == id)).ToArray();
}
