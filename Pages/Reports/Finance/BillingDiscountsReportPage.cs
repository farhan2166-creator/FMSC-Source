using Microsoft.Data.SqlClient;
namespace FMSC_SMS.Pages.Reports.Finance;
public sealed class BillingDiscountsReportPage : CanonicalFinanceReportPage
{
    private readonly Func<SqlParameter[],CancellationToken,Task<DataSet>>? reportReader;
    private readonly FMSC_SMS.Core.ModernDatePicker asOf = new() {
        Format=DateTimePickerFormat.Custom,CustomFormat="dd-MM-yyyy",Value=DateTime.Today
    };
    public BillingDiscountsReportPage() : this(null) { }
    internal BillingDiscountsReportPage(Func<SqlParameter[],CancellationToken,Task<DataSet>>? reportReader) : base(
        "Billing & Discounts", "Review billed fees, discounts and net amounts payable.",
        "GetBillingAndDiscountsReport", "FMSC_Billing_Discounts",
        new[] { "Billing Summary", "Billing Detail", "Discount Detail", "Concession Assignments", "Concession Summary" },
        new[] { "Day", "Week", "Month", "Class", "Campus", "Fee Type" },
        true, false, false, true)
    { this.reportReader=reportReader;asOf.ValueChanged += (_,_) => FiltersChanged(); }
    protected override IEnumerable<(string,Control)> AdditionalReportFilters() => new[] { ("Concession summary as of",(Control)asOf) };
    protected override string AdditionalFilterSignature => ViewName=="Concession Summary" ? asOf.Value.Date.ToString("yyyy-MM-dd") : "";
    protected override string AdditionalExportFileScope => ViewName == "Concession Summary"
        ? "as of " + asOf.Value.ToString("yyyy-MM-dd") : "";
    protected override void UpdateViewControls()
    {
        base.UpdateViewControls();
        bool summary=ViewName=="Concession Summary";
        asOf.Enabled=summary;
        From.Enabled=To.Enabled=!summary;
    }
    protected override string AdditionalFilterDescription => ViewName == "Concession Summary"
        ? $"Effective agreements on {asOf.Value:dd-MM-yyyy}; one current term per enrolled fee. Monthly and yearly rates stay separate. Not discounts posted to bills." : "";
    protected override bool AppendReportTotals => ViewName != "Concession Summary" && base.AppendReportTotals;
    protected override async Task<DataSet> ReadReportAsync(SqlParameter[] parameters, CancellationToken token)
    {
        if(reportReader is not null)return await reportReader(parameters,token).ConfigureAwait(false);
        if (!Equals(parameters.Single(p=>p.ParameterName=="@View").Value,"Concession Summary"))
            return await base.ReadReportAsync(parameters,token);
        var queryParameters=parameters.Concat(new[] { DatabaseHelper.Int("@UserId",CurrentUser.UserId), DatabaseHelper.Date("@AsOfDate",asOf.Value.Date) }).ToArray();
        var rows=await Task.Run(()=>DatabaseHelper.QuerySql(ConcessionSummaryData.Query,queryParameters),token);
        var result=new DataSet();result.Tables.Add(rows);return result;
    }
    protected override Func<DataTable, DataTable?, (DataTable Rows, DataTable? Totals)> CaptureResultTransform(SqlParameter[] parameters)
    {
        if (ViewName != "Concession Summary") return base.CaptureResultTransform(parameters);
        return (rows,_) => { var summary=ConcessionSummaryData.Create(rows);return (summary.Rows,summary.Totals); };
    }
    protected override bool IsSummaryView => ViewName == "Billing Summary";
    protected override void PrepareReportData(DataTable data, SqlParameter[] parameters)
    {
        if (!Equals(parameters.Single(p => p.ParameterName == "@View").Value, "Billing Summary")) return;
        string group = Convert.ToString(parameters.Single(p => p.ParameterName == "@GroupBy").Value) ?? "";
        string heading = group switch {
            "Day" => "Charge Date",
            "Week" => "Charge Week Starting",
            "Month" => "Charge Month",
            _ => group
        };
        foreach (var (name, label) in new[] {
            ("Group", heading), ("Charges", "Charge Count"),
            ("Gross Charges", "Gross Billed"), ("Discounts Applied", "Discounts"),
            ("Net Charges", "Net Billed") })
            if (data.Columns.Contains(name)) data.Columns[name]!.ColumnName = label;
    }
    protected override IEnumerable<SqlParameter> BuildParameters()
    {
        yield return DatabaseHelper.P("@View", ViewName);
        yield return DatabaseHelper.P("@GroupBy", GroupName);
        yield return DatabaseHelper.Date("@FromDate", From.Checked ? From.Value : null);
        yield return DatabaseHelper.Date("@ToDate", To.Checked ? To.Value : null);
        yield return DatabaseHelper.Int("@CampusId", Campus.SelectedId);
        yield return DatabaseHelper.Int("@ClassId", Class.SelectedId);
        yield return DatabaseHelper.Int("@SectionId", Section.SelectedId);
    }
}
