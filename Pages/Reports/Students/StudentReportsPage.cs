using FMSC_SMS.Core;
using Microsoft.Data.SqlClient;
using FMSC_SMS.Pages.Students.Admissions;
using FMSC_SMS.Pages.Reports.Finance;
namespace FMSC_SMS.Pages.Reports.Students;
/// <summary>One student report, built by the person using it. The four old pages remain only as shells that open this one on the matching preset.</summary>
public partial class StudentReportsPage : V2Page
{
    internal const string ModeList = "list";
    internal const string ModeAdmissions = "admissions";
    internal const string ModeStrength = "strength";
    internal const string ModeChanges = "changes";
    private const string ViewsKey = "students";
    private const string LastScopeKey = "students.last-scope";
    private bool _scopeEdited;
    private const string NotSet = "(not set)";
    private sealed class Choice
    {
        public Choice(string key, string text)
        {
            Key = key;
            Text = text;
        }
        public string Key { get; }
        public string Text { get; }
        public override string ToString() => Text;
    }
    /// <summary>The filter values a run was started with, read on the UI thread.</summary>
    private sealed record RunArgs(
        string Mode,
        int CampusId,
        int YearId,
        int CompareYearId,
        int ClassId,
        int SectionId,
        string Status,
        string Gender,
        string Search,
        bool ByAdmissionId,
        string AdmissionNumber,
        bool LimitDate,
        DateTime From,
        DateTime To,
        bool IncludeFormerStudents);
    /// <summary>How the loaded rows are to be arranged. Changing any of it never needs the database.</summary>
    private sealed record ViewArgs(
        string Mode,
        string GroupKey,
        string SplitKey,
        string SortKey,
        string ChangeType,
        string ClientSearch,
        string YearName,
        string CompareName,
        string Transport, string Completion, bool CanViewDetails);
    private sealed record LoadResult(DataTable Raw, DataTable? Compare, bool HasBalance);
    // Report choice and column layouts survive Reset Filters; arrangements return to the mode's preset.
    private readonly ModernComboBox mode = NewCombo();
    private readonly ModernComboBox group = NewCombo();
    private readonly ModernComboBox split = NewCombo();
    private readonly ModernComboBox sort = NewCombo();
    private readonly ModernComboBox views = NewCombo();
    private readonly ModernComboBox transportFilter = NewCombo();
    private readonly ModernComboBox completionFilter = NewCombo();
    private readonly ModernComboBox strengthScope = NewCombo();
    // filters
    private readonly LookupBox campus = new();
    private readonly LookupBox year = new();
    private readonly LookupBox compareYear = new();
    private readonly LookupBox cls = new();
    private readonly LookupBox section = new();
    private readonly LookupBox status = new();
    private readonly LookupBox gender = new();
    private readonly LookupBox changeType = new();
    private readonly ModernTextBox search = HumanUi.TextBox(false, 250);
    private readonly CheckBox limitDate = new ModernCheckBox {
        Text = "Limit by date",
        OptionStyle = true,
        AutoSize = true
    };
    private readonly ModernDatePicker from = new() {
        Format = DateTimePickerFormat.Custom,
        CustomFormat = "dd-MM-yyyy",
        Value = DateTime.Today.AddMonths(-1)
    };
    private readonly ModernDatePicker to = new() {
        Format = DateTimePickerFormat.Custom,
        CustomFormat = "dd-MM-yyyy",
        Value = DateTime.Today
    };
    private readonly CheckBox inactive = new ModernCheckBox {
        Text = "Include former students",
        OptionStyle = true,
        AutoSize = true
    };
    private readonly ModernButton exportButton;
    private readonly ModernButton printButton;
    private readonly ModernButton deleteViewButton;
    private readonly Label guide = new() {
        Dock = DockStyle.Fill,
        AutoEllipsis = true,
        Font = AppTheme.NormalFont,
        ForeColor = AppTheme.MutedText,
        TextAlign = ContentAlignment.MiddleLeft
    };
    /* The filter area is a wrapping flow of captioned fields; a field the chosen report does not use is hidden (not greyed). */
    private readonly Dictionary<Control, Panel> _fieldOf = new();
    private readonly List<(Panel Field, bool IsFilter)> _fieldList = new();
    private readonly HashSet<Panel> _hiddenFields = new();
    private TableLayoutPanel? _filterHost;
    private int _fieldHeight;
    /* The valid campus / class / section combinations, so the three lists can narrow each other. */
    private DataTable? _combos;
    private DataTable? _allClasses;
    private DataTable? _allSections;
    private bool _cascading;
    private FormValidator? rangeValidator;
    private DataTable? _years;
    private string _mode;
    private bool _suppress;
    private int _comparisonBaseYearId = -1;
    private bool _busyViews;
    private bool _openingAdmissions;
    private ModernButton? openAdmissionsButton;
    /* The rows as loaded (raw), the second year when comparing, and whether the outstanding balance was fetched with them. */
    private DataTable? _raw;
    private DataTable? _compareRaw;
    private bool _rawHasBalance;
    /* The filter signature the rows in the grid answer. Empty until a report has been run. */
    private string _appliedSignature = string.Empty;
    private int _reportRevision;
    private string _filterText = string.Empty;
    private string _countText = string.Empty;
    private ReportShape _shape;
    public StudentReportsPage()
        : this(ModeList, "Student Reports", string.Empty)
    {
    }
    protected StudentReportsPage(string startMode, string title, string context)
        : base(title, string.IsNullOrWhiteSpace(context) ? ModeContext(startMode) : context)
    {
        _mode = startMode;
        search.PlaceholderText = "Admission Number / student name";
        var viewReport = AddBusyAction("View Report", RunAsync, "Primary");
        var classSummary = AddBusyAction("Class Summary A4", PreviewClassSummaryAsync, "Secondary");
        var columnsButton = AddColumnsAction();
        var saveView = AddAction("Save View", (_, _) => SaveCurrentView(), "Secondary");
        renameViewButton=AddAction("Rename View",(_,_)=>RenameSelectedView(),"Secondary");
        openAdmissionsButton = AddBusyAction("Open Admissions", OpenSelectedAdmissionsAsync, "Secondary");
        openAdmissionsButton.Enabled = false;
        openAdmissionsButton.EnabledChanged += (_, _) => {
            if (openAdmissionsButton.Enabled && SelectedAdmissionId() <= 0) openAdmissionsButton.Enabled = false;
        };
        var resetFilters = AddAction("Reset Filters", (_, _) => ResetFilters(), "Secondary");
        deleteViewButton = AddAction("Delete View", (_, _) => DeleteSelectedView(), "Secondary");
        var export = AddExportAction("FMSC_Student_Report");
        printButton = AddBusyAction("Print Preview", PreviewReportAsync, "Secondary");
        printButton.Enabled = false;
        RelocateFromActions(viewReport, resetFilters, columnsButton, printButton, saveView, renameViewButton, deleteViewButton, export, openAdmissionsButton, classSummary);
        exportButton = export;
        deleteViewButton.Enabled = false;
        renameViewButton.Enabled=false;
        datePeriod.Items.AddRange(ReportDatePeriod.Choices);datePeriod.SelectedItem="All dates";
        Disposed+=(_,_)=>limitDate.Dispose();
        foreach (var (key, text) in ModeChoices)
            mode.Items.Add(new Choice(key, text));
        _suppress = true;
        try
        {
            FillChoices(strengthScope, new[] {
                new Choice("active", "Active placements"),
                new Choice("history", "All recorded placements"),
                new Choice("left", "Former placements only")
            }, "active");
            SelectKey(mode, _mode);
            FillChoices(transportFilter, new[] {
                new Choice("", "All students"), new Choice("yes", "Uses transport"), new Choice("no", "No transport")
            }, "");
            var completionChoices = new List<Choice> { new("", "All records"), new("any", "Any missing detail"),
                new("contact", CurrentUser.HasPermission("Students.View") ? "Missing contact number" : "Missing primary contact") };
            if (CurrentUser.HasPermission("Students.View"))
                completionChoices.AddRange(new[] { new Choice("birth", "Missing date of birth"), new Choice("address", "Missing address") });
            FillChoices(completionFilter, completionChoices, "");
            RebuildGroupChoices();
            ApplyPreset(_mode, keepFilters: true);
            RefreshViewChoices(string.Empty);
            UpdateEnabledState();
        }
        finally
        {
            _suppress = false;
        }
        _shape = ComputeShape(ReadView(), Array.Empty<string>());
        var filters = BuildFilters();
        UiHelper.SetGridPlaceholderMessage(Grid, "Choose your filters above, then press View Report.");
        var root = ReportUi.Layout(
            filters,
            Grid,
            "Student Reports",
            viewReport,
            resetFilters,
            columnsButton,
            printButton,
            saveView,
            renameViewButton,
            deleteViewButton,
            export,
            openAdmissionsButton,
            classSummary);
        SetContent(root);
        Defer(BindLookupsAsync);
        foreach (var lookup in new[] { campus, year, cls, section })
            lookup.UserSelectionChanged += (_, _) => _scopeEdited = true;
        Grid.SelectionChanged += (_, _) => UpdateAdmissionsAction();
        WireEvents();
        UpdateEnabledState();
    }
    private async Task PreviewClassSummaryAsync()
    {
        if (!CurrentUser.HasPermission("Reports.View")) return;
        int campusId = campus.SelectedId, yearId = year.SelectedId;
        string scope = $"Campus: {(campusId == 0 ? "All campuses" : campus.Text)} | Academic year: {(yearId == 0 ? "All years" : year.Text)}";
        DateTime generated = DateTime.Now;
        try
        {
            var studentsTask = DatabaseHelper.QueryUserAsync("GetStudentRegister",
                DatabaseHelper.Int("@CampusId", campusId), DatabaseHelper.Int("@AcademicYearId", yearId),
                DatabaseHelper.Int("@ClassId", 0), DatabaseHelper.Int("@SectionId", 0),
                DatabaseHelper.P("@EnrollmentStatus", "Active"), DatabaseHelper.Bit("@IncludeFormerStudents", true),
                DatabaseHelper.Bit("@IncludeBalance", false));
            var balancesTask = Task.Run(() => DatabaseHelper.QuerySql(ClassOutstandingReportData.Query,
                DatabaseHelper.Int("@UserId", CurrentUser.UserId), DatabaseHelper.Int("@CampusId", campusId),
                DatabaseHelper.Int("@AcademicYearId", yearId), DatabaseHelper.Int("@ClassId", 0), DatabaseHelper.Int("@SectionId", 0)));
            var schoolTask = InstitutionProfile.GetNameAsync();
            await Task.WhenAll(studentsTask, balancesTask, schoolTask);
            if (IsDisposed) return;
            if (campusId != campus.SelectedId || yearId != year.SelectedId)
            { Warn("Campus or year changed. Open Class Summary A4 again for the selected filters."); return; }
            string school = await schoolTask;
            if (string.IsNullOrWhiteSpace(school)) { Warn("Check School Profile before printing the summary."); return; }
            var data = ClassSectionSummaryData.Create(await studentsTask, await balancesTask, school, scope, generated);
            if (data.Rows.Count == 0) { Info("No active students or outstanding charges were found for this campus and year."); return; }
            RdlcReportPreview.Show(FindForm(), "Class & Section Summary",
                Path.Combine(AppContext.BaseDirectory, "Reports", "ClassSectionSummary.rdlc"), "ClassSummaryData", data);
        }
        catch (Exception ex) { if (!IsDisposed) Fail(ex); }
    }

