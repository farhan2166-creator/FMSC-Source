using System.Data;
using Microsoft.Data.SqlClient;

namespace FMSC_SMS.Core;

internal sealed record CommunicationConfirmation(Guid BatchId, long RecipientCount, bool AlreadyConfirmed);

/// <summary>
/// Bridges a reviewed preview to an idempotent SQL confirmation, WITHOUT
/// performing HTTP requests, scheduling or provider delivery.
/// The caller must preserve one request GUID across duplicate clicks/timeouts.
/// SQL independently recomputes the complete current eligible audience.
/// </summary>
internal static class CommunicationBatchStore
{
    internal static async Task<CommunicationConfirmation> ConfirmPreviewAsync(
        CommunicationTarget target, CommunicationPreview preview,
        string message, Guid clientRequestId, bool manualWhatsApp = false)
    {
        CommunicationRecipientResolver.Validate(target);
        if (manualWhatsApp && target.Channel != "WhatsApp")
            throw new ArgumentException("Manual handoff is only approved for WhatsApp.");
        if (!CurrentUser.HasPermission("Communication.Send"))
            throw new UnauthorizedAccessException("Communication.Send is required.");
        if (clientRequestId == Guid.Empty)
            throw new ArgumentException("Confirmation requires a stable request identifier.", nameof(clientRequestId));
        if (string.IsNullOrWhiteSpace(message) || message.Length > 1600)
            throw new ArgumentException("A message of at most 1600 characters is required.", nameof(message));
        if (preview.Included == 0)
            throw new InvalidOperationException("No eligible recipients remain.");
        string purpose = target.Purpose;
        CommunicationPreparedMessages.ValidateFormat(message, purpose);
        // TVP contains the exact eligible preview, never excluded or declined
        // records, and is checked again by SQL against authoritative Admissions.
        using var destinations = new DataTable();
        destinations.Columns.Add("RecipientKind", typeof(string));
        destinations.Columns.Add("RecordId", typeof(int));
        destinations.Columns.Add("PhoneE164", typeof(string));
        foreach (var item in preview.Entries.Where(item => item.Included))
        {
            if (item.PhoneE164 is null) throw new InvalidOperationException("Preview has no verified mobile.");
            destinations.Rows.Add(item.Kind, item.RecordId, item.PhoneE164);
        }
        var parameter = new SqlParameter("@Recipients", SqlDbType.Structured)
        {
            TypeName = "msg.RecipientSelection",
            Value = destinations
        };
        int actor = CurrentUser.UserId;
        DataTable result = await DatabaseHelper.QueryUserAsync(
            manualWhatsApp ? "msg.ConfirmManualBatch" : "msg.ConfirmBatch",
            DatabaseHelper.P("@ClientRequestId", clientRequestId),
            DatabaseHelper.P("@Channel", target.Channel),
            DatabaseHelper.P("@Purpose", purpose),
            DatabaseHelper.P("@Body", message),
            DatabaseHelper.P("@Audience", target.Audience.ToString()),
            DatabaseHelper.Int("@CampusId", target.CampusId),
            DatabaseHelper.Int("@ClassId", target.ClassId),
            DatabaseHelper.Int("@StudentId", target.StudentId),
            DatabaseHelper.Int("@StaffId", target.StaffId),
            parameter);
        if (actor != CurrentUser.UserId)
            throw new InvalidOperationException("Signed-in account changed while confirming the batch.");
        if (result.Rows.Count != 1)
            throw new InvalidOperationException("SQL did not return exactly one confirmed batch.");
        DataRow row = result.Rows[0];
        return new CommunicationConfirmation(
            (Guid)row["BatchId"],
            Convert.ToInt64(row["RecipientCount"]),
            Convert.ToBoolean(row["AlreadyConfirmed"]));
    }
}
