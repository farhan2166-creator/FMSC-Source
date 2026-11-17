using FMSC_SMS.Core;
using Microsoft.Data.SqlClient;

namespace FMSC_SMS.Pages.Reports.Certificates;

public sealed class CertificateIssueRegisterPage : V2Page
{
    private readonly ModernComboBox type=new(),recipient=new(),entry=new(),collection=new(),orderBy=new();
    private readonly ModernTextBox search=new() { PlaceholderText="Name, student/staff no. or certificate no." };
    private readonly ModernDatePicker from=new() { Format=DateTimePickerFormat.Custom,CustomFormat="dd-MM-yyyy",ShowCheckBox=true,Checked=false };
    private readonly ModernDatePicker to=new() { Format=DateTimePickerFormat.Custom,CustomFormat="dd-MM-yyyy",ShowCheckBox=true,Checked=false };
    private readonly Label summary=new() { Dock=DockStyle.Top,AutoSize=true,Padding=new Padding(8),Text="Run the report to see totals.",Font=AppTheme.ButtonFont,ForeColor=AppTheme.HeadingText };
    private readonly ModernButton print,export;
    private readonly FormValidator validator;
    private string applied="",subtitle="";
    private IReadOnlyList<ReportPrintMetric> printMetrics=Array.Empty<ReportPrintMetric>();
    private protected override IReadOnlyList<ReportPrintMetric>? ReportPrintMetrics() => printMetrics;
    private bool running,hasRows;
    private readonly ReportSavedViewsBar savedViews;
    private readonly ModernButton columns;
    private IReadOnlyList<ReportColumnDef>? availableColumns;

