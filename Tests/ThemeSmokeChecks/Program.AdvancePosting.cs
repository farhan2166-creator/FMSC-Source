using System.Data;
using System.Reflection;
using FMSC_SMS.Core;
using FMSC_SMS.Pages.Finance.Billing;
using FMSC_SMS.Pages.Security;

internal static partial class Program
{
    private static void CheckAdvancePosting()
    {
        var preview = new DataTable();
        preview.Columns.Add("EnrollmentId", typeof(int)); preview.Columns.Add("FeeTypeId", typeof(int));
        preview.Columns.Add("BillingMonth", typeof(DateTime));
        var june = new DateTime(2027, 6, 1); var july = june.AddMonths(1); var august = june.AddMonths(2);
        preview.Rows.Add(1, 2, august); preview.Rows.Add(1, 2, june); preview.Rows.Add(1, 2, july); preview.Rows.Add(2, 2, june);
        Check(FeePostingPage.PreviewMonths(preview).SequenceEqual(new[] { june, july, august }), "Vacation months are distinct and posted chronologically");
        foreach (var month in FeePostingPage.PreviewMonths(preview))
        {
            var selection = Tvp.PostingSelectionsFromPreview(preview, row => (DateTime)row["BillingMonth"] == month);
            Check(selection.AsEnumerable().All(row => (DateTime)row["BillingMonth"] == month), "A monthly procedure receives only its own month");
        }
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        using var page = new FeePostingPage(false);
        var through = (LookupBox)typeof(FeePostingPage).GetField("throughMonth", flags)!.GetValue(page)!;
        var picker = (StudentPicker)typeof(FeePostingPage).GetField("student", flags)!.GetValue(page)!;
        var months = new DataTable(); months.Columns.Add("MonthKey", typeof(int)); months.Columns.Add("Display");
        months.Rows.Add(202708, "August 2027"); through.Bind(months, "MonthKey", "Display");
        typeof(FeePostingPage).GetField("eligibleRows", flags)!.SetValue(page, preview);
        through.SelectedValue = 202708;
        Check(typeof(FeePostingPage).GetField("eligibleRows", flags)!.GetValue(page) is null, "Changing the end month invalidates the old preview");
        var signature = typeof(FeePostingPage).GetMethod("FilterSignature", flags)!;
        string before = (string)signature.Invoke(page, null)!;
        picker.SetStudent(12, "Selected student");
        Check((string)signature.Invoke(page, null)! != before, "Student selection participates in stale-preview protection");
        // Child month dropdown follows the selected starting month, preserving only valid choices.
        var from = (LookupBox)typeof(FeePostingPage).GetField("monthBox", flags)!.GetValue(page)!;
        var available = new DataTable();available.Columns.Add("MonthKey", typeof(int));available.Columns.Add("Display");
        available.Rows.Add(202706, "June 2027"); available.Rows.Add(202707, "July 2027"); available.Rows.Add(202708, "August 2027");
        typeof(FeePostingPage).GetField("postableMonths", flags)!.SetValue(page, available);
        from.Bind(available, "MonthKey", "Display"); from.SelectedValue = 202707;
        Check(through.Items.Cast<DataRowView>().Where(row => Convert.ToInt32(row["MonthKey"]) > 0)
            .Select(row => Convert.ToInt32(row["MonthKey"])) .SequenceEqual(new[] { 202707, 202708 }),
            "Through Month omits months earlier than From Month");
        through.SelectedValue = 202708; from.SelectedValue = 202708;
        Check(through.SelectedId == 202708, "Changing From Month keeps a valid end month");
        from.SelectedValue = 202706; through.SelectedValue = 202706; from.SelectedValue = 202708;
        Check(through.SelectedId == 0 && through.Items.Cast<DataRowView>()
            .Where(row => Convert.ToInt32(row["MonthKey"]) > 0)
            .Select(row => Convert.ToInt32(row["MonthKey"])).SequenceEqual(new[] { 202708 }),
            "Changing From Month clears a stale earlier end month");
        typeof(FeePostingPage).GetField("loadedMonth", flags)!.SetValue(page, june);
        typeof(FeePostingPage).GetField("loadedThrough", flags)!.SetValue(page, august);
        Check((string)typeof(FeePostingPage).GetProperty("PeriodLabel", flags)!.GetValue(page)! == "June 2027 through August 2027", "Confirmation identifies the complete month range");

        var previous=(CurrentUser.UserId,CurrentUser.Username,CurrentUser.RoleName,
            CurrentUser.MustChangePassword,CurrentUser.GetPermissions().ToArray());
        try
        {
            foreach (var role in new[] {
                (Name:"Collector",Grants:new[]{"Fees.Collect"},CanSearch:false),
                (Name:"Collector with student viewing",Grants:new[]{"Fees.Collect","Students.View"},CanSearch:true),
                (Name:"Administrator",Grants:Array.Empty<string>(),CanSearch:true)
            })
            {
                CurrentUser.SignIn(991,role.Name,role.Name=="Administrator"?"Administrator":"Accountant",false,role.Grants);
                using var scope=new FeePostingPage(false);
                var search=(StudentPicker)typeof(FeePostingPage).GetField("student",flags)!.GetValue(scope)!;
                var caption=search.Parent!.Controls.OfType<Label>().Single();
                Check(search.Enabled==role.CanSearch && search.TabStop==role.CanSearch,
                    "Individual fee-posting student search respects Students.View, including administrator bypass");
                Check(caption.Text==(role.CanSearch?"Student (optional)":"Student (restricted)"),
                    "Posting clearly labels a disabled student-only filter");
                if(!role.CanSearch)
                {
                    Check(FinanceHelp.Tooltip(scope,search.Parent!).Contains("View Students"),
                        "Restricted student search explains the missing permission on the enabled field caption");
                    Check(((LookupBox)typeof(FeePostingPage).GetField("campus",flags)!.GetValue(scope)!).Enabled,
                        "Bulk campus/class posting remains available with Fees.Collect alone");
                }
            }
        }
        finally
        {
            if(previous.UserId>0)CurrentUser.SignIn(previous.UserId,previous.Username,previous.RoleName,
                previous.MustChangePassword,previous.Item5);
            else CurrentUser.SignOut();
        }
    }
}
