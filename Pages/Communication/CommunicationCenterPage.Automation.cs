using FMSC_SMS.Core;

namespace FMSC_SMS.Pages.Communication;

public sealed partial class CommunicationCenterPage
{
    private CommunicationAutomationInboxStore.Suggestion? pendingAutomationSuggestion;

    private void OpenAutomationInbox()
    {
        if(IsBusy || !CurrentUser.HasPermission("Communication.Configure")
            || !CurrentUser.HasPermission("Fees.View")) return;
        using var dialog=new CommunicationAutomationInboxDialog();
        if(dialog.ShowDialog(FindForm())!=DialogResult.OK ||
            dialog.SelectedSuggestion is not { } eventSuggestion)return;

        UseEventSuggestion(eventSuggestion);
    }

    private void UseEventSuggestion(CommunicationAutomationInboxStore.Suggestion eventSuggestion)
    {
        // This is an ordinary editable draft, NOT approved communication.
        // Selecting a suggestion never confirms a batch or starts sending.
        pendingAutomationSuggestion=null;
        SetStep(0);
        bool financial=eventSuggestion.EventCode is
            "OutstandingFees" or "UpcomingDue" or "OverdueFollowUp";
        SelectedAudience=financial ? CommunicationAudience.FeeFamily : CommunicationAudience.Student;
        student.SetStudent(eventSuggestion.AdmissionId,eventSuggestion.StudentName);
        if(!IsMeta)
        {
            message.Text=CommunicationAutomationInboxStore.SuggestedBody(
                eventSuggestion.EventCode,CurrentLanguage,financial);
        }
        else
        {
            // Meta Cloud requires an approved provider-side template. Never
            // silently substitute a chargeable API or unpublished template.
            message.Clear();
            Warn("Choose an approved WhatsApp API template for this suggested event.");
        }
        pendingAutomationSuggestion=eventSuggestion;
        SetStep(0);
    }

    /// <summary>
    /// Only link a suggestion AFTER SQL confirms a real reviewed batch and
    /// BEFORE opening a WhatsApp chat or allowing the API dispatch dialog to
    /// send. If the suggestion link cannot be persisted, leave the batch in
    /// Queue and refuse an external handoff to avoid accidental duplicates.
    /// </summary>
    private async Task MarkAutomationSuggestionPreparedAsync(
        CommunicationTarget target,Guid confirmedBatchId)
    {
        if(pendingAutomationSuggestion is not { } suggestion)return;
        bool financial=suggestion.EventCode is "OutstandingFees" or "UpcomingDue" or "OverdueFollowUp";
        bool validAudience=financial
            ? target.Audience is CommunicationAudience.FeeFamily or CommunicationAudience.FeeStudent
            : target.Audience==CommunicationAudience.Student;
        if(!validAudience || target.StudentId!=suggestion.AdmissionId)
        {
            pendingAutomationSuggestion=null;
            // The operator composed a different audience. The original
            // event remains available for review; do not mark it handled.
            return;
        }
        await CommunicationAutomationInboxStore.MarkPreparedAsync(
            suggestion.CandidateId,confirmedBatchId);
        pendingAutomationSuggestion=null;
    }
}
