using System.Data;
using Microsoft.Data.SqlClient;

namespace FMSC_SMS.Services.Backup;

/// <summary>
/// The final SQL-only activation step for a separately authorized, verified
/// replacement-PC recovery. It has no live account/network dependencies and
/// is exercised on an owned disposable SQL instance in regression tests.
/// The caller MUST enforce identity, device and Administrator approval before
/// invoking, and MUST refuse REPLACE unless the installed database is a
/// verified never-configured first-run template with an external safety copy.
/// </summary>
internal static class ReplacementSqlActivation
{
    internal static void RestoreVerifiedBackup(SqlConnection master, string backupPath,
        string dataPath, string logPath, bool verifiedVirginTemplate)
    {
        if (master.State != ConnectionState.Open ||
            !string.Equals(master.Database, "master", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Replacement SQL activation requires an opened master connection.");
        if (!File.Exists(backupPath) || string.IsNullOrWhiteSpace(dataPath) ||
            string.IsNullOrWhiteSpace(logPath) ||
            string.Equals(Path.GetFullPath(dataPath), Path.GetFullPath(logPath),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A valid verified backup and distinct recovery data/log paths are required.");
        using (var check = new SqlCommand(
            "SELECT COUNT(*) FROM sys.databases WHERE name=N'FMSC_DB';", master))
        {
            bool existing = Convert.ToInt32(check.ExecuteScalar()) != 0;
            if (existing != verifiedVirginTemplate)
                throw new InvalidOperationException(
                    "The target FMSC_DB presence changed. SQL activation refused to replace any school.");
        }
        using var list = new SqlCommand("RESTORE FILELISTONLY FROM DISK=@Path WITH FILE=1;", master) {
            CommandTimeout = 0
        };
        list.Parameters.Add("@Path", SqlDbType.NVarChar, 1000).Value = backupPath;
        using var table = new DataTable();
        using (var adapter = new SqlDataAdapter(list)) adapter.Fill(table);
        var dataRows = table.Rows.Cast<DataRow>()
            .Where(r => Convert.ToString(r["Type"]) == "D").ToArray();
        var logRows = table.Rows.Cast<DataRow>()
            .Where(r => Convert.ToString(r["Type"]) == "L").ToArray();
        if (table.Rows.Count != 2 || dataRows.Length != 1 || logRows.Length != 1)
            throw new InvalidDataException(
                "The recovery SQL backup must contain exactly one FMSC data file and one log file.");
        if (!verifiedVirginTemplate && (File.Exists(dataPath) || File.Exists(logPath)))
            throw new IOException(
                "Existing data or log files were found. New-school restore must not overwrite them.");
        if (verifiedVirginTemplate)
        {
            using var exclusive = new SqlCommand(
                "ALTER DATABASE [FMSC_DB] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;", master) {
                CommandTimeout = 60
            };
            exclusive.ExecuteNonQuery();
        }
        string sql = "RESTORE DATABASE [FMSC_DB] FROM DISK=@Path " +
            "WITH FILE=1, RECOVERY, CHECKSUM, " +
            "MOVE @DataLogical TO @DataPath, MOVE @LogLogical TO @LogPath" +
            (verifiedVirginTemplate ? ", REPLACE;" : ";");
        using var restore = new SqlCommand(sql, master) { CommandTimeout = 0 };
        restore.Parameters.Add("@Path", SqlDbType.NVarChar, 1000).Value = backupPath;
        restore.Parameters.Add("@DataLogical", SqlDbType.NVarChar, 128)
            .Value = Convert.ToString(dataRows[0]["LogicalName"]);
        restore.Parameters.Add("@LogLogical", SqlDbType.NVarChar, 128)
            .Value = Convert.ToString(logRows[0]["LogicalName"]);
        restore.Parameters.Add("@DataPath", SqlDbType.NVarChar, 1000).Value = dataPath;
        restore.Parameters.Add("@LogPath", SqlDbType.NVarChar, 1000).Value = logPath;
        restore.ExecuteNonQuery();
    }
}
