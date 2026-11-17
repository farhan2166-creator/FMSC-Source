using System.Data;
using System.Text.Json;

namespace FMSC_SMS.Core;

internal sealed record CommunicationSessionRow(Guid BatchId, string Method, string Outcome, string Audience);

internal sealed record CommunicationHistoryDraft(CommunicationTarget Target, string Provider, string Body,
    string StudentName, string StaffName, CommunicationMetaTransport.Selection? MetaTemplate);

internal static class CommunicationHistoryStore
{
    internal static async Task<DataTable> ReadAsync(Guid batchId)
    {
        if (!CurrentUser.HasPermission("Communication.View"))
            throw new UnauthorizedAccessException("Communication history permission is required.");
        // History only returns masked contact details. API review text is
        // historical; manual batches retain their original message format.
        const string sql = """
            SET NOCOUNT ON;
            EXEC dbo.RequirePermission @UserId,N'Communication.View';
            DECLARE @Audience varchar(20)=(SELECT Audience FROM msg.Batch WHERE BatchId=@BatchId);
            IF @Audience IS NULL THROW 51132,'The saved message is no longer available.',1;
            IF @Audience='Staff' EXEC dbo.RequirePermission @UserId,N'Staff.View';
            ELSE EXEC dbo.RequirePermission @UserId,N'Students.View';
            IF @Audience IN ('OutstandingFees','FeeStudent','FeeFamily') EXEC dbo.RequirePermission @UserId,N'Fees.View';
            SELECT B.Channel,B.Body,B.Audience,B.CampusId,B.ClassId,
                CASE WHEN B.Audience='OutstandingFees' AND B.SingleStudentScope=0 THEN 0 ELSE B.StudentId END StudentId,
                B.StaffId,SelectedStudent.StudentName AS SelectedStudentName,SelectedStaff.StaffName AS SelectedStaffName,
                CASE WHEN M.BatchId IS NOT NULL THEN 'WhatsAppBusinessManual' ELSE A.ProviderCode END ProviderCode,
                A.TemplateJson,COALESCE(H.State,R.State) Outcome,
                B.CreatedUtc PreparedUtc,B.ConfirmedUtc ApprovedUtc,
                COALESCE(Creator.Username,CONVERT(nvarchar(30),B.CreatedBy)) PreparedByName,
                COALESCE(Approver.Username,CONVERT(nvarchar(30),B.ConfirmedBy)) ApprovedByName,
                Takeover.ApprovedUtc TakeoverUtc,Takeover.Reason TakeoverReason,
                Correction.OriginalBatchId CorrectionOfBatchId,
                Correction.Reason CorrectionReason,
                Correction.LinkedUtc CorrectionLinkedUtc,
                Automatic.EventCode AutomaticEventCode,Automatic.ApprovedUtc AutomaticAuthorizedUtc,
                COALESCE(AutomaticActor.Username,CONVERT(nvarchar(30),Automatic.ApprovedBy)) AutomaticAuthorizedBy,
                Planned.SourceKind PlanSourceKind,Planned.DueUtc OccurrenceDueUtc,Recurring.RuleId RecurringRuleId,
                Recurring.Frequency RecurringFrequency,Recurring.Every RecurringEvery,
                COALESCE(RuleAuthor.Username,CONVERT(nvarchar(30),Recurring.CreatedBy)) RecurringAuthorizedBy,
                COALESCE(TakeoverActor.Username,CONVERT(nvarchar(30),Takeover.ApprovedBy)) TakeoverApprovedBy,
                H.LastOpenedUtc,H.ReportedUtc,
                COALESCE(Reporter.Username,CONVERT(nvarchar(30),H.ChangedBy)) ManualReportedByName,
                Evidence.AttemptId,Evidence.RequestUtc,Evidence.ResponseUtc,
                Evidence.ProviderMessageId,Evidence.FailureCode,Evidence.ClaimKind,Evidence.WorkerEnrollmentId,
                COALESCE(ClaimActor.Username,CONVERT(nvarchar(30),Evidence.ClaimedBy)) AttemptActorName,
                COALESCE(S.StudentName,T.StaffName,N'School record') RecipientName,
                COALESCE(S.AdmissionNumber,T.StaffNumber) RecordNumber,RIGHT(R.PhoneE164,4) LastFour,
                COALESCE(J.RenderedBody,ManualEvidence.RenderedBody) RenderedBody
            FROM msg.Batch B
            LEFT JOIN msg.ManualBatch M ON M.BatchId=B.BatchId
            LEFT JOIN msg.ApiBatch A ON A.BatchId=B.BatchId
            LEFT JOIN msg.Recipient R ON R.BatchId=B.BatchId
            LEFT JOIN msg.ManualHandoff H ON H.RecipientId=R.RecipientId
            LEFT JOIN dbo.Users Creator ON Creator.UserId=B.CreatedBy
            LEFT JOIN dbo.Users Approver ON Approver.UserId=B.ConfirmedBy
            LEFT JOIN dbo.Users Reporter ON Reporter.UserId=H.ChangedBy
            OUTER APPLY (
                SELECT TOP(1) T.ApprovedUtc,T.ApprovedBy,T.Reason
                FROM msg.BatchTakeover T WHERE T.BatchId=B.BatchId
                ORDER BY T.TakeoverId DESC
            ) Takeover
            LEFT JOIN dbo.Users TakeoverActor ON TakeoverActor.UserId=Takeover.ApprovedBy
            LEFT JOIN msg.PlannedWork Planned ON Planned.BatchId=B.BatchId
            LEFT JOIN msg.AutomaticAuthorization Automatic ON Automatic.AuthorizationId=Planned.AuthorizationId
            LEFT JOIN dbo.Users AutomaticActor ON AutomaticActor.UserId=Automatic.ApprovedBy
            LEFT JOIN msg.RecurringRule Recurring ON Recurring.RuleId=Planned.RuleId
            LEFT JOIN dbo.Users RuleAuthor ON RuleAuthor.UserId=Recurring.CreatedBy
            LEFT JOIN msg.BatchCorrection Correction ON Correction.CorrectionBatchId=B.BatchId
            OUTER APPLY (
                SELECT TOP(1) X.AttemptId,X.RequestUtc,X.ResponseUtc,X.ProviderMessageId,X.FailureCode,X.ClaimedBy,X.ClaimKind,X.WorkerEnrollmentId
                FROM msg.Attempt X WHERE X.RecipientId=R.RecipientId
                ORDER BY X.AttemptId DESC
            ) Evidence
            LEFT JOIN dbo.Users ClaimActor ON ClaimActor.UserId=Evidence.ClaimedBy
            LEFT JOIN dbo.Students S ON S.AdmissionId=R.AdmissionId
            LEFT JOIN dbo.Staff T ON T.StaffId=R.StaffId
            LEFT JOIN dbo.Students SelectedStudent ON SelectedStudent.AdmissionId=B.StudentId
            LEFT JOIN dbo.Staff SelectedStaff ON SelectedStaff.StaffId=B.StaffId
            OUTER APPLY (
                SELECT TOP (1) X.RenderedBody FROM OPENJSON(A.ReviewJson)
                WITH (Kind varchar(8),RecordId int,Phone varchar(16),RenderedBody nvarchar(1600) '$.Text') X
                WHERE X.Kind=R.RecipientKind AND X.RecordId=COALESCE(R.AdmissionId,R.StaffId) AND X.Phone=R.PhoneE164
            ) J
            OUTER APPLY (
                SELECT TOP(1) E.RenderedBody FROM msg.ManualOpenEvidence E
                WHERE E.RecipientId=R.RecipientId ORDER BY E.OpenCount DESC
            ) ManualEvidence
            WHERE B.BatchId=@BatchId
            ORDER BY R.RecipientId;
            """;
        int actor = CurrentUser.UserId;
        DataTable table = await DatabaseHelper.QuerySqlAsync(sql,
            DatabaseHelper.Int("@UserId", actor), DatabaseHelper.P("@BatchId", batchId));
        if (actor == CurrentUser.UserId && table.Rows.Count > 0) return table;
        table.Dispose();
        throw new CommunicationValidationException("The saved message is no longer available.");
    }

