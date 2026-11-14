using FMSC_SMS.Core;
using FMSC_SMS.Services.Backup;
using System.Diagnostics;
using System.Security.Cryptography;
namespace FMSC_SMS.Pages.Administration;
public sealed class BackupRestorePage : V2Page, IBusyPage
{
    readonly ModernTextBox backupFolder = HumanUi.TextBox(false, 500);
    readonly ModernTextBox restoreFile = HumanUi.TextBox(false, 500);
    readonly Button browseBackupFolder;
    readonly Button? browseRestoreFile;
    readonly bool canRestore = CurrentUser.HasPermission("Backup.Restore");
    private readonly Label operationStage = new() {
        Dock = DockStyle.Fill, Font = AppTheme.FontSmallBold, AutoEllipsis = true,
        TextAlign = ContentAlignment.MiddleLeft
    };
    private readonly ProgressBar operationBar = new() { Dock = DockStyle.Fill };
    private readonly Label operationStats = new() {
        Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = AppTheme.MutedText,
        TextAlign = ContentAlignment.MiddleLeft
    };
    private readonly Stopwatch operationWatch = new();
    private readonly System.Windows.Forms.Timer operationTimer = new() { Interval = 500 };
    private RowStyle operationRow = null!;
    private BackupTransferProgress? operationTransfer;
    private TimeSpan transferUpdatedAt;
    ModernButton backupNowButton = null!;
    private readonly LinkLabel toggleTechnicalDetails = new() {
        Dock = DockStyle.Top, Height = AppScale.Scale(24),
        Text = "Show technical details", LinkColor = AppTheme.Primary,
        TextAlign = ContentAlignment.MiddleLeft
    };
    private readonly TextBox technicalDetails = new() {
        Dock = DockStyle.Top, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Vertical, Height = AppScale.Scale(110), Visible = false
    };
    readonly ModernButton retryPendingButton;
    private readonly ModernButton refreshListsButton;
    private readonly Label historyCount = new() {
        Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight,
        ForeColor = AppTheme.MutedText, AutoEllipsis = true
    };
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    private readonly TabPage backupTab = new("Backup / Restore") { BackColor = AppTheme.Surface };
    ModernButton? cloudSettingsButton;
    ModernButton? selectCloudCopyButton;
    FlowLayoutPanel? cloudCopyActions;
    readonly RecoveryPointsGrid availableBackupsGrid = CreateAvailableBackupsGrid();
    readonly Label availableBackupsStatus = new() {
        Dock = DockStyle.Top, Height = AppScale.Scale(42), AutoEllipsis = true,
        ForeColor = AppTheme.MutedText, TextAlign = ContentAlignment.MiddleLeft
    };
    int recoveryCatalogueVersion;
    bool selectingBackup;
    private readonly DataGridView recordedCloudActivity = new() {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
        AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
        RowHeadersVisible = false, MultiSelect = false, BorderStyle = BorderStyle.None,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None
    };
    CloudBackupCoordinator.CloudSelection? selectedCloud;
    IReadOnlyList<CloudRemoteBackup> selectedCloudAlternatives = Array.Empty<CloudRemoteBackup>();
    string cloudRecoveryPassphrase = string.Empty;
    string selectedLocalRecoverySha256 = string.Empty;
    private string cloudStatusDetails = string.Empty;
    private readonly LinkLabel viewCloudStatus = new() {
        Text = "Last cloud backup details", AutoSize = true, LinkColor = AppTheme.Primary,
        Anchor = AnchorStyles.Left, Visible = false
    };
    private Label backupStatusHeadline = null!;
    private Label backupStatusDetail = null!;
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
    readonly ModernButton refreshHistoryButton;
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
        backupFolder.PlaceholderText = "No local folder selected";
        restoreFile.ReadOnly = true;
        restoreFile.PlaceholderText = "No backup selected";
        const string folderBrowseLabel = "Browse for local backup folder";
        browseBackupFolder = backupFolder.AddFolderBrowseButton(folderBrowseLabel, async (_, _) => {
            try { await ChooseBackupFolderAsync(); }
            catch (Exception ex) { Fail(ex); }
        });
        SetToolTip(browseBackupFolder, folderBrowseLabel);
        backupNowButton =
            AddBusyAction("Back Up Now", BackupNowAsync, "Success");
        if (canRestore)
        {
            const string fileBrowseLabel = "Browse for a backup file";
            browseRestoreFile = restoreFile.AddFolderBrowseButton(fileBrowseLabel, (_, _) => {
                try { ChooseRestoreFile(); }
                catch (Exception ex) { Fail(ex); }
            });
            SetToolTip(browseRestoreFile, fileBrowseLabel);
            selectCloudCopyButton =
                AddAction("Choose Cloud Copy…", (_, _) => UseSelectedCloudCopy(), "Secondary");
            /* Prevent duplicate restore submissions. */
            restoreButton =
                AddBusyAction("Restore Now", RestoreNowAsync, "Danger");
            /* Preserve disabled states when the busy scope restores actions. */
            restoreButton.EnabledChanged += (_, _) => {
                if ((!restoreAllowed || isBusy) &&
                    restoreButton is { Enabled: true })
                    restoreButton.Enabled = false;
            };
        }
        refreshListsButton = AddBusyAction("Refresh Lists", async () => {
            await LoadHistoryAsync();
            await LoadRecoveryCatalogueAsync();
        });
        refreshHistoryButton = AddBusyAction("Refresh History", LoadHistoryAsync);
        if (canRestore)
            cloudSettingsButton = AddAction("Settings…", (_, _) => OpenCloudSettings(), "Secondary");
        retryPendingButton = AddBusyAction("Retry Pending Uploads", RetryPendingAsync, "Secondary");
        retryPendingButton.Visible = false;
        locateFileButton =
            AddBusyAction("Locate Missing File…", LocateMissingFileAsync, "Secondary");
        RelocateFromActions(refreshListsButton, refreshHistoryButton, retryPendingButton, locateFileButton, backupNowButton);
        if (cloudSettingsButton is not null)
            RelocateFromActions(cloudSettingsButton);
        if (selectCloudCopyButton is not null)
            RelocateFromActions(selectCloudCopyButton);
        if (restoreButton is not null)
            RelocateFromActions(restoreButton);
        var backupCard = HumanUi.Card("Backup", "Save a verified copy to your backup destinations.");
        var backupStatus = CreatePanelStatus(out backupStatusHeadline, out backupStatusDetail);
        CreatePanelFields(backupCard, "Local Backup Folder", backupFolder,
            CreateCenteredActionRow(backupNowButton), backupStatus);
        viewCloudStatus.LinkClicked += (_, _) =>
            ModernMessageDialog.ShowInfo(FindForm(), cloudStatusDetails, "Last Cloud Backup");

