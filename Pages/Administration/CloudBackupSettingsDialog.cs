using FMSC_SMS.Core;
using FMSC_SMS.Services.Backup;

namespace FMSC_SMS.Pages.Administration;

/// <summary>
/// Modal settings editor launched from the EXISTING Backup / Restore page.
/// No additional navigation page or second backup screen is created.
/// </summary>
internal sealed class CloudBackupSettingsDialog : ApplicationWindowForm
{
    private readonly ModernCheckBox automatic = Option("Enable automatic backups");
    private readonly ModernCheckBox local = Option("Keep local SQL backups");
    private readonly ModernCheckBox google = Option("Upload encrypted backups");
    private readonly ModernCheckBox dropbox = Option("Upload encrypted backups");
    private readonly ModernComboBox frequency = HumanUi.Combo();
    private readonly ModernComboBox weeklyDay = HumanUi.Combo();
    private readonly ModernDatePicker scheduleTime = new() { TimeOnly = true };
    private readonly ModernNumericTextBox localMax = Limit(), googleMax = Limit(), dropboxMax = Limit();
    private readonly ModernNumericTextBox maximumQueueMb = new() {
        Minimum = 512, Maximum = 1_048_576, ThousandsSeparator = true
    };
    private readonly ModernButton driveConnect = ActionButton("Connect Google Drive");
    private readonly ModernButton driveRepair = ActionButton("Verify / Reconnect Drive");
    private readonly ModernButton dropboxConnect = ActionButton("Connect Dropbox");
    private readonly ModernButton dropboxRepair = ActionButton("Verify / Reconnect Dropbox");
    private readonly Label recoveryStatus = Note("");
    private readonly ModernButton save = new() { Text = "Save Backup Settings", ThemeRole = "Primary" };
    private readonly ModernButton cancel = ActionButton("Cancel");
    private readonly ModernButton passphraseButton = ActionButton("Set recovery credential");
    private readonly CloudBackupOptions previous;
    private readonly string? settingsWarning;
    private bool operationBusy;
    private bool closingAfterSave;

    internal CloudBackupSettingsDialog()
    {
        Text = "Cloud Backup Options";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(AppScale.Scale(740), AppScale.Scale(600));
        BackColor = AppTheme.PageBackground;
        Font = AppTheme.NormalFont;
        previous = CloudBackupSettings.LoadForEditing(out settingsWarning);

        frequency.Items.AddRange(new object[] {
            "Manual only", "Every 12 hours", "Daily", "Weekly"
        });
        frequency.SelectedIndex = (int)previous.Frequency;
        scheduleTime.Value = DateTime.Today.Add(previous.LocalScheduledTime.ToTimeSpan());
        weeklyDay.Items.AddRange(Enum.GetNames<DayOfWeek>());
        weeklyDay.SelectedIndex = (int)previous.WeeklyDay;
        automatic.Checked = previous.AutomaticEnabled;
        local.Checked = previous.LocalEnabled;
        google.Checked = previous.GoogleDriveEnabled;
        dropbox.Checked = previous.DropboxEnabled;
        localMax.Value = previous.MaximumLocalBackups;
        googleMax.Value = previous.MaximumGoogleDriveBackups;
        dropboxMax.Value = previous.MaximumDropboxBackups;
        maximumQueueMb.Value = previous.MaximumPendingQueueMegabytes;

        BuildLayout();
        CancelButton = cancel;
        SetSaveShortcut(save);
        RefreshConnectionState();
        Shown += (_, _) => {
            if (settingsWarning is not null)
                ModernMessageDialog.ShowWarning(this, settingsWarning, "Backup Settings Need Repair");
        };

        driveConnect.Click += async (_, _) => await ToggleProviderAsync(CloudProvider.GoogleDrive, driveConnect);
        dropboxConnect.Click += async (_, _) => await ToggleProviderAsync(CloudProvider.Dropbox, dropboxConnect);
        driveRepair.Click += async (_, _) => await VerifyOrReconnectAsync(CloudProvider.GoogleDrive);
        dropboxRepair.Click += async (_, _) => await VerifyOrReconnectAsync(CloudProvider.Dropbox);
        passphraseButton.Click += (_, _) => SetRecoveryPassphrase();
        save.Click += async (_, _) => await SaveAsync();
        FormClosing += (_, e) =>
        {
            // A pending OAuth connection or settings/task registration can
            // outlive the dialog. Do not allow disposal or a false Cancel.
            if (operationBusy && !closingAfterSave)
            {
                e.Cancel = true;
                DialogResult = DialogResult.None;
            }
        };
    }

