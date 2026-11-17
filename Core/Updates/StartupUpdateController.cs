namespace FMSC_SMS.Core.Updates;

/// <summary>Allows a blocked startup to prepare an update without a database login.</summary>
internal sealed class StartupUpdateController
{
    private readonly Form _owner;
    private readonly Control _content;
    private UpdateConnectionForm? _activeDialog;
    private readonly Func<int, UpdateConnectionForm> _createDialog;
    private readonly Action<PreparedUpdate> _validate;
    private readonly Func<bool> _confirm;
    private readonly Action<string> _reportError;
    internal PreparedUpdate? PendingUpdate { get; private set; }

    internal StartupUpdateController(Form owner, Control content,
        Func<int, UpdateConnectionForm>? createDialog = null,
        Action<PreparedUpdate>? validate = null, Func<bool>? confirm = null, Action<string>? reportError = null)
    {
        _owner = owner;
        _content = content;
        _createDialog = createDialog ?? (schema => new UpdateConnectionForm(
            installedSchema: schema, autoCheckOnVerifiedAccess: true));
        _validate = validate ?? (prepared => {
            UpdateInstallerHandoff.EnsureInstalledLocation();
            UpdateInstallerHandoff.Validate(prepared);
        });
        _confirm = confirm ?? (() => ModernMessageDialog.Confirm(_owner,
            "Close FMSC and install the prepared update? FMSC Setup will back up your school data and apply the required database updates. " +
            "Windows will ask for permission to run the installer.", "Install FMSC Update"));
        _reportError = reportError ?? (message => ModernMessageDialog.ShowWarning(_owner,
            "The update could not be started: " + message, "FMSC Update"));
    }

    internal void Open(int schema)
    {
#if FMSC_LEVEL2
        throw new InvalidOperationException("Disposable test builds cannot activate school updates.");
#else
        if (_activeDialog is not null || PendingUpdate is not null) return;
        var dialog = _createDialog(schema);
        _activeDialog = dialog;
        var previous = _content.Controls.Cast<Control>().Where(control => control.Visible).ToArray();
        var previousBounds = _owner.Bounds;
        var previousTitle = _owner.Text;
        var previousAccept = _owner.AcceptButton;
        var previousCancel = _owner.CancelButton;
        var updateSize = dialog.ClientSize;
        dialog.TopLevel = false;
        dialog.FormBorderStyle = FormBorderStyle.None;
        dialog.Dock = DockStyle.Fill;
        dialog.InstallRequested += prepared => RequestInstallation(prepared, dialog);
        dialog.FormClosed += (_, _) => {
            _activeDialog = null;
            _content.Controls.Remove(dialog);
            if (_owner.IsDisposed || _owner.Disposing) return;
            if (PendingUpdate is not null)
            {
                // Program consumes the update after the login dialog exits.
                _owner.DialogResult = DialogResult.Cancel;
                _owner.Close();
                return;
            }
            _owner.Text = previousTitle;
            _owner.Bounds = previousBounds;
            _owner.AcceptButton = previousAccept;
            _owner.CancelButton = previousCancel;
            foreach (var control in previous) if (!control.IsDisposed) control.Visible = true;
            (previousAccept as Control)?.Focus();
        };
        foreach (var control in previous) control.Visible = false;
        _content.Controls.Add(dialog);
        _owner.Text = dialog.Text;
        _owner.ClientSize = updateSize;
        _owner.AcceptButton = dialog.AcceptButton;
        _owner.CancelButton = dialog.CancelButton;
        dialog.BringToFront();
        dialog.Show();
        if (_owner is ApplicationWindowForm window)
            window.FitToWorkingArea(Screen.FromControl(_owner).WorkingArea, _owner.DeviceDpi);
        (dialog.AcceptButton as Control)?.Focus();
#endif
    }

    internal bool RequestInstallation(PreparedUpdate prepared, Form dialog)
    {
        if (PendingUpdate is not null) return false;
        try
        {
            _validate(prepared);
            if (!_confirm()) return false;
            PendingUpdate = prepared;
            dialog.DialogResult = DialogResult.OK;
            dialog.Close();
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or
            InvalidOperationException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
        {
            _reportError(error.Message);
            return false;
        }
    }
}
