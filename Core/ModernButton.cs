using System.Drawing.Drawing2D;
namespace FMSC_SMS.Core
{
    public class ModernButton : Button
    {
        // Explicit semantics for commit buttons whose caption is not "Save".
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal bool SavesChanges { get; set; }
        private char? _shortcutLetter;
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal char? ShortcutLetter {
            get=>_shortcutLetter;
            set {if(_shortcutLetter==value)return;_shortcutLetter=value;Invalidate();}
        }
        private int _radius =
            AppTheme.ControlRadius;
        private Color _normalColor;
        private Color _hoverColor;
        private Color _pressedColor;
        private bool _isHovered;
        private bool _isPressed;
        private UiColorTransition? _hoverMotion;
        private void TransitionFill(Color color, bool animate = true)
        {
            _hoverMotion ??= new UiColorTransition(this, BackColor, value => { BackColor = value; Invalidate(); });
            _hoverMotion.MoveTo(color, animate);
        }
        private string _themeRole =
            "Primary";
        private bool _themeRoleExplicitlySet;
        private bool _outline;
        private Color _outlineColor = AppTheme.Border;
        private Color _outlineTextColor = AppTheme.Primary;
        private string _outlineBorderRole = "Fixed";
        private string _outlineTextRole = "Fixed";
        private string _outlineFillRole = "Fixed";
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public int CornerRadius
        {
            get => _radius;
            set
            {
                _radius = value;
                UpdateRegion();
                Invalidate();
            }
        }
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public string ThemeRole
        {
            get => _themeRole;
            set
            {
                _themeRole =
                    ActionButtonStyle.Role(Text, value ?? "Primary");
                _themeRoleExplicitlySet = true;
                ApplyTheme();
            }
        }
        /// <summary>False keeps the button out of the keyboard focus completely: Tab skips it and a click runs it without moving the focus off the field the operator was typing in.</summary>
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool TakesFocus
        {
            get => GetStyle(ControlStyles.Selectable);
            set
            {
                SetStyle(ControlStyles.Selectable, value);
                TabStop = value;
            }
        }
        public ModernButton()
        {
            UseMnemonic=false; // Shortcut underlines are painted from the shared Ctrl bindings.
            FlatStyle =
                FlatStyle.Flat;
            FlatAppearance.BorderSize =
                0;
            UseVisualStyleBackColor =
                false;
            Height =
                AppMetrics.ButtonHeight;
            Font =
                AppTheme.ButtonFont;
            ForeColor =
                GetButtonTextColor(AppTheme.Primary);
            Cursor =
                Cursors.Hand;
            TextAlign =
                ContentAlignment.MiddleCenter;
            _normalColor =
                AppTheme.Primary;
            _hoverColor =
                AppTheme.PrimaryHover;
            _pressedColor =
                ControlPaint.Dark(AppTheme.Primary, 0.10f);
            BackColor =
                _normalColor;
            SetStyle(
                ControlStyles.UserPaint |
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw,
                true);
        }
        public void SetColors(Color normalColor)
        {
            _outline = false;
            _themeRoleExplicitlySet = true;
            FlatAppearance.BorderSize = 0;
            _normalColor =
                normalColor;
            if (normalColor ==
                AppTheme.Primary)
            {
                _themeRole =
                    "Primary";
            }
            else if (normalColor ==
                     AppTheme.Success)
            {
                _themeRole =
                    "Success";
            }
            else if (normalColor ==
                     AppTheme.Warning)
            {
                _themeRole =
                    "Warning";
            }
            else if (normalColor ==
                     AppTheme.Danger)
            {
                _themeRole =
                    "Danger";
            }
            else
            {
                _themeRole =
                    "Custom";
            }
            UpdateColors();
        }
        public void SetOutlineColors(Color borderColor, Color textColor, Color fillColor)
        {
            _themeRole = "Outline";
            _outline = true;
            _outlineColor = borderColor;
            _outlineTextColor = textColor;
            _normalColor = fillColor;
            _outlineBorderRole = ClassifyThemeColor(borderColor);
            _outlineTextRole = ClassifyThemeColor(textColor);
            _outlineFillRole = ClassifyThemeColor(fillColor);
            bool fillIsWhite = fillColor.ToArgb() == Color.White.ToArgb();
            if (fillIsWhite)
            {
                _hoverColor = AppTheme.Tint(textColor, 0.88f);
                _pressedColor = AppTheme.Tint(textColor, 0.78f);
            }
            else
            {
                _hoverColor = ControlPaint.Dark(fillColor, 0.10f);
                _pressedColor = ControlPaint.Dark(fillColor, 0.20f);
            }
            BackColor = fillColor;
            ForeColor = textColor;
            FlatAppearance.BorderSize = 1;
            FlatAppearance.BorderColor = borderColor;
            Invalidate();
        }
        private static string ClassifyThemeColor(Color color)
        {
            if (color == AppTheme.Primary) return "Primary";
            if (color == AppTheme.Success) return "Success";
            if (color == AppTheme.Warning) return "Warning";
            if (color == AppTheme.Danger) return "Danger";
            if (color == AppTheme.Surface) return "Surface";
            if (color == AppTheme.InputBorder) return "InputBorder";
            if (color == AppTheme.PrimaryLight) return "PrimaryLight";
            return "Fixed";
        }
        private static Color ResolveThemeColor(string role, Color fixedValue) => role switch {
            "Primary" => AppTheme.Primary,
            "Success" => AppTheme.Success,
            "Warning" => AppTheme.Warning,
            "Danger" => AppTheme.Danger,
            "Surface" => AppTheme.Surface,
            "InputBorder" => AppTheme.InputBorder,
            "PrimaryLight" => AppTheme.PrimaryLight,
            _ => fixedValue
        };
        private static Color GetButtonTextColor(Color background) => ThemeAppearance.ReadableText(background);

