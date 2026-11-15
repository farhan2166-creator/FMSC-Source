using FMSC_SMS.Pages.Communication;

namespace FMSC_SMS.Core;

/// <summary>Optional entry to the existing workflow; never saves or sends on behalf of its parent.</summary>
internal static class CommunicationRecordEntry
{
    internal static string DisplayFor(DataRow? row)
    {
        string name = row is not null && row.Table.Columns.Contains("StudentName")
            ? Convert.ToString(row["StudentName"])?.Trim() ?? "" : "";
        string number = row is not null ? AdmissionNumbers.Read(row) : "";
        if (name.Length == 0) return number.Length == 0 ? "Selected student" : $"Admission {number}";
        return number.Length == 0 ? name : $"{name} ({number})";
    }

    internal static CommunicationTarget Target(int admissionId, bool fees)
    {
        if (admissionId <= 0) throw new CommunicationValidationException("Select a saved student first.");
        return new(fees ? CommunicationAudience.OutstandingFees : CommunicationAudience.Student,
            0, 0, admissionId, 0, "WhatsApp");
    }

    internal static void Open(Control owner, int admissionId, string display, bool fees)
    {
        if (!CommunicationAccess.CanMessageStudent(CurrentUser.HasPermission, fees))
            throw new UnauthorizedAccessException("School-message and source-record access are required.");
        using var dialog = new CommunicationRecordMessageDialog(admissionId, display, fees);
        dialog.ShowDialog(owner.FindForm());
        // No admission/payment reload, write or draft reset on return.
    }
}
