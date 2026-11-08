using System.Data;
using System.Reflection;
using FMSC_SMS.Core;
using FMSC_SMS.Pages.Security;
using FMSC_SMS.Pages.Finance.Billing;

internal static partial class Program
{
    private static void CheckBillingGrid()
    {
        var rows = new DataTable();
        rows.Columns.Add("AdmissionId", typeof(int));
        foreach (string column in new[] { "AdmissionNumber", "StudentName", "Reason" }) rows.Columns.Add(column);
        rows.Columns.Add("IsLegacyReference", typeof(bool));
        rows.Rows.Add(1, "10", "One", "Review", true);
        rows.Rows.Add(2, "2a", "Two", "Review", true);
        rows.Rows.Add(3, "2", "Three", "Review", true);
        var students = new DataTable(); students.Columns.Add("AdmissionId", typeof(int));
        foreach (string column in new[] { "FatherName", "ClassName", "SectionName" }) students.Columns.Add(column);
        students.Rows.Add(1, "Father one", "Class 2", "A");
        students.Rows.Add(2, "Father two", DBNull.Value, DBNull.Value);
        students.Rows.Add(3, "Father three", "Class 1", DBNull.Value);
        BillingStatusPage.AddStudentDetails(rows, students);
        Check((string)rows.Rows[0]["FatherName"] == "Father one", "Father name is retained");
        Check((string)rows.Rows[0]["ClassSection"] == "Class 2 - A", "Existing class and section are combined");
        Check((string)rows.Rows[1]["ClassSection"] == "", "Missing placement stays blank");
        Check((string)rows.Rows[2]["ClassSection"] == "Class 1", "Class without a section has no trailing separator");
        var data = new DataSet(); data.Tables.Add(); data.Tables.Add(); data.Tables.Add(rows);
        using var page = new BillingStatusPage(null, () => Task.FromResult(data), false);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(BillingStatusPage).GetField("review", flags)!.SetValue(page, data);
        var view = (ModernComboBox)typeof(BillingStatusPage).GetField("view", flags)!.GetValue(page)!;
        using var form = new Form(); form.Controls.Add(page); form.Show(); Application.DoEvents();
        view.SelectedIndex = 2; Application.DoEvents();
        var grid = (DataGridView)typeof(V2Page).GetField("Grid", flags)!.GetValue(page)!;
        var bound = (DataView)grid.DataSource!;
        Check(bound.Cast<DataRowView>().Select(row => (string)row["AdmissionNumber"]).SequenceEqual(new[] { "2", "2a", "10" }), "Admission numbers use natural numeric and suffix order");
        Check(new[] { "AdmissionNumber", "StudentName", "FatherName", "ClassSection" }
                .All(name => grid.Columns.Contains(name) && grid.Columns[name].Visible),
            "Billing review retains admission number, student, guardian and placement regardless of display order");

        // Billing SQL permits Fees.Collect alone; the separate student list does
        // not. An operator without Students.View must still get the base review.
        var prior = (CurrentUser.UserId, CurrentUser.Username, CurrentUser.RoleName,
            CurrentUser.MustChangePassword, CurrentUser.GetPermissions().ToArray());
        try
        {
            CurrentUser.SignIn(950, "Billing-only fixture", "Accountant", false, new[] { "Fees.Collect" });
            using var restricted = new DataSet();
            var status = new DataTable();
            status.Columns.Add("BillingMonth", typeof(DateTime));
            status.Columns.Add("CurrentCharges", typeof(int));
            status.Columns.Add("CurrentAmount", typeof(decimal));
            status.Rows.Add(new DateTime(2026, 11, 1), 2, 1500m);
            restricted.Tables.Add(status);
            restricted.Tables.Add(new DataTable());
            var pending = new DataTable();
            pending.Columns.Add("AdmissionId", typeof(int));
            pending.Columns.Add("AdmissionNumber");
            pending.Columns.Add("StudentName");
            pending.Columns.Add("Reason");
            pending.Columns.Add("IsLegacyReference", typeof(bool));
            pending.Rows.Add(1, "41", "Synthetic student", "Needs review", false);
            restricted.Tables.Add(pending);
            using var collectOnly = new BillingStatusPage(null, () => Task.FromResult(restricted), false);
            ((Task)typeof(BillingStatusPage).GetMethod("RefreshAsync", flags)!.Invoke(collectOnly, null)!)
                .GetAwaiter().GetResult();
            var reviewField = typeof(BillingStatusPage).GetField("review", flags)!;
            Check(ReferenceEquals(reviewField.GetValue(collectOnly), restricted),
                "Collect-only operator loads SQL-authorized billing review without student lookup");
            Check(!pending.Columns.Contains("FatherName") && !pending.Columns.Contains("ClassSection"),
                "Collect-only review does not attach student-reader details");
            var restrictedView = (ModernComboBox)typeof(BillingStatusPage).GetField("view", flags)!.GetValue(collectOnly)!;
            restrictedView.SelectedIndex = 1;
            var restrictedGrid = (DataGridView)typeof(V2Page).GetField("Grid", flags)!.GetValue(collectOnly)!;
            Check(restrictedGrid.DataSource is DataView limited && limited.Count == 1 &&
                Convert.ToInt32(limited[0]["AdmissionId"]) == 1,
                "Collect-only operator can review billing exceptions without student viewing access");

            int lookupCalls = 0;
            var allowedRows = pending.Copy();
            CurrentUser.SignIn(951, "Billing and student viewer", "Accountant", false,
                new[] { "Fees.Collect", "Students.View" });
            BillingStatusPage.EnrichStudentDetailsAsync(allowedRows, () => {
                lookupCalls++;
                return Task.FromResult(students);
            }, CurrentUser.HasPermission).GetAwaiter().GetResult();
            Check(lookupCalls == 1 && allowedRows.Columns.Contains("FatherName") &&
                Convert.ToString(allowedRows.Rows[0]["FatherName"]) == "Father one",
                "Student-view permission enables student details from an authorized lookup");

            // Revoking the permission during a completed asynchronous read still
            // must not attach the student's sensitive detail columns.
            bool canView = true;
            var revokedRows = pending.Copy();
            BillingStatusPage.EnrichStudentDetailsAsync(revokedRows, () => {
                canView = false;
                return Task.FromResult(students);
            }, _ => canView).GetAwaiter().GetResult();
            Check(!revokedRows.Columns.Contains("FatherName"),
                "Revoked student-view access cannot enrich a billing review after lookup");
        }
        finally
        {
            CurrentUser.SignIn(prior.UserId, prior.Username, prior.RoleName,
                prior.MustChangePassword, prior.Item5);
        }

    }
}
