using FMSC_SMS.Core;

namespace FMSC_SMS.Pages.Students.Admissions;

internal sealed class AdmissionSiblingSyncDialog : ApplicationWindowForm
{
    private readonly AdmissionSiblingSyncData.Preview preview;
    private readonly Func<AdmissionSiblingSyncData.Preview, Task<int>> apply;
    private readonly DataGridView rows = HumanUi.Grid();
    private readonly ModernButton confirm = new() { Text = "Confirm Updates", ThemeRole = "Primary" };
    private readonly ModernButton cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel };
    private readonly Label status = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private bool busy;
    internal int Changed { get; private set; }

    internal AdmissionSiblingSyncDialog(AdmissionSiblingSyncData.Preview preview,
        Func<AdmissionSiblingSyncData.Preview, Task<int>>? apply = null)
    {
        this.preview = preview;
        this.apply = apply ?? AdmissionSiblingSyncData.ApplyAsync;
        Text = "Sync " + preview.Field.Label + " with Siblings";
        ClientSize = new Size(AppScale.Scale(960), AppScale.Scale(420));
        MinimumSize = new Size(AppScale.Scale(720), AppScale.Scale(320));
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = ShowInTaskbar = false;
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(AppScale.Scale(14)) };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(48)));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(45)));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.ButtonHeight + AppScale.Scale(8)));
        var source = preview.Rows.Single(row => row.AdmissionId == preview.Anchor);
        body.Controls.Add(new Label { Dock = DockStyle.Fill, Text = $"Copy {preview.Field.Label} from {source.StudentName} to the records below. Only this field is saved.",
            TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        rows.AutoGenerateColumns = false;
        rows.ReadOnly = true;
        rows.AllowUserToAddRows = rows.AllowUserToDeleteRows = false;
        rows.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
        rows.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        Add("AdmissionNumber", "Admission No.", 15);
        Add("StudentName", "Sibling", 25);
        Add("OldValue", "Old Value", 30);
        Add("NewValue", "New Value", 30);
        foreach (var row in preview.Rows)
            rows.Rows.Add(row.AdmissionNumber, row.StudentName + (row.AdmissionId == preview.Anchor ? " (current)" : ""),
                row.OldValue, preview.NewValue);
        rows.CellFormatting += (_, e) =>
        {
            if (e.ColumnIndex >= 2 && string.IsNullOrEmpty(Convert.ToString(e.Value))) e.Value = "(blank)";
        };
        body.Controls.Add(rows, 0, 1);
        status.Text = preview.Changes == 0 ? "These records already match." : $"{preview.Changes} record(s) will change.";
        body.Controls.Add(status, 0, 2);
        confirm.Enabled = preview.Changes > 0;
        body.Controls.Add(HumanUi.DialogButtons(confirm, cancel), 0, 3);
        Controls.Add(body);
        CancelButton = cancel;
        confirm.Click += async (_, _) => await ConfirmAsync();
        FormClosing += (_, e) => { if (busy) { e.Cancel = true; DialogResult = DialogResult.None; } };
        void Add(string name, string caption, float width) => rows.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = name, HeaderText = caption, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = width, MinimumWidth = AppScale.Scale(name == "AdmissionNumber" ? 100 : 130)
        });
    }

    private async Task ConfirmAsync()
    {
        if (busy || preview.Changes == 0) return;
        busy = true;
        confirm.Enabled = cancel.Enabled = false;
        UseWaitCursor = true;
        try
        {
            Changed = await apply(preview);
            busy = false;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            status.ForeColor = AppTheme.Danger;
            status.Text = DbMessage.UserMessage(ex);
        }
        finally
        {
            busy = false;
            if (!IsDisposed) { confirm.Enabled = cancel.Enabled = true; UseWaitCursor = false; }
        }
    }
}
