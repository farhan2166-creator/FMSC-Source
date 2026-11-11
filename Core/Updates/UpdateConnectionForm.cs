using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Diagnostics;
using FMSC_SMS.Core;

namespace FMSC_SMS.Core.Updates;

/// <summary>
/// School-facing manual private Git update checking and isolated download.
/// No installation or SQL operation occurs here, or background check at startup.
/// A school receives authorization only after the repository owner adds this
/// computer's public key as a READ-ONLY deploy key in FMSC-Updates.
/// </summary>
internal sealed class UpdateConnectionForm : ApplicationWindowForm
{
    private readonly TextBox _publicKey;
    private readonly Label _status;
    private readonly ModernButton _generate;
    private readonly ModernButton _copy;
    private readonly ModernButton _verify;
    private readonly ModernButton _check;
    private readonly ModernButton _download;
    private readonly ModernButton _install;
    internal event Func<PreparedUpdate, bool>? InstallRequested;
    private bool _installRequested;
    private readonly Label _latestVersion;
    private readonly Label _downloadDetails;
    private readonly Label _accessStatus;
    private readonly Panel _authorizationPanel;
    private readonly ProgressBar _progress;
    private readonly Label _transferStats;
    private readonly System.Windows.Forms.Timer _transferTimer = new() { Interval = 500 };
    private readonly Stopwatch _downloadWatch = new();
    private UpdateDownloadProgress? _lastTransfer;
    private bool _showTransferStats;
    private int _downloadGeneration;
    private readonly Func<(bool Enrolled, string PublicKey)> _authorization;
    private readonly Func<string> _generateEnrollment;
    private readonly Func<CancellationToken, Task> _verifyAuthorization;
    private readonly bool _autoVerifyOnOpen;
    private bool _hasKey;
    private bool _hasAuthorization;
    private bool _verifyingAuthorization;
    private PreparedUpdate? _prepared;
    private VerifiedUpdate? _release;
    private UpdateDownloadChoice _method;
    private readonly int _installedSchema;
    private readonly Func<bool> _networkAvailable;
    private static string InstalledVersion => typeof(UpdateConnectionForm).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().First().InformationalVersion.Split('+')[0];
    private readonly CancellationTokenSource _lifetime = new();