    public CertificateIssueRegisterPage() : base("Certificate Issue Register",
        "Recorded issues and saved certificates. Saved record only does not confirm printing or collection. Dates filter Recorded On; only permitted certificate sources are included.")
    {
        validator=CreateValidator();
        type.Items.Add("All");
        if (CurrentUser.HasPermission("Certificates.Create")) type.Items.AddRange(CertificateIssueRegisterData.GeneralTypes);
        if (CurrentUser.HasPermission("Students.Leave")) type.Items.AddRange(CertificateIssueRegisterData.LeavingTypes);
        recipient.Items.AddRange(new object[] { "All","Student","Staff" });
        entry.Items.AddRange(new object[] { "All","Issue logged","Saved record only" });
        collection.Items.AddRange(new object[] { "All","Collected","Not Collected","Not tracked" });
        orderBy.Items.AddRange(ReportRowOrder.Choices("CertificateIssueRegister", "").Cast<object>().ToArray());
        SetToolTip(orderBy, "Optional. Default shows the most recently recorded certificates first. Choose another order only when needed, then Run Report. Print and export use the same order across all pages.");
        foreach (var combo in new[] { type,recipient,entry,collection,orderBy })
        {
            combo.SelectedIndex=0; ReportUi.RegisterChoiceFilter(combo);
            combo.SelectedIndexChanged+=(_,_) => FiltersChanged();
        }
        from.ValueChanged+=(_,_) => FiltersChanged(); to.ValueChanged+=(_,_) => FiltersChanged(); search.TextChanged+=(_,_) => FiltersChanged();
        var run=AddBusyAction("Run Report",RunAsync,"Primary");
        print=AddBusyAction(UiText.Actions.Print,PrintAsync,"Secondary"); export=AddExportAction("FMSC_Certificate_Issue_Register");
        columns=AddColumnsAction();columns.Enabled=false;
        RelocateFromActions(run,print,export,columns);
        // One short filter band. Result-specific tools sit beside the records heading.
        var content=ReportUi.CompactRegisterLayout(ReportUi.Bar(("Certificate",type),("Recipient",recipient),
            ("Recorded from",from),("Recorded to",to),("Entry",entry),("Collection",collection),
            ("Find",search),("Sort",orderBy)),Grid,ClearReport,run,print,export,columns);
        savedViews=new ReportSavedViewsBar("certificates.issue-register",CaptureSavedView,RestoreSavedView,(message,warning)=>SetStatus(message,warning),()=>!running && CanRead,from,to);
        SetContent(savedViews.Wrap(content));
        Grid.Parent?.Controls.Add(summary); Grid.BringToFront();
        UiHelper.SetGridPlaceholderMessage(Grid,UiText.EmptyGrid.RunReport);
        print.Enabled=export.Enabled=false;
    }
    private bool CanRead => CurrentUser.HasPermission("Reports.View")
        && (CurrentUser.HasPermission("Certificates.Create") || CurrentUser.HasPermission("Students.Leave"));
    private string Signature() => string.Join('|',CurrentUser.UserId,CurrentUser.HasPermission("Reports.View"),
        CurrentUser.HasPermission("Certificates.Create"),CurrentUser.HasPermission("Students.Leave"),
        type.Text,recipient.Text,entry.Text,collection.Text,search.Text.Trim(),from.Checked,from.Value.Date,to.Checked,to.Value.Date,orderBy.Text);
    protected override string ReportFilterSignature() => Signature();
    protected override string ReportExportSubtitle() => subtitle;
    protected override string ReportLayoutKey => "certificates.issue-register.columns";
    protected override IReadOnlyList<ReportColumnDef>? ReportColumnCatalog => availableColumns;
    // Less horizontal scrolling for new users; existing column choices and saved views win.
    protected override IReadOnlyList<string>? ReportDefaultColumnKeys => new[] {
        "Recorded On", "Certificate Type", "Certificate No.", "Recipient",
        "Student / Staff No.", "Certificate Date", "Entry", "Issue Type",
        "Collection", "Collected On"
    };
    protected override void OnReportPageBound()
    {
        availableColumns=ReportAvailableColumns.FromGrid(Grid);RefreshReportColumnLayout();base.OnReportPageBound();
        LockGridOrder();
    }
    private (string Key,Control Control)[] SavedFields() => new (string,Control)[] {
        ("Type",type),("Recipient",recipient),("From",from),("To",to),("Entry",entry),("Collection",collection),("Search",search),("Order by",orderBy)
    };
    private Dictionary<string,string> CaptureSavedView()
    {
        var state=ReportFilterState.Capture(SavedFields());
        state["$columns"]=System.Text.Json.JsonSerializer.Serialize(availableColumns is null ? ReportLayoutStore.GetColumns(ReportLayoutKey) : CurrentReportColumns());return state;
    }
    private void RestoreSavedView(IReadOnlyDictionary<string,string> state)
    {
        if (!state.ContainsKey("Order by")) orderBy.SelectedIndex=0;
        ReportFilterState.Restore(SavedFields(),state);
        if (state.TryGetValue("$columns",out var value)) RememberReportColumns(ReportLayoutKey,System.Text.Json.JsonSerializer.Deserialize<string[]?>(value));
        ClearReport();columns.Enabled=false;
    }
    private void ClearReport()
    {
        savedViews?.ResetDates();
        validator.ClearAll();
        applied=subtitle=""; hasRows=false; print.Enabled=export.Enabled=false;
        summary.Text="Run the report to see totals."; InvalidateReport();
        columns.Enabled=false;
    }
    private void FiltersChanged()
    {
        if (running) CancelPendingReport();
        savedViews?.RefreshAvailability();
        if (applied.Length==0) return;
        bool current=CanRead && applied==Signature(); print.Enabled=export.Enabled=current && hasRows && !running;
        columns.Enabled=current && hasRows && !running;
        if (current) RestoreReportStale(); else MarkReportStale();
        SetStatus(current ? UiText.Reports.MatchesFilters : UiText.Reports.FiltersChanged,!current);
    }
    private static string? Selected(ModernComboBox choice) => choice.Text=="All" ? null : choice.Text;
    private async Task RunAsync()
    {
        if (!CanRead) { Warn("Report and certificate access are required."); return; }
        savedViews.ResolveDates();
        if (!ReportUi.ValidDateRange(validator,from,to)) return;
        string requested=Signature();
        string query=CertificateIssueRegisterData.BuildQuery(CurrentUser.HasPermission("Certificates.Create"),CurrentUser.HasPermission("Students.Leave"));
        SqlParameter[] parameters={ DatabaseHelper.Int("@UserId",CurrentUser.UserId),
            DatabaseHelper.P("@FromDate",from.Checked ? from.Value.Date : null),DatabaseHelper.P("@ToDate",to.Checked ? to.Value.Date : null),
            DatabaseHelper.P("@CertificateType",Selected(type)),DatabaseHelper.P("@RecipientType",Selected(recipient)),
            DatabaseHelper.P("@EntryKind",Selected(entry)),DatabaseHelper.P("@CollectionStatus",Selected(collection)),DatabaseHelper.P("@Search",search.Text.Trim()) };
        string captured=$"Type: {type.Text} | Recipient: {recipient.Text} | Entry: {entry.Text} | Collection: {collection.Text}"
            +$" | Recorded dates: {(from.Checked ? from.Value.ToString("dd-MM-yyyy") : "Any start")} to {(to.Checked ? to.Value.ToString("dd-MM-yyyy") : "Any end")}"
            +$" | Find: {search.Text.Trim()} | Sources: {(CurrentUser.HasPermission("Certificates.Create") ? "General certificates" : "")}{(CurrentUser.HasPermission("Students.Leave") ? " Leaving certificates" : "")}"
            +(orderBy.SelectedIndex>0 ? " | Order by: "+orderBy.Text : "")
            +$" | Report generated: {DateTime.Now:dd-MM-yyyy HH:mm}";
        try
        {
            running=true; hasRows=false; print.Enabled=export.Enabled=false; summary.Text="Generating report…";
            columns.Enabled=false;savedViews.RefreshAvailability();
            DataTable? rows=null;
            var order=(ReportRowOrder.Choice)orderBy.SelectedItem!;
            await BindReportAsync(token => Task.Run(() => rows=CertificateIssueRegisterData.Display(
                ReportRowOrder.Apply(DatabaseHelper.QuerySql(query,parameters),order)),token));
            if (IsDisposed) return;
            if (!ReportLanded || rows is null) { summary.Text="Run the report again to see current totals."; return; }
            var totals=CertificateIssueRegisterData.Totals(rows);
            printMetrics=ReportPrintSummary.FromTotals(totals);
            summary.Text=$"Logged issues: {totals.Rows[0]["Logged issues"]:N0}    " +
                $"Saved only: {totals.Rows[0]["Saved records only"]:N0}    " +
                $"Awaiting collection: {totals.Rows[0]["Awaiting collection"]:N0}";
            subtitle=captured+" | "+summary.Text+" | Saved record only does not confirm printing or collection.";
            applied=requested; hasRows=rows.Rows.Count>0;
            bool current=CanRead && ReportMatchesCurrentFilters(requested); print.Enabled=export.Enabled=current && hasRows;
            columns.Enabled=current && hasRows;
        }
        catch (Exception ex)
        {
            if (!IsDisposed) { applied=subtitle=""; summary.Text="Could not generate the report. Run Report to retry."; Fail(ex); }
        }
        finally { running=false;if (!IsDisposed) savedViews.RefreshAvailability(); }
    }
    private async Task PrintAsync()
    {
        if (!CanRead || applied!=Signature()) { Warn("Run the report again before printing."); return; }
        if (!hasRows) { Warn("No records match these filters."); return; }
        try { await PreviewReportAsync(); }
        catch (Exception ex) { if (!IsDisposed) Fail(ex); }
    }
}
