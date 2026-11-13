using Microsoft.Data.SqlClient;

namespace FMSC_SMS.Services.Backup;

internal static class ReplacementTemplateGuard
{
    internal static void RequireNoSchoolRecords(SqlConnection connection)
    {
        // Admission and communication migrations seed factory rows even on a
        // data-free install. Only those exact defaults may pass; a missing admin
        // never licenses overwriting an initialized or damaged school's records.
        using var check = new SqlCommand("""
            SELECT CASE WHEN EXISTS (
                SELECT 1 FROM sys.dm_db_partition_stats S
                JOIN sys.tables T ON T.object_id=S.object_id
                JOIN sys.schemas N ON N.schema_id=T.schema_id
                WHERE S.index_id IN (0,1) AND S.row_count>0 AND NOT (
                    N.name=N'dbo' AND T.name IN (N'DatabaseVersion',N'Roles',N'Permissions',
                        N'RolePermissions',N'BillingAutomationState',N'FeeTypes',N'AdmissionNumberCounter')
                    OR N.name=N'msg' AND T.name=N'ChannelSettings'))
                OR EXISTS (SELECT 1 FROM dbo.AdmissionNumberCounter
                           WHERE CounterId<>1 OR LastNumber<>0)
                OR EXISTS (SELECT 1 FROM msg.ChannelSettings
                           WHERE Channel NOT IN ('Sms','WhatsApp','Email') OR Enabled<>0
                              OR ProviderCode IS NOT NULL OR UpdatedBy IS NOT NULL)
                THEN 1 ELSE 0 END;
            """, connection);
        if (Convert.ToInt32(check.ExecuteScalar()) != 0)
            throw new UnauthorizedAccessException(
                "School records or setup data already exist. The database is not a clean first-run template; recovery was refused.");
    }
}
