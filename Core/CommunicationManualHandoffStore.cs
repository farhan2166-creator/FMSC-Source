using System.Data;

namespace FMSC_SMS.Core;

/// <summary>
/// Manual, human-confirmed WhatsApp Business handoff. SQL owns the durable
/// per-recipient ledger, authorizations and live scope/consent rechecks.
/// Opening a link is never submission or proof of delivery.
/// </summary>
internal sealed record CommunicationManualRecipient(
    long RecipientId, string RecordName, string LastFour, string State);

internal sealed record CommunicationManualOpen(
    bool Allowed, Uri? Link, string? ExclusionCode, string? Message = null);

internal static class CommunicationManualHandoffStore
{
    internal static async Task<bool> IsEnabledAsync()
    {
        RequireSender();
        using DataTable rows=await DatabaseHelper.QueryUserAsync("msg.ReadManualWhatsAppMode");
        return rows.Rows.Count==1 && Convert.ToBoolean(rows.Rows[0]["Enabled"]);
    }

    internal static async Task<IReadOnlyList<CommunicationManualRecipient>> ReadAsync(Guid batchId)
    {
        RequireSender();
        using DataTable rows=await DatabaseHelper.QueryUserAsync("msg.ReadManualHandoff",
            DatabaseHelper.P("@BatchId",batchId));
        return rows.AsEnumerable().Select(row=>new CommunicationManualRecipient(
            Convert.ToInt64(row["RecipientId"]),
            Convert.ToString(row["RecipientName"])??"School record",
            Convert.ToString(row["PhoneLast4"])??"",
            Convert.ToString(row["State"])??"Pending")).ToArray();
    }

    /// <summary>Server marks Opened before any external shell launch.</summary>
    internal static async Task<CommunicationManualOpen> OpenAsync(long recipientId)
    {
        RequireSender();
        using DataTable rows=await DatabaseHelper.QueryUserAsync("msg.OpenManualHandoff",
            DatabaseHelper.P("@RecipientId",recipientId));
        if(rows.Rows.Count!=1) throw new InvalidOperationException("Manual handoff did not return one validated recipient.");
        DataRow row=rows.Rows[0];
        if(!Convert.ToBoolean(row["Opened"]))
            return new CommunicationManualOpen(false,null,
                Convert.ToString(row["ExclusionCode"])??"NoLongerEligible");
        string phone=Convert.ToString(row["PhoneE164"])
            ?? throw new InvalidOperationException("No validated WhatsApp destination.");
        string body=Convert.ToString(row["Body"])
            ?? throw new InvalidOperationException("No reviewed WhatsApp message.");
        CommunicationPreparedMessages.ValidateFormat(body);
        if (CommunicationPreparedMessages.HasFields(body))
        {
            bool feeAmount = body.Contains("[OutstandingAmount]", StringComparison.Ordinal);
            var fields = await CommunicationPreparedMessages.ReadLiveAsync(recipientId, feeAmount);
            body = CommunicationPreparedMessages.Render(body, fields);
        }
        return new CommunicationManualOpen(true,BuildClickToChatLink(phone,body),null,body);
    }

    internal static Task MarkAsync(long recipientId,string result)
    {
        RequireSender();
        if(result is not ("ReportedSent" or "Skipped"))
            throw new ArgumentException("Only explicit operator report or skip is allowed.",nameof(result));
        return DatabaseHelper.ExecuteUserAsync("msg.MarkManualHandoff",
            DatabaseHelper.P("@RecipientId",recipientId),
            DatabaseHelper.P("@Outcome",result));
    }

    /// <summary>
    /// Official WhatsApp click-to-chat URL: digits-only E.164 and a pre-filled
    /// message. The operator must still press Send in their WhatsApp account.
    /// </summary>
    internal static Uri BuildClickToChatLink(string e164,string body)
    {
        if(CommunicationRecipientResolver.NormalizeMobile(e164)!=e164
            || body.Length is < 1 or > 1600 || string.IsNullOrWhiteSpace(body)
            || body.Contains("{{",StringComparison.Ordinal)
            || body.Contains("}}",StringComparison.Ordinal))
            throw new ArgumentException("Invalid reviewed WhatsApp destination or message.");
        string number=e164[1..]; // wa.me uses international digits WITHOUT '+'
        return new Uri("https://wa.me/"+number+"?text="+Uri.EscapeDataString(body),
            UriKind.Absolute);
    }

    private static void RequireSender()
    {
        if(!CurrentUser.HasPermission("Communication.Send"))
            throw new UnauthorizedAccessException("Communication.Send is required.");
    }
}
