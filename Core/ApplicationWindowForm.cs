using System.Runtime.InteropServices;

namespace FMSC_SMS.Core;

/// <summary>Native resizing frame with a custom caption and monitor-safe maximize bounds.</summary>
public class ApplicationWindowForm : Form
{
    private ApplicationTitleBar? caption;
    private Icon? schoolIcon;
    private Size? requestedClientSize;
    private int geometryDpi = AppScale.Dpi;
    private Size? requestedMinimum;
    private int minimumDpi;
    private bool windowReady;
    private bool fittingWindow;
    private Rectangle lastWorkingArea;
    private ResponsiveViewport? viewport;
    private readonly ToolTip saveShortcutTip = new();
    private readonly PageActionShortcutController saveShortcutController;
    public ApplicationWindowForm()
    {
        // Shared metrics own geometry. Do not add a second Font/Dpi autoscale pass.
        AutoScaleMode = AutoScaleMode.None;
        saveShortcutController = new PageActionShortcutController(this, saveShortcutTip);
        KeyboardShortcutHints.Register(this);
    }
    internal PageActionShortcutController ShortcutController => saveShortcutController;
    internal void CaptureLayoutDpi() => geometryDpi = AppScale.Dpi;
    public override Size MinimumSize
    {
        get => base.MinimumSize;
        set
        {
            // Form's setter clamps to the current physical monitor. Retain the
            // requested size before that clamp so moving back can restore it.
            if (!fittingWindow)
            {
                requestedMinimum = value;
                minimumDpi = geometryDpi;
            }
            base.MinimumSize = value;
        }
    }
    protected void SetSaveShortcut(ModernButton button) => saveShortcutController.SetSaveButton(button);
    protected void SetResponsiveContentMinimum(Size requestedDialogClientSize)
    {
        if (viewport is null) return;
        // The custom caption occupies client space but is outside the scrolling content.
        viewport.SetContentMinimum(new Size(requestedDialogClientSize.Width,
            Math.Max(0, requestedDialogClientSize.Height - (caption?.Height ?? 0))));
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (PageActionShortcutController.ActiveContent(this).Controller.TryHandle(keyData))
            return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }
    protected override void SetClientSizeCore(int x, int y)
    {
        requestedClientSize = new Size(x, y);
        base.SetClientSizeCore(x, y);
        if (caption is not null && UsesCaption && WindowState == FormWindowState.Normal)
            Size = new Size(Width + x - ClientSize.Width, Height + y + caption.Height - ClientSize.Height);
    }
    private bool UsesCaption => TopLevel && FormBorderStyle != FormBorderStyle.None;
    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            if (UsesCaption) parameters.Style &= ~0x00C00000;
            return parameters;
        }
    }
    protected override void OnLoad(EventArgs e)
    {
        if (TopLevel) ApplyDpiGeometry(DeviceDpi, resizeWindow: true);
        if (UsesCaption && caption is null)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Assets", "FMSC_Logo_Circular.ico");
            if (File.Exists(path)) { schoolIcon = new Icon(path); Icon = schoolIcon; }
            caption = new ApplicationTitleBar(this);
            // Reclaim the native caption's space while preserving the requested
            // dialog content size. The overall window height matches a native caption.
            if (WindowState == FormWindowState.Normal)
            {
                Size requested = requestedClientSize ?? ClientSize;
                // Form's ClientSize setter still budgets a native caption even
                // though CreateParams removed it. Correct the outer size directly.
                Size = new Size(Width + requested.Width - ClientSize.Width,
                    Height + requested.Height + caption.Height - ClientSize.Height);
            }
            Controls.Add(caption);
        }
        UiHelper.ApplyThemeToForm(this);
        PrepareResponsiveContent();
        base.OnLoad(e);
        windowReady = true;
        FitWorkingArea();
    }
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        if (e.Cancel || !TopLevel) return;
        ApplyDpiGeometry(e.DeviceDpiNew, resizeWindow: false);
        AppMetrics.InvalidateScaleCache();
        UiHelper.RefreshControlsForScaleChange(this);
        FitWorkingArea();
    }
    protected override bool OnGetDpiScaledSize(int deviceDpiOld, int deviceDpiNew, ref Size desiredSize)
    {
        desiredSize = DpiLayout.ChangeDpi(Size, deviceDpiOld, deviceDpiNew);
        return true;
    }
    internal void ApplyDpiGeometry(int dpi, bool resizeWindow)
    {
        if (dpi == geometryDpi || dpi <= 0) return;
        int previous = geometryDpi;
        geometryDpi = dpi;
        SuspendLayout();
        try
        {
            var factor = new SizeF(dpi / (float)previous, dpi / (float)previous);
            viewport?.ChangeDpi(previous, dpi);
            // The custom caption already handles its own native DPI notification.
            foreach (Control child in Controls)
                if (!ReferenceEquals(child, caption)) child.Scale(factor);
            if (resizeWindow)
            {
                fittingWindow = true;
                try
                {
                    MinimumSize = DpiLayout.ChangeDpi(requestedMinimum ?? MinimumSize,
                        minimumDpi > 0 ? minimumDpi : previous, dpi);
                }
                finally { fittingWindow = false; }
                ClientSize = DpiLayout.ChangeDpi(ClientSize, previous, dpi);
            }
        }
        finally { ResumeLayout(true); }
    }
    private void PrepareResponsiveContent()
    {
        if (!TopLevel || this is FMSC_SMS.MainDashboardForm || viewport is not null) return;
        Control? root = Controls.Cast<Control>().FirstOrDefault(child => child.Dock == DockStyle.Fill && child != caption);
        // Reports, grids and existing scroll hosts already own their viewport.
        if (root is null || root is DataGridView || root is ListBox ||
            root is ScrollableControl { AutoScroll: true } ||
            root.GetType().Namespace?.StartsWith("Microsoft.Reporting", StringComparison.Ordinal) == true) return;
        Size minimum = root.Size;
        int rootIndex = Controls.GetChildIndex(root);
        Controls.Remove(root);
        viewport = new ResponsiveViewport(root, minimum);
        Controls.Add(viewport);
        Controls.SetChildIndex(viewport, rootIndex);
        // Top docking runs in reverse z-order: the caption must dock first.
        caption?.SendToBack();
    }
    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        if (windowReady && Screen.FromControl(this).WorkingArea != lastWorkingArea) FitWorkingArea();
    }
    protected override void OnShown(EventArgs e)
    {
#if FMSC_LEVEL2
        Text += " [Fictional Level 2]";
#endif
        base.OnShown(e);
        FitWorkingArea();
        if (this is not FMSC_SMS.MainDashboardForm)
            BeginInvoke(new Action(() => {
                if (!IsDisposed && !FormFlow.HasInputFocus(this))
                    FormFlow.ScheduleInitialFocus(this, this, () => Visible);
            }));
    }
    protected override bool ProcessTabKey(bool forward)
    {
        Control scope = PageActionShortcutController.ActiveContent(this).Root;
        if (scope != this && scope.ContainsFocus)
        {
            Control focused = scope;
            while (focused.Controls.Cast<Control>().FirstOrDefault(child => child.ContainsFocus) is { } child)
                focused = child;
            // Wrap within the open page before the shell can select its navigation controls.
            return scope.SelectNextControl(focused, forward, true, true, true);
        }
        return base.ProcessTabKey(forward);
    }
    protected override bool ProcessDialogKey(Keys keyData)
    {
        // Controls first handle Escape for their own open dropdown or edit.
        // A dialog without a Cancel button then closes through FormClosing,
        // so unsaved-change and in-progress-operation guards still run.
        if (keyData == Keys.Escape && TopLevel &&
            (Modal || Owner is not null && !ShowInTaskbar) && CancelButton is null)
        {
            Close();
            return true;
        }
        return base.ProcessDialogKey(keyData);
    }
    private void FitWorkingArea()
    {
        if (!TopLevel || fittingWindow || WindowState == FormWindowState.Minimized) return;
        Rectangle workingArea = Screen.FromControl(this).WorkingArea;
        FitToWorkingArea(workingArea, DeviceDpi);
    }
    internal void FitToWorkingArea(Rectangle workingArea, int dpi)
    {
        requestedMinimum ??= MinimumSize;
        if (minimumDpi == 0) minimumDpi = dpi;
        var minimum = DpiLayout.ChangeDpi(requestedMinimum.Value, minimumDpi, dpi);
        var fitted = WorkingAreaLayout.Fit(Bounds, minimum, workingArea, dpi);
        fittingWindow = true;
        try
        {
            lastWorkingArea = workingArea;
            MinimumSize = fitted.MinimumSize;
            if (WindowState == FormWindowState.Normal) Bounds = fitted.Bounds;
        }
        finally { fittingWindow = false; }
    }
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) { KeyboardShortcutHints.Unregister(this); schoolIcon?.Dispose(); schoolIcon = null; saveShortcutTip.Dispose(); }
    }
    protected override void WndProc(ref Message message)
    {
        if (UsesCaption && message.Msg == 0x86) // WM_NCACTIVATE
        {
            // Modal close activates the owner. Let Windows update activation
            // without painting a legacy caption over our client title bar.
            message.LParam = new IntPtr(-1);
            base.WndProc(ref message);
            caption?.Invalidate(true);
            return;
        }
        if (UsesCaption && message.Msg == 0x85 && IsZoomed(message.HWnd)) // WM_NCPAINT
        {
            message.Result = IntPtr.Zero;
            return;
        }
        // WinForms updates native styles when a modal report disables and
        // re-enables its owner. Enforce the custom caption on those updates,
        // not just during initial handle creation.
        if (UsesCaption && message.Msg == 0x7C && message.WParam.ToInt64() == -16
            && message.LParam != IntPtr.Zero) // WM_STYLECHANGING / GWL_STYLE
        {
            var styles = Marshal.PtrToStructure<WindowStyles>(message.LParam);
            styles.NewStyle &= ~0x00C00000;
            Marshal.StructureToPtr(styles, message.LParam, false);
        }
        // A maximized custom-caption window must use the entire work area as
        // its client surface. Keeping the native resize frame here leaves
        // colored strips along the sides and bottom. Normal windows retain
        // that frame so native resizing and shadows still work.
        if (UsesCaption && message.Msg == 0x83 && message.WParam != IntPtr.Zero
            && IsZoomed(message.HWnd)) // WM_NCCALCSIZE; native state updates before Form.WindowState
        {
            message.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref message);
        if (!UsesCaption || message.Msg != 0x24 || message.LParam == IntPtr.Zero) return; // WM_GETMINMAXINFO
        // Removing the native caption changes Windows' frame calculations.
        // Constrain the OUTER window to the work area, rather than allowing
        // its resize borders/content to spill past the screen or taskbar.
        var screen = Screen.FromHandle(Handle);
        var bounds = Marshal.PtrToStructure<MinMaxInfo>(message.LParam);
        bounds.MaxPosition = new Point(screen.WorkingArea.Left - screen.Bounds.Left,
            screen.WorkingArea.Top - screen.Bounds.Top);
        bounds.MaxSize = screen.WorkingArea.Size;
        Marshal.StructureToPtr(bounds, message.LParam, false);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point Reserved;
        public Size MaxSize;
        public Point MaxPosition;
        public Size MinTrackSize;
        public Size MaxTrackSize;
    }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr window);
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowStyles
    {
        public int OldStyle;
        public int NewStyle;
    }
}
