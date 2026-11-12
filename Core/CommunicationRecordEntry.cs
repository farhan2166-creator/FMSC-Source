using FMSC_SMS.Pages.Communication;

namespace FMSC_SMS.Core;

/// <summary>Optional entry to the existing workflow; never saves or sends on behalf of its parent.</summary>
internal static class CommunicationRecordEntry
{
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
        var target = Target(admissionId, fees);
        string body = CommunicationPreparedMessages.BuiltIns.First(item =>
            item.Name == (fees ? "Outstanding fee reminder" : "Individual student notice")).Body;
        using var page = new CommunicationCenterPage(target, body, display);
        using var dialog = new ApplicationWindowForm {
            Text = "School Message", StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(AppScale.Scale(1100), AppScale.Scale(760)),
            MinimumSize = new Size(AppScale.Scale(800), AppScale.Scale(600)), ShowInTaskbar = false
        };
        page.Dock = DockStyle.Fill;
        dialog.Controls.Add(page);
        ModalPageCloseGuard.Attach(dialog, page);
        dialog.ShowDialog(owner.FindForm());
        // No admission/payment reload, write or draft reset on return.
    }
}
