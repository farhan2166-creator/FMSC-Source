using FMSC_SMS.Services.Backup;
using Microsoft.Data.SqlClient;

internal static class TemplateChecks
{
    internal static void Run(string master)
    {
        string catalog = Environment.GetEnvironmentVariable("FMSC_SAFETY_TEMPLATE") ?? throw new Exception("Owned fresh template required.");
        string owner = Environment.GetEnvironmentVariable("FMSC_SAFETY_OWNER")!;
        if (catalog != "FMSC_BackupAudit_" + owner) throw new Exception("Unknown template ownership.");
        using var connection = new SqlConnection(new SqlConnectionStringBuilder(master) { InitialCatalog = catalog }.ConnectionString);
        connection.Open();
        using (var first = new SqlCommand("EXEC dbo.GetFirstRunStatus;", connection))
        using (var reader = first.ExecuteReader())
            if (!reader.Read() || !reader.GetBoolean(reader.GetOrdinal("IsFirstRun")))
                throw new Exception("Fixture is not an actual data-free migrated first-run template.");
        ReplacementTemplateGuard.RequireNoSchoolRecords(connection);
        int checks = 1;
        void Refused(string mutation)
        {
            using var change = new SqlCommand("BEGIN TRANSACTION; " + mutation, connection);
            change.ExecuteNonQuery();
            try
            {
                try { ReplacementTemplateGuard.RequireNoSchoolRecords(connection); }
                catch (UnauthorizedAccessException) { checks++; return; }
                throw new Exception("Existing or modified school data was accepted: " + mutation);
            }
            finally { using var rollback = new SqlCommand("ROLLBACK TRANSACTION;", connection); rollback.ExecuteNonQuery(); }
        }
        Refused("UPDATE dbo.AdmissionNumberCounter SET LastNumber=1;");
        Refused("UPDATE msg.ChannelSettings SET ProviderCode='Changed' WHERE Channel='Sms';");
        Refused("UPDATE msg.ChannelSettings SET Enabled=1,ProviderCode='Changed' WHERE Channel='Sms';");
        Refused("CREATE TABLE dbo.FMSC_UnknownRecords(Id int); INSERT dbo.FMSC_UnknownRecords VALUES(1);");
        Refused("CREATE TABLE msg.Roles(Id int); INSERT msg.Roles VALUES(1);");
        ReplacementTemplateGuard.RequireNoSchoolRecords(connection);
        Console.WriteLine($"PASS: {checks} real migrated first-run factory defaults and initialized-record refusal checks.");
    }
}