    internal UpdateConnectionForm(Func<(bool Enrolled, string PublicKey)>? authorization = null, int? installedSchema = null,
        Func<bool>? networkAvailable = null, Func<CancellationToken, Task>? verifyAuthorization = null,
        Func<string>? generateEnrollment = null)
    {
        _networkAvailable = networkAvailable ?? System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable;
        _installedSchema = installedSchema ?? int.Parse(DatabaseHelper.RequiredDatabaseSchemaVersion);
        _authorization = authorization ?? (() => (UpdateDeviceKey.IsEnrolled,
            UpdateDeviceKey.IsEnrolled ? UpdateDeviceKey.GetPublicKey() : ""));
        _autoVerifyOnOpen = authorization is null;
        _verifyAuthorization = verifyAuthorization ?? VerifyLiveAuthorizationAsync;
        _generateEnrollment = generateEnrollment ?? UpdateDeviceKey.GenerateEnrollment;
        Text = "FMSC — Check for Updates";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = AppMetrics.StartupDialogClientSize;
        Font = AppTheme.NormalFont;
        BackColor = AppTheme.PageBackground;
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8,
            Padding = new Padding(AppScale.S(20)), BackColor = AppTheme.PageBackground };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int row = 0; row < 8; row++)
            content.RowStyles.Add(new RowStyle(row == 4 ? SizeType.Percent : SizeType.AutoSize, row == 4 ? 100 : 0));
        var heading = new Label { Text = "Check for Updates", Font = AppTheme.HeadingFont,
            AutoSize = true, Dock = DockStyle.Top, TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = AppTheme.Text, Margin = new Padding(0, 0, 0, AppScale.S(12)) };
        var summary = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2,
            Padding = new Padding(AppScale.S(12)), BackColor = AppTheme.Surface, Margin = new Padding(0, 0, 0, AppScale.S(12)) };
        summary.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        summary.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        summary.Controls.Add(new Label { Text = "Installed version", AutoSize = true, Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter, ForeColor = AppTheme.MutedText }, 0, 0);
        summary.Controls.Add(new Label { Text = "Latest published version", AutoSize = true, Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter, ForeColor = AppTheme.MutedText }, 1, 0);
        summary.Controls.Add(new Label { Text = InstalledVersion, AutoSize = true, Font = AppTheme.BodyBoldFont,
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = AppTheme.Text, Margin = new Padding(3, 4, 3, 10) }, 0, 1);
        _latestVersion = new Label { Text = "Not checked", AutoSize = true, Font = AppTheme.BodyBoldFont,
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = AppTheme.Text, Margin = new Padding(3, 4, 3, 10) };
        summary.Controls.Add(_latestVersion, 1, 1);
        _downloadDetails = new Label { Text = "Check for an approved update when you are ready.", AutoSize = true,
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = AppTheme.MutedText };
        summary.Controls.Add(_downloadDetails, 0, 2); summary.SetColumnSpan(_downloadDetails, 2);
        var access = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false, Margin = Padding.Empty };
        var accessButton = Button("Computer access", 150, "Secondary");
        _accessStatus = new Label { AutoSize = true, ForeColor = AppTheme.MutedText, Margin = new Padding(8, 10, 0, 0) };
        access.Controls.AddRange(new Control[] { accessButton, _accessStatus });
        _authorizationPanel = new Panel { Dock = DockStyle.Top, Height = AppScale.S(175), Margin = new Padding(0, 8, 0, 8) };
        var enrollment = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        enrollment.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.S(48)));
        enrollment.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        enrollment.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        enrollment.Controls.Add(new Label { Text = "Generate a key and send its public half to the software owner. " +
            "The owner authorizes this computer for read-only access. No school GitHub account is needed.",
            Dock = DockStyle.Fill, ForeColor = AppTheme.MutedText }, 0, 0);
        _publicKey = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true,
            ScrollBars = ScrollBars.Vertical, WordWrap = true, Font = AppTheme.SmallFont,
            BackColor = AppTheme.Surface, ForeColor = AppTheme.Text };
        enrollment.Controls.Add(_publicKey, 0, 1);
        var keyActions = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false };
        _generate = Button("Generate key", 140, "Secondary");
        _copy = Button("Copy public key", 155, "Secondary");
        _verify = Button("Verify access", 140, "Secondary");
        keyActions.Controls.AddRange(new Control[] { _generate, _copy, _verify });
        enrollment.Controls.Add(CenteredRow(keyActions), 0, 2);
        _authorizationPanel.Controls.Add(enrollment);
        accessButton.Click += (_, _) => {
            // A pending key must remain visible for the owner to approve. Only
            // verified computers may collapse their optional key details.
            if (!_hasAuthorization) return;
            _authorizationPanel.Visible = !_authorizationPanel.Visible;
            SetContentSize(_authorizationPanel.Visible);
        };
        _status = new Label { AutoSize = false, Dock = DockStyle.Fill, ForeColor = AppTheme.Text,
            Margin = new Padding(0, 12, 0, 8), TextAlign = ContentAlignment.MiddleCenter };
        _progress = new ProgressBar { Dock = DockStyle.Top, Height = AppScale.S(14), Style = ProgressBarStyle.Marquee,
            Visible = false, Margin = new Padding(0, 0, 0, 12) };
        _transferStats = new Label { Dock = DockStyle.Top, Height = AppScale.S(48), TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = AppTheme.MutedText, Visible = false, Margin = new Padding(0, 0, 0, AppScale.S(8)) };
        _transferTimer.Tick += (_, _) => RenderTransferStats();
        _check = Button("Check for Updates", 175, "Primary");
        _download = Button("Download Update", 170, "Primary"); _download.Enabled = false;
        _install = Button("Install Update", 150, "Primary"); _install.Enabled = false;
        _install.Click += (_, _) => {
            if (_prepared is null || _installRequested) return;
            _installRequested = true; _install.Enabled = false;
            try { InstallRequested?.Invoke(_prepared); }
            finally {
                if (!IsDisposed && !Disposing) { _installRequested = false; _install.Enabled = _prepared is not null; }
            }
        };
        var later = Button("Remind Me Later", 160, "Secondary");
        var close = Button("Close", 100, "Secondary");
        // This is a modeless window: DialogResult alone never closes it.
        later.Click += (_, _) => Close();
        close.Click += (_, _) => Close();
        _generate.Click += (_, _) => GenerateKey();
        _verify.Click += async (_, _) => await VerifyAccessAsync();
        _copy.Click += (_, _) => {
            if (string.IsNullOrWhiteSpace(_publicKey.Text)) return;
            try { Clipboard.SetText(_publicKey.Text); _status.Text = "Public key copied."; }
            catch (ExternalException) { _status.Text = "Clipboard unavailable. Select and copy the public key manually."; }
        };
        _check.Click += async (_, _) => await CheckNowAsync();
        _download.Click += async (_, _) => await DownloadAsync();
        var actions = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Margin = Padding.Empty };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var updateActions = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false, Margin = Padding.Empty };
        // Only the action relevant to the current state is shown in this slot.
        updateActions.Controls.AddRange(new Control[] { _check, _download, _install });
        _download.Visible = _install.Visible = false;
        var dismissActions = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false, Margin = Padding.Empty };
        dismissActions.Controls.AddRange(new Control[] { later, close });
        actions.Controls.Add(CenteredRow(updateActions), 0, 0);
        var dismissRow = CenteredRow(dismissActions);
        dismissRow.Margin = new Padding(0, AppScale.S(8), 0, 0);
        actions.Controls.Add(dismissRow, 0, 1);
        content.Controls.Add(heading, 0, 0); content.Controls.Add(summary, 0, 1);
        content.Controls.Add(CenteredRow(access), 0, 2); content.Controls.Add(_authorizationPanel, 0, 3);
        content.Controls.Add(_status, 0, 4); content.Controls.Add(_progress, 0, 5);
        content.Controls.Add(_transferStats, 0, 6); content.Controls.Add(actions, 0, 7);
        Controls.Add(content);
        AcceptButton = _check; CancelButton = close;
        FormClosed += (_, _) => { _transferTimer.Stop(); _lifetime.Cancel(); };
        Disposed += (_, _) => { _lifetime.Cancel(); _transferTimer.Dispose(); };
        RefreshAuthorization();
    }

    // Keep the download option completely out of the layout until checking the
    // already-prepared package has finished. Enabled=false alone still paints
    // a disabled button for a moment.
    private void SetDownloadActionVisible(bool available)
    {
        _download.Enabled = _download.Visible = available;
    }

    private static ModernButton Button(string text, int width, string role) => new() {
        Text = text, ThemeRole = role, Width = AppScale.S(width), Height = AppMetrics.ButtonHeight,
        Margin = new Padding(AppScale.S(4), 0, AppScale.S(4), 0) };

    private static TableLayoutPanel CenteredRow(Control content)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Margin = Padding.Empty };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        content.Anchor = AnchorStyles.None;
        row.Controls.Add(content, 1, 0);
        return row;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        CenterOnOwnerScreen();
        // Recheck an existing key when the update dialog opens, not on app
        // startup. Local key existence is NOT proof of GitHub approval.
        if (_autoVerifyOnOpen && _hasKey)
            BeginInvoke(new Action(async () => await VerifyAccessAsync()));
    }

    private void CenterOnOwnerScreen()
    {
        if (!Visible || !TopLevel) return;
        var area = Screen.FromControl(Owner ?? this).WorkingArea;
        Location = new Point(area.Left + Math.Max(0, (area.Width - Width) / 2),
            area.Top + Math.Max(0, (area.Height - Height) / 2));
    }

    private void SetContentSize(bool expanded)
    {
        // Match login at rest; grow only for computer authorization or transfer details.
        var size = new Size(AppMetrics.StartupDialogClientSize.Width,
            Math.Max(AppMetrics.StartupDialogClientSize.Height,
                AppScale.S((expanded ? 580 : 405) + (_showTransferStats ? 65 : 0))));
        // The responsive viewport must discard the previous expanded minimum
        // before a compact authorization view can shrink without scrollbars.
        SetResponsiveContentMinimum(size);
        if (!TopLevel && TopLevelControl is Form host && host != this)
        {
            host.ClientSize = size;
            if (host is ApplicationWindowForm window)
                window.FitToWorkingArea(Screen.FromControl(host).WorkingArea, host.DeviceDpi);
        }
        else
            ClientSize = size;
        CenterOnOwnerScreen();
    }

    internal void ApplyDownloadProgress(UpdateDownloadProgress state)
    {
        if (IsDisposed || Disposing) return;
        _lastTransfer = state;
        bool resize = !_showTransferStats;
        _showTransferStats = _transferStats.Visible = true;
        if (resize) SetContentSize(_authorizationPanel.Visible);
        _progress.Visible = true;
        _progress.Style = ProgressBarStyle.Continuous;
        _progress.Value = state.Percent;
        string stage = state.Phase switch {
            UpdateDownloadPhase.Verifying or UpdateDownloadPhase.Preparing => "Verifying",
            UpdateDownloadPhase.Ready => "Ready to Install",
            UpdateDownloadPhase.Failed => "Download stopped",
            _ => "Downloading"
        };
        _status.Text = stage + Environment.NewLine + state.Message;
        _downloadDetails.Text = state.Method + " · " + SizeText(state.TotalBytes) +
            (state.ReusedBytes > 0 ? " · " + SizeText(state.ReusedBytes) + " reused" : "");
        RenderTransferStats();
    }

    private void RenderTransferStats()
    {
        if (_lastTransfer is not { } state || IsDisposed || Disposing) return;
        TimeSpan elapsed = _downloadWatch.IsRunning ? _downloadWatch.Elapsed : state.Elapsed;
        string speed = state.BytesPerSecond > 0 ? SizeText(state.BytesPerSecond) + "/s" : "—";
        string remaining = state.TimeRemaining is { } eta ? "~" + DurationText(eta) :
            state.Phase == UpdateDownloadPhase.Downloading ? "Calculating…" : "—";
        _transferStats.Text = $"Download: {state.Percent}% · Downloaded: {SizeText(state.ReceivedBytes)} / {SizeText(state.TotalBytes)} · " +
            $"{SizeText(state.BytesRemaining)} remaining\nSpeed: {speed} · Elapsed: {DurationText(elapsed)} · Time left: {remaining}";
    }

    private static string SizeText(double bytes) => bytes >= 1048576 ? $"{bytes / 1048576:0.00} MB" :
        bytes >= 1024 ? $"{bytes / 1024:0.0} KB" : $"{bytes:0} B";
    private static string DurationText(TimeSpan span) => span.TotalHours >= 1 ?
        $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}" : $"{(int)span.TotalMinutes:00}:{span.Seconds:00}";
    private void RefreshAuthorization()
    {
        try
        {
            var state = _authorization();
            _hasKey = state.Enrolled;
            _hasAuthorization &= _hasKey; // Only a successful live Git check can set this true.
            _generate.Enabled = _generate.Visible = !_hasKey;
            _copy.Enabled = _hasKey;
            _verify.Visible = _hasKey && !_hasAuthorization;
            _verify.Enabled = _hasKey && !_hasAuthorization && !_verifyingAuthorization;
            _check.Enabled = _hasAuthorization;
            _publicKey.Text = state.PublicKey;
            _authorizationPanel.Visible = !_hasAuthorization;
            SetContentSize(!_hasAuthorization);
            _accessStatus.Text = _hasAuthorization ? "Computer authorized" :
                _hasKey ? "Approval not verified" : "Authorization required";
            _status.Text = _hasAuthorization ? "Select Check for Updates to contact the private update repository." :
                _hasKey ? "Send this public key to the software owner. After approval, select Verify access." :
                "Generate a key and have the software owner authorize this computer.";
            int applicationSchema = int.Parse(DatabaseHelper.RequiredDatabaseSchemaVersion);
            if (_installedSchema < applicationSchema)
                _status.Text = "Your school database needs an update before sign-in.\r\n\r\n" +
                    (_hasAuthorization ? "Choose Check for Updates to see whether a newer approved release is published. " +
                        "If an update is available, download it and choose Install Update; FMSC Setup will back up your school data before applying migrations. " +
                        "If none is available, contact your administrator for the required database update." :
                        "Ask the administrator to authorize this computer before checking for a published update. No database changes have been made.");
            else if (_installedSchema > applicationSchema)
                _status.Text = "Your school database uses a newer schema than this application.\r\n\r\n" +
                    (_hasAuthorization ? "Choose Check for Updates to look for a published application release compatible with your database. " +
                        "If none is available, contact your administrator. Do not downgrade or replace your school data." :
                        "Ask the administrator to authorize this computer before checking for a compatible application. Do not downgrade the database.");
        }
        catch (Exception error) when (error is CryptographicException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _publicKey.Text = "";
            _copy.Enabled = false;
            _check.Enabled = false;
            _verify.Enabled = false;
            _hasAuthorization = false;
            _status.Text = "This Windows user's protected authorization could not be read. " +
                "Contact the software owner; no key was replaced. " + error.GetType().Name;
        }
    }

    private void GenerateKey()
    {
        try
        {
            _ = _generateEnrollment();
            RefreshAuthorization();
        }
        catch (Exception error) when (error is CryptographicException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _status.Text = "Unable to prepare this computer's update authorization: " + error.Message;
        }
    }

    private static async Task VerifyLiveAuthorizationAsync(CancellationToken token)
    {
        await using var session = await UpdateGitSession.OpenAsync(token);
    }

    private async Task VerifyAccessAsync()
    {
        if (!_hasKey || _hasAuthorization || _verifyingAuthorization) return;
        _verifyingAuthorization = true;
        _verify.Enabled = false;
        _status.Text = "Verifying this computer's read-only access with the software owner's repository...";
        try
        {
            if (!_networkAvailable())
            {
                _status.Text = "Unable to verify access while offline. Connect to the internet and select Verify access again.";
                return;
            }
            await _verifyAuthorization(_lifetime.Token);
            if (IsDisposed || Disposing) return;
            _hasAuthorization = true;
            RefreshAuthorization();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is CryptographicException or IOException or UnauthorizedAccessException
            or InvalidDataException or InvalidOperationException or UpdateGitCommandException
            or System.ComponentModel.Win32Exception)
        {
            if (!IsDisposed && !Disposing)
                _status.Text = "Computer access has not been verified. The key may still need the owner's approval, " +
                    "or the connection may be unavailable. Select Verify access to retry. " + error.Message;
        }
        finally
        {
            _verifyingAuthorization = false;
            if (!IsDisposed && !Disposing) _verify.Enabled = _hasKey && !_hasAuthorization;
        }
    }

    private async Task CheckNowAsync()
    {
        // Disabled UI is not an authorization boundary: reject direct/queued
        // check attempts until a live read-only Git connection has succeeded.
        if (!_hasAuthorization)
        {
            _check.Enabled = false;
            _status.Text = _hasKey ? "Verify computer access before checking for updates." :
                "Generate a key and obtain the owner's approval before checking for updates.";
            return;
        }
        _showTransferStats = _transferStats.Visible = false;
        _lastTransfer = null;
        SetContentSize(_authorizationPanel.Visible);
        _check.Enabled = false;
        _generate.Enabled = false;
        SetDownloadActionVisible(false);
        _download.Text = "Download Update";
        _release = null;
        _prepared = null;
        _install.Enabled = _install.Visible = false;
        _status.Text = "Checking the private update repository without interrupting FMSC...";
        _progress.Visible = true;
        _progress.Style = ProgressBarStyle.Marquee;
        try
        {
            if (!_networkAvailable())
            {
                _status.Text = "This computer is offline. Connect to the internet, then choose Check for Updates again. Your school records have not been changed.";
                return;
            }
            await using var session = await UpdateGitSession.OpenAsync(_lifetime.Token);
            if (!await session.HasStableFeedAsync(_lifetime.Token))
            {
                _status.Text = "No approved FMSC release is published on this update channel yet. " +
                    "Unpublished development changes are not available to install. Ask your administrator for a compatible published release.";
                _accessStatus.Text = "Computer authorized";
                _latestVersion.Text = "No release published";
                return;
            }
            var release = await UpdateReleasePreparer.ReadLatestAsync(session, _lifetime.Token);
            if (IsDisposed || Disposing) return;
            if (release is null) return;
            // A newer database can only be opened by a release supporting at least
            // its schema. Do not offer a package that would imply downgrading SQL.
            if (release.Manifest.RequiredDatabaseSchema < _installedSchema)
            {
                _latestVersion.Text = release.Manifest.Version;
                _accessStatus.Text = "Computer authorized";
                _status.Text = $"No compatible application update is published. Your database uses schema {_installedSchema}, " +
                    $"but the latest published application (version {release.Manifest.Version}) supports schema {release.Manifest.RequiredDatabaseSchema}. " +
                    "Ask your administrator for a release that supports your database. Do not downgrade or replace your school data.";
                return;
            }
            _method = await Task.Run(() => UpdateReleasePreparer.Choose(release, InstalledVersion,
                _installedSchema, AppContext.BaseDirectory), _lifetime.Token);
            if (IsDisposed || Disposing) return;
            _release = release;
            _latestVersion.Text = release.Manifest.Version;
            _accessStatus.Text = "Computer authorized";
            var archive = _method == UpdateDownloadChoice.Incremental ? release.Manifest.Incremental! : release.Manifest.Full;
            _status.Text = "Latest published version: " + release.Manifest.Version + "\n" +
                (_method == UpdateDownloadChoice.NotNewer
                    ? _installedSchema == int.Parse(DatabaseHelper.RequiredDatabaseSchemaVersion)
                        ? "No newer approved application release is available on this update channel. " +
                          "This checks published releases only; unpublished development changes are not included."
                        : "No newer approved application release is available, and the database schema does not match this application. " +
                          "Ask your administrator for a compatible release or the required database migration. Do not replace or downgrade the database."
                    :
                 _method == UpdateDownloadChoice.RequiresDatabaseInstaller ?
                 "Update available — Full package; database migration will run through the installer after FMSC closes. " +
                    (archive.Length / 1048576d).ToString("N1") + " MB." :
                 "Update available — " + _method + ", approximately " + (archive.Length / 1048576d).ToString("N1") + " MB.");
            if (_method == UpdateDownloadChoice.RequiresDatabaseInstaller) _method = UpdateDownloadChoice.Full;
            bool canDownload = _method is UpdateDownloadChoice.Full or UpdateDownloadChoice.Incremental;
            // An authorized release is not yet a final download decision:
            // first revalidate any saved stage before displaying either action.
            SetDownloadActionVisible(false);
            _install.Visible = false;
            _downloadDetails.Text = canDownload ? _method + " · approximately " +
                (archive.Length / 1048576d).ToString("N1") + " MB" : "No newer published package available";
            if (canDownload)
            {
                string checkedStatus = _status.Text;
                _status.Text = "Checking previously prepared update against the approved release…";
                // The manual check already authenticated to the current private
                // Git feed. Reuse an old stage only if its exact manifest matches
                // that feed AND every staged file passes the installer handoff.
                var saved = await Task.Run(() => UpdatePreparedResume.FindVerified(
                    release, _method, UpdateDeviceKey.StateDirectory), _lifetime.Token);
                if (IsDisposed || Disposing) return;
                if (saved is not null)
                {
                    _prepared = saved;
                    _download.Enabled = _download.Visible = false;
                    _install.Enabled = _install.Visible = true;
                    _downloadDetails.Text = saved.Method + " · downloaded and verified (reused)";
                    _status.Text = "Ready to Install" + Environment.NewLine +
                        "Saved update rechecked against the private repository and all application files. " +
                        "The update is not installed yet.";
                }
                else
                {
                    _status.Text = checkedStatus;
                    SetDownloadActionVisible(true);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Closing this window cancels the network check. No install is running.
        }
        catch (Exception error) when (error is CryptographicException or IOException or UnauthorizedAccessException
                                         or InvalidDataException or JsonException or UpdateGitCommandException
                                         or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            if (!IsDisposed && !Disposing)
                _status.Text = "Unable to check updates safely: " + error.Message +
                    "\r\n\r\nCheck your internet connection, then choose Check for Updates to try again. " +
                    "If the internet is working, ask the administrator to check this computer's update access.";
        }
        finally
        {
            if (!IsDisposed && !Disposing)
            { _check.Enabled = _hasAuthorization && _prepared is null; _progress.Visible = false; }
        }
    }

    private async Task DownloadAsync()
    {
        if (_release is null) return;
        var token = _lifetime.Token;
        int generation = ++_downloadGeneration;
        _prepared = null;
        _install.Visible = _install.Enabled = false;
        _download.Enabled = _check.Enabled = false;
        _downloadWatch.Restart();
        _showTransferStats = _transferStats.Visible = true;
        SetContentSize(_authorizationPanel.Visible);
        var archive = _method == UpdateDownloadChoice.Incremental ? _release.Manifest.Incremental! : _release.Manifest.Full;
        ApplyDownloadProgress(new(UpdateDownloadPhase.Connecting, 0, archive.Length, 0, TimeSpan.Zero, null,
            0, archive.Parts.Count, 1, 0, "Connecting to the update repository…", _method));
        _transferTimer.Start();
        IProgress<UpdateDownloadProgress> progress = new Progress<UpdateDownloadProgress>(state => {
            if (generation == _downloadGeneration) ApplyDownloadProgress(state);
        });
        try
        {
            var release = _release;
            string root = Path.Combine(UpdateDeviceKey.StateDirectory, "prepared-" + Guid.NewGuid().ToString("N"));
            var prepared = await Task.Run(async () =>
            {
                int connectionAttempt = 0;
                Task<UpdateGitSession> Connect()
                {
                    progress.Report(new(UpdateDownloadPhase.Connecting, 0, archive.Length, 0, _downloadWatch.Elapsed, null,
                        0, archive.Parts.Count, ++connectionAttempt, 0,
                        $"Connecting to the update repository (attempt {connectionAttempt}/{UpdateRetry.MaximumAttempts})…", _method));
                    return UpdateGitSession.OpenAsync(token);
                }
                await using var session = await UpdateRetry.RunAsync(Connect, token,
                    (attempt, wait) => progress.Report(new(UpdateDownloadPhase.Retrying, 0, archive.Length, 0,
                        _downloadWatch.Elapsed, null, 0, archive.Parts.Count, attempt, 0,
                        $"Connecting failed. Retrying in {wait.TotalSeconds:0}s (attempt {attempt}/{UpdateRetry.MaximumAttempts})…", _method)));
                return await UpdateReleasePreparer.PrepareAsync(release, session, _method,
                    AppContext.BaseDirectory, root, token, progress, Path.Combine(UpdateDeviceKey.StateDirectory, "download-cache"));
            }, token);
            await Task.Run(() => UpdateInstallerHandoff.Validate(prepared), token);
            if (!IsDisposed && !Disposing)
            {
                _downloadWatch.Stop();
                ++_downloadGeneration;
                var completedArchive = prepared.Method == UpdateDownloadChoice.Incremental ? release.Manifest.Incremental! : release.Manifest.Full;
                if (_lastTransfer is { } last)
                    ApplyDownloadProgress(last with { Phase = UpdateDownloadPhase.Ready,
                        TotalBytes = completedArchive.Length, ReceivedBytes = completedArchive.Length,
                        Method = prepared.Method, VerifiedBytes = completedArchive.Length,
                        Elapsed = _downloadWatch.Elapsed, TimeRemaining = null, Message = "Download complete. Update verified and ready to install." });
                _prepared = prepared;
                _install.Enabled = true;
                _install.Visible = true;
                _download.Visible = false;
                _downloadDetails.Text = prepared.Method + " · downloaded and verified";
                _status.Text = "Ready to Install" + Environment.NewLine +
                    "Downloaded and verified. The update is not installed yet. Choose Install Update when you are ready.";
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException
            or UpdateGitCommandException or System.ComponentModel.Win32Exception or CryptographicException)
        {
            if (!IsDisposed && !Disposing)
            {
                ++_downloadGeneration;
                if (_lastTransfer is { } last)
                    ApplyDownloadProgress(last with { Phase = UpdateDownloadPhase.Failed, ReceivedBytes = last.VerifiedBytes,
                        BytesPerSecond = 0, TimeRemaining = null, Elapsed = _downloadWatch.Elapsed,
                        Message = "Download stopped. Verified chunks are saved for retry." });
                _status.Text = "Update could not be prepared: " + error.Message + " Verified chunks are saved for retry.";
                _download.Text = "Retry Download";
                _download.Enabled = true;
            }
        }
        finally
        {
            _downloadWatch.Stop();
            if (!IsDisposed && !Disposing) { _transferTimer.Stop(); _check.Enabled = _hasAuthorization && _prepared is null; }
        }
    }
}
