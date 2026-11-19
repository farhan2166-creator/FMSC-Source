using FMSC_SMS.Core;
using Microsoft.Data.SqlClient;
namespace FMSC_SMS.Pages.Reports.Finance;
/// <summary>Shared behavior for the three canonical finance reports. The same captured table feeds the grid, printer and CSV export.</summary>
// BD-26/27: sort before pagination and reuse the same captured result for print/export.
// Academic month blocks and custom dates select the same bounds; each report retains its own date meaning.
public abstract class CanonicalFinanceReportPage : V2Page
{
    protected readonly ModernDatePicker From = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "dd-MM-yyyy", Value = DateTime.Today.AddMonths(-1) };
    protected readonly ModernDatePicker To = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "dd-MM-yyyy", Value = DateTime.Today };
    protected readonly LookupBox Campus = new();
    protected readonly LookupBox Year = new();
    protected readonly LookupBox Class = new();
    protected readonly LookupBox Section = new();
    protected readonly ModernComboBox View = new();
    protected readonly ModernComboBox GroupBy = new();
    protected readonly ModernComboBox PaymentMethod = new();
    private readonly ModernComboBox _orderBy = new();
    private readonly string _procedure;
    private readonly bool _usesDates;
    private readonly bool _usesYear;
    private readonly ModernButton _print;
    private readonly ModernButton _export;
    private readonly ModernButton? _classOutstandingButton;
    private readonly Label _summary = new() {
        Dock = DockStyle.Top,
        Height = Math.Max(38, AppMetrics.TextHeight(AppTheme.ButtonFont) + 10),
        Padding = new Padding(8, 6, 8, 4),
        Font = AppTheme.ButtonFont, ForeColor = AppTheme.HeadingText,
        BackColor = AppTheme.SurfaceSecondary, Text = "Run the report to see totals."
    };
    private FormValidator? _rangeValidator;
    private DataTable? _displayed;
    private string _appliedSignature = string.Empty;
    private string _filterSummary = string.Empty;
    private IReadOnlyList<ReportPrintMetric> _printMetrics=Array.Empty<ReportPrintMetric>();
    private protected override IReadOnlyList<ReportPrintMetric>? ReportPrintMetrics() => _printMetrics;
    private readonly string _scopePreferenceKey;
    private bool _scopeEdited;
    private bool _applyingScope;
    private bool _runningReport;
    private bool _lookupsReady;
    private readonly string _presetKey;
    private readonly ReportSavedViewsBar _savedViews;
    private readonly ReportClassSectionFilters _locationFilters;
    private readonly ModernButton _columnsButton;
    private IReadOnlyList<ReportColumnDef>? _availableColumns;
    private string _boundColumnLayout="";
    protected CanonicalFinanceReportPage(
        string title,
        string description,
        string procedure,
        string exportName,
        string[] views,
        string[] groupings,
        bool usesDates,
        bool usesYear,
        bool paymentMethod,
        bool grouping,
        bool classOutstandingPrint = false)
        : base(title, description)
    {
        _procedure = procedure;
        _usesDates = usesDates;
        _usesYear = usesYear;
        _scopePreferenceKey = $"finance.{exportName}.last-scope";
        _presetKey=$"finance.{exportName}";
        if (usesDates)
        {
            From.ShowCheckBox = To.ShowCheckBox = true;
            From.Checked = To.Checked = false;
        }
        View.Items.AddRange(views);
        View.SelectedIndex = 0;
        GroupBy.Items.AddRange(groupings);
        if (GroupBy.Items.Count > 0) GroupBy.SelectedIndex = 0;
        PaymentMethod.Items.AddRange(new object[] { "All", "Cash", "Bank", "Card", "Online", "Other", "Account Credit", "Refund", "Correction" });
        PaymentMethod.SelectedIndex = 0;
        FinanceHelp.Attach(this,PaymentMethod,"Filter the recorded payment method or adjustment type. Correction means a reduction in the recorded payment, rather than a change to the original fee.","A Rs. 1,000 payment reduced to Rs. 800 has a Rs. 200 payment reduction. It is separate from changing the fee itself.");
        ReportUi.RegisterChoiceFilter(PaymentMethod);
        var run = AddBusyAction("Run Report", RunAsync, "Primary");
        _print = AddBusyAction(UiText.Actions.Print, PrintAsync, "Secondary");
        FinanceHelp.Attach(this,_print,"Print the report already generated on screen. It does not receive money or change student fees.","Printing a report with Rs. 1,000 outstanding leaves the student's outstanding balance at Rs. 1,000.");
        _export = AddExportAction(exportName);
        _columnsButton=AddColumnsAction();
        var reportActions = new List<ModernButton> { run, _print };
        if (classOutstandingPrint)
        {
            _classOutstandingButton=AddBusyAction("Class / Section A4", PreviewClassOutstandingAsync, "Secondary");
            SetToolTip(_classOutstandingButton,"Print all current outstanding within the location filters. For exact-day or minimum-balance filters, use Print.");
            reportActions.Add(_classOutstandingButton);
        }
        reportActions.Add(_export);
        reportActions.Add(_columnsButton);
        reportActions.AddRange(AdditionalReportActions());
        RelocateFromActions(reportActions.ToArray());
        Defer(BindLookupsAsync);
        var fields = new List<(string, Control)> { ("View", View) };
        if (usesDates) { fields.Add(("From (optional)", From)); fields.Add(("To (optional)", To)); }
        if (usesYear) fields.Add(("Academic Year", Year));
        fields.Add(("Campus", Campus));
        fields.Add(("Class", Class));
        fields.Add(("Section", Section));
        if (paymentMethod) fields.Add(("Payment Method", PaymentMethod));
        if (grouping) fields.Add((GroupingCaption, GroupBy));
        fields.AddRange(AdditionalReportFilters());
        fields.Add(("Order by", _orderBy));
        ReportUi.RegisterChoiceFilter(_orderBy);
        SetToolTip(_orderBy, "Optional. Default uses this report view's normal order. Choose another order only when needed, then Run Report. Print and export follow the same order. Grouped receipts sort within each group; totals stay at the end.");
        UiHelper.SetGridPlaceholderMessage(Grid, UiText.EmptyGrid.RunReport);
        var content=ReportUi.CompactLayout(ReportUi.Bar(fields.ToArray()), Grid, title, () => {
            _savedViews?.ResetDates();
            _rangeValidator?.ClearAll();
            _orderBy.SelectedIndex = 0;
            _scopeEdited = true;
            ReportLayoutStore.SetPreference(_scopePreferenceKey, "");
            _displayed = null;
            _appliedSignature = _filterSummary = string.Empty;
            _print.Enabled = _export.Enabled = false;
            _summary.Text = "Run the report to see totals.";
            ReportAvailabilityChanged();
            InvalidateReport();
            SetStatus("Choose filters, then press Run Report.");
        }, reportActions.ToArray());
        _savedViews=new ReportSavedViewsBar(_presetKey,CaptureSavedView,RestoreSavedView,(message,warning)=>SetStatus(message,warning),
            ()=>_lookupsReady && !_runningReport,usesDates ? From : null,usesDates ? To : null,
            usesDates ? ()=>Year.DataSource as DataTable : null,usesYear ? id=>Year.SelectedValue=id : null);
        SetContent(_savedViews.Wrap(content));
        Grid.Parent?.Controls.Add(_summary);
        Grid.BringToFront();
        _locationFilters = new ReportClassSectionFilters(Campus, Class, Section);
        View.SelectedIndexChanged += (_, _) => { UpdateViewControls(); RefreshOrderChoices(); FiltersChanged(); };
        GroupBy.SelectedIndexChanged += (_, _) => { RefreshOrderChoices(); FiltersChanged(); };
        _orderBy.SelectedIndexChanged += (_, _) => FiltersChanged();
        PaymentMethod.SelectedIndexChanged += (_, _) => FiltersChanged();
        From.ValueChanged += (_, _) => FiltersChanged();
        To.ValueChanged += (_, _) => FiltersChanged();
        foreach (var lookup in new[] { Campus, Year, Class, Section })
            lookup.SelectedIndexChanged += (_, _) => {
                if (ReferenceEquals(lookup,Year)) _savedViews?.AcademicYearChanged();
                if (!_applyingScope) _scopeEdited = true;
                FiltersChanged();
            };
        UpdateViewControls();
        RefreshOrderChoices();
        ReportAvailabilityChanged();
    }
    protected virtual IEnumerable<ModernButton> AdditionalReportActions() => Array.Empty<ModernButton>();
    protected virtual IEnumerable<(string, Control)> AdditionalReportFilters() => Array.Empty<(string, Control)>();
    protected static string NumericFilterSignature(string text) => string.IsNullOrWhiteSpace(text) ? "0"
        : decimal.TryParse(text,out decimal value) ? value.ToString(System.Globalization.CultureInfo.InvariantCulture) : text.Trim();
    protected virtual string AdditionalFilterSignature => "";
    protected virtual string AdditionalFilterDescription => "";
    protected virtual bool ValidateAdditionalFilters() => true;
    protected virtual Task<DataSet> ReadReportAsync(SqlParameter[] parameters, CancellationToken token) =>
        DatabaseHelper.QueryUserSetAsync(_procedure, token, parameters);
    protected virtual bool AppendReportTotals => _procedure != "GetOutstandingAndAgingReport" && _procedure != "GetCollectionsAndReceiptsReport";
    protected virtual Func<DataTable, DataTable?, (DataTable Rows, DataTable? Totals)> CaptureResultTransform(SqlParameter[] parameters) =>
        (rows, totals) => { PrepareReportData(rows, parameters); return (rows, totals); };
    protected virtual bool CanPrintClassOutstanding => true;
    protected virtual void ReportAvailabilityChanged()
    {
        if (_classOutstandingButton is not null) _classOutstandingButton.Enabled=CanPrintClassOutstanding;
        if (_columnsButton is not null) _columnsButton.Enabled=CurrentReportAvailable && ReportColumnCatalog is { Count:>0 };
        _savedViews?.RefreshAvailability();
    }
    protected override string ReportLayoutKey => $"{_presetKey}.{ViewName}.{(GroupBy.Enabled ? GroupName : "")}";
    protected override IReadOnlyList<ReportColumnDef>? ReportColumnCatalog => _boundColumnLayout==ReportLayoutKey ? _availableColumns : null;
    protected override IReadOnlyList<string> ReportRequiredColumnKeys => _procedure=="GetCollectionsAndReceiptsReport" && ViewName=="Receipts"
        ? new[] { "Receipt / Reference" } : AppendReportTotals && ReportColumnCatalog is { Count:>0 } ? new[] { ReportColumnCatalog[0].Key } : Array.Empty<string>();
    protected virtual IReadOnlyList<ReportColumnDef> DefineReportColumns()=>ReportAvailableColumns.FromGrid(Grid);
    protected override void OnReportPageBound()
    {
        _availableColumns=DefineReportColumns();_boundColumnLayout=ReportLayoutKey;
        RefreshReportColumnLayout();base.OnReportPageBound();
        LockGridOrder();
    }
    private void RefreshOrderChoices()
    {
        var choices = ReportRowOrder.Choices(_procedure, ViewName, GroupName);
        if (_orderBy.Items.Cast<ReportRowOrder.Choice>().SequenceEqual(choices)) return;
        string previous = _orderBy.Text;
        _orderBy.Items.Clear();
        _orderBy.Items.AddRange(choices.Cast<object>().ToArray());
        _orderBy.SelectedItem = choices.FirstOrDefault(c => c.Label == previous) ?? choices[0];
    }
    private IEnumerable<(string Key,Control Control)> SavedFields()
    {
        yield return ("View",View);yield return ("Grouping",GroupBy);
        if (_usesDates) { yield return ("From",From);yield return ("To",To); }
        if (_usesYear) yield return ("Year",Year);
        yield return ("Campus",Campus);yield return ("Class",Class);yield return ("Section",Section);
        yield return ("Payment method",PaymentMethod);
        foreach(var (caption,control) in AdditionalReportFilters()) yield return (caption,control);
        yield return ("Order by", _orderBy);
    }
    private Dictionary<string,string> CaptureSavedView()
    {
        var state=ReportFilterState.Capture(SavedFields().Where(f=>f.Control is not ModernComboBox combo || combo.Items.Count>0));
        var columns=ReportColumnCatalog is not null ? CurrentReportColumns() : ReportLayoutStore.GetColumns(ReportLayoutKey);
        state["$columns"]=System.Text.Json.JsonSerializer.Serialize(columns);return state;
    }
    private void RestoreSavedView(IReadOnlyDictionary<string,string> state)
    {
        _scopeEdited=true;
        if (!state.ContainsKey("Order by")) _orderBy.SelectedIndex=0;
        // View changes rebuild the grouping choices synchronously before Grouping is restored.
        ReportFilterState.Restore(SavedFields().Where(f=>f.Control is not ModernComboBox combo || combo.Items.Count>0),state);
        if (state.TryGetValue("$columns",out string? columns))
            RememberReportColumns(ReportLayoutKey,System.Text.Json.JsonSerializer.Deserialize<string[]?>(columns));
        _displayed=null;_appliedSignature=_filterSummary="";
        _print.Enabled=_export.Enabled=false;_summary.Text="View applied. Run the report to see current totals.";
        InvalidateReport();ReportAvailabilityChanged();
    }
    protected bool CurrentReportAvailable => !_runningReport && _displayed is not null && _appliedSignature == Signature();
    protected virtual Task PreviewClassOutstandingAsync() => Task.CompletedTask;
    protected override void OnWorkspaceStateRestoring() => _scopeEdited = true;
    protected abstract IEnumerable<SqlParameter> BuildParameters();
    protected virtual bool IsSummaryView => false;
    protected bool ResolvingReportDates=>_savedViews?.IsResolvingDates??false;
    protected string ViewName => View.Text;
    protected string GroupName => GroupBy.Text;
    protected string MethodName => PaymentMethod.Text == "All" ? string.Empty : PaymentMethod.Text;
    private async Task BindLookupsAsync()
    {
        try
        {
            Task<DataTable> campusesTask = DatabaseHelper.QueryUserAsync("GetCampuses");
            Task<DataTable> yearsTask = DatabaseHelper.QueryUserAsync("GetAcademicYears");
            Task<DataTable> classesTask = DatabaseHelper.QueryUserAsync("GetClasses");
            Task<DataTable> sectionsTask = ReportUi.SectionFilterListAsync();
            Task<DataTable> placementsTask = ReportClassSectionLookup.QueryAsync(activeOnly: true);
            await Task.WhenAll(campusesTask, yearsTask, classesTask, sectionsTask, placementsTask);
            if (IsDisposed) return;
            _applyingScope = true;
            try
            {
                Campus.Bind(await campusesTask, "CampusId", "CampusName", "All");
                Year.Bind(await yearsTask, "AcademicYearId", "YearName", "All");
                _locationFilters.Bind(await classesTask, await sectionsTask, await placementsTask);
                if (!_scopeEdited && _appliedSignature.Length == 0) RestoreLastScope();
                _lookupsReady=true;
            }
            finally { _applyingScope = false; }
            _savedViews.RefreshAvailability();
        }
        catch (Exception ex) { if (!IsDisposed) Fail(ex); }
    }
    private ReportScopeSelection CurrentScope() => new(Campus.SelectedId, _usesYear ? Year.SelectedId : 0, Class.SelectedId, Section.SelectedId);
    private void RestoreLastScope()
    {
        var saved = ReportScopeSelection.Read(ReportLayoutStore.GetPreference(_scopePreferenceKey));
        if (saved is null) return;
        void Select(LookupBox box, int id)
        {
            if (id > 0) box.SelectedValue = id;
            if (box.SelectedId <= 0) box.SelectedIndex = 0;
        }
        Select(Campus, saved.Campus);
        if (_usesYear) Select(Year, saved.Year);
        Select(Class, saved.Class);
        Select(Section, saved.Section);
        bool matches = saved == CurrentScope();
        SetStatus(matches ? "Last location filters restored. Review them, then press Run Report."
            : "Some saved filters are unavailable. Review them before running the report.", !matches);
    }
    protected virtual string GroupingCaption => "Group Summary By";
    protected virtual void UpdateViewControls()
    {
        GroupBy.Enabled = IsSummaryView;
    }
    private string Signature() => string.Join('|', ViewName, GroupName, MethodName,
        $"{From.Checked}:{From.Value.Date:yyyy-MM-dd}", $"{To.Checked}:{To.Value.Date:yyyy-MM-dd}",
        Campus.SelectedId, Year.SelectedId, Class.SelectedId, Section.SelectedId, AdditionalFilterSignature, _orderBy.Text);
    protected override string ReportFilterSignature() => Signature();
    protected override string ReportExportTitle() => string.IsNullOrWhiteSpace(ViewName)
        ? NavigationTitle : $"{NavigationTitle} — {ViewName}";
    protected override string ReportExportSubtitle() =>
        _filterSummary.Length == 0 ? base.ReportExportSubtitle() : _filterSummary;
    protected override string? ReportCampusContext => Campus.SelectedId>0?Campus.Text:null;
    protected void FiltersChanged()
    {
        if (_runningReport)
        {
            CancelPendingReport();
            ReportAvailabilityChanged();
            return;
        }
        ReportAvailabilityChanged();
        if (_appliedSignature.Length == 0) return;
        bool current = string.Equals(_appliedSignature, Signature(), StringComparison.Ordinal);
        _print.Enabled = current;
        _export.Enabled = current;
        if (current) RestoreReportStale(); else MarkReportStale();
        SetStatus(current ? UiText.Reports.MatchesFilters : UiText.Reports.FiltersChanged, !current);
    }
    private string FiltersText()
    {
        string Pick(LookupBox box, string label) => $"{label}: {(box.SelectedId > 0 ? box.Text : "All")}";
        var parts = new List<string> { $"View: {ViewName}" };
        if (_usesDates && From.Enabled) parts.Add($"Period: {(From.Checked ? From.Value.ToString("dd-MM-yyyy") : "Any start")} to {(To.Checked ? To.Value.ToString("dd-MM-yyyy") : "Any end")}");
        if (_usesYear) parts.Add(Pick(Year, "Year"));
        parts.Add(Pick(Campus, "Campus"));
        parts.Add(Pick(Class, "Class"));
        parts.Add(Pick(Section, "Section"));
        if (PaymentMethod.Enabled && MethodName.Length > 0) parts.Add($"Method: {MethodName}");
        if (GroupBy.Enabled) parts.Add($"Grouped by: {GroupName}");
        if (AdditionalFilterDescription.Length > 0) parts.Add(AdditionalFilterDescription);
        if (_orderBy.SelectedIndex > 0) parts.Add("Order by: " + _orderBy.Text);
        return string.Join("   ", parts);
    }
    protected virtual void PrepareReportData(DataTable data, SqlParameter[] parameters) { }
    private async Task RunAsync()
    {
        _scopeEdited = true;
        string requested;
        ReportScopeSelection scopeToRemember = CurrentScope();
        try
        {
            _savedViews.ResolveDates();
            requested=Signature();
            if (_usesDates && From.Enabled && !ReportUi.ValidDateRange(_rangeValidator ??= CreateValidator(), From, To)) return;
            if (!ValidateAdditionalFilters()) return;
            _runningReport = true;
            _savedViews.RefreshAvailability();
            _print.Enabled = _export.Enabled = false;
            _summary.Text = "Generating report…";
            SqlParameter[] parameters = BuildParameters().ToArray();
            DataTable? captured = null;
            DataTable? totals = null;
            DateTime generated = DateTime.Now;
            string summary = FiltersText() + $"   Report generated: {generated:dd-MM-yyyy HH:mm}";
            var asOf = parameters.FirstOrDefault(p => p.ParameterName == "@AsOfDate");
            if (asOf?.Value is DateTime date) summary += $"   Aging date: {date:dd-MM-yyyy}";
            var transform = CaptureResultTransform(parameters);
            // Subclasses may consult view controls here. Capture before the
            // provider continuation moves onto a worker thread.
            bool appendTotals = AppendReportTotals;
            var order = (ReportRowOrder.Choice)_orderBy.SelectedItem!;
            await BindReportAsync(async token => {
                DataSet result = await ReadReportAsync(parameters, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                captured = result.Tables.Count > 0 ? result.Tables[0] : new DataTable();
                totals = result.Tables.Count > 1 ? result.Tables[1] : null;
                // Outstanding totals already appear above the grid. Keep its rows
                // limited to actual records so pagination does not count a total.
                if (appendTotals)
                    AppendAuthoritativeTotalRow(captured, totals);
                (captured, totals) = transform(captured, totals);
                captured = ReportRowOrder.Apply(captured, order);
                return captured;
            });
            if (!ReportLanded || captured is null)
            {
                if (!IsDisposed) _summary.Text = "Run the report again to see current totals.";
                return;
            }
            // Pager rows are detached copies. Keep the private full result for
            // print eligibility without duplicating every financial row.
            _displayed = captured;
            _printMetrics=ReportPrintSummary.FromTotals(totals);
            string totalsText = FormatTotals(totals);
            _summary.Text = totalsText;
            _filterSummary = summary + "   " + totalsText;
            _appliedSignature = requested;
            bool current = ReportMatchesCurrentFilters(requested);
            if (current) ReportLayoutStore.SetPreference(_scopePreferenceKey, scopeToRemember.Serialize());
            _print.Enabled = current;
            _export.Enabled = current;
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
            {
                _displayed = null;
                _appliedSignature = _filterSummary = string.Empty;
                _print.Enabled = _export.Enabled = false;
                _summary.Text = "Could not generate the report. Run Report to retry.";
                Fail(ex);
            }
        }
        finally { _runningReport = false; if (!IsDisposed) ReportAvailabilityChanged(); }
    }
    private static string FormatTotals(DataTable? totals)
    {
        if (totals is null || totals.Rows.Count == 0) return "No matching financial activity.";
        DataRow row = totals.Rows[0];
        return string.Join("     ", totals.Columns.Cast<DataColumn>().Select(column => {
            object value = row[column];
            string shown = value == DBNull.Value ? "0" : value is decimal money
                ? $"Rs. {money:N0}" : Convert.ToString(value) ?? "0";
            return $"{ReportPrintSummary.Caption(column.ColumnName)}: {shown}";
        }));
    }
    private static void AppendAuthoritativeTotalRow(DataTable data, DataTable? totals)
    {
        if (data.Columns.Count == 0 || totals is null || totals.Rows.Count == 0) return;
        DataRow source = totals.Rows[0];
        DataRow total = data.NewRow();
        DataColumn? label = data.Columns.Cast<DataColumn>().FirstOrDefault(c => c.DataType == typeof(string));
        if (label is not null) total[label] = "TOTAL";
        var aliases = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) {
            ["Gross Received"] = new[] { "Gross Received" }, ["Refunds"] = new[] { "Refunds" },
            ["Corrections"] = new[] { "Corrections" }, ["Net Collection"] = new[] { "Net Collection", "Net Amount", "Amount" },
            ["Receipts"] = new[] { "Receipts" }, ["Gross Billed"] = new[] { "Gross Charges", "Gross Amount" },
            ["Discounts"] = new[] { "Discounts Applied", "Discount", "Applied Discount" },
            ["Net Billed"] = new[] { "Net Charges", "Net Charge" },
            ["Total Outstanding"] = new[] { "Outstanding" }, ["Students Owing"] = new[] { "Students" }
        };
        bool wroteValue = false;
        foreach (DataColumn metric in totals.Columns)
        {
            if (!aliases.TryGetValue(metric.ColumnName, out string[]? candidates)) continue;
            DataColumn? destination = candidates.Select(name => data.Columns[name]).FirstOrDefault(c => c is not null);
            if (destination is null || source[metric] == DBNull.Value) continue;
            total[destination] = source[metric];
            wroteValue = true;
        }
        if (wroteValue) data.Rows.Add(total);
    }
    private async Task PrintAsync()
    {
        try
        {
            if (_displayed is null) { Warn("Run the report first."); return; }
            if (!string.Equals(_appliedSignature, Signature(), StringComparison.Ordinal)) { Warn("Filters changed. Run the report again before printing."); return; }
            if (_displayed.Rows.Count == 0) { Warn("No records match these filters."); return; }
            await PreviewReportAsync();
        }
        catch (Exception ex) { if (!IsDisposed) Fail(ex); }
    }
}
