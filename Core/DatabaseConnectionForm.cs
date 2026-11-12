
namespace FMSC_SMS.Core;

/// <summary>Also runs before login, so recovery never depends on a working database.</summary>
internal sealed class DatabaseConnectionForm : ApplicationWindowForm
{
    internal static bool IsConnectionProblem(Exception ex) =>
        ex is DatabaseRecoveryException or DatabaseCompatibilityException || ex is Microsoft.Data.SqlClient.SqlException sql &&
        (DatabaseHelper.IsConnectionFailure(sql) || sql.Number is 4060 or 911) ||
        ex.InnerException is not null && IsConnectionProblem(ex.InnerException);
    internal DatabaseConnectionForm(bool startup)
    {
        Text = DatabaseConnectionView.WindowTitle;
        StartPosition = startup ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = AppMetrics.StartupDialogClientSize;
        Font = AppTheme.NormalFont;
        BackColor = AppTheme.PageBackground;
        AutoScaleMode = AutoScaleMode.None;
        var view = new DatabaseConnectionView(this, startup ? _ => DialogResult = DialogResult.OK : null);
        Controls.Add(view);
        AcceptButton = view.AcceptAction;
        CancelButton = view.CancelAction;
        FormClosing += (_, e) => { if (view.Busy) e.Cancel = true; };
        Shown += async (_, _) => await view.ConnectAsync(false);
    }
}

