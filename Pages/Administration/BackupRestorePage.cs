using FMSC_SMS.Core;
using FMSC_SMS.Services.Backup;
namespace FMSC_SMS.Pages.Administration;
public sealed class BackupRestorePage : V2Page, IBusyPage
{
    readonly ModernTextBox backupFolder = HumanUi.TextBox(false, 500);
    readonly ModernTextBox restoreFile = HumanUi.TextBox(false, 500);
    readonly bool canRestore = CurrentUser.HasPermission("Backup.Restore");
    ModernButton backupNowButton = null!;
    ModernButton? cloudBackupButton;
    ModernButton? cloudSettingsButton;
    ModernButton? cloudChooseRestoreButton;
    CloudBackupCoordinator.CloudSelection? selectedCloud;
    string cloudRecoveryPassphrase = string.Empty;
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
            AddBusyAction("Back Up Data", BackupNowAsync, "Success");
        cloudBackupButton = AddBusyAction("Cloud Backup Now", CloudBackupNowAsync, "Secondary");
        if (canRestore)
            cloudSettingsButton = AddAction("Cloud Settings…", (_, _) => OpenCloudSettings());
        ModernButton? chooseRestoreButton = null;
        if (canRestore)
        {
            chooseRestoreButton =
                AddAction("Choose Backup File…", (_, _) => ChooseRestoreFile());
            cloudChooseRestoreButton =
                AddBusyAction("Choose Cloud Backup…", ChooseCloudRestoreAsync);
            /* Prevent duplicate restore submissions. */
            restoreButton =
                AddBusyAction("Restore Data", RestoreNowAsync, "Danger");
            /* Preserve disabled states when the busy scope restores actions. */
            restoreButton.EnabledChanged += (_, _) => {
                if ((!restoreAllowed || !HistoryDependentActionsUsable) &&
                    restoreButton is { Enabled: true })
                    restoreButton.Enabled = false;
            };
        }
        refreshHistoryButton = AddBusyAction("Refresh History", LoadHistoryAsync);
        locateFileButton =
            AddBusyAction("Locate Missing File…", LocateMissingFileAsync, "Secondary");
        RelocateFromActions(
            chooseBackupButton,
            backupNowButton);
        if (cloudSettingsButton is not null)
            RelocateFromActions(cloudSettingsButton);
        if (cloudBackupButton is not null)
            RelocateFromActions(cloudBackupButton);
        if (cloudChooseRestoreButton is not null)
            RelocateFromActions(cloudChooseRestoreButton);
        if (chooseRestoreButton is not null && restoreButton is not null)
            RelocateFromActions(
                chooseRestoreButton,
                restoreButton);
        UseToolbar();
        var backupCard = HumanUi.Card(
            "Backup",
            "Choose a folder and save a complete copy of your school data.");
        var localOnlyNotice = new Label {
            Dock = DockStyle.Top,
            Height = AppScale.Scale(46),
            AutoEllipsis = false,
            Font = AppTheme.NormalFont,
            ForeColor = AppTheme.MutedText,
            TextAlign = ContentAlignment.TopLeft,
            Text = "School records are saved on this computer. They are not shared over a " +
                   "network. Records entered on " +
                   "one computer will not appear on another. Back up regularly; " +
                   "this is the only copy of your data."
        };
        var backupFields = new TableLayoutPanel {
            Dock = DockStyle.Fill,
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
            HumanUi.Field("Backup Folder", backupFolder),
            0,
            0);
        backupFields.Controls.Add(
            CreateActionRow(chooseBackupButton, backupNowButton),
            0,
            1);
        backupFields.Controls.Add(
            canRestore ? CreateActionRow(cloudSettingsButton!, cloudBackupButton!)
                       : CreateActionRow(cloudBackupButton!),
            0, 2);
        backupCard.Content.Controls.Add(backupFields);
        backupCard.Content.Controls.Add(localOnlyNotice);
        var root = new TableLayoutPanel {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = canRestore ? 3 : 2
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute,
            AppMetrics.FieldCardHeight(1) + 2 * AppMetrics.ButtonRowHeight + localOnlyNotice.Height));
        root.Controls.Add(backupCard, 0, 0);
        if (canRestore)
        {
            var restoreCard = HumanUi.Card(
                "Restore",
                "Replace current school data with a saved backup. Newer records will be replaced.");
            var restoreFields = new TableLayoutPanel {
                Dock = DockStyle.Fill,
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
                CreateActionRow(cloudChooseRestoreButton!), 0, 3);
            restoreCard.Content.Controls.Add(restoreFields);
            root.RowStyles.Add(
                new RowStyle(
                    SizeType.Absolute,
                    AppMetrics.FieldCardHeight(1) +
                    2 * AppMetrics.ButtonRowHeight +
                    restoreStatusHeight));
            root.Controls.Add(restoreCard, 0, 1);
            /* No selected file means unverified, not valid. */
            ShowRestoreFileStatus(RestoreFileState.None, null);
        }
        var historyCard = HumanUi.Card(
            "Backup & Restore History",
            "Every backup and restore operation performed from this application.");
        historyCard.Content.Controls.Add(Grid);
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(historyCard, 0, canRestore ? 2 : 1);
        SetContent(root);
        LoadBackupFolder();
        /* Keep history-dependent actions disabled until loading succeeds. */
        CloseHistoryDependentActions();
        Defer(LoadHistoryAsync);
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
        if (historyLoadFailed)
            return;
        ApplyHistoryDependentEnablement();
    }
    /// <summary>Shared eligibility rules for Restore and Locate Missing File.</summary>
    void ApplyHistoryDependentEnablement()
    {
        bool usable = HistoryDependentActionsUsable;
        if (locateFileButton is not null)
            locateFileButton.Enabled = usable;
        if (restoreButton is not null)
            restoreButton.Enabled = usable && restoreAllowed;
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
        if (isBusy)
            return;
        string folder = backupFolder.Text.Trim();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            Warn("Please choose a valid backup folder first.");
            return;
        }
        string backupPath = DatabaseBackupManager.CreateUniqueBackupPath(
            folder,
            DatabaseBackupManager.CreateBackupFileName());
        isBusy = true;
        busyMessage = "A backup is running. Please wait until it finishes.";
        SetBusyState(true);
        SetStatus("Backing up school data…");
        try
        {
            var result = await Task.Run(() => DatabaseBackupManager.CreateBackup(backupPath));
            /* reload FIRST, then report. */
            await LoadHistoryAsync();
            if (result.Success)
            {
                /* Restoring replaces the database and requires explicit confirmation. */
                ModernMessageDialog.ShowInfo(
                    FindForm(),
                    $"{result.Message}\n\n{backupPath}");
            }
            else
            {
                Warn(result.Message);
            }
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
            SetBusyState(false);
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
                SetStatus("Cloud provider, schedule and retention settings saved.");
        }
        catch (Exception ex) { Fail(ex); }
    }

    private async Task CloudBackupNowAsync()
    {
        if (isBusy) return;
        isBusy = true;
        busyMessage = "Creating a verified, encrypted school backup and uploading enabled destinations.";
        SetBusyState(true);
        try
        {
            SetStatus("Creating encrypted cloud backup…");
            var result = await CloudBackupCoordinator.CreateAndUploadAsync();
            await LoadHistoryAsync();
            SetStatus(result.Message, !result.GoogleSucceeded && !result.DropboxSucceeded);
            if (result.LocalSucceeded)
                ModernMessageDialog.ShowInfo(FindForm(), result.Message);
        }
        catch (Exception ex) { Fail(ex); }
        finally
        {
            isBusy = false; busyMessage = string.Empty;
            if (!IsDisposed) SetBusyState(false);
        }
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
            if (options.Count == 0)
            {
                Warn("No cloud backups are available. Check the connected Google Drive and Dropbox accounts.");
                return;
            }
            DateTime? newestLocal = await Task.Run(DatabaseBackupManager.GetNewestAvailableLocalBackupUtc);
            using var dialog = new CloudRestoreSelectionDialog(options, newestLocal);
            if (dialog.ShowDialog(FindForm()) != DialogResult.OK ||
                dialog.SelectedCopy is not { } selected)
                return;
            selectedCloud = selected;
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
                selection.Remote, cloudRecoveryPassphrase);
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
            cloudRecoveryPassphrase = string.Empty;
            isBusy = false; busyMessage = string.Empty;
            if (!IsDisposed) SetBusyState(false);
        }
    }

    void ChooseRestoreFile()
    {
        if (isBusy)
            return;
        selectedCloud = null;
        cloudRecoveryPassphrase = string.Empty;
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
        /* Valid and stale backups require confirmation; unverifiable or foreign backups are rejected. */
        SetRestoreAllowed(
            state is RestoreFileState.Valid or RestoreFileState.Stale or RestoreFileState.None);
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
            var result = await Task.Run(() => DatabaseBackupManager.RestoreBackup(backupPath));
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
    /// <summary>Restart once after restore; if closing is refused, launch nothing and request a manual restart.</summary>
    static void RestartApplication(Form? shell)
    {
        var exePath = Environment.ProcessPath;
        if (shell == null || shell.IsDisposed || string.IsNullOrEmpty(exePath))
        {
            try
            {
                Application.Restart();
            }
            catch (Exception nonFatalException26)
            {
                FMSC_SMS.Core.AppExceptionHandler.LogNonFatal(nonFatalException26, "BackupRestorePage: RestartApplication fallback");
                Application.Exit();
            }
            return;
        }
        FormClosedEventHandler? launchNewInstance = null;
        launchNewInstance = (_, _) => {
            shell.FormClosed -= launchNewInstance;
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(exePath) {
                        UseShellExecute = true,
                        WorkingDirectory = AppContext.BaseDirectory
                    });
            }
            catch (Exception nonFatalException27)
            {
                FMSC_SMS.Core.AppExceptionHandler.LogNonFatal(nonFatalException27, "BackupRestorePage: RestartApplication fallback");
                /* The application is closing; the operator can start it again from the shortcut. */
            }
        };
        shell.FormClosed += launchNewInstance;
        shell.Close();
        if (!shell.IsDisposed)
        {
            /* The close was refused, so the window is still open and no second instance was started. */
            shell.FormClosed -= launchNewInstance;
            ModernMessageDialog.ShowInfo(
                shell,
                "The restore is complete, but FMSC could not close itself. " +
                "Please close FMSC and open it again.",
                "Restore Complete");
        }
    }
    /// <summary>Gate history-dependent actions for the entire read.</summary>
    async Task LoadHistoryAsync()
    {
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
        if (cloudBackupButton != null) cloudBackupButton.Enabled = !busy;
        if (cloudSettingsButton != null) cloudSettingsButton.Enabled = !busy;
        if (cloudChooseRestoreButton != null) cloudChooseRestoreButton.Enabled = !busy;
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
