using System.Data;
using FMSC_SMS.Core;
using FMSC_SMS.Pages.Administration;
using FMSC_SMS.Pages.Finance.Configuration;
using FMSC_SMS.Pages.Reports.Finance;
using FMSC_SMS.Pages.Security;

internal static partial class Program
{
    private static void CheckDialogSizeTiers()
    {
        // Pure layout inspection: no live SQL, installer, or update access.
        var years = new DataTable();
        years.Columns.Add("AcademicYearId", typeof(int));
        years.Columns.Add("YearName", typeof(string));
        years.Columns.Add("StartDate", typeof(DateTime));
        years.Columns.Add("EndDate", typeof(DateTime));
        years.Columns.Add("IsCurrent", typeof(bool));
        years.Rows.Add(1, "2026-27", new DateTime(2026, 7, 1), new DateTime(2027, 6, 30), true);

        // User-facing minimum space is a usability guard, not a fixed design tier.
        // Related dialogs may legitimately choose different sizes and layouts.
        void CheckUsableSize(Form dialog, string description, int minWidth, int minHeight) =>
            Check(dialog.ClientSize.Width >= AppScale.S(minWidth) &&
                  dialog.ClientSize.Height >= AppScale.S(minHeight),
                  description + " provides usable client space");

        using var period = new ReportAcademicPeriodDialog(years, 12, 1, null);
        using var annual = new AnnualReceiptYearDialog("Example student", years);
        CheckUsableSize(period, "Academic period selection", 360, 180);
        CheckUsableSize(annual, "Annual receipt year selection", 360, 180);

        using var fee = new FeeRateDetailsDialog("Class 1", "Tuition", 500, "Monthly", "");
        CheckUsableSize(fee, "Fee-rate editor", 360, 220);

        using var reset = new PasswordResetDialog("test account",
            (_, _) => Task.FromException<AccessSaveResult>(new InvalidOperationException("UI-only fixture")));
        using var change = new ChangePasswordForm();
        CheckUsableSize(reset, "Account password reset", 380, 280);
        CheckUsableSize(change, "Own password change", 380, 280);

        using var about = new AboutForm();
        using var navigation = new CommandPaletteDialog(Array.Empty<NavigationCommand>());
        CheckUsableSize(about, "About window", 420, 280);
        CheckUsableSize(navigation, "Navigation palette", 420, 280);

        // These constructors accept a no-database initialization mode.
        using var copy = new CopyFeeRatesForm(0, 0, false);
        using var bulk = new BulkFeeRateChangesForm(0, 0, false);
        CheckUsableSize(copy, "Copy fee rates", 600, 380);
        CheckUsableSize(bulk, "Bulk fee-rate changes", 600, 380);

        // The program manager and add-fee dialog load live catalogs in their
        // constructors; their geometry follows the tiers but isn't opened here.
    }
}
