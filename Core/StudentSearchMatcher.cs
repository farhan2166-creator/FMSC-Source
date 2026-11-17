using System.Data;
using System.Globalization;
namespace FMSC_SMS.Core;
internal static class StudentSearchMatcher
{
    // Quick-registration references use QR-<digits>. Permit QR6591 as a search
    // spelling without ever changing the reference displayed or stored.
    internal static bool TryQuickReferenceDigits(string? searchText, out string digits)
    {
        digits = string.Empty;
        string text = (searchText ?? string.Empty).Trim();
        if (!text.StartsWith("QR", StringComparison.OrdinalIgnoreCase)) return false;
        int offset = text.Length > 2 && text[2] == '-' ? 3 : 2;
        if (offset == text.Length) return false;
        for (int i = offset; i < text.Length; i++)
            if (text[i] < '0' || text[i] > '9') return false;
        digits = text[offset..];
        return true;
    }

    private static bool IsDigits(string text) => text.Length > 0 &&
        text.All(ch => ch >= '0' && ch <= '9');

    // The picker shares this exact-match definition so ambiguous official
    // and temporary references are shown without silently preferring either.
    internal static bool IsExactReference(string reference, string searchText)
    {
        string raw = searchText.Trim();
        bool numeric = AdmissionNumbers.TryNormalize(raw, out string number);
        if (string.Equals(reference, numeric ? number : raw, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!reference.StartsWith("QR-", StringComparison.OrdinalIgnoreCase)) return false;
        string suffix = reference[3..];
        return (numeric && IsDigits(number) && string.Equals(suffix, number, StringComparison.OrdinalIgnoreCase)) ||
            (TryQuickReferenceDigits(raw, out string digits) &&
             string.Equals(suffix, digits, StringComparison.OrdinalIgnoreCase));
    }

    public static List<DataRow> Find(DataTable source, string? searchText, int maxRows = 0, Func<DataRow, bool>? additionalFilter = null)
    {
        string rawTerm = (searchText ?? string.Empty).Trim();
        bool numeric = AdmissionNumbers.TryNormalize(rawTerm, out string normalizedNumber);
        string term = numeric ? normalizedNumber : rawTerm;
        bool numericDigits = numeric && IsDigits(normalizedNumber);
        bool quickReferenceInput = TryQuickReferenceDigits(rawTerm, out string quickDigits);
        DataColumn? admissionColumn = source.Columns.Contains("AdmissionNumber")
            ? source.Columns["AdmissionNumber"]
            : null;
        DataColumn? nameColumn = source.Columns.Contains("StudentName")
            ? source.Columns["StudentName"]
            : null;
        DataColumn? fatherColumn = source.Columns.Contains("FatherName")
            ? source.Columns["FatherName"]
            : null;
        DataColumn? registrationColumn = source.Columns.Contains("RegistrationNumber")
            ? source.Columns["RegistrationNumber"]
            : null;
        bool MatchesText(DataRow row) =>
            Text(row, registrationColumn).TrimStart().StartsWith(rawTerm, StringComparison.OrdinalIgnoreCase) ||
            (!numeric && (Text(row, nameColumn).Contains(rawTerm, StringComparison.OrdinalIgnoreCase) ||
                Text(row, fatherColumn).Contains(rawTerm, StringComparison.OrdinalIgnoreCase)));
        bool MatchesAdmissionPrefix(DataRow row)
        {
            if (admissionColumn is null || row[admissionColumn] == DBNull.Value)
                return false;
            string id = Convert.ToString(row[admissionColumn], CultureInfo.InvariantCulture) ?? string.Empty;
            if (id.StartsWith(term, StringComparison.OrdinalIgnoreCase)) return true;
            if (!id.StartsWith("QR-", StringComparison.OrdinalIgnoreCase)) return false;
            string suffix = id[3..];
            return (numericDigits && suffix.StartsWith(term, StringComparison.OrdinalIgnoreCase)) ||
                (quickReferenceInput && suffix.StartsWith(quickDigits, StringComparison.OrdinalIgnoreCase));
        }
        string Text(DataRow row, DataColumn? column) =>
            column is not null && row[column] != DBNull.Value
                ? Convert.ToString(row[column], CultureInfo.InvariantCulture) ?? string.Empty
                : string.Empty;
        var matches = new List<DataRow>();
        foreach (DataRow row in source.Rows)
        {
            if (additionalFilter is not null && !additionalFilter(row))
                continue;
            if (term.Length > 0 &&
                !MatchesAdmissionPrefix(row) &&
                !MatchesText(row))
            {
                continue;
            }
            matches.Add(row);
        }
        matches.Sort((left, right) => {
            int ExactRank(DataRow row)
            {
                if (IsExactReference(Text(row, admissionColumn), rawTerm) ||
                    string.Equals(Text(row, registrationColumn), rawTerm, StringComparison.OrdinalIgnoreCase)) return 0;
                if (numeric) return 1;
                if (string.Equals(Text(row, nameColumn), rawTerm, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Text(row, fatherColumn), rawTerm, StringComparison.OrdinalIgnoreCase)) return 1;
                if (Text(row, nameColumn).StartsWith(rawTerm, StringComparison.OrdinalIgnoreCase) ||
                    Text(row, fatherColumn).StartsWith(rawTerm, StringComparison.OrdinalIgnoreCase)) return 2;
                return 3;
            }
            if (term.Length > 0)
            {
                int exactOrder = ExactRank(left).CompareTo(ExactRank(right));
                if (exactOrder != 0) return exactOrder;
            }
            if (numeric)
            {
                int prefixOrder = (MatchesAdmissionPrefix(left) ? 0 : 1)
                    .CompareTo(MatchesAdmissionPrefix(right) ? 0 : 1);
                if (prefixOrder != 0)
                    return prefixOrder;
            }
            int idOrder = AdmissionNumbers.Compare(Text(left, admissionColumn), Text(right, admissionColumn));
            if (idOrder != 0)
                return idOrder;
            int nameOrder = StringComparer.OrdinalIgnoreCase.Compare(
                Text(left, nameColumn),
                Text(right, nameColumn));
            return nameOrder != 0
                ? nameOrder
                : StringComparer.OrdinalIgnoreCase.Compare(
                    Text(left, fatherColumn),
                    Text(right, fatherColumn));
        });
        if (maxRows > 0 && matches.Count > maxRows)
            matches.RemoveRange(maxRows, matches.Count - maxRows);
        return matches;
    }
}