        public void ApplyTheme()
        {
            UpdateRegion();
            try { ApplyThemeColors(); }
            finally { _hoverMotion?.MoveTo(BackColor, animate: false); }
        }
        private void ApplyThemeColors()
        {
            if (_themeRole == "Outline")
            {
                var border = ResolveThemeColor(_outlineBorderRole, _outlineColor);
                var text = ResolveThemeColor(_outlineTextRole, _outlineTextColor);
                var fill = ResolveThemeColor(_outlineFillRole, _normalColor);
                SetOutlineColors(border, text, fill);
                return;
            }
            switch (_themeRole)
            {
                case "Success":
                    _normalColor =
                        AppTheme.Success;
                    break;
                case "Warning":
                    _normalColor =
                        AppTheme.Warning;
                    break;
                case "Danger":
                    _normalColor =
                        AppTheme.Danger;
                    break;
                case "Secondary":
                    SetOutlineColors(AppTheme.InputBorder, AppTheme.Text, AppTheme.Surface);
                    // Preserve the semantic role across theme changes.
                    _themeRole = "Secondary";
                    return;
                case "TabActive":
                    _outline = false;
                    FlatAppearance.BorderSize = 0;
                    _normalColor = AppTheme.Primary;
                    break;
                case "TabInactive":
                    _outline = false;
                    FlatAppearance.BorderSize = 0;
                    _normalColor =
                        AppTheme.SurfaceSecondary;
                    ForeColor = AppTheme.Text;
                    BackColor = _normalColor;
                    _hoverColor = AppTheme.PrimaryLight;
                    _pressedColor = AppTheme.PrimaryLight;
                    Invalidate();
                    return;
                case "Navigation":
                    _normalColor = GetMenuBackgroundColor();
                    BackColor = _normalColor;
                    ForeColor = AppTheme.MenuText;
                    _hoverColor = AppTheme.MenuHover;
                    _pressedColor = AppTheme.MenuActive;
                    FlatAppearance.BorderSize = 0;
                    Invalidate();
                    return;
                case "Custom":
                    _themeRole = "Primary";
                    _normalColor = AppTheme.Primary;
                    break;
                default:
                    _normalColor =
                        AppTheme.Primary;
                    break;
            }
            UpdateColors();
        }
        private static Color GetMenuBackgroundColor()
        {
            return AppTheme.MenuBackground;
        }
        private void UpdateColors()
        {
            _hoverColor =
                AppTheme.UseSimpleRendering ? _themeRole switch {
                    "Primary" or "TabActive" => AppTheme.PrimaryHover,
                    "Success" => AppTheme.SuccessHover,
                    "Warning" => AppTheme.WarningHover,
                    "Danger" => AppTheme.DangerHover,
                    _ => ControlPaint.Dark(_normalColor,0.08f)
                } : ControlPaint.Light(_normalColor, 0.08f);
            _pressedColor =
                ControlPaint.Dark(_normalColor, 0.10f);
            BackColor =
                _normalColor;
            ForeColor =
                GetButtonTextColor(_normalColor);
            Invalidate();
        }
        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            string normalizedRole = ActionButtonStyle.Role(Text, _themeRoleExplicitlySet ? _themeRole : InferTextRole(Text));
            if (!string.Equals(_themeRole, normalizedRole, StringComparison.OrdinalIgnoreCase))
            {
                _themeRole = normalizedRole;
                ApplyTheme();
            }
        }
        protected override void OnParentChanged(EventArgs e)
        {
            base.OnParentChanged(e);
            if (Parent is not null && Height >= AppTheme.CompactButtonHeight &&
                ActionButtonStyle.Role(Text, "Unspecified") != "Unspecified" &&
                _themeRole is not ("Navigation" or "TabActive" or "TabInactive" or "Outline" or "Custom"))
            {
                Height = AppMetrics.ButtonHeight;
                Font = UiDpi.ForControl(this, AppTheme.ButtonFont);
            }
        }
        private static string InferTextRole(string? text) =>
            ActionButtonStyle.Role(text ?? string.Empty, UiDesignSystem.Actions.Primary);
        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            Invalidate();
        }
        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            Invalidate();
        }
        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _isHovered = true;
            TransitionFill(_hoverColor);
            Invalidate();
        }
        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _isHovered = false;
            _isPressed = false;
            TransitionFill(_normalColor);
            Invalidate();
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button ==
                MouseButtons.Left)
            {
                _isPressed = true;
                TransitionFill(_pressedColor, animate: false);
                Invalidate();
            }
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _isPressed = false;
            if (IsDisposed || Disposing || !IsHandleCreated)
                return;
            _isHovered = ClientRectangle.Contains(PointToClient(Cursor.Position));
            TransitionFill(_isHovered
                ? _hoverColor
                : _normalColor);
            Invalidate();
        }
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateRegion();
        }
        private void UpdateRegion()
        {
            if (AppTheme.UseSimpleRendering)
            {
                if (Region is not null) UiHelper.ReplaceRegion(this, null);
                return;
            }
            if (Width <= 0 ||
                Height <= 0)
                return;
            using var path =
                CreateRoundedPath(ClientRectangle, _radius);
            UiHelper.ReplaceRegion(this, new Region(path));
        }
        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            if (!Enabled)
            {
                _isHovered = false;
                _isPressed = false;
                _hoverMotion?.MoveTo(_normalColor, animate: false);
            }
            Cursor =
                Enabled
                    ? Cursors.Hand
                    : Cursors.Default;
            Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            bool simple = AppTheme.UseSimpleRendering;
            e.Graphics.SmoothingMode = simple ? SmoothingMode.None : SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            bool insetBorder = !simple && _radius > 0 && rect.Width > 2 && rect.Height > 2;
            // The native rounded region must contain the full anti-aliased stroke.
            var face = insetBorder ? Rectangle.Inflate(rect, -1, -1) : rect;
            int radius = insetBorder ? _radius - 1 : _radius;
            using var shape = new UiPaintShape(face, radius);
            if (insetBorder && Parent is { } parent)
            {
                using var backdrop = new SolidBrush(parent.BackColor);
                e.Graphics.FillRectangle(backdrop, ClientRectangle);
            }
            var fillColor =
                Enabled
                    ? BackColor
                    : AppTheme.ButtonDisabledBackground;
            var textColor =
                Enabled
                    ? _outline || _themeRole is "Navigation" or "TabInactive" ? ForeColor : GetButtonTextColor(fillColor)
                    : AppTheme.ButtonDisabledText;
            // Keyboard focus needs a visible whole-button change as well as a ring.
            // Keep this separate from hover, so focus remains clear with the mouse elsewhere.
            // An inactive tab can retain keyboard focus after Ctrl+Tab or a busy
            // refresh. Keep its inactive fill; the focus ring below remains visible.
            if (Focused && Enabled && !_isPressed && _themeRole != "TabInactive")
            {
                fillColor = _outline
                    ? AppTheme.Primary
                    : ControlPaint.Dark(fillColor, 0.22f);
                textColor = GetButtonTextColor(fillColor);
            }
            if (!simple && Enabled && !_outline && face.Width > 4 && face.Height > 4)
            {
                var shadowRect = new Rectangle(face.X, face.Y + 1, face.Width, face.Height);
                using var shadowPath =
                    CreateRoundedPath(shadowRect, radius);
                using var shadowBrush =
                    new SolidBrush(AppTheme.ButtonShadow);
                e.Graphics.FillPath(shadowBrush, shadowPath);
            }
            using var brush =
                new SolidBrush(fillColor);
            shape.Fill(e.Graphics, brush);
            var borderColor =
                _outline
                    ? _outlineColor
                    : ControlPaint.Dark(fillColor, 0.08f);
            float borderWidth =
                _outline ? 1.2f : 0.8f;
            using var pen = new Pen(borderColor, borderWidth) {
                Alignment = insetBorder ? PenAlignment.Center : PenAlignment.Inset
            };
            shape.Draw(e.Graphics, pen);
            // Paint focus AFTER the fill and border, otherwise the fill erases it.
            // Always show the focused action, including focus moved here by Enter.
            if (Focused && Enabled && rect.Width > 12 && rect.Height > 12 &&
                (_themeRole is not ("TabActive" or "TabInactive") || ShowFocusCues))
            {
                int inset = Math.Max(3, (int)Math.Round(3f * DeviceDpi / 96f));
                using var focusPen = new Pen(GetButtonTextColor(fillColor), Math.Max(2f, 2f * DeviceDpi / 96f));
                var focusRect = Rectangle.Inflate(rect, -inset, -inset);
                using var focusShape = new UiPaintShape(focusRect, Math.Max(1, _radius - inset));
                focusShape.Draw(e.Graphics, focusPen);
            }
            var textRect = Rectangle.Inflate(rect, -Padding.Horizontal / 2, -Padding.Vertical / 2);
            var alignmentFlags = TextAlign switch {
                ContentAlignment.TopLeft => TextFormatFlags.Left | TextFormatFlags.Top,
                ContentAlignment.MiddleLeft => TextFormatFlags.Left | TextFormatFlags.VerticalCenter,
                ContentAlignment.BottomLeft => TextFormatFlags.Left | TextFormatFlags.Bottom,
                ContentAlignment.TopCenter => TextFormatFlags.HorizontalCenter | TextFormatFlags.Top,
                ContentAlignment.BottomCenter => TextFormatFlags.HorizontalCenter | TextFormatFlags.Bottom,
                ContentAlignment.TopRight => TextFormatFlags.Right | TextFormatFlags.Top,
                ContentAlignment.MiddleRight => TextFormatFlags.Right | TextFormatFlags.VerticalCenter,
                ContentAlignment.BottomRight => TextFormatFlags.Right | TextFormatFlags.Bottom,
                _ => TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
            };
            if (Enabled && _isPressed)
                textRect.Offset(0, 1);
            textRect = PaintLeadingContent(e.Graphics, textRect, textColor);
            TextRenderer.DrawText(e.Graphics, Text, Font, textRect, textColor, alignmentFlags | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
        protected virtual Rectangle PaintLeadingContent(Graphics graphics, Rectangle bounds, Color textColor) => bounds;
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hoverMotion?.Dispose();
                Image? image = Image;
                Image = null;
                image?.Dispose();
            }
            base.Dispose(disposing);
        }
        private static GraphicsPath CreateRoundedPath(Rectangle rect, int radius)
        {
            int diameter =
                radius * 2;
            var path =
                new GraphicsPath();
            if (radius <= 0)
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
    }
}
