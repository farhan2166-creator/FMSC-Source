using System.Diagnostics.CodeAnalysis;
using System.Drawing.Drawing2D;
namespace FMSC_SMS.Core;
/// <summary>The one student finder used by every fee screen.</summary>
public class StudentPicker : Panel, IValidationTarget, IAccessibleInput
{
    private bool _invalid;
    private const int InsetLeft = 8;
    private const int InsetRight = 8;
    private const int MaxRows = 150;
    private const int VisibleRows = 10;
    private const int MaxPopupWidth = 760;
    private const int DropDownButtonWidth = 26;
    private const int CellPadding = 6;
    private const int ColumnGap = 14;
    internal sealed class Entry
    {
        public Entry(
            int id,
            string display,
            int admissionId = 0,
            string[]? cells = null)
        {
            Id = id;
            Display = display;
            AdmissionId = admissionId;
            Cells = cells ?? Array.Empty<string>();
        }
        public int Id { get; }
        public string Display { get; }
        public int AdmissionId { get; }
        /// <summary>One text per popup column, in column order. Empty for the "no match" row.</summary>
        public string[] Cells { get; }
        public override string ToString() => Display;
    }
    /// <summary>One column of the popup list: heading, drawn width, alignment and whether it may shrink.</summary>
    internal sealed class ColumnSpec
    {
        public ColumnSpec(
            string header,
            int maxWidth,
            bool rightAligned = false,
            bool flex = false)
        {
            Header = header;
            MaxWidth = maxWidth;
            RightAligned = rightAligned;
            Flex = flex;
        }
        public string Header { get; }
        public int MaxWidth { get; }
        public bool RightAligned { get; }
        public bool Flex { get; }
        public int Width { get; set; }
    }
    private readonly StudentPickerTextBox _text;
    private readonly ListBox _list;
    private readonly StudentPickerHeaderStrip _header;
    private readonly StudentPickerPopupForm _popup;
    private List<ColumnSpec> _columns = new();
    private bool _showCampusColumn = true;
    // User requirement: registration numbers must stay hidden in student searches.
    // Do not advertise them in prompts or add them to visible result columns.
    public const string SearchPlaceholder = "Admission no., student or father name";
    private int _radius = Math.Max(
        1,
        AppTheme.ControlRadius - 2);
    private bool _hovered;
    private bool _suppress;
    private bool _commitWhenReady;
    private bool _capped;
    private int _matchCount;
    private int _selectedId;
    private string _committedText = string.Empty;
    private readonly StudentPickerSearchSession _searchSession = new();
    private System.Windows.Forms.Timer? _searchDebounce;
    private bool _selectAllOnMouseUp;
    private DataTable? _source;
    private DataTable? _fixedSource;
    private bool _showDropDownButton;
    private bool _dropDownHot;
    private string _noMatchText =
        "No matching student";
    private static readonly DataTable EmptySource = new();
    /// <summary>The operator chose a student.</summary>
    public event EventHandler? UserSelectionChanged;
    /// <summary>The operator edited the text away from the chosen student.</summary>
    public event EventHandler? SelectionCleared;
    /// <summary>Called before a user gesture replaces the committed identity.</summary>
    public Func<int, bool>? SelectionChangeGuard { get; set; }
    /// <summary>The list of matches changed.</summary>
    public event EventHandler? MatchesChanged;
    /// <summary>Reading the student list failed.</summary>
    public event Action<Exception>? SearchFailed;
    public StudentPicker()
    {
        _text = new StudentPickerTextBox {
            BorderStyle = BorderStyle.None,
            Font = AppTheme.NormalFont,
            ForeColor = AppTheme.Text,
            BackColor = AppTheme.SurfaceElevated,
            PlaceholderText = SearchPlaceholder
        };
        _text.OwnsKey = OwnsKey;
        _list = new ListBox {
            BorderStyle = BorderStyle.None,
            IntegralHeight = false,
            DrawMode = DrawMode.OwnerDrawFixed,
            TabStop = false,
            Font = AppTheme.NormalFont,
            BackColor = AppTheme.SurfaceElevated,
            ForeColor = AppTheme.Text,
            ItemHeight = ListItemHeight()
        };
        _list.DrawItem += OnListDrawItem;
        _list.MouseDown += OnListMouseDown;
        _header = new StudentPickerHeaderStrip {
            Height = ListItemHeight()
        };
        _popup = new StudentPickerPopupForm(
            _list,
            _header);
        _text.TextChanged +=
            (_, _) => OnTextEdited();
        _text.KeyDown += OnTextKeyDown;
        /* Regaining focus selects everything, so typing replaces what was there. */
        _text.GotFocus += (_, _) => {
            Invalidate();
            _selectAllOnMouseUp = true;
            _text.SelectAll();
        };
        _text.LostFocus += (_, _) => {
            if (_selectedId > 0 && _text.Text == _committedText)
                ShowTextStart();
            Invalidate();
            if (IsHandleCreated &&
                !IsDisposed)
            {
                BeginInvoke(
                    new Action(() => {
                        if (!IsDisposed &&
                            !_text.Focused &&
                            !_list.Focused)
                        {
                            HidePopup();
                        }
                    }));
            }
        };
        _text.MouseUp += (_, e) => {
            if (e.Button != MouseButtons.Left) return;
            if (_selectAllOnMouseUp)
            {
                _selectAllOnMouseUp = false;
                _text.SelectAll();
            }
        };
        _text.MouseEnter += (_, _) => {
            _hovered = true;
            Invalidate();
        };
        _text.MouseLeave += (_, _) => {
            _hovered = false;
            Invalidate();
        };
        Controls.Add(_text);
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);
        BackColor = AppTheme.SurfaceElevated;
        ForeColor = AppTheme.Text;
        Font = AppTheme.NormalFont;
        Dock = DockStyle.Fill;
        Height = AppMetrics.InputControlHeight;
        Reposition();
        AppTheme.ThemeChanged += OnThemeChanged;
    }
    // Public surface
    /// <summary>The admission id of the chosen student, or 0 when none is chosen.</summary>
    public int SelectedId => _selectedId;
    /// <summary>Optional extra rule for who may be offered.</summary>
    public Func<DataRow, bool>? RowFilter { get; set; }
    /// <summary>The column whose value SelectedId returns.</summary>
    public string IdColumn { get; set; } =
        "AdmissionId";
    /// <summary>Writes the text shown in the box and list for a row.</summary>
    public Func<DataRow, string>? DisplayFormatter { get; set; }
    /// <summary>What the list says when nothing matches.</summary>
    public string NoMatchText
    {
        get => _noMatchText;
        set =>
            _noMatchText =
                string.IsNullOrWhiteSpace(value)
                    ? "No matching student"
                    : value;
    }
    /// <summary>Adds a drop-down arrow at the right of the box.</summary>
    public bool ShowDropDownButton
    {
        get => _showDropDownButton;
        set
        {
            if (_showDropDownButton == value)
                return;
            _showDropDownButton = value;
            Reposition();
            Invalidate();
        }
    }
    /// <summary>Searches this list instead of the cached student list.</summary>
    public void SetSource(DataTable? table)
    {
        _fixedSource = table;
        _source = table;
        _searchSession.Cancel();
        HidePopup();
    }
    /// <summary>The student table the list is filtered from.</summary>
    public DataTable Source =>
        _source ?? EmptySource;
    /// <summary>The row of the chosen student, or null.</summary>
    public DataRow? SelectedRow
    {
        get
        {
            if (_selectedId <= 0)
                return null;
            DataTable table = Source;
            if (!table.Columns.Contains(IdColumn))
                return null;
            foreach (DataRow row in table.Rows)
            {
                if (ReadInt(row, IdColumn) ==
                    _selectedId)
                {
                    return row;
                }
            }
            return null;
        }
    }
    /// <summary>How many students the current text matches.</summary>
    public int MatchCount =>
        _matchCount;
    /// <summary>True when more students matched than the list shows.</summary>
    public bool MatchesCapped =>
        _capped;
    /// <summary>Standard operator feedback for the current search result.</summary>
    public string MatchStatusText =>
        _matchCount == 0
            ? $"No {EntityPlural} found."
            : _capped
                ? $"{MaxRows}+ {EntityPlural} match - keep typing to narrow the list."
                : $"{_matchCount} {EntityPlural} found. Click one, or use Up/Down and Enter.";
    public string PlaceholderText
    {
        get => _text.PlaceholderText;
        set =>
            _text.PlaceholderText = value;
    }
    /// <summary>The text in the box. Setting it clears any chosen student and never starts a search.</summary>
    [AllowNull]
    public override string Text
    {
        get =>
            _text?.Text ?? string.Empty;
        set
        {
            if (_text == null)
                return;
            _suppress = true;
            try
            {
                _text.Text =
                    value ?? string.Empty;
            }
            finally
            {
                _suppress = false;
            }
            _selectedId = 0;
            _committedText = string.Empty;
            _searchSession.Cancel();
            HidePopup();
        }
    }
    public new bool Focused =>
        _text.Focused;
    public new bool Focus() =>
        _text.Focus();
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
    /// <summary>The text box inside is what a screen reader lands on.</summary>
    public void ApplyAccessibleInfo(string? name, string? description)
    {
#if FMSC_LEVEL2
        _text.Name = Level2Target.InputId(name);
#endif
        _text.AccessibleName = name;
        _text.AccessibleDescription = description;
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (string.IsNullOrEmpty(_text.AccessibleName) && !string.IsNullOrEmpty(AccessibleName))
            _text.AccessibleName = AccessibleName;
        if (string.IsNullOrEmpty(_text.AccessibleDescription) && !string.IsNullOrEmpty(AccessibleDescription))
            _text.AccessibleDescription = AccessibleDescription;
    }
    public void SelectAll() =>
        _text.SelectAll();
    /// <summary>Puts the cursor in the box and lists matches.</summary>
    public void ShowMatches()
    {
        _text.Focus();
        if (_text.Text.Trim().Length > 0)
            RunSearch(false);
    }
    /// <summary>Clears the box and selected student.</summary>
    public void Clear()
    {
        Text = string.Empty;
        _matchCount = 0;
        _capped = false;
        Invalidate();
    }
    /// <summary>Shows a student as selected without raising UserSelectionChanged.</summary>
    public void SetStudent(
        int admissionId,
        string display)
    {
        _suppress = true;
        try
        {
            _text.Text = display;
            _selectedId = admissionId;
            _committedText =
                _text.Text;
            ShowTextStart();
            _selectAllOnMouseUp = true;
        }
        finally
        {
            _suppress = false;
        }
        _searchSession.Cancel();
        HidePopup();
        Invalidate();
    }
    /// <summary>Same as SetStudent(int,string), using a DataRow.</summary>
    public void SetStudent(DataRow row)
    {
        _source = row.Table;
        SetStudent(
            ReadInt(row, IdColumn),
            DisplayText(row));
    }
    private string DisplayText(DataRow row) =>
        DisplayFormatter?.Invoke(row) ??
        DisplayFor(row);
    /// <summary>Adds a Campus column to the popup list.</summary>
    public bool ShowCampusColumn
    {
        get => _showCampusColumn;
        set => _showCampusColumn = value;
    }
    /// <summary>Short text shown in the box after a student is chosen. Example: 1234 - Ali Raza s/o Imran Raza</summary>
    public static string DisplayFor(DataRow row)
        => StudentPickerRowProjector.DisplayFor(row);
    /// <summary>Popup cells: 0 = Adm. ID 1 = Student 2 = Father 3 = Class / Section 4 = Campus 5 = Status Registration number is intentionally not displayed.</summary>
    internal virtual string[] CellsFor(DataRow row)
        => StudentPickerRowProjector.CellsFor(row, _showCampusColumn);
    /// <summary>Chooses and sizes popup columns.</summary>
    private void BuildColumns(
        List<Entry> entries)
    {
        _columns = PlanColumns(entries, _list.Font);
        _header.Columns = _columns;
    }
    internal virtual string EntityPlural => "students";
    internal virtual bool FillFromHighlightedRow => false;
    internal virtual Task<DataTable> LoadSourceAsync() => StudentSearchCache.GetAllAsync();
    internal virtual List<ColumnSpec> PlanColumns(List<Entry> entries, Font font)
        => StudentPickerColumnPlanner.Apply(entries, font);
    internal virtual StudentPickerMatchBuilder.Result FindMatches(DataTable all, string term)
        => StudentPickerMatchBuilder.Build(all, term, MaxRows, RowFilter, IdColumn, _selectedId, DisplayText, CellsFor);
    // Searching
    private void OnTextEdited()
    {
        if (_suppress)
            return;
        _selectAllOnMouseUp = false;
        if (_selectedId > 0 &&
            !string.Equals(
                _text.Text,
                _committedText,
                StringComparison.Ordinal))
        {
            if (SelectionChangeGuard?.Invoke(0) == false)
            {
                SetStudent(_selectedId, _committedText);
                return;
            }
            _selectedId = 0;
            _committedText =
                string.Empty;
            SelectionCleared?.Invoke(
                this,
                EventArgs.Empty);
        }
        // Invalidate and hide the old matches immediately: Enter must never
        // commit a highlighted result belonging to the previous typed term.
        HidePopup();
        if (_text.Text.Trim().Length == 0) SetMatches(0, false);
        else
        {
            if (_searchDebounce is null)
            {
                _searchDebounce = new System.Windows.Forms.Timer { Interval = 180 };
                _searchDebounce.Tick += (_, _) => {
                    _searchDebounce?.Stop();
                    if (!IsDisposed && Visible && Enabled && _text.Focused) RunSearch(false);
                };
            }
            _searchDebounce.Start();
        }
        OnTextChanged(
            EventArgs.Empty);
    }
    private void RunSearch(
        bool commitWhenReady,
        bool listAll = false)
    {
        _searchDebounce?.Stop();
        /* listAll is the drop-down: every row of the list, regardless of what is typed. */
        string term =
            listAll
                ? string.Empty
                : _text.Text.Trim();
        _commitWhenReady =
            commitWhenReady;
        if (term.Length == 0 &&
            !listAll)
        {
            _searchSession.Cancel();
            HidePopup();
            SetMatches(
                0,
                false);
            return;
        }
        _searchSession.Begin(
            _fixedSource,
            term,
            listAll,
            () => _text.Text.Trim(),
            () => IsDisposed,
            ApplyResults,
            ex => { ShowSearchMessage($"Could not load {EntityPlural}. Type again to retry."); SearchFailed?.Invoke(ex); },
            () => ShowSearchMessage($"Loading {EntityPlural}..."), LoadSourceAsync);
    }
    private void ShowSearchMessage(string message)
    {
        if (IsDisposed) return;
        BuildColumns(new List<Entry>());
        _list.Items.Clear();
        _list.Items.Add(new Entry(0, message));
        _list.SelectedIndex = -1;
        SetMatches(0, false);
        ShowPopup();
    }
    private void ApplyResults(
        DataTable all,
        string term)
    {
        _source = all;
        StudentPickerMatchBuilder.Result result = FindMatches(all, term);
        List<Entry> entries = result.Entries;
        BuildColumns(entries);
        int highlight = result.Highlight;
        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            if (entries.Count == 0)
            {
                _list.Items.Add(
                    new Entry(
                        0,
                        _noMatchText));
            }
            else
            {
                _list.Items.AddRange(
                    entries.ToArray());
            }
            _list.SelectedIndex =
                highlight;
        }
        finally
        {
            _list.EndUpdate();
        }
        SetMatches(
            entries.Count,
            result.Capped);
        if (_commitWhenReady)
        {
            _commitWhenReady = false;
            if (highlight >= 0)
            {
                Commit(
                    entries[highlight]);
                return;
            }
        }
        ShowPopup();
    }
    private void SetMatches(
        int count,
        bool capped)
    {
        _matchCount = count;
        _capped = capped;
        MatchesChanged?.Invoke(
            this,
            EventArgs.Empty);
    }
    // Choosing
    private void Commit(Entry entry, bool keepPopupOpen = false)
    {
        if (entry.Id <= 0)
            return;
        if (entry.Id != _selectedId && SelectionChangeGuard?.Invoke(entry.Id) == false)
            return;
        _suppress = true;
        try
        {
            _text.Text =
                entry.Display;
            _selectedId =
                entry.Id;
            _committedText =
                _text.Text;
            ShowTextStart();
        }
        finally
        {
            _suppress = false;
        }
        _searchSession.Cancel();
        if (!keepPopupOpen) HidePopup();
        if (!_text.Focused)
            _text.Focus();
        ShowTextStart();
        // Choosing a result can retain focus. The next click starts a fresh search.
        _selectAllOnMouseUp = true;
        Invalidate();
        UserSelectionChanged?.Invoke(
            this,
            EventArgs.Empty);
    }
    private void ShowTextStart()
    {
        _text.SelectionStart = 0;
        _text.SelectionLength = 0;
        _text.ScrollToCaret();
    }
    private void OnListMouseDown(
        object? sender,
        MouseEventArgs e)
    {
        int index =
            _list.IndexFromPoint(
                e.Location);
        if (index >= 0 &&
            index < _list.Items.Count &&
            _list.Items[index] is Entry entry)
        {
            Commit(entry);
        }
    }
    private bool OwnsKey(
        Keys keyData)
    {
        if ((keyData &
             (Keys.Control |
              Keys.Alt)) != 0)
        {
            return false;
        }
        switch (keyData &
                Keys.KeyCode)
        {
            case Keys.Down:
                return
                    PopupOpen ||
                    _text.TextLength > 0 ||
                    _showDropDownButton;
            case Keys.F4:
                return
                    _showDropDownButton;
            case Keys.Up:
            case Keys.PageDown:
            case Keys.PageUp:
                return
                    PopupOpen;
            case Keys.Escape:
                return PopupOpen || _searchDebounce?.Enabled == true;
            case Keys.Enter:
                return
                    PopupOpen ||
                    _text.Text.Trim().Length > 0;
            default:
                return false;
        }
    }
    private void OnTextKeyDown(
        object? sender,
        KeyEventArgs e)
    {
        _selectAllOnMouseUp = false;
        bool consumed = false;
        switch (e.KeyCode)
        {
            case Keys.Down:
                consumed = MoveSelection(1);
                break;
            case Keys.Up:
                consumed = MoveSelection(-1);
                break;
            case Keys.PageDown:
                consumed =
                    MoveSelection(VisibleRows);
                break;
            case Keys.PageUp:
                consumed =
                    MoveSelection(-VisibleRows);
                break;
            case Keys.Escape:
                if (PopupOpen || _searchDebounce?.Enabled == true)
                {
                    HidePopup();
                    consumed = true;
                }
                break;
            case Keys.F4:
                if (_showDropDownButton)
                {
                    ToggleDropDown();
                    consumed = true;
                }
                break;
            case Keys.Enter:
                consumed =
                    HandleEnter();
                break;
        }
        if (consumed)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        OnKeyDown(e);
    }
    private bool MoveSelection(int delta)
    {
        if (!PopupOpen)
        {
            bool blank =
                _text.Text.Trim().Length == 0;
            if (blank &&
                !_showDropDownButton)
            {
                return false;
            }
            /* With a drop-down, Down on an empty box or already-selected student opens all rows. */
            RunSearch(
                false,
                listAll:
                    _showDropDownButton &&
                    (blank || _selectedId > 0));
            return true;
        }
        int count =
            _list.Items.Count;
        if (count == 0 ||
            (count == 1 &&
             _list.Items[0] is Entry { Id: 0 }))
        {
            return true;
        }
        int next =
            _list.SelectedIndex < 0
                ? delta > 0
                    ? 0
                    : count - 1
                : Math.Clamp(
                    _list.SelectedIndex + delta,
                    0,
                    count - 1);
        _list.SelectedIndex =
            next;
        if (FillFromHighlightedRow && _list.SelectedItem is Entry entry)
            Commit(entry, keepPopupOpen: true);
        return true;
    }
    private bool HandleEnter()
    {
        /* Highlighted row is the operator's choice. */
        if (PopupOpen &&
            _list.SelectedItem is Entry picked &&
            picked.Id > 0)
        {
            Commit(picked);
            return true;
        }
        /* Enter on the student already chosen reloads that student. */
        if (_selectedId > 0)
        {
            HidePopup();
            UserSelectionChanged?.Invoke(
                this,
                EventArgs.Empty);
            return true;
        }
        /* Typed text: pick exact admission number or only match. */
        if (_text.Text.Trim().Length > 0)
        {
            RunSearch(true);
            return true;
        }
        return false;
    }
    /// <summary>Opens the complete list, or closes it when it is already open.</summary>
    private void ToggleDropDown()
    {
        if (!_showDropDownButton ||
            !Enabled)
        {
            return;
        }
        if (PopupOpen)
        {
            HidePopup();
            _text.Focus();
            return;
        }
        _text.Focus();
        RunSearch(
            false,
            listAll: true);
    }
    private Rectangle DropDownRect =>
        new(
            Math.Max(
                0,
                Width -
                AppScale.Scale(
                    DropDownButtonWidth)),
            0,
            AppScale.Scale(
                DropDownButtonWidth),
            Height);
    protected override void OnMouseDown(
        MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button ==
                MouseButtons.Left &&
            _showDropDownButton &&
            DropDownRect.Contains(
                e.Location))
        {
            ToggleDropDown();
        }
    }
    protected override void OnMouseMove(
        MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool hot =
            _showDropDownButton &&
            DropDownRect.Contains(
                e.Location);
        if (hot != _dropDownHot)
        {
            _dropDownHot = hot;
            Cursor =
                hot
                    ? Cursors.Hand
                    : Cursors.Default;
            Invalidate();
        }
    }
    protected override void OnMouseLeave(
        EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_dropDownHot)
        {
            _dropDownHot = false;
            Cursor =
                Cursors.Default;
            Invalidate();
        }
    }
    // Popup
    private bool PopupOpen =>
        _popup.Visible;
    private static int ListItemHeight() =>
        AppMetrics.TextHeight(
            AppTheme.NormalFont) + 10;
    private void ShowPopup()
    {
        if (IsDisposed ||
            !IsHandleCreated ||
            !_text.Focused)
        {
            return;
        }
        int rows =
            Math.Min(
                Math.Max(
                    _list.Items.Count,
                    1),
                VisibleRows);
        int itemHeight =
            _list.ItemHeight;
        /* Real rows have a heading strip. "No match" is just one plain line. */
        bool hasRows =
            _list.Items.Count > 0 &&
            _list.Items[0] is Entry { Id: > 0 };
        _header.Visible =
            hasRows;
        _header.Height =
            itemHeight;
        int scrollBar =
            _list.Items.Count > VisibleRows
                ? SystemInformation.VerticalScrollBarWidth
                : 0;
        int maxWidth =
            Math.Min(
                AppScale.Scale(
                    MaxPopupWidth),
                Screen
                    .FromControl(this)
                    .WorkingArea.Width - 16);
        int columnsWidth =
            CellPadding * 2 +
            _columns.Sum(
                c => c.Width) +
            Math.Max(
                0,
                _columns.Count - 1) *
            ColumnGap;
        int widest =
            hasRows
                ? columnsWidth + scrollBar
                : UiTextWidthCache.Measure(
                    _noMatchText,
                    _list.Font) + 24;
        int width =
            Math.Min(
                maxWidth,
                Math.Max(
                    Width,
                    widest));
        /* If there is not enough room, flexible name columns shrink first. */
        if (hasRows &&
            columnsWidth + scrollBar > width)
        {
            int excess =
                columnsWidth +
                scrollBar -
                width;
            var flex =
                _columns
                    .Where(c => c.Flex)
                    .ToList();
            int flexTotal =
                flex.Sum(
                    c => c.Width);
            if (flexTotal > 0)
            {
                foreach (ColumnSpec column in flex)
                {
                    int cut =
                        (int)Math.Ceiling(
                            excess *
                            (column.Width /
                             (double)flexTotal));
                    column.Width =
                        Math.Max(
                            60,
                            column.Width - cut);
                }
            }
        }
        int height =
            rows * itemHeight +
            2 +
            (hasRows
                ? itemHeight
                : 0);
        _header.Invalidate();
        Rectangle work =
            Screen
                .FromControl(this)
                .WorkingArea;
        Point below =
            PointToScreen(
                new Point(
                    0,
                    Height));
        Point location =
            below;
        if (below.Y + height >
            work.Bottom)
        {
            location.Y =
                Math.Max(
                    work.Top,
                    PointToScreen(
                        Point.Empty).Y -
                    height);
        }
        if (location.X + width >
            work.Right)
        {
            location.X =
                Math.Max(
                    work.Left,
                    work.Right - width);
        }
        _popup.SetBounds(
            location.X,
            location.Y,
            width,
            height);
        if (!_popup.Visible)
        {
            Form? owner =
                FindForm();
            if (owner is not null)
                _popup.Show(owner);
            else
                _popup.Show();
        }
    }
    private void HidePopup()
    {
        _searchDebounce?.Stop();
        _searchSession.Cancel();
        _commitWhenReady = false;
        if (!IsDisposed &&
            !_popup.IsDisposed &&
            _popup.Visible)
        {
            _popup.Hide();
        }
    }
    private void OnListDrawItem(
        object? sender,
        DrawItemEventArgs e)
    {
        if (e.Index < 0 ||
            e.Index >= _list.Items.Count ||
            _list.Items[e.Index] is not Entry entry)
        {
            return;
        }
        StudentPickerRowRenderer.Draw(e, entry, _columns, _list.Font);
        return;
    }
    // Look and layout
    private void OnThemeChanged(
        object? sender,
        EventArgs e)
    {
        if (IsDisposed ||
            Disposing)
        {
            return;
        }
        Font = UiDpi.ForControl(this, AppTheme.NormalFont);
        ForeColor =
            AppTheme.Text;
        BackColor =
            AppTheme.SurfaceElevated;
        _text.Font = Font;
        _text.ForeColor =
            AppTheme.Text;
        _text.BackColor =
            AppTheme.SurfaceElevated;
        _list.Font = UiDpi.ForControl(_list, AppTheme.NormalFont);
        _list.ForeColor =
            AppTheme.Text;
        _list.BackColor =
            AppTheme.SurfaceElevated;
        _list.ItemHeight =
            ListItemHeight();
        _header.Height =
            _list.ItemHeight;
        _header.Invalidate();
        _popup.BackColor =
            AppTheme.InputBorder;
        UiTheme.ApplyControl(this);
        Reposition();
        Invalidate();
    }
    protected override void Dispose(
        bool disposing)
    {
        if (disposing)
        {
            _searchDebounce?.Dispose();
            _searchDebounce = null;
            _searchSession.Cancel();
            AppTheme.ThemeChanged -=
                OnThemeChanged;
            _popup.Dispose();
        }
        base.Dispose(disposing);
    }
    protected override void OnVisibleChanged(
        EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible)
            HidePopup();
    }
    protected override void OnEnabledChanged(
        EventArgs e)
    {
        base.OnEnabledChanged(e);
        if (!Enabled)
            HidePopup();
        Invalidate();
    }
    protected override void OnFontChanged(
        EventArgs e)
    {
        base.OnFontChanged(e);
        if (_text == null)
            return;
        _text.Font = Font;
        Reposition();
    }
    protected override void OnResize(
        EventArgs e)
    {
        base.OnResize(e);
        Reposition();
    }
    protected override void SetBoundsCore(
        int x,
        int y,
        int width,
        int height,
        BoundsSpecified specified)
    {
        base.SetBoundsCore(
            x,
            y,
            width,
            AppMetrics.InputControlHeight,
            specified);
    }
    private void Reposition()
    {
        if (_text == null ||
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
        int arrow =
            _showDropDownButton
                ? AppScale.Scale(
                    DropDownButtonWidth)
                : 0;
        Rectangle textBounds = StudentPickerGeometry.TextBounds(
            ClientSize, _text.PreferredHeight, InsetLeft, InsetRight, arrow);
        _text.Bounds = textBounds;
        Invalidate();
    }
    protected override void OnPaint(
        PaintEventArgs e)
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
                Math.Max(
                    0,
                    Height - 1));
        using var shape = new UiPaintShape(rect, _radius);
        using var brush =
            new SolidBrush(
                Enabled
                    ? AppTheme.SurfaceElevated
                    : AppTheme.ButtonDisabledBackground);
        shape.Fill(e.Graphics, brush);
        Color border =
            !Enabled
                ? AppTheme.InputBorder
                : _invalid
                    ? AppTheme.Danger
                    : _text.Focused
                        ? AppTheme.BorderFocus
                        : _hovered
                            ? AppTheme.InputBorderHover
                            : AppTheme.InputBorder;
        using var pen =
            new Pen(
                border,
                Enabled && _text.Focused
                    ? 1.8F
                    : Enabled && _invalid
                        ? 1.5F
                        : 1F) {
                Alignment =
                    PenAlignment.Inset
            };
        shape.Draw(e.Graphics, pen);
        if (_showDropDownButton)
        {
            Rectangle arrow =
                DropDownRect;
            using var divider =
                new Pen(
                    AppTheme.InputBorder,
                    1F);
            e.Graphics.DrawLine(
                divider,
                arrow.Left,
                5,
                arrow.Left,
                Math.Max(
                    5,
                    Height - 6));
            int cx =
                arrow.Left +
                arrow.Width / 2;
            int cy =
                Height / 2;
            using var chevron =
                new Pen(
                    Enabled
                        ? (_dropDownHot
                            ? AppTheme.Primary
                            : AppTheme.MutedText)
                        : AppTheme.InputBorder,
                    1.8F) {
                    StartCap =
                        LineCap.Round,
                    EndCap =
                        LineCap.Round
                };
            e.Graphics.DrawLines(
                chevron,
                new[]
                {
                    new Point(
                        cx - 4,
                        cy - 2),
                    new Point(
                        cx,
                        cy + 2),
                    new Point(
                        cx + 4,
                        cy - 2)
                });
        }
    }
    private static GraphicsPath CreateRoundedPath(
        Rectangle rect,
        int radius)
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
        path.AddArc(
            rect.X,
            rect.Y,
            diameter,
            diameter,
            180,
            90);
        path.AddArc(
            rect.Right - diameter,
            rect.Y,
            diameter,
            diameter,
            270,
            90);
        path.AddArc(
            rect.Right - diameter,
            rect.Bottom - diameter,
            diameter,
            diameter,
            0,
            90);
        path.AddArc(
            rect.X,
            rect.Bottom - diameter,
            diameter,
            diameter,
            90,
            90);
        path.CloseFigure();
        return path;
    }
    private static int ReadInt(
        DataRow row,
        string column)
    {
        if (!row.Table.Columns.Contains(column) ||
            row[column] == DBNull.Value)
        {
            return 0;
        }
        return int.TryParse(
            Convert.ToString(
                row[column]),
            out int value)
            ? value
            : 0;
    }
}
