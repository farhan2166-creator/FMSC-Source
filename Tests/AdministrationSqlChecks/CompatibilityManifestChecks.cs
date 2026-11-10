using Microsoft.Data.SqlClient;
using FMSC_SMS.Core;

namespace FMSC_SMS
{
    // The production schema version comes from Installer/core.ps1 via the
    // disposable SQL runner; never freeze this test to a specific version.
    public static partial class DatabaseHelper
    {
        internal static string RequiredDatabaseSchemaVersion =>
            Environment.GetEnvironmentVariable("FMSC_AUDIT_SCHEMA_VERSION")
            ?? throw new InvalidOperationException("Disposable SQL schema authority was not provided.");
    }
}

internal static class CompatibilityManifestChecks
{
    internal static void Run(string connectionString, Action<bool, string> check)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        DatabaseCompatibility.Validate(connection);
        check(true, "Production startup validator accepts a complete disposable migrated school schema.");

        // Delete objects/fields only within an owned SQL transaction and roll
        // it back regardless of outcome. No real school database is connected.
        VerifyRefusal("DROP PROCEDURE dbo.GetSchoolCertificateSignature;",
            "GetSchoolCertificateSignature",
            "Missing schema-11 signature procedure fails closed.");
        VerifyRefusal("ALTER TABLE dbo.AdministratorRecoveryEvents DROP COLUMN WindowsComputer;",
            "AdministratorRecoveryEvents.WindowsComputer",
            "Missing schema-12 recovery audit column fails closed.");
        DatabaseCompatibility.Validate(connection);
        check(true, "After negative tests roll back, original schema remains compatible.");

        void VerifyRefusal(string ddl, string expectedObject, string label)
        {
            using var transaction = connection.BeginTransaction();
            try
            {
                using (var cmd = new SqlCommand(ddl, connection, transaction))
                    cmd.ExecuteNonQuery();
                bool rejected = false;
                try { DatabaseCompatibility.Validate(connection, transaction); }
                catch (DatabaseCompatibilityException ex)
                    when (ex.Failure.Issue == DatabaseCompatibilityIssue.SchemaStructure
                          && ex.Failure.Details.Contains(expectedObject, StringComparison.Ordinal))
                {
                    rejected = true;
                }
                check(rejected, label);
            }
            finally { transaction.Rollback(); }
        }
    }
}
