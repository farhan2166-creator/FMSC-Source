using Microsoft.Data.SqlClient;
namespace FMSC_SMS;
public static partial class DatabaseHelper
{
    public const string DatabaseFolder = @"C:\ProgramData\FMSC_Database";
    public const string DatabaseFile = @"C:\ProgramData\FMSC_Database\FMSC_DB.mdf";
    public const string LocalDbInstance = @"(localdb)\MSSQLLocalDB";
    private const string DatabaseName = "FMSC_DB";
    // The required schema constant is generated at build time from Installer/core.ps1.
#if !FMSC_LEVEL2
    private static volatile bool _databaseAttachConfirmed;
    private static readonly object DatabaseAttachSync = new();
#endif
    public static string DatabaseEndpoint =>
#if FMSC_LEVEL2
        Core.Level2Target.Endpoint;
#else
        Core.LocalDatabaseEngine.Endpoint();
#endif
    private static string ConnectionString => GetConnectionString("FMSC_DB");
    public static string GetConnectionString(string catalog) => new SqlConnectionStringBuilder {
        DataSource = DatabaseEndpoint,
        InitialCatalog =
#if FMSC_LEVEL2
            Core.Level2Target.Catalog,
#else
            catalog,
#endif
        IntegratedSecurity = true,
        TrustServerCertificate = true,
        ConnectTimeout = 8,
        ConnectRetryCount = 0
    }.ConnectionString;
    public static SqlConnection GetMasterConnection() => new(GetConnectionString("master"));
    internal static bool IsConnectionFailure(SqlException ex) =>
        ex.Number is -2 or -1 or 0 or 2 or 20 or 26 or 40 or 53 or 64 or 233 or 1231 or 10053 or 10054 or 10060 or 10061 or 17142
        || ex.Number < -1000;
    private static void OpenWithStartupRetry(SqlConnection cn)
    {
#if FMSC_LEVEL2
        Core.Level2Target.Open(cn);
        return;
#else
        try { cn.Open(); }
        catch (SqlException ex) when (IsConnectionFailure(ex))
        {
            Core.AppExceptionHandler.LogNonFatal(ex, "Refreshing database connection before retry");
            cn.Close();
            SqlConnection.ClearPool(cn);
            _databaseAttachConfirmed = false;
            var builder = new SqlConnectionStringBuilder(cn.ConnectionString) {
                DataSource = Core.LocalDatabaseEngine.Endpoint(refresh: true)
            };
            cn.ConnectionString = builder.ConnectionString;
            cn.Open();
        }
#endif
    }
    public static void RefreshDatabaseConnection(bool restartEngine = false)
    {
#if FMSC_LEVEL2
        // Never restart or discover the operator's normal SQL instance.
        SqlConnection.ClearAllPools();
        Core.Level2Target.Validate();
        Core.StudentSearchCache.Invalidate();
        Core.LookupDataCache.Invalidate();
#else
        lock (DatabaseAttachSync)
        {
            _databaseAttachConfirmed = false;
            SqlConnection.ClearAllPools();
            Core.LocalDatabaseEngine.Forget();
            Core.StudentSearchCache.Invalidate();
            Core.LookupDataCache.Invalidate();
            if (restartEngine) Core.LocalDatabaseEngine.Restart();
        }
#endif
    }
    private static void EnsureDatabaseAttached()
    {
        if (!File.Exists(DatabaseFile))
        {
            throw new FileNotFoundException(
                $"The FMSC database file was not found.\n{DatabaseFile}",
                DatabaseFile);
        }
        using var cn = GetMasterConnection();
        OpenWithStartupRetry(cn);
        using (var checkCmd = new SqlCommand(
            "SELECT COUNT(*) FROM sys.databases WHERE name = @DatabaseName;",
            cn))
        {
            checkCmd.Parameters.Add("@DatabaseName", SqlDbType.NVarChar, 128)
                .Value = DatabaseName;
            var exists = Convert.ToInt32(checkCmd.ExecuteScalar()) > 0;
            if (exists)
            {
                /* An attached database with the right NAME is not proof it is the right database. */
                VerifyDatabaseIdentity();
                return;
            }
        }
        var escapedFile = DatabaseFile.Replace("'", "''");
        var attachSql = $@"
CREATE DATABASE [{DatabaseName}]
ON
(
    FILENAME = N'{escapedFile}'
)
FOR ATTACH;";
        try
        {
            using var attachCmd = new SqlCommand(attachSql, cn);
            attachCmd.CommandTimeout = 120;
            attachCmd.ExecuteNonQuery();
        }
        catch (SqlException ex)
        {
            Core.AppExceptionHandler.LogNonFatal(ex, "School database attach failed");
            throw new Core.DatabaseRecoveryException("AttachFailed",
                "The school data file could not be opened. Your records have not been replaced. " +
                "Try Reconnect; if the file is damaged or locked, use Setup to restore your verified backup.", ex.ToString());
        }
        // A successful attach alone does not establish identity or schema compatibility.
        VerifyDatabaseIdentity();
    }

