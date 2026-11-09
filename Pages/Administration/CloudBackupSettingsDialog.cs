using FMSC_SMS.Core;
using FMSC_SMS.Services.Backup;

namespace FMSC_SMS.Pages.Administration;

/// <summary>
/// Modal settings editor launched from the EXISTING Backup / Restore page.
/// No additional navigation page or second backup screen is created.
/// </summary>
internal sealed class CloudBackupSettingsDialog : ApplicationWindowForm
{
    private readonly CheckBox automatic = new() { Text = "Enable automatic backups", AutoSize = true };
    private readonly CheckBox google = new() { Text = "Upload encrypted copy to Google Drive", AutoSize = true };
    private readonly CheckBox dropbox = new() { Text = "Upload encrypted copy to Dropbox", AutoSize = true };
    private readonly ComboBox frequency = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 210 };
    private readonly ComboBox weeklyDay = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 210 };
    private readonly DateTimePicker scheduleTime = new() {
        Format = DateTimePickerFormat.Custom, CustomFormat = "hh:mm tt", ShowUpDown = true, Width = 160
    };
    private readonly NumericUpDown localMax = Limit(), googleMax = Limit(), dropboxMax = Limit();
    private readonly ModernButton driveConnect = ActionButton("Connect Google Drive");
    private readonly ModernButton dropboxConnect = ActionButton("Connect Dropbox");
    private readonly Label recoveryStatus = new() { AutoSize = true, ForeColor = AppTheme.MutedText };
    private readonly ModernButton save = ActionButton("Save Backup Settings");
    private readonly CloudBackupOptions previous;

    internal CloudBackupSettingsDialog()
    {
        Text = "Cloud backup options";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(AppScale.Scale(700), AppScale.Scale(640));
        BackColor = AppTheme.PageBackground;
        previous = CloudBackupSettings.Load();

        frequency.Items.AddRange(new object[] {
            "Manual only", "Every 12 hours", "Daily", "Weekly"
        });
        frequency.SelectedIndex = (int)previous.Frequency;
        scheduleTime.Value = DateTime.Today.Add(previous.LocalScheduledTime.ToTimeSpan());
        weeklyDay.Items.AddRange(Enum.GetNames<DayOfWeek>());
        weeklyDay.SelectedIndex = (int)previous.WeeklyDay;
        automatic.Checked = previous.AutomaticEnabled;
        google.Checked = previous.GoogleDriveEnabled;
        dropbox.Checked = previous.DropboxEnabled;
        localMax.Value = previous.MaximumLocalBackups;
        googleMax.Value = previous.MaximumGoogleDriveBackups;
        dropboxMax.Value = previous.MaximumDropboxBackups;

        var body = new TableLayoutPanel {
            Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = true,
            Padding = new Padding(AppScale.Scale(20)), RowCount = 0
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 47));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 53));
        AddRow(body, "Automatic scheduling", automatic);
        AddRow(body, "Frequency", frequency);
        AddRow(body, "Backup time (computer local)", scheduleTime);
        AddRow(body, "Weekly backup day", weeklyDay);
        AddRow(body, "Maximum automatic local backups", localMax);
        AddRow(body, "Google Drive", google);
        AddRow(body, "Google Drive maximum cloud backups", googleMax);
        AddRow(body, "Google Drive account", driveConnect);
        AddRow(body, "Dropbox", dropbox);
        AddRow(body, "Dropbox maximum cloud backups", dropboxMax);
        AddRow(body, "Dropbox account", dropboxConnect);
        var passphraseButton = ActionButton("Set recovery passphrase");
        AddRow(body, "Encryption recovery", passphraseButton);
        AddRow(body, "Recovery status", recoveryStatus);
        var note = new Label {
            Text = "Cloud copies are encrypted before upload. Keep the recovery passphrase outside this PC. " +
                   "Automatic backups use the same Windows/LocalDB identity that enrolled the providers. " +
                   "Upload failures never change school records.",
            Dock = DockStyle.Fill, ForeColor = AppTheme.MutedText, AutoSize = false
        };
        AddRow(body, "Important", note, AppScale.Scale(87));
        var cancel = ActionButton("Cancel");
        cancel.DialogResult = DialogResult.Cancel;
        var actions = new FlowLayoutPanel {
            Dock = DockStyle.Bottom, Height = AppScale.Scale(62),
            FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(15)
        };
        actions.Controls.Add(save);
        actions.Controls.Add(cancel);
        Controls.Add(body);
        Controls.Add(actions);
        CancelButton = cancel;
        RefreshConnectionState();

        driveConnect.Click += async (_, _) => await ToggleProviderAsync(CloudProvider.GoogleDrive, driveConnect);
        dropboxConnect.Click += async (_, _) => await ToggleProviderAsync(CloudProvider.Dropbox, dropboxConnect);
        passphraseButton.Click += (_, _) => SetRecoveryPassphrase();
        save.Click += async (_, _) => await SaveAsync();
    }

    private async Task ToggleProviderAsync(CloudProvider provider, ModernButton button)
    {
        button.Enabled = false;
        try
        {
            if (CloudBackupOAuth.IsConnected(provider))
            {
                if (MessageBox.Show(this, "Disconnect this cloud backup account?", "Disconnect",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    await CloudBackupOAuth.DisconnectAsync(provider);
            }
            else
                await CloudBackupOAuth.ConnectAsync(provider);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Cloud account", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            RefreshConnectionState();
            button.Enabled = true;
        }
    }

    private void SetRecoveryPassphrase()
    {
        if (CloudBackupSettings.HasLocalRecoveryPassphrase &&
            MessageBox.Show(this,
                "Changing the recovery passphrase does not change encryption on existing backups. " +
                "Existing cloud copies still require the old passphrase. Confirm you have safely retained it.",
                "Existing backups keep their original passphrase",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        using var dialog = new Form {
            Text = "Cloud recovery passphrase", StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false,
            ClientSize = new Size(AppScale.Scale(480), AppScale.Scale(220))
        };
        var first = new TextBox { UseSystemPasswordChar = true, Width = AppScale.Scale(400) };
        var again = new TextBox { UseSystemPasswordChar = true, Width = AppScale.Scale(400) };
        var note = new Label {
            Text = "At least 12 characters. Keep it in a safe place outside this PC. " +
                   "Without it, cloud backups cannot be recovered after the computer is lost.",
            AutoSize = true, MaximumSize = new Size(AppScale.Scale(440), 0)
        };
        var button = new Button { Text = "Save recovery passphrase", AutoSize = true };
        var layout = new FlowLayoutPanel {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            Padding = new Padding(15), WrapContents = false
        };
        layout.Controls.Add(note);
        layout.Controls.Add(new Label { Text = "Recovery passphrase:", AutoSize = true });
        layout.Controls.Add(first);
        layout.Controls.Add(new Label { Text = "Confirm passphrase:", AutoSize = true });
        layout.Controls.Add(again);
        layout.Controls.Add(button);
        dialog.Controls.Add(layout);
        button.Click += (_, _) =>
        {
            if (first.Text.Length < 12 || first.Text != again.Text)
            {
                MessageBox.Show(dialog, "Enter a matching passphrase of at least 12 characters.",
                    "Recovery passphrase", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try
            {
                CloudBackupSettings.SaveRecoveryPassphrase(first.Text);
                first.Clear(); again.Clear();
                dialog.DialogResult = DialogResult.OK;
                dialog.Close();
            }
            catch (Exception ex) { MessageBox.Show(dialog, ex.Message); }
        };
        dialog.ShowDialog(this);
        RefreshConnectionState();
    }

    private async Task SaveAsync()
    {
        save.Enabled = false;
        try
        {
            if (google.Checked && !CloudBackupOAuth.IsConnected(CloudProvider.GoogleDrive))
                throw new InvalidOperationException("Connect Google Drive before enabling it.");
            if (dropbox.Checked && !CloudBackupOAuth.IsConnected(CloudProvider.Dropbox))
                throw new InvalidOperationException("Connect Dropbox before enabling it.");
            if (automatic.Checked && (!google.Checked && !dropbox.Checked))
                throw new InvalidOperationException("Choose at least one cloud destination for automatic backup.");
            if (automatic.Checked && frequency.SelectedIndex == 0)
                throw new InvalidOperationException("Select a scheduled frequency or disable automatic backup.");
            if ((automatic.Checked || google.Checked || dropbox.Checked) &&
                !CloudBackupSettings.HasLocalRecoveryPassphrase)
                throw new InvalidOperationException("Set and independently retain a recovery passphrase first.");
            var newSettings = new CloudBackupOptions {
                AutomaticEnabled = automatic.Checked,
                GoogleDriveEnabled = google.Checked, DropboxEnabled = dropbox.Checked,
                Frequency = (CloudBackupFrequency)frequency.SelectedIndex,
                LocalScheduledTime = TimeOnly.FromDateTime(scheduleTime.Value),
                WeeklyDay = (DayOfWeek)weeklyDay.SelectedIndex,
                MaximumLocalBackups = (int)localMax.Value,
                MaximumGoogleDriveBackups = (int)googleMax.Value,
                MaximumDropboxBackups = (int)dropboxMax.Value
            };
            newSettings.Validate();
            if (newSettings.AutomaticEnabled && BackupSettings.GetBackupFolder().Length == 0)
                throw new InvalidOperationException("Select a local backup folder on the Backup / Restore page before scheduling.");
            // Persist settings first, then register the task. If Windows
            // rejects task registration, restore the previously saved state
            // so FMSC never claims automatic backup is active when it isn't.
            CloudBackupSettings.Save(newSettings);
            try { await CloudBackupTaskSchedule.ConfigureAsync(newSettings.AutomaticEnabled); }
            catch
            {
                try { CloudBackupSettings.Save(previous); }
                catch (Exception rollbackError)
                {
                    AppExceptionHandler.LogNonFatal(rollbackError,
                        "Could not roll back cloud backup settings after task registration failure");
                }
                throw;
            }
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Backup settings not saved",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { save.Enabled = true; }
    }

    private void RefreshConnectionState()
    {
        driveConnect.Text = CloudBackupOAuth.IsConnected(CloudProvider.GoogleDrive)
            ? "Disconnect Google Drive" : "Connect Google Drive";
        dropboxConnect.Text = CloudBackupOAuth.IsConnected(CloudProvider.Dropbox)
            ? "Disconnect Dropbox" : "Connect Dropbox";
        recoveryStatus.Text = CloudBackupSettings.HasLocalRecoveryPassphrase
            ? "Recovery passphrase enrolled on this Windows account"
            : "Recovery passphrase not configured";
    }
    private static ModernButton ActionButton(string title) =>
        new() { Text = title, AutoSize = true, Height = AppMetrics.ButtonHeight, ThemeRole = "Secondary" };
    private static NumericUpDown Limit() => new() {
        Minimum = 1, Maximum = 365, Value = 7, Width = 110
    };
    private static void AddRow(TableLayoutPanel panel, string title, Control input, int height = 0)
    {
        int index = panel.RowCount++;
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute,
            height > 0 ? height : AppScale.Scale(45)));
        panel.Controls.Add(new Label {
            Text = title, AutoSize = false, Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = AppTheme.Text
        }, 0, index);
        input.Anchor = AnchorStyles.Left;
        panel.Controls.Add(input, 1, index);
    }
}
