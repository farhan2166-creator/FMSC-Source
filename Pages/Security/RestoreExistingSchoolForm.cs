using FMSC_SMS.Core;
using FMSC_SMS.Core.Updates;
using FMSC_SMS.Services.Backup;

namespace FMSC_SMS.Pages.Security;

/// <summary>
/// Secure first-start recovery from a detached .bak or remote encrypted copy.
/// This dialog does not permit recovery over an existing school or bypass
/// either device authorization or the recovered Administrator's credentials.
/// </summary>
internal sealed class RestoreExistingSchoolForm : ApplicationWindowForm
{
    private readonly ComboBox source = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ListBox available = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly TextBox localPath = new() { Dock = DockStyle.Fill, ReadOnly = true };
    private readonly TextBox administrator = new() { Dock = DockStyle.Fill };
    private readonly TextBox password = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly TextBox passphrase = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly TextBox diagnostics = new() {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Vertical, Visible = false
    };
    private readonly Label status = new() {
        Dock = DockStyle.Fill, AutoEllipsis = true,
        ForeColor = AppTheme.MutedText, Text = "Authorize this computer, then choose the school's recovery point."
    };
    private readonly Button authorize = new() { Text = "Authorize This Computer", Dock = DockStyle.Fill };
    private readonly Button chooseLocal = new() { Text = "Choose .bak File…", Dock = DockStyle.Fill };
    private readonly Button connect = new() { Text = "Connect Cloud Account", Dock = DockStyle.Fill };
    private readonly Button refresh = new() { Text = "Find Cloud Backups", Dock = DockStyle.Fill };
    private readonly Button recover = new() { Text = "Restore Existing School", Dock = DockStyle.Fill };
    private readonly Button help = new() { Text = "Forgot Administrator Login?", Dock = DockStyle.Fill };
    private readonly Button close = new() { Text = "Cancel", Dock = DockStyle.Fill };
    private readonly LinkLabel toggle = new() { Text = "Show technical details", Dock = DockStyle.Fill };
    private bool busy;

    // Internal delegates keep modal behavior testable without SQL, credentials or browser sign-in.
    internal Func<bool> ConfirmRecovery { get; set; }
    internal Action ShowRecoveryComplete { get; set; }
    internal Action<string> ShowRecoveryFailure { get; set; }
    internal Func<string?, CloudRemoteBackup?, string, string, string,
        IProgress<BackupProgressUpdate>, IReadOnlyList<CloudRemoteBackup>, Task> RecoveryAction { get; set; } =
        (local, remote, key, name, password, progress, alternatives) =>
            ReplacementSchoolRecovery.RecoverAsync(local, remote, key, name, password,
                progress, alternatives: alternatives);

