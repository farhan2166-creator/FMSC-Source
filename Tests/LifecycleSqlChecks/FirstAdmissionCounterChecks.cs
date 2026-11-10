using System.Data;
using Microsoft.Data.SqlClient;

// Runs only inside the SQL lifecycle runner's owned, empty school fixture.
// Real admission procedures are used; the surrounding transaction is always
// rolled back, so subsequent lifecycle scenarios begin with no student records.
internal static class FirstAdmissionCounterChecks
{
    internal static void Run(Fixture fixture, Report report)
    {
        report.Check(Sql.Count("SELECT COUNT(*) FROM dbo.Students;") == 0,
            "First-admission check begins without any student records.");
        report.Check(Sql.Count(
            "SELECT COUNT(*) FROM dbo.AdmissionNumberCounter WHERE CounterId=1 AND LastNumber=0;") == 1,
            "Schema upgrade initializes the first official admission counter at zero.");

        using (var connection = DatabaseHelper.GetConnection())
        {
            connection.Open();
            using var transaction = connection.BeginTransaction();
            try
            {
                string next = Convert.ToString(ExecuteScalar(connection, transaction,
                    "dbo.GetNextAdmissionNumber",
                    Sql.P("@UserId", fixture.Admin), storedProcedure: true)) ?? "";
                report.Check(next == "1", "The normal SQL preview proposes admission number 1.");

                int first = Admit(fixture, connection, transaction, "First Student From Empty School");
                report.Check(Convert.ToString(ExecuteScalar(connection, transaction,
                    "SELECT AdmissionNumber FROM dbo.Students WHERE AdmissionId=@Id;",
                    Sql.P("@Id", first))) == "1",
                    "The first direct admission is saved with official number 1.");
                report.Check(Convert.ToInt32(ExecuteScalar(connection, transaction,
                    "SELECT LastNumber FROM dbo.AdmissionNumberCounter WHERE CounterId=1;")) == 1,
                    "The real admission writer advances the same singleton to 1.");

                int second = Admit(fixture, connection, transaction, "Second Student From Empty School");
                report.Check(Convert.ToString(ExecuteScalar(connection, transaction,
                    "SELECT AdmissionNumber FROM dbo.Students WHERE AdmissionId=@Id;",
                    Sql.P("@Id", second))) == "2",
                    "A second direct admission receives a distinct official number 2.");
                report.Check(Convert.ToInt32(ExecuteScalar(connection, transaction,
                    "SELECT LastNumber FROM dbo.AdmissionNumberCounter WHERE CounterId=1;")) == 2,
                    "The counter advances exactly once per successful admission.");
            }
            finally
            {
                transaction.Rollback();
            }
        }

        report.Check(Sql.Count("SELECT COUNT(*) FROM dbo.Students;") == 0 &&
                     Sql.Count("SELECT COUNT(*) FROM dbo.AdmissionNumberCounter WHERE CounterId=1 AND LastNumber=0;") == 1,
            "The two transactional admissions leave no records or counter changes.");

        // Preserve the prior fixture's deterministic numbering for unrelated
        // Lifecycle, SchoolScenarios, mutation and Level3 regression suites.
        Sql.Query("UPDATE dbo.AdmissionNumberCounter SET LastNumber=1000 WHERE CounterId=1;");
        report.Check(Sql.Count(
            "SELECT COUNT(*) FROM dbo.AdmissionNumberCounter WHERE CounterId=1 AND LastNumber=1000;") == 1,
            "Only the synthetic test fixture restores its historical numbering seed.");
    }

    private static int Admit(Fixture fixture, SqlConnection connection,
        SqlTransaction transaction, string name)
    {
        using var command = new SqlCommand("dbo.AdmitStudent", connection, transaction)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = 90
        };
        command.Parameters.AddRange(fixture.AdmissionArgs(name));
        using var data = new DataSet();
        using (var adapter = new SqlDataAdapter(command)) adapter.Fill(data);
        if (data.Tables.Count == 0 || data.Tables[^1].Rows.Count != 1)
            throw new InvalidOperationException("Admission writer did not return its saved student.");
        return Convert.ToInt32(data.Tables[^1].Rows[0]["AdmissionId"]);
    }

    private static object? ExecuteScalar(SqlConnection connection, SqlTransaction transaction,
        string sql, SqlParameter? parameter = null, bool storedProcedure = false)
    {
        using var command = new SqlCommand(sql, connection, transaction)
        {
            CommandType = storedProcedure ? CommandType.StoredProcedure : CommandType.Text,
            CommandTimeout = 90
        };
        if (parameter is not null) command.Parameters.Add(parameter);
        return command.ExecuteScalar();
    }
}
