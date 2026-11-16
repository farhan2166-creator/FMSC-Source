using FMSC_SMS.Core;

namespace FMSC_SMS.Pages.Communication;

/// <summary>
/// Only collects an operator-approved local sending time. The database is
/// authoritative and independently enforces due/expiry at recipient claim.
/// </summary>
internal sealed class CommunicationScheduleDialog : ApplicationWindowForm
{
    private readonly TimeZoneInfo schoolZone;
    private readonly DateTimePicker when = new()
    {
        Name = "communicationScheduleWhen",
        Format = DateTimePickerFormat.Custom,
        CustomFormat = "dd MMM yyyy  hh:mm tt",
    };
    private readonly ModernComboBox priority = HumanUi.Combo();
    private readonly CheckBox expire = new() { Text = "Expire unsent messages after", Checked = true, AutoSize = true };
    private readonly NumericUpDown hours = new() { Minimum = 1, Maximum = 720, Value = 24, Width = AppScale.Scale(90) };
    private readonly Label problem = new() { AutoSize = true, ForeColor = AppTheme.Danger };
    internal DateTimeOffset Due { get; private set; }
    internal DateTimeOffset? Expires { get; private set; }
    internal string Priority => priority.SelectedIndex switch { 0 => "High", 2 => "Low", _ => "Normal" };
    internal string SchoolTimeLabel => TimeZoneInfo.ConvertTime(Due, schoolZone).ToString("dd MMM yyyy h:mm tt") + " (" + schoolZone.Id + ")";

    internal CommunicationScheduleDialog(TimeZoneInfo schoolZone)
    {
        this.schoolZone = schoolZone;
        var now = CommunicationSchoolTime.LocalNow(schoolZone);
        when.MinDate = now.Date;
        when.Value = now.AddHours(1);
        Text = "Schedule Reviewed Messages";
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ClientSize = new Size(AppScale.Scale(525), AppScale.Scale(310));
        BackColor = AppTheme.PageBackground;
        priority.Items.AddRange(new object[] { "High", "Normal", "Low" });
        priority.SelectedIndex = 1;
        var save = new ModernButton { Name = "communicationConfirmSchedule", Text = "Confirm Schedule", ThemeRole = "Primary" };
        var cancel = new ModernButton { Name = "communicationCancelSchedule", Text = "Cancel", DialogResult = DialogResult.Cancel };
        CancelButton = cancel;
        var expiryRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        expiryRow.Controls.Add(expire);
        expiryRow.Controls.Add(hours);
        expiryRow.Controls.Add(new Label { Text = "hours", AutoSize = true });
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5,
            Padding = new Padding(AppScale.Scale(18))
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.FieldRowHeight));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.FieldRowHeight));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(50)));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.ButtonHeight + AppScale.Scale(12)));
        layout.Controls.Add(HumanUi.Field("Send on (" + schoolZone.Id + ")", when, true), 0, 0);
        layout.Controls.Add(HumanUi.Field("Priority", priority), 0, 1);
        layout.Controls.Add(expiryRow, 0, 2);
        layout.Controls.Add(problem, 0, 3);
        layout.Controls.Add(HumanUi.DialogButtons(save, cancel), 0, 4);
        Controls.Add(layout);
        hours.Enabled = expire.Checked;
        expire.CheckedChanged += (_, _) => hours.Enabled = expire.Checked;
        save.Click += (_, _) =>
        {
            DateTimeOffset chosen;
            try { chosen = CommunicationSchoolTime.ToInstant(when.Value, schoolZone); }
            catch (CommunicationValidationException ex) { problem.Text = ex.Message; return; }
            if (chosen <= DateTimeOffset.UtcNow.AddMinutes(1))
            {
                problem.Text = "Choose a time at least one minute in the future.";
                return;
            }
            Due = chosen;
            Expires = expire.Checked ? Due.AddHours(Decimal.ToDouble(hours.Value)) : null;
            DialogResult = DialogResult.OK;
        };
    }
}