internal sealed class DatabaseConnectionView : UserControl
{
    internal const string WindowTitle = "FMSC — Management System Connection";
    private readonly Form _host;
    private readonly Action<DatabaseConnectionView>? _connected;
    private readonly Func<bool, (bool Connected, string Message, Exception? Failure)> _connect;
    private readonly Action<int>? _checkUpdates;
    private readonly ModernButton _updates = new() { Text = "Open Updates", ThemeRole = "Primary", Visible = false };
    private int? _updateSchema;
    private readonly Label _message = new() { Dock = DockStyle.Top, AutoSize = true };
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoSize = false };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Style = ProgressBarStyle.Marquee };
    private readonly ModernButton _reconnect = new() { Text = "Reconnect", ThemeRole = "Primary" };
    private readonly ModernButton _restart = new() { Text = "Restart Connection", ThemeRole = "Secondary" };
    private readonly ModernButton _repair = new() { Text = "Repair Connection", ThemeRole = "Secondary" };
    private readonly ModernButton _close = new() { Text = UiText.Actions.Close, ThemeRole = "Secondary" };
    private readonly ModernButton _restoreSchool = new() {
        Text = "Restore Existing School", ThemeRole = "Secondary", Visible = false
    };
    private readonly ModernTextBox _details = new() {
        Multiline = true, ReadOnly = true, Dock = DockStyle.Fill,
        ScrollBars = ScrollBars.Vertical
    };
    private bool _busy;
    private bool _canRestart = true;
    private bool _canRepair;

    internal bool Busy => _busy;
    internal IButtonControl AcceptAction => _updateSchema.HasValue && _checkUpdates is not null ? _updates : _reconnect;
    internal IButtonControl CancelAction => _close;

    internal DatabaseConnectionView(Form host, Action<DatabaseConnectionView>? connected,
        Func<bool, (bool Connected, string Message, Exception? Failure)>? connect = null,
        Action<int>? checkUpdates = null)
    {
        _host = host;
        _connected = connected;
        _connect = connect ?? CheckConnection;
        _checkUpdates = checkUpdates;
        Dock = DockStyle.Fill;
        AutoScaleMode = AutoScaleMode.None;
        int gap = UiDesignSystem.Spacing.Md;
        var root = new TableLayoutPanel {
            Dock = DockStyle.Fill, Padding = new Padding(UiDesignSystem.Spacing.Lg),
            ColumnCount = 1, RowCount = 5, BackColor = AppTheme.PageBackground
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.TextHeight(AppTheme.PageTitleFont) + gap));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.ButtonHeight + gap * 2));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.ButtonHeight + gap));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        var title = new Label {
            Text = "", Dock = DockStyle.Fill,
            Font = AppTheme.PageTitleFont, ForeColor = AppTheme.HeadingText,
            TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty
        };
        var connectionCard = HumanUi.Card("Connection Status", accent: AppTheme.Primary);
        var state = new TableLayoutPanel {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3,
            BackColor = AppTheme.Surface, Margin = Padding.Empty
        };
        state.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        state.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.TextHeight(AppTheme.ButtonFont) + gap));
        state.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        state.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.S(12)));
        _message.Text = "Please wait while we connect you to the Database.";
        _message.ForeColor = AppTheme.Text;
        _message.UseMnemonic = false;
        _message.Margin = new Padding(0, 0, 0, gap);
        _status.Text = "Preparing connection………………";
        _status.Font = AppTheme.ButtonFont;
        _status.ForeColor = AppTheme.Primary;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Margin = Padding.Empty;
        _progress.Margin = Padding.Empty;
        state.Controls.Add(_status, 0, 0);
        // Keep the explanation readable when DPI or the working area leaves less room.
        var explanation = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
        explanation.Controls.Add(_message);
        explanation.SizeChanged += (_, _) => _message.MaximumSize = new Size(
            Math.Max(1, explanation.ClientSize.Width - SystemInformation.VerticalScrollBarWidth), 0);
        state.Controls.Add(explanation, 0, 1);
        state.Controls.Add(_progress, 0, 2);
        connectionCard.Content.Controls.Add(state);
        var actionHost = new TableLayoutPanel {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1,
            Padding = new Padding(0, gap, 0, 0), Margin = Padding.Empty
        };
        actionHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actionHost.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var actions = new FlowLayoutPanel {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.None, WrapContents = false, Padding = Padding.Empty, Margin = Padding.Empty
        };
        foreach (var button in new[] { _reconnect, _restart, _repair, _updates, _restoreSchool })
        {
            button.Height = AppMetrics.ButtonHeight;
            button.Width = TextRenderer.MeasureText(button.Text, button.Font).Width + AppScale.S(36);
            button.Margin = new Padding(UiDesignSystem.Spacing.Sm / 2, 0, UiDesignSystem.Spacing.Sm / 2, 0);
            actions.Controls.Add(button);
        }
        _reconnect.Width = _restart.Width;
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, AppScale.S(100)));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        var detailsLink = new LinkLabel {
            Text = "Show connection details", Dock = DockStyle.Fill,
            LinkColor = AppTheme.Primary, ActiveLinkColor = AppTheme.PrimaryHover, VisitedLinkColor = AppTheme.Primary,
            TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty
        };
        _close.Dock = DockStyle.Top;
        _close.Height = AppMetrics.ButtonHeight;
        _close.Margin = Padding.Empty;
        footer.Controls.Add(detailsLink, 0, 0);
        footer.Controls.Add(_close, 1, 0);
        var detailsCard = HumanUi.Card("Connection details", accent: AppTheme.Primary);
        detailsCard.Visible = false;
        _details.Font = AppTheme.NormalFont;
        detailsCard.Content.Controls.Add(_details);
        root.Controls.Add(title, 0, 0);
        root.Controls.Add(connectionCard, 0, 1);
        actionHost.Controls.Add(actions, 0, 0);
        root.Controls.Add(actionHost, 0, 2);
        root.Controls.Add(footer, 0, 3);
        root.Controls.Add(detailsCard, 0, 4);
        Controls.Add(root);
        _restoreSchool.Click += async (_, _) => {
#if !FMSC_LEVEL2
            if (_busy) return;
            try { Services.Backup.ReplacementSchoolRecovery.RequireAbsentOrVirginSchool(); }
            catch (Exception ex)
            {
                ModernMessageDialog.ShowWarning(_host, DbMessage.UserMessage(ex), "Replacement School Recovery");
                return;
            }
            using var recovery = new FMSC_SMS.Pages.Security.RestoreExistingSchoolForm();
            if (recovery.ShowDialog(_host) == DialogResult.OK)
                await ConnectAsync(false);
#endif
        };
        _reconnect.Click += async (_, _) => await ConnectAsync(false);
        _restart.Click += async (_, _) => await ConnectAsync(true);
        _updates.Click += (_, _) => {
            if (!_busy && _updateSchema is int schema) _checkUpdates?.Invoke(schema);
        };
#if FMSC_LEVEL2
        // This fixture may reconnect, but must never launch the school installer.
        _repair.Visible = false;
