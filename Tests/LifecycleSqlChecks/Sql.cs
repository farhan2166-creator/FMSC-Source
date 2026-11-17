using Microsoft.Data.SqlClient;
using System.Text.RegularExpressions;

internal static class Sql
{
    internal static void ValidateTarget()
    {
        var target = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("FMSC_AUDIT_CONNECTION")
            ?? throw new InvalidOperationException("The disposable test connection is required."));
        if (!Regex.IsMatch(target.DataSource, @"^127\.0\.0\.1,[1-9][0-9]{0,4}$") ||
            !Regex.IsMatch(target.InitialCatalog, @"^FMSC_AuditChecks_[A-Za-z0-9_]+$") ||
            target.IntegratedSecurity || target.UserID != "sa" || target.AttachDBFilename.Length != 0 ||
            !Guid.TryParse(Environment.GetEnvironmentVariable("FMSC_TEST_RUN_ID"), out var owner))
            throw new InvalidOperationException("Only a marked disposable catalog on the isolated loopback SQL endpoint is accepted.");
        DatabaseHelper.ConnectionString = target.ConnectionString;
        var actual = Scalar("SELECT TRY_CONVERT(uniqueidentifier,value) FROM sys.extended_properties WHERE class=0 AND name=N'FMSC_Level1Owner';");
        if (actual is not Guid id || id != owner) throw new InvalidOperationException("The database ownership marker does not match this test run.");
        string privateRoot = Environment.GetEnvironmentVariable("FMSC_TEST_PRIVATE_ROOT")
            ?? throw new InvalidOperationException("An isolated recovery-journal directory is required.");
        if (!Path.GetFullPath(CollectionRecoveryJournal.JournalPath).StartsWith(Path.GetFullPath(privateRoot) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Collection recovery files must stay inside this run's private directory.");
    }

    internal static SqlParameter P(string name, object? value) => new(name, value ?? DBNull.Value);
    internal static SqlParameter TvpParameter(string name, string type, DataTable value) =>
        new(name, SqlDbType.Structured) { TypeName = "dbo." + type, Value = value };
    internal static DataTable Query(string text, params SqlParameter[] args)
    {
        using var connection = DatabaseHelper.GetConnection(); connection.Open();
        using var command = new SqlCommand(text, connection) { CommandTimeout = 90 };
        command.Parameters.AddRange(args);
        var result = new DataTable(); using var adapter = new SqlDataAdapter(command);
        try { adapter.Fill(result); }
        catch (SqlException ex) { ex.Data["FMSC_TestOperation"] = "Query"; throw; }
        return result;
    }
    internal static object? Scalar(string text, params SqlParameter[] args)
    {
        using var connection = DatabaseHelper.GetConnection(); connection.Open();
        using var command = new SqlCommand(text, connection) { CommandTimeout = 90 }; command.Parameters.AddRange(args);
        try { return command.ExecuteScalar(); }
        catch (SqlException ex) { ex.Data["FMSC_TestOperation"] = "Scalar"; throw; }
    }
    internal static int Count(string text, params SqlParameter[] args) => Convert.ToInt32(Scalar(text, args));
    internal static decimal Money(string text, params SqlParameter[] args) => Convert.ToDecimal(Scalar(text, args));
    internal static DataTable Proc(string name, params SqlParameter[] args)
    {
        using var connection = DatabaseHelper.GetConnection(); connection.Open();
        using var command = new SqlCommand(name, connection) { CommandType = CommandType.StoredProcedure, CommandTimeout = 90 };
        command.Parameters.AddRange(args);
        using var result = new DataSet(); using var adapter = new SqlDataAdapter(command);
        try { adapter.Fill(result); }
        catch (SqlException ex)
        {
            // The synthetic runner never logs parameters or school records.
            // The stable procedure identifier is enough to locate a failing
            // authorization, constraint or transactional invariant.
            ex.Data["FMSC_TestOperation"] = "Procedure " + name;
            throw;
        }
        // Admission with a family link emits nested family results before the
        // final authoritative admission result. Never treat that first row as
        // the newly saved student's identity.
        if (result.Tables.Count == 0) return new DataTable();
        var last = result.Tables[^1]; result.Tables.Remove(last); return last;
    }
    internal static int Key(string procedure, params SqlParameter[] args) => Convert.ToInt32(Proc(procedure, args).Rows[0][0]);
}

namespace FMSC_SMS.Core
{
    // Only the logging sink is replaced. SQL, validation, transaction and recovery code are linked unchanged.
    internal static class AppExceptionHandler
    {
        internal static void LogNonFatal(Exception ex, string context) => Console.WriteLine("NONFATAL: recovery-file cleanup requires review.");
    }
}
