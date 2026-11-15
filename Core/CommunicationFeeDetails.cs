using System.Globalization;
using System.Text.RegularExpressions;

namespace FMSC_SMS.Core;

// A concise statement of posted balances, not an overdue notice. Advance-posted
// charges can be outstanding before their due date; never imply they are late.
internal static class CommunicationFeeDetails
{
    internal sealed record Student(int AdmissionId, string Name, string AdmissionNumber, string ClassSection, decimal Outstanding);

    internal const string Field = "[FeeDetails]";
    internal const string Template = "[FeeDetails]\n\nPlease contact the school accounts office if you have already paid or need assistance.\nThank you.";

    internal static string Format(string school, DateTime asAt, IReadOnlyList<Student> students, decimal total, bool family)
    {
        if (students.Count == 0 || (!family && students.Count != 1)
            || students.Any(student => student.AdmissionId <= 0 || student.Outstanding <= 0)
            || students.Select(student => student.AdmissionId).Distinct().Count() != students.Count
            || students.Sum(student => student.Outstanding) != total)
            throw new CommunicationValidationException("The fee breakdown does not match the outstanding total. Review the fees again.");

        var lines = new List<string>
        {
            Label(school), "Outstanding fees as at " + asAt.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture),
            "", "Dear parent/guardian,", ""
        };
        foreach (var student in students)
        {
            string admission = Label(student.AdmissionNumber);
            string name = Label(student.Name);
            lines.Add((name.Length == 0 ? "Student" : name) + (admission.Length == 0 ? "" : " (Adm. " + admission + ")"));
            string placement = Label(student.ClassSection);
            if (placement.Length > 0) lines.Add("Class: " + placement);
            lines.Add("Outstanding: PKR " + student.Outstanding.ToString("N2", CultureInfo.InvariantCulture));
            lines.Add("");
        }
        if (family) lines.Add("Total outstanding: PKR " + total.ToString("N2", CultureInfo.InvariantCulture));
        return string.Join("\n", lines).Trim();
    }

    internal static string Expand(string body, string details)
    {
        if (!body.Contains(Field, StringComparison.Ordinal)) return body;
        if (string.IsNullOrWhiteSpace(details))
            throw new CommunicationValidationException("The fee details are unavailable. Review the fees again.");
        string expanded = body.Replace(Field, details, StringComparison.Ordinal);
        if (expanded.Length > 1600)
            throw new CommunicationValidationException("The fee breakdown exceeds 1600 characters. Choose a balance-only template and share the bill image for the details.");
        return expanded;
    }

    // These labels become literal batch text. Keep record brackets from being
    // interpreted as prepared fields, and keep embedded newlines out of headings.
    private static string Label(string value) => Regex.Replace(value.Replace('[', '(').Replace(']', ')')
        .Replace('{', '(').Replace('}', ')'), @"\s+", " ").Trim();
}