#endif
        _repair.Click += async (_, _) => await RepairDependenciesAsync();
        _repair.Visible = false;
        _close.Click += (_, _) => { _host.DialogResult = DialogResult.Cancel; _host.Close(); };
        bool detailsExpanded = false;
        detailsLink.LinkClicked += (_, _) => {
            detailsExpanded = !detailsExpanded;
            root.SuspendLayout();
            detailsCard.Visible = detailsExpanded;
            root.RowStyles[4].Height = detailsExpanded ? AppScale.S(200) : 0;
            _host.ClientSize = new Size(AppMetrics.StartupDialogClientSize.Width,
                AppMetrics.StartupDialogClientSize.Height + (detailsExpanded ? AppScale.S(200) : 0));
            detailsLink.Text = detailsExpanded ? "Hide connection details" : "Show connection details";
            root.ResumeLayout(true);
            var fitted = WorkingAreaLayout.Fit(_host.Bounds, _host.MinimumSize, Screen.FromControl(_host).WorkingArea);
            _host.MinimumSize = fitted.MinimumSize;
            _host.Bounds = fitted.Bounds;
        };
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        foreach (var button in new[] { _reconnect, _restart, _repair, _updates, _close, _restoreSchool }) button.Enabled = !busy;
        _restart.Enabled = !busy && _canRestart;
        _repair.Enabled = !busy && _canRepair;
        _progress.Visible = busy;
        UseWaitCursor = busy;
    }

    internal async Task ConnectAsync(bool restart)
    {
        if (_busy) return;
        SetBusy(true);
        _message.Text = restart ? "Restarting the connection. Your database records will be kept." : "Reconnecting to your Database…";
        _status.Text = restart ? "Restarting connection………" : "Checking connection………";
        _details.Text = ConnectionDetails(_status.Text);
        try
        {
            var result = await Task.Run(() => _connect(restart));
            if (IsDisposed) return;
            if (result.Connected)
            {
                SetUpdateAction(null, false);
                _canRestart = true;
                _canRepair = false;
                _repair.Visible = false;
                _restoreSchool.Visible = false;
                _message.Text = "Your database is connected and ready.";
                _status.Text = "Connection Successful";
                _details.Text = ConnectionDetails(string.Empty);
                SetBusy(false);
                _connected?.Invoke(this);
            }
            else ShowFailure(result.Message, result.Failure);
        }
        catch (Exception ex)
        {
            AppExceptionHandler.LogNonFatal(ex, "Database connection recovery");
            if (!IsDisposed) ShowFailure(ex is DatabaseRecoveryException recovery ? recovery.Message : DbMessage.UserMessage(ex),
                ex);
        }
        finally { if (!IsDisposed) SetBusy(false); }
        // Either version mismatch needs the existing updater, not a connection
        // restart. Open only after releasing Busy; never install automatically.
        if (!IsDisposed && _updateSchema is int schema) _checkUpdates?.Invoke(schema);
    }

    private static (bool Connected, string Message, Exception? Failure) CheckConnection(bool restart)
    {
        DatabaseHelper.RefreshDatabaseConnection(restart);
        bool connected = DatabaseHelper.TestConnection(out string message, out var failure);
        return (connected, message, failure);
    }

    private void ShowFailure(string message, Exception? failure = null)
    {
        int? mismatchedSchema = null;
        bool databaseNewer = false;
        string requiredVersion = DatabaseHelper.RequiredDatabaseSchemaVersion;
        for (Exception? current = failure; current is not null; current = current.InnerException)
            if (current is DatabaseCompatibilityException compatibility &&
                (compatibility.Failure.IsDatabaseOlder || compatibility.Failure.IsDatabaseNewer) &&
                int.TryParse(compatibility.Failure.FoundVersion, out int schema) && schema >= 0)
            {
                mismatchedSchema = schema;
                databaseNewer = compatibility.Failure.IsDatabaseNewer;
                requiredVersion = compatibility.Failure.RequiredVersion;
            }
        SetUpdateAction(mismatchedSchema, databaseNewer);
        var guidance = DatabaseConnectionGuidance.Create(message, failure,
#if FMSC_LEVEL2
            File.Exists(Path.Combine(Level2Target.Root,"database","fixture.mdf")));
#else
            File.Exists(DatabaseHelper.DatabaseFile));
#endif
        _canRestart = guidance.CanRestart;
        _canRepair =
#if FMSC_LEVEL2
            false;
#else
            guidance.CanRepair;
