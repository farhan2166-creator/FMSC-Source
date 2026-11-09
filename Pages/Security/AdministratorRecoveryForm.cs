using FMSC_SMS.Core;

namespace FMSC_SMS.Pages.Security;

/// <summary>Explicit, online-only emergency Administrator recovery on the SQL host.</summary>
internal sealed class AdministratorRecoveryForm : ApplicationWindowForm
{
    private readonly ModernTextBox username = HumanUi.TextBox(false, 100);
    private readonly ModernTextBox password = HumanUi.TextBox(false, 100);
    private readonly ModernTextBox confirm = HumanUi.TextBox(false, 100);
    private readonly ModernButton reset = new() {
        Text = "Reset Administrator Login", Dock = DockStyle.Fill, SavesChanges = true
    };
    private readonly ModernButton cancel = new() { Text = "Cancel", Dock = DockStyle.Fill };
    private readonly Label status = new() {
        Dock = DockStyle.Fill, AutoSize = false, TextAlign = ContentAlignment.MiddleCenter
    };
    private bool busy;
    internal string RecoveredUsername { get; private set; } = "";

    internal AdministratorRecoveryForm()
    {
        Text = "FMSC - Administrator Recovery";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(AppScale.Scale(615), AppScale.Scale(555));
        MinimumSize = SizeFromClientSize(new Size(AppScale.Scale(560), AppScale.Scale(500)));
        BackColor = AppTheme.PageBackground;
        Font = AppTheme.NormalFont;
        password.UseSystemPasswordChar = true;
        confirm.UseSystemPasswordChar = true;
        username.PlaceholderText = "New Administrator username";
        password.PlaceholderText = "New password";
        confirm.PlaceholderText = "Confirm new password";
        reset.SetColors(AppTheme.Primary);
        cancel.SetColors(AppTheme.MutedText);

        var layout = new TableLayoutPanel {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8,
            Padding = new Padding(AppScale.Scale(24)), BackColor = AppTheme.PageBackground
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(48)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(75)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.FieldRowHeight));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.FieldRowHeight));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.FieldRowHeight));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.ButtonHeight + AppMetrics.SpacingSm));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.ButtonHeight + AppMetrics.SpacingSm));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label {
            Text = "Recover Administrator Access", Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter, Font = AppTheme.PageTitleFont,
            ForeColor = AppTheme.HeadingText
        }, 0, 0);
        layout.Controls.Add(new Label {
            Text = "Only the registered database-host Windows account can recover the existing Administrator. " +
                   "Run FMSC as Windows Administrator and stay connected to GitHub. " +
                   "Other users, roles, fees and school records will not change.",
            Dock = DockStyle.Fill, AutoSize = false, ForeColor = AppTheme.MutedText
        }, 0, 1);
        layout.Controls.Add(HumanUi.Field("New Username", username, true), 0, 2);
        layout.Controls.Add(HumanUi.Field("New Password", password, true), 0, 3);
        layout.Controls.Add(HumanUi.Field("Confirm Password", confirm, true), 0, 4);
        layout.Controls.Add(reset, 0, 5);
        layout.Controls.Add(cancel, 0, 6);
        status.ForeColor = AppTheme.MutedText;
        status.Text = "A recovery-host registration from an earlier successful Administrator login is required.";
        layout.Controls.Add(status, 0, 7);
        Controls.Add(layout);

        AcceptButton = reset;
        CancelButton = cancel;
        cancel.Click += (_, _) => { if (!busy) DialogResult = DialogResult.Cancel; };
        reset.Click += async (_, _) => await ResetAsync();
        FormClosing += (_, args) => {
            if (busy) args.Cancel = true;
        };
        Shown += (_, _) => username.Focus();
    }

    private async Task ResetAsync()
    {
        if (busy) return;
        string desiredName = username.Text.Trim();
        string desiredPassword = password.Text;
        if (desiredName.Length is < 1 or > 100 || desiredName.Any(char.IsControl))
        {
            ModernMessageDialog.ShowWarning(this, "Enter a valid Administrator username.", "Administrator Recovery");
            username.Focus();
            return;
        }
        if (!PasswordPolicy.IsValid(desiredPassword))
        {
            ModernMessageDialog.ShowWarning(this, PasswordPolicy.Requirement, "Administrator Recovery");
            password.Focus();
            return;
        }
        if (!string.Equals(desiredPassword, confirm.Text, StringComparison.Ordinal))
        {
            ModernMessageDialog.ShowWarning(this, "The two passwords do not match.", "Administrator Recovery");
            confirm.Focus();
            return;
        }
        if (!ModernMessageDialog.Confirm(this,
            "Replace the existing Administrator username and password on the verified school database? " +
            "All other accounts and school records remain unchanged.",
            "Confirm Administrator Recovery", defaultYes: false))
            return;

        busy = true;
        reset.Enabled = cancel.Enabled = false;
        username.Enabled = password.Enabled = confirm.Enabled = false;
        status.Text = "Verifying GitHub computer access and database host…";
        UseWaitCursor = true;
        try
        {
            await AdministratorRecoveryService.RecoverAsync(desiredName, desiredPassword);
            if (IsDisposed) return;
            RecoveredUsername = desiredName;
            ModernMessageDialog.ShowInfo(this,
                "Administrator access has been recovered. Sign in with the new username and password.",
                "Administrator Recovery");
            DialogResult = DialogResult.OK;
        }
        catch (Exception ex)
        {
            AppExceptionHandler.LogNonFatal(ex, "Administrator recovery refused");
            if (!IsDisposed)
            {
                status.Text = "Recovery was not completed. The existing account remains unchanged.";
                string guidance = ex is UnauthorizedAccessException or ArgumentException or
                    InvalidOperationException or InvalidDataException
                    ? ex.Message : DbMessage.UserMessage(ex);
                ModernMessageDialog.ShowWarning(this, guidance,
                    "Administrator Recovery");
            }
        }
        finally
        {
            busy = false;
            if (!IsDisposed)
            {
                UseWaitCursor = false;
                reset.Enabled = cancel.Enabled = true;
                username.Enabled = password.Enabled = confirm.Enabled = true;
                password.Text = confirm.Text = "";
            }
        }
    }
}