        // Leave most of the backup tab for the recovery list.
        var root = new TableLayoutPanel {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.PageActionBarHeight));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        operationRow = new RowStyle(SizeType.Absolute, 0);
        root.RowStyles.Add(operationRow);
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
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
                "Restore a backup. Newer changes will be lost.");
            restoreStatusPanel = CreatePanelStatus(out var headline, out var detail);
            restoreStatusHeadline = headline;
            restoreStatusDetail = detail;
            CreatePanelFields(restoreCard, "Selected Backup", restoreFile,
                CreateCenteredActionRow(restoreButton!), restoreStatusPanel);
            restoreCard.Margin = new Padding(AppMetrics.SpacingXs, 0, 0, 0);
            upper.Controls.Add(restoreCard, 1, 0);
            /* No selected file means unverified, not valid. */
            ShowRestoreFileStatus(RestoreFileState.None, null);
        }
        var recoveryList = new Panel {
            Dock = DockStyle.Fill, Margin = new Padding(0, AppMetrics.SpacingXs, 0, 0)
        };
        var activityTab = new TabPage("Activity History") { BackColor = AppTheme.Surface };
        backupTab.Padding = activityTab.Padding = new Padding(UiDesignSystem.Spacing.Md);
        tabs.Selecting += (_, e) => { if (isBusy) e.Cancel = true; };
        tabs.SelectedIndexChanged += (_, _) => {
            UpdateSelectedBackupAction();
            ApplyHistoryDependentEnablement();
        };
        availableBackupsGrid.SelectionChanged += (_, _) => UpdateSelectedBackupAction();
        availableBackupsGrid.CellDoubleClick += async (_, e) => {
            if (canRestore && e.RowIndex >= 0) await UseSelectedRecoveryPointAsync();
        };
        if (canRestore)
            availableBackupsGrid.LoadSelected += async (_, _) => await UseSelectedRecoveryPointAsync();
        recoveryList.Controls.Add(availableBackupsGrid);
        recoveryList.Controls.Add(availableBackupsStatus);
        if (canRestore)
        {
            cloudCopyActions = CreateCompactActionRow(selectCloudCopyButton!);
            cloudCopyActions.Dock = DockStyle.Top;
            cloudCopyActions.Height = AppMetrics.ButtonRowHeight;
            cloudCopyActions.Visible = false;
            recoveryList.Controls.Add(cloudCopyActions);
        }
        var activityLayout = new TableLayoutPanel {
            Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 1,
            Margin = Padding.Empty, Padding = Padding.Empty
        };
        activityLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.PageActionBarHeight));
        activityLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        activityLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        activityLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        activityLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        activityLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        ConfigureActivityGrids();
        var activityLinks = new FlowLayoutPanel {
            Dock = DockStyle.Fill, AutoSize = true, Margin = Padding.Empty,
            Padding = new Padding(0, AppMetrics.SpacingXs, 0, AppMetrics.SpacingXs)
        };
        activityLinks.Controls.Add(viewCloudStatus);
        toggleTechnicalDetails.Dock = DockStyle.None;
        toggleTechnicalDetails.AutoSize = true;
        activityLinks.Controls.Add(toggleTechnicalDetails);
        var viewSqlRecord = new LinkLabel {
            Text = "Selected SQL record details", AutoSize = true,
            LinkColor = AppTheme.Primary, Enabled = false
        };
        viewSqlRecord.LinkClicked += (_, _) => ShowSelectedSqlRecord();
        Grid.SelectionChanged += (_, _) => {
            viewSqlRecord.Enabled = SelectedSqlRecord is not null;
            ApplyHistoryDependentEnablement();
        };
        activityLinks.Controls.Add(viewSqlRecord);
        var historyToolbar = new TableLayoutPanel {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty
        };
        historyToolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        historyToolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        historyCount.AutoSize = true;
        historyToolbar.Controls.Add(CreateCompactActionRow(refreshHistoryButton, locateFileButton), 0, 0);
        historyToolbar.Controls.Add(historyCount, 1, 0);
        activityLayout.Controls.Add(historyToolbar, 0, 0);
        activityLayout.Controls.Add(activityLinks, 0, 1);
        activityLayout.Controls.Add(technicalDetails, 0, 2);
        activityLayout.Controls.Add(ActivitySection("Recent activity", recordedCloudActivity), 0, 3);
        activityLayout.Controls.Add(ActivitySection("SQL backup and restore records", Grid), 0, 4);
        toggleTechnicalDetails.LinkClicked += (_, _) => {
            technicalDetails.Visible = !technicalDetails.Visible;
            toggleTechnicalDetails.Text = technicalDetails.Visible
                ? "Hide technical details" : "Show technical details";
        };
        activityTab.Controls.Add(activityLayout);
        if (!canRestore)
            upper.SetColumnSpan(backupCard, 2);
        var backupToolbar = cloudSettingsButton is null
            ? CreateCompactActionRow(refreshListsButton, retryPendingButton)
            : CreateCompactActionRow(refreshListsButton, cloudSettingsButton, retryPendingButton);
        root.Controls.Add(backupToolbar, 0, 0);
        root.Controls.Add(upper, 0, 1);
        root.Controls.Add(CreateOperationPanel(), 0, 2);
        root.Controls.Add(recoveryList, 0, 3);
        backupTab.Controls.Add(root);
        tabs.TabPages.Add(backupTab);
        tabs.TabPages.Add(activityTab);
        TabStrip.Apply(tabs, Accent);
        AppTheme.ThemeChanged += RepaintTabs;
        operationTimer.Tick += (_, _) => RenderOperationStats();
        Disposed += (_, _) => {
            AppTheme.ThemeChanged -= RepaintTabs;
            operationTimer.Dispose();
        };
        SetContent(TabStrip.CreateHost(tabs));
        RefreshCloudStatus();
        LoadBackupFolder();
        /* Keep history-dependent actions disabled until loading succeeds. */
        CloseHistoryDependentActions();
        Defer(LoadHistoryAsync);
        Defer(LoadRecoveryCatalogueAsync);
        RefreshRecordedActivity();
        UpdateSelectedBackupAction();
    }

    private void RepaintTabs(object? sender, EventArgs e) => TabStrip.Repaint(tabs);

    private Control CreateOperationPanel()
    {
        var panel = new TableLayoutPanel {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3,
            Margin = Padding.Empty, Padding = new Padding(AppMetrics.SpacingSm, 0, AppMetrics.SpacingSm, 0),
            BackColor = AppTheme.Tint(AppTheme.MutedText, 0.94f)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 24));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
        panel.Controls.Add(operationStage, 0, 0);
        panel.Controls.Add(operationBar, 0, 1);
        panel.Controls.Add(operationStats, 0, 2);
        return panel;
    }

    private void StartOperationProgress(string stage)
    {
        operationWatch.Restart();
        operationRow.Height = 2 * AppMetrics.TextHeight(AppTheme.NormalFont) + AppScale.Scale(24);
        ReportBackupStage(new BackupProgressUpdate(stage));
        operationTimer.Start();
    }

    private void StopOperationProgress()
    {
        if (!operationWatch.IsRunning) return;
        operationWatch.Stop();
        operationTimer.Stop();
        operationBar.Style = ProgressBarStyle.Continuous;
        operationRow.Height = 0;
    }

    private void RenderOperationStats()
    {
        TimeSpan elapsed = operationWatch.Elapsed;
        string elapsedText = "Elapsed " + TransferMetrics.DurationText(elapsed);
        if (operationTransfer is not { } transfer)
        {
            operationStats.Text = elapsedText;
            return;
        }

        string amount = TransferMetrics.SizeText(transfer.Bytes);
        long? remaining = null;
        if (transfer.TotalBytes is long total && total > 0)
        {
            int percent = (int)Math.Clamp(100d * transfer.Bytes / total, 0, 100);
            amount = $"{percent}%   ·   {amount} / {TransferMetrics.SizeText(total)}";
            remaining = Math.Max(0, total - transfer.Bytes);
        }
        double speed = elapsed - transferUpdatedAt < TimeSpan.FromSeconds(5) ? transfer.BytesPerSecond : 0;
        string speedText = speed > 0 ? TransferMetrics.SizeText(speed) + "/s" : "—";
        TimeSpan? eta = remaining is long bytes ? TransferMetrics.EstimateRemaining(bytes, speed) : null;
        string timeLeft = eta is TimeSpan estimate ? "~" + TransferMetrics.DurationText(estimate) : "—";
        operationStats.Text = $"{amount}   ·   {speedText}   ·   {elapsedText}   ·   Time left {timeLeft}";
    }

    private static void CreatePanelFields(HumanCard card, string caption, Control field,
        Control actions, Control status)
    {
        int statusHeight = AppMetrics.TextHeight(AppTheme.FontSmallBold) +
            AppMetrics.TextHeight(AppTheme.NormalFont) + 2 * AppMetrics.SpacingSm;
        var fields = new TableLayoutPanel {
            Dock = DockStyle.Top,
            ColumnCount = 1,
            RowCount = 3,
            Height = AppMetrics.FieldRowHeight + AppMetrics.ButtonRowHeight + statusHeight,
            Margin = Padding.Empty
        };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.FieldRowHeight));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.ButtonRowHeight));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, statusHeight));
        fields.Controls.Add(HumanUi.Field(caption, field), 0, 0);
        fields.Controls.Add(actions, 0, 1);
        fields.Controls.Add(status, 0, 2);
        card.Content.AutoScroll = true;
        card.Content.Controls.Add(fields);
        card.Content.AutoScrollMinSize = new Size(0, fields.Height + card.Content.Padding.Vertical);
    }

    private static Panel CreatePanelStatus(out Label headline, out Label detail)
    {
        headline = new Label {
            Dock = DockStyle.Top, Height = AppMetrics.TextHeight(AppTheme.FontSmallBold),
            Font = AppTheme.FontSmallBold, ForeColor = AppTheme.MutedText,
            TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true
        };
        detail = new Label {
            Dock = DockStyle.Fill, Font = AppTheme.NormalFont, ForeColor = AppTheme.MutedText,
            TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true
        };
        var panel = new Panel {
            Dock = DockStyle.Fill,
            Padding = new Padding(AppMetrics.SpacingSm, AppMetrics.SpacingXs,
                AppMetrics.SpacingSm, AppMetrics.SpacingXs),
            BackColor = AppTheme.Tint(AppTheme.MutedText, 0.90f)
        };
        panel.Controls.Add(detail);
        panel.Controls.Add(headline);
        return panel;
    }

    private void RefreshCloudStatus()
    {
        if (IsDisposed || Disposing) return;
        var status = CloudBackupCoordinator.ReadLastRunStatus();
        cloudStatusDetails = status.Message;
        viewCloudStatus.Visible = status.Message.Length > 0;
        var settings = CloudBackupSettings.LoadForEditing(out string? warning);
        retryPendingButton.Visible = CloudBackupCoordinator.HasRetryableDeliveries(settings);
        if (warning is not null)
        {
            backupStatusHeadline.Text = "Backup Settings Need Attention";
            backupStatusHeadline.ForeColor = AppTheme.Warning;
            backupStatusDetail.Text = "Open Backup Settings to repair the configuration.";
            return;
        }
        backupStatusHeadline.Text = status.Warning ? "Cloud Backup Needs Attention" : "Backup Destinations";
        backupStatusHeadline.ForeColor = status.Warning ? AppTheme.Warning : AppTheme.MutedText;
        if (status.Warning)
        {
            backupStatusDetail.Text = "See Last cloud backup details in Activity History.";
            return;
        }
        backupStatusDetail.Text =
            (settings.LocalEnabled ? "Local · " : "") +
            (settings.GoogleDriveEnabled ? "Google Drive · " : "") +
            (settings.DropboxEnabled ? "Dropbox · " : "") +
            (settings.AutomaticEnabled ? "Scheduled" : "Manual");
    }

    private void UpdateSelectedBackupAction()
    {
        var point = SelectedRecoveryPoint;
        bool canSelect = !isBusy && !selectingBackup;
        if (cloudCopyActions is not null && selectCloudCopyButton is not null)
        {
            bool canChooseCloudCopy = point is { HasLocal: true, CloudCopies.Count: > 0 };
            cloudCopyActions.Visible = canChooseCloudCopy;
            selectCloudCopyButton.Enabled = canSelect && canChooseCloudCopy;
        }
    }

    private UnifiedRecoveryPoint? SelectedRecoveryPoint =>
        tabs.SelectedTab == backupTab && availableBackupsGrid.SelectedRows.Count == 1
            ? availableBackupsGrid.SelectedRows[0].Tag as UnifiedRecoveryPoint
            : null;

    private void RefreshRecordedActivity()
    {
        if (IsDisposed || Disposing) return;
        recordedCloudActivity.Rows.Clear();
        foreach (var item in BackupActivityJournal.ReadRecent().Take(80))
        {
            int index = recordedCloudActivity.Rows.Add(item.Utc.ToLocalTime(), item.Operation,
                item.Destination, item.State, item.Detail);
            recordedCloudActivity.Rows[index].Cells["Details"].ToolTipText = item.Detail;
        }
        UiHelper.SetGridEmptyMessage(recordedCloudActivity, "No recent backup or restore activity.");
        ClearGridSelection(recordedCloudActivity);
    }

    private static Control ActivitySection(string title, DataGridView grid)
    {
        var section = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, AppMetrics.SpacingXs, 0, 0) };
        var heading = new Label {
            Text = title, Dock = DockStyle.Top, Font = AppTheme.FontSmallBold,
            ForeColor = AppTheme.MutedText, Height = AppMetrics.TextHeight(AppTheme.FontSmallBold) + AppMetrics.SpacingXs
        };
        section.Controls.Add(grid);
        section.Controls.Add(heading);
        return section;
    }

    private void ConfigureActivityGrids()
    {
        const string dateFormat = "dd-MMM-yyyy h:mm:ss tt";
        AddActivityColumn(recordedCloudActivity, "Time", "", 175, dateFormat);
        AddActivityColumn(recordedCloudActivity, "Activity", "", 135);
        AddActivityColumn(recordedCloudActivity, "Destination", "", 120);
        AddActivityColumn(recordedCloudActivity, "Result", "", 100);
        AddActivityColumn(recordedCloudActivity, "Details", "", 80, fill: true);
        HumanUi.ApplyGridStyle(recordedCloudActivity, Accent);

        Grid.AutoGenerateColumns = false;
        AddActivityColumn(Grid, "Recorded", "BackupDate", 175, dateFormat);
        AddActivityColumn(Grid, "Operation", "Operation", 80);
        AddActivityColumn(Grid, "Backup copy", "FileName", 145, fill: true);
        AddActivityColumn(Grid, "Size", "FileSizeBytes", 85);
        AddActivityColumn(Grid, "Result", "Status", 85);
        AddActivityColumn(Grid, "Availability", "FileAvailable", 95);
        Grid.CellFormatting += FormatSqlHistoryCell;
    }

    private static void AddActivityColumn(DataGridView grid, string header, string field, int width,
        string format = "", bool fill = false)
    {
        grid.Columns.Add(new DataGridViewTextBoxColumn {
            HeaderText = header, DataPropertyName = field, Name = header,
            Width = AppScale.Scale(width), MinimumWidth = AppScale.Scale(fill ? width : 55),
            AutoSizeMode = fill ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.None,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = new DataGridViewCellStyle { Format = format }
        });
    }

    private void FormatSqlHistoryCell(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
        switch (Grid.Columns[e.ColumnIndex].DataPropertyName)
        {
            case "FileName" when Grid.Rows[e.RowIndex].DataBoundItem is DataRowView record:
                string path = Convert.ToString(record["FilePath"]) ?? "";
                if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.Scheme == "cloud")
                {
                    e.Value = uri.Host.Equals("GoogleDrive", StringComparison.OrdinalIgnoreCase)
                        ? "Google Drive" : uri.Host.Equals("Dropbox", StringComparison.OrdinalIgnoreCase)
                            ? "Dropbox" : "Cloud backup";
                    e.FormattingApplied = true;
                }
                break;
            case "FileSizeBytes":
                e.Value = long.TryParse(Convert.ToString(e.Value), out long bytes) && bytes > 0
                    ? TransferMetrics.SizeText(bytes) : "—";
                e.FormattingApplied = true;
                break;
        }
    }

    private DataRowView? SelectedSqlRecord => Grid.SelectedRows.Count == 1
        ? Grid.SelectedRows[0].DataBoundItem as DataRowView : null;

    private void ShowSelectedSqlRecord()
    {
        if (SelectedSqlRecord is not { } record) return;
        var details = new[] {
            ("Recorded", "BackupDate"), ("Operation", "Operation"), ("Result", "Status"),
            ("User", "Username"), ("File name", "FileName"), ("Recorded path", "FilePath"),
            ("Remarks", "Remarks")
        };
        string text = string.Join(Environment.NewLine, details
            .Where(item => record.Row.Table.Columns.Contains(item.Item2))
            .Select(item => (Label: item.Item1, Value: record[item.Item2]))
            .Where(item => !string.IsNullOrWhiteSpace(Convert.ToString(item.Value)))
            .Select(item => item.Label + "  " + item.Value));
        ModernMessageDialog.ShowInfo(FindForm(), text, "SQL Backup and Restore Record");
    }

    private void ReportBackupStage(BackupProgressUpdate update)
    {
        if (IsDisposed || Disposing || !operationWatch.IsRunning) return;
        operationStage.Text = update.Display;
        operationStage.ForeColor = update.IsError ? AppTheme.Warning : AppTheme.MutedText;
        operationTransfer = update.Transfer;
        transferUpdatedAt = operationWatch.Elapsed;
        if (update.Transfer is { TotalBytes: > 0 } transfer)
        {
            operationBar.Style = ProgressBarStyle.Continuous;
            operationBar.Value = (int)Math.Clamp(100d * transfer.Bytes / transfer.TotalBytes.Value, 0, 100);
        }
        else
            operationBar.Style = ProgressBarStyle.Marquee;
        RenderOperationStats();
        if (update.IsError && update.Detail.Length > 0)
            operationStage.Text += " · " + update.Detail;
        if (update.Detail.Length > 0)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + update.Display +
                " — " + update.Detail + Environment.NewLine;
            if (technicalDetails.TextLength > 12000) technicalDetails.Clear();
            technicalDetails.AppendText(line);
        }
    }

    private sealed class RecoveryPointsGrid : DataGridView
    {
        internal event EventHandler? LoadSelected;

        protected override bool ProcessDialogKey(Keys keyData) =>
            TryLoadSelected(keyData) || base.ProcessDialogKey(keyData);

        protected override bool ProcessDataGridViewKey(KeyEventArgs e) =>
            TryLoadSelected(e.KeyData) || base.ProcessDataGridViewKey(e);

        private bool TryLoadSelected(Keys keyData)
        {
            if (keyData != Keys.Enter || SelectedRows.Count != 1 || LoadSelected is null)
                return false;
            LoadSelected(this, EventArgs.Empty);
            return true;
        }
    }

    private static RecoveryPointsGrid CreateAvailableBackupsGrid()
    {
        var grid = new RecoveryPointsGrid {
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
            HeaderText = "SQL backup date", Width = AppScale.Scale(185),
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn {
            HeaderText = "Age", Width = AppScale.Scale(80),
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn {
            HeaderText = "Available copies", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            MinimumWidth = AppScale.Scale(105), FillWeight = 40,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn {
            HeaderText = "Copy size", Width = AppScale.Scale(110),
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn {
            HeaderText = "Recovery status", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            MinimumWidth = AppScale.Scale(160), FillWeight = 60,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        return grid;
    }

    private static TableLayoutPanel CreateCenteredActionRow(ModernButton button)
    {
        var row = new TableLayoutPanel {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(0, AppMetrics.SpacingXs, 0, AppMetrics.SpacingXs)
        };
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        button.AutoSize = false;
        button.MinimumSize = Size.Empty;
        button.Dock = DockStyle.Fill;
        button.Margin = new Padding(AppMetrics.SpacingXs, 0, AppMetrics.SpacingXs, 0);
        row.Controls.Add(button, 1, 0);
        return row;
    }

    private static FlowLayoutPanel CreateCompactActionRow(params ModernButton[] buttons)
    {
        var row = new FlowLayoutPanel {
            Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true, Margin = Padding.Empty,
            Padding = new Padding(0, AppMetrics.SpacingXs, 0, 0)
        };
        row.Controls.AddRange(buttons);
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
        {
            bool missingLocal = SelectedSqlRecord is { } record && Equals(record["FileAvailable"], "Missing");
            locateFileButton.Visible = missingLocal;
            locateFileButton.Enabled = usable && missingLocal;
        }
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
        try
        {
            var settings = CloudBackupSettings.Load();
            if (settings.LocalEnabled &&
                (string.IsNullOrWhiteSpace(backupFolder.Text) || !Directory.Exists(backupFolder.Text)))
            {
                Warn("Choose a valid local backup folder or disable local backups in Backup Settings.");
                return;
            }
            technicalDetails.Clear();
            StartOperationProgress("Preparing backup");
            var progress = new Progress<BackupProgressUpdate>(ReportBackupStage);
            var result = await CloudBackupCoordinator.CreateAndUploadAsync(progress: progress);
            StopOperationProgress();
            await LoadHistoryAsync();
            await LoadRecoveryCatalogueAsync();
            RefreshRecordedActivity();
            if (result.UploadsPending) Warn(result.Message);
            else Info(result.Message);
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
                RefreshCloudStatus();
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
            technicalDetails.Clear();
            StartOperationProgress("Retrying pending cloud deliveries");
            string result = await CloudBackupCoordinator.RetryPendingUploadsNowAsync(
                progress: new Progress<BackupProgressUpdate>(ReportBackupStage));
            StopOperationProgress();
            RefreshCloudStatus();
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
            DateTime nowUtc = DateTime.UtcNow;
            foreach (var point in inventory.Points)
            {
                var sizes = point.LocalCopies.Select(copy => copy.SizeBytes)
                    .Concat(point.CloudCopies.Select(copy => (long?)copy.Remote.Size))
                    .Where(size => size is > 0).Select(size => size!.Value).Order().ToArray();
                string sizeText = sizes.Length == 0 ? "—" : TransferMetrics.SizeText(sizes[0]);
                if (sizes.Length > 1 && sizes[0] != sizes[^1])
                    sizeText = $"{TransferMetrics.SizeText(sizes[0])} – {TransferMetrics.SizeText(sizes[^1])}";
                int rowIndex = availableBackupsGrid.Rows.Add(
                    point.SqlBackupCreatedUtc.ToLocalTime().ToString("dd-MMM-yyyy h:mm:ss tt"),
                    FormatRecoveryPointAge(nowUtc - point.SqlBackupCreatedUtc),
                    point.AvailableSources,
                    sizeText,
                    point.VerificationSummary);
                var row = availableBackupsGrid.Rows[rowIndex];
                row.Tag = point;
                string copyDetails = FormatRecoveryCopyDetails(point);
                for (int column = 2; column < row.Cells.Count; column++)
                    row.Cells[column].ToolTipText = copyDetails;
            }
            availableBackupsGrid.ClearSelection();
            string countText = inventory.Points.Count == 0
                ? "No backups found. Browse for a backup file or connect a cloud account in Backup Settings."
                : canRestore ? "Double-click a backup or press Enter to load it for restore."
                    : inventory.Points.Count + " available backups.";
            availableBackupsStatus.Text = inventory.Warnings.Count == 0
                ? countText : countText + "  " + string.Join(" ", inventory.Warnings);
        }
        catch (Exception ex)
        {
            if (IsDisposed || version != recoveryCatalogueVersion) return;
            AppExceptionHandler.LogNonFatal(ex, "Loading unified backup recovery-point catalogue");
            availableBackupsStatus.Text =
                "Could not refresh available backups. Refresh the list or browse for a backup file.";
        }
    }

    private static string FormatRecoveryPointAge(TimeSpan age) =>
        age < TimeSpan.Zero ? "—" :
        age.TotalDays >= 1 ? $"{(int)age.TotalDays} d" :
        age.TotalHours >= 1 ? $"{(int)age.TotalHours} h {age.Minutes} m" :
        age.TotalMinutes >= 1 ? $"{(int)age.TotalMinutes} min" : "<1 min";

    private static string FormatRecoveryCopyDetails(UnifiedRecoveryPoint point)
    {
        var local = point.LocalCopies.Select(copy =>
            $"Local · {Path.GetFileName(copy.FilePath)} · " +
            (copy.SizeBytes is > 0 ? TransferMetrics.SizeText(copy.SizeBytes.Value) + " · " : "") +
            "SQL header checked");
        var cloud = point.CloudCopies.Select(copy =>
            $"{copy.Remote.Provider} · {TransferMetrics.SizeText(copy.Remote.Size)} · " +
            (copy.Remote.UploadedUtc == default ? "" : $"Uploaded {copy.Remote.UploadedUtc.ToLocalTime():dd-MMM-yyyy h:mm tt} · ") +
            (copy.RecoveryTested ? "Recovery-tested" : "Verify before restore"));
        return string.Join(Environment.NewLine, local.Concat(cloud));
    }

    private async Task UseSelectedRecoveryPointAsync()
    {
        if (isBusy || selectingBackup || !canRestore) return;
        if (SelectedRecoveryPoint is not { } point)
        {
            Warn("Select a row in Available Backups first.");
            return;
        }
        selectingBackup = true;
        UpdateSelectedBackupAction();
        try
        {
            RequirePermission("Backup.Restore");
            if (point.LocalCopies.Count > 0)
            {
                string path = point.LocalCopies[0].FilePath;
                if (!File.Exists(path))
                {
                    if (point.CloudCopies.Count > 0)
                        SelectCloudCopy(point);
                    else
                        Warn("This local backup is no longer available. Refresh the backup list.");
                    return;
                }
                var info = await Task.Run(() => DatabaseBackupManager.GetBackupInfo(path));
                if (IsDisposed || isBusy || !ReferenceEquals(SelectedRecoveryPoint, point)) return;
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
                    SetStatus("Local backup selected.");
                else
                    Warn("The selected local file could not be verified as an FMSC database backup.");
                return;
            }
            SelectCloudCopy(point);
        }
        catch (Exception ex) { Fail(ex); }
        finally
        {
            selectingBackup = false;
            if (!IsDisposed) UpdateSelectedBackupAction();
        }
    }

    private void UseSelectedCloudCopy()
    {
        if (isBusy || selectingBackup ||
            SelectedRecoveryPoint is not { HasLocal: true, CloudCopies.Count: > 0 } point) return;
        try
        {
            RequirePermission("Backup.Restore");
            SelectCloudCopy(point);
        }
        catch (Exception ex) { Fail(ex); }
    }

    private void SelectCloudCopy(UnifiedRecoveryPoint point)
    {
        using var dialog = new CloudRestoreSelectionDialog(point);
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK ||
            dialog.SelectedCopy is not { } selected) return;
        selectedCloud = selected;
        selectedCloudAlternatives = point.CloudCopies.Select(x => x.Remote).ToArray();
        cloudRecoveryPassphrase = dialog.RecoveryPassphrase;
        selectedRestoreBackupInfo = null;
        selectedLocalRecoverySha256 = string.Empty;
        restoreFile.Tag = null;
        restoreFile.Text = selected.Remote.Provider + " — " +
            selected.Remote.SqlBackupCreatedUtc.ToLocalTime().ToString("dd-MMM-yyyy hh:mm tt");
        ShowRestoreFileStatus(RestoreFileState.Valid, null);
        if (restoreStatusHeadline != null)
            restoreStatusHeadline.Text = "Cloud Backup Selected";
        if (restoreStatusDetail != null)
            restoreStatusDetail.Text = "FMSC verifies the cloud copy before restoring it.";
        SetStatus("Cloud backup selected.");
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
            StartOperationProgress("Preparing cloud restore");
            var result = await CloudBackupCoordinator.RestoreFromCloudAsync(
                selection.Remote, cloudRecoveryPassphrase, selectedCloudAlternatives,
                progress: new Progress<BackupProgressUpdate>(ReportBackupStage));
            StopOperationProgress();
            if (result.Success)
            {
                // CloudRestoreStaging has already removed the temporary
                // download after RestoreBackup verified successful recovery.
                cloudRecoveryPassphrase = string.Empty;
                selectedCloud = null;
                BackupActivityJournal.TryRecord("Restore", "Cloud", "Completed");
                ModernMessageDialog.Show(FindForm(),
                    result.Message + "\n\nFMSC will restart.",
                    "Restore Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                isBusy = false; busyMessage = string.Empty;
                RestartApplication(FindForm());
                return;
            }
            BackupActivityJournal.TryRecord("Restore", "Cloud", "Failed");
            await LoadHistoryAsync();
            Warn(result.Message);
        }
        catch (Exception ex)
        {
            BackupActivityJournal.TryRecord("Restore", "Cloud", "Failed");
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
        using var dialog = new OpenFileDialog {
            Title = "Select FMSC Database Backup",
            Filter = "SQL Server Backup Files (*.bak)|*.bak|All Files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            RestoreDirectory = true
        };
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
            return;
        selectedCloud = null;
        selectedCloudAlternatives = Array.Empty<CloudRemoteBackup>();
        cloudRecoveryPassphrase = string.Empty;
        selectedLocalRecoverySha256 = string.Empty;
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
                detail = "Browse for a file or select a backup below.";
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
            if (string.IsNullOrWhiteSpace(input.Text))
            {
                ModernMessageDialog.ShowWarning(window, "Enter the Administrator password or separate passphrase used for this backup.",
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

    private async Task<bool> TryCloudFallbackForUnavailableLocalAsync()
    {
        if (selectedCloudAlternatives.Count == 0 ||
            !UnifiedRecoveryCatalog.IsSha256(selectedLocalRecoverySha256)) return false;
        string? passphrase = AskFallbackPassphrase();
        if (passphrase is null) return true; // The user chose not to restore.
        selectedCloud = new CloudBackupCoordinator.CloudSelection(
            selectedCloudAlternatives[0], KnownVerifiedUpload: false,
            SourceBakSha256: selectedLocalRecoverySha256);
        cloudRecoveryPassphrase = passphrase;
        passphrase = string.Empty;
        await RestoreCloudNowAsync();
        return true;
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
            if (await TryCloudFallbackForUnavailableLocalAsync()) return;
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
            if (await TryCloudFallbackForUnavailableLocalAsync()) return;
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
        try
        {
            technicalDetails.Clear();
            StartOperationProgress("Preparing local restore");
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
            StopOperationProgress();
            if (result.Success)
            {
                BackupActivityJournal.TryRecord("Restore", beforeSqlFailed ? "Cloud fallback" : "Local", "Completed");
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
            BackupActivityJournal.TryRecord("Restore", beforeSqlFailed ? "Cloud fallback" : "Local", "Failed");
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
        RefreshCloudStatus();
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
                Grid.DataSource = history;
                ReportGridStyler.Apply(Grid, Accent);
                ClearGridSelection(Grid);
                historyCount.Text = $"{history.Rows.Count:N0} SQL record(s)";
                historyLoadedLatest = true;
                RefreshRecordedActivity();
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
        if (SelectedSqlRecord is not { } selected)
        {
            Warn("Select a row in the history grid first, then choose Locate Missing File.");
            return;
        }
        DataRow row = selected.Row;
        long backupId = Convert.ToInt64(row["BackupId"]);
        string recordedFileName = row["FileName"] as string ?? string.Empty;
        string recordedPath = row["FilePath"] as string ?? string.Empty;
        if (recordedPath.StartsWith("cloud://", StringComparison.OrdinalIgnoreCase))
        {
            Warn("Select a missing local file from the SQL records.");
            return;
        }
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
        if (!busy) StopOperationProgress();
        browseBackupFolder.Enabled = !busy;
        if (browseRestoreFile is not null) browseRestoreFile.Enabled = !busy;
        backupNowButton.Enabled = !busy;
        retryPendingButton.Enabled = !busy;
        if (cloudSettingsButton != null) cloudSettingsButton.Enabled = !busy;
        refreshListsButton.Enabled = !busy;
        UpdateSelectedBackupAction();
        /* Keep Refresh History available for retrying a failed read. */
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
