using FMSC_SMS.Core;
using FMSC_SMS.Pages.Reports;
using Microsoft.Data.SqlClient;
namespace FMSC_SMS.Pages.Students.Enrollment;
public sealed class PromotionHistoryPage : V2Page
{
    private readonly LookupBox year = new();
    private readonly LookupBox cls = new();
    private readonly LookupBox section = new();
    private DataTable? sectionCatalog, sourceAssignments;
    private bool bindingSections;
    private readonly ModernButton exportButton;
    private readonly Func<string, SqlParameter[], DataTable> query;
    private readonly (int Year, int Class, int Section) initialFilters;
    /* The filter values that produced the rows currently in the grid. Empty until a load has actually produced a batch history. */
    private string _appliedFilterSignature = string.Empty;
    private readonly Label summary = new() {
        Dock = DockStyle.Top,
        AutoSize = false,
        Height = Math.Max(24, AppMetrics.TextHeight(AppTheme.NormalFont)),
        Font = AppTheme.NormalFont,
        ForeColor = AppTheme.MutedText,
        TextAlign = ContentAlignment.MiddleLeft
    };
    public PromotionHistoryPage(int sourceAcademicYearId = 0, int sourceClassId = 0, int sourceSectionId = 0)
        : this(sourceAcademicYearId, sourceClassId, sourceSectionId,
            (procedure, parameters) => DatabaseHelper.QueryUser(procedure, parameters)) { }
    internal PromotionHistoryPage(int sourceAcademicYearId, int sourceClassId, int sourceSectionId,
        Func<string, SqlParameter[], DataTable> query)
        : base("Promotion History", "Review completed promotions and the students included.")
    {
        this.query = query;
        initialFilters = (sourceAcademicYearId, sourceClassId, sourceSectionId);
        var load = AddAction("Load", (_, _) => LoadData(), "Secondary");
        var export = AddExportAction("Promotion_Batch_History");
        var clear = AddAction(UiText.Actions.Clear, (_, _) => ClearForm(), "Secondary");
        RelocateFromActions(load, export, clear);
        var filters = ReportUi.Bar(("Source Academic Year", year), ("Source Class", cls), ("Source Section", section));
        var filterCard = HumanUi.Card("Filter", "Leave filters blank to view all completed promotions.");
        filterCard.Content.Controls.Add(filters);
        var historyCard = HumanUi.Card(
            "Batch History",
            "See the previous class, next class, promotion date and student totals.");
        historyCard.Content.Controls.Add(Grid);
        historyCard.Content.Controls.Add(summary);
        /* Load, Export and Clear were removed from the action bar and never placed, so the page had no buttons. */
        var buttonRow = HumanUi.ButtonRow(load, export, clear);
        buttonRow.Dock = DockStyle.Fill;
        buttonRow.Margin = new Padding(0);
        var root = new TableLayoutPanel {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.FieldCardHeight(1)));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.ButtonRowHeight));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(filterCard, 0, 0);
        root.Controls.Add(buttonRow, 0, 1);
        root.Controls.Add(historyCard, 0, 2);
        SetContent(root);
        exportButton = export;
        SetReportActionsEnabled(false);
        /* The grid keeps showing the batches an earlier load produced, so a filter change has to be recorded here. */
        cls.SelectedIndexChanged += (_, _) => RefreshSections();
        foreach (var filter in new[] { year, cls, section })
            filter.SelectedIndexChanged += (_, _) => OnFiltersChanged();
        Defer(() => {
            if (BindLookups() && ApplyInitialFilters()) LoadData();
        });
    }
    private bool ApplyInitialFilters()
    {
        foreach (var (lookup, id) in new[] { (year, initialFilters.Year), (cls, initialFilters.Class), (section, initialFilters.Section) })
        {
            if (id <= 0) continue;
            lookup.SelectedValue = id;
            if (lookup.SelectedId == id) continue;
            SetStatus("A selected source filter is no longer available. Choose filters and use Load to view history.", true);
            return false;
        }
        return true;
    }
    protected override string ReportExportFileScope()
    {
        var parts = new List<string>();
        if (year.SelectedId > 0) parts.Add("Year " + year.Text);
        if (cls.SelectedId > 0) parts.Add("Class " + cls.Text);
        if (section.SelectedId > 0) parts.Add("Section " + section.Text);
        return string.Join(" - ", parts);
    }
    private string FilterSignature() =>
        $"{year.SelectedId}|{cls.SelectedId}|{section.SelectedId}";
    private void SetReportActionsEnabled(bool enabled)
    {
        exportButton.Enabled = enabled;
    }
    /* report the displayed batch history as stale the moment the filters no longer match the values that produced it. */
    private void OnFiltersChanged()
    {
        if (string.IsNullOrEmpty(_appliedFilterSignature))
            return;
        bool current = string.Equals(_appliedFilterSignature, FilterSignature(), StringComparison.Ordinal);
        SetReportActionsEnabled(current);
        if (current)
            SetStatus("Batch history matches the current filters.");
        else
            SetStatus("Filters changed. Use Load again before exporting.", true);
    }
    private void RefreshSections()
    {
        if (bindingSections || sectionCatalog is null || sourceAssignments is null) return;
        bindingSections = true;
        try
        {
            int previous = section.SelectedId;
            int classId = cls.SelectedId;
            var allowed = sourceAssignments.AsEnumerable()
                .Where(row => classId <= 0 || Convert.ToInt32(row["ClassId"]) == classId)
                .Select(row => Convert.ToInt32(row["SectionId"])).ToHashSet();
            var choices = sectionCatalog.Clone();
            foreach (DataRow row in sectionCatalog.Rows)
                if (allowed.Contains(Convert.ToInt32(row["SectionId"]))) choices.ImportRow(row);
            section.Bind(choices, "SectionId", "SectionName");
            section.SelectedValue = previous > 0 && allowed.Contains(previous) ? previous : 0;
        }
        finally { bindingSections = false; }
    }
    private bool BindLookups()
    {
        try
        {
            year.Bind(query("GetAcademicYears", Array.Empty<SqlParameter>()), "AcademicYearId", "YearName");
            cls.Bind(query("GetClasses", Array.Empty<SqlParameter>()), "ClassId", "ClassName");
            sectionCatalog = query("GetSections", Array.Empty<SqlParameter>());
            sourceAssignments = query("GetClassSections", new[] {
                DatabaseHelper.Int("@CampusId", 0), DatabaseHelper.Int("@ClassId", 0), DatabaseHelper.Int("@SectionId", 0)
            });
            RefreshSections();
            return true;
        }
        catch (Exception ex)
        {
            Fail(ex);
            return false;
        }
    }
    private void LoadData()
    {
        try
        {
            var data = query(
                "GetPromotionBatchesFiltered",
                new[] {
                    DatabaseHelper.Int("@SourceAcademicYearId", year.SelectedId),
                    DatabaseHelper.Int("@SourceClassId", cls.SelectedId),
                    DatabaseHelper.Int("@SourceSectionId", section.SelectedId)
                });
            Bind(data);
            foreach(DataColumn column in data.Columns)
                if(Grid.Columns.Contains(column.ColumnName)&&column.ColumnName is "ProcessedCount" or "PromotedCount" or "HeldBackCount")
                    Grid.Columns[column.ColumnName].HeaderText=CountLabel(column.ColumnName);
            UpdateSummary(data);
            /* The load is synchronous, so the filters cannot move while the query runs: a successful load belongs to the filters now selected. */
            _appliedFilterSignature = FilterSignature();
            SetReportActionsEnabled(true);
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }
    // Summarizes count columns across the displayed promotion batches.
    private void UpdateSummary(DataTable data)
    {
        var countColumns = data.Columns.Cast<DataColumn>()
            .Where(c =>
                c.ColumnName.EndsWith("Count", StringComparison.OrdinalIgnoreCase) &&
                (c.DataType == typeof(int) || c.DataType == typeof(long) || c.DataType == typeof(short)))
            .ToList();
        if (data.Rows.Count == 0)
        {
            summary.Text = "No batches match the current filter.";
            return;
        }
        string countsText = string.Join(
            "   ",
            countColumns.Select(c =>
                $"{CountLabel(c.ColumnName)}: {data.AsEnumerable().Sum(r => r[c] == DBNull.Value ? 0 : Convert.ToInt64(r[c])):N0}"));
        summary.Text = $"{data.Rows.Count:N0} batch(es).   {countsText}";
    }
    private static string CountLabel(string name)=>name switch {
        "ProcessedCount"=>"Processed","PromotedCount"=>"Promoted","HeldBackCount"=>"Repeated class",_=>name
    };
    private void ClearForm()
    {
        year.SelectedIndex = -1;
        cls.SelectedIndex = -1;
        section.SelectedIndex = -1;
        Grid.DataSource = null;
        summary.Text = "";
        /* The displayed history is gone, so Export must not be able to re-export the cleared batch list as if it were the current one. */
        _appliedFilterSignature = string.Empty;
        SetReportActionsEnabled(false);
        SetStatus("Promotion history filter cleared.");
    }
}