    private static void EnsureDatabaseReady(bool verifyWhenReady = false)
    {
#if FMSC_LEVEL2
        Core.Level2Target.Validate();
        return;
#else
        if (_databaseAttachConfirmed && !verifyWhenReady)
            return;
        lock (DatabaseAttachSync)
        {
            if (_databaseAttachConfirmed && !verifyWhenReady)
                return;
            try
            {
                if (_databaseAttachConfirmed)
                    VerifyDatabaseIdentity();
                else
                    EnsureDatabaseAttached(); // Both existing and fresh attachments verify once.
                _databaseAttachConfirmed = true;
            }
            catch
            {
                _databaseAttachConfirmed = false;
                throw;
            }
        }
#endif
    }
    public static SqlConnection GetConnection()
    {
        EnsureDatabaseReady();
        return new SqlConnection(ConnectionString);
    }
    public static void OpenConnection(SqlConnection cn)
    {
#if FMSC_LEVEL2
        Core.Level2Target.Open(cn);
#else
        try
        {
            OpenWithStartupRetry(cn);
        }
        catch (SqlException ex) when (ex.Number is 4060 or 911)
        {
            _databaseAttachConfirmed = false;
            EnsureDatabaseReady();
            cn.ConnectionString = GetConnectionString(new SqlConnectionStringBuilder(cn.ConnectionString).InitialCatalog);
            OpenWithStartupRetry(cn);
        }
#endif
    }
    /// <summary>The single definition of "this connection is the FMSC database".</summary>
    private static void VerifyDatabaseIdentity()
    {
        using var cn = new SqlConnection(ConnectionString);
        OpenWithStartupRetry(cn);
        using (var cmd = new SqlCommand("SELECT DB_NAME(), physical_name FROM sys.database_files WHERE file_id=1;", cn))
        using (var r = cmd.ExecuteReader())
        {
            if (!r.Read())
                throw new Core.DatabaseCompatibilityException(new(Core.DatabaseCompatibilityIssue.DatabaseIdentity,
                    RequiredDatabaseSchemaVersion, Details: "The database did not return its identity information."));
            string? name = Convert.ToString(r.GetValue(0));
            string? path = Convert.ToString(r.GetValue(1));
            if (!string.Equals(name, DatabaseName, StringComparison.OrdinalIgnoreCase))
                throw new Core.DatabaseCompatibilityException(new(Core.DatabaseCompatibilityIssue.DatabaseIdentity,
                    RequiredDatabaseSchemaVersion, Details: $"Expected database: {DatabaseName}. Connected database: {name}."));
            if (!string.Equals(Path.GetFullPath(path ?? ""), Path.GetFullPath(DatabaseFile), StringComparison.OrdinalIgnoreCase))
                throw new Core.DatabaseCompatibilityException(new(Core.DatabaseCompatibilityIssue.DatabaseIdentity,
                    RequiredDatabaseSchemaVersion, Details: $"The connected FMSC_DB is not the approved database file.\nExpected: {DatabaseFile}\nConnected: {path}"));
        }
        Core.DatabaseCompatibility.Validate(cn);
    }
    public static bool TestConnection(out string message) => TestConnection(out message, out _);

