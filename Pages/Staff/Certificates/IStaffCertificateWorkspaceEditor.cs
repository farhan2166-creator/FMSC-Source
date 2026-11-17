using System.Data;
using FMSC_SMS.Core;

namespace FMSC_SMS.Pages.Staff.Certificates;

/// <summary>
/// Narrow adapter to existing staff certificate pages. The workspace never
/// creates an alternative SQL writer or bypasses the existing button workflow.
/// </summary>
internal interface IStaffCertificateWorkspaceEditor
{
    HumanCard DetailsCard { get; }
    Label Feedback { get; }
    Action<int>? Saved { set; }
    void ConfigureWorkspaceDetails(DataRow staff, bool editing);
    void LoadWorkspaceSelection(DataRow staff, int recordId);
    bool CanPrintFromWorkspace { get; }
    bool CanCollectFromWorkspace { get; }
    bool CanIssueAgainFromWorkspace { get; }
    void PrintFromWorkspace();
    void CollectFromWorkspace();
    void IssueAgainFromWorkspace();
}

internal static class StaffCertificateWorkspaceEditorUi
{
    internal static void LockStaff(StaffPicker picker, DataRow staff)
    {
        picker.SetStaff(staff);
        picker.Enabled = false;
        picker.EnabledChanged += (_, _) => { if (picker.Enabled) picker.Enabled = false; };
        if (picker.Parent is not TableLayoutPanel field) return;
        var cell = field.GetCellPosition(picker);
        picker.Visible = false;
        field.Controls.Remove(picker);
        var display = new Label
        {
            Text = CertificateUi.GetString(staff, "StaffNumber") + " — " +
                   CertificateUi.GetString(staff, "StaffName"),
            Dock = DockStyle.Top,
            Height = AppMetrics.InputControlHeight,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = AppTheme.Text,
            BackColor = AppTheme.SurfaceElevated,
            AutoEllipsis = true,
            BorderStyle = BorderStyle.FixedSingle
        };
        display.Controls.Add(picker); // Keep the real picker owned until the editor is disposed.
        field.Controls.Add(display, cell.Column, cell.Row);
    }
}
