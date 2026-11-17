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
        int AdmissionId,string StudentName,string? AdmissionNumber,DateTime CreatedUtc,
        string? NoticeTitle=null,string? NoticeText=null,string? NoticeLanguageCode=null);

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
            Convert.ToDateTime(row["CreatedUtc"]),
            row.Table.Columns.Contains("NoticeTitle") ? Convert.ToString(row["NoticeTitle"]) : null,
            row.Table.Columns.Contains("NoticeText") ? Convert.ToString(row["NoticeText"]) : null,
            row.Table.Columns.Contains("NoticeLanguageCode") ? Convert.ToString(row["NoticeLanguageCode"]) : null))
            .OrderBy(suggestion=>suggestion.CreatedUtc).ThenBy(suggestion=>suggestion.CandidateId).ToArray();
    }

    internal static async Task<Suggestion?> OfferReviewAsync(long candidateId)
    {
        RequireReviewPermission();int actor=CurrentUser.UserId;
        await DatabaseHelper.ExecuteUserAsync("msg.OfferEventReview",DatabaseHelper.BigInt("@CandidateId",candidateId));
        using var rows=await DatabaseHelper.QueryUserAsync("msg.ReadEventForReview",DatabaseHelper.BigInt("@CandidateId",candidateId));
        if(actor!=CurrentUser.UserId)throw new UnauthorizedAccessException("The school operator changed.");
        if(rows.Rows.Count!=1)return null;
        DataRow row=rows.Rows[0];
        return new(Convert.ToInt64(row["CandidateId"]),Convert.ToString(row["EventCode"])!,Convert.ToInt64(row["SourceRecordId"]),
            Convert.ToInt32(row["AdmissionId"]),Convert.ToString(row["StudentName"])!,Convert.ToString(row["AdmissionNumber"]),Convert.ToDateTime(row["CreatedUtc"]),
            Convert.ToString(row["NoticeTitle"]),Convert.ToString(row["NoticeText"]),Convert.ToString(row["NoticeLanguageCode"]));
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

    // Curated operator-reviewed versions only. Fee-family notices never
    // imply one representative student is the entire household's balance.
    internal static string SuggestedBody(string eventCode,string languageCode="en",
        bool familyFinancial=false)
    {
        if(languageCode is not ("en" or "ur"))
            throw new ArgumentException("Choose an approved English or Urdu template.");
        if (familyFinancial && eventCode is ("OutstandingFees" or "UpcomingDue" or "OverdueFollowUp"))
        {
            return languageCode=="ur"
                ? "محترم والدین/سرپرست، آپ کے خاندان کی موجودہ بقایا فیس PKR [OutstandingAmount] ہے۔ تصدیق شدہ تفصیل کے لیے اسکول اکاؤنٹس دفتر سے رابطہ کریں۔"
                : "Dear parent/guardian, the current outstanding fee balance for your linked family is PKR [OutstandingAmount]. For the verified bill and payment details, please contact the school accounts office.";
        }
        if(languageCode=="ur")
            return eventCode switch
            {
                "Admission" => CommunicationPreparedMessages.BuiltIns
                    .Single(t=>t.Name=="New admission welcome (Urdu)").Body,
                "PaymentReceived" => CommunicationPreparedMessages.BuiltIns
                    .Single(t=>t.Name=="Payment received acknowledgement (Urdu)").Body,
                "Birthday" => CommunicationPreparedMessages.BuiltIns
                    .Single(t=>t.Name=="Student birthday greeting (Urdu)").Body,
                "OutstandingFees" => CommunicationPreparedMessages.BuiltIns
                    .Single(t=>t.Name=="Outstanding fee reminder (Urdu)").Body,
                "UpcomingDue" => "محترم والدین/سرپرست، [StudentName] کی فیس کی مقررہ تاریخ قریب ہے۔ درست تفصیل کے لیے اسکول کے اکاؤنٹس دفتر سے رابطہ کریں۔",
                "OverdueFollowUp" => "محترم والدین/سرپرست، [StudentName] کی واجب الادا فیس کی مقررہ تاریخ گزر چکی ہے۔ تازہ بقایا رقم کے لیے اسکول اکاؤنٹس دفتر سے رابطہ کریں۔",
                "Announcement" or "ExaminationResult" => "محترم والدین/سرپرست،\n[NoticeTitle]\n[NoticeText]\nRegards: FMSC",
                _ => throw new ArgumentOutOfRangeException(nameof(eventCode),
                    "This event does not have an approved Urdu format.")
            };
        return eventCode switch
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
            "Announcement" or "ExaminationResult" => "Dear parent/guardian,\n[NoticeTitle]\n[NoticeText]\nRegards: FMSC",
            _ => throw new ArgumentOutOfRangeException(nameof(eventCode),
                "This event does not have an approved school message suggestion.")
        };
    }

    internal static string EventLabel(string code) => code switch
    {
        "Admission" => "New admission",
        "PaymentReceived" => "Payment received",
        "Birthday" => "Birthday greeting",
        "OutstandingFees" => "Outstanding fees",
        "UpcomingDue" => "Upcoming fee due",
        "OverdueFollowUp" => "Overdue fee follow-up",
        "Announcement" => "School announcement",
        "ExaminationResult" => "Examination/result notice",
        _ => code
    };
}