    internal static bool TestConnection(out string message, out Exception? failure)
    {
        failure = null;
        try
        {
            EnsureDatabaseReady(verifyWhenReady: true);
            /* The verification now lives in one place and is shared with EnsureDatabaseAttached, so this cannot drift from it. */
            message = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            /* Log the real exception (number, state, message) so a failure that the friendly text cannot explain can still be diagnosed from the log. */
            Core.AppExceptionHandler.LogNonFatal(ex, "TestConnection failed");
            failure = ex;
            message = Core.DbMessage.UserMessage(
                ex,
                "The FMSC database could not be opened.");
            return false;
        }
    }
    private static T WithWaitCursor<T>(Func<T> action)
    {
        bool onUiThread = SynchronizationContext.Current is WindowsFormsSynchronizationContext;
        Cursor? previous = onUiThread ? Cursor.Current : null;
        try
        {
            if (onUiThread)
                Cursor.Current = Cursors.WaitCursor;
            return action();
        }
        finally
        {
            if (onUiThread && previous is not null)
                Cursor.Current = previous;
        }
    }
    public static DataTable Query(
        string procedure,
        params SqlParameter[] parameters) =>
        Query(procedure, 120, parameters);
    public static DataTable Query(
        string procedure,
        int commandTimeoutSeconds,
        params SqlParameter[] parameters) =>
        Core.LookupDataCache.Get(procedure, parameters, commandParameters => WithWaitCursor(() => {
            using var cn = GetConnection();
            using var cmd = new SqlCommand(procedure, cn) {
                CommandType = CommandType.StoredProcedure,
                CommandTimeout = commandTimeoutSeconds
            };
            if (commandParameters.Length > 0)
                cmd.Parameters.AddRange(commandParameters);
            using var adapter = new SqlDataAdapter(cmd);
            var dt = new DataTable();
            OpenConnection(cn);
            adapter.Fill(dt);
            Core.LookupDataCache.OperationCompleted(procedure);
            Core.AdmissionNumbers.Enrich(dt, cn);
            Core.RecordTextPolicy.FormatDisplayRows(dt);
            return dt;
        }));
    public static DataTable QuerySql(
        string sql,
        params SqlParameter[] parameters) =>
        QuerySql(sql, 120, parameters);
    public static DataTable QuerySql(
        string sql,
        int commandTimeoutSeconds,
        params SqlParameter[] parameters) =>
        WithWaitCursor(() => {
            using var cn = GetConnection();
            using var cmd = new SqlCommand(sql, cn) {
                CommandType = CommandType.Text,
                CommandTimeout = commandTimeoutSeconds
            };
            if (parameters.Length > 0)
                cmd.Parameters.AddRange(parameters);
            using var adapter = new SqlDataAdapter(cmd);
            var dt = new DataTable();
            OpenConnection(cn);
            adapter.Fill(dt);
            Core.AdmissionNumbers.Enrich(dt, cn);
            Core.RecordTextPolicy.FormatDisplayRows(dt);
            return dt;
        });
    public static DataTable QueryUser(
        string procedure,
        params SqlParameter[] parameters) =>
        QueryUser(procedure, 120, parameters);

    /// <summary>Async text-query reads. Callers retain explicit SQL permission
    /// checks; do not use cancellation-driven reads for financial writes.</summary>
    public static Task<DataTable> QuerySqlAsync(string sql, params SqlParameter[] parameters) =>
        QuerySqlAsync(sql, CancellationToken.None, parameters);

    public static async Task<DataTable> QuerySqlAsync(string sql, CancellationToken token, params SqlParameter[] parameters)
    {
        token.ThrowIfCancellationRequested();
        using var cn = GetConnection();
        using var cmd = new SqlCommand(sql, cn) { CommandType = CommandType.Text, CommandTimeout = 120 };
        cmd.Parameters.AddRange(parameters);
        await OpenConnectionAsync(cn, token).ConfigureAwait(false);
        var table = new DataTable();
        using (var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            do
            {
                if (reader.FieldCount == 0) continue;
                table = await Core.AsyncTableLoader.ReadAsync(reader, "Table", token).ConfigureAwait(false);
                break;
            } while (await reader.NextResultAsync(token).ConfigureAwait(false));
            // Match the first-table contract of QuerySql and finish any later
            // results before issuing enrichment on the same connection.
            while (await reader.NextResultAsync(token).ConfigureAwait(false))
                while (await reader.ReadAsync(token).ConfigureAwait(false)) { }
        }
        await Core.AdmissionNumbers.EnrichAsync(table, cn, token).ConfigureAwait(false);
        Core.RecordTextPolicy.FormatDisplayRows(table);
        token.ThrowIfCancellationRequested();
        return table;
    }