#endif
        _repair.Visible = _canRepair;
        // Only offer new-school recovery when there is no installed MDF and
        // no schema mismatch. The recovery service independently refuses an
        // attached/foreign database even if this UI check is stale.
#if !FMSC_LEVEL2
        _restoreSchool.Visible = !File.Exists(DatabaseHelper.DatabaseFile) &&
            !_updateSchema.HasValue;
#endif
        _status.Text = guidance.Status;
        _message.Text = guidance.Message;
#if !FMSC_LEVEL2
        if (_updateSchema.HasValue && _checkUpdates is not null)
            _message.Text = databaseNewer
                ? $"Your database is newer than this application.\r\n\r\n" +
                  $"Database schema version: {_updateSchema}. Required by this application: {requiredVersion}.\r\n\r\n" +
                  "Choose Update Application to check for a published compatible release. " +
                  "If one is available, download it and choose Install Update. " +
                  "If none is published, contact your administrator for a compatible application release. Do not downgrade or replace your school database."
                : $"Your database is connected, but it needs an update before you can sign in.\r\n\r\n" +
                  $"Database schema version: {_updateSchema}. Required by this application: {requiredVersion}.\r\n\r\n" +
                  "Connect to the internet and choose Open Updates to check whether a newer approved release is available. " +
                  "If one is offered, download it and choose Install Update; FMSC Setup will back up your school data before applying the required migrations. " +
                  "If no newer release is published, ask your administrator to arrange the database update. Do not replace your school database.";
#endif
        _details.Text = ConnectionDetails(message + (failure is null ? "" : "\r\n\r\n" + failure));
    }

    private void SetUpdateAction(int? mismatchedSchema, bool databaseNewer)
    {
#if FMSC_LEVEL2
        mismatchedSchema = null;
#endif
        _updateSchema = mismatchedSchema;
        bool offerUpdates = mismatchedSchema.HasValue && _checkUpdates is not null;
        _updates.Text = databaseNewer ? "Update Application" : "Open Updates";
        _updates.Width = TextRenderer.MeasureText(_updates.Text, _updates.Font).Width + AppScale.S(36);
        _updates.Visible = offerUpdates;
        _reconnect.Visible = _restart.Visible = !offerUpdates;
        _host.AcceptButton = AcceptAction;
    }

    private async Task RepairDependenciesAsync()
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            var progress = new Progress<string>(message => { if (!IsDisposed) { _status.Text = "Repairing SQL dependencies..."; _message.Text = message; } });
            int result = await SqlDependencyRepair.RepairAsync(progress);
            if (IsDisposed) return;
            _details.Text = ConnectionDetails($"SQL dependency repair returned code {result}.");
            if (result == 3010)
            {
                _canRestart = false;
                _canRepair = false;
                _repair.Visible = false;
                _status.Text = "Windows restart required";
                _message.Text = "A SQL prerequisite was installed or repaired. Restart Windows, then open FMSC again to finish SQL repair.";
            }
            else if (result == 0)
            { SetBusy(false); await ConnectAsync(false); }
            else
            { _status.Text = "SQL repair did not complete"; _message.Text = $"SQL dependency repair returned code {result}. Database has not been replaced. Try again or contact your administrator."; }
        }
        catch (Exception ex)
        {
            AppExceptionHandler.LogNonFatal(ex, "SQL dependency download and repair");
            if (!IsDisposed)
            {
                _status.Text = "SQL repair did not complete";
                _message.Text = ex.Message;
                _details.Text = ConnectionDetails(ex.ToString());
            }
        }
        finally { if (!IsDisposed) SetBusy(false); }
    }

    private static string ConnectionDetails(string problem)
    {
#if FMSC_LEVEL2
        return $"{problem}\r\n\r\nFictional Level 2 database: {Level2Target.Catalog}\r\nConnection: {Level2Target.Endpoint}\r\nPrivate data: {Path.Combine(Level2Target.Root, "database", "fixture.mdf")}";
#else
        string endpoint = LocalDatabaseEngine.CurrentEndpoint ?? "Unavailable";
        return $"{problem}\r\n\r\nRequired database schema version: {DatabaseHelper.RequiredDatabaseSchemaVersion}\r\nSchool data: {DatabaseHelper.DatabaseFile}\r\nConnection: {endpoint}\r\nDiagnostic log: {AppExceptionHandler.LogPath}";
#endif
    }

}


