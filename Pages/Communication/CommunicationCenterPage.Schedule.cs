using FMSC_SMS.Core;

namespace FMSC_SMS.Pages.Communication;

public sealed partial class CommunicationCenterPage
{
    /// <summary>Schedule an immutable reviewed API batch without sending HTTP.
    /// The original reviewed request key survives a failed persistence attempt.
    /// A future due time is enforced again by SQL at every recipient claim.</summary>
    private async Task ScheduleReviewedMessageAsync()
    {
        if (IsManual || step != 1)
            return;
        try
        {
            var target = previewTarget;
            var reviewed = preview;
            string? body = previewMessage;
            Guid request = manualRequestId;
            if (!manualModeAvailable || target is null || reviewed is null ||
                target != CurrentTarget() || body != message.Text ||
                request == Guid.Empty || reviewed.Included <= 0)
                throw new CommunicationValidationException(
                    "The recipients or message changed. Review the message again before scheduling.");
            int openingActor = CurrentUser.UserId;
            var schoolZone = await CommunicationQueuePolicyStore.ReadSchoolTimeZoneAsync();
            if (IsDisposed || openingActor != CurrentUser.UserId) return;
            using var dialog = new CommunicationScheduleDialog(schoolZone);
            if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
                return;
            if (!Confirm($"Schedule {reviewed.Included:N0} reviewed API message(s) for " +
                dialog.SchoolTimeLabel +
                "? Provider charges may apply when the approved messages are sent.",
                "Confirm Schedule", false))
                return;
            int actor = CurrentUser.UserId;
            var saved = await CommunicationApiBatchStore.ConfirmAsync(
                target, reviewed, body, request, ApiProvider, metaSelection);
            if (IsDisposed || actor != CurrentUser.UserId)
                return;
            if (request != manualRequestId || target != CurrentTarget() || body != message.Text)
                throw new CommunicationValidationException(
                    "The reviewed batch was saved but your draft changed. " +
                    "Open it in Queue before scheduling or sending.");
            // No dispatch operation is performed here. SQL makes the due date
            // authoritative and rejects any premature API claim.
            await LinkPendingCorrectionAsync(saved.BatchId);
            await CommunicationQueuePolicyStore.ScheduleReviewedBatchAsync(
                saved.BatchId, dialog.Due, dialog.Expires, dialog.Priority);
            await MarkAutomationSuggestionPreparedAsync(target,saved.BatchId);
            if (!IsDisposed && actor == CurrentUser.UserId)
            {
                ClearReviewedDraft();
                await ShowSessionsAsync(unfinished: true);
                SetStatus("Scheduled batch saved in Queue.");
            }
        }
        catch (CommunicationValidationException ex)
        {
            if (!IsDisposed) Warn(ex.Message);
        }
        catch (Exception ex)
        {
            if (!IsDisposed) Fail(ex);
        }
    }
}