    private int SelectedAdmissionId()
    {
        if (_openingAdmissions || _mode != ModeList || _shape.Grouped ||
            !CurrentUser.HasPermission("Students.View") ||
            !(CurrentUser.HasPermission("Students.Admit") || CurrentUser.HasPermission("Students.Administer")) ||
            _appliedSignature.Length == 0 || !string.Equals(_appliedSignature, FilterSignature(), StringComparison.Ordinal) ||
            Grid.SelectedRows.Count != 1 || Grid.SelectedRows[0].DataBoundItem is not DataRowView selected ||
            !selected.Row.Table.Columns.Contains("AdmissionId") || selected.Row.IsNull("AdmissionId")) return 0;
        return Convert.ToInt32(selected.Row["AdmissionId"]);
    }

    private void UpdateAdmissionsAction()
    {
        if (openAdmissionsButton is not null) openAdmissionsButton.Enabled = SelectedAdmissionId() > 0;
    }

    private async Task OpenSelectedAdmissionsAsync()
    {
        int admissionId = SelectedAdmissionId();
        if (admissionId <= 0) return;
        _openingAdmissions = true;
        UpdateAdmissionsAction();
        try
        {
            using var dialog = new ApplicationWindowForm {
                Text = "Admissions", StartPosition = FormStartPosition.CenterParent,
                Size = new Size(AppScale.Scale(1200), AppScale.Scale(800)),
                MinimumSize = new Size(AppScale.Scale(980), AppScale.Scale(620)),
                BackColor = AppTheme.PageBackground
            };
            var page = new StudentAdmissionPage(preselectAdmissionId: admissionId) { Dock = DockStyle.Fill };
            dialog.Controls.Add(page);
            dialog.FormClosing += (_, args) => {
                if (page.IsBusy)
                {
                    args.Cancel = true;
                    ModernMessageDialog.ShowWarning(dialog, page.BusyMessage);
                    return;
                }
                args.Cancel = !ModernMessageDialog.ConfirmDiscardIfDirty(dialog,
                    () => page.HasUnsavedChanges, page.UnsavedChangesMessage);
            };
            dialog.ShowDialog(FindForm());
            if (!IsDisposed) await RunAsync();
        }
        catch (Exception ex) { if (!IsDisposed) Fail(ex); }
        finally { _openingAdmissions = false; if (!IsDisposed) UpdateAdmissionsAction(); }
    }

