using System.Diagnostics.CodeAnalysis;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
namespace FMSC_SMS.Core;
public sealed class ModernDatePicker : Panel, IValidationTarget, IAccessibleInput
{
    private readonly InnerDatePicker _inner;
    private DateTimePickerFormat _format = DateTimePickerFormat.Custom;
    private string? _customFormat = "dd-MMM-yyyy";
    private ToolStripDropDown? _popup;
    private bool _invalid;
    private int _radius =
        Math.Max(1, AppTheme.ControlRadius - 2);
    private const int InsetLeft = 8;
    private const int InsetRight = 8;
    private bool _isHovered;
    private bool _lastChecked;
    /// <summary>Raised when an optional date is enabled or cleared, even if its date value is unchanged.</summary>
    public event EventHandler? CheckedChanged;
    public ModernDatePicker()
    {
        _inner = new InnerDatePicker {
            Format = DateTimePickerFormat.Custom,
            CustomFormat = "dd-MMM-yyyy",
            Font = AppTheme.NormalFont,
            ForeColor = AppTheme.Text,
            CalendarMonthBackground = AppTheme.SurfaceElevated
        };
        _lastChecked = _inner.Checked;
        _inner.OpenCalendar = OpenCalendar;
        _inner.StateChanged = NotifyDateStateChanged;
        _inner.ValueChanged += (_, _) => NotifyDateStateChanged();
        _inner.GotFocus += (_, _) => {
            OnGotFocus(EventArgs.Empty);
            Invalidate();
        };
        _inner.LostFocus += (_, _) => {
            OnLostFocus(EventArgs.Empty);
            Invalidate();
        };
        _inner.MouseEnter += (_, _) => {
            _isHovered = true;
            Invalidate();
        };
        _inner.MouseLeave += (_, _) => {
            _isHovered = false;
            Invalidate();
        };
        Controls.Add(_inner);
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);
        BackColor = AppTheme.SurfaceElevated;
        ForeColor = AppTheme.Text;
        Font = AppTheme.NormalFont;
        Height = AppMetrics.InputControlHeight;
        Reposition();
        AppTheme.ThemeChanged += OnThemeChanged;
    }
    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || Disposing)
            return;
        if(_popup is {IsDisposed:false})_popup.DropShadowEnabled=!AppTheme.UseSimpleRendering;
        RefreshTheme();
    }
    public void RefreshTheme()
    {
        if (IsDisposed || Disposing)
            return;
        SuspendLayout();
        try
        {
            Font = UiDpi.ForControl(this, AppTheme.NormalFont);
            ForeColor = AppTheme.Text;
            BackColor = AppTheme.SurfaceElevated;
            _inner.Font = Font;
            _inner.ForeColor = AppTheme.Text;
            _inner.CalendarMonthBackground = AppTheme.SurfaceElevated;
            UiTheme.ApplyControl(this);
            Reposition();
            Invalidate();
            _inner.Invalidate();
        }
        finally
        {
            ResumeLayout();
        }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _popup?.Dispose();
            AppTheme.ThemeChanged -= OnThemeChanged;
        }
        base.Dispose(disposing);
    }
    public bool TimeOnly
    {
        get => _format == DateTimePickerFormat.Custom &&
               _inner.ShowUpDown &&
               string.Equals(_customFormat, "hh:mm tt", StringComparison.Ordinal);
        set
        {
            if (value)
            {
                _format = DateTimePickerFormat.Custom;
                _customFormat = "hh:mm tt";
                _inner.ShowUpDown = true;
            }
            else
            {
                _format = DateTimePickerFormat.Custom;
                _customFormat = "dd-MMM-yyyy";
                _inner.ShowUpDown = false;
            }
            RefreshDisplayFormat();
            Reposition();
            Invalidate();
        }
    }
    public DateTime Value
    {
        get => _inner.Value;
        set => _inner.Value = value;
    }
    // plain pass-through to the inner DateTimePicker's own MinDate, same pattern as every other property on this control.
    public DateTime MinDate
    {
        get => _inner.MinDate;
        set => _inner.MinDate = value;
    }
    public DateTime MaxDate
    {
        get => _inner.MaxDate;
        set => _inner.MaxDate = value;
    }
    public event EventHandler? ValueChanged
    {
        add => _inner.ValueChanged += value;
        remove => _inner.ValueChanged -= value;
    }
    public DateTimePickerFormat Format
    {
        get => _format;
        set { _format = value; RefreshDisplayFormat(); }
    }
    [AllowNull]
    public string CustomFormat
    {
        get => _customFormat!;
        set { _customFormat = value; RefreshDisplayFormat(); }
    }
    public bool ShowUpDown
    {
        get => _inner.ShowUpDown;
        set => _inner.ShowUpDown = value;
    }
    public bool ShowCheckBox
    {
        get => _inner.ShowCheckBox;
        set { _inner.ShowCheckBox = value; RefreshDisplayFormat(); }
    }
    public bool Checked
    {
        get => _inner.Checked;
        set { _inner.Checked = value; NotifyDateStateChanged(); }
    }
    private void NotifyDateStateChanged()
    {
        bool checkedChanged = _lastChecked != _inner.Checked;
        _lastChecked = _inner.Checked;
        RefreshDisplayFormat();
        if (checkedChanged) CheckedChanged?.Invoke(this, EventArgs.Empty);
    }
    private void RefreshDisplayFormat()
    {
        bool empty = _inner.ShowCheckBox && !_inner.Checked;
        _inner.Format = empty ? DateTimePickerFormat.Custom : _format;
        _inner.CustomFormat = empty ? "'Not entered'" : _customFormat;
    }
    private void OpenCalendar()
    {
        if (!Enabled || _inner.ShowUpDown || _format == DateTimePickerFormat.Time) return;
        if (_popup is { Visible: true }) { _popup.Close(); return; }
        _popup?.Dispose();
        var selector = new DateSelectionPanel(Value, MinDate, MaxDate, ShowCheckBox);
        var popup = new ToolStripDropDown {
            Padding = Padding.Empty, AutoSize = true,
            BackColor = AppTheme.Surface, DropShadowEnabled = !AppTheme.UseSimpleRendering
        };
        _popup = popup;
        popup.Items.Add(new ToolStripControlHost(selector) {
            AutoSize = false, Size = selector.Size,
            Margin = Padding.Empty, Padding = Padding.Empty
        });
        selector.DateChosen += date => {
            ApplyCalendarDate(date);
            popup.Close();
        };
        selector.DateCleared += () => { Checked = false; popup.Close(); };
        popup.Closed += (_, _) => { if (!IsDisposed) _inner.Focus(); };
        popup.Show(this, new Point(0, Height));
        selector.FocusYear();
    }
    private void ApplyCalendarDate(DateTime date)
    {
        // Keep any time component carried by a date/time field, including
        // boundary times. The selection changes only the calendar date.
        DateTime selected = date.Date.Add(Value.TimeOfDay);
        selected = selected < MinDate ? MinDate : selected > MaxDate ? MaxDate : selected;
        Value = selected;
        if (ShowCheckBox) Checked = true;
    }
    public Color CalendarMonthBackground
    {
        get => _inner.CalendarMonthBackground;
        set => _inner.CalendarMonthBackground = value;
    }
    public new bool TabStop
    {
        get => _inner.TabStop;
        set { _inner.TabStop = value; base.TabStop = value; }
    }
    public new bool Focus() =>
        _inner.Focus();
    public new bool Focused =>
        _inner.Focused;
    /// <summary>Shared validation state (see <see cref="FormValidator"/>): a subtle danger-coloured border.</summary>
    public bool IsInvalid
    {
        get => _invalid;
        set
        {
            if (_invalid == value)
                return;
            _invalid = value;
            Invalidate();
        }
    }
    /// <summary>The native date picker inside is what a screen reader lands on.</summary>
    public void ApplyAccessibleInfo(string? name, string? description)
    {
#if FMSC_LEVEL2
        _inner.Name = Level2Target.InputId(name);
#endif
        _inner.AccessibleName = name;
        _inner.AccessibleDescription = description;
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (string.IsNullOrEmpty(_inner.AccessibleName) && !string.IsNullOrEmpty(AccessibleName))
            _inner.AccessibleName = AccessibleName;
        if (string.IsNullOrEmpty(_inner.AccessibleDescription) && !string.IsNullOrEmpty(AccessibleDescription))
            _inner.AccessibleDescription = AccessibleDescription;
    }
    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        if (_inner == null)
            return;
        _inner.Font = Font;
        Reposition();
    }
    protected override void OnForeColorChanged(EventArgs e)
    {
        base.OnForeColorChanged(e);
        if (_inner != null)
            _inner.ForeColor = ForeColor;
    }
    protected override void OnBackColorChanged(EventArgs e)
    {
        base.OnBackColorChanged(e);
        Invalidate();
    }
    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Reposition();
    }
    private void Reposition()
    {
        if (_inner == null ||
            Width <= 0 ||
            Height <= 0)
            return;
        if (AppTheme.UseSimpleRendering)
        {
            if (Region is not null) UiHelper.ReplaceRegion(this, null);
        }
        else
        {
            using var path = CreateRoundedPath(new Rectangle(0, 0, Width, Height), _radius);
            UiHelper.ReplaceRegion(this, new Region(path));
        }
        int innerWidth =
            Math.Max(0, Width - InsetLeft - InsetRight);
        int innerHeight =
            Math.Min(_inner.PreferredHeight, Math.Max(0, Height - 2));
        int top =
            Math.Max(0, (Height - innerHeight) / 2);
        _inner.SetBounds(InsetLeft, top, innerWidth, innerHeight);
        Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode =
            AppTheme.UseSimpleRendering ? SmoothingMode.None : SmoothingMode.AntiAlias;
        var rect =
            new Rectangle(0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
        using var shape =
            new UiPaintShape(rect, _radius);
        using var brush =
            new SolidBrush(
                Enabled
                    ? AppTheme.SurfaceElevated
                    : AppTheme.ButtonDisabledBackground);
        shape.Fill(e.Graphics, brush);
        var borderColor =
            !Enabled
                ? AppTheme.InputBorder
                : _invalid
                    ? AppTheme.Danger
                    : _inner.Focused
                        ? AppTheme.BorderFocus
                        : _isHovered
                            ? AppTheme.InputBorderHover
                            : AppTheme.InputBorder;
        using var pen =
            new Pen(
                borderColor,
                Enabled && _inner.Focused
                    ? 1.8F
                    : Enabled && _invalid
                        ? 1.5F
                        : 1F) {
                Alignment = PenAlignment.Inset
            };
        shape.Draw(e.Graphics, pen);
    }
    private static GraphicsPath CreateRoundedPath(Rectangle rect, int radius)
    {
        int diameter =
            radius * 2;
        var path =
            new GraphicsPath();
        if (radius <= 0 ||
            diameter > rect.Width ||
            diameter > rect.Height)
        {
            path.AddRectangle(rect);
            return path;
        }
        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
    private sealed class InnerDatePicker : DateTimePicker
    {
        internal Action? OpenCalendar;
        internal Action? StateChanged;
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            StateChanged?.Invoke();
        }
        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            StateChanged?.Invoke();
        }
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (!ShowUpDown && Format != DateTimePickerFormat.Time &&
                (keyData == Keys.F4 || keyData == (Keys.Alt | Keys.Down)))
            {
                OpenCalendar?.Invoke();
                return true;
            }
            if (keyData == Keys.Enter && FormFlow.TryAdvance(this))
                return true;
            return base.ProcessCmdKey(ref msg, keyData);
        }
        private const int WM_NCPAINT = 0x0085;
        private const int WM_NCACTIVATE = 0x0086;
        private const int WS_BORDER = 0x00800000;
        private const int WS_EX_CLIENTEDGE = 0x00000200;
        [DllImport(
            "uxtheme.dll",
            CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(nint hWnd, string? pszSubAppName, string? pszSubIdList);
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.Style &= ~WS_BORDER;
                cp.ExStyle &= ~WS_EX_CLIENTEDGE;
                return cp;
            }
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                SetWindowTheme(Handle, string.Empty, string.Empty);
            }
            catch (Exception nonFatalException15)
            {
                FMSC_SMS.Core.AppExceptionHandler.LogNonFatal(nonFatalException15, "ModernDatePicker: OnHandleCreated fallback");
            }
        }
        protected override void WndProc(ref Message m)
        {
            const int WM_LBUTTONDOWN = 0x0201;
            if (m.Msg == WM_LBUTTONDOWN && !ShowUpDown && Format != DateTimePickerFormat.Time)
            {
                int x = (short)((long)m.LParam & 0xffff);
                int arrowWidth = SystemInformation.VerticalScrollBarWidth + 4;
                if (x >= ClientSize.Width - arrowWidth)
                {
                    OpenCalendar?.Invoke();
                    return;
                }
            }
            if (m.Msg == WM_NCPAINT ||
                m.Msg == WM_NCACTIVATE)
            {
                m.Result = nint.Zero;
                return;
            }
            base.WndProc(ref m);
            // Checkbox changes do not consistently raise ValueChanged. Refresh
            // after the native date-change notification as well as mouse/keys.
            const int WM_REFLECT_NOTIFY = 0x204e;
            if (m.Msg == WM_REFLECT_NOTIFY) StateChanged?.Invoke();
        }
    }
}
