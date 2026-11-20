using System.Diagnostics.CodeAnalysis;
using System.Drawing.Drawing2D;
namespace FMSC_SMS.Core;
public class ModernComboBox : Panel, IValidationTarget, IAccessibleInput
{
    private readonly InnerComboBox _inner;
    private bool _invalid;
    private int _radius =
        Math.Max(1, AppTheme.ControlRadius - 2);
    private const int InsetLeft = 8;
    private const int InsetRight = 8;
    private bool _isHovered;
    private UiColorTransition? _borderMotion;
    private bool _dropDownOpen;
    private void UpdateBorderMotion(bool animate = true)
    {
        var color = !Enabled ? AppTheme.InputBorder : _invalid ? AppTheme.Danger :
            _dropDownOpen || _inner.Focused ? AppTheme.BorderFocus :
            _isHovered ? AppTheme.InputBorderHover : AppTheme.InputBorder;
        _borderMotion ??= new UiColorTransition(this, AppTheme.InputBorder, _ => Invalidate());
        _borderMotion.MoveTo(color, animate && Enabled && !_invalid);
        Invalidate();
    }
    public int CornerRadius
    {
        get => _radius;
        set
        {
            _radius = value;
            Reposition();
        }
    }
    public ModernComboBox()
    {
        _inner = new InnerComboBox {
            FlatStyle = FlatStyle.Flat,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = AppTheme.NormalFont,
            ForeColor = AppTheme.Text,
            BackColor = AppTheme.SurfaceElevated,
            IntegralHeight = false
        };
        _inner.TextChanged += (_, _) =>
            OnTextChanged(EventArgs.Empty);
        _inner.GotFocus += (_, _) => {
            OnGotFocus(EventArgs.Empty);
            UpdateBorderMotion();
        };
        _inner.LostFocus += (_, _) => {
            OnLostFocus(EventArgs.Empty);
            UpdateBorderMotion();
        };
        _inner.MouseEnter += (_, _) => {
            _isHovered = true;
            UpdateBorderMotion();
        };
        _inner.MouseLeave += (_, _) => {
            _isHovered = false;
            UpdateBorderMotion();
        };
        _inner.Enter += (_, _) =>
            OnEnter(EventArgs.Empty);
        _inner.Leave += (_, _) =>
            OnLeave(EventArgs.Empty);
        _inner.KeyDown += (_, e) =>
            OnKeyDown(e);
        _inner.KeyPress += (_, e) =>
            OnKeyPress(e);
        _inner.KeyUp += (_, e) =>
            OnKeyUp(e);
        _inner.Click += (_, e) =>
            OnClick(e);
        _inner.DoubleClick += (_, e) =>
            OnDoubleClick(e);
        _inner.SelectedIndexChanged += (_, e) =>
            SelectedIndexChanged?.Invoke(this, e);
        _inner.SelectionChangeCommitted += (_, e) =>
            SelectionChangeCommitted?.Invoke(this, e);
        _inner.DropDown += (_, _) => { _dropDownOpen = true; UpdateBorderMotion(); };
        _inner.DropDownClosed += (_, _) => { _dropDownOpen = false; UpdateBorderMotion(); };
        Controls.Add(_inner);
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);
        BackColor =
            AppTheme.SurfaceElevated;
        ForeColor =
            AppTheme.Text;
        Font =
            AppTheme.NormalFont;
        Height =
            AppMetrics.InputControlHeight;
        Reposition();
        AppTheme.ThemeChanged +=
            OnThemeChanged;
    }
    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (IsDisposed ||
            Disposing)
        {
            return;
        }
        RefreshTheme();
    }
    public void RefreshTheme()
    {
        if (IsDisposed ||
            Disposing)
        {
            return;
        }
        SuspendLayout();
        try
        {
            Font = UiDpi.ForControl(this, AppTheme.NormalFont);
            ForeColor =
                AppTheme.Text;
            BackColor =
                AppTheme.SurfaceElevated;
            _inner.Font = Font;
            _inner.ForeColor =
                AppTheme.Text;
            _inner.BackColor =
                Enabled?AppTheme.SurfaceElevated:AppTheme.ButtonDisabledBackground;
            UiTheme.ApplyControl(this);
            Reposition();
            Invalidate();
            _inner.Invalidate();
            UpdateBorderMotion(animate: false);
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
            _borderMotion?.Dispose();
            AppTheme.ThemeChanged -=
                OnThemeChanged;
        }
        base.Dispose(disposing);
    }
    public event EventHandler?
        SelectedIndexChanged;
    public event EventHandler?
        SelectionChangeCommitted;
    public event EventHandler? DataSourceChanged
    {
        add => _inner.DataSourceChanged += value;
        remove => _inner.DataSourceChanged -= value;
    }
    [AllowNull]
    public override string Text
    {
        get =>
            _inner?.Text ??
            string.Empty;
        set
        {
            if (_inner != null)
            {
                _inner.Text =
                    value ??
                    string.Empty;
            }
        }
    }
    public ComboBoxStyle DropDownStyle
    {
        get =>
            _inner.DropDownStyle;
        set =>
            _inner.DropDownStyle =
                value;
    }
    public object? DataSource
    {
        get =>
            _inner.DataSource;
        set =>
            _inner.DataSource =
                value is DataTable table ? LookupDisplayOrder.ForBinding(table, ValueMember) : value;
    }
    public string ValueMember
    {
        get =>
            _inner.ValueMember;
        set =>
            _inner.ValueMember =
                value;
    }
    public string DisplayMember
    {
        get =>
            _inner.DisplayMember;
        set =>
            _inner.DisplayMember =
                value;
    }
    public bool FormattingEnabled { get=>_inner.FormattingEnabled; set=>_inner.FormattingEnabled=value; }
    public event ListControlConvertEventHandler? Format { add=>_inner.Format+=value; remove=>_inner.Format-=value; }
    public object? SelectedItem
    {
        get =>
            _inner.SelectedItem;
        set =>
            _inner.SelectedItem =
                value;
    }
    public object? SelectedValue
    {
        get =>
            _inner.SelectedValue;
        set =>
            _inner.SelectedValue =
                value;
    }
    public int SelectedIndex
    {
        get =>
            _inner.SelectedIndex;
        set =>
            _inner.SelectedIndex =
                value;
    }
    public ComboBox.ObjectCollection Items =>
        _inner.Items;
    public int DropDownWidth
    {
        get =>
            _inner.DropDownWidth;
        set =>
            _inner.DropDownWidth =
                value;
    }
    public int SelectionStart
    {
        get =>
            _inner.SelectionStart;
        set =>
            _inner.SelectionStart =
                value;
    }
    public int SelectionLength
    {
        get =>
            _inner.SelectionLength;
        set =>
            _inner.SelectionLength =
                value;
    }
    public bool DroppedDown
    {
        get =>
            _inner.DroppedDown;
        set =>
            _inner.DroppedDown =
                value;
    }
    public int FindStringExact(
        string s) =>
        _inner.FindStringExact(s);
    public AutoCompleteMode AutoCompleteMode
    {
        get =>
            _inner.AutoCompleteMode;
        set =>
            _inner.AutoCompleteMode =
                value;
    }
    public AutoCompleteSource AutoCompleteSource
    {
        get =>
            _inner.AutoCompleteSource;
        set =>
            _inner.AutoCompleteSource =
                value;
    }
    public void SelectAll() =>
        _inner.SelectAll();
    public new bool Focus() =>
        _inner.Focus();
    public new bool TabStop
    {
        get => _inner.TabStop;
        set { _inner.TabStop = value; base.TabStop = value; }
    }
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
            UpdateBorderMotion(animate: false);
        }
    }
    /// <summary>The native combo box inside is what a screen reader lands on.</summary>
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
        _inner.Font =
            Font;
        Reposition();
    }
    protected override void OnForeColorChanged(EventArgs e)
    {
        base.OnForeColorChanged(e);
        if (_inner != null)
        {
            _inner.ForeColor =
                ForeColor;
        }
    }
    protected override void OnBackColorChanged(EventArgs e)
    {
        base.OnBackColorChanged(e);
        if (_inner != null)
        {
            _inner.BackColor =
                Enabled?AppTheme.SurfaceElevated:AppTheme.ButtonDisabledBackground;
        }
        Invalidate();
    }
    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        if(_inner is not null)_inner.BackColor=Enabled?AppTheme.SurfaceElevated:AppTheme.ButtonDisabledBackground;
        if (_inner is not null) UpdateBorderMotion(animate: false);
    }
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Reposition();
    }
    protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified)
    {
        int standardHeight =
            AppMetrics.InputControlHeight;
        height = standardHeight;
        base.SetBoundsCore(x, y, width, height, specified);
    }
    private void Reposition()
    {
        if (_inner == null ||
            Width <= 0 ||
            Height <= 0)
        {
            return;
        }
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
            Math.Max(
                0,
                Width -
                InsetLeft -
                InsetRight);
        int innerHeight =
            Math.Min(
                _inner.PreferredHeight,
                Math.Max(0, Height - 2));
        int top =
            Math.Max(
                0,
                (Height -
                 innerHeight) / 2);
        _inner.SetBounds(InsetLeft, top, innerWidth, innerHeight);
        Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode =
            AppTheme.UseSimpleRendering ? SmoothingMode.None : SmoothingMode.AntiAlias;
        var rect =
            new Rectangle(
                0,
                0,
                Math.Max(
                    0,
                    Width - 1),
                Math.Max(0, Height - 1));
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
        if (Enabled && !_invalid && _borderMotion is not null)
            borderColor = _borderMotion.Value;
        using var pen =
            new Pen(
                borderColor,
                Enabled && _inner.Focused
                    ? 1.8F
                    : Enabled && _invalid
                        ? 1.5F
                        : 1F) {
                Alignment =
                    PenAlignment.Inset
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
    private sealed class InnerComboBox : ComboBox
    {
        private const int WM_MOUSEWHEEL = 0x020A;
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        /* A closed list now hands the wheel to the surrounding page (so it scrolls); an open list still scrolls its own items. */
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            /* Enter on a closed list moves on when the form opted in (FormFlow); on an open list it still just picks the highlighted item. */
            if (keyData == Keys.Enter && !DroppedDown && FormFlow.TryAdvance(this))
                return true;
            return base.ProcessCmdKey(ref msg, keyData);
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEWHEEL && !DroppedDown && Parent is { IsHandleCreated: true } parent)
            {
                SendMessage(parent.Handle, m.Msg, m.WParam, m.LParam);
                return;
            }
            base.WndProc(ref m);
        }
        protected override void OnDropDown(EventArgs e)
        {
            DropDownHeight = Math.Max(1, Math.Min(Items.Count, 8)) * ItemHeight + 2;
            base.OnDropDown(e);
            UpdateDropDownWidth();
        }
        private void UpdateDropDownWidth()
        {
            if (Items.Count == 0)
                return;
            int widest =
                Width;
            using (var g =
                CreateGraphics())
            {
                foreach (object? item in Items)
                {
                    string? text =
                        GetItemText(item);
                    int textWidth =
                        TextRenderer.MeasureText(
                            g,
                            text,
                            Font).Width;
                    if (textWidth > widest)
                        widest =
                            textWidth;
                }
            }
            DropDownWidth =
                Math.Min(widest + 32, 640);
        }
    }
}
