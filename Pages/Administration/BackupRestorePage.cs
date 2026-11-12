using FMSC_SMS.Core;
using FMSC_SMS.Services.Backup;
using System.Security.Cryptography;
namespace FMSC_SMS.Pages.Administration;
public sealed class BackupRestorePage : V2Page, IBusyPage
{
    readonly ModernTextBox backupFolder = HumanUi.TextBox(false, 500);
    readonly ModernTextBox restoreFile = HumanUi.TextBox(false, 500);
    readonly bool canRestore = CurrentUser.HasPermission("Backup.Restore");
    ModernButton backupNowButton = null!;
    private readonly Label operationStage = new() {
        Dock = DockStyle.Top, Height = AppScale.Scale(28),
        ForeColor = AppTheme.Primary, Font = AppTheme.FontSmallBold,
        Text = "Ready for backup or restore", TextAlign = ContentAlignment.MiddleLeft
    };
    private readonly LinkLabel toggleTechnicalDetails = new() {
        Dock = DockStyle.Top, Height = AppScale.Scale(24),
        Text = "Show technical details", LinkColor = AppTheme.Primary,
        TextAlign = ContentAlignment.MiddleLeft
    };
    private readonly TextBox technicalDetails = new() {
        Dock = DockStyle.Top, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Vertical, Height = AppScale.Scale(110), Visible = false
    };
    private readonly Label destinationResults = new() {
        Dock = DockStyle.Top, Height = AppScale.Scale(24),
        ForeColor = AppTheme.MutedText, AutoEllipsis = true,
        Text = "Select destinations in Backup Settings."
    };
    ModernButton? retryPendingButton;
    ModernButton? cloudSettingsButton;
    ModernButton? cloudChooseRestoreButton;
    ModernButton? selectRecoveryButton;
    readonly DataGridView availableBackupsGrid = CreateAvailableBackupsGrid();
    readonly Label availableBackupsStatus = new() {
        Dock = DockStyle.Top, Height = AppScale.Scale(42), AutoEllipsis = true,
        ForeColor = AppTheme.MutedText, TextAlign = ContentAlignment.MiddleLeft
    };
    int recoveryCatalogueVersion;
    private readonly ListBox recordedCloudActivity = new() {
        Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true
    };
    CloudBackupCoordinator.CloudSelection? selectedCloud;
    IReadOnlyList<CloudRemoteBackup> selectedCloudAlternatives = Array.Empty<CloudRemoteBackup>();
    string cloudRecoveryPassphrase = string.Empty;
    string selectedLocalRecoverySha256 = string.Empty;
    Action? refreshCloudStatus;
    ModernButton? restoreButton;
    ModernButton? locateFileButton;
    bool isBusy;
    /* Block navigation, sign-out and closing while backup or restore is running. */
    string busyMessage = string.Empty;
    /// <summary>Leaving disposes the page; sign-out or closing ends the process.</summary>
    public bool IsBusy => isBusy;
    public string BusyMessage =>
        string.IsNullOrEmpty(busyMessage)
            ? "Work is running on this page. Please wait until it finishes."
            : busyMessage;
    /* Hold Refresh History during backup and restore. */
    ModernButton? refreshHistoryButton;
    /* Only the latest history read may bind results. */
    int historyLoadVersion;
    bool historyLoadedLatest;
    /* Restore and Locate Missing File require a successfully loaded history. */
    int historyLoadDepth;
    bool historyLoadFailed;
    bool historyLoading;
    DatabaseBackupManager.BackupInfo? selectedRestoreBackupInfo;
    /* This status area exists only for users with restore permission. */
    Panel? restoreStatusPanel;
    Label? restoreStatusHeadline;
    Label? restoreStatusDetail;
    /* Only eligible files may reach the mandatory restore confirmation. */
    bool restoreAllowed = true;
    /// <summary>Backup eligibility states derived from the existing header metadata.</summary>
    private enum RestoreFileState
    {
        None,
        Valid,
        Stale,
        WrongDatabase,
        Unverified
    }
    public BackupRestorePage()
        : base(
            PageTitles.BackupRestore,
            "Back up your school data or restore a saved copy.")
    {
        backupFolder.ReadOnly = true;
        restoreFile.ReadOnly = true;
        var chooseBackupButton =
            AddBusyAction("Choose Backup Folder…", ChooseBackupFolderAsync);
        backupNowButton =
            AddBusyAction("Back Up Now", BackupNowAsync, "Success");
        retryPendingButton = AddBusyAction("Retry Pending Uploads", RetryPendingAsync, "Secondary");
        if (canRestore)
            cloudSettingsButton = AddAction("Cloud Settings…", (_, _) => OpenCloudSettings());
        ModernButton? chooseRestoreButton = null;
        if (canRestore)
        {
            chooseRestoreButton =
                AddAction("Choose Backup File…", (_, _) => ChooseRestoreFile());
            cloudChooseRestoreButton =
                AddBusyAction("Choose Cloud Backup…", ChooseCloudRestoreAsync);
            selectRecoveryButton =
                AddBusyAction("Use Selected Recovery Point", UseSelectedRecoveryPointAsync);
            /* Prevent duplicate restore submissions. */
            restoreButton =
                AddBusyAction("Restore Data", RestoreNowAsync, "Danger");
            /* Preserve disabled states when the busy scope restores actions. */
            restoreButton.EnabledChanged += (_, _) => {
                if ((!restoreAllowed || isBusy) &&
                    restoreButton is { Enabled: true })
                    restoreButton.Enabled = false;
            };
        }
        refreshHistoryButton = AddBusyAction("Refresh History", async () => {
            await LoadHistoryAsync();
            await LoadRecoveryCatalogueAsync();
        });
        locateFileButton =
            AddBusyAction("Locate Missing File…", LocateMissingFileAsync, "Secondary");
        RelocateFromActions(
            chooseBackupButton,
            backupNowButton);
        if (cloudSettingsButton is not null)
            RelocateFromActions(cloudSettingsButton);
        if (retryPendingButton is not null)
            RelocateFromActions(retryPendingButton);
        if (cloudChooseRestoreButton is not null)
            RelocateFromActions(cloudChooseRestoreButton);
        if (selectRecoveryButton is not null)
            RelocateFromActions(selectRecoveryButton);
        if (chooseRestoreButton is not null && restoreButton is not null)
            RelocateFromActions(
                chooseRestoreButton,
                restoreButton);
        UseToolbar();
        var backupCard = HumanUi.Card(
            "Backup",
            "Create one verified backup and deliver to all enabled destinations.");
        var localOnlyNotice = new Label {
            Dock = DockStyle.Top,
            Height = AppScale.Scale(46),
            AutoEllipsis = false,
            Font = AppTheme.NormalFont,
            ForeColor = AppTheme.MutedText,
            TextAlign = ContentAlignment.TopLeft,
            Text = "The live school database stays on this computer. Cloud backups are encrypted " +
                   "recovery copies, not a second live database or a shared school connection. " +
                   "Keep verified backups both locally and off this computer."
        };
        var backupFields = new TableLayoutPanel {
            Dock = DockStyle.Top,
            Height = AppMetrics.FieldRowHeight + 2 * AppMetrics.ButtonRowHeight + AppMetrics.SpacingXs,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(2, 4, 2, 0)
        };
        backupFields.RowStyles.Add(
            new RowStyle(SizeType.Absolute, AppMetrics.FieldRowHeight));
        backupFields.RowStyles.Add(
            new RowStyle(SizeType.Absolute, AppMetrics.ButtonRowHeight));
        backupFields.RowStyles.Add(
            new RowStyle(SizeType.Absolute, AppMetrics.ButtonRowHeight));
        backupFields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        backupFields.Controls.Add(
            HumanUi.Field("Local Backup Folder", backupFolder),
            0,
            0);
        backupFields.Controls.Add(
            CreateActionRow(chooseBackupButton, backupNowButton),
            0,
            1);
        backupFields.Controls.Add(
            canRestore ? CreateActionRow(cloudSettingsButton!, retryPendingButton!)
                       : CreateActionRow(retryPendingButton!),
            0, 2);
        // Each upper card can scroll independently when the available half-height
        // is smaller than its actions and warnings (compact windows / high DPI).
        backupCard.Content.AutoScroll = true;
        backupCard.Content.Controls.Add(technicalDetails);
        backupCard.Content.Controls.Add(toggleTechnicalDetails);
        backupCard.Content.Controls.Add(destinationResults);
        backupCard.Content.Controls.Add(operationStage);
        backupCard.Content.Controls.Add(backupFields);
        backupCard.Content.Controls.Add(localOnlyNotice);
        toggleTechnicalDetails.LinkClicked += (_, _) => {
            technicalDetails.Visible = !technicalDetails.Visible;
            toggleTechnicalDetails.Text = technicalDetails.Visible
                ? "Hide technical details" : "Show technical details";
            FitBackupNotice();
        };
        void FitBackupNotice()
        {
            if (backupCard.Content.ClientSize.Width <= 0)
                return;
            int textWidth = Math.Max(1, backupCard.Content.ClientSize.Width -
                backupCard.Content.Padding.Horizontal - AppMetrics.SpacingLg -
                SystemInformation.VerticalScrollBarWidth);
            int textHeight = TextRenderer.MeasureText(
                localOnlyNotice.Text, localOnlyNotice.Font,
                new Size(textWidth, int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height;
            localOnlyNotice.Height = Math.Max(
                AppMetrics.TextHeight(localOnlyNotice.Font),
                textHeight + AppMetrics.SpacingSm);
            backupCard.Content.AutoScrollMinSize = new Size(
                0, localOnlyNotice.Height + backupFields.Height +
                    operationStage.Height + destinationResults.Height + toggleTechnicalDetails.Height +
                    (technicalDetails.Visible ? technicalDetails.Height : 0) +
                    backupCard.Content.Padding.Vertical);
        }
        backupCard.Content.SizeChanged += (_, _) => FitBackupNotice();
        string backupNotice = localOnlyNotice.Text;
        refreshCloudStatus = () => {
            var status = CloudBackupCoordinator.ReadLastRunStatus();
            localOnlyNotice.Text = backupNotice + (status.Message.Length == 0 ? "" : "\n\n" + status.Message);
            FitBackupNotice();
        };

        // 50% actions above, 50% backup history / recorded file paths below.
        // The upper half is itself split evenly between Backup and Restore.
        var root = new TableLayoutPanel {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        var upper = new TableLayoutPanel {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = new Padding(0, 0, 0, AppMetrics.SpacingXs)
        };
        upper.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        upper.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        upper.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        backupCard.Margin = new Padding(0, 0, canRestore ? AppMetrics.SpacingXs : 0, 0);
        upper.Controls.Add(backupCard, 0, 0);
        if (canRestore)
        {
            var restoreCard = HumanUi.Card(
                "Restore",
                "Replace current school data with a saved backup. Newer records will be replaced.");
            var restoreFields = new TableLayoutPanel {
                Dock = DockStyle.Top,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(2, 4, 2, 0)
            };
            restoreFields.RowStyles.Add(
                new RowStyle(SizeType.Absolute, AppMetrics.FieldRowHeight));
            /* Show backup eligibility before the restore actions. */
            int restoreStatusHeight =
                AppMetrics.TextHeight(AppTheme.FontSmallBold) +
                AppMetrics.TextHeight(AppTheme.NormalFont) +
                2 * AppMetrics.SpacingSm;
            restoreFields.RowStyles.Add(
                new RowStyle(SizeType.Absolute, restoreStatusHeight));
            restoreFields.RowStyles.Add(
                new RowStyle(SizeType.Absolute, AppMetrics.ButtonRowHeight));
            restoreFields.RowStyles.Add(
                new RowStyle(SizeType.Absolute, AppMetrics.ButtonRowHeight));
            restoreFields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            restoreStatusHeadline = new Label {
                Dock = DockStyle.Top,
                Height = AppMetrics.TextHeight(AppTheme.FontSmallBold),
                Font = AppTheme.FontSmallBold,
                ForeColor = AppTheme.MutedText,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true
            };
            restoreStatusDetail = new Label {
                Dock = DockStyle.Fill,
                Font = AppTheme.NormalFont,
                ForeColor = AppTheme.MutedText,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true
            };
            restoreStatusPanel = new Panel {
                Dock = DockStyle.Fill,
                Padding = new Padding(
                    AppMetrics.SpacingSm,
                    AppMetrics.SpacingXs,
                    AppMetrics.SpacingSm,
                    AppMetrics.SpacingXs),
                BackColor = AppTheme.Tint(AppTheme.MutedText, 0.90f)
            };
            /* Add Fill before Top so the headline retains its space. */
            restoreStatusPanel.Controls.Add(restoreStatusDetail);
            restoreStatusPanel.Controls.Add(restoreStatusHeadline);
            restoreFields.Controls.Add(
                HumanUi.Field("Backup File", restoreFile),
                0,
                0);
            restoreFields.Controls.Add(
                restoreStatusPanel,
                0,
                1);
            restoreFields.Controls.Add(
                CreateActionRow(chooseRestoreButton!, restoreButton!),
                0,
                2);
            restoreFields.Controls.Add(
                CreateActionRow(selectRecoveryButton!, cloudChooseRestoreButton!), 0, 3);
            restoreFields.Height = AppMetrics.FieldRowHeight +
                restoreStatusHeight + 2 * AppMetrics.ButtonRowHeight + AppMetrics.SpacingXs;
            restoreCard.Content.AutoScroll = true;
            restoreCard.Content.Controls.Add(restoreFields);
            restoreCard.Content.AutoScrollMinSize = new Size(
                0, restoreFields.Height + restoreCard.Content.Padding.Vertical);
            restoreCard.Margin = new Padding(AppMetrics.SpacingXs, 0, 0, 0);
            upper.Controls.Add(restoreCard, 1, 0);
            /* No selected file means unverified, not valid. */
            ShowRestoreFileStatus(RestoreFileState.None, null);
        }
        var historyCard = HumanUi.Card(
            "Available Backups & Activity History",
            "Choose a recovery point from available destinations, or review past operations.");
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var availableTab = new TabPage("Available Backups") { BackColor = AppTheme.PageBackground };
        var activityTab = new TabPage("Activity History") { BackColor = AppTheme.PageBackground };
        availableBackupsGrid.CellDoubleClick += async (_, _) => await UseSelectedRecoveryPointAsync();
        availableTab.Controls.Add(availableBackupsGrid);
        availableTab.Controls.Add(availableBackupsStatus);
        var activityLayout = new TableLayoutPanel {
            Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1,
            Margin = Padding.Empty, Padding = Padding.Empty
        };
        activityLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 36));
        activityLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 64));
        activityLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        activityLayout.Controls.Add(recordedCloudActivity, 0, 0);
        activityLayout.Controls.Add(Grid, 0, 1);
        activityTab.Controls.Add(activityLayout);
        tabs.TabPages.Add(availableTab);
        tabs.TabPages.Add(activityTab);
        historyCard.Content.Controls.Add(tabs);
        historyCard.Margin = new Padding(0, AppMetrics.SpacingXs, 0, 0);
        if (!canRestore)
            upper.SetColumnSpan(backupCard, 2);
        root.Controls.Add(upper, 0, 0);
        root.Controls.Add(historyCard, 0, 1);
        SetContent(root);
        FitBackupNotice();
        LoadBackupFolder();
        /* Keep history-dependent actions disabled until loading succeeds. */
        CloseHistoryDependentActions();
        Defer(LoadHistoryAsync);
        Defer(LoadRecoveryCatalogueAsync);
        RefreshRecordedActivity();
        RefreshDestinationSummary();
    }

    private void RefreshDestinationSummary()
    {
        if (IsDisposed || Disposing) return;
        var settings = CloudBackupSettings.LoadForEditing(out string? warning);
        if (warning is not null)
        {
            destinationResults.Text = "Backup settings need repair — open Backup Settings.";
            return;
        }
        destinationResults.Text = "Destinations: " +
            (settings.LocalEnabled ? "Local · " : "") +
            (settings.GoogleDriveEnabled ? "Google Drive · " : "") +
            (settings.DropboxEnabled ? "Dropbox · " : "") +
            (settings.AutomaticEnabled ? "Scheduled" : "Manual");
    }

    private void RefreshRecordedActivity()
    {
        if (IsDisposed || Disposing) return;
        recordedCloudActivity.BeginUpdate();
        try
        {
            recordedCloudActivity.Items.Clear();
            foreach (var item in BackupActivityJournal.ReadRecent().Take(80))
                recordedCloudActivity.Items.Add(
                    item.Utc.ToLocalTime().ToString("dd-MMM HH:mm") + "  |  " +
                    item.Operation + "  |  " + item.Destination + "  |  " + item.State +
                    (item.Detail.Length > 0 ? "  |  " + item.Detail : ""));
            if (recordedCloudActivity.Items.Count == 0)
                recordedCloudActivity.Items.Add("No recorded cloud delivery or restore activity yet.");
        }
        finally { recordedCloudActivity.EndUpdate(); }
    }

    private void ReportBackupStage(BackupProgressUpdate update)
    {
        if (IsDisposed || Disposing) return;
        operationStage.Text = update.Display;
        operationStage.ForeColor = update.IsError ? AppTheme.Danger : AppTheme.Primary;
        if (update.Detail.Length > 0)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + update.Display +
                " — " + update.Detail + Environment.NewLine;
            if (technicalDetails.TextLength > 12000) technicalDetails.Clear();
            technicalDetails.AppendText(line);
        }
        if (restoreStatusDetail is not null && isBusy)
            restoreStatusDetail.Text = update.Display;
    }

    private static DataGridView CreateAvailableBackupsGrid()
    {
        var grid = new DataGridView {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoGenerateColumns = false,
            RowHeadersVisible = false,
            BackgroundColor = AppTheme.PageBackground,
            BorderStyle = BorderStyle.None
        };
        grid.Columns.Add(new DataGridViewTextBoxColumn {
            HeaderText = "SQL backup date", Width = AppScale.Scale(170),
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn {
            HeaderText = "Available copies", Width = AppScale.Scale(200),
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn {
            HeaderText = "Recovery status", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        return grid;
    }

    static TableLayoutPanel CreateActionRow(params ModernButton[] buttons)
    {
        var row = new TableLayoutPanel {
            Dock = DockStyle.Fill,
            ColumnCount = buttons.Length,
            RowCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(0, AppMetrics.SpacingXs, 0, AppMetrics.SpacingXs)
        };
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        for (int column = 0; column < buttons.Length; column++)
        {
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / buttons.Length));
            ModernButton button = buttons[column];
            button.AutoSize = false;
            button.MinimumSize = Size.Empty;
            button.Dock = DockStyle.Fill;
            button.Margin = new Padding(AppMetrics.SpacingXs, 0, AppMetrics.SpacingXs, 0);
            row.Controls.Add(button, column, 0);
        }
        return row;
    }
    /// <summary>Whether history-dependent actions are currently available.</summary>
    bool HistoryDependentActionsUsable =>
        !isBusy &&
        !historyLoading &&
        !historyLoadFailed;
    void CloseHistoryDependentActions()
    {
        if (locateFileButton is not null)
            locateFileButton.Enabled = false;
        if (restoreButton is not null)
            restoreButton.Enabled = false;
    }
    void BeginHistoryLoad()
    {
        historyLoadDepth++;
        historyLoading = true;
        CloseHistoryDependentActions();
    }
    /// <summary>Reopen the gate only after a successful read.</summary>
    void EndHistoryLoad()
    {
        if (historyLoadDepth > 0)
            historyLoadDepth--;
        if (historyLoadDepth > 0)
            return;
        historyLoading = false;
        historyLoadFailed = !historyLoadedLatest;
        ApplyHistoryDependentEnablement();
    }
    /// <summary>Shared eligibility rules for Restore and Locate Missing File.</summary>
    void ApplyHistoryDependentEnablement()
    {
        bool usable = HistoryDependentActionsUsable;
        if (locateFileButton is not null)
            locateFileButton.Enabled = usable;
        // History-query failures must not disable a separately validated file
        // or cloud recovery point. RestoreBackup enforces SQL identity and safety.
        if (restoreButton is not null)
            restoreButton.Enabled = !isBusy && restoreAllowed;
    }
    void LoadBackupFolder()
    {
        string folder = BackupSettings.GetBackupFolder();
        if (!string.IsNullOrWhiteSpace(folder))
            backupFolder.Text = folder;
    }
    async Task ChooseBackupFolderAsync()
    {
        if (isBusy)
            return;
        using var dialog = new FolderBrowserDialog {
            Description = "Choose where to save school data backups.",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };
        string currentFolder = backupFolder.Text.Trim();
        if (Directory.Exists(currentFolder))
            dialog.SelectedPath = currentFolder;
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
            return;
        string newFolder = dialog.SelectedPath;
        isBusy = true;
        busyMessage = "The backup folder is being updated. Wait until it finishes.";
        SetBusyState(true);
        try
        {
            /* Changing the folder also relinks stored backup records. */
            int relinkable = await Task.Run(
                () => DatabaseBackupManager.CountAutoRelinkableBackups(newFolder));
            if (relinkable > 0)
            {
                var confirmation = ModernMessageDialog.Show(
                    FindForm(),
                    new MessageDialogContent("Change the backup folder?",
                        Facts: new (string, string)[] { ("New folder", newFolder), ("Records to relink", relinkable.ToString("N0")) },
                        Notice: "Existing backup records will point to this folder. FMSC will look here for those files."),
                    "Confirm Backup Folder Change",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
                if (confirmation != DialogResult.Yes)
                {
                    SetStatus("Backup folder was not changed.");
                    return;
                }
            }
            if (!BackupSettings.SetBackupFolder(newFolder))
            {
                Warn("The backup folder could not be saved. The selected folder and backup records were not changed. Contact the administrator to check settings-folder access.");
                return;
            }
            backupFolder.Text = newFolder;
            int relinked = await Task.Run(() => DatabaseBackupManager.TryAutoRelinkMissingBackups(newFolder));
            SetStatus($"Backup folder changed to {newFolder}.");
            if (relinked > 0)
                Info($"Relinked {relinked} existing backup record(s) to this folder.");
            await LoadHistoryAsync();
        }
        finally
        {
            isBusy = false;
            busyMessage = string.Empty;
            if (!IsDisposed) SetBusyState(false);
        }
    }
    async Task BackupNowAsync()
    {
        if (isBusy) return;
        isBusy = true;
        busyMessage = "Creating one verified school backup for the enabled destinations.";
        SetBusyState(true);
        SetStatus("Backing up school data…");
        try
        {
            var settings = CloudBackupSettings.Load();
            if (settings.LocalEnabled &&
                (string.IsNullOrWhiteSpace(backupFolder.Text) || !Directory.Exists(backupFolder.Text)))
            {
                Warn("Choose a valid Local Backup Folder or disable the Local destination in Cloud Settings.");
                return;
            }
            technicalDetails.Clear();
            var progress = new Progress<BackupProgressUpdate>(ReportBackupStage);
            var result = await CloudBackupCoordinator.CreateAndUploadAsync(progress: progress);
            destinationResults.Text = "Local: " + (settings.LocalEnabled
                    ? (result.LocalSucceeded ? "Verified" : "Failed") : "Disabled") +
                "   |   Drive: " + (settings.GoogleDriveEnabled
                    ? (result.GoogleSucceeded ? "Recovery verified" : "Pending retry") : "Disabled") +
                "   |   Dropbox: " + (settings.DropboxEnabled
                    ? (result.DropboxSucceeded ? "Recovery verified" : "Pending retry") : "Disabled");
            await LoadHistoryAsync();
            await LoadRecoveryCatalogueAsync();
            RefreshRecordedActivity();
            SetStatus(result.Message, result.UploadsPending);
            if (result.UploadsPending) Warn(result.Message);
            else ModernMessageDialog.ShowInfo(FindForm(), result.Message);
        }
        catch (Exception ex) { Fail(ex); }
        finally
        {
            isBusy = false;
            busyMessage = string.Empty;
            if (!IsDisposed) SetBusyState(false);
        }
    }

    private void OpenCloudSettings()
    {
        if (isBusy) return;
        try
        {
            RequirePermission("Backup.Restore");
            using var dialog = new CloudBackupSettingsDialog();
            if (dialog.ShowDialog(FindForm()) == DialogResult.OK)
            {
                SetStatus("Backup destination, schedule and retention settings saved.");
                RefreshDestinationSummary();
                _ = LoadRecoveryCatalogueAsync();
            }
        }
        catch (Exception ex) { Fail(ex); }
    }

    private async Task RetryPendingAsync()
    {
        if (isBusy) return;
        isBusy = true;
        busyMessage = "Retrying encrypted cloud uploads already in the protected queue.";
        SetBusyState(true);
        try
        {
            SetStatus("Retrying pending cloud deliveries…");
            technicalDetails.Clear();
            string result = await CloudBackupCoordinator.RetryPendingUploadsNowAsync(
                progress: new Progress<BackupProgressUpdate>(ReportBackupStage));
            await LoadRecoveryCatalogueAsync();
            RefreshRecordedActivity();
            SetStatus(result);
            ModernMessageDialog.ShowInfo(FindForm(), result);
        }
        catch (Exception ex) { Fail(ex); }
        finally
        {
            isBusy = false;
            busyMessage = string.Empty;
            if (!IsDisposed) SetBusyState(false);
        }
    }

    private async Task LoadRecoveryCatalogueAsync()
    {
        int version = ++recoveryCatalogueVersion;
        if (!IsDisposed)
            availableBackupsStatus.Text = "Checking local and connected cloud recovery points…";
        try
        {
            var inventory = await UnifiedRecoveryInventory.LoadAsync();
            if (IsDisposed || version != recoveryCatalogueVersion) return;
            availableBackupsGrid.Rows.Clear();
            foreach (var point in inventory.Points)
            {
                int rowIndex = availableBackupsGrid.Rows.Add(
                    point.SqlBackupCreatedUtc.ToLocalTime().ToString("dd-MMM-yyyy  hh:mm tt"),
                    point.AvailableSources,
                    point.VerificationSummary);
                availableBackupsGrid.Rows[rowIndex].Tag = point;
            }
            availableBackupsGrid.ClearSelection();
            string countText = inventory.Points.Count == 0
                ? "No available recovery points found. Choose a backup file manually or connect a cloud account."
                : inventory.Points.Count + " available recovery point(s). Select a row and use 'Use Selected Recovery Point'.";
            availableBackupsStatus.Text = inventory.Warnings.Count == 0
                ? countText : countText + "  " + string.Join(" ", inventory.Warnings);
        }
        catch (Exception ex)
        {
            if (IsDisposed || version != recoveryCatalogueVersion) return;
            AppExceptionHandler.LogNonFatal(ex, "Loading unified backup recovery-point catalogue");
            availableBackupsStatus.Text =
                "Recovery points could not be refreshed. Existing backups were not modified; try Refresh History or Choose Backup File.";
        }
    }

    private async Task UseSelectedRecoveryPointAsync()
    {
        if (isBusy || !canRestore) return;
        if (availableBackupsGrid.SelectedRows.Count != 1 ||
            availableBackupsGrid.SelectedRows[0].Tag is not UnifiedRecoveryPoint point)
        {
            Warn("Select a recovery point in Available Backups, then choose Use Selected Recovery Point.");
            return;
        }
        try
        {
            RequirePermission("Backup.Restore");
            if (point.LocalCopies.Count > 0)
            {
                string path = point.LocalCopies[0].FilePath;
                if (!File.Exists(path))
                {
                    Warn("This local backup is no longer available. Refresh the recovery points or select a cloud copy.");
                    return;
                }
                var info = await Task.Run(() => DatabaseBackupManager.GetBackupInfo(path));
                RestoreFileState state = ClassifyRestoreFile(info);
                if (state is RestoreFileState.Valid or RestoreFileState.Stale)
                {
                    DateTime currentSqlUtc = DateTime.SpecifyKind(info!.BackupStartDate, DateTimeKind.Local)
                        .ToUniversalTime();
                    if ((currentSqlUtc - point.SqlBackupCreatedUtc).Duration() > TimeSpan.FromSeconds(1))
                    {
                        Warn("The backup file changed since the catalogue was refreshed. Refresh and select it again.");
                        return;
                    }
                }
                selectedCloud = null;
                // The catalogue has checked both SQL time and the original
                // .bak digest. Cloud copies of this same point are candidates
                // ONLY for a failed pre-SQL acquisition, never SQL rollback.
                selectedCloudAlternatives = point.CloudCopies.Select(x => x.Remote).ToArray();
                cloudRecoveryPassphrase = string.Empty;
                selectedRestoreBackupInfo = info;
                selectedLocalRecoverySha256 = point.LocalCopies[0].SourceBakSha256;
                restoreFile.Text = path;
                restoreFile.Tag = null; // Re-read the SQL header at final restore confirmation.
                ShowRestoreFileStatus(state, info);
                if (state is RestoreFileState.Valid or RestoreFileState.Stale)
                    SetStatus("Local copy selected. Restore Selected will validate the file again before replacing school data.");
                else
                    Warn("The selected local file could not be verified as an FMSC database backup.");
                return;
            }
            if (point.CloudCopies.Count == 0)
            {
                Warn("This recovery point has no currently available copies.");
                return;
            }
            // Ask for the original passphrase only when the chosen source is
            // encrypted. Do not retrieve secrets from unrelated cloud accounts.
            var copies = point.CloudCopies.Select(x =>
                new CloudBackupCoordinator.CloudSelection(x.Remote, x.RecoveryTested, x.SourceBakSha256))
                .ToArray();
            using var dialog = new CloudRestoreSelectionDialog(copies, null);
            if (dialog.ShowDialog(FindForm()) != DialogResult.OK ||
                dialog.SelectedCopy is not { } selected) return;
            selectedCloud = selected;
            selectedCloudAlternatives = point.CloudCopies.Select(x => x.Remote).ToArray();
            cloudRecoveryPassphrase = dialog.RecoveryPassphrase;
            selectedRestoreBackupInfo = null;
            restoreFile.Tag = null;
            restoreFile.Text = selected.Remote.Provider + " — " +
                selected.Remote.SqlBackupCreatedUtc.ToLocalTime().ToString("dd-MMM-yyyy hh:mm tt");
            ShowRestoreFileStatus(RestoreFileState.Valid, null);
            if (restoreStatusHeadline != null)
                restoreStatusHeadline.Text = "Cloud Recovery Point Selected";
            if (restoreStatusDetail != null)
                restoreStatusDetail.Text = "Remote metadata is provisional; downloaded encryption and SQL integrity are checked before restoration.";
            SetStatus("Encrypted cloud recovery point selected. Restore Selected will download and validate this exact recovery point.");
        }
        catch (Exception ex) { Fail(ex); }
    }

    private async Task ChooseCloudRestoreAsync()
    {
        if (isBusy) return;
        try
        {
            RequirePermission("Backup.Restore");
            SetStatus("Checking available cloud recovery points…");
            var options = await CloudBackupCoordinator.BrowseCloudAsync();
            if (IsDisposed) return;
            if (options.Copies.Count == 0)
            {
                Warn(options.Warnings.Count > 0 ? string.Join("\n", options.Warnings)
                    : "No cloud backups are available. Check the connected Google Drive and Dropbox accounts.");
                return;
            }
            DateTime? newestLocal = await Task.Run(DatabaseBackupManager.GetNewestAvailableLocalBackupUtc);
            using var dialog = new CloudRestoreSelectionDialog(options.Copies, newestLocal, options.Warnings);
            if (dialog.ShowDialog(FindForm()) != DialogResult.OK ||
                dialog.SelectedCopy is not { } selected)
                return;
            selectedCloud = selected;
            selectedCloudAlternatives = options.Copies
                .Where(copy => string.Equals(copy.Remote.PackageSha256, selected.Remote.PackageSha256,
                    StringComparison.OrdinalIgnoreCase) &&
                    (copy.Remote.SqlBackupCreatedUtc - selected.Remote.SqlBackupCreatedUtc).Duration() < TimeSpan.FromSeconds(1))
                .Select(copy => copy.Remote).ToArray();
            cloudRecoveryPassphrase = dialog.RecoveryPassphrase;
            selectedRestoreBackupInfo = null;
            restoreFile.Tag = null;
            restoreFile.Text = selected.Remote.Provider + " — " +
                selected.Remote.SqlBackupCreatedUtc.ToLocalTime().ToString("dd-MMM-yyyy hh:mm tt");
            ShowRestoreFileStatus(RestoreFileState.Valid, null);
            if (restoreStatusHeadline != null)
                restoreStatusHeadline.Text = "Cloud Recovery Point Selected";
            if (restoreStatusDetail != null)
                restoreStatusDetail.Text =
                    "Creation time is provisional until the encrypted download and SQL header are verified.";
            SetStatus("Cloud backup selected. Restore Data will download it to a protected local temporary folder.");
        }
        catch (Exception ex) { Fail(ex); }
    }

    private async Task RestoreCloudNowAsync()
    {
        if (isBusy || selectedCloud is null) return;
        try { RequirePermission("Backup.Restore"); }
        catch (Exception ex) { Fail(ex); return; }
        var selection = selectedCloud;
        var confirmation = ModernMessageDialog.Show(FindForm(),
            new MessageDialogContent("Restore this cloud backup?",
                "The encrypted cloud backup will be downloaded to a private local staging directory, verified, and only then used for SQL restoration.",
                Facts: new (string, string)[] {
                    ("Source", selection.Remote.Provider.ToString()),
                    ("SQL backup created", selection.Remote.SqlBackupCreatedUtc.ToLocalTime().ToString("dd-MMM-yyyy hh:mm tt")),
                    ("Uploaded", selection.Remote.UploadedUtc.ToLocalTime().ToString("dd-MMM-yyyy hh:mm tt"))
                },
                Notice: "The database may lose admissions, payments and other records newer than the selected backup. " +
                        "The SQL header and school identity will be checked and a mandatory verified safety backup of current data created before replacement."),
            "Confirm Cloud Restore", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (confirmation != DialogResult.Yes) return;
        isBusy = true;
        busyMessage = "Downloading, verifying and restoring cloud backup. Do not close FMSC.";
        SetBusyState(true);
        try
        {
            SetStatus("Downloading and verifying encrypted cloud backup…");
            var result = await CloudBackupCoordinator.RestoreFromCloudAsync(
                selection.Remote, cloudRecoveryPassphrase, selectedCloudAlternatives,
                progress: new Progress<BackupProgressUpdate>(ReportBackupStage));
            if (result.Success)
            {
                // CloudRestoreStaging has already removed the temporary
                // download after RestoreBackup verified successful recovery.
                cloudRecoveryPassphrase = string.Empty;
                selectedCloud = null;
                ModernMessageDialog.Show(FindForm(),
                    result.Message + "\n\nFMSC will restart.",
                    "Restore Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                isBusy = false; busyMessage = string.Empty;
                RestartApplication(FindForm());
                return;
            }
            await LoadHistoryAsync();
            Warn(result.Message);
        }
        catch (Exception ex)
        {
            await LoadHistoryAsync();
            Fail(ex);
        }
        finally
        {
            // A failed cloud restore must never leave the same selection active
            // with an erased passphrase: that made immediate retries fail again.
            cloudRecoveryPassphrase = string.Empty;
            selectedCloud = null;
            selectedCloudAlternatives = Array.Empty<CloudRemoteBackup>();
            if (!IsDisposed)
            {
                restoreFile.Text = string.Empty;
                selectedRestoreBackupInfo = null;
                ShowRestoreFileStatus(RestoreFileState.None, null);
            }
            isBusy = false; busyMessage = string.Empty;
            if (!IsDisposed) SetBusyState(false);
        }
    }

    void ChooseRestoreFile()
    {
        if (isBusy)
            return;
        selectedCloud = null;
        selectedCloudAlternatives = Array.Empty<CloudRemoteBackup>();
        cloudRecoveryPassphrase = string.Empty;
        selectedLocalRecoverySha256 = string.Empty;
        using var dialog = new OpenFileDialog {
            Title = "Select FMSC Database Backup",
            Filter = "SQL Server Backup Files (*.bak)|*.bak|All Files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            RestoreDirectory = true
        };
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
            return;
        restoreFile.Text = dialog.FileName;
        selectedRestoreBackupInfo = DatabaseBackupManager.GetBackupInfo(dialog.FileName);
        RestoreFileState state = ClassifyRestoreFile(selectedRestoreBackupInfo);
        ShowRestoreFileStatus(state, selectedRestoreBackupInfo);
        string ageText = selectedRestoreBackupInfo is null
            ? "unknown"
            : FormatShortAge(selectedRestoreBackupInfo);
        switch (state)
        {
            case RestoreFileState.Unverified:
                SetStatus(
                    "The selected backup could not be checked. " +
                    "Restore is disabled.",
                    true);
                break;
            case RestoreFileState.WrongDatabase:
                SetStatus("This backup is not compatible with FMSC. Choose another file.", true);
                /* Reject ineligible files before confirmation. */
                Warn("This backup is not compatible with FMSC. Choose an FMSC school data backup.");
                break;
            case RestoreFileState.Stale:
                SetStatus(
                    $"Restore file selected (stale FMSC backup, {ageText} old).",
                    true);
                break;
            case RestoreFileState.Valid:
                SetStatus($"Restore file selected (valid FMSC backup, {ageText} old).");
                break;
        }
    }
    /// <summary>Classify existing backup metadata without rereading or revalidating the file.</summary>
    static RestoreFileState ClassifyRestoreFile(DatabaseBackupManager.BackupInfo? info)
    {
        /* A missing header means unreadable or corrupt. */
        if (info is null)
            return RestoreFileState.Unverified;
        /* The manager uses "(unknown)" when the header omits DatabaseName. */
        if (string.IsNullOrWhiteSpace(info.DatabaseName) ||
            string.Equals(info.DatabaseName, "(unknown)", StringComparison.Ordinal))
        {
            return RestoreFileState.Unverified;
        }
        if (!info.IsForFmscDatabase)
            return RestoreFileState.WrongDatabase;
        return info.IsStale
            ? RestoreFileState.Stale
            : RestoreFileState.Valid;
    }
    /// <summary>Display backup eligibility in the restore card.</summary>
    void ShowRestoreFileStatus(RestoreFileState state, DatabaseBackupManager.BackupInfo? info)
    {
        if (restoreStatusPanel is null ||
            restoreStatusHeadline is null ||
            restoreStatusDetail is null)
            return;
        string backupAge = info is null ? string.Empty : FormatBackupAge(info);
        string headline;
        string detail;
        Color accent;
        switch (state)
        {
            case RestoreFileState.Valid:
                accent = AppTheme.Success;
                headline = "Ready to Restore";
                detail =
                    $"Backup taken {backupAge}. " +
                    "Restore Data will ask for confirmation.";
                break;
            case RestoreFileState.Stale:
                accent = AppTheme.Warning;
                headline = "Older FMSC Backup";
                detail =
                    $"Backup taken {backupAge}. " +
                    "Warning: newer records would be lost.";
                break;
            case RestoreFileState.WrongDatabase:
                accent = AppTheme.Danger;
                headline = "Incompatible Backup";
                detail =
                    "Choose an FMSC backup file. Restore is disabled.";
                break;
            case RestoreFileState.Unverified:
                accent = AppTheme.Danger;
                headline = "Backup Could Not Be Checked";
                detail =
                    "The file could not be checked. " +
                    "Restore is disabled.";
                break;
            default:
                accent = AppTheme.MutedText;
                headline = "No Backup Selected";
                detail = "Choose a .bak file to see whether it is a valid FMSC backup.";
                break;
        }
        restoreStatusHeadline.Text = headline;
        restoreStatusHeadline.ForeColor = accent;
        restoreStatusDetail.Text = detail;
        restoreStatusDetail.ForeColor =
            state == RestoreFileState.None ? AppTheme.MutedText : AppTheme.Text;
        restoreStatusPanel.BackColor = AppTheme.Tint(accent, 0.90f);
        /* No selection is not an eligible restore. A selected cloud copy is
           represented as Valid but still requires verification before SQL. */
        SetRestoreAllowed(state is RestoreFileState.Valid or RestoreFileState.Stale);
    }
    void SetRestoreAllowed(bool allowed)
    {
        restoreAllowed = allowed;
        if (restoreButton is null)
            return;
        /* Require both eligible metadata and a completed history read. */
        restoreButton.Enabled =
            allowed &&
            HistoryDependentActionsUsable;
    }
    static string FormatShortAge(DatabaseBackupManager.BackupInfo info)
    {
        TimeSpan age = info.Age;
        return age.TotalDays >= 1
            ? $"{(int)age.TotalDays}d"
            : age.TotalHours >= 1
                ? $"{(int)age.TotalHours}h"
                : "<1h";
    }
    static string FormatBackupAge(DatabaseBackupManager.BackupInfo info)
    {
        string when = info.BackupStartDate.ToString("dd-MMM-yyyy HH:mm");
        TimeSpan age = info.Age;
        string ageText = age.TotalDays >= 1
            ? $"{(int)age.TotalDays} day(s) ago"
            : age.TotalHours >= 1
                ? $"{(int)age.TotalHours} hour(s) ago"
                : "less than an hour ago";
        return $"on {when} ({ageText})";
    }
    private string? AskFallbackPassphrase()
    {
        using var window = new Form {
            Text = "Cloud fallback authorization",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            ClientSize = new Size(AppScale.Scale(450), AppScale.Scale(180)),
            MaximizeBox = false, MinimizeBox = false
        };
        var input = new TextBox { Dock = DockStyle.Top, UseSystemPasswordChar = true };
        var label = new Label { Dock = DockStyle.Top, AutoSize = true,
            Text = "The selected local copy failed before SQL restoration.\n" +
                   "Enter the school's cloud recovery passphrase to try the same recovery point." };
        var ok = new Button { Dock = DockStyle.Top, Text = "Verify cloud copy", Height = AppScale.Scale(38) };
        window.Controls.Add(ok);
        window.Controls.Add(input);
        window.Controls.Add(label);
        ok.Click += (_, _) => {
            if (input.Text.Length < 12)
            {
                ModernMessageDialog.ShowWarning(window, "Enter the saved recovery passphrase (at least 12 characters).",
                    "Cloud fallback");
                return;
            }
            window.DialogResult = DialogResult.OK;
            window.Close();
        };
        var answer = window.ShowDialog(FindForm()) == DialogResult.OK ? input.Text : null;
        input.Clear();
        return answer;
    }

    async Task RestoreNowAsync()
    {
        if (isBusy)
            return;
        if (selectedCloud is not null)
        {
            await RestoreCloudNowAsync();
            return;
        }
        try
        {
            RequirePermission("Backup.Restore");
        }
        catch (Exception ex)
        {
            Fail(ex);
            return;
        }
        string backupPath = restoreFile.Text.Trim();
        if (string.IsNullOrWhiteSpace(backupPath) || !File.Exists(backupPath))
        {
            Warn("Please choose a valid .bak backup file first.");
            return;
        }
        if (selectedRestoreBackupInfo is null ||
            !string.Equals(restoreFile.Tag as string, backupPath, StringComparison.OrdinalIgnoreCase))
        {
            selectedRestoreBackupInfo = DatabaseBackupManager.GetBackupInfo(backupPath);
        }
        restoreFile.Tag = backupPath;
        var info = selectedRestoreBackupInfo;
        /* Recheck changed file paths before allowing restore confirmation. */
        RestoreFileState state = ClassifyRestoreFile(info);
        ShowRestoreFileStatus(state, info);
        if (state is RestoreFileState.Unverified or RestoreFileState.WrongDatabase)
        {
            Warn(state == RestoreFileState.Unverified
                ? "This backup could not be checked. Choose a readable FMSC backup file."
                : "This backup is not compatible with FMSC. Choose an FMSC school data backup.");
            return;
        }
        var confirmation = ModernMessageDialog.Show(
            FindForm(),
            new MessageDialogContent("Restore this backup?",
                "This replaces your current school records with the selected backup. Check the backup date first.",
                Facts: new (string, string)[] {
                    ("Backup file", backupPath),
                    ("Database", info?.DatabaseName ?? "Unknown"),
                    ("Backup date", info is null ? "Unknown" : FormatBackupAge(info))
                },
                Notice: (info?.IsStale == true ? $"This backup is {(int)info.Age.TotalDays} days old. " : "") +
                    "Records added after the backup, including admissions and payments, will be lost. " +
                    "A safety copy of the current data is saved before restoring; use it to recover your current records if needed."),
            "Confirm Data Restore", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (confirmation != DialogResult.Yes)
            return;
        isBusy = true;
        busyMessage = "A restore is running. Please wait until it finishes.";
        SetBusyState(true);
        SetStatus("Restoring school data…");
        try
        {
            technicalDetails.Clear();
            var stageReporter = new Progress<string>(message =>
                ReportBackupStage(new BackupProgressUpdate(message, "Validated local SQL restore stage.")));
            DatabaseBackupManager.BackupResult result;
            bool beforeSqlFailed = false;
            if (selectedCloudAlternatives.Count > 0)
            {
                try
                {
                    // Pairing local/cloud needs the original verified SQL bytes,
                    // not just matching SQL creation timestamps. Recheck the
                    // hash at restore time before any live SQL work begins.
                    if (!UnifiedRecoveryCatalog.IsSha256(selectedLocalRecoverySha256))
                        throw new InvalidDataException("The selected local/cloud pairing lacks a trusted original SQL digest.");
                    await using (var stream = File.OpenRead(backupPath))
                    {
                        string digest = Convert.ToHexString(await SHA256.HashDataAsync(stream))
                            .ToLowerInvariant();
                        if (!string.Equals(digest, selectedLocalRecoverySha256,
                                StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException(
                                "The selected local backup has changed since its recovery-point identity was recorded.");
                    }
                    ReportBackupStage(new BackupProgressUpdate("Preflighting local recovery copy",
                        "The local source must pass isolated SQL identity checks before SQL restoration begins."));
                    await Task.Run(() => DatabaseBackupManager.VerifyRecoveryCandidate(backupPath));
                }
                catch (Exception preflightError)
                {
                    beforeSqlFailed = true;
                    AppExceptionHandler.LogNonFatal(preflightError, "Local recovery candidate rejected before SQL restore; checking same-point cloud fallback");
                }
            }
            if (beforeSqlFailed)
            {
                string? fallbackPassphrase = AskFallbackPassphrase();
                if (fallbackPassphrase is null)
                {
                    Warn("The local copy failed verification before SQL restoration. Cloud fallback was cancelled; the live school database is unchanged.");
                    return;
                }
                ReportBackupStage(new BackupProgressUpdate("Trying cloud fallback",
                    "Only the exact recovery point selected from the catalogue is eligible."));
                result = await CloudBackupCoordinator.RestoreFromCloudAsync(
                    selectedCloudAlternatives[0], fallbackPassphrase, selectedCloudAlternatives,
                    progress: new Progress<BackupProgressUpdate>(ReportBackupStage));
                fallbackPassphrase = string.Empty;
            }
            else
                result = await Task.Run(() => DatabaseBackupManager.RestoreBackup(backupPath,
                    progress: stageReporter));
            if (result.Success)
            {
                ModernMessageDialog.Show(
                    FindForm(),
                    result.Message + "\n\nThe application will now restart so all database " +
                    "connections and data are refreshed.",
                    "Restore Complete",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                /* The RESTORE statement has finished, so closing the application is safe now. */
                isBusy = false;
                busyMessage = string.Empty;
                RestartApplication(FindForm());
                return;
            }
            /* Reload before reporting success so refresh feedback cannot replace the outcome. */
            await LoadHistoryAsync();
            Warn(result.Message);
        }
        catch (Exception ex)
        {
            await LoadHistoryAsync();
            Fail(ex);
        }
        finally
        {
            isBusy = false;
            busyMessage = string.Empty;
            /* Successful restore may already have disposed this page. */
            if (!IsDisposed)
                SetBusyState(false);
        }
    }
    /// <summary>Request relaunch after shutdown; never start a second process while this session owns the gate.</summary>
    internal static void RestartApplication(Form? shell, Action? notifyRefused = null)
    {
        if (shell == null || shell.IsDisposed)
        {
            // ApplicationExit occurs only if all windows accept closing.
            EventHandler request = (_, _) => Program.RequestRestart();
            Application.ApplicationExit += request;
            try { Application.Exit(); }
            finally { Application.ApplicationExit -= request; }
            return;
        }
        FormClosedEventHandler? requestRestart = null;
        requestRestart = (_, _) => {
            shell.FormClosed -= requestRestart;
            Program.RequestRestart();
        };
        shell.FormClosed += requestRestart;
        shell.Close();
        if (!shell.IsDisposed)
        {
            shell.FormClosed -= requestRestart;
            if (notifyRefused is not null) notifyRefused();
            else ModernMessageDialog.ShowInfo(shell,
                "The restore is complete, but FMSC could not close itself. " +
                "Please close FMSC and open it again.", "Restore Complete");
        }
    }
    /// <summary>Gate history-dependent actions for the entire read.</summary>
    async Task LoadHistoryAsync()
    {
        refreshCloudStatus?.Invoke();
        int version = ++historyLoadVersion;
        BeginHistoryLoad();
        try
        {
            /* Tidy BEFORE the list is read, so what the operator sees is what survived. */
            (int removed, int failed, string? firstFailure) pruned =
                await Task.Run(DatabaseBackupManager.PruneHistory);
            DataTable history = await Task.Run(DatabaseBackupManager.GetHistory);
            if (IsDisposed) return;
            /* Superseded history reads must not bind results or update eligibility. */
            if (version == historyLoadVersion)
            {
                Bind(history);
                historyLoadedLatest = true;
                RefreshRecordedActivity();
                /* Report after binding, which overwrites the status with its row count. */
                if (pruned.removed > 0)
                {
                    SetStatus(
                        $"{pruned.removed} history entr" +
                        (pruned.removed == 1 ? "y was" : "ies were") +
                        " removed: the backup file is no longer there, or the list is past " +
                        $"its {DatabaseBackupManager.HistoryLimit}-entry limit.");
                }
                if (pruned.failed > 0)
                {
                    /* Report cleanup failure; missing FMSC_FIX_083 can leave obsolete entries. */
                    SetStatus(
                        $"{pruned.failed} history entr" +
                        (pruned.failed == 1 ? "y could" : "ies could") +
                        $" not be tidied: {pruned.firstFailure}",
                        true);
                }
            }
        }
        catch (Exception ex)
        {
            if (version == historyLoadVersion)
                historyLoadedLatest = false;
            if (!IsDisposed) Fail(ex);
        }
        finally
        {
            EndHistoryLoad();
        }
    }
    async Task LocateMissingFileAsync()
    {
        if (isBusy)
            return;
        if (Grid.SelectedRows.Count == 0 || Grid.SelectedRows[0].DataBoundItem is not DataRowView selected)
        {
            Warn("Select a row in the history grid first, then choose Locate Missing File.");
            return;
        }
        DataRow row = selected.Row;
        long backupId = Convert.ToInt64(row["BackupId"]);
        string recordedFileName = row["FileName"] as string ?? string.Empty;
        string recordedPath = row["FilePath"] as string ?? string.Empty;
        if (File.Exists(recordedPath))
        {
            Info($"This backup's recorded file is already available:\n\n{recordedPath}");
            return;
        }
        using var dialog = new OpenFileDialog {
            Title = $"Locate {recordedFileName}",
            Filter = "SQL Server Backup Files (*.bak)|*.bak|All Files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            RestoreDirectory = true
        };
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
            return;
        var result = await Task.Run(() => DatabaseBackupManager.RelocateBackupPath(backupId, dialog.FileName));
        if (result.Success)
        {
            SetStatus("Backup record relocated.");
            await LoadHistoryAsync();
        }
        else
        {
            Warn(result.Message);
        }
    }
    void SetBusyState(bool busy)
    {
        backupNowButton.Enabled = !busy;
        if (retryPendingButton != null) retryPendingButton.Enabled = !busy;
        if (cloudSettingsButton != null) cloudSettingsButton.Enabled = !busy;
        if (cloudChooseRestoreButton != null) cloudChooseRestoreButton.Enabled = !busy;
        if (selectRecoveryButton != null) selectRecoveryButton.Enabled = !busy;
        /* Keep Refresh History available for retrying a failed read. */
        if (refreshHistoryButton is not null)
            refreshHistoryButton.Enabled = !busy;
        if (busy)
        {
            if (restoreButton != null)
                restoreButton.Enabled = false;
            if (locateFileButton != null)
                locateFileButton.Enabled = false;
            return;
        }
        /* Restore the page-specific enabled state. */
        ApplyHistoryDependentEnablement();
    }
}