    private static ModernComboBox NewCombo() => new() {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Dock = DockStyle.Fill,
        Height = AppMetrics.InputControlHeight,
        Font = AppTheme.NormalFont
    };
    private const int FilterColumns = 6;
    private const int FilterRows = 2;
    private void AddField(string caption, Control control, bool isFilter)
    {
        Panel field = ReportUi.Field(caption, control);
        field.Margin = new Padding(0);
        _fieldOf[control] = field;
        _fieldList.Add((field, isFilter));
        _filterHost!.Controls.Add(field);
    }
    /* The filter area has a fixed shape - a guide line, one row for how the report is arranged, and two rows of six cells for the filters. */
    private Control BuildFilters()
    {
        _fieldHeight = Math.Max(16, AppMetrics.TextHeight(AppTheme.NormalFont)) + AppMetrics.InputControlHeight + 6;
        int guideHeight = Math.Max(20, AppMetrics.TextHeight(AppTheme.NormalFont) + 8);
        var host = new TableLayoutPanel {
            Dock = DockStyle.Top,
            ColumnCount = FilterColumns,
            RowCount = 2 + FilterRows,
            Margin = new Padding(0),
            Padding = new Padding(0),
            Height = guideHeight + ((1 + FilterRows) * _fieldHeight) + 4
        };
        for (int i = 0; i < FilterColumns; i++)
            host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / FilterColumns));
        host.RowStyles.Add(new RowStyle(SizeType.Absolute, guideHeight));
        for (int i = 0; i < 1 + FilterRows; i++)
            host.RowStyles.Add(new RowStyle(SizeType.Absolute, _fieldHeight));
        _filterHost = host;
        guide.Dock = DockStyle.Fill;
        host.Controls.Add(guide, 0, 0);
        host.SetColumnSpan(guide, FilterColumns);
        /* Arrangement and record checks share the top row. */
        AddField("Report", mode, false);
        AddField("Group by", group, false);
        AddField("Split by", split, false);
        AddField("Sort by", sort, false);
        AddField("Views", views, false);
        AddField("Record check", completionFilter, false);
        /* What goes into it: only the filters the chosen report uses are shown. */
        AddField("Campus", campus, true);
        AddField("Academic Year", year, true);
        AddField("Compare To", compareYear, true);
        AddField("Count placements", strengthScope, true);
        AddField("Class", cls, true);
        AddField("Section", section, true);
        AddField("Status", status, true);
        AddField("Gender", gender, true);
        AddField("Transport", transportFilter, true);
        AddField("Change type", changeType, true);
        AddField("Search", search, true);
        AddField("Date period", datePeriod, true);
        AddField("From", from, true);
        AddField("To", to, true);
        AddField(string.Empty, inactive, true);
        PlaceFilters();
        return host;
    }
    /// <summary>Puts the arrangement boxes on their row and packs the visible filters into the cells below, closing any gap.</summary>
    private void PlaceFilters()
    {
        if (_filterHost is null || _filterHost.IsDisposed)
            return;
        _filterHost.SuspendLayout();
        int arrange = 0;
        int filter = 0;
        foreach (var (field, isFilter) in _fieldList)
        {
            bool shown = !_hiddenFields.Contains(field);
            if (!isFilter)
            {
                _filterHost.SetCellPosition(field, new TableLayoutPanelCellPosition(arrange++, 1));
                field.Visible = shown;
                continue;
            }
            if (!shown || filter >= FilterColumns * FilterRows)
            {
                field.Visible = false;
                continue;
            }
            _filterHost.SetCellPosition(field, new TableLayoutPanelCellPosition(filter % FilterColumns, 2 + (filter / FilterColumns)));
            field.Visible = true;
            filter++;
        }
        _filterHost.ResumeLayout(true);
    }
    private void ReflowFilters() => PlaceFilters();
    private void ShowField(Control control, bool visible)
    {
        if (!_fieldOf.TryGetValue(control, out var field))
            return;
        if (visible)
            _hiddenFields.Remove(field);
        else
            _hiddenFields.Add(field);
    }
    private void WireEvents()
    {
        mode.SelectionChangeCommitted += async (_, _) => await OnModeChosenAsync();
        strengthScope.SelectedIndexChanged += (_, _) => OnFilterChanged();
        transportFilter.SelectedIndexChanged += (_, _) => OnFilterChanged();
        completionFilter.SelectedIndexChanged += (_, _) => OnFilterChanged();
        group.SelectionChangeCommitted += (_, _) => {
            RebuildSplitChoices();
            RebuildSortChoices();
            UpdateEnabledState();
            OnViewChanged();
        };
        split.SelectionChangeCommitted += (_, _) => OnViewChanged();
        sort.SelectionChangeCommitted += (_, _) => OnViewChanged();
        views.SelectionChangeCommitted += async (_, _) => await OnViewChosenAsync();
        /* SelectedIndexChanged (rather than UserSelectionChanged) also catches the shared "Clear Filters" button, which resets lookups programmatically. */
        foreach (var filter in new[] { year, compareYear, status, gender })
            filter.SelectedIndexChanged += (_, _) => OnFilterChanged();
        /* "Biggest increase / drop" only means something with a second year to compare against. */
        compareYear.SelectedIndexChanged += (_, _) => {
            if (!_suppress)
                RebuildSortChoices();
        };
        /* One-way cascade: Campus limits Classes; Campus and Class limit Sections. A selected Section must never hide a valid Class. */
        campus.SelectedIndexChanged += (_, _) => {
            OnParentChanged(classes: true, sections: true);
        };
        cls.SelectedIndexChanged += (_, _) => {
            OnParentChanged(classes: false, sections: true);
        };
        section.SelectedIndexChanged += (_, _) => { if (!_cascading) OnFilterChanged(); };
        /* Which change types to show is only a way of looking at the rows already loaded. */
        changeType.SelectedIndexChanged += (_, _) => OnViewChanged();
        search.TextChanged += (_, _) => {
            if (_mode == ModeChanges)
                OnViewChanged();
            else
                OnFilterChanged();
        };
        datePeriod.SelectedIndexChanged+=(_,_)=>{
            if (_suppress) return;
            ResolveStudentDatePeriod();UpdateEnabledState();OnFilterChanged();
        };
        limitDate.CheckedChanged += (_, _) => {
            if (!_suppress && !resolvingDatePeriod) datePeriod.SelectedItem=limitDate.Checked ? ReportDatePeriod.Fixed : "All dates";
            OnFilterChanged();
        };
        inactive.CheckedChanged += (_, _) => OnFilterChanged();
        from.ValueChanged += (_, _) => StudentDateEdited();
        to.ValueChanged += (_, _) => StudentDateEdited();
    }
    // modes, presets, choices
    private static readonly (string Key, string Text)[] ModeChoices =
    {
        (ModeList, "Student list"),
        (ModeAdmissions, "Admissions"),
        (ModeStrength, "Strength and comparison"),
        (ModeChanges, "Movement and changes")
    };
    private static string ModeContext(string key) => key switch {
        ModeAdmissions => "Review admissions for a date range. Choose the grouping and columns.",
        ModeStrength => "Review student totals by class and compare academic years.",
        ModeChanges => "Review admissions, promotions, transfers, departures and corrections.",
        _ => "Review student details. Choose the filters, grouping and columns."
    };
    /* The one line above the filters: what this report is for, and the next step. */
    private static string ModeHint(string key) => key switch {
        ModeAdmissions => "Choose admission dates and optional campus/class/section filters, then View Report.",
        ModeStrength => "Count active, all or former placements. Select a year and optional Compare To, then View Report.",
        ModeChanges => "Choose movement dates, then View Report. Group by Change type for totals.",
        _ => "Use View Report for all students or filter below. Use Columns to choose details."
    };
    private static string ModeTitle(string key) => key switch {
        ModeAdmissions => "Admissions",
        ModeStrength => "Student Strength",
        ModeChanges => "Student Movement",
        _ => "Student List"
    };
    private static bool IsRegisterMode(string key) =>
        key == ModeList || key == ModeAdmissions;
    private static string SelectedKey(ModernComboBox combo) =>
        (combo.SelectedItem as Choice)?.Key ?? string.Empty;
    private static void SelectKey(ModernComboBox combo, string key)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is Choice choice &&
                string.Equals(choice.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = i;
                return;
            }
        }
        if (combo.Items.Count > 0)
            combo.SelectedIndex = 0;
    }
    private static void FillChoices(ModernComboBox combo, IEnumerable<Choice> choices, string keepKey)
    {
        combo.Items.Clear();
        foreach (var choice in choices)
            combo.Items.Add(choice);
        SelectKey(combo, keepKey);
    }
    private static void BindStaticLookup(LookupBox box, string[] names, string blankText)
    {
        var table = new DataTable();
        table.Columns.Add("Id", typeof(int));
        table.Columns.Add("Name", typeof(string));
        for (int i = 0; i < names.Length; i++)
            table.Rows.Add(i + 1, names[i]);
        box.Bind(table, "Id", "Name", blankText);
    }
    private static string SelectedText(LookupBox box, string column) =>
        box.SelectedId > 0 && box.SelectedItem is DataRowView row && row.DataView.Table!.Columns.Contains(column)
            ? Convert.ToString(row[column])?.Trim() ?? string.Empty
            : string.Empty;
    private static void SelectId(LookupBox box, int id)
    {
        if (box.Items.Count == 0)
            return;
        box.SelectedValue = id;
        if (box.SelectedIndex < 0)
            box.SelectedIndex = 0;
    }
    private static void SelectText(LookupBox box, string column, string text)
    {
        if (box.Items.Count == 0)
            return;
        box.SelectedIndex = 0;
        if (string.IsNullOrWhiteSpace(text))
            return;
        for (int i = 1; i < box.Items.Count; i++)
        {
            if (box.Items[i] is DataRowView row &&
                string.Equals(Convert.ToString(row[column]), text, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedIndex = i;
                return;
            }
        }
    }
    private void RebuildGroupChoices()
    {
        string keep = SelectedKey(group);
        FillChoices(group, GroupChoicesFor(_mode), keep);
        RebuildSplitChoices();
        RebuildSortChoices();
    }
    /// <summary>Splitting by the very thing the rows are grouped by would say nothing, so it is not offered.</summary>
    private void RebuildSplitChoices() =>
        FillChoices(split, SplitChoicesFor(_mode, SelectedKey(group)), SelectedKey(split));
    private void RebuildSortChoices()
    {
        bool grouped = SelectedKey(group) is not (GroupNone or "");
        FillChoices(sort, SortChoicesFor(_mode, grouped, compareYear.SelectedId > 0), SelectedKey(sort));
    }
    /// <summary>A preset is only a starting arrangement: how to group, how to sort, and which switches to throw.</summary>
    private void ApplyPreset(string key, bool keepFilters)
    {
        datePeriod.SelectedItem="All dates";
        SelectKey(transportFilter, "");
        SelectKey(completionFilter, "");
        switch (key)
        {
            case ModeAdmissions:
                SelectKey(group, GroupNone);
                SelectKey(split, GroupNone);
                RebuildSortChoices();
                SelectKey(sort, "admdate_desc");
                limitDate.Checked = false;
                inactive.Checked = true;
                break;
            case ModeStrength:
                SelectKey(group, "campusclasssection");
                SelectKey(split, GroupNone);
                RebuildSortChoices();
                SelectKey(sort, "group");
                limitDate.Checked = false;
                inactive.Checked = false;
                break;
            case ModeChanges:
                SelectKey(group, GroupNone);
                SelectKey(split, GroupNone);
                RebuildSortChoices();
                SelectKey(sort, "date_asc");
                limitDate.Checked = false;
                break;
            default:
                SelectKey(group, GroupNone);
                SelectKey(split, GroupNone);
                RebuildSortChoices();
                SelectKey(sort, "name");
                limitDate.Checked = false;
                inactive.Checked = false;
                break;
        }
    }
    /// <summary>Date ranges are optional for lists, admissions, and movements.</summary>
    private bool LimitOn =>
        (_mode == ModeAdmissions || _mode == ModeList || _mode == ModeChanges) && limitDate.Checked;
    /// <summary>Whether Left students are included: always for Admissions (a student admitted then left was still admitted).</summary>
    private bool IncludeFormerStudentsOn =>
        _mode == ModeAdmissions ||
        (_mode == ModeList && (SelectedText(status, "Name") == "Left" || inactive.Checked && status.SelectedId == 0));

    private void ResetFilters()
    {
        _scopeEdited = true;
        ReportLayoutStore.SetPreference(LastScopeKey, "");
        ++_reportRevision;
        InvalidateReport();
        _raw = _compareRaw = null;
        _rawHasBalance = false;
        _appliedSignature = _filterText = _countText = string.Empty;
        SetReportActionsEnabled(false);
        bool previous = _suppress;
        _suppress = true;
        try
        {
            foreach (var lookup in new[] { campus, year, compareYear, cls, section, status, gender, changeType })
                SelectId(lookup, 0);
            search.Clear();
            SelectKey(strengthScope, "active");
            limitDate.Checked = inactive.Checked = false;
            from.Value = DateTime.Today.AddMonths(-1);
            to.Value = DateTime.Today;
            SelectKey(views, string.Empty);
            deleteViewButton.Enabled = false;
            renameViewButton.Enabled=false;
            rangeValidator?.ClearAll();
            _comparisonBaseYearId = -1;
            RefreshClassSectionLists(classes: true, sections: true);
            ApplyPreset(_mode, keepFilters: true);
        }
        finally { _suppress = previous; }
        UpdateEnabledState();
        _shape = ComputeShape(ReadView(), Array.Empty<string>());
        UiHelper.ClearGridState(Grid);
        UiHelper.SetGridPlaceholderMessage(Grid, "Choose your filters above, then press View Report.");
        UiHelper.SetGridEmptyMessage(Grid, "Choose your filters above, then press View Report.");
        campus.Focus();
        SetStatus("Filters reset. Press View Report to load the report with these filters.");
    }
    /// <summary>Shows only what the chosen report uses, and enables what depends on something else being chosen.</summary>
    private void UpdateEnabledState()
    {
        bool register = IsRegisterMode(_mode);
        bool strength = _mode == ModeStrength;
        bool changes = _mode == ModeChanges;
        bool byId = register && TryAdmissionNumber(search.Text.Trim(), out _);
        bool previous = _suppress;
        _suppress = true;
        try
        {
            if (status.SelectedId > 0) inactive.Checked = false;
            if (year.SelectedId == 0 || compareYear.SelectedId == year.SelectedId)
                SelectId(compareYear, 0);
            if (_years is not null && _comparisonBaseYearId != year.SelectedId)
            {
                _comparisonBaseYearId = year.SelectedId;
                int keep = compareYear.SelectedId;
                var candidates = _years.Clone();
                foreach (DataRow row in _years.Rows)
                    if (Convert.ToInt32(row["AcademicYearId"]) != year.SelectedId) candidates.ImportRow(row);
                compareYear.Bind(candidates, "AcademicYearId", "YearName", "None");
                if (year.SelectedId > 0 && keep != year.SelectedId) SelectId(compareYear, keep);
                RebuildSortChoices();
            }
        }
        finally { _suppress = previous; }
        ShowField(campus, true);
        ShowField(year, _mode == ModeList || strength);
        ShowField(compareYear, strength);
        ShowField(strengthScope, strength);
        ShowField(cls, !changes);
        ShowField(section, !changes);
        ShowField(status, register);
        ShowField(gender, !changes);
        ShowField(transportFilter, _mode == ModeList);
        ShowField(completionFilter, _mode == ModeList);
        completionFilter.Enabled = !byId;
        ShowField(changeType, changes);
        ShowField(search, register || changes);
        ShowField(datePeriod, register || changes);
        ShowField(from, register || changes);
        ShowField(to, register || changes);
        ShowField(inactive, _mode == ModeList);
        campus.Enabled = !byId;
        year.Enabled = !byId;
        cls.Enabled = !byId && cls.Items.Count > 1;
        section.Enabled = !byId && section.Items.Count > 1;
        status.Enabled = gender.Enabled = limitDate.Enabled = !byId;
        datePeriod.Enabled=!byId;
        transportFilter.Enabled = !byId;
        compareYear.Enabled = strength && year.SelectedId > 0 && compareYear.Items.Count > 1;
        from.Enabled = to.Enabled = !byId && LimitOn;
        inactive.Enabled = !byId && status.SelectedId == 0;
        split.Enabled = SelectedKey(group) is not (GroupNone or "");
        void Explain(Control control, string reason)
        {
            if (!_fieldOf.TryGetValue(control, out var field)) return;
            SetToolTip(field, control.Enabled ? string.Empty : reason);
            foreach (var label in field.Controls.OfType<Label>())
                label.ForeColor = control.Enabled ? AppTheme.Text : AppTheme.MutedText;
        }
        foreach (var control in new Control[] { campus, year, status, gender, limitDate, transportFilter })
            Explain(control, "Clear the Admission Number search to use this filter.");
        Explain(cls, byId ? "Clear the Admission Number search to use this filter." : "No classes are available for the selected campus and section.");
        Explain(section, byId ? "Clear the Admission Number search to use this filter." : "No sections are available for the selected campus and class.");
        Explain(compareYear, "Choose an Academic Year first. Compare To offers other years only.");
        Explain(inactive, byId ? "Admission Number search includes former students automatically." : "Status already determines whether active or former students are shown.");
        Explain(from, byId ? "Dates are ignored for Admission Number search." : "Choose a date period to use this date.");
        Explain(to, byId ? "Dates are ignored for Admission Number search." : "Choose a date period to use this date.");
        Explain(split, "Choose Group by first to split grouped totals.");
        guide.Text = byId ? "Admission Number search finds that student directly. Other filters are disabled until you clear or change Search."
            : ModeHint(_mode);
        ReflowFilters();
    }
    private async Task OnModeChosenAsync()
    {
        string chosen = SelectedKey(mode);
        if (chosen.Length == 0 || chosen == _mode)
            return;
        SelectKey(views, "");
        deleteViewButton.Enabled = false;
        renameViewButton.Enabled=false;
        SwitchMode(chosen, applyPreset: true);
        await RunAsync();
    }
    private void SwitchMode(string key, bool applyPreset)
    {
        _suppress = true;
        try
        {
            _mode = key;
            SelectKey(mode, key);
            RebuildGroupChoices();
            if (applyPreset)
                ApplyPreset(key, keepFilters: true);
            UpdateEnabledState();
            SetPageHeaderText(string.Empty, ModeContext(key));
        }
        finally
        {
            _suppress = false;
        }
        if (_raw is null)
            _shape = ComputeShape(ReadView(), Array.Empty<string>());
        OnFilterChanged();
    }
    private void OnParentChanged(bool classes, bool sections)
    {
        if (_cascading)
            return;
        string? note = _suppress
            ? null
            : RefreshClassSectionLists(classes, sections);
        OnFilterChanged();
        if (note is not null)
            SetStatus(note, true);
    }
    /// <summary>The ids of one column of the valid campus / class / section combinations, for the parents chosen (0 = any).</summary>
    private HashSet<int> ComboIds(string column, int campusId, int classId, int sectionId)
    {
        var ids = new HashSet<int>();
        if (_combos is null)
            return ids;
        foreach (DataRow row in _combos.Rows)
        {
            if (campusId > 0 && Convert.ToInt32(row["CampusId"]) != campusId)
                continue;
            if (classId > 0 && Convert.ToInt32(row["ClassId"]) != classId)
                continue;
            if (sectionId > 0 && Convert.ToInt32(row["SectionId"]) != sectionId)
                continue;
            ids.Add(Convert.ToInt32(row[column]));
        }
        return ids;
    }
    /// <summary>Rebinds a lookup with only the rows whose id is allowed (null = all of them), keeping the selection if it is still there.</summary>
    private static void RebindRestricted(LookupBox box, DataTable all, string idColumn, string textColumn, string blankText, HashSet<int>? allowed, int keepId)
    {
        DataTable rows = all.Clone();
        foreach (DataRow row in all.Rows)
        {
            if (allowed is null || allowed.Contains(Convert.ToInt32(row[idColumn])))
                rows.ImportRow(row);
        }
        box.Bind(rows, idColumn, textColumn, blankText);
        if (keepId > 0)
            SelectId(box, keepId);
    }
    /// <summary>A class the chosen campus does not run, a section the chosen class does not have - neither is offered.</summary>
    private string? RefreshClassSectionLists(bool classes, bool sections)
    {
        if (_combos is null || _allClasses is null || _allSections is null)
            return null;
        var notes = new List<string>();
        _cascading = true;
        try
        {
            int campusId = campus.SelectedId;
            int classId = cls.SelectedId;
            int sectionId = section.SelectedId;
            if (classId > 0 && campusId > 0 && !ComboIds("ClassId", campusId, 0, 0).Contains(classId))
            {
                classId = 0;
                notes.Add("Class");
            }
            if (sectionId > 0 && (campusId > 0 || classId > 0) &&
                !ComboIds("SectionId", campusId, classId, 0).Contains(sectionId))
            {
                sectionId = 0;
                notes.Add("Section");
            }
            bool sectionChanged = section.SelectedId != sectionId;
            bool classChanged = cls.SelectedId != classId;
            if (classes || classChanged)
            {
                var allowed = ComboIds("ClassId", campusId, 0, 0);
                RebindRestricted(cls, _allClasses, "ClassId", "ClassName", "All classes", allowed, classId);
            }
            if (sections || sectionChanged || classChanged)
            {
                var allowed = ComboIds("SectionId", campusId, classId, 0);
                RebindRestricted(section, _allSections, "SectionId", "SectionName", "All sections", allowed, sectionId);
            }
        }
        finally
        {
            _cascading = false;
        }
        return notes.Count == 0
            ? null
            : $"{string.Join(" and ", notes)} reset to All - not available for the campus or class you chose.";
    }
    // what changed -> stale or reshape
    private static bool TryAdmissionNumber(string text, out string number) =>
        AdmissionNumbers.TryNormalize(text, out number);
    private static string DateKey(ModernDatePicker picker, bool active) =>
        active ? picker.Value.Date.ToString("yyyy-MM-dd") : string.Empty;
    /* The signature mirrors RunAsync's parameter resolution exactly, and only counts the filters the chosen report actually sends. */
    private string FilterSignature()
    {
        string searchText = search.Text.Trim();
        string genderText = SelectedText(gender, "Name");
        switch (_mode)
        {
            case ModeChanges:
                return $"{_mode}|{campus.SelectedId}|{LimitOn}|{DateKey(from, LimitOn)}|{DateKey(to, LimitOn)}";
            case ModeStrength:
                return $"{_mode}|{campus.SelectedId}|{year.SelectedId}|{compareYear.SelectedId}|{cls.SelectedId}|{section.SelectedId}|{genderText}|{SelectedKey(strengthScope)}";
            default:
                /* Admission Number is its own, independent filter: a search that is a whole number finds that one student whatever Campus / Year / Class / Section / Status say. */
                if (TryAdmissionNumber(searchText, out _))
                    return $"{_mode}|id:{searchText}";
                return $"{_mode}|{campus.SelectedId}|{(_mode == ModeList ? year.SelectedId : 0)}|{cls.SelectedId}|{section.SelectedId}|{SelectedText(status, "Name")}|{genderText}|{searchText}|{LimitOn}|{DateKey(from, LimitOn)}|{DateKey(to, LimitOn)}|{IncludeFormerStudentsOn}|{(_mode == ModeList ? SelectedKey(transportFilter) : "")}|{(_mode == ModeList ? SelectedKey(completionFilter) : "")}";
        }
    }
    /* Lets the shared report machinery compare the filters on screen with the ones a landing answered, so a report that arrives under changed filters is never treated as current. */
    protected override string ReportFilterSignature() => FilterSignature();
    private void SetReportActionsEnabled(bool enabled)
    {
        exportButton.Enabled = enabled;
        printButton.Enabled = enabled;
        UpdateAdmissionsAction();
    }
    /* The report on screen goes stale the moment the filters no longer match the ones that produced it. */
    private void OnFilterChanged()
    {
        if (_suppress)
            return;
        CancelPendingReport();
        UpdateEnabledState();
        if (string.IsNullOrEmpty(_appliedSignature))
            return;
        bool current = string.Equals(_appliedSignature, FilterSignature(), StringComparison.Ordinal);
        SetReportActionsEnabled(current);
        if (current)
        {
            RestoreReportStale();
            SetStatus(UiText.Reports.MatchesFilters);
        }
        else
        {
            MarkReportStale();
            SetStatus(UiText.Reports.FiltersChanged, true);
        }
    }
    /// <summary>A change to how the loaded rows are arranged (group, split, sort, which change types).</summary>
    private void OnViewChanged()
    {
        if (_suppress)
            return;
        UpdateEnabledState();
        if (_raw is null)
        {
            _shape = ComputeShape(ReadView(), Array.Empty<string>());
            return;
        }
        if (!string.Equals(_appliedSignature, FilterSignature(), StringComparison.Ordinal))
        {
            SetStatus("The filters have changed - run the report again to apply this.", true);
            return;
        }
        _ = ReshapeAsync();
    }
    protected override void OnReportPageBound()
    {
        UpdateAdmissionsAction();
        if (!_shape.Grouped)
            return;
        /* A grouped table is arranged by "Sort by", and ends in a Total row that a header click would shuffle into the middle. */
        LockGridOrder();
        string first = _shape.FirstDimColumn;
        if (first.Length == 0 || !Grid.Columns.Contains(first))
            return;
        foreach (DataGridViewRow row in Grid.Rows)
        {
            if (string.Equals(Convert.ToString(row.Cells[first].Value), TotalLabel, StringComparison.Ordinal))
                row.DefaultCellStyle.Font = AppTheme.ButtonFont;
        }
    }
    // V2Page report hooks
    protected override string? ReportCampusContext => campus.SelectedId>0?campus.Text:null;
    protected override string ReportLayoutKey => _shape.Key;
    protected override IReadOnlyList<ReportColumnDef>? ReportColumnCatalog => _shape.Catalog;
    protected override IReadOnlyList<string>? ReportDefaultColumnKeys => _shape.DefaultKeys;
    protected override string ReportExportTitle() =>
        _shape.Grouped && _lastView is not null
            ? $"{ModeTitle(_lastView.Mode)} - by {GroupText(_lastView.GroupKey)}"
            : _mode == ModeList
                ? StudentReportQuickViews.All.FirstOrDefault(view => "ready:" + view.Key == SelectedKey(views))?.Name ?? ModeTitle(_mode)
                : ModeTitle(_mode);
    protected override string ReportExportSubtitle()
    {
        string text = string.Join("  |  ", new[] { _filterText, _countText }.Where(t => t.Length > 0));
        return text.Length > 0
            ? text
            : ModeContext(_mode);
    }
    protected override string ReportExportFileScope()
    {
        // Keep applied filter choices, but exclude result counts and explanatory text.
        // Those belong under the PDF title and can make the filename excessively long.
        return string.Join(" - ", _filterText.Split("  |  ", StringSplitOptions.RemoveEmptyEntries)
            .Select(part => {
                string concise = part.Trim();
                int explanation = concise.IndexOf(" (contact,", StringComparison.OrdinalIgnoreCase);
                if (explanation >= 0) concise = concise[..explanation];
                if (concise.StartsWith("Placements: ", StringComparison.Ordinal))
                    return concise["Placements: ".Length..];
                if (concise.StartsWith("Compared with: ", StringComparison.Ordinal))
                    return "vs " + concise["Compared with: ".Length..];
                return concise.Replace(": ", " ");
            })
            .Where(part => part.Length > 0));
    }
    protected override bool ReportLayoutNeedsReload(IReadOnlyList<string> keys) =>
        IsRegisterMode(_mode) &&
        _raw is not null &&
        !_rawHasBalance &&
        keys.Contains(BalanceKey, StringComparer.OrdinalIgnoreCase);
    protected override void ReloadReportForLayout()
    {
        _ = RunAsync();
    }
    private ViewArgs? _lastView;
    // reading the controls
    private ViewArgs ReadView() => new(
        _mode,
        SelectedKey(group),
        split.Enabled ? SelectedKey(split) : GroupNone,
        SelectedKey(sort),
        SelectedText(changeType, "Name"),
        _mode == ModeChanges ? search.Text.Trim() : string.Empty,
        SelectedText(year, "YearName"),
        compareYear.SelectedId > 0 ? SelectedText(compareYear, "YearName") : string.Empty,
        _mode == ModeList && !TryAdmissionNumber(search.Text.Trim(), out _) ? SelectedKey(transportFilter) : "",
        _mode == ModeList && !TryAdmissionNumber(search.Text.Trim(), out _) ? SelectedKey(completionFilter) : "",
        CurrentUser.HasPermission("Students.View"));
    private RunArgs ReadArgs()
    {
        string searchText = search.Text.Trim();
        bool register = IsRegisterMode(_mode);
        bool byId = register && TryAdmissionNumber(searchText, out _);
        TryAdmissionNumber(searchText, out string admissionNumber);
        if (byId) searchText = admissionNumber;
        return new RunArgs(
            _mode,
            byId ? 0 : campus.SelectedId,
            byId || !(_mode == ModeList || _mode == ModeStrength) ? 0 : year.SelectedId,
            _mode == ModeStrength && compareYear.Enabled ? compareYear.SelectedId : 0,
            byId ? 0 : cls.SelectedId,
            byId ? 0 : section.SelectedId,
            _mode == ModeStrength ? SelectedKey(strengthScope) switch { "history" => "", "left" => "Left", _ => "Active" }
                : byId ? string.Empty : SelectedText(status, "Name"),
            byId ? string.Empty : SelectedText(gender, "Name"),
            register ? searchText : string.Empty,
            byId,
            byId ? admissionNumber : string.Empty,
            !byId && LimitOn,
            from.Value.Date,
            to.Value.Date,
            /* A student the operator names by Admission Number is found even when Left. */
            byId || IncludeFormerStudentsOn);
    }
    /// <summary>A one-line description of the filters, for the subtitle of the exported file.</summary>
    private string DescribeFilters(RunArgs a)
    {
        var parts = new List<string>();
        if (a.Mode == ModeList && !a.ByAdmissionId && SelectedKey(completionFilter).Length > 0)
            parts.Add(completionFilter.Text + (CurrentUser.HasPermission("Students.View")
                ? " (contact, date of birth and address only)" : " (primary contact only)"));
        if (a.Mode == ModeList && !a.ByAdmissionId && SelectedKey(transportFilter).Length > 0)
            parts.Add(SelectedKey(transportFilter) == "yes" ? "Uses transport" : "No transport");
        if (a.ByAdmissionId)
        {
            parts.Add($"Admission Number {a.AdmissionNumber}");
            return string.Join("  |  ", parts);
        }
        if (a.CampusId > 0)
            parts.Add($"Campus: {SelectedText(campus, "CampusName")}");
        if (a.Mode != ModeChanges)
        {
            if (a.Mode == ModeStrength)
                parts.Add($"Placements: {(a.Status.Length == 0 ? "All recorded" : a.Status == "Left" ? "Former" : "Active")}");
            if (a.YearId > 0)
                parts.Add($"Year: {SelectedText(year, "YearName")}");
            if (a.Mode == ModeStrength && a.CompareYearId > 0)
                parts.Add($"Compared with: {SelectedText(compareYear, "YearName")}");
            if (a.ClassId > 0)
                parts.Add($"Class: {SelectedText(cls, "ClassName")}");
            if (a.SectionId > 0)
                parts.Add($"Section: {SelectedText(section, "SectionName")}");
            if (a.Gender.Length > 0)
                parts.Add($"Gender: {a.Gender}");
        }
        if (IsRegisterMode(a.Mode))
        {
            if (a.Status.Length > 0)
                parts.Add($"Status: {a.Status}");
            else if (a.IncludeFormerStudents)
                parts.Add("Including students who left");
            if (a.Search.Length > 0)
                parts.Add($"Search: {a.Search}");
        }
        if (a.LimitDate)
        {
            string what = a.Mode == ModeChanges ? "Changes" : "Admitted";
            parts.Add($"{what} {a.From:dd-MM-yyyy} to {a.To:dd-MM-yyyy}");
        }
        return string.Join("  |  ", parts);
    }
    // lookups
    private async Task BindLookupsAsync()
    {
        try
        {
            /* Bound here, once the window exists, so the "Any / All" text of the blank row shows. */
            BindStaticLookup(status, new[] { "Active", "Left" }, "Any status");
            BindStaticLookup(gender, new[] { "Male", "Female" }, "All");
            BindStaticLookup(changeType, new[] { "Admission", "Promote", "Leave", "Transfer", "Correction" }, "All changes");
            Task<DataTable> campusesTask = DatabaseHelper.QueryUserAsync("GetCampuses");
            Task<DataTable> yearsTask = DatabaseHelper.QueryUserAsync("GetAcademicYears");
            Task<DataTable> classesTask = DatabaseHelper.QueryUserAsync("GetClasses");
            Task<DataTable> sectionsTask = ReportUi.SectionFilterListAsync();
            await Task.WhenAll(campusesTask, yearsTask, classesTask, sectionsTask);
            campus.Bind(await campusesTask, "CampusId", "CampusName", "All campuses");
            _years = await yearsTask;
            year.Bind(_years, "AcademicYearId", "YearName", "All years");
            compareYear.Bind(_years.Copy(), "AcademicYearId", "YearName", "None");
            _allClasses = await classesTask;
            _allSections = await sectionsTask;
            cls.Bind(_allClasses, "ClassId", "ClassName", "All classes");
            section.Bind(_allSections, "SectionId", "SectionName", "All sections");
            /* Use active campus assignments for filter choices; the underlying reports still read historical records when scope is All. */
            try
            {
                _combos = await ReportClassSectionLookup.QueryAsync(activeOnly: true);
                if (!_combos.Columns.Contains("CampusId") ||
                    !_combos.Columns.Contains("ClassId") ||
                    !_combos.Columns.Contains("SectionId"))
                {
                    _combos = null;
                }
            }
            catch (Exception nonFatalException35)
            {
                FMSC_SMS.Core.AppExceptionHandler.LogNonFatal(nonFatalException35, "StudentReportsPage: BindLookupsAsync fallback");
                _combos = null;
            }
            if (IsDisposed)
                return;
            _suppress = true;
            try
            {
                RebuildSortChoices();
                RefreshClassSectionLists(classes: true, sections: true);
            }
            finally
            {
                _suppress = false;
            }
            UpdateEnabledState();
            if (!_scopeEdited && _reportRevision == 0)
                RestoreLastScope();
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
                Fail(ex);
        }
    }

    private ReportScopeSelection CurrentScopeSelection() => new(campus.SelectedId, year.SelectedId, cls.SelectedId, section.SelectedId);

    private void RestoreLastScope()
    {
        var saved = ReportScopeSelection.Read(ReportLayoutStore.GetPreference(LastScopeKey));
        if (saved is null) return;
        bool previous = _suppress;
        _suppress = true;
        try
        {
            SelectId(campus, saved.Campus);
            SelectId(year, saved.Year);
            SelectId(cls, 0); SelectId(section, 0);
            RefreshClassSectionLists(classes: true, sections: true);
            SelectId(cls, saved.Class);
            RefreshClassSectionLists(classes: false, sections: true);
            SelectId(section, saved.Class > 0 && cls.SelectedId == 0 ? 0 : saved.Section);
            RefreshClassSectionLists(classes: false, sections: false);
        }
        finally { _suppress = previous; }
        UpdateEnabledState();
        bool matches = saved == CurrentScopeSelection();
        SetStatus(matches ? "Last location filters restored. Review them, then press View Report."
            : "Some saved filters are unavailable. Review the filters before running the report.", !matches);
    }
    // run
    /// <summary>Thrown when the outstanding-balance enrichment cannot be trusted, so the report must not be rendered. 0 - that reads as "owes nothing".</summary>
    private sealed class BalanceUnavailableException : Exception
    {
        public BalanceUnavailableException(string message, Exception? inner = null)
            : base(message, inner)
        {
        }
    }
    private async Task RunAsync()
    {
        ResolveStudentDatePeriod();
        int revision = ++_reportRevision;
        string requested = FilterSignature();
        try
        {
            /* Each check is shown on the field that needs fixing, and clears itself when it is fixed. */
            var validator = rangeValidator ??= CreateValidator();
            validator.Begin();
            if (_mode == ModeStrength)
            {
                bool hasComparisonBase() => compareYear.SelectedId == 0 || year.SelectedId > 0;
                validator.Check(year, hasComparisonBase(), "Choose a year to compare", hasComparisonBase);
                bool differentYear() => compareYear.SelectedId == 0 || compareYear.SelectedId != year.SelectedId;
                validator.Check(compareYear, differentYear(), "Pick a different year", differentYear);
            }
            if (!validator.Finish())
                return;
            if (LimitOn && !ReportUi.ValidDateRange(validator, from, to))
                return;
            /* BindReportAsync runs its query off the UI thread, so everything the controls hold is read here first. */
            RunArgs args = ReadArgs();
            ViewArgs view = ReadView();
            var scopeToRemember = CurrentScopeSelection();
            string filterText = DescribeFilters(args);
            bool wantBalance = IsRegisterMode(_mode) && WantsBalance(view);
            int matched = 0;
            await BindReportAsync(async token => {
                LoadResult loaded = await LoadAsync(args, wantBalance, token);
                token.ThrowIfCancellationRequested();
                if (revision != _reportRevision || IsDisposed)
                    return new DataTable();
                _raw = loaded.Raw;
                _compareRaw = loaded.Compare;
                _rawHasBalance = loaded.HasBalance;
                matched = loaded.Raw.Rows.Count;
                _lastView = view;
                _filterText = filterText;
                return ShapeTable(view);
            });
            /* The rows that landed answer `requested`, so that is what this page has applied - even when the operator has moved a filter since. */
            if (revision != _reportRevision || !ReportLanded)
                return;
            _appliedSignature = requested;
            if (!args.ByAdmissionId && ReportMatchesCurrentFilters(requested))
                ReportLayoutStore.SetPreference(LastScopeKey, scopeToRemember.Serialize());
            SetReportActionsEnabled(ReportMatchesCurrentFilters(requested));
            ShowLandedStatus(args, matched);
        }
        catch (BalanceUnavailableException ex)
        {
            if (revision != _reportRevision || IsDisposed)
                return;
            /* Fail closed. */
            InvalidateReport();
            _raw = null;
            _compareRaw = null;
            _rawHasBalance = false;
            _appliedSignature = string.Empty;
            SetReportActionsEnabled(false);
            SetStatus("No report is displayed - the last run could not be completed. Run it again.", true);
            Warn(DbMessage.UserMessage(ex));
        }
        catch (Exception ex)
        {
            if (revision == _reportRevision && !IsDisposed)
                Fail(ex);
        }
    }
    /// <summary>Applies a new arrangement to the rows already loaded.</summary>
    private async Task ReshapeAsync()
    {
        try
        {
            if (_raw is null)
                return;
            ViewArgs view = ReadView();
            string requested = FilterSignature();
            await BindReportAsync(() => {
                _lastView = view;
                return Task.FromResult(ShapeTable(view));
            });
            if (!ReportLanded)
                return;
            SetReportActionsEnabled(ReportMatchesCurrentFilters(requested));
            ShowLandedStatus(null, -1);
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
                Fail(ex);
        }
    }
    private void ShowLandedStatus(RunArgs? args, int matched)
    {
        if (args is { Mode: ModeStrength } && matched == 0 && (_compareRaw is null || _compareRaw.Rows.Count == 0))
        {
            string scope = args.Status.Length == 0 ? "recorded" : args.Status == "Left" ? "former" : "active";
            string message = $"No {scope} placements match these filters. Change Count placements or Academic Year to see another scope.";
            UiHelper.SetGridEmptyMessage(Grid, message);
            SetStatus(message);
            return;
        }
        if (args is { ByAdmissionId: true })
        {
            SetStatus(matched == 0
                ? $"No student has Admission Number {args.AdmissionNumber}."
                : $"Admission Number {args.AdmissionNumber}: shown whatever Campus, Year, Class, Section, Status or dates say.");
            return;
        }
        if (_shape.Grouped && _countText.Length > 0)
            SetStatus(_countText);
    }
    /// <summary>True when the columns the operator has chosen for the shape about to be built include the balance.</summary>
    private bool WantsBalance(ViewArgs view)
    {
        ReportShape shape = ComputeShape(view, Array.Empty<string>());
        var saved = ReportLayoutStore.GetColumns(shape.Key);
        IEnumerable<string> keys = saved ?? shape.DefaultKeys ?? shape.Catalog.Where(c => c.DefaultVisible).Select(c => c.Key);
        return keys.Contains(BalanceKey, StringComparer.OrdinalIgnoreCase);
    }
    private async Task<LoadResult> LoadAsync(RunArgs a, bool wantBalance, CancellationToken token)
    {
        if (a.Mode == ModeChanges)
        {
            DataTable changes = await DatabaseHelper.QueryUserAsync(
                "GetStudentMovementReport",
                token,
                DatabaseHelper.Date("@FromDate", a.LimitDate ? a.From : new DateTime(1900, 1, 1)),
                DatabaseHelper.Date("@ToDate", a.LimitDate ? a.To : new DateTime(2999, 12, 31)),
                DatabaseHelper.Int("@CampusId", a.CampusId));
            token.ThrowIfCancellationRequested();
            PrepareChanges(changes);
            return new LoadResult(changes, null, false);
        }
        DataTable raw = await QueryRegisterAsync(a, a.YearId, wantBalance, token);
        token.ThrowIfCancellationRequested();
        if (a.Mode == ModeStrength)
            KeepEnrolledStudents(raw);
        if (a.ByAdmissionId)
            KeepOnlyAdmissionNumber(raw, a.AdmissionNumber);
        PrepareRegister(raw);
        DataTable? compare = null;
        if (a.Mode == ModeStrength && a.CompareYearId > 0)
        {
            compare = await QueryRegisterAsync(a, a.CompareYearId, cancellationToken: token);
            token.ThrowIfCancellationRequested();
            KeepEnrolledStudents(compare);
            PrepareRegister(compare);
        }
        if (wantBalance && (!raw.Columns.Contains(BalanceKey) || raw.AsEnumerable().Any(r => r.IsNull(BalanceKey))))
            throw new BalanceUnavailableException("The report did not return verified outstanding balances, so it was not generated.");
        return new LoadResult(raw, compare, wantBalance);
    }
    private static Task<DataTable> QueryRegisterAsync(RunArgs a, int yearId, bool includeBalance = false, CancellationToken cancellationToken = default)
    {
        bool strength = a.Mode == ModeStrength;
        /* Historical enrollment remains part of that year's strength after the student leaves.
           Unenrolled admissions are removed separately before grouping. */
        bool includeFormerStudents = strength || a.IncludeFormerStudents;
        string enrolmentStatus = strength ? a.Status : string.Empty;
        return DatabaseHelper.QueryUserAsync(
            "GetStudentRegister",
            cancellationToken,
            DatabaseHelper.Int("@CampusId", a.CampusId),
            DatabaseHelper.Int("@AcademicYearId", yearId),
            DatabaseHelper.Int("@ClassId", a.ClassId),
            DatabaseHelper.Int("@SectionId", a.SectionId),
            DatabaseHelper.P("@Gender", a.Gender),
            DatabaseHelper.P("@StudentStatus", strength ? string.Empty : a.Status),
            DatabaseHelper.P("@EnrollmentStatus", enrolmentStatus),
            DatabaseHelper.P("@Search", a.Search),
            NullableDate("@AdmittedFrom", a.LimitDate && !strength ? a.From : null),
            NullableDate("@AdmittedTo", a.LimitDate && !strength ? a.To : null),
            DatabaseHelper.Bit("@IncludeFormerStudents", includeFormerStudents),
            DatabaseHelper.Bit("@IncludeBalance", includeBalance));
    }
    private static SqlParameter NullableDate(string name, DateTime? value) =>
        new(name, SqlDbType.Date) {
            Value = value.HasValue ? value.Value.Date : DBNull.Value
        };
    private static void KeepEnrolledStudents(DataTable table)
    {
        foreach (DataRow row in table.Select())
            if (row.IsNull("EnrollmentId"))
                table.Rows.Remove(row);
    }
    /* GetStudentRegister also matches a number inside names and registration numbers; for an Admission Number search those extra rows are noise. */
    private static void KeepOnlyAdmissionNumber(DataTable table, string admissionNumber)
    {
        if (!table.Columns.Contains("AdmissionNumber"))
            return;
        foreach (DataRow row in table.Select())
        {
            if (!string.Equals(AdmissionNumbers.Read(row), admissionNumber, StringComparison.OrdinalIgnoreCase))
            {
                table.Rows.Remove(row);
            }
        }
    }
    // saved views
    private void RefreshViewChoices(string selectKey)
    {
        _busyViews = true;
        try
        {
            views.Items.Clear();
            views.Items.Add(new Choice(string.Empty, "(none)"));
            foreach (var view in StudentReportQuickViews.All)
                views.Items.Add(new Choice("ready:" + view.Key, view.Name));
            foreach (var view in ReportLayoutStore.GetViews(ViewsKey))
                views.Items.Add(new Choice("saved:" + view.Name, "My view: " + view.Name));
            SelectKey(views, selectKey.Length > 0 ? "saved:" + selectKey : "");
            deleteViewButton.Enabled = SelectedSavedViewName().Length > 0;
            renameViewButton.Enabled=deleteViewButton.Enabled;
        }
        finally
        {
            _busyViews = false;
        }
    }
    private Dictionary<string, string> CaptureState() => new() {
        ["mode"] = _mode,
        ["group"] = SelectedKey(group),
        ["split"] = SelectedKey(split),
        ["sort"] = SelectedKey(sort),
        ["campus"] = campus.SelectedId.ToString(),
        ["year"] = year.SelectedId.ToString(),
        ["compare"] = compareYear.SelectedId.ToString(),
        ["strengthScope"] = SelectedKey(strengthScope),
        ["class"] = cls.SelectedId.ToString(),
        ["section"] = section.SelectedId.ToString(),
        ["status"] = SelectedText(status, "Name"),
        ["gender"] = SelectedText(gender, "Name"),
        ["change"] = SelectedText(changeType, "Name"),
        ["search"] = search.Text.Trim(),
        ["limit"] = limitDate.Checked ? "1" : "0",
        ["datePeriod"] = datePeriod.Text,
        ["from"] = from.Value.Date.ToString("yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture),
        ["to"] = to.Value.Date.ToString("yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture),
        ["inactive"] = inactive.Checked ? "1" : "0",
        ["transport"] = SelectedKey(transportFilter),
        ["completion"] = SelectedKey(completionFilter),
        ["columns"] = string.Join(",", CurrentReportColumns())
    };
    private void SaveCurrentView()
    {
        string suggestion = SelectedSavedViewName();
        if (suggestion.Length == 0 && views.SelectedItem is Choice choice && choice.Key.StartsWith("ready:", StringComparison.Ordinal))
            suggestion = choice.Text;
        string? name = ReportNameDialog.Prompt(
            FindForm(),
            "Save view",
            "Name this view (filters, arrangement and columns):",
            suggestion);
        if (name is null)
            return;
        bool exists = ReportLayoutStore.GetViews(ViewsKey)
            .Any(v => string.Equals(v.Name, name, StringComparison.CurrentCultureIgnoreCase));
        if (exists &&
            !ModernMessageDialog.Confirm(
                FindForm(),
                $"A view named \"{name}\" already exists. Replace it with what is on screen now?",
                "Save view",
                defaultYes: false))
        {
            return;
        }
        var saved = ReportLayoutStore.SaveView(ViewsKey, new ReportSavedView {
            Name = name,
            State = CaptureState()
        });
        if (!saved.Succeeded)
        {
            SetStatus($"View \"{name}\" was not saved. Your current selections are retained. " +
                DbMessage.UserMessage(saved.Error!), PageStatusKind.Warning);
            return;
        }
        RefreshViewChoices(name);
        SetStatus($"View \"{name}\" saved." + (saved.RecoveryPath is not null
            ? " Previous damaged settings were backed up for recovery." : ""), PageStatusKind.Success);
    }
    private void DeleteSelectedView()
    {
        string name = SelectedSavedViewName();
        if (name.Length == 0)
            return;
        if (!ModernMessageDialog.Confirm(
                FindForm(),
                $"Delete the saved view \"{name}\"? This cannot be undone.",
                "Delete view",
                defaultYes: false))
        {
            return;
        }
        var deleted = ReportLayoutStore.DeleteView(ViewsKey, name);
        if (!deleted.Succeeded)
        {
            SetStatus($"View \"{name}\" was not deleted. " + DbMessage.UserMessage(deleted.Error!), PageStatusKind.Warning);
            return;
        }
        RefreshViewChoices(string.Empty);
        SetStatus(deleted.Changed ? $"View \"{name}\" deleted." : "That saved view is already absent.",
            deleted.Changed ? PageStatusKind.Success : PageStatusKind.Info);
    }
    private async Task OnViewChosenAsync()
    {
        _scopeEdited = true;
        if (_busyViews)
            return;
        string key = SelectedKey(views);
        string name = SelectedSavedViewName();
        deleteViewButton.Enabled = name.Length > 0;
        renameViewButton.Enabled=deleteViewButton.Enabled;
        var ready = StudentReportQuickViews.All.FirstOrDefault(view => "ready:" + view.Key == key);
        if (ready is not null)
        {
            // Keep the chosen campus/year/class/section; start with a straightforward active-student list.
            var state = CaptureState();
            state["mode"] = ModeList;
            state["group"] = state["split"] = GroupNone;
            state["sort"] = "name";
            state["search"] = state["status"] = state["gender"] = "";
            state["compare"] = "0";
            state["limit"] = state["inactive"] = "0";
            state["datePeriod"]="All dates";
            state["transport"] = ready.Transport;
            state["completion"] = ready.Completion;
            state["columns"] = ready.Columns;
            ApplyState(state);
            await RunAsync();
            return;
        }
        if (name.Length == 0)
            return;
        var view = ReportLayoutStore.GetViews(ViewsKey)
            .FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.CurrentCultureIgnoreCase));
        if (view is null)
            return;
        if (!SavedLocationsAvailable(view.State)) return;
        ApplyState(view.State);
        await RunAsync();
    }
    private string SelectedSavedViewName() => SelectedKey(views).StartsWith("saved:", StringComparison.Ordinal)
        ? SelectedKey(views)[6..] : "";
    private static string Get(IReadOnlyDictionary<string, string> state, string key, string fallback = "") =>
        state.TryGetValue(key, out var value) ? value : fallback;
    private static int GetInt(IReadOnlyDictionary<string, string> state, string key) =>
        int.TryParse(Get(state, key), out int value) ? value : 0;
    private void ApplyState(IReadOnlyDictionary<string, string> state)
    {
        string wanted = Get(state, "mode", ModeList);
        if (!ModeChoices.Any(m => m.Key == wanted))
            wanted = ModeList;
        _suppress = true;
        try
        {
            _mode = wanted;
            SelectKey(strengthScope, Get(state, "strengthScope", "active"));
            SelectKey(mode, wanted);
            RebuildGroupChoices();
            SelectKey(group, Get(state, "group", GroupNone));
            FillChoices(split, SplitChoicesFor(_mode, SelectedKey(group)), Get(state, "split", GroupNone));
            SelectId(campus, GetInt(state, "campus"));
            SelectId(year, GetInt(state, "year"));
            SelectId(compareYear, GetInt(state, "compare"));
            RebuildSortChoices();
            SelectKey(sort, Get(state, "sort"));
            /* Class and section are chosen through the same narrowing as by hand. */
            SelectId(cls, 0);
            SelectId(section, 0);
            RefreshClassSectionLists(classes: true, sections: true);
            SelectId(cls, GetInt(state, "class"));
            RefreshClassSectionLists(classes: false, sections: true);
            SelectId(section, GetInt(state, "section"));
            RefreshClassSectionLists(classes: false, sections: false);
            SelectText(status, "Name", Get(state, "status"));
            SelectText(gender, "Name", Get(state, "gender"));
            SelectText(changeType, "Name", Get(state, "change"));
            search.Text = Get(state, "search");
            limitDate.Checked = Get(state, "limit") == "1";
            datePeriod.SelectedItem=Get(state,"datePeriod",limitDate.Checked ? ReportDatePeriod.Fixed : "All dates");
            inactive.Checked = Get(state, "inactive") == "1";
            SelectKey(transportFilter, Get(state, "transport"));
            SelectKey(completionFilter, Get(state, "completion"));
            if (DateTime.TryParse(Get(state, "from"), out var fromDate))
                from.Value = fromDate;
            if (DateTime.TryParse(Get(state, "to"), out var toDate))
                to.Value = toDate;
            ResolveStudentDatePeriod();
            UpdateEnabledState();
            SetPageHeaderText(string.Empty, ModeContext(_mode));
        }
        finally
        {
            _suppress = false;
        }
        /* The columns belong to the shape this view leads to, so they are put in place before the report runs. */
        string columns = Get(state, "columns");
        if (columns.Length > 0)
        {
            ReportShape target = ComputeShape(ReadView(), Array.Empty<string>());
            RememberReportColumns(
                target.Key,
                columns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
    }
}