    internal RestoreExistingSchoolForm()
    {
        ConfirmRecovery = () => MessageBox.Show(this,
            "Recover the selected school to this NEW computer? " +
            "FMSC will refuse if an existing school database is detected. " +
            "The computer must be publisher-authorized and the original Administrator credentials must match.",
            "Confirm school recovery", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        ShowRecoveryComplete = () => ModernMessageDialog.ShowInfo(this,
            "The verified school database has been recovered. Sign in with the original school Administrator account.",
            "School Recovery Completed");
        ShowRecoveryFailure = message => ModernMessageDialog.ShowWarning(this, message, "Recovery not completed");
        Text = "FMSC — Restore an Existing School";
        ClientSize = new Size(AppScale.Scale(830), AppScale.Scale(700));
        MinimumSize = new Size(AppScale.Scale(710), AppScale.Scale(580));
        StartPosition = FormStartPosition.CenterParent;
        BackColor = AppTheme.PageBackground;
        FormBorderStyle = FormBorderStyle.Sizable;
        ShowInTaskbar = false;
        var root = new TableLayoutPanel {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 12,
            Padding = new Padding(AppScale.Scale(20)), AutoScroll = true
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 66));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(46)));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(42)));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(43)));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(43)));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 35));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(42)));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(42)));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(42)));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(42)));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(45)));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(45)));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
        void Full(Control control, int row)
        {
            root.Controls.Add(control, 0, row);
            root.SetColumnSpan(control, 2);
        }
        void Field(int row, string label, Control control)
        {
            root.Controls.Add(new Label {
                Text = label, Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft, ForeColor = AppTheme.Text
            }, 0, row);
            root.Controls.Add(control, 1, row);
        }
        Full(new Label {
            Dock = DockStyle.Fill, Text = "Recover a school on this computer",
            Font = AppTheme.PageTitleFont, ForeColor = AppTheme.Primary
        }, 0);
        Full(authorize, 1);
        source.Items.AddRange(new object[] { "Local .bak file", "Google Drive", "Dropbox" });
        source.SelectedIndex = 0;
        Field(2, "Backup location", source);
        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
        for (int i = 0; i < 3; i++) actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
        actions.Controls.Add(chooseLocal, 0, 0);
        actions.Controls.Add(connect, 1, 0);
        actions.Controls.Add(refresh, 2, 0);
        Full(actions, 3);
        Full(available, 4);
        Field(5, "Local backup file", localPath);
        Field(6, "School Administrator username", administrator);
        Field(7, "Administrator password", password);
        Field(8, "Cloud passphrase (if different)", passphrase);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
        for (int i = 0; i < 3; i++) footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
        footer.Controls.Add(recover, 0, 0);
        footer.Controls.Add(help, 1, 0);
        footer.Controls.Add(close, 2, 0);
        Full(footer, 9);
        var info = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        info.RowStyles.Add(new RowStyle(SizeType.Percent, 70));
        info.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
        info.Controls.Add(status, 0, 0);
        info.Controls.Add(toggle, 0, 1);
        Full(info, 10);
        Full(diagnostics, 11);
        Controls.Add(root);
        source.SelectedIndexChanged += (_, _) => RefreshSourceDisplay();
        authorize.Click += (_, _) => {
            if (busy) return;
            using var approval = new UpdateConnectionForm();
            approval.ShowDialog(this);
        };
        chooseLocal.Click += (_, _) => ChooseLocal();
        connect.Click += async (_, _) => await ConnectCloudAsync();
        refresh.Click += async (_, _) => await ListCloudAsync();
        recover.Click += async (_, _) => await RecoverAsync();
        close.Click += (_, _) => { if (!busy) { DialogResult = DialogResult.Cancel; Close(); } };
        help.Click += (_, _) => ModernMessageDialog.ShowWarning(this,
            "Administrator credentials are required to recover an existing school on a new computer. " +
            "If they are forgotten, request a separately approved recovery procedure from the FMSC publisher. " +
            "This wizard will not bypass school ownership or reset credentials without an additional secure approval.",
            "Protected Administrator Recovery");
        toggle.LinkClicked += (_, _) => {
            diagnostics.Visible = !diagnostics.Visible;
            toggle.Text = diagnostics.Visible ? "Hide technical details" : "Show technical details";
        };
        FormClosing += (_, e) => { if (busy) e.Cancel = true; };
        RefreshSourceDisplay();
        Shown += (_, _) => administrator.Focus();
    }

    private CloudProvider SelectedProvider =>
        source.SelectedIndex == 1 ? CloudProvider.GoogleDrive : CloudProvider.Dropbox;
    private bool IsCloud => source.SelectedIndex is 1 or 2;
    private void RefreshSourceDisplay()
    {
        chooseLocal.Enabled = !busy && !IsCloud;
        connect.Enabled = !busy && IsCloud;
        refresh.Enabled = !busy && IsCloud;
        passphrase.Enabled = IsCloud;
        localPath.Enabled = !IsCloud;
        available.Items.Clear();
        if (IsCloud)
            status.Text = "Connect " + SelectedProvider + " and select a cloud recovery point. Its date is verified after decryption.";
        else
            status.Text = "Select an existing FMSC .bak file. The backup will be checked in an isolated database.";
    }

    private void ChooseLocal()
    {
        if (busy || IsCloud) return;
        using var picker = new OpenFileDialog {
            Title = "Select FMSC school SQL backup",
            Filter = "SQL backup (*.bak)|*.bak|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        localPath.Text = picker.FileName;
        available.Items.Clear();
        available.Items.Add(Path.GetFileName(picker.FileName));
        available.SelectedIndex = 0;
    }

    private async Task ConnectCloudAsync()
    {
        if (busy || !IsCloud) return;
        SetBusy(true);
        try
        {
            await CloudBackupOAuth.ConnectForReplacementRecoveryAsync(SelectedProvider);
            status.Text = "Cloud account connected. Select Find Cloud Backups.";
        }
        catch (Exception error) { ReportError(error); }
        finally { SetBusy(false); }
    }

    private async Task ListCloudAsync()
    {
        if (busy || !IsCloud) return;
        CloudProvider preferred = SelectedProvider;
        SetBusy(true);
        try
        {
            status.Text = "Checking connected cloud accounts for encrypted FMSC backups…";
            var inventory = await CloudBackupOperations.BrowseAsync(CloudBackupOAuth.IsConnected,
                CloudBackupTransport.ListAsync,
                (provider, error) => AppExceptionHandler.LogNonFatal(error,
                    provider + " replacement computer discovery"), CancellationToken.None);
            var copies = inventory.Copies;
            var warnings = inventory.Warnings;
            if (IsDisposed) return;
            available.Items.Clear();
            var groups = copies.GroupBy(x => (x.PackageSha256.ToLowerInvariant(),
                    x.SqlBackupCreatedUtc.Ticks))
                .OrderByDescending(g => g.First().SqlBackupCreatedUtc);
            foreach (var group in groups)
            {
                var eligible = group.OrderBy(x => x.Provider == preferred ? 0 : 1)
                    .DistinctBy(x => (x.Provider, x.RemoteId)).ToArray();
                available.Items.Add(new RecoverySelection(eligible));
            }
            status.Text = available.Items.Count == 0
                ? "No FMSC cloud backups could be confirmed. " + string.Join(" ", warnings)
                : available.Items.Count + " recovery point(s) found. Select a point; cloud copies are verified again before recovery. " +
                    string.Join(" ", warnings);
            if (available.Items.Count > 0) available.SelectedIndex = 0;
        }
        catch (Exception error) { ReportError(error); }
        finally { SetBusy(false); }
    }

    private sealed record RecoverySelection(IReadOnlyList<CloudRemoteBackup> Copies)
    {
        internal CloudRemoteBackup Backup => Copies[0];
        public override string ToString() =>
            Backup.SqlBackupCreatedUtc.ToLocalTime().ToString("dd-MMM-yyyy hh:mm tt") +
            "   —   " + string.Join(" + ", Copies.Select(x => x.Provider).Distinct()) +
            "   (date provisional until verified)";
    }

    private async Task RecoverAsync()
    {
        if (busy) return;
        string? local = null;
        CloudRemoteBackup? remote = null;
        IReadOnlyList<CloudRemoteBackup> alternatives = Array.Empty<CloudRemoteBackup>();
        if (IsCloud)
        {
            if (available.SelectedItem is not RecoverySelection selection)
            { status.Text = "Choose a cloud recovery point first."; return; }
            remote = selection.Backup;
            alternatives = selection.Copies;
        }
        else
        {
            local = localPath.Text.Trim();
            if (!File.Exists(local))
            { status.Text = "Choose a valid SQL backup file."; return; }
        }
        if (string.IsNullOrWhiteSpace(administrator.Text) || password.Text.Length == 0)
        { status.Text = "Enter the original school Administrator credentials."; return; }
        if (!ConfirmRecovery()) return;
        string name = administrator.Text, secret = password.Text;
        string key = passphrase.Text.Length == 0 ? secret : passphrase.Text;
        SetBusy(true);
        diagnostics.Clear();
        try
        {
            await RecoveryAction(local, remote, key, name, secret,
                new Progress<BackupProgressUpdate>(update =>
                {
                    if (IsDisposed) return;
                    status.Text = update.Display;
                    if (update.Detail.Length > 0)
                        diagnostics.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " +
                            update.Display + ": " + update.Detail + Environment.NewLine);
                }), alternatives);
            if (IsDisposed) return;
            password.Clear();
            passphrase.Clear();
            ShowRecoveryComplete();
            SetBusy(false); // Backend activation has finished; allow the successful modal close.
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception error) { ReportError(error); }
        finally
        {
            secret = "";
            key = "";
            if (!IsDisposed) SetBusy(false);
        }
    }

    private void ReportError(Exception ex)
    {
        AppExceptionHandler.LogNonFatal(ex, "Replacement-computer school recovery");
        status.Text = DbMessage.UserMessage(ex);
        diagnostics.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  Recovery stopped: " +
            DbMessage.UserMessage(ex) + Environment.NewLine);
        ShowRecoveryFailure(status.Text);
    }

    private void SetBusy(bool isBusy)
    {
        busy = isBusy;
        source.Enabled = !busy;
        authorize.Enabled = !busy;
        chooseLocal.Enabled = !busy && !IsCloud;
        connect.Enabled = !busy && IsCloud;
        refresh.Enabled = !busy && IsCloud;
        recover.Enabled = !busy;
        help.Enabled = !busy;
        close.Enabled = !busy;
        UseWaitCursor = busy;
    }
}
