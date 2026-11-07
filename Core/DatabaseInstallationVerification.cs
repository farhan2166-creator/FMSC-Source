using Microsoft.Data.SqlClient;
using System.Text.RegularExpressions;

namespace FMSC_SMS.Core;

// Installer-only core database verification; no optional communication dependency.
internal static class DatabaseInstallationVerification
{
    internal const int Ready = 0, BaseIncompatible = 20, Failed = 23;

    internal static int Run(string[] args)
    {
        if (args.Length != 3 || args[0] != "--verify-installation") return Failed;
        // Production installer connections are Windows-authenticated LocalDB only.
        // Do not route through DatabaseHelper.GetConnection/auto-attach.
        if (args[2] != "FMSC_DB" ||
            !(args[1].Equals(@"(localdb)\MSSQLLocalDB", StringComparison.OrdinalIgnoreCase) ||
              Regex.IsMatch(args[1], "^" + Regex.Escape(@"np:\\.\pipe\LOCALDB#") + @"[A-Za-z0-9]+" + Regex.Escape(@"\tsql\query") + "$", RegexOptions.IgnoreCase))) return Failed;
        try
        {
            var builder = new SqlConnectionStringBuilder { DataSource = args[1], InitialCatalog = args[2],
                IntegratedSecurity = true, TrustServerCertificate = true, ConnectTimeout = 15 };
            using var connection = new SqlConnection(builder.ConnectionString);
            connection.Open();
            try { DatabaseCompatibility.Validate(connection); return Ready; }
            catch (Exception ex) when (ex is SqlException or DatabaseCompatibilityException)
            { return BaseIncompatible; }
        }
        catch (Exception)
        { return Failed; } // Installer explains exit codes; no credentials/raw SQL are logged.
    }
}
