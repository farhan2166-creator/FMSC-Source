using System.Data;
using FMSC_SMS.Core;

namespace FMSC_SMS.Pages.Staff.Certificates;

/// <summary>
/// A read-only, staff-scoped view of the two existing certificate histories.
/// Existing editors continue to own every write, print, collection and reissue.
/// </summary>
internal static class StaffCertificateRegisterReader
{
    internal sealed record Kind(string Key, string Caption, string Reader, string RecordKey);

    internal static readonly Kind[] Kinds =
    {
        new("Lecturer", "Regular Teacher / Lecturer", "GetRegularLecturerCertificateRecords", "RegularLecturerCertificateId"),
        new("Bank", "Bank Account Opening", "GetBankAccountOpeningCertificateRecords", "BankAccountOpeningCertificateId")
    };

    internal static DataTable CreateTable()
    {
        var table = new DataTable();
        table.Columns.Add("Kind");
        table.Columns.Add("Certificate Type");
        table.Columns.Add("Certificate Date");
        table.Columns.Add("Reference");
        table.Columns.Add("Collection");
        table.Columns.Add("Collected On");
        table.Columns.Add("Recorded On");
        table.Columns.Add("RecordId", typeof(int));
        table.Columns.Add("IssueId", typeof(int));
        table.Columns.Add("RecordedAt", typeof(DateTime));
        table.Columns.Add("SourceCopyId", typeof(int));
        return table;
    }

    private static int Number(DataRow row, string name) =>
        row.Table.Columns.Contains(name) && !row.IsNull(name) ? Convert.ToInt32(row[name]) : 0;

    private static string Value(DataRow row, string name) =>
        row.Table.Columns.Contains(name) && !row.IsNull(name) ?
            Convert.ToString(row[name])?.Trim() ?? "" : "";

    private static DateTime? Date(DataRow row, params string[] names)
    {
        foreach (string name in names)
            if (row.Table.Columns.Contains(name) && !row.IsNull(name))
                return Convert.ToDateTime(row[name]);
        return null;
    }

    internal static void AppendRecords(DataTable destination, Kind kind, DataTable source, int staffId)
    {
        if (!source.Columns.Contains("StaffId") || !source.Columns.Contains(kind.RecordKey))
            throw new InvalidOperationException(kind.Reader + " did not return the required staff and certificate identifiers.");

        foreach (DataRow row in source.Rows)
        {
            // A mixed/incorrect staff result must never be displayed or operated on.
            if (Number(row, "StaffId") != staffId)
                throw new InvalidOperationException("A certificate reader returned another staff member's record.");
            int recordId = Number(row, kind.RecordKey);
            if (recordId <= 0) continue;
            DateTime? dated = Date(row, "CertificateDate");
            DateTime? recorded = Date(row, "IssuedAt", "CreatedAt", "CertificateDate");
            DateTime? collected = Date(row, "CollectedAt");
            string state = Value(row, "CollectionStatus");
            if (string.IsNullOrWhiteSpace(state))
                state = collected.HasValue ? "Collected" : "Not Collected";
            string reference = Value(row, "CertificateNumber");
            if (reference.Length == 0) reference = "Record #" + recordId;
            destination.Rows.Add(kind.Key, kind.Caption,
                dated?.ToString("dd MMM yyyy") ?? "—", reference, state,
                collected?.ToString("dd MMM yyyy") ?? "—",
                recorded?.ToString("dd MMM yyyy HH:mm") ?? "—",
                recordId, 0, recorded ?? DateTime.MinValue, Number(row, "PreviousCertificateId"));
        }
    }

    internal static DataTable Read(int staffId)
    {
        if (staffId <= 0) throw new ArgumentOutOfRangeException(nameof(staffId));
        var combined = CreateTable();
        foreach (Kind kind in Kinds)
        {
            using var source = DatabaseHelper.QueryUser(kind.Reader, DatabaseHelper.Int("@StaffId", staffId));
            AppendRecords(combined, kind, source, staffId);
        }
        combined.DefaultView.Sort = "RecordedAt DESC, [Certificate Type] ASC, RecordId DESC";
        return combined;
    }
}
