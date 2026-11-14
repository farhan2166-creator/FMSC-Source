using FMSC_SMS.Core;
using FMSC_SMS.Pages.Reports;
using Microsoft.Data.SqlClient;
namespace FMSC_SMS.Pages.Finance.Billing;

/// <summary>Undo only the latest current-year month, for the whole school, with no payment history.</summary>
// BD-16: reverse the latest eligible posting whole-school and atomically; never offer per-student exclusions.
// SQL must recheck payment history, later postings and the reviewed snapshot.
public sealed class FeePostingReversalPage : V2Page, IBusyPage
{
    private readonly LookupBox monthBox = new();
    private readonly ModernTextBox reason = HumanUi.TextBox(false,500);
    private readonly ModernButton reverseButton;
    private DataTable? previewRows;
    private string? snapshot;
    private DateTime loadedMonth;
    private int readVersion;
    private bool rebinding;
    private bool hasLoaded;
    public bool IsBusy => BusyOperationRunning;
    public string BusyMessage => "The whole month's posting is being reversed. Wait for the result.";
    public FeePostingReversalPage() : this(true) { }
    internal FeePostingReversalPage(bool initialize) : base("Reverse a Posting",
        "Review and reverse the latest monthly posting. The reversal covers the whole school.")
    {
        reason.PlaceholderText="Why was this month posted by mistake?";
        var preview=AddBusyAction("Preview Whole Month",PreviewAsync,"Secondary");
        var refresh=AddBusyAction("Refresh Months",RefreshMonthsAsync,"Secondary");
        reverseButton=AddAction("Reverse Whole Month",async(_,_)=>await ReverseAsync(),"Danger");
        reverseButton.Enabled=false;
        FinanceHelp.Attach(this,monthBox,FinanceHelp.Topic.CancelMonth);
        FinanceHelp.Attach(this,preview,FinanceHelp.Topic.Review);
        FinanceHelp.Attach(this,reverseButton,FinanceHelp.Topic.CancelMonth);
        RelocateFromActions(refresh,preview,reverseButton);
        var filters=ReportUi.CenteredBar(("Latest reversible month",monthBox),("Reason (required)",reason));
        SetContent(ReportUi.WorkflowLayout(filters,Grid,"Reversal Scope",
            "Only the latest month in the current academic year is eligible. Payment history blocks reversal.",
            "Charges to Reverse", "Review the whole month and enter a reason before reversing.",refresh,preview,reverseButton));
        monthBox.DropDownWidth=AppScale.Scale(440);
        monthBox.SelectedIndexChanged+=(_,_)=>{if(!rebinding)ClearPreview();};
        reason.TextChanged+=(_,_)=>UpdateReverse();
        if (initialize) Defer(RefreshMonthsAsync);
        VisibleChanged+=async(_,_)=>{
            if(Visible&&hasLoaded&&!IsBusy)await RefreshMonthsAsync();
        };
    }
    private void UpdateReverse()=>reverseButton.Enabled=!IsBusy&&CurrentUser.HasPermission("Fees.Collect")&&snapshot is not null&&previewRows?.Rows.Count>0&&!string.IsNullOrWhiteSpace(reason.Text);
    private void ClearPreview()
    {
        readVersion++; snapshot=null; previewRows=null; InvalidateReport(); UpdateReverse();
    }
    private static SqlParameter AllFeeTypes()=>new("@FeeTypeIds",SqlDbType.Structured){TypeName="dbo.FeeTypeIdList",Value=Tvp.FeeTypeIds(Array.Empty<int>())};
    private async Task RefreshMonthsAsync()
    {
        ClearPreview(); int version=readVersion;
        try
        {
            var rows=await DatabaseHelper.QueryUserAsync("GetReversibleMonths",AllFeeTypes());
            if(IsDisposed||version!=readVersion)return;
            hasLoaded=true;
            var choices=new DataTable(); choices.Columns.Add("MonthKey",typeof(int)); choices.Columns.Add("Display",typeof(string));
            foreach(DataRow row in rows.Rows)
            {
                DateTime month=Convert.ToDateTime(row["BillingMonth"]);
                choices.Rows.Add(BillingMonthKey.From(month),$"{month:MMMM yyyy} · {row["ChargesAffected"]} charges · reverse whole month");
            }
            rebinding=true;
            try {monthBox.Bind(choices,"MonthKey","Display","No month selected");monthBox.SelectedValue=choices.Rows.Count>0?choices.Rows[0]["MonthKey"]:0;}
            finally {rebinding=false;}
            SetStatus(rows.Rows.Count==0
                ? "No eligible month. Only the latest current-year posting can be reversed; payment history or closed placements block reversal."
                : "Preview the whole month, review the charges and enter a reason. Individual charges cannot be excluded.",rows.Rows.Count==0);
        }
        catch(Exception ex){if(!IsDisposed)Fail(ex);}
    }
    private async Task PreviewAsync()
    {
        ClearPreview();
        DateTime? month=BillingMonthKey.ToMonth(monthBox.SelectedId);
        if(month is null){Warn("There is no selected reversible month. Only the latest current-year month with no payment history can be reversed.");return;}
        int version=readVersion;
        try
        {
            var rows=await DatabaseHelper.QueryUserAsync("PreviewPostingReversal",DatabaseHelper.Date("@BillingMonth",month.Value),AllFeeTypes());
            if(IsDisposed||version!=readVersion)return;
            if (!rows.Columns.Contains("ClassSection")) rows.Columns.Add("ClassSection", typeof(string));
            foreach (DataRow row in rows.Rows)
            {
                string klass = Convert.ToString(row["ClassName"])?.Trim() ?? "";
                string section = Convert.ToString(row["SectionName"])?.Trim() ?? "";
                row["ClassSection"] = klass.Length == 0 ? "" : section.Length == 0 ? klass : $"{klass} - {section}";
            }
            if (rows.Columns.Contains("AdmissionNumber")) rows = AdmissionNumbers.Sort(rows);
            await BindReportAsync(()=>Task.FromResult(rows));
            if(IsDisposed||version!=readVersion){InvalidateReport();return;}
            previewRows=rows; loadedMonth=month.Value;
            snapshot=rows.Rows.Count>0?Convert.ToString(rows.Rows[0]["Snapshot"]):null;
            ConfigureGrid(); UpdateReverse();
            SetStatus($"{loadedMonth:MMMM yyyy}: reverse all {rows.Rows.Count} charge(s), Rs. {rows.AsEnumerable().Sum(r=>Convert.ToDecimal(r["NetAmount"])):N0}. Nothing is saved until you confirm. A new payment or later posting will block reversal.");
        }
        catch(Exception ex){ClearPreview();if(!IsDisposed)Fail(ex);}
    }
    protected override void OnReportPageBound()=>ConfigureGrid();
    private void ConfigureGrid()
    {
        foreach(var name in new[]{"BillingChargeId","HasCollectedMoney","GrossAmount","DiscountAmount","Snapshot","RegistrationNumber","ClassName","SectionName","CollectedAmount","UncollectedAmount"})
            if(Grid.Columns.Contains(name))Grid.Columns[name].Visible=false;
        int index = 0;
        foreach (string name in new[] { "AdmissionNumber", "StudentName", "CampusName", "ClassSection", "FeeTypeName", "ChargeKind", "BillingMonth", "NetAmount", "CollectedAmount", "UncollectedAmount", "ReversalNote" })
            if (Grid.Columns.Contains(name)) Grid.Columns[name].DisplayIndex = index++;
        if (Grid.Columns.Contains("ClassSection")) Grid.Columns["ClassSection"].HeaderText = "Class - Section";
        if (Grid.Columns.Contains("BillingMonth")) Grid.Columns["BillingMonth"].DefaultCellStyle.Format = "MMM yyyy";
    }
    private async Task ReverseAsync()
    {
        try {await RunBusyAsync(BusyMessage,ReverseCoreAsync,this);}
        finally {if(!IsDisposed)UpdateReverse();}
    }
    // Business invariant: this action reverses one eligible latest monthly posting for
    // the entire school, never selected students or individual fee types. The database
    // must recheck payment history, chronology, and the captured snapshot before writing;
    // a preview alone is not authorization. Preserve the reason and all-or-nothing rule.
    private async Task ReverseCoreAsync()
    {
        if(snapshot is null||previewRows is null||previewRows.Rows.Count==0||string.IsNullOrWhiteSpace(reason.Text))
        {Warn("Preview the whole month and enter a reason first.");return;}
        var expected=snapshot; int count=previewRows.Rows.Count;
        decimal total=previewRows.AsEnumerable().Sum(r=>Convert.ToDecimal(r["NetAmount"]));
        if(!Confirm($"Reverse the entire {loadedMonth:MMMM yyyy} posting?\n\nAll {count} charges across the school: Rs. {total:N0}.\n\nNo partial reversal is allowed. Any payment history or later posted month will block the whole operation. Cancelled charges remain in history. No payments are changed.","Reverse Whole Month",defaultYes:false))return;
        try
        {
            var result=await DatabaseHelper.QueryUserAsync("ReverseMonthlyFees",DatabaseHelper.Date("@BillingMonth",loadedMonth),
                DatabaseHelper.P("@Reason",reason.Text.Trim()),AllFeeTypes(),DatabaseHelper.P("@ExpectedSnapshot",expected));
            Info($"Whole month reversed: {result.Rows[0]["ChargesReversed"]} charges, Rs. {Convert.ToDecimal(result.Rows[0]["ReversedAmount"]):N0}. The preceding month is available only if it has no payment history.");
            reason.Clear(); await RefreshMonthsAsync();
        }
        catch(Exception ex){ClearPreview();if(!IsDisposed)Fail(ex);}
    }
}
