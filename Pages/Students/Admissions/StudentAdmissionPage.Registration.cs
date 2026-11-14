using FMSC_SMS.Core;
using FMSC_SMS.Pages.Students.Maintenance;

namespace FMSC_SMS.Pages.Students.Admissions;

public sealed partial class StudentAdmissionPage
{
    protected override bool StatusBelowActions => true;
    private ModernButton? quickRegistrationButton, registrationRecoveryButton;
    private readonly Label pendingSiblingCount = new() { AutoSize = false, Height = AppMetrics.ButtonHeight, Font = AppTheme.NormalFont, TextAlign = ContentAlignment.MiddleLeft };
    private bool IsCancelledRegistration => _workspaceStudentStatus == "Cancelled";
    // Mirror CloseStudentAdmission's mandatory cancellation permissions. When saved
    // charges need reversing, the cancellation dialog additionally checks Fees.Adjust.
    private static bool CanCancelPendingRegistration() =>
        CurrentUser.HasPermission("Students.Administer") && CurrentUser.HasPermission("Fees.Collect");
    // ReopenPendingRegistration requires all four permissions, not only write access.
    private static bool CanReopenPendingRegistration() =>
        CurrentUser.HasPermission("Students.View") && CurrentUser.HasPermission("Students.Admit") &&
        CurrentUser.HasPermission("Students.Administer") && CurrentUser.HasPermission("Fees.View");
    private Action<Func<Control>, string, string> RegistrationNavigation => _navigate ?? ((_, _, _) => { });

