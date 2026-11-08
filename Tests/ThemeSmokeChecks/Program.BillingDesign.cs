using System.Data;
using System.Reflection;
using FMSC_SMS;

internal static partial class Program
{
    private static void CheckBillingDesignDialogs()
    {
        string output = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "billing-ui"));
        Directory.CreateDirectory(output);
        var assembly = typeof(MainDashboardForm).Assembly;
        Form Create(string name, params object[] parameters) => (Form)Activator.CreateInstance(
            assembly.GetType(name, throwOnError: true)!, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, parameters, null)!;
        void Draw(Form form, string name)
        {
            using (form)
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-32000, -32000);
                form.ShowInTaskbar = false;
                form.Show(); Application.DoEvents(); form.PerformLayout();
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, form.ClientRectangle);
                bitmap.Save(Path.Combine(output, name + ".png"));
                var visibleContent = form.Controls.Cast<Control>()
                    .Where(control => control.Visible && control.Width > 0 && control.Height > 0)
                    .ToArray();
                Check(form.ClientSize.Width > 0 && form.ClientSize.Height > 0 &&
                      visibleContent.Any(control => form.ClientRectangle.IntersectsWith(control.Bounds)),
                    name + " exposes visible billing controls within its usable viewport");
                Check(new FileInfo(Path.Combine(output, name + ".png")).Length > 0,
                    name + " produces nonempty visual review evidence");
                form.Close();
            }
        }
        var months = new DataTable();
        months.Columns.Add("BillingMonth", typeof(DateTime)); months.Columns.Add("Students", typeof(int));
        months.Columns.Add("Charges", typeof(int)); months.Columns.Add("Amount", typeof(decimal));
        DateTime month = new(DateTime.Today.Year, DateTime.Today.Month, 1);
        months.Rows.Add(month.AddMonths(-1), 25, 30, 250000m); months.Rows.Add(month, 25, 30, 250000m);
        Draw(Create("FMSC_SMS.Core.BillingRecoveryDialog", months), "missed-month-choice");
        Draw(Create("FMSC_SMS.Pages.Finance.Collection.StudentCreditDialog", 1, false), "student-credit");
        Draw(Create("FMSC_SMS.Pages.Finance.Configuration.FeeRateCorrectionDialog", 1, 1, 1,
            "Synthetic campus / class / fee"), "bill-rate-corrections");
        var period = new DataTable();
        period.Columns.Add("Frequency"); period.Columns.Add("ConcessionLabel"); period.Columns.Add("EndDate", typeof(DateTime));
        period.Rows.Add("Monthly", "Scholarship", month.AddMonths(8));
        Draw(Create("FMSC_SMS.Pages.Finance.StudentAccounts.ChangeFeeDiscountForm", 1, "Tuition", 10000m, 2000m, period.Rows[0]), "assignment-concession");
    }
}
