using FMSC_SMS.Core;

namespace FMSC_SMS.Pages.Students.Admissions;

public sealed partial class StudentAdmissionPage
{
    private readonly List<Button> siblingSyncButtons = new();
    private bool readingSiblingSync;

    private void ConfigureSiblingSync()
    {
        var tips = new ToolTip();
        Disposed += (_, _) => tips.Dispose();
        Add(address, AdmissionSiblingSyncData.Address);
        Add(contact, AdmissionSiblingSyncData.PrimaryPhone);
        Add(altContact, AdmissionSiblingSyncData.AlternatePhone);
        Add(fatherName, AdmissionSiblingSyncData.FatherName);
        Add(prevSchool, AdmissionSiblingSyncData.PreviousSchool);
        Add(fatherOccupation, AdmissionSiblingSyncData.FatherOccupation);
        Add(casteTribe, AdmissionSiblingSyncData.CasteTribe);
        void Add(ModernTextBox input, AdmissionSiblingSyncData.Field field)
        {
            input.Name = "admission" + field.Column;
            var button = input.AddInlineActionButton("\uE895", "Sync " + field.Label + " with siblings",
                async (_, _) => await SyncSiblingFieldAsync(input, field));
            button.Name = "syncSibling" + field.Column;
            button.AccessibleDescription = "Review old and new values before updating this field for the linked family.";
            tips.SetToolTip(button, button.AccessibleName);
            siblingSyncButtons.Add(button);
        }
    }

    private void UpdateSiblingSyncActions()
    {
        foreach (var button in siblingSyncButtons)
        {
            button.Visible = AdmissionSiblingSyncData.CanUse;
            button.Enabled = currentAdmissionId > 0 && !IsBusy && unconfirmedAdmission is null && !IsCancelledRegistration;
        }
    }

    internal async Task SyncSiblingFieldAsync(ModernTextBox input, AdmissionSiblingSyncData.Field field,
        Func<int, AdmissionSiblingSyncData.Field, string, Task<AdmissionSiblingSyncData.Preview?>>? read = null)
    {
        if (readingSiblingSync || IsBusy || currentAdmissionId <= 0 || unconfirmedAdmission is not null || IsCancelledRegistration
            || !AdmissionSiblingSyncData.CanUse) return;
        int anchor = currentAdmissionId, version = admissionLoadVersion;
        string value = input.Text;
        bool ownsSaveBusy = false;
        readingSiblingSync = true;
        try
        {
            // Keep the form untouched during this read: most clicks may have
            // nothing to sync. A full busy/disable cycle causes a visible blink.
            var preview = await (read ?? AdmissionSiblingSyncData.ReadAsync)(anchor, field, value);
            if (IsDisposed || currentAdmissionId != anchor || admissionLoadVersion != version
                || input.Text != value || IsBusy || preview is null) return;
            ownsSaveBusy = true;
            saveBusy = true;
            UpdateStudentWorkspace();
            using var submitted = HoldSubmittedInputs();
            using var dialog = new AdmissionSiblingSyncDialog(preview);
            if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
            if (IsDisposed || currentAdmissionId != anchor || admissionLoadVersion != version) return;
            AcceptSyncedField(input, preview.NewValue);
            SetStatus($"{field.Label} synced. {dialog.Changed} record(s) updated.", PageStatusKind.Success);
        }
        catch (Exception ex)
        {
            if (!IsDisposed && currentAdmissionId == anchor && admissionLoadVersion == version) Fail(ex);
        }
        finally
        {
            readingSiblingSync = false;
            if (ownsSaveBusy)
            {
                saveBusy = false;
                if (!IsDisposed) UpdateStudentWorkspace();
            }
        }
    }

    private void AcceptSyncedField(ModernTextBox input, string value)
    {
        input.Text = value;
        // This field (including the displayed student) was committed independently.
        // Keep every other draft edit and pending photo dirty; never recapture the entire form.
        if (_admissionBaseline is { } baseline)
        {
            int index = baseline.FindIndex(part => part.Where == input.Name);
            if (index >= 0) baseline[index] = new AdmissionPart(input.Name, value);
        }
        _admissionDifferenceReported = false;
    }
}
