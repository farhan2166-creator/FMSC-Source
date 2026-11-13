using FMSC_SMS.Core;
using FMSC_SMS.Services.Backup;

namespace FMSC_SMS.Pages.Administration;

internal sealed class CloudRestoreSelectionDialog : ApplicationWindowForm
{
    private sealed record RecoveryPoint(DateTime SqlCreatedUtc,
        IReadOnlyList<CloudBackupCoordinator.CloudSelection> Copies);

    private readonly ModernComboBox provider = HumanUi.Combo();
    private readonly ModernTextBox passphrase = HumanUi.TextBox(maxLength: 32767);

    internal CloudBackupCoordinator.CloudSelection? SelectedCopy { get; private set; }
    internal string RecoveryPassphrase { get; private set; } = "";

    internal CloudRestoreSelectionDialog(IReadOnlyList<CloudBackupCoordinator.CloudSelection> all,
        DateTime? latestLocalBackupUtc, IReadOnlyList<string>? warnings = null)
    {
        Text = "Choose Cloud Backup";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(AppScale.Scale(660), AppScale.Scale(540));
        BackColor = AppTheme.PageBackground;
        Font = AppTheme.NormalFont;
        Padding = new Padding(AppMetrics.SpacingMd);

        var groups = all.GroupBy(copy => copy.Remote.PackageSha256, StringComparer.OrdinalIgnoreCase)
            .Select(group => new RecoveryPoint(group.First().Remote.SqlBackupCreatedUtc,
                group.OrderByDescending(copy => copy.KnownVerifiedUpload).ToArray()))
            .OrderByDescending(point => point.SqlCreatedUtc)
            .ToArray();

        var points = HumanUi.Grid();
        points.AccessibleName = "Cloud recovery points";
        points.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        points.AllowUserToResizeColumns = false;
        points.Columns.Add(new DataGridViewTextBoxColumn {
            HeaderText = "SQL backup date",
            FillWeight = 55,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        points.Columns.Add(new DataGridViewTextBoxColumn {
            HeaderText = "Available copies",
            FillWeight = 45,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        foreach (var point in groups)
        {
            points.Rows.Add(FormatBackupDate(point.SqlCreatedUtc),
                string.Join(", ", point.Copies.Select(copy => ProviderName(copy.Remote.Provider)).Distinct()));
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
        var card = HumanUi.Card("Cloud Recovery Points",
            $"Latest local: {FormatBackupDate(latestLocalBackupUtc)}    |    Latest cloud: {FormatBackupDate(groups.FirstOrDefault()?.SqlCreatedUtc)}");
        var body = new TableLayoutPanel {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            Margin = Padding.Empty
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.FieldRowHeight));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.FieldRowHeight));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        points.Margin = new Padding(0, 0, 0, AppMetrics.SpacingXs);
        points.MinimumSize = new Size(0, AppMetrics.GridHeaderHeight + 2 * AppMetrics.GridRowHeight);
        body.Controls.Add(points, 0, 0);
        var warning = new Label {
            Text = warnings is null ? string.Empty : string.Join(Environment.NewLine, warnings),
            AutoSize = true,
            Dock = DockStyle.Fill,
            ForeColor = AppTheme.Warning,
            Margin = new Padding(0, AppMetrics.SpacingXs, 0, AppMetrics.SpacingSm),
            UseMnemonic = false
        };
        if (warning.Text.Length > 0)
            body.Controls.Add(warning, 0, 1);
        body.Controls.Add(HumanUi.Field("Download from", provider), 0, 2);
        body.Controls.Add(HumanUi.Field("Cloud recovery passphrase", passphrase, required: true), 0, 3);
        body.Controls.Add(new Label {
            Text = "FMSC checks the SQL backup after download and before restore.",
            AutoSize = true,
            Dock = DockStyle.Fill,
            ForeColor = AppTheme.MutedText,
            Margin = new Padding(0, 0, 0, AppMetrics.SpacingMd)
        }, 0, 4);
        body.Controls.Add(HumanUi.DialogButtons(cancel, confirm), 0, 5);
        card.Content.Controls.Add(body);
        Controls.Add(card);

        void UpdateConfirm() => confirm.Enabled = provider.SelectedItem is not null &&
            !string.IsNullOrWhiteSpace(passphrase.Text);
        void SelectPoint(RecoveryPoint point)
        {
            provider.Items.Clear();
            foreach (var copy in point.Copies)
                provider.Items.Add(copy);
            provider.SelectedIndex = 0;
        }

        provider.SelectedIndexChanged += (_, _) => UpdateConfirm();
        passphrase.TextChanged += (_, _) => UpdateConfirm();
        if (groups.Length > 0)
        {
            points.CurrentCell = points.Rows[0].Cells[0];
            SelectPoint(groups[0]);
        }
        points.RowEnter += (_, e) => SelectPoint(groups[e.RowIndex]);
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

    private static string FormatBackupDate(DateTime? utc) =>
        utc?.ToLocalTime().ToString("dd-MMM-yyyy hh:mm tt") ?? "None";

    private static string ProviderName(CloudProvider provider) =>
        provider == CloudProvider.GoogleDrive ? "Google Drive" : provider.ToString();
}