    private void BuildLayout()
    {
        var tabs = new TabControl { Dock = DockStyle.Fill, Margin = Padding.Empty };
        var destinations = new TableLayoutPanel {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty
        };
        destinations.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        destinations.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        destinations.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        destinations.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var localSettings = Section("Local Backups", local,
            Field("Maximum local backups", localMax));
        localSettings.Margin = new Padding(0, 0, 0, AppMetrics.SpacingMd);
        destinations.Controls.Add(localSettings, 0, 0);
        destinations.SetColumnSpan(localSettings, 2);

        var driveSettings = Section("Google Drive", google,
            Field("Maximum cloud backups", googleMax), driveConnect, driveRepair);
        driveSettings.Margin = new Padding(0, 0, AppMetrics.SpacingSm, 0);
        destinations.Controls.Add(driveSettings, 0, 1);
        destinations.Controls.Add(Section("Dropbox", dropbox,
            Field("Maximum cloud backups", dropboxMax), dropboxConnect, dropboxRepair), 1, 1);

        AddTab(tabs, "Schedule", Section("Automatic Backups", automatic,
            Field("Frequency", frequency), Field("Backup time (local)", scheduleTime),
            Field("Weekly backup day", weeklyDay),
            Note("Automatic backups use the Windows account and LocalDB instance that connected the cloud accounts.")));
        AddTab(tabs, "Destinations", destinations);
        AddTab(tabs, "Recovery", Section("Cloud Recovery", recoveryStatus, passphraseButton,
            Note("Use your Administrator password or a separate passphrase. Keep the credential used for each backup."),
            Field("Maximum encrypted upload queue (MB)", maximumQueueMb)));

        cancel.DialogResult = DialogResult.Cancel;
        var root = new TableLayoutPanel {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
            Padding = new Padding(AppMetrics.SpacingMd)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var actions = HumanUi.DialogButtons(cancel, save);
        actions.Margin = new Padding(0, AppMetrics.SpacingMd, 0, 0);
        root.Controls.Add(tabs, 0, 0);
        root.Controls.Add(actions, 0, 1);
        Controls.Add(root);
    }

    private static void AddTab(TabControl tabs, string title, Control content)
    {
        var page = new TabPage(title) { BackColor = AppTheme.Surface };
        var viewport = new Panel {
            Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(AppMetrics.SpacingMd)
        };
        viewport.Controls.Add(content);
        page.Controls.Add(viewport);
        tabs.TabPages.Add(page);
    }

    private static TableLayoutPanel Section(string title, params Control[] controls)
    {
        var section = new TableLayoutPanel {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top, ColumnCount = 1, RowCount = controls.Length + 1,
            Margin = Padding.Empty
        };
        section.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var heading = HumanUi.GroupHeading(title);
        heading.Margin = new Padding(0, 0, 0, AppMetrics.SpacingSm);
        section.RowStyles.Add(new RowStyle(SizeType.Absolute, HumanUi.GroupHeadingHeight + heading.Margin.Vertical));
        section.Controls.Add(heading, 0, 0);
        for (int index = 0; index < controls.Length; index++)
        {
            section.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            section.Controls.Add(controls[index], 0, index + 1);
        }
        return section;
    }

    private static Control Field(string caption, Control input)
    {
        var field = HumanUi.Field(caption, input);
        field.Height = AppMetrics.FieldRowHeight;
        return field;
    }

    private static ModernCheckBox Option(string text) => new() {
        Text = text, OptionStyle = true, AutoSize = true, Anchor = AnchorStyles.Left,
        Margin = new Padding(0, 0, 0, AppMetrics.SpacingSm)
    };

    private static Label Note(string text) => new() {
        Text = text, AutoSize = true, Dock = DockStyle.Fill, ForeColor = AppTheme.MutedText,
        Margin = new Padding(0, AppMetrics.SpacingXs, 0, AppMetrics.SpacingSm)
    };

    private bool TryBeginOperation()
    {
        if (operationBusy || IsDisposed || Disposing) return false;
        operationBusy = true;
        driveConnect.Enabled = false;
        driveRepair.Enabled = false;
        dropboxConnect.Enabled = false;
        dropboxRepair.Enabled = false;
        save.Enabled = false;
        cancel.Enabled = false;
        passphraseButton.Enabled = false;
        return true;
    }

    private void EndOperation()
    {
        operationBusy = false;
        if (IsDisposed || Disposing) return;
        driveConnect.Enabled = true;
        driveRepair.Enabled = true;
        dropboxConnect.Enabled = true;
        dropboxRepair.Enabled = true;
        save.Enabled = true;
        cancel.Enabled = true;
        passphraseButton.Enabled = true;
    }

    private async Task ToggleProviderAsync(CloudProvider provider, ModernButton button)
    {
        if (!TryBeginOperation()) return;
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
            if (!IsDisposed && !Disposing) RefreshConnectionState();
            EndOperation();
        }
    }