    public static Task<DataTable> QueryUserAsync(string procedure, params SqlParameter[] parameters) =>
        QueryUserCoreAsync(procedure, 120, CancellationToken.None, WithCurrentUser(parameters));
    public static Task<DataTable> QueryUserAsync(string procedure, int commandTimeoutSeconds, params SqlParameter[] parameters) =>
        QueryUserCoreAsync(procedure, commandTimeoutSeconds, CancellationToken.None, WithCurrentUser(parameters));
    public static Task<DataTable> QueryUserAsync(string procedure, CancellationToken cancellationToken, params SqlParameter[] parameters) =>
        QueryUserCoreAsync(procedure, 120, cancellationToken, WithCurrentUser(parameters));
    public static Task<DataTable> QueryUserAsync(string procedure, int commandTimeoutSeconds, CancellationToken cancellationToken, params SqlParameter[] parameters) =>
        QueryUserCoreAsync(procedure, commandTimeoutSeconds, cancellationToken, WithCurrentUser(parameters));
    public static Task<int> ExecuteUserAsync(string procedure, params SqlParameter[] parameters) =>
        ExecuteUserCoreAsync(procedure, 120, CancellationToken.None, WithCurrentUser(parameters));
    public static Task<int> ExecuteUserAsync(string procedure, int commandTimeoutSeconds, params SqlParameter[] parameters) =>
        ExecuteUserCoreAsync(procedure, commandTimeoutSeconds, CancellationToken.None, WithCurrentUser(parameters));
    public static Task<int> ExecuteUserAsync(string procedure, CancellationToken cancellationToken, params SqlParameter[] parameters) =>
        ExecuteUserCoreAsync(procedure, 120, cancellationToken, WithCurrentUser(parameters));
    public static Task<int> ExecuteUserAsync(string procedure, int commandTimeoutSeconds, CancellationToken cancellationToken, params SqlParameter[] parameters) =>
        ExecuteUserCoreAsync(procedure, commandTimeoutSeconds, cancellationToken, WithCurrentUser(parameters));
    public static Task<object?> ScalarUserAsync(string procedure, params SqlParameter[] parameters) =>
        ScalarUserCoreAsync(procedure, 120, CancellationToken.None, WithCurrentUser(parameters));
    public static Task<object?> ScalarUserAsync(string procedure, int commandTimeoutSeconds, params SqlParameter[] parameters) =>
        ScalarUserCoreAsync(procedure, commandTimeoutSeconds, CancellationToken.None, WithCurrentUser(parameters));
    public static Task<object?> ScalarUserAsync(string procedure, CancellationToken cancellationToken, params SqlParameter[] parameters) =>
        ScalarUserCoreAsync(procedure, 120, cancellationToken, WithCurrentUser(parameters));
    public static Task<object?> ScalarUserAsync(string procedure, int commandTimeoutSeconds, CancellationToken cancellationToken, params SqlParameter[] parameters) =>
        ScalarUserCoreAsync(procedure, commandTimeoutSeconds, cancellationToken, WithCurrentUser(parameters));
    public static Task<DataSet> QueryUserSetAsync(string procedure, params SqlParameter[] parameters) =>
        QueryUserSetCoreAsync(procedure, 120, CancellationToken.None, WithCurrentUser(parameters));
    public static Task<DataSet> QueryUserSetAsync(string procedure, int commandTimeoutSeconds, params SqlParameter[] parameters) =>
        QueryUserSetCoreAsync(procedure, commandTimeoutSeconds, CancellationToken.None, WithCurrentUser(parameters));
    public static Task<DataSet> QueryUserSetAsync(string procedure, CancellationToken cancellationToken, params SqlParameter[] parameters) =>
        QueryUserSetCoreAsync(procedure, 120, cancellationToken, WithCurrentUser(parameters));
    public static Task<DataSet> QueryUserSetAsync(string procedure, int commandTimeoutSeconds, CancellationToken cancellationToken, params SqlParameter[] parameters) =>
        QueryUserSetCoreAsync(procedure, commandTimeoutSeconds, cancellationToken, WithCurrentUser(parameters));
    // Accepted local trust boundary (BL-03): SQL receives the signed-in application UserId.
    // This does not authenticate independent clients with the same local SQL rights.
    private static SqlParameter[] WithCurrentUser(SqlParameter[] parameters)
    {
        foreach (SqlParameter parameter in parameters)
        {
            if (!string.Equals(
                    parameter.ParameterName,
                    "@UserId",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            parameter.SqlDbType = SqlDbType.Int;
            parameter.Value = CurrentUser.UserId;
            return parameters;
        }
        var result = new SqlParameter[parameters.Length + 1];
        Array.Copy(parameters, result, parameters.Length);
        result[^1] = new SqlParameter("@UserId", SqlDbType.Int) {
            Value = CurrentUser.UserId
        };
        return result;
    }
    public static DataTable QueryUser(
        string procedure,
        int commandTimeoutSeconds,
        params SqlParameter[] parameters)
    {
        return Query(
            procedure,
            commandTimeoutSeconds,
            WithCurrentUser(parameters));
    }
    public static DataSet QueryUserSet(
        string procedure,
        params SqlParameter[] parameters) =>
        QueryUserSet(procedure, 120, parameters);

    /// <summary>Like QueryUser, but for a procedure that returns more than one result set - e.g. dbo.PreviewMonthlyFees, whose detail rows and summary row are two separate SELECTs.</summary>
    public static DataSet QueryUserSet(
        string procedure,
        int commandTimeoutSeconds,
        params SqlParameter[] parameters)
    {
        SqlParameter[] userParameters = WithCurrentUser(parameters);
        return WithWaitCursor(() => {
            using var cn = GetConnection();
            using var cmd = new SqlCommand(procedure, cn) {
                CommandType = CommandType.StoredProcedure,
                CommandTimeout = commandTimeoutSeconds
            };
            if (userParameters.Length > 0)
                cmd.Parameters.AddRange(userParameters);
            using var adapter = new SqlDataAdapter(cmd);
            var ds = new DataSet();
            OpenConnection(cn);
            adapter.Fill(ds);
            Core.LookupDataCache.OperationCompleted(procedure);
            foreach (DataTable table in ds.Tables) { Core.AdmissionNumbers.Enrich(table, cn); Core.RecordTextPolicy.FormatDisplayRows(table); }
            return ds;
        });
    }

    public static int ExecuteUser(
        string procedure,
        params SqlParameter[] parameters) =>
        ExecuteUser(procedure, 120, parameters);


    public static int ExecuteUser(
        string procedure,
        int commandTimeoutSeconds,
        params SqlParameter[] parameters)
    {
        return Execute(
            procedure,
            commandTimeoutSeconds,
            WithCurrentUser(parameters));
    }
    public static object? ScalarUser(
        string procedure,
        params SqlParameter[] parameters) =>
        ScalarUser(procedure, 120, parameters);
    public static object? ScalarUser(
        string procedure,
        int commandTimeoutSeconds,
        params SqlParameter[] parameters)
    {
        return Scalar(
            procedure,
            commandTimeoutSeconds,
            WithCurrentUser(parameters));
    }
    public static object? Scalar(
        string procedure,
        params SqlParameter[] parameters) =>
        Scalar(procedure, 120, parameters);
    public static object? Scalar(
        string procedure,
        int commandTimeoutSeconds,
        params SqlParameter[] parameters) =>
        WithWaitCursor(() => {
            using var cn = GetConnection();
            using var cmd = new SqlCommand(procedure, cn) {
                CommandType = CommandType.StoredProcedure,
                CommandTimeout = commandTimeoutSeconds
            };
            if (parameters.Length > 0)
                cmd.Parameters.AddRange(parameters);
            OpenConnection(cn);
            var result = cmd.ExecuteScalar();
            Core.LookupDataCache.OperationCompleted(procedure);
            return result;
        });
    public static int Execute(
        string procedure,
        params SqlParameter[] parameters) =>
        Execute(procedure, 120, parameters);
    public static int Execute(
        string procedure,
        int commandTimeoutSeconds,
        params SqlParameter[] parameters) =>
        WithWaitCursor(() => {
            using var cn = GetConnection();
            using var cmd = new SqlCommand(procedure, cn) {
                CommandType = CommandType.StoredProcedure,
                CommandTimeout = commandTimeoutSeconds
            };
            if (parameters.Length > 0)
                cmd.Parameters.AddRange(parameters);
            OpenConnection(cn);
            int result = cmd.ExecuteNonQuery();
            Core.LookupDataCache.OperationCompleted(procedure);
            return result;
        });
    private static async Task OpenConnectionAsync(SqlConnection cn, CancellationToken cancellationToken)
    {
#if FMSC_LEVEL2
        await Core.Level2Target.OpenAsync(cn,cancellationToken).ConfigureAwait(false);
#else
        try
        {
            await OpenWithStartupRetryAsync(cn, cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException ex) when (ex.Number is 4060 or 911)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _databaseAttachConfirmed = false;
            EnsureDatabaseReady();
            cn.ConnectionString = GetConnectionString(new SqlConnectionStringBuilder(cn.ConnectionString).InitialCatalog);
            await OpenWithStartupRetryAsync(cn, cancellationToken).ConfigureAwait(false);
        }
#endif
    }

    private static async Task OpenWithStartupRetryAsync(SqlConnection cn, CancellationToken cancellationToken)
    {
#if FMSC_LEVEL2
        await Core.Level2Target.OpenAsync(cn,cancellationToken).ConfigureAwait(false);
#else
        try { await cn.OpenAsync(cancellationToken).ConfigureAwait(false); }
        catch (SqlException ex) when (IsConnectionFailure(ex))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Core.AppExceptionHandler.LogNonFatal(ex, "Refreshing database connection before retry");
            cn.Close();
            SqlConnection.ClearPool(cn);
            _databaseAttachConfirmed = false;
            var builder = new SqlConnectionStringBuilder(cn.ConnectionString) {
                DataSource = Core.LocalDatabaseEngine.Endpoint(refresh: true)
            };
            cn.ConnectionString = builder.ConnectionString;
            await cn.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
#endif
    }

    private static SqlCommand CreateUserCommand(string procedure, int timeout, SqlParameter[] parameters, SqlConnection cn)
    {
        var cmd = new SqlCommand(procedure, cn) {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = timeout
        };
        cmd.Parameters.AddRange(parameters);
        return cmd;
    }

    private static async Task<DataTable> QueryUserCoreAsync(string procedure, int timeout, CancellationToken token, SqlParameter[] parameters)
        => await Core.LookupDataCache.GetAsync(procedure, parameters,
            commandParameters => QueryUserTableCoreAsync(procedure, timeout,
                Core.LookupDataCache.CanCache(procedure, parameters) ? CancellationToken.None : token, commandParameters), token).ConfigureAwait(false);

    private static async Task<DataTable> QueryUserTableCoreAsync(string procedure, int timeout, CancellationToken token, SqlParameter[] parameters)
    {
        var result = await QueryUserSetCoreAsync(procedure, timeout, token, parameters, singleResult: true).ConfigureAwait(false);
        if (result.Tables.Count == 0) return new DataTable();
        var table = result.Tables[0];
        result.Tables.Remove(table);
        return table;
    }

    private static async Task<DataSet> QueryUserSetCoreAsync(string procedure, int timeout, CancellationToken token,
        SqlParameter[] parameters, bool singleResult = false, SqlParameter[]? admissionConsent = null)
    {
        token.ThrowIfCancellationRequested();
        using var cn = GetConnection();
        using var cmd = CreateUserCommand(procedure, timeout, parameters, cn);
        await OpenConnectionAsync(cn, token).ConfigureAwait(false);
        using var transaction = admissionConsent is null ? null : cn.BeginTransaction();
        if (admissionConsent is not null)
        {
            cmd.Transaction = transaction;
            using var consentCommand = CreateUserCommand("msg.RecordAdmissionConsent", timeout, admissionConsent, cn);
            consentCommand.Transaction = transaction;
            await consentCommand.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        var result = new DataSet();
        using (var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            do
            {
                if (reader.FieldCount == 0) continue;
                if (singleResult && result.Tables.Count > 0)
                {
                    // Drain later results asynchronously so output parameters are available on completion.
                    while (await reader.ReadAsync(token).ConfigureAwait(false)) { }
                    continue;
                }
                var table = await Core.AsyncTableLoader.ReadAsync(reader,
                    result.Tables.Count == 0 ? "Table" : $"Table{result.Tables.Count}", token).ConfigureAwait(false);
                result.Tables.Add(table);
            } while (await reader.NextResultAsync(token).ConfigureAwait(false));
        }
        transaction?.Commit();
        Core.LookupDataCache.OperationCompleted(procedure);
        // Close the reader before issuing the admission-number lookup on this connection.
        foreach (DataTable table in result.Tables) {
            await Core.AdmissionNumbers.EnrichAsync(table, cn, token).ConfigureAwait(false);
            Core.RecordTextPolicy.FormatDisplayRows(table);
        }
        token.ThrowIfCancellationRequested();
        return result;
    }

    internal static Task<DataSet> SaveStudentDetailsWithConsentAsync(int admissionId, bool consent,
        params SqlParameter[] parameters) =>
        QueryUserSetCoreAsync("SaveStudentDetailsWithFirstEnrollment", 120, CancellationToken.None,
            WithCurrentUser(parameters), admissionConsent: WithCurrentUser(Core.AdmissionConsentStore.Parameters(admissionId, consent)));

    private static async Task<int> ExecuteUserCoreAsync(string procedure, int timeout, CancellationToken token, SqlParameter[] parameters)
    {
        token.ThrowIfCancellationRequested();
        using var cn = GetConnection();
        using var cmd = CreateUserCommand(procedure, timeout, parameters, cn);
        await OpenConnectionAsync(cn, token).ConfigureAwait(false);
        int result = await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        Core.LookupDataCache.OperationCompleted(procedure);
        return result;
    }

    private static async Task<object?> ScalarUserCoreAsync(string procedure, int timeout, CancellationToken token, SqlParameter[] parameters)
    {
        token.ThrowIfCancellationRequested();
        using var cn = GetConnection();
        using var cmd = CreateUserCommand(procedure, timeout, parameters, cn);
        await OpenConnectionAsync(cn, token).ConfigureAwait(false);
        var result = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
        Core.LookupDataCache.OperationCompleted(procedure);
        return result;
    }
    public static object DbValue(object? value) =>
        value ?? DBNull.Value;
    /// <summary>Trims optional text; blank becomes null so <see cref="P"/> sends database null.</summary>
    public static string? OptionalText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    public static SqlParameter P(
        string name,
        object? value) =>
        new(name, DbValue(Core.RecordTextPolicy.NormalizeParameter(name, Core.WholeRupees.ValidateParameter(name,value))));
    public static SqlParameter Int(
        string name,
        int? value) =>
        new(name, SqlDbType.Int) {
            Value = value ?? 0
        };
    public static SqlParameter NullableInt(
        string name,
        int? value) =>
        new(name, SqlDbType.Int) {
            Value = value.HasValue
                ? value.Value
                : DBNull.Value
        };
    public static SqlParameter Bit(
        string name,
        bool value) =>
        new(name, SqlDbType.Bit) {
            Value = value
        };
    public static SqlParameter Date(
        string name,
        DateTime? value) =>
        new(name, SqlDbType.Date) {
            Value = value.HasValue ? value.Value.Date : DBNull.Value
        };
    public static SqlParameter BigInt(
        string name,
        long? value) =>
        new(name, SqlDbType.BigInt) {
            Value = value ?? 0L
        };
}
