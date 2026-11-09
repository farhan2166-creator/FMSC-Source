using FMSC_SMS.Core;
using FMSC_SMS.Services.Backup;

namespace FMSC_SMS.Pages.Administration;

/// <summary>
/// Compact picker within the existing Restore workflow. Multiple cloud copies
/// of the same encrypted package appear as ONE recovery point; the operator
/// may explicitly select which connected cloud service to download from.
/// </summary>
internal sealed class CloudRestoreSelectionDialog : ApplicationWindowForm
{
    private sealed record RecoveryPoint(DateTime SqlCreatedUtc,
        string Hash, IReadOnlyList<CloudBackupCoordinator.CloudSelection> Copies)
    {
        public override string ToString() =>
            SqlCreatedUtc.ToLocalTime().ToString("dd-MMM-yyyy  hh:mm tt") +
            "   —   " + string.Join(" + ", Copies.Select(x => x.Remote.Provider.ToString()).Distinct());
    }
    private readonly ListBox points = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly ComboBox provider = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly TextBox passphrase = new() {
        UseSystemPasswordChar = true, Dock = DockStyle.Fill
    };
    private readonly Label details = new() {
        Dock = DockStyle.Fill, AutoSize = false, ForeColor = AppTheme.MutedText
    };

    internal CloudBackupCoordinator.CloudSelection? SelectedCopy { get; private set; }
    internal string RecoveryPassphrase { get; private set; } = "";

    internal CloudRestoreSelectionDialog(IReadOnlyList<CloudBackupCoordinator.CloudSelection> all,
        DateTime? latestLocalBackupUtc)
    {
        Text = "Select a cloud recovery point";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(AppScale.Scale(680), AppScale.Scale(480));
        BackColor = AppTheme.PageBackground;

        var groups = all.GroupBy(x => x.Remote.PackageSha256, StringComparer.OrdinalIgnoreCase)
            .Select(g => new RecoveryPoint(g.First().Remote.SqlBackupCreatedUtc,
                g.Key, g.OrderByDescending(x => x.KnownVerifiedUpload).ToArray()))
            .OrderByDescending(x => x.SqlCreatedUtc).ToArray();
        foreach (var point in groups) points.Items.Add(point);
        points.SelectedIndexChanged += (_, _) =>
        {
            provider.Items.Clear();
            if (points.SelectedItem is not RecoveryPoint selected) return;
            foreach (var copy in selected.Copies) provider.Items.Add(copy);
            if (provider.Items.Count > 0) provider.SelectedIndex = 0;
            details.Text =
                "SQL backup created: " + selected.SqlCreatedUtc.ToLocalTime().ToString("dd-MMM-yyyy hh:mm tt") +
                " (computer time). Uploaded time does NOT determine the recovery order. " +
                "The SQL header is independently verified after download, before live restoration.";
        };
        provider.Format += (_, e) =>
        {
            if (e.ListItem is CloudBackupCoordinator.CloudSelection copy)
                e.Value = copy.Remote.Provider + "  —  uploaded " +
                    copy.Remote.UploadedUtc.ToLocalTime().ToString("dd-MMM-yyyy hh:mm tt");
        };
        provider.FormattingEnabled = true;

        var confirm = new Button { Text = "Restore selected cloud backup", AutoSize = true };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttonRow = new FlowLayoutPanel {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft
        };
        buttonRow.Controls.Add(confirm);
        buttonRow.Controls.Add(cancel);
        var panel = new TableLayoutPanel {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7,
            Padding = new Padding(AppScale.Scale(18))
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(36)));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(30)));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(42)));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(66)));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(44)));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(48)));
        string newestCloud = groups.Length > 0 ? groups[0].SqlCreatedUtc.ToLocalTime().ToString("dd-MMM HH:mm") : "None";
        string newestLocal = latestLocalBackupUtc?.ToLocalTime().ToString("dd-MMM HH:mm") ?? "None";
        panel.Controls.Add(new Label { Text = "Latest local: " + newestLocal +
            "    |    Latest cloud: " + newestCloud + "    (by SQL backup time)",
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        panel.Controls.Add(points, 0, 1);
        panel.Controls.Add(new Label { Text = "Download from", Dock = DockStyle.Fill }, 0, 2);
        panel.Controls.Add(provider, 0, 3);
        panel.Controls.Add(details, 0, 4);
        var secretRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        secretRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 41));
        secretRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 59));
        secretRow.Controls.Add(new Label { Text = "Cloud recovery passphrase", Dock = DockStyle.Fill }, 0, 0);
        secretRow.Controls.Add(passphrase, 1, 0);
        panel.Controls.Add(secretRow, 0, 5);
        panel.Controls.Add(buttonRow, 0, 6);
        Controls.Add(panel);
        CancelButton = cancel;
        if (points.Items.Count > 0) points.SelectedIndex = 0;
        confirm.Click += (_, _) =>
        {
            if (provider.SelectedItem is not CloudBackupCoordinator.CloudSelection selected ||
                passphrase.Text.Length < 12)
            {
                MessageBox.Show(this, "Choose a recovery point and enter its recovery passphrase.",
                    "Cloud restore", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            SelectedCopy = selected;
            RecoveryPassphrase = passphrase.Text;
            passphrase.Clear();
            DialogResult = DialogResult.OK;
            Close();
        };
        FormClosed += (_, _) => passphrase.Clear();
    }
}