    internal static CommunicationHistoryDraft? CreateDraft(DataTable history)
    {
        // Meta acceptance completes submission work; delivery remains unverified.
        // Send Again creates a separate draft for fresh eligibility checks and
        // confirmation. Pending and uncertain attempts can never use this path.
        if (history.Rows.Count == 0 || history.AsEnumerable().Any(row =>
                row.Field<string>("Outcome") switch
                {
                    "Pending" or "Opened" or "Submitted" or "Unknown" => true,
                    "Accepted" => row.Field<string>("ProviderCode") != "MetaCloud"
                        || row.Field<string>("Channel") != "WhatsApp",
                    _ => false
                }))
            return null;
        DataRow row = history.Rows[0];
        string provider = Convert.ToString(row["ProviderCode"]) ?? "";
        var scope = Enum.Parse<CommunicationAudience>((string)row["Audience"]);
        string permission = scope == CommunicationAudience.Staff ? "Staff.View" : "Students.View";
        if (!CurrentUser.HasPermission("Communication.Send") || !CurrentUser.HasPermission(permission)
            || scope is CommunicationAudience.FeeStudent or CommunicationAudience.FeeFamily
            || (scope == CommunicationAudience.OutstandingFees && !CurrentUser.HasPermission("Fees.View"))
            || provider is not ("WhatsAppBusinessManual" or "MetaCloud" or "Twilio")) return null;
        var target = new CommunicationTarget(scope, (int)row["CampusId"], (int)row["ClassId"],
            (int)row["StudentId"], (int)row["StaffId"], (string)row["Channel"]);
        string studentName = Convert.ToString(row["SelectedStudentName"]) ?? "";
        string staffName = Convert.ToString(row["SelectedStaffName"]) ?? "";
        if ((target.StudentId > 0 && studentName.Length == 0)
            || (target.StaffId > 0 && staffName.Length == 0)) return null;
        var metaTemplate = row["TemplateJson"] is string json
            ? JsonSerializer.Deserialize<CommunicationMetaTransport.Selection>(json) : null;
        return new(target, provider, (string)row["Body"], studentName, staffName, metaTemplate);
    }
}