    private async Task VerifyOrReconnectAsync(CloudProvider provider)
    {
        if (!TryBeginOperation()) return;
        try
        {
            bool healthy = false;
            try
            {
                await CloudBackupOAuth.CheckConnectionAsync(provider);
                healthy = true;
            }
            catch (Exception error)
            {
                AppExceptionHandler.LogNonFatal(error, provider + " account connection health check failed");
            }
            if (healthy)
            {
                MessageBox.Show(this, provider + " responded successfully. Its connection is ready.",
                    "Cloud connection verified", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show(this, provider + " authorization needs attention. " +
                    "Reconnect using the browser? Existing saved credentials will remain until new authorization succeeds.",
                    "Reconnect cloud backup account", MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            await CloudBackupOAuth.ReconnectAsync(provider);
            await CloudBackupOAuth.CheckConnectionAsync(provider);
            MessageBox.Show(this, provider + " is reconnected and ready.",
                "Cloud account recovered", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception error)
        {
            AppExceptionHandler.LogNonFatal(error, provider + " cloud reconnect failed");
            MessageBox.Show(this, provider + " could not be reconnected. Other backup destinations are unchanged. " +
                "Open technical diagnostics or retry when the network is available.",
                "Cloud account needs attention", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            if (!IsDisposed && !Disposing) RefreshConnectionState();
            EndOperation();
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
        using var dialog = new ApplicationWindowForm {
            Text = "Cloud Recovery Credential", StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false,
            ShowInTaskbar = false, Font = AppTheme.NormalFont, BackColor = AppTheme.PageBackground,
            ClientSize = new Size(AppScale.Scale(540), AppScale.Scale(360))
        };
        var useAdministrator = Option("Use my Administrator password");
        useAdministrator.Checked = useAdministrator.Enabled = CurrentUser.IsAdministrator;
        var first = HumanUi.TextBox(maxLength: 32767);
        var again = HumanUi.TextBox(maxLength: 32767);
        first.UseSystemPasswordChar = again.UseSystemPasswordChar = true;
        var note = Note("");
        var button = new ModernButton { Text = "Save Recovery Credential", ThemeRole = "Primary" };
        var close = new ModernButton {
            Text = UiText.Actions.Cancel, ThemeRole = "Secondary", DialogResult = DialogResult.Cancel
        };
        var firstField = Field("Recovery credential", first);
        var againField = Field("Confirm passphrase", again);
        var layout = new TableLayoutPanel {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(AppMetrics.SpacingLg)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.FieldRowHeight));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(useAdministrator, 0, 0);
        layout.Controls.Add(note, 0, 1);
        layout.Controls.Add(firstField, 0, 2);
        layout.Controls.Add(againField, 0, 3);
        layout.Controls.Add(HumanUi.DialogButtons(close, button), 0, 5);
        dialog.Controls.Add(layout);
        dialog.AcceptButton = button;
        dialog.CancelButton = close;
        dialog.FormClosed += (_, _) => { first.Clear(); again.Clear(); };
        void UpdateMode()
        {
            first.Clear();
            again.Clear();
            first.PlaceholderText = useAdministrator.Checked ? "Current Administrator password" : "At least 12 characters";
            againField.Visible = !useAdministrator.Checked;
            note.Text = useAdministrator.Checked
                ? "Backups use this password until you update the recovery credential here. A stolen backup allows offline password guessing."
                : "Use at least 12 characters and keep the passphrase somewhere safe outside this PC.";
        }
        useAdministrator.CheckedChanged += (_, _) => UpdateMode();
        UpdateMode();
        button.Click += (_, _) =>
        {
            if (useAdministrator.Checked)
            {
                if (!AuthenticationService.VerifyAdministratorPassword(first.Text, out string message))
                {
                    ModernMessageDialog.ShowWarning(dialog, message, "Administrator Password");
                    return;
                }
            }
            else if (first.Text.Length < 12 || first.Text != again.Text)
            {
                MessageBox.Show(dialog, "Enter a matching passphrase of at least 12 characters.",
                    "Recovery passphrase", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try
            {
                CloudBackupCoordinator.SetRecoveryPassphrase(first.Text, useAdministrator.Checked);
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
        if (!TryBeginOperation()) return;
        try
        {
            if (google.Checked && !CloudBackupOAuth.IsConnected(CloudProvider.GoogleDrive))
                throw new InvalidOperationException("Connect Google Drive before enabling it.");
            if (dropbox.Checked && !CloudBackupOAuth.IsConnected(CloudProvider.Dropbox))
                throw new InvalidOperationException("Connect Dropbox before enabling it.");
            if (!local.Checked && !google.Checked && !dropbox.Checked)
                throw new InvalidOperationException("Choose at least one backup destination.");
            if (automatic.Checked && frequency.SelectedIndex == 0)
                throw new InvalidOperationException("Select a scheduled frequency or disable automatic backup.");
            if ((google.Checked || dropbox.Checked) &&
                !CloudBackupSettings.HasLocalRecoveryPassphrase)
                throw new InvalidOperationException("Set and independently retain a recovery passphrase first.");
            var newSettings = new CloudBackupOptions {
                AutomaticEnabled = automatic.Checked,
                LocalEnabled = local.Checked,
                GoogleDriveEnabled = google.Checked, DropboxEnabled = dropbox.Checked,
                Frequency = (CloudBackupFrequency)frequency.SelectedIndex,
                LocalScheduledTime = TimeOnly.FromDateTime(scheduleTime.Value),
                WeeklyDay = (DayOfWeek)weeklyDay.SelectedIndex,
                MaximumLocalBackups = (int)localMax.Value,
                MaximumGoogleDriveBackups = (int)googleMax.Value,
                MaximumDropboxBackups = (int)dropboxMax.Value,
                MaximumPendingQueueMegabytes = (int)maximumQueueMb.Value
            };
            newSettings.Validate();
            if (newSettings.LocalEnabled && BackupSettings.GetBackupFolder().Length == 0)
                throw new InvalidOperationException("Select a local backup folder on the Backup / Restore page before scheduling.");
            // Persist settings first, then register the task. If Windows
            // rejects task registration, restore the previously saved state
            // so FMSC never claims automatic backup is active when it isn't.
            if (settingsWarning is not null) CloudBackupSettings.PreserveUnreadableSettings();
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
            closingAfterSave = true;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
                MessageBox.Show(this, ex.Message, "Backup settings not saved",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { EndOperation(); }
    }

    private void RefreshConnectionState()
    {
        driveConnect.Text = CloudBackupOAuth.IsConnected(CloudProvider.GoogleDrive)
            ? "Disconnect Google Drive" : "Connect Google Drive";
        dropboxConnect.Text = CloudBackupOAuth.IsConnected(CloudProvider.Dropbox)
            ? "Disconnect Dropbox" : "Connect Dropbox";
        recoveryStatus.Text = CloudBackupSettings.HasLocalRecoveryPassphrase
            ? "Recovery credential saved for this Windows account"
            : "Recovery credential not configured";
    }
    private static ModernButton ActionButton(string title) => new() {
        Text = title, AutoSize = true, Height = AppMetrics.ButtonHeight, ThemeRole = "Secondary",
        Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 0, AppMetrics.SpacingSm)
    };
    private static ModernNumericTextBox Limit() => new() { Minimum = 1, Maximum = 365, Value = 7 };
}
