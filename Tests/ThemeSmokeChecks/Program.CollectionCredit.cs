using System.Reflection;
using FMSC_SMS.Core;
using FMSC_SMS.Pages.Finance.Collection;

internal static partial class Program
{
    private static void CheckCollectionCredit()
    {
        Check(int.TryParse(FMSC_SMS.DatabaseHelper.RequiredDatabaseSchemaVersion, out int schema) && schema >= 1,
            "Application requires the complete first-release baseline or a later schema");
        using var page = new FeeCollectionPage(false);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        // Family summaries resolve official numbers; keep this UI check independent of SQL.
        typeof(AdmissionNumbers).GetField("_numbers", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, new Dictionary<int,string> { [6594]="6590", [6589]="6589" });
        typeof(AdmissionNumbers).GetField("_loadedAt", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, DateTime.UtcNow);
        var type = typeof(FeeCollectionPage);
        object Field(string name) => type.GetField(name, flags)!.GetValue(page)!;
        type.GetField("admissionId", flags)!.SetValue(page, 6594);
        type.GetField("enrollmentId", flags)!.SetValue(page, 732);
        type.GetField("suppressDiscountEvents", flags)!.SetValue(page, true);
        type.GetField("suppressPaymentEvents", flags)!.SetValue(page, true);
        var grid = (DataGridView)Field("charges");
        var credit = (Dictionary<int, decimal>)Field("availableCreditByStudent");
        var use = (CheckBox)Field("useAvailableCredit");
        var discount = (ModernTextBox)Field("discountAmount");
        void Bill(long id, int owner, decimal balance)
        {
            int index = grid.Rows.Add();
            var row = grid.Rows[index];
            row.Cells["BillingChargeId"].Value = id;
            row.Cells["BillingMonth"].Tag = DateTime.Today.AddMonths(1); // already posted future bill
            row.Cells["AdmissionId"].Value = owner;
            row.Cells["EnrollmentId"].Value = 732;
            row.Cells["Balance"].Value = balance;
        }
        decimal Payable() => (decimal)type.GetMethod("CurrentPayableAfterDiscount", flags)!.Invoke(page, null)!;
        Dictionary<long,decimal> Plan() => (Dictionary<long,decimal>)type.GetMethod("GetCreditAllocations", flags)!.Invoke(page, new object?[] { null })!;
        void Allocate(decimal cash) => type.GetMethod("FillFromReceivedAmount", flags)!.Invoke(page, new object[] { cash });
        decimal Cash(int index) => Convert.ToDecimal(grid.Rows[index].Cells["Amount"].Value);
        Bill(16,6594,300); Bill(17,6594,400); credit[6594]=400;
        Allocate(300);
        Check(Cash(0)==0 && Cash(1)==300 && grid.Rows[0].Tag is false,
            "Mixed settlement skips the bill covered by advance and assigns cash to the remaining bill");
        Allocate(100);
        Check(Cash(0)==0 && Cash(1)==100, "Partial cash also skips advance-covered bills");
        discount.Text="100"; Allocate(200);
        Check(Payable()==200 && Cash(0)==0 && Cash(1)==200,
            "Mixed settlement allocates cash after both the largest-fee discount and advance");
        discount.Text="0"; use.Checked=false; Allocate(700);
        Check(Cash(0)==300 && Cash(1)==400, "Opting out of advance restores ordinary cash allocation");
        use.Checked=true; grid.Rows.Clear(); credit.Clear();
        Bill(16,6594,300); Bill(17,6589,400); credit[6594]=400; credit[6589]=100;
        Allocate(300);
        Check(Cash(0)==0 && Cash(1)==300 && !Plan().ContainsKey(17)&&Payable()==400,
            "Ordinary collection uses only the main student's advance; selecting a sibling does not opt in their advance");
        grid.Rows.Clear(); credit.Clear();
        Bill(1,6594,400); Bill(2,6594,2500); Bill(3,6589,400);
        credit[6594]=8750; credit[6589]=100;
        Check(use.Checked, "Credit use is checked by default");
        var plan=Plan();
        Check(plan[1]==400 && plan[2]==2500 && !plan.ContainsKey(3), "Only the main student's posted bills use their advance");
        Check(Payable()==400, "Sibling dues remain payable without borrowing or automatically using sibling advance");
        use.Checked=false;
        Check(Payable()==3300 && Plan().Count==0, "Unticking credit restores the cash balance");
        use.Checked=true;
        discount.Text="2000";
        plan=Plan();
        Check(plan[2]==500 && Payable()==400, "Largest-fee discount is applied before the main student's credit");
        var received = (ModernTextBox)Field("paymentAmount");
        var collect = (Button)Field("executeButton");
        var validator = (FormValidator)Field("collectionValidator");
        void RefreshTotals() => type.GetMethod("UpdateCollectionTotals", flags)!.Invoke(page,null);
        received.Text="401"; RefreshTotals();
        Check(!collect.Enabled && validator.IsMarked(received), "Family cash above the balance after credit is rejected inline");
        received.Text="400"; RefreshTotals();
        Check(collect.Enabled && !validator.IsMarked(received), "Exactly the remaining cash balance is accepted");
        use.Checked=false; discount.Text="0"; received.Text="3300"; RefreshTotals();
        Check(collect.Enabled, "Without credit the full family balance is accepted");
        // Simulate a charge-grid reload with unchanged numeric text. Clicking
        // Use Full Amount must allocate even when TextChanged will not fire.
        foreach (DataGridViewRow row in grid.Rows)
        {
            row.Cells["Amount"].Value=0m;
            row.Tag=false;
            row.Cells["Pay"].Value="+";
        }
        type.GetMethod("UseFullAmount", flags)!.Invoke(page,null);
        Check(grid.Rows.Cast<DataGridViewRow>().Sum(row=>Convert.ToDecimal(row.Cells["Amount"].Value))==3300m,
            "Use Full Amount recalculates a missing allocation even if the displayed amount did not change");
        received.Text="3301"; RefreshTotals();
        Check(!collect.Enabled && validator.IsMarked(received), "Overpayment is rejected even without credit");
        received.Text="3300"; discount.Text="2000"; RefreshTotals();
        Check(!collect.Enabled && validator.IsMarked(received), "Adding a discount revalidates the existing received amount");
        discount.Text="0"; use.Checked=true; RefreshTotals();
        Check(!collect.Enabled && validator.IsMarked(received), "Applying credit revalidates the existing received amount");
        received.Text="0";
        grid.Rows.Clear(); credit.Clear(); Bill(4,6594,8700); credit[6594]=8750; discount.Text="0";
        type.GetMethod("UpdateCollectionTotals", flags)!.Invoke(page,null);
        Check(Payable()==0 && ((Label)Field("remainingCredit")).Text=="50", "Credit-only settlement previews the exact remaining credit");
        received.Text="1"; RefreshTotals();
        Check(!collect.Enabled && validator.IsMarked(received) && ((Label)Field("remainingCredit")).Text=="50",
            "A fully credit-covered bill rejects cash and never previews an implicit credit deposit");
        received.Text="0"; RefreshTotals();
        Check(collect.Enabled && !validator.IsMarked(received), "Credit-only settlement remains available with zero cash");
        Check(((Label)Field("availableCredit")).Text=="8,700", "The advance checkbox shows the amount usable against the listed fees");
        var selectedDate=(ModernDatePicker)Field("paymentDate");
        selectedDate.Value=DateTime.Today.AddDays(-1);
        RefreshTotals();
        Check(!use.Enabled && !use.Checked && Plan().Count==0 && Payable()==8700m,
            "Backdated collection disables advance and restores the full cash payable balance");
        Check(((Label)Field("advanceScopeHint")).Text.Contains("backdated collection"),
            "Backdated collection explains why student advance is unavailable");
        selectedDate.Value=DateTime.Today;
        Check(use.Enabled && !use.Checked, "Returning to today enables advance without silently opting in");
        use.Checked=true;RefreshTotals();
        Check(Plan()[4]==8700m && Payable()==0m,
            "The operator can deliberately select credit again for a collection dated today");
        grid.Rows.Clear();credit.Clear();Bill(5,6589,2300m);credit[6594]=5000m;
        Allocate(0);RefreshTotals();
        Check(Plan().Count==0&&Payable()==2300m&&((Label)Field("availableCredit")).Text=="0"
            &&((Label)Field("remainingCredit")).Text=="5,000",
            "Screenshot regression: the main student's 5000 advance does not settle a sibling's 2300 dues");
        string transferGuidance = ((Label)Field("advanceScopeHint")).Text;
        Check(transferGuidance.Contains("transfer", StringComparison.OrdinalIgnoreCase) &&
              transferGuidance.Contains("sibling", StringComparison.OrdinalIgnoreCase),
            "Unusable main-student advance explains that sibling dues require an explicit transfer");
        Allocate(2300m);received.Text="2300";RefreshTotals();
        Check(Cash(0)==2300m&&collect.Enabled,"Sibling dues can still be collected in cash while the main student's advance remains untouched");
        received.Text="0";grid.Rows.Clear();credit.Clear();Bill(4,6594,8700);credit[6594]=8750;RefreshTotals();
        Check(((Label)Field("availableCredit")).Font.Bold && ((Label)Field("availableCredit")).ForeColor==Color.ForestGreen,
            "Available credit is bold green");
        var layout=(TableLayoutPanel)((Control)Field("creditSummary")).Parent!;
        Check(layout.RowStyles[2].Height>0 && layout.RowStyles[7].Height>0 && layout.RowStyles[6].Height==0,
            "Remaining credit replaces a settled balance and credit rows have space");
        using var form = new Form { ClientSize = new Size(1320,900) };
        page.Dock=DockStyle.Fill; form.Controls.Add(page); form.Show(); Application.DoEvents();
        type.GetMethod("UpdateCollectionTotals", flags)!.Invoke(page,null);
        int captionWidth=TextRenderer.MeasureText(use.Text,use.Font).Width;
        Check(use.Visible && use.Width >= captionWidth + 20,
            $"Credit checkbox fits payment panel (width={use.Width}, text={captionWidth}, parent={use.Parent?.ClientSize.Width}, caption='{use.Text}')");
        Directory.CreateDirectory("artifacts/collection-credit");
        using var picture=new Bitmap(form.Width,form.Height);
        form.DrawToBitmap(picture,new Rectangle(Point.Empty,picture.Size));
        picture.Save("artifacts/collection-credit/payment-panel.png");
        grid.Rows.Clear(); credit[6594]=50;
        type.GetMethod("UpdateCollectionTotals", flags)!.Invoke(page,null);
        Check(layout.RowStyles[0].Height==0 && ((Label)Field("remainingCredit")).Text=="50",
            "After full settlement, remaining credit replaces the empty outstanding summary");
        Check(((Label)Field("collectionResult")).Parent is null, "Transaction text is absent below Next Student");
        page.ConfigureCounter(new[]{6594,6589});
        grid.Rows.Clear();credit.Clear();Bill(6,6594,300);Bill(7,6589,400);credit[6594]=400;credit[6589]=100;
        plan=Plan();
        Check(plan[6]==300&&plan[7]==100&&Payable()==300,
            "The fixed family counter retains explicit per-child credit without transferring money between children");
        var split=(DataGridView)Field("familyProposal");
        Check(split.Columns["Outstanding"].MinimumWidth>=90 && split.Columns["Proposed"].MinimumWidth>=90,
            "Counter allocation reserves readable widths for owed and payable amounts");
        Check(!((Control)Field("student")).Enabled && !((Control)Field("siblings")).Visible &&
            !((Control)Field("studentFeesMenuButton")).Visible,"Counter keeps its selected group and leaves exceptional finance work outside this payment step");
    }
}
