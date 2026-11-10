using Microsoft.Data.SqlClient;

// Runs only against the disposable schema-only Lifecycle SQL fixture, after
// creating a synthetic Administrator and before admitting any students.
internal static class FeeSetupBootstrapChecks
{
    private static int Save(Fixture f, int id, string code, string name,
        string frequency, bool? mandatory, bool? transportLinked)
    {
        var parameters = new List<SqlParameter> {
            Sql.P("@UserId", f.Admin), Sql.P("@FeeTypeId", id),
            Sql.P("@FeeTypeCode", code), Sql.P("@FeeTypeName", name),
            Sql.P("@Description", null), Sql.P("@DisplayOrder", 0),
            Sql.P("@DefaultFrequency", frequency)
        };
        if (mandatory.HasValue)
            parameters.Add(Sql.P("@IsMandatory", mandatory.Value));
        if (transportLinked.HasValue)
            parameters.Add(Sql.P("@IsTransportLinked", transportLinked.Value));
        return Sql.Key("SaveFeeTypeWithDefaultFrequency", parameters.ToArray());
    }

    private static DataRow Find(DataTable rows, int id) =>
        rows.AsEnumerable().Single(row => Convert.ToInt32(row["FeeTypeId"]) == id);

    private static DataTable FeeTypes(Fixture f) =>
        Sql.Proc("GetFeeTypes", Sql.P("@UserId", f.Admin));

    private static bool MissingMandatoryFee(Fixture f) =>
        Sql.Proc("GetSetupReadiness", Sql.P("@UserId", f.Admin))
            .AsEnumerable().Any(row => Convert.ToString(row["Area"]) == "Fee types");

    internal static void Run(Fixture f, Report report)
    {
        var firstRead = FeeTypes(f);
        report.Check(firstRead.Columns.Contains("IsMandatory") &&
                     firstRead.Columns.Contains("IsTransportLinked"),
            "Fee Type editor query returns both fee-policy checkboxes.");
        report.Check(Convert.ToBoolean(Find(firstRead, f.Tuition)["IsMandatory"]) &&
                     Convert.ToBoolean(Find(firstRead, f.AdmissionFee)["IsMandatory"]) &&
                     !Convert.ToBoolean(Find(firstRead, f.Purchase)["IsMandatory"]),
            "Mandatory tuition/admission and optional purchases persist through the operator save writer.");
        report.Check(!MissingMandatoryFee(f),
            "Fresh school has no fee-type warning after saving mandatory tuition/admission.");

        // Reproduce the original empty-school problem via the public writer,
        // then resolve the exact readiness warning by an operator-equivalent save.
        Save(f, f.Tuition, "TUITION", "Tuition fee", "Monthly", false, false);
        Save(f, f.AdmissionFee, "ADMISSION", "Admission fee", "Yearly", false, false);
        report.Check(MissingMandatoryFee(f),
            "Setup Readiness warns when no non-transport fee is mandatory.");
        Save(f, f.Tuition, "TUITION", "Tuition fee", "Monthly", true, false);
        report.Check(!MissingMandatoryFee(f) &&
                     Convert.ToBoolean(Find(FeeTypes(f), f.Tuition)["IsMandatory"]),
            "Checking Mandatory Fee in Fee Types resolves the exact readiness warning.");

        // Older callers omit the new parameters. Editing an existing fee must
        // not silently clear either persisted flag.
        Save(f, f.Tuition, "TUITION", "Tuition fee", "Monthly", null, null);
        report.Check(Convert.ToBoolean(Find(FeeTypes(f), f.Tuition)["IsMandatory"]),
            "Legacy saves preserve mandatory policy when new flags are omitted.");

        Save(f, f.Purchase, "PURCHASE", "Fictional workbook purchase", "As Needed", false, true);
        report.Check(Convert.ToBoolean(Find(FeeTypes(f), f.Purchase)["IsTransportLinked"]) &&
                     !MissingMandatoryFee(f),
            "Transport linked policy round-trips and does not replace the non-transport prerequisite.");

        // Restore the fixture's billing semantics before Lifecycle/Level 3 tests.
        Save(f, f.Purchase, "PURCHASE", "Fictional workbook purchase", "As Needed", false, false);
        Save(f, f.AdmissionFee, "ADMISSION", "Admission fee", "Yearly", true, false);
        report.Check(!Convert.ToBoolean(Find(FeeTypes(f), f.Purchase)["IsTransportLinked"]) &&
                     Convert.ToBoolean(Find(FeeTypes(f), f.AdmissionFee)["IsMandatory"]) &&
                     !MissingMandatoryFee(f),
            "Fee editor can restore original policy without affecting existing class rates.");
        report.Check(Sql.Count("SELECT COUNT(*) FROM dbo.FeeConfigurations;") == 4,
            "Changing policy settings does not rewrite class fee rates.");
    }
}
