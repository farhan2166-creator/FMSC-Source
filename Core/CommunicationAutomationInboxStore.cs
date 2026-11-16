using System.Data;

namespace FMSC_SMS.Core;

/// <summary>
/// A review-only source-event ledger. It never creates a communication batch,
/// dispatch attempt or provider call; the existing composer must re-resolve
/// authoritative guardian eligibility and explicit operator confirmation.
/// </summary>
internal static class CommunicationAutomationInboxStore
{
    internal sealed record Suggestion(
        long CandidateId,string EventCode,long SourceRecordId,
        int AdmissionId,string StudentName,string? AdmissionNumber,DateTime CreatedUtc);

    private static void RequireReviewPermission()
    {
        if (!CurrentUser.HasPermission("Communication.Configure") ||
            !CurrentUser.HasPermission("Communication.Send") ||
            !CurrentUser.HasPermission("Students.View") ||
            !CurrentUser.HasPermission("Fees.View"))
            throw new UnauthorizedAccessException(
                "School communication administrator, student and fee access are required.");
    }

    internal static async Task<IReadOnlyList<Suggestion>> RefreshAsync()
    {
        RequireReviewPermission();
        int actor=CurrentUser.UserId;
        // No messages, approvals or provider requests are created by a scan.
        using(var generated=await DatabaseHelper.QueryUserAsync("msg.ScanAutomationCandidates"))
        {
            if(generated.Rows.Count!=1)
                throw new InvalidOperationException("School event suggestions could not be refreshed.");
        }
        using(var generated=await DatabaseHelper.QueryUserAsync("msg.ScanFeeReviewCandidates"))
        {
            if(generated.Rows.Count!=1)
                throw new InvalidOperationException("Fee review suggestions could not be refreshed.");
        }
        using var table=await DatabaseHelper.QueryUserAsync("msg.ReadAutomationCandidates");
        using var feeTable=await DatabaseHelper.QueryUserAsync("msg.ReadFeeReviewCandidates");
        if(actor!=CurrentUser.UserId || !CurrentUser.HasPermission("Communication.Configure"))
            throw new UnauthorizedAccessException("School communication administrator changed.");
        return table.AsEnumerable().Concat(feeTable.AsEnumerable()).Select(row=>new Suggestion(
            Convert.ToInt64(row["CandidateId"]),
            Convert.ToString(row["EventCode"])??"",
            Convert.ToInt64(row["SourceRecordId"]),
            Convert.ToInt32(row["AdmissionId"]),
            Convert.ToString(row["StudentName"])??"",
            row.IsNull("AdmissionNumber") ? null : Convert.ToString(row["AdmissionNumber"]),
            Convert.ToDateTime(row["CreatedUtc"])))
            .OrderBy(suggestion=>suggestion.CreatedUtc).ThenBy(suggestion=>suggestion.CandidateId).ToArray();
    }

    internal static Task DismissAsync(long candidateId)
    {
        RequireReviewPermission();
        if(candidateId<=0) throw new ArgumentOutOfRangeException(nameof(candidateId));
        return DatabaseHelper.ExecuteUserAsync("msg.DismissAutomationCandidate",
            DatabaseHelper.P("@CandidateId",candidateId));
    }

    internal static Task MarkPreparedAsync(long candidateId,Guid batchId)
    {
        RequireReviewPermission();
        if(candidateId<=0 || batchId==Guid.Empty)
            throw new ArgumentException("A reviewed suggestion and confirmed batch are required.");
        return DatabaseHelper.ExecuteUserAsync("msg.MarkAutomationCandidatePrepared",
            DatabaseHelper.P("@CandidateId",candidateId),
            DatabaseHelper.P("@BatchId",batchId));
    }

    internal static Task<DataTable> ReadFamilyExceptionsAsync()
    {
        if(!CurrentUser.HasPermission("Communication.Configure") ||
            !CurrentUser.HasPermission("Students.View"))
            throw new UnauthorizedAccessException("Communication administrator and student access are required.");
        return DatabaseHelper.QueryUserAsync("msg.ReadAutomationFamilyExceptions");
    }

    internal static async Task SaveFamilyExceptionAsync(
        int admissionId,string eventCode,DateTime? expiryUtc,string reason)
    {
        if(!CurrentUser.HasPermission("Communication.Configure") ||
           !CurrentUser.HasPermission("Students.View"))
            throw new UnauthorizedAccessException("Communication administrator and student access are required.");
        if(admissionId<=0 || string.IsNullOrWhiteSpace(eventCode) ||
           string.IsNullOrWhiteSpace(reason) || reason.Length>200 ||
           (expiryUtc is { } date && date<=DateTime.UtcNow))
            throw new ArgumentException("Choose a valid student, event and reason; expiry must be in the future.");
        using var result=await DatabaseHelper.QueryUserAsync("msg.SaveAutomationFamilyException",
            DatabaseHelper.Int("@AdmissionId",admissionId),
            DatabaseHelper.P("@EventCode",eventCode),
            DatabaseHelper.P("@UntilUtc",expiryUtc),
            DatabaseHelper.P("@Reason",reason.Trim()));
        if(result.Rows.Count!=1)
            throw new InvalidOperationException("The family exception could not be confirmed.");
    }

    internal static Task RevokeFamilyExceptionAsync(long exceptionId)
    {
        if(!CurrentUser.HasPermission("Communication.Configure"))
            throw new UnauthorizedAccessException("Communication.Configure is required.");
        if(exceptionId<=0)throw new ArgumentOutOfRangeException(nameof(exceptionId));
        return DatabaseHelper.ExecuteUserAsync("msg.RevokeAutomationFamilyException",
            DatabaseHelper.P("@ExceptionId",exceptionId));
    }

    internal static string SuggestedBody(string eventCode) => eventCode switch
    {
        "Admission" => CommunicationPreparedMessages.BuiltIns
            .Single(t=>t.Name=="New admission welcome").Body,
        "PaymentReceived" => CommunicationPreparedMessages.BuiltIns
            .Single(t=>t.Name=="Payment received acknowledgement").Body,
        "Birthday" => CommunicationPreparedMessages.BuiltIns
            .Single(t=>t.Name=="Student birthday greeting").Body,
        "OutstandingFees" => CommunicationPreparedMessages.BuiltIns
            .Single(t=>t.Name=="Outstanding fee reminder").Body,
        "UpcomingDue" => "Dear parent/guardian, a fee for [StudentName] (admission [AdmissionNumber]) is approaching its due date. Please review the current school fee statement or contact the accounts office.",
        "OverdueFollowUp" => "Dear parent/guardian, a fee for [StudentName] (admission [AdmissionNumber]) is now past its due date. Please contact the accounts office to review the current balance.",
        _ => throw new ArgumentOutOfRangeException(nameof(eventCode),
            "This event does not have an approved school message suggestion.")
    };

    internal static string EventLabel(string code) => code switch
    {
        "Admission" => "New admission",
        "PaymentReceived" => "Payment received",
        "Birthday" => "Birthday greeting",
        "OutstandingFees" => "Outstanding fees",
        "UpcomingDue" => "Upcoming fee due",
        "OverdueFollowUp" => "Overdue fee follow-up",
        _ => code
    };
}
