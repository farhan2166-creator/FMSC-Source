using System.Reflection;
using FMSC_SMS.Core;
using FMSC_SMS.Pages.Students.Admissions;
using FMSC_SMS.Pages.Security;

internal static partial class Program
{
    private static void CheckAdmissionSiblingSync()
    {
        var previous = (CurrentUser.UserId, CurrentUser.Username, CurrentUser.RoleName, CurrentUser.MustChangePassword, CurrentUser.GetPermissions().ToArray());
        try
        {
            CurrentUser.SignIn(7201, "Synthetic administrator", "Fixture", false, new[] { "Students.View", "Students.Administer" });
            var source = new AdmissionSiblingSyncData.Row(41, "0041", "Synthetic source", "Old Source Address");
            var sibling = new AdmissionSiblingSyncData.Row(42, "0042", "Synthetic sibling", "Old Sibling Address");
            var preview = new AdmissionSiblingSyncData.Preview(CurrentUser.UserId, 41, 7, AdmissionSiblingSyncData.Address,
                "Reviewed Address", new[] { source, sibling });
            Check(AdmissionSiblingSyncData.Review(CurrentUser.UserId, 41, 0, AdmissionSiblingSyncData.Address,
                "", new[] { source }) is null,
                "No linked sibling returns silently, even when the unused source value is blank");
            Check(AdmissionSiblingSyncData.Review(CurrentUser.UserId, 41, 7, AdmissionSiblingSyncData.Address,
                source.OldValue, new[] { source, sibling with { OldValue = source.OldValue } }) is null,
                "Matching sibling values do not open a preview or request an update");
            Check(AdmissionSiblingSyncData.Review(CurrentUser.UserId, 41, 7, AdmissionSiblingSyncData.Address,
                preview.NewValue, new[] { source, sibling with { OldValue = preview.NewValue } }) is null,
                "An unsaved source change alone is left to normal Save when the siblings already match");
            Check(AdmissionSiblingSyncData.Review(CurrentUser.UserId, 41, 7, AdmissionSiblingSyncData.Address,
                preview.NewValue, preview.Rows) is { NeedsSync: true },
                "A differing linked sibling still produces the reviewed sync scope");
            int writes = 0;
            Task<int> Apply(AdmissionSiblingSyncData.Preview reviewed)
            {
                Check(reviewed == preview, "Confirmation submits exactly the displayed field, value and family scope");
                writes++;
                return Task.FromResult(2);
            }
            using (var cancelled = new AdmissionSiblingSyncDialog(preview, Apply))
            {
                cancelled.Show(); Application.DoEvents();
                var grid = Field<DataGridView>(cancelled, "rows");
                var displayed=grid.Rows.Cast<DataGridViewRow>().Where(row=>!row.IsNewRow)
                    .Select(row=>row.Cells.Cast<DataGridViewCell>()
                        .Select(cell=>Convert.ToString(cell.Value)).ToArray()).ToArray();
                Check(grid.ReadOnly && displayed.Length==preview.Rows.Count &&
                    displayed.Any(values=>values.Contains("0042") &&
                        values.Contains(sibling.OldValue) && values.Contains(preview.NewValue)) && writes==0,
                    "The read-only preview retains the admission and exact old/new values without fixing column order");
                Field<ModernButton>(cancelled, "cancel").PerformClick();
                Check(writes == 0, "Cancelling the preview never updates a record");
            }
            using (var confirmed = new AdmissionSiblingSyncDialog(preview, Apply))
            {
                confirmed.Show(); Application.DoEvents();
                Field<ModernButton>(confirmed, "confirm").PerformClick(); Application.DoEvents();
                Check(writes == 1 && confirmed.DialogResult == DialogResult.OK && confirmed.Changed == 2,
                    "One confirmation performs one reviewed update and acknowledges the affected records");
            }
            using (var identical = new AdmissionSiblingSyncDialog(preview with
            {
                Rows = new[] { source with { OldValue = preview.NewValue }, sibling with { OldValue = preview.NewValue } }
            }, Apply))
            {
                identical.Show(); Application.DoEvents();
                Check(!Field<ModernButton>(identical, "confirm").Enabled && writes == 1,
                    "Already matching records do not permit an unnecessary write");
                identical.Close();
            }
            using (var stale = new AdmissionSiblingSyncDialog(preview,
                _ => throw new InvalidOperationException("A reviewed value changed. Reopen the preview.")))
            {
                stale.Show(); Application.DoEvents();
                Field<ModernButton>(stale, "confirm").PerformClick(); Application.DoEvents();
                Check(stale.Visible && stale.DialogResult != DialogResult.OK && Field<ModernButton>(stale, "cancel").Enabled,
                    "A failed or stale update stays recoverable and never reports successful completion");
                stale.Close();
            }
            using (var host = new ApplicationWindowForm { ClientSize = new Size(400, 100), ShowInTaskbar = false })
            using (var input = HumanUi.TextBox())
            {
                input.SetBounds(15, 15, 300, AppMetrics.InputControlHeight);
                var icon = input.AddInlineActionButton("\uE895", "Sync fixture", (_, _) => { });
                host.Controls.Add(input);
                host.Show(); Application.DoEvents();
                Check(input.ClientRectangle.Contains(icon.Bounds) && input.Controls.OfType<TextBox>().Single().Right <= icon.Left,
                    "The inline icon fits inside the field and reserves space instead of covering typed text");
                host.Close();
            }
            using var page = new StudentAdmissionPage(initialize: false);
            var type = typeof(StudentAdmissionPage);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            T Get<T>(string name) => (T)type.GetField(name, flags)!.GetValue(page)!;
            void Call(string name, params object[] args) => type.GetMethod(name, flags)!.Invoke(page, args);
            var editors = new[] { "address", "contact", "altContact", "fatherName", "prevSchool", "fatherOccupation", "casteTribe", "placeOfBirthInput" }
                .Select(Get<ModernTextBox>).ToArray();
            Check(editors.All(editor => editor.Controls.OfType<Button>().Count() == 1),
                "Each shared text field, including place of birth, owns an inline sync icon");
            var consent = Get<ModernComboBox>("schoolMessagesConsent");
            var consentAction = Get<ModernButton>("consentSyncButton");
            var consentEntry = consent.Parent as Panel;
            var consentField = consentEntry?.Parent;
            var guardianGrid = consentField?.Parent as TableLayoutPanel;
            var fatherField = Get<ModernTextBox>("fatherOccupation").Parent;
            var addressField = Get<ModernTextBox>("address").Parent;
            Check(consentEntry is not null && consentEntry.Controls.Contains(consentAction) &&
                guardianGrid is not null && ReferenceEquals(guardianGrid, fatherField?.Parent) &&
                ReferenceEquals(guardianGrid, addressField?.Parent) &&
                guardianGrid.GetPositionFromControl(consentField!).Column ==
                    guardianGrid.GetPositionFromControl(fatherField!).Column &&
                guardianGrid.GetPositionFromControl(consentField!).Row ==
                    guardianGrid.GetPositionFromControl(fatherField!).Row + 1 &&
                guardianGrid.GetPositionFromControl(consentField!).Column ==
                    guardianGrid.GetPositionFromControl(addressField!).Column + guardianGrid.GetColumnSpan(addressField!),
                "Consent is beside its own sync action below father occupation and address stops before consent");
            // Test excess vertical space and width changes. Neither a large
            // field container nor resizing can change this button's height.
            var originalConsentEntrySize = consentEntry!.Size;
            foreach (int width in new[] { AppScale.Scale(260), AppScale.Scale(380) })
            {
                consentEntry.Size = new Size(width, AppMetrics.InputControlHeight * 3);
                consentEntry.PerformLayout();
                Check(consentEntry is not TableLayoutPanel &&
                    consentAction.Dock == DockStyle.None && consent.Dock == DockStyle.None &&
                    consentAction.Height == AppMetrics.InputControlHeight &&
                    consent.Height == AppMetrics.InputControlHeight &&
                    consentAction.Top == consent.Top && consentAction.Top == 0 &&
                    consentAction.Bottom < consentEntry.ClientSize.Height &&
                    consentAction.Right == consentEntry.ClientSize.Width &&
                    consent.Right + UiDesignSystem.Spacing.Xs <= consentAction.Left,
                    $"Consent sync button geometry: requested width={width}, panel={consentEntry.ClientSize}, " +
                    $"combo={consent.Bounds}, button={consentAction.Bounds}, " +
                    $"button dock={consentAction.Dock}, combo dock={consent.Dock}, expected height={AppMetrics.InputControlHeight}");
            }
            consentEntry.Size = originalConsentEntrySize;
            Call("UpdateSiblingSyncActions");
            Check(editors.All(editor => !editor.Controls.OfType<Button>().Single().Enabled) && !consentAction.Enabled,
                "Unsaved admissions cannot update sibling records or consent");
            type.GetField("currentAdmissionId", flags)!.SetValue(page, 41);
            Call("UpdateSiblingSyncActions");
            Check(editors.All(editor => editor.Controls.OfType<Button>().Single().Enabled) && consentAction.Enabled,
                "Saved records expose text and consent sync actions to student administrators");
            Call("CaptureAdmissionBaseline");
            consent.SelectedIndex = 1;
            Call("AcceptSyncedField", consent, "Yes");
            Check(!page.HasUnsavedChanges && consent.SelectedIndex == 1 && Get<bool?>("loadedSchoolMessageConsent") == true,
                "Consent synchronization commits the selected combo choice without leaving a false unsaved edit");
            consent.SelectedIndex = 2;
            Check(page.HasUnsavedChanges, "Changing a synced consent choice still counts as an unsaved edit");
            consent.SelectedIndex = 1;
            Check(!page.HasUnsavedChanges, "Re-selecting the committed consent choice clears the draft difference");
            Get<ModernTextBox>("name").Text = "Other Unsaved Change";
            editors[0].Text = preview.NewValue;
            Call("AcceptSyncedField", editors[0], preview.NewValue);
            Check(page.HasUnsavedChanges && Get<ModernTextBox>("name").Text == "Other Unsaved Change",
                "Saving the shared field preserves unrelated draft edits and their unsaved guard");
            Get<ModernTextBox>("name").Text = "";
            Check(!page.HasUnsavedChanges, "The committed source field alone is marked saved without recapturing other draft values");
            int enabledChanges = 0, reads = 0;
            page.EnabledChanged += (_, _) => enabledChanges++;
            var pending = new TaskCompletionSource<AdmissionSiblingSyncData.Preview?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<AdmissionSiblingSyncData.Preview?> Read(int anchor, AdmissionSiblingSyncData.Field field, string value)
            {
                reads++;
                Check(anchor == 41 && field == AdmissionSiblingSyncData.Address && value == editors[0].Text,
                    "A click checks the current student's exact field value");
                return pending.Task;
            }
            var quietClick = page.SyncSiblingFieldAsync(editors[0], AdmissionSiblingSyncData.Address, Read);
            var repeatedClick = page.SyncSiblingFieldAsync(editors[0], AdmissionSiblingSyncData.Address, Read);
            Check(page.Enabled && editors.All(editor => editor.Enabled) && !page.IsBusy && enabledChanges == 0
                && reads == 1 && repeatedClick.IsCompleted,
                "Checking for differences leaves the form enabled and ignores repeat clicks without a busy-state blink");
            pending.SetResult(null);
            PumpUntil(() => quietClick.IsCompleted, "Quiet sibling check completes");
            quietClick.GetAwaiter().GetResult();
            Check(page.Enabled && enabledChanges == 0 && !page.IsBusy && !page.HasUnsavedChanges,
                "A no-op click completes without disabling, refreshing or dirtying the form");

            var duringSave = new TaskCompletionSource<AdmissionSiblingSyncData.Preview?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var racingClick = page.SyncSiblingFieldAsync(editors[0], AdmissionSiblingSyncData.Address, (_, _, _) => duringSave.Task);
            type.GetField("saveBusy", flags)!.SetValue(page, true);
            duringSave.SetResult(null);
            PumpUntil(() => racingClick.IsCompleted, "A read overlapping normal Save completes");
            racingClick.GetAwaiter().GetResult();
            Check(Get<bool>("saveBusy") && enabledChanges == 0,
                "A quiet sibling read never clears another operation's Save guard");
            type.GetField("saveBusy", flags)!.SetValue(page, false);

            bool invalidPhone = false;
            try { AdmissionSiblingSyncData.Normalize(AdmissionSiblingSyncData.PrimaryPhone, "not a phone"); }
            catch (InvalidOperationException) { invalidPhone = true; }
            Check(invalidPhone && AdmissionSiblingSyncData.Normalize(AdmissionSiblingSyncData.FatherName, "ali khan") == "Ali Khan",
                "Sync retains the admission phone validation and name formatting rules");
            Check(AdmissionSiblingSyncData.Normalize(AdmissionSiblingSyncData.FatherOccupation, "math teacher") == "Math Teacher"
                && AdmissionSiblingSyncData.Normalize(AdmissionSiblingSyncData.CasteTribe, "abbasi") == "Abbasi",
                "Father occupation and caste use their existing admission text rules");
            bool rejectsUnknownConsent = false;
            try { AdmissionSiblingSyncData.Normalize(AdmissionSiblingSyncData.Consent, "Not recorded"); }
            catch (InvalidOperationException) { rejectsUnknownConsent = true; }
            Check(rejectsUnknownConsent &&
                AdmissionSiblingSyncData.Normalize(AdmissionSiblingSyncData.Consent, "Yes") == "Yes" &&
                AdmissionSiblingSyncData.Normalize(AdmissionSiblingSyncData.Consent, "No") == "No" &&
                AdmissionSiblingSyncData.Normalize(AdmissionSiblingSyncData.PlaceOfBirth, "islamabad") == "Islamabad",
                "Sync accepts only explicit Yes/No consent and applies the normal birthplace text policy");
            // Exercise the real Admission page while visible, not only the
            // detached control tree. This catches layout after the host scales.
            using (var visibleHost = new ApplicationWindowForm {
                ClientSize = new Size(AppScale.Scale(1280), AppScale.Scale(860)),
                ShowInTaskbar = false
            })
            {
                page.Dock = DockStyle.Fill;
                visibleHost.Controls.Add(page);
                visibleHost.Show();
                Application.DoEvents();
                Check(consentEntry.Width > 0 &&
                    consentAction.Height == AppMetrics.InputControlHeight &&
                    consent.Height == AppMetrics.InputControlHeight &&
                    consentAction.Top == consent.Top &&
                    consentAction.Bottom <= consentEntry.ClientSize.Height &&
                    consentAction.Right == consentEntry.ClientSize.Width,
                    "The visible Admission page keeps the consent sync button at dropdown height");
                visibleHost.Controls.Remove(page);
                visibleHost.Close();
            }
            CurrentUser.SignIn(7202, "Synthetic viewer", "Fixture", false, new[] { "Students.View" });
            Call("UpdateSiblingSyncActions");
            Check(!AdmissionSiblingSyncData.CanUse, "Student viewers do not gain shared-record editing permission");
        }
        finally { CurrentUser.SignIn(previous.UserId, previous.Username, previous.RoleName, previous.MustChangePassword, previous.Item5); }
    }
}
