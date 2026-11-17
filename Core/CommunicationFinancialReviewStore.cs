namespace FMSC_SMS.Core;

internal sealed record CommunicationFinancialReview(
    string SourceKind,string ReceiptNumber,Guid? ReceiptId,int AdmissionId,bool WholeFamily,string SourceHash);

/// <summary>Pin the reviewed bill or receipt without editing finances or recording a send.</summary>
internal static class CommunicationFinancialReviewStore
{
    private static void RequireAccess()
    {
        foreach(string permission in new[]{"Communication.Send","Students.View","Fees.View"})
            if(!CurrentUser.HasPermission(permission))
                throw new UnauthorizedAccessException("School-message, student-view and fee-view permissions are required.");
    }

    internal static async Task<CommunicationFinancialReview> ReadAsync(int admissionId,bool family,string? receiptNumber=null)
    {
        RequireAccess();
        int actor=CurrentUser.UserId;
        string kind=receiptNumber is null ? "Bill" : "Receipt";
        using var rows=await DatabaseHelper.QueryUserAsync("msg.ReadFinancialReviewSource",
            DatabaseHelper.P("@SourceKind",kind),DatabaseHelper.P("@ReceiptNumber",receiptNumber),
            DatabaseHelper.Int("@AdmissionId",admissionId),DatabaseHelper.Bit("@WholeFamily",family));
        if(actor!=CurrentUser.UserId || rows.Rows.Count!=1 || rows.Rows[0]["SourceHash"] is not byte[] {Length:32} hash)
            throw new CommunicationValidationException("The bill, receipt or family changed. Review the document again before sending.");
        return new(kind,Convert.ToString(rows.Rows[0]["ReceiptNumber"])!,
            rows.Rows[0]["CollectionReceiptId"] is Guid id ? id : null,admissionId,family,Convert.ToHexString(hash));
    }

    internal static async Task ReserveAsync(Guid request,CommunicationFinancialReview source)
    {
        RequireAccess();
        if(request==Guid.Empty || source.AdmissionId<=0 || source.SourceHash.Length!=64
            || source.SourceKind is not ("Bill" or "Receipt"))
            throw new CommunicationValidationException("A reviewed financial document and request reference are required.");
        int actor=CurrentUser.UserId;
        await DatabaseHelper.ExecuteUserAsync("msg.ReserveFinancialReview",
            DatabaseHelper.P("@ClientRequestId",request),DatabaseHelper.P("@SourceKind",source.SourceKind),
            DatabaseHelper.P("@ReceiptNumber",source.SourceKind=="Receipt" ? source.ReceiptNumber : null),
            DatabaseHelper.Int("@AdmissionId",source.AdmissionId),DatabaseHelper.Bit("@WholeFamily",source.WholeFamily),
            DatabaseHelper.P("@SourceHash",Convert.FromHexString(source.SourceHash)));
        if(actor!=CurrentUser.UserId)
            throw new UnauthorizedAccessException("The signed-in account changed. Review the message again.");
    }
}
