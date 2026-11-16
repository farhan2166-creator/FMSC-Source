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
                    OR N.name=N'msg' AND T.name IN
                       (N'ChannelSettings',N'DispatchControl',N'AutomationRule',
                        N'BackgroundWorkerLease',N'NonWorkingWeekday',
                        N'AutomationScanControl')))
                OR EXISTS (SELECT 1 FROM dbo.AdmissionNumberCounter
                           WHERE CounterId<>1 OR LastNumber<>0)
                OR EXISTS (SELECT 1 FROM msg.ChannelSettings
                           WHERE Channel NOT IN ('Sms','WhatsApp','Email') OR Enabled<>0
                              OR ProviderCode IS NOT NULL OR UpdatedBy IS NOT NULL)
                -- Only precisely seeded, inert communication controls are
                -- valid on a never-used school. Any configured sending or
                -- changed automation is evidence of an initialized school.
                OR (SELECT COUNT(*) FROM msg.DispatchControl)<>1
                OR EXISTS (SELECT 1 FROM msg.DispatchControl WHERE ControlId<>1
                    OR MasterPaused<>0 OR UnattendedEnabled<>0
                    OR WindowStart<>CONVERT(time(0),'08:00')
                    OR WindowEnd<>CONVERT(time(0),'18:00')
                    OR TimeZoneId<>N'Pakistan Standard Time'
                    OR MaxPerMinute<>60 OR PreferredChannel<>'WhatsApp'
                    OR PreferredProviderCode<>'WhatsAppBusinessManual'
                    OR UpdatedBy IS NOT NULL)
                OR (SELECT COUNT(*) FROM msg.AutomationRule)<>8
                OR EXISTS (SELECT 1 FROM msg.AutomationRule
                    WHERE EventCode NOT IN ('Admission','PaymentReceived','OutstandingFees',
                      'UpcomingDue','OverdueFollowUp','Announcement','ExaminationResult','Birthday')
                      OR Mode<>'Off' OR CampusId<>0 OR ClassId<>0
                      OR ActivationBaselineUtc IS NOT NULL OR UpdatedBy IS NOT NULL
                      OR BaselineAdmissionId<>0 OR BaselinePaymentId<>0)
                OR (SELECT COUNT(*) FROM msg.BackgroundWorkerLease)<>1
                OR EXISTS(SELECT 1 FROM msg.BackgroundWorkerLease
                    WHERE LeaseId<>1 OR EnrollmentId IS NOT NULL OR LeaseNonce IS NOT NULL
                    OR LeaseUntilUtc<>CONVERT(datetime2(3),'20000101'))
                OR (SELECT COUNT(*) FROM msg.NonWorkingWeekday)<>7
                OR EXISTS(SELECT 1 FROM msg.NonWorkingWeekday
                    WHERE IsoWeekday NOT BETWEEN 1 AND 7
                       OR (IsoWeekday=7 AND Enabled<>0)
                       OR (IsoWeekday BETWEEN 1 AND 6 AND Enabled<>1))
                -- The schema-26 review scanner has one inert seed row.
                -- A claimed scan or missing control row must never look virgin.
                OR (SELECT COUNT(*) FROM msg.AutomationScanControl)<>1
                OR EXISTS (SELECT 1 FROM msg.AutomationScanControl
                    WHERE ControlId<>1 OR LastScanClaimUtc<>CONVERT(datetime2(3),'20000101'))
                THEN 1 ELSE 0 END;
            """, connection);
        if (Convert.ToInt32(check.ExecuteScalar()) != 0)
            throw new UnauthorizedAccessException(
                "School records or setup data already exist. The database is not a clean first-run template; recovery was refused.");
    }
}