    private void ConfigureRegistrationActions()
    {
        quickRegistrationButton = AddAction("Admit Sibling", async (_, _) => await RegisterChildAsync(true), "Secondary");
        Actions.Controls.Add(pendingSiblingCount);
        registrationRecoveryButton = AddAction("Check Registration Save", async (_, _) => {
            if (!IsBusy) RegistrationEntryDialog.CheckPreviousSave(FindForm(), RegistrationNavigation);
            await Task.CompletedTask;
        }, "Secondary");
        var draft = new ToolStripMenuItem("Save Registration Draft");
        draft.Click += async (_, _) => { if (IsPendingRegistration && !IsBusy) await SaveDraftOrDetailsAsync(); };
        enrollmentActionsMenu.Items.Add(draft);
        enrollmentActionsMenu.Opening += (_, _) => { draft.Visible = IsPendingRegistration; draft.Enabled = !IsBusy && CurrentUser.HasPermission("Students.Admit"); };
        var cancel = new ToolStripMenuItem("Cancel Registration");
        cancel.Click += async (_, _) => { if (IsPendingRegistration && !IsBusy && CanCancelPendingRegistration()) await CloseAdmissionAsync(true); };
        var reopen = new ToolStripMenuItem("Reopen Registration");
        reopen.Click += async (_, _) => { if (IsCancelledRegistration && !IsBusy && CanReopenPendingRegistration()) await ReopenRegistrationAsync(); };
        enrollmentActionsMenu.Items.Add(cancel);
        enrollmentActionsMenu.Items.Add(reopen);
        enrollmentActionsMenu.Opening += (_, _) => {
            cancel.Visible = IsPendingRegistration;
            cancel.Enabled = !IsBusy && CanCancelPendingRegistration();
            reopen.Visible = IsCancelledRegistration;
            reopen.Enabled = !IsBusy && CanReopenPendingRegistration();
        };
        siblingsGrid.CellContentClick += async (_, e) => await RunSiblingRegistrationActionAsync(e.RowIndex, e.ColumnIndex);
        siblingsGrid.CellPainting += (_, e) => {
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || siblingsGrid.Columns[e.ColumnIndex] is not DataGridViewButtonColumn ||
                !string.IsNullOrWhiteSpace(Convert.ToString(e.Value))) return;
            e.Paint(e.ClipBounds, DataGridViewPaintParts.Background | DataGridViewPaintParts.Border | DataGridViewPaintParts.SelectionBackground);
            e.Handled = true;
        };
        foreach (var button in new[] { quickRegistrationButton }) GateAction(button, "Students.Admit");
        UpdateRegistrationActions();
    }
    private void UpdateRegistrationActions()
    {
        bool ready = !IsBusy && unconfirmedAdmission is null;
        if (quickRegistrationButton is not null) quickRegistrationButton.Enabled = ready && currentAdmissionId > 0 && !IsCancelledRegistration && CurrentUser.HasPermission("Students.Admit") && CurrentUser.HasPermission("Students.View") && CurrentUser.HasPermission("Students.Administer");
        if (registrationRecoveryButton is not null)
        {
            try { registrationRecoveryButton.Visible = QuickRegistrationRecoveryJournal.ReadPending() is not null; }
            catch { registrationRecoveryButton.Visible = true; }
            registrationRecoveryButton.Enabled = ready && QuickRegistrationAccess.CanUse(CurrentUser.HasPermission);
        }
        admissionIdCaption.Text = IsPendingRegistration || IsCancelledRegistration ? "Temp Reference" : "Admission No.";
        if (saveButton is not null) saveButton.Text = IsPendingRegistration ? "Confirm & Save" : UiText.Actions.Save;
        RefreshSiblingRegistrationRows();
    }
    private async Task RunSiblingRegistrationActionAsync(int rowIndex, int columnIndex)
    {
            if (rowIndex < 0 || columnIndex < 0 || IsBusy || openingSibling) return;
            string action = siblingsGrid.Columns[columnIndex].Name;
            if (action is not ("RegistrationConfirm" or "RegistrationCancel" or "RegistrationReopen") ||
                string.IsNullOrWhiteSpace(Convert.ToString(siblingsGrid.Rows[rowIndex].Cells[columnIndex].Value))) return;
            if (action == "RegistrationConfirm" && !CurrentUser.HasPermission("Students.Admit") ||
                action == "RegistrationCancel" && !CanCancelPendingRegistration() ||
                action == "RegistrationReopen" && !CanReopenPendingRegistration()) return;
            int id = Convert.ToInt32(siblingsGrid.Rows[rowIndex].Cells["AdmissionId"].Value);
            if (id != currentAdmissionId)
            {
                if (!ConfirmDiscardAdmission() || !await LoadStudentForEditAsync(id) || currentAdmissionId != id) return;
            }
            if (action == "RegistrationConfirm") { SwitchTab(0); await CompleteRegistrationAsync(); }
            else if (action == "RegistrationCancel") await CloseAdmissionAsync(true);
            else await ReopenRegistrationAsync();
    }
    private void ConfigureSiblingRegistrationRows()
    {
        foreach (var item in new[] { ("RegistrationConfirm", "Confirm"), ("RegistrationCancel", "Cancel"), ("RegistrationReopen", "Reopen") })
            siblingsGrid.Columns.Add(new DataGridViewButtonColumn {
                Name = item.Item1, HeaderText = item.Item2, ReadOnly = true,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
                Width = AppScale.Scale(item.Item1 == "RegistrationConfirm" ? 145 : 100),
                MinimumWidth = AppScale.Scale(item.Item1 == "RegistrationConfirm" ? 145 : 100)
            });
        RefreshSiblingRegistrationRows();
    }
    private void RefreshSiblingRegistrationRows()
    {
        int waiting = siblingsGrid.Columns.Contains("Status") ? siblingsGrid.Rows.Cast<DataGridViewRow>().Count(row => !row.IsNewRow &&
            Convert.ToString(row.Cells["Status"].Value) == "Pending" && Convert.ToInt32(row.Cells["AdmissionId"].Value) != currentAdmissionId) : 0;
        pendingSiblingCount.Visible = currentAdmissionId > 0;
        pendingSiblingCount.Text = $"Pending: {waiting}";
        pendingSiblingCount.AccessibleDescription = "Siblings waiting for confirmation of admission.";
        pendingSiblingCount.Width = TextRenderer.MeasureText(pendingSiblingCount.Text, pendingSiblingCount.Font).Width + 16;
        if (!siblingsGrid.Columns.Contains("RegistrationConfirm")) return;
        foreach (DataGridViewRow row in siblingsGrid.Rows)
        {
            if (row.IsNewRow) continue;
            string state = Convert.ToString(row.Cells["Status"].Value) ?? "";
            bool ready = !IsBusy;
            row.Cells["RegistrationConfirm"].Value = ready && state == "Pending" && CurrentUser.HasPermission("Students.Admit") ? "Confirm & Save" : "";
            row.Cells["RegistrationCancel"].Value = ready && state == "Pending" && CanCancelPendingRegistration() ? "Cancel" : "";
            row.Cells["RegistrationReopen"].Value = ready && state == "Cancelled" && CanReopenPendingRegistration() ? "Reopen" : "";
        }
        foreach (string column in new[] { "RegistrationConfirm", "RegistrationCancel", "RegistrationReopen" })
            siblingsGrid.Columns[column].Visible = siblingsGrid.Rows.Cast<DataGridViewRow>().Any(row => !row.IsNewRow &&
                (column == "RegistrationReopen" ? Convert.ToString(row.Cells["Status"].Value) == "Cancelled" : Convert.ToString(row.Cells["Status"].Value) == "Pending"));
    }
    private async Task RegisterChildAsync(bool sibling)
    {
        if (IsBusy || !QuickRegistrationAccess.CanUse(CurrentUser.HasPermission) ||
            sibling && (currentAdmissionId <= 0 || IsCancelledRegistration || !CurrentUser.HasPermission("Students.Administer"))) return;
        if (HasUnsavedChanges) { Warn("Save or discard the loaded student's edits before admitting a sibling."); return; }
        int anchor = currentAdmissionId;
        string siblingPhone = contact.Text;
        try
        {
            var details = await DatabaseHelper.QueryUserAsync("GetStudentDetails", DatabaseHelper.Int("@AdmissionId", anchor));
            if (details.Rows.Count > 0 && !string.IsNullOrWhiteSpace(GetTextColumn(details.Rows[0], "ContactNumber")))
                siblingPhone = GetTextColumn(details.Rows[0], "ContactNumber");
        }
        catch (Exception ex) { Fail(ex); return; }
        if (IsDisposed || currentAdmissionId != anchor) return;
        using var dialog = new RegistrationEntryDialog(RegistrationNavigation, anchor, fatherName.Text, siblingPhone, address.Text, placeOfBirthInput.Text);
        dialog.ShowDialog(FindForm());
        if (dialog.SavedAdmissionId > 0)
        {
            StudentSearchCache.Invalidate();
            if (sibling) { await LoadCurrentSiblingsAsync(); SetStatus("Sibling saved as Pending. Select and open the sibling to complete details, then Confirm & Save.", PageStatusKind.Success); }
            else await LoadStudentForEditAsync(dialog.SavedAdmissionId);
        }
    }
    private async Task ReopenRegistrationAsync()
    {
        if (IsBusy || !IsCancelledRegistration || !CanReopenPendingRegistration()) return;
        if (HasUnsavedChanges && !ConfirmDiscardAdmission()) return;
        int id = currentAdmissionId;
        using var dialog = new RegistrationReopenDialog(id, name.Text);
        dialog.ShowDialog(FindForm());
        StudentSearchCache.Invalidate();
        await LoadStudentForEditAsync(id);
    }
}
