using System.Security.Cryptography;
using System.Text;
using FMSC_SMS.Core.Updates;

namespace FMSC_SMS.Core;

/// <summary>Windows account protected evidence shared by interactive and worker
/// dispatch. Enrollment changes never replace or clear these submission records.</summary>
internal static class CommunicationDispatchEvidence
{
    private static readonly byte[] Entropy=SHA256.HashData(Encoding.UTF8.GetBytes("FMSC/Communication/attempt-evidence/v1"));
    private static CommunicationAttemptJournal Open()
    {
        string root=Path.Combine(CommunicationProviderCredentialStore.DirectoryPath,"Attempts");
        var journal=new CommunicationAttemptJournal(root,bytes=>ProtectedData.Protect(bytes,Entropy,DataProtectionScope.CurrentUser),
            bytes=>ProtectedData.Unprotect(bytes,Entropy,DataProtectionScope.CurrentUser));
        Directory.CreateDirectory(root);
        UpdateDeviceKey.RestrictDirectory(root);
        return journal;
    }
    internal sealed record Context(CommunicationAttemptJournal.Identity Identity,CommunicationAttemptJournal.Attempt[] Attempts);

    internal static async Task<Context> ReadAsync(long recipientId,CancellationToken token)
    {
        if(!CurrentUser.HasPermission("Communication.Send"))throw new UnauthorizedAccessException("Communication.Send is required.");
        int actor=CurrentUser.UserId;
        using var data=await DatabaseHelper.QueryUserSetAsync("msg.ReadDispatchEvidence",token,DatabaseHelper.P("@RecipientId",recipientId));
        if(actor!=CurrentUser.UserId || data.Tables.Count!=2 || data.Tables[0].Rows.Count!=1 || data.Tables[1].Rows.Count>3)
            throw new CommunicationValidationException("The school or message identity changed. Nothing was sent; review it again.");
        var row=data.Tables[0].Rows[0];
        var identity=new CommunicationAttemptJournal.Identity(Convert.ToHexString(SHA256.HashData(((Guid)row["SchoolScope"]).ToByteArray())),
            (Guid)row["ClientRequestId"],Convert.ToString(row["RecipientKind"])!,Convert.ToInt32(row["RecordId"]),
            CommunicationAttemptJournal.Hash(Convert.ToString(row["PhoneE164"])!),Convert.ToString(row["Channel"])!,
            Convert.ToString(row["ProviderCode"])!,Convert.ToString(row["ProfileHash"])!);
        var attempts=data.Tables[1].AsEnumerable().Select(attempt=>new CommunicationAttemptJournal.Attempt(
            Convert.ToInt64(attempt["AttemptId"]),(Guid)attempt["AttemptKey"],Convert.ToString(attempt["Channel"])!,Convert.ToString(attempt["ProviderCode"])!)).ToArray();
        Open().AssertConsistent(identity,attempts);
        return new(identity,attempts);
    }

    internal static CommunicationAttemptJournal.Claim RecordClaim(Context context,long attemptId,Guid attemptKey)
    {
        var attempt=context.Attempts.SingleOrDefault(item=>item.AttemptId==attemptId && item.AttemptKey==attemptKey)
            ?? throw new CommunicationValidationException("The claimed submission differs from this database. Keep it held for recovery; do not resend.");
        return Open().RecordClaim(context.Identity,attempt);
    }
    internal static CommunicationAttemptJournal.Result? ReadResult(Context context,Guid attemptKey)=>
        Open().ReadResult(context.Identity,attemptKey);
    internal static void RecordResult(CommunicationAttemptJournal.Claim claim,CommunicationDeliveryOutcome outcome)=>
        Open().RecordResult(claim,outcome.State,outcome.ProviderMessageId);
}
