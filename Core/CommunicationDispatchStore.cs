using System.Data;
using System.Net;
using Microsoft.Data.SqlClient;

namespace FMSC_SMS.Core;

/// <summary>
/// Explicit, one-at-a-time manual dispatch boundary. No background worker,
/// no automatic retry, no UI activation, and no sender startup side effects.
/// Each outbound attempt is persisted by SQL BEFORE HTTP is invoked.
/// </summary>
internal static class CommunicationDispatchStore
{
    internal sealed record DispatchResult(bool Claimed, long? AttemptId, string State, string? ExclusionCode);

    internal static async Task<DispatchResult> SendOneReviewedAsync(
        long recipientId,string channel,CancellationToken token=default)
    {
        if (recipientId<=0) throw new ArgumentOutOfRangeException(nameof(recipientId));
        if (channel is not ("Sms" or "WhatsApp"))
            throw new ArgumentException("Unknown messaging channel.",nameof(channel));
        if (!CurrentUser.HasPermission("Communication.Send"))
            throw new UnauthorizedAccessException("Manual school message sending is not authorized.");
        int actor=CurrentUser.UserId;
        var credentials=CommunicationProviderCredentialStore.LoadForDispatch(
            channel,CommunicationTwilioTransport.ProviderCode);
        CommunicationTwilioTransport.ValidateCredentials(credentials);
        Guid attemptKey=Guid.NewGuid();
        // A claim moves Pending to Unknown and records the attempt under lock.
        DataTable claim=await DatabaseHelper.QueryUserAsync("msg.ClaimRecipient",
            DatabaseHelper.P("@RecipientId",recipientId),
            DatabaseHelper.P("@AttemptKey",attemptKey),
            DatabaseHelper.P("@ExpectedProviderCode",CommunicationTwilioTransport.ProviderCode));
        if (actor!=CurrentUser.UserId)
            throw new InvalidOperationException("Signed-in account changed after claim. The attempt is Unknown and cannot be automatically resent.");
        if (claim.Rows.Count!=1)
            throw new InvalidOperationException("SQL did not return one recipient claim.");
        DataRow row=claim.Rows[0];
        if (!Convert.ToBoolean(row["Claimed"]))
            return new DispatchResult(false,null,"Excluded",Convert.ToString(row["ExclusionCode"]));
        long attemptId=Convert.ToInt64(row["AttemptId"]);
        if ((Guid)row["AttemptKey"]!=attemptKey
            || Convert.ToString(row["ProviderCode"])!=CommunicationTwilioTransport.ProviderCode
            || Convert.ToString(row["Channel"])!=channel)
            throw new InvalidOperationException("Provider claim identity changed; the attempt remains Unknown.");

        // Do not retry on cancellation, timeouts, 5xx or lost SQL responses.
        // The corresponding recipient is already Unknown until resolved.
        CommunicationTwilioTransport.Outcome outcome;
        using (var handler=new HttpClientHandler { AllowAutoRedirect=false,UseCookies=false })
        using (var client=new HttpClient(handler) { Timeout=TimeSpan.FromSeconds(25) })
            outcome=await CommunicationTwilioTransport.SendOnceAsync(
                credentials,Convert.ToString(row["PhoneE164"])!,
                Convert.ToString(row["Body"])!,client,token).ConfigureAwait(false);

        // Persist response using a non-cancelled bounded DB operation.
        // An interrupted or failed write leaves the pre-existing Unknown
        // attempt and must never trigger a second HTTP POST.
        await DatabaseHelper.QueryUserAsync("msg.RecordProviderResult",
            DatabaseHelper.P("@AttemptKey",attemptKey),
            DatabaseHelper.P("@Outcome",outcome.State),
            DatabaseHelper.P("@ProviderMessageId",outcome.MessageSid),
            DatabaseHelper.P("@FailureCode",outcome.ErrorCode)).ConfigureAwait(false);
        return new DispatchResult(true,attemptId,outcome.State,null);
    }

