// Execute the production read queries against the runner's owned loopback
// fixture only. Sql.ValidateTarget verifies the database before any stage runs.
namespace FMSC_SMS;
public static partial class DatabaseHelper
{
    public static Task<System.Data.DataTable> QuerySqlAsync(string text,
        params Microsoft.Data.SqlClient.SqlParameter[] parameters) => Task.Run(() => QuerySql(text, parameters));
}
