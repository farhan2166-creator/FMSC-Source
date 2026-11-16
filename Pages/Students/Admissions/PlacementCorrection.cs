using Microsoft.Data.SqlClient;
namespace FMSC_SMS.Pages.Students.Admissions;

internal static class PlacementCorrection
{
    private static readonly string Sql = LoadSql();
    private static string LoadSql()
    {
        using var stream = typeof(PlacementCorrection).Assembly.GetManifestResourceStream(
            "FMSC_SMS.Pages.Students.Admissions.PlacementCorrection.sql")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("-- MISSING_PLACEMENT_POLICY", MissingPlacementPolicy.Sql);
    }
    internal static Task<DataTable> ReviewAsync(int admission, int enrollment, int year, int placement,
        DateTime start, DateTime? leaving, DateTime? billing, string status, string reason, bool legacy,
        bool recordHistoricalDeparture, string? snapshot = null)
    {
        int user = CurrentUser.UserId;
        return Task.Run(() => DatabaseHelper.QuerySql(Sql,
            DatabaseHelper.Int("@UserId", user), DatabaseHelper.Int("@AdmissionId", admission),
            DatabaseHelper.Int("@EnrollmentId", enrollment), DatabaseHelper.Int("@YearId", year),
            DatabaseHelper.Int("@PlacementId", placement), DatabaseHelper.Date("@StartDate", start),
            DatabaseHelper.P("@LeaveDate", leaving.HasValue ? leaving.Value : DBNull.Value),
            DatabaseHelper.P("@BillingStartDate", billing.HasValue ? billing.Value : DBNull.Value),
            DatabaseHelper.P("@Status", status), DatabaseHelper.P("@Reason", reason),
            DatabaseHelper.Bit("@RecordHistoricalDeparture", recordHistoricalDeparture),
            DatabaseHelper.Bit("@Legacy", legacy), DatabaseHelper.Bit("@Preview", snapshot is null),
            DatabaseHelper.P("@ExpectedSnapshot", snapshot is null ? DBNull.Value : snapshot)));
    }
}