    /// <summary>
    /// Explicit status reconciliation uses authenticated provider GET, never
    /// a second POST. SID-less Unknown attempts require operator/provider review.
    /// </summary>
    internal static async Task<string> ReconcileKnownAttemptAsync(long attemptId,
        CancellationToken token=default)
    {
        if (attemptId<=0) throw new ArgumentOutOfRangeException(nameof(attemptId));
        if (!CurrentUser.HasPermission("Communication.Send"))
            throw new UnauthorizedAccessException("Reconciliation is not authorized.");
        int actor=CurrentUser.UserId;
        DataTable rows=await DatabaseHelper.QueryUserAsync(
            "msg.ReadAttemptForReconciliation",DatabaseHelper.P("@AttemptId",attemptId));
        if (rows.Rows.Count!=1)
            throw new InvalidOperationException("The attempt has no correlatable provider identifier.");
        string channel=Convert.ToString(rows.Rows[0]["Channel"])!;
        string provider=Convert.ToString(rows.Rows[0]["ProviderCode"])!;
        string sid=Convert.ToString(rows.Rows[0]["ProviderMessageId"])!;
        var credential=CommunicationProviderCredentialStore.LoadForDispatch(channel,provider);
        using var handler=new HttpClientHandler { AllowAutoRedirect=false,UseCookies=false };
        using var client=new HttpClient(handler) { Timeout=TimeSpan.FromSeconds(25) };
        var result=await CommunicationTwilioTransport.ReadStatusAsync(
            credential,sid,client,token).ConfigureAwait(false);
        if (result.State=="Unknown") return "Unknown";
        if (actor!=CurrentUser.UserId)
            throw new InvalidOperationException("Account changed during reconciliation.");
        string eventId="poll-"+sid+"-"+result.State;
        await DatabaseHelper.QueryUserAsync("msg.ApplyVerifiedReceipt",
            DatabaseHelper.P("@ProviderCode",provider),
            DatabaseHelper.P("@EventId",eventId),
            DatabaseHelper.P("@ProviderMessageId",sid),
            DatabaseHelper.P("@Outcome",result.State)).ConfigureAwait(false);
        return result.State;
    }

    /// <summary>
    /// For a future trusted HTTPS ingress, not a listening endpoint. Always
    /// authenticate the original signed URL and every form field before SQL.
    /// The Windows desktop does not expose a public webhook or polling worker.
    /// </summary>
    internal static async Task<bool> ApplySignedCallbackAsync(
        string channel,Uri trustedOriginalPublicUrl,
        IReadOnlyDictionary<string,string> form,string signature)
    {
        if (!CurrentUser.HasPermission("Communication.Send"))
            throw new UnauthorizedAccessException("Delivery receipt reconciliation is not authorized.");
        int actor=CurrentUser.UserId;
        var credential=CommunicationProviderCredentialStore.LoadForDispatch(
            channel,CommunicationTwilioTransport.ProviderCode);
        var verified=CommunicationTwilioTransport.VerifyCallback(
            credential,trustedOriginalPublicUrl,form,signature);
        if (actor!=CurrentUser.UserId)
            throw new InvalidOperationException("Account changed during callback verification.");
        DataTable result=await DatabaseHelper.QueryUserAsync("msg.ApplyVerifiedReceipt",
            DatabaseHelper.P("@ProviderCode",CommunicationTwilioTransport.ProviderCode),
            DatabaseHelper.P("@EventId",verified.EventId),
            DatabaseHelper.P("@ProviderMessageId",verified.MessageSid),
            DatabaseHelper.P("@Outcome",verified.Status)).ConfigureAwait(false);
        return result.Rows.Count==1 && Convert.ToBoolean(result.Rows[0]["AlreadyRecorded"]);
    }

    /// <summary>Retries never happen in a worker. The operator must review a
    /// definitive failed result, then approve it with a recorded reason.</summary>
    internal static Task ApproveDefinitiveRetryAsync(long recipientId,string reason)
    {
        if (!CurrentUser.HasPermission("Communication.Send"))
            throw new UnauthorizedAccessException("Retry permission is required.");
        if (recipientId<=0 || string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Recipient and manual retry justification are required.");
        return DatabaseHelper.ExecuteUserAsync("msg.ApproveDefinitiveRetry",
            DatabaseHelper.P("@RecipientId",recipientId),
            DatabaseHelper.P("@Reason",reason.Trim()));
    }
}
