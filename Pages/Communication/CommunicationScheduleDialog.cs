using FMSC_SMS.Core;

namespace FMSC_SMS.Pages.Communication;

/// <summary>Explicit local-time one-off scheduling; no message is queued or
/// confirmed until the user accepts the dialog and the reviewed batch.</summary>
internal sealed class CommunicationScheduleDialog : ApplicationWindowForm
{
    private readonly DateTimePicker date = new() {
        Format = DateTimePickerFormat.Short,
        Width = AppScale.Scale(175)
    };
    private readonly DateTimePicker time = new() {
        Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm",
        ShowUpDown = true, Width = AppScale.Scale(175)
    };
    private readonly DateTimePicker expiresDate = new() {
        Format = DateTimePickerFormat.Short,
        Width = AppScale.Scale(175)
    };
    private readonly DateTimePicker expiresTime = new() {
        Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm",
        ShowUpDown = true, Width = AppScale.Scale(175)
    };
    internal DateTime SendAtLocal => date.Value.Date.Add(time.Value.TimeOfDay);
    internal DateTime ExpiresLocal => expiresDate.Value.Date.Add(expiresTime.Value.TimeOfDay);

    internal CommunicationScheduleDialog()
    {
        Text = "Schedule Message";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(AppScale.Scale(470), AppScale.Scale(258));
        MinimumSize = new Size(AppScale.Scale(455), AppScale.Scale(250));
        ShowInTaskbar = false;
        Padding = new Padding(AppScale.Scale(14));
        BackColor = AppTheme.PageBackground;
        DateTime initial = DateTime.Now.AddHours(1);
        DateTime expiry = initial.AddHours(24);
        date.MinDate = DateTime.Today;
        expiresDate.MinDate = DateTime.Today;
        date.Value = initial; time.Value = initial;
        expiresDate.Value = expiry; expiresTime.Value = expiry;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(42)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(42)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(40)));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label { Text = "Send on (local date and time)", AutoSize = true }, 0, 0);
        layout.Controls.Add(date, 1, 0);
        layout.Controls.Add(new Label { Text = "Send at", AutoSize = true }, 0, 1);
        layout.Controls.Add(time, 1, 1);
        layout.Controls.Add(new Label { Text = "Expires after", AutoSize = true }, 0, 2);
        var expires = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true };
        expiresDate.Width = AppScale.Scale(110);
        expiresTime.Width = AppScale.Scale(80);
        expires.Controls.Add(expiresDate); expires.Controls.Add(expiresTime);
        layout.Controls.Add(expires, 1, 2);
        layout.Controls.Add(new Label {
            Text = "For free WhatsApp, scheduling prepares a reminder for the operator. It does not send automatically.",
            Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = AppTheme.MutedText
        }, 0, 3);
        layout.SetColumnSpan(layout.GetControlFromPosition(0, 3)!, 2);
        var ok = new ModernButton { Text = "Schedule", ThemeRole = "Primary" };
        var cancel = new ModernButton { Text = "Cancel", DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) =>
        {
            if (SendAtLocal <= DateTime.Now.AddMinutes(1) || ExpiresLocal <= SendAtLocal)
            {
                ModernMessageDialog.Show(this, "Choose a future sending time and an expiry after that time.",
                    "Schedule", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            DialogResult = DialogResult.OK;
        };
        var footer = HumanUi.DialogButtons(ok, cancel);
        footer.Dock = DockStyle.Bottom;
        Controls.Add(layout);
        Controls.Add(footer);
        CancelButton = cancel;
    }
}
