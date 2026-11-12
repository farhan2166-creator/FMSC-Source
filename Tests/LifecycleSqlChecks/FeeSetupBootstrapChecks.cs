using Microsoft.Data.SqlClient;
using System.Data;

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

    private static void CheckDefaultAdmissionWithoutTransport(Fixture f, Report report)
    {
        // This is an explicit SQL transaction in the owned disposable fixture:
        // test the real default admission procedure, then roll back every row.
        // Setup Readiness excludes mandatory transport rate requirements when
        // the student has NOT opted into transport; the writer must agree.
        using (var connection = DatabaseHelper.GetConnection())
        {
            connection.Open();
            using var transaction = connection.BeginTransaction();
            try
            {
                using var command = new SqlCommand("dbo.AdmitStudent", connection, transaction) {
                    CommandType = CommandType.StoredProcedure, CommandTimeout = 90
                };
                command.Parameters.AddRange(f.AdmissionArgs("Non Transport Admission Fixture"));
                using var result = new DataSet();
                using (var adapter = new SqlDataAdapter(command)) adapter.Fill(result);
                report.Check(result.Tables.Count > 0 &&
                             result.Tables[^1].Rows.Count == 1,
                    "Default non-transport admission succeeds without a mandatory transport rate.");
                report.Check(SqlCount(connection, transaction,
                    "SELECT COUNT(*) FROM dbo.Students WHERE StudentName=N'Non Transport Admission Fixture';") == 1,
                    "Non-transport admission writes a student only within the rollback test transaction.");
            }
            finally { if (transaction.Connection is not null) transaction.Rollback(); }
        }
        // When the school explicitly opts into transport, a missing mandatory
        // transport fee rate must still block the save.
        bool refused = false;
        using (var connection = DatabaseHelper.GetConnection())
        {
            connection.Open();
            using var transaction = connection.BeginTransaction();
            try
            {
                using var command = new SqlCommand("dbo.AdmitStudent", connection, transaction) {
                    CommandType = CommandType.StoredProcedure, CommandTimeout = 90
                };
                var args = f.AdmissionArgs("Transport Rate Missing Fixture");
                args.Single(p => p.ParameterName == "@UsesTransport").Value = true;
                command.Parameters.AddRange(args);
                using var results = new DataSet();
                using var adapter = new SqlDataAdapter(command);
                adapter.Fill(results);
            }
            catch (SqlException ex) when (ex.Number == 51019) { refused = true; }
            finally
            {
                try { if (transaction.Connection is not null) transaction.Rollback(); }
                catch (InvalidOperationException) { /* XACT_ABORT already rolled back. */ }
            }
        }
        report.Check(refused,
            "A transport opt-in still refuses admission when its mandatory class rate is absent.");
        report.Check(Sql.Count("SELECT COUNT(*) FROM dbo.Students;") == 0,
            "Optional transport scenarios leave no synthetic admissions after rollback.");
    }

    private static int SqlCount(SqlConnection connection, SqlTransaction tx, string sql)
    {
        using var command = new SqlCommand(sql, connection, tx);
        return Convert.ToInt32(command.ExecuteScalar());
    }

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

        // DSA-14: a mandatory transport-only fee with no class rate cannot
        // block a non-transport child when Setup Readiness reports no gap.
        Save(f, f.Purchase, "PURCHASE", "Fictional workbook purchase", "As Needed", true, true);
        var readiness = Sql.Proc("GetSetupReadiness", Sql.P("@UserId", f.Admin));
        report.Check(!readiness.AsEnumerable().Any(r =>
                         Convert.ToString(r["Area"]) == "Fee rates" &&
                         (Convert.ToString(r["Issue"]) ?? "").Contains("Fictional workbook purchase")),
            "Setup Readiness does not require transport-only rates from ordinary admissions.");
        CheckDefaultAdmissionWithoutTransport(f, report);


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
