using System.Diagnostics.CodeAnalysis;
using System.Drawing.Drawing2D;
namespace FMSC_SMS.Core
{
    public class ModernTextBox : Panel, IValidationTarget, IAccessibleInput
    {
        private readonly InnerTextBox _inner;
        private Button? _browseButton;
        private LiveTextSuggestions? _liveSuggestions;
        private AutoCompleteMode _autoCompleteMode;
        private bool _invalid;
        private bool _selectAllOnMouseUp;
        /// <summary>Select existing search text on entry; subsequent clicks can position the caret.</summary>
        public bool SelectAllOnFocus { get; set; }
        private bool _zeroAsPlaceholder;
        /// <summary>Show an empty numeric editor while retaining zero for existing text-based parsers.</summary>
        public bool ZeroAsPlaceholder
        {
            get => _zeroAsPlaceholder;
            set { _zeroAsPlaceholder = value; if(value && this is not ModernNumericTextBox)WholeNumberInput.Attach(this); Text = Text; UpdateNumericPlaceholder(); }
        }
        private void UpdateNumericPlaceholder()
        {
            if (_zeroAsPlaceholder) _inner.PlaceholderText = _inner.Focused ? "" : "0";
        }
        private int _radius =
            Math.Max(1, AppTheme.ControlRadius - 2);
        private const int TextInsetLeft = 8;
        private const int TextInsetRight = 8;
        private const int MultilineVerticalInset = 8;
        private bool _isHovered;
        private bool _blendWithParent;
        private Control? _backgroundParent;
        public bool BlendWithParent
        {
            get => _blendWithParent;
            set { _blendWithParent = value; UpdateBlendedBackground(); UpdateInputBackground(); Invalidate(); }
        }
        private void UpdateInputBackground()
        {
            if(_inner is null)return;
            Color color=!_blendWithParent&&(!Enabled||ReadOnly)?AppTheme.ButtonDisabledBackground:BackColor;
            if(_inner.BackColor!=color)_inner.BackColor=color;
        }
        private void UpdateBlendedBackground()
        {
            if (!_blendWithParent) return;
            Control? parent = Parent;
            while (parent is not null && parent.BackColor.A < 255) parent = parent.Parent;
            Color color = parent?.BackColor ?? AppTheme.SectionHeaderBackground;
            if (BackColor != color) BackColor = color;
            if (_inner is not null) _inner.BackColor = color;
        }
        private void ParentBackgroundChanged(object? sender, EventArgs e) => UpdateBlendedBackground();
        protected override void OnParentChanged(EventArgs e)
        {
            if (_backgroundParent is not null) _backgroundParent.BackColorChanged -= ParentBackgroundChanged;
            base.OnParentChanged(e);
            _backgroundParent = Parent;
            if (_backgroundParent is not null) _backgroundParent.BackColorChanged += ParentBackgroundChanged;
            UpdateBlendedBackground();
        }
        public HorizontalAlignment TextAlign
        {
            get => _inner.TextAlign;
            set => _inner.TextAlign = value;
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
        public ModernTextBox()
        {
            _inner = new InnerTextBox {
                BorderStyle = BorderStyle.None,
                Multiline = false,
                ScrollBars = ScrollBars.None,
                Font = AppTheme.NormalFont,
                ForeColor = AppTheme.Text,
                BackColor = AppTheme.SurfaceElevated
            };
            _inner.TextChanged += (_, _) => { _selectAllOnMouseUp = false; OnTextChanged(EventArgs.Empty); };
            _inner.GotFocus += (_, _) => {
                UpdateNumericPlaceholder();
                OnGotFocus(EventArgs.Empty);
                _selectAllOnMouseUp = SelectAllOnFocus;
                if (SelectAllOnFocus) _inner.SelectAll();
                Invalidate();
            };
            _inner.MouseUp += (_, e) => {
                if (e.Button != MouseButtons.Left) return;
                if (_selectAllOnMouseUp && SelectAllOnFocus) _inner.SelectAll();
                _selectAllOnMouseUp = false;
            };
            _inner.LostFocus += (_, _) => { if (_zeroAsPlaceholder) Text = Text; UpdateNumericPlaceholder(); OnLostFocus(EventArgs.Empty); Invalidate(); };
            _inner.MouseEnter += (_, _) => { _isHovered = true; Invalidate(); };
            _inner.MouseLeave += (_, _) => { _isHovered = false; Invalidate(); };
            _inner.Enter += (_, _) => OnEnter(EventArgs.Empty);
            _inner.Leave += (_, _) => OnLeave(EventArgs.Empty);
            _inner.KeyDown += (_, e) => { _selectAllOnMouseUp = false; OnKeyDown(e); };
            _inner.KeyPress += (_, e) => OnKeyPress(e);
            _inner.KeyUp += (_, e) => OnKeyUp(e);
            _inner.Click += (_, e) => OnClick(e);
            _inner.DoubleClick += (_, e) => OnDoubleClick(e);
            Controls.Add(_inner);
            SetStyle(
                ControlStyles.UserPaint |
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw,
                true);
            BackColor =
                AppTheme.SurfaceElevated;
            Font =
                AppTheme.NormalFont;
            Height =
                AppMetrics.InputControlHeight;
            Reposition();
            AppTheme.ThemeChanged += OnThemeChanged;
        }
        private void OnThemeChanged(object? sender, EventArgs e)
        {
            if (IsDisposed || Disposing)
                return;
            Font = UiDpi.ForControl(this, AppTheme.NormalFont);
            ForeColor = AppTheme.Text;
            BackColor = AppTheme.SurfaceElevated;
            _inner.Font = Font;
            _inner.ForeColor = AppTheme.Text;
            _inner.BackColor = AppTheme.SurfaceElevated;
            UiTheme.ApplyControl(this);
            UpdateBlendedBackground();
            UpdateInputBackground();
            Reposition();
            Invalidate();
            _inner.Invalidate();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _liveSuggestions?.Dispose();
                if (_backgroundParent is not null) _backgroundParent.BackColorChanged -= ParentBackgroundChanged;
                AppTheme.ThemeChanged -= OnThemeChanged;
            }
            base.Dispose(disposing);
        }
        [AllowNull]
        public override string Text
        {
            get => _zeroAsPlaceholder && string.IsNullOrWhiteSpace(_inner?.Text) ? "0" : _inner?.Text ?? string.Empty;
            set {
                if (_inner == null) return;
                bool zero = _zeroAsPlaceholder && decimal.TryParse(value, System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out decimal number) && number == 0;
                _inner.Text = zero ? "" : value ?? string.Empty;
            }
        }
        /// <summary>A multiline field must accept Enter as a new line. Setting it with Multiline fixes every multiline field at once, including the shared ones built by HumanUi.TextBox.</summary>
        public bool Multiline
        {
            get => _inner.Multiline;
            set
            {
                _inner.Multiline = value;
                _inner.AcceptsReturn = value;
                Reposition();
            }
        }
        public ScrollBars ScrollBars
        {
            get => _inner.ScrollBars;
            set => _inner.ScrollBars = value;
        }
        public int MaxLength
        {
            get => _inner.MaxLength;
            set => _inner.MaxLength = value;
        }
        public string PlaceholderText
        {
            get => _inner.PlaceholderText;
            set => _inner.PlaceholderText = value;
        }
        internal Button AddFolderBrowseButton(string accessibleName, EventHandler click)
        {
            var button = new Button {
                Text = "\uE838",
                Font = AppTheme.IconFont(12f * AppScale.BodyFontScale),
                AccessibleName = accessibleName,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.Transparent,
                ForeColor = AppTheme.Text,
                UseVisualStyleBackColor = false,
                Cursor = Cursors.Hand,
                TabIndex = _inner.TabIndex + 1
            };
            button.FlatAppearance.BorderSize = 0;
            button.Click += click;
            _browseButton = button;
            Controls.Add(button);
            Reposition();
            return button;
        }
        public bool UseSystemPasswordChar
        {
            get => _inner.UseSystemPasswordChar;
            set => _inner.UseSystemPasswordChar = value;
        }
        public bool ReadOnly
        {
            get => _inner.ReadOnly;
            set
            {
                _inner.ReadOnly = value;
                UpdateInputBackground();
                Invalidate();
            }
        }
        /// <summary>Proxies the inner text box's TabStop so a display-only field can be kept out of the keyboard path.</summary>
        public new bool TabStop
        {
            get => _inner.TabStop;
            set
            {
                _inner.TabStop = value;
                base.TabStop = value;
            }
        }
        public int SelectionStart
        {
            get => _inner.SelectionStart;
            set => _inner.SelectionStart = value;
        }
        public int SelectionLength
        {
            get => _inner.SelectionLength;
            set => _inner.SelectionLength = value;
        }
        public void SelectAll() => _inner.SelectAll();
        public void Clear() => _inner.Clear();
        public new bool Focus() => _inner.Focus();
        public new bool Focused => _inner.Focused;
        /// <summary>Shared validation state (see <see cref="FormValidator"/>): a subtle danger-coloured border, nothing else. The fill, font and layout are untouched.</summary>
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
        /// <summary>The native text box inside is what a screen reader lands on, so the name and description given to this wrapper have to reach it.</summary>
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
        public AutoCompleteMode AutoCompleteMode
        {
            get => _autoCompleteMode;
            set
            {
                _autoCompleteMode = value;
                if (value == AutoCompleteMode.SuggestAppend)
                {
                    _inner.AutoCompleteMode = AutoCompleteMode.None;
                    _liveSuggestions ??= new LiveTextSuggestions(this, _inner);
                    _liveSuggestions.Enabled = true;
                }
                else
                {
                    if (_liveSuggestions is not null) { _liveSuggestions.Enabled = false; _liveSuggestions.Hide(); }
                    _inner.AutoCompleteMode = value;
                }
            }
        }
        public AutoCompleteSource AutoCompleteSource
        {
            get => _inner.AutoCompleteSource;
            set => _inner.AutoCompleteSource = value;
        }
        public AutoCompleteStringCollection AutoCompleteCustomSource
        {
            get => _inner.AutoCompleteCustomSource;
            set
            {
                _inner.AutoCompleteCustomSource = value;
                _liveSuggestions?.Refresh();
            }
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
            UpdateBlendedBackground();
            UpdateInputBackground();
            Invalidate();
        }
        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            UpdateInputBackground();
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
            int left = LogicalToDeviceUnits(TextInsetLeft);
            int right = LogicalToDeviceUnits(TextInsetRight);
            int vertical = LogicalToDeviceUnits(MultilineVerticalInset);
            if (_browseButton is { } browse)
            {
                int inset = LogicalToDeviceUnits(3);
                int size = Math.Max(0, Math.Min(Height - 2 * inset, Width - 2 * inset));
                browse.SetBounds(Width - inset - size, (Height - size) / 2, size, size);
                right += size;
            }
            int innerWidth = Math.Max(0, Width - left - right);
            if (_inner.Multiline)
            {
                int innerHeight =
                    Math.Max(0, Height - vertical * 2);
                _inner.SetBounds(left, vertical, innerWidth, innerHeight);
            }
            else
            {
                int innerHeight =
                    Math.Min(_inner.PreferredHeight, Math.Max(0, Height - 2));
                int top =
                    Math.Max(0, (Height - innerHeight) / 2);
                _inner.SetBounds(left, top, innerWidth, innerHeight);
            }
            Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            if (_blendWithParent)
            {
                if (_invalid || _inner.Focused)
                {
                    using var underline = new Pen(_invalid ? AppTheme.Danger : AppTheme.BorderFocus, 1.5F);
                    e.Graphics.DrawLine(underline, 8, Height - 2, Math.Max(8, Width - 8), Height - 2);
                }
                return;
            }
            e.Graphics.SmoothingMode =
                AppTheme.UseSimpleRendering ? SmoothingMode.None : SmoothingMode.AntiAlias;
            var rect =
                new Rectangle(0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
            using var shape =
                new UiPaintShape(rect, _radius);
            bool locked = !Enabled || ReadOnly;
            using var brush =
                new SolidBrush(
                    locked
                        ? AppTheme.ButtonDisabledBackground
                        : BackColor);
            shape.Fill(e.Graphics, brush);
            var borderColor =
                locked
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
                    !locked && _inner.Focused
                        ? 1.8F
                        : !locked && _invalid
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
        /* FormFlow opt-in makes Enter advance from single-line inputs. */
        private sealed class InnerTextBox : TextBox
        {
            protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
            {
                if (keyData == Keys.Enter && !Multiline && !AcceptsReturn && FormFlow.TryAdvance(this))
                    return true;
                return base.ProcessCmdKey(ref msg, keyData);
            }
        }
    }
}
