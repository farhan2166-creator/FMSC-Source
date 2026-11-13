using FMSC_SMS.Core;
using FMSC_SMS.Services.Backup;

namespace FMSC_SMS.Pages.Administration;

internal sealed class CloudRestoreSelectionDialog : ApplicationWindowForm
{
    private readonly ModernComboBox provider = HumanUi.Combo();
    private readonly ModernTextBox passphrase = HumanUi.TextBox(maxLength: 32767);

    internal CloudBackupCoordinator.CloudSelection? SelectedCopy { get; private set; }
    internal string RecoveryPassphrase { get; private set; } = "";

    internal CloudRestoreSelectionDialog(UnifiedRecoveryPoint point)
    {
        Text = "Use Cloud Backup";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(AppScale.Scale(580), AppScale.Scale(350));
        BackColor = AppTheme.PageBackground;
        Font = AppTheme.NormalFont;
        Padding = new Padding(AppMetrics.SpacingMd);

        foreach (var copy in point.CloudCopies.OrderByDescending(copy => copy.RecoveryTested))
        {
            provider.Items.Add(new CloudBackupCoordinator.CloudSelection(
                copy.Remote, copy.RecoveryTested, copy.SourceBakSha256));
        }

        provider.FormattingEnabled = true;
        provider.Format += (_, e) =>
        {
            if (e.ListItem is CloudBackupCoordinator.CloudSelection copy)
                e.Value = $"{ProviderName(copy.Remote.Provider)} · uploaded {FormatBackupDate(copy.Remote.UploadedUtc)}";
        };
        passphrase.UseSystemPasswordChar = true;
        passphrase.PlaceholderText = "Administrator password or separate passphrase";

        var confirm = new ModernButton { Text = "Select Backup", ThemeRole = "Primary" };
        var cancel = new ModernButton {
            Text = UiText.Actions.Cancel, ThemeRole = "Secondary", DialogResult = DialogResult.Cancel
        };
        var card = HumanUi.Card("Cloud Backup", $"SQL backup from {FormatBackupDate(point.SqlBackupCreatedUtc)}");
        var body = new TableLayoutPanel {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Margin = Padding.Empty
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.FieldRowHeight));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.FieldRowHeight));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        provider.SelectedIndex = 0;
        provider.Enabled = provider.Items.Count > 1;
        body.Controls.Add(HumanUi.Field("Download from", provider), 0, 0);
        body.Controls.Add(HumanUi.Field("Cloud recovery credential", passphrase, required: true), 0, 1);
        body.Controls.Add(new Label {
            Text = "FMSC checks the SQL backup after download and before restore.",
            AutoSize = true,
            Dock = DockStyle.Fill,
            ForeColor = AppTheme.MutedText,
            Margin = new Padding(0, 0, 0, AppMetrics.SpacingMd)
        }, 0, 2);
        body.Controls.Add(HumanUi.DialogButtons(cancel, confirm), 0, 4);
        card.Content.Controls.Add(body);
        Controls.Add(card);

        void UpdateConfirm() => confirm.Enabled = provider.SelectedItem is not null &&
            !string.IsNullOrWhiteSpace(passphrase.Text);
        passphrase.TextChanged += (_, _) => UpdateConfirm();
        Shown += (_, _) => passphrase.Focus();
        UpdateConfirm();
        AcceptButton = confirm;
        CancelButton = cancel;
        confirm.Click += (_, _) =>
        {
            if (provider.SelectedItem is not CloudBackupCoordinator.CloudSelection selected ||
                string.IsNullOrWhiteSpace(passphrase.Text))
                return;
            SelectedCopy = selected;
            RecoveryPassphrase = passphrase.Text;
            passphrase.Clear();
            DialogResult = DialogResult.OK;
        };
        FormClosed += (_, _) => passphrase.Clear();
    }

    private static string FormatBackupDate(DateTime utc) =>
        utc.ToLocalTime().ToString("dd-MMM-yyyy hh:mm tt");

    private static string ProviderName(CloudProvider provider) =>
        provider == CloudProvider.GoogleDrive ? "Google Drive" : provider.ToString();
}
