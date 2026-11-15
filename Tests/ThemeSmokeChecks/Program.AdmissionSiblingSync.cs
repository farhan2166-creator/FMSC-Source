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
                Check(grid.ReadOnly && grid.ColumnCount == 4 && grid.RowCount == 2
                    && Convert.ToString(grid.Rows[1].Cells[0].Value) == "0042"
                    && Convert.ToString(grid.Rows[1].Cells[2].Value) == sibling.OldValue
                    && Convert.ToString(grid.Rows[1].Cells[3].Value) == preview.NewValue && writes == 0,
                    "The read-only four-column preview shows admission, sibling and exact old/new values without writing");
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
            var editors = new[] { "address", "contact", "altContact", "fatherName", "prevSchool" }.Select(Get<ModernTextBox>).ToArray();
            Check(editors.All(editor => editor.Controls.OfType<Button>().Count() == 1),
                "Each of the five fields owns a separate inline sync icon");
            Call("UpdateSiblingSyncActions");
            Check(editors.All(editor => !editor.Controls.OfType<Button>().Single().Enabled),
                "Unsaved admissions cannot update sibling records");
            type.GetField("currentAdmissionId", flags)!.SetValue(page, 41);
            Call("UpdateSiblingSyncActions");
            Check(editors.All(editor => editor.Controls.OfType<Button>().Single().Enabled),
                "Saved records expose their own field action to student administrators");
            Call("CaptureAdmissionBaseline");
            Get<ModernTextBox>("name").Text = "Other Unsaved Change";
            editors[0].Text = preview.NewValue;
            Call("AcceptSyncedField", editors[0], preview.NewValue);
            Check(page.HasUnsavedChanges && Get<ModernTextBox>("name").Text == "Other Unsaved Change",
                "Saving the shared field preserves unrelated draft edits and their unsaved guard");
            Get<ModernTextBox>("name").Text = "";
            Check(!page.HasUnsavedChanges, "The committed source field alone is marked saved without recapturing other draft values");
            bool invalidPhone = false;
            try { AdmissionSiblingSyncData.Normalize(AdmissionSiblingSyncData.PrimaryPhone, "not a phone"); }
            catch (InvalidOperationException) { invalidPhone = true; }
            Check(invalidPhone && AdmissionSiblingSyncData.Normalize(AdmissionSiblingSyncData.FatherName, "ali khan") == "Ali Khan",
                "Sync retains the admission phone validation and name formatting rules");
            CurrentUser.SignIn(7202, "Synthetic viewer", "Fixture", false, new[] { "Students.View" });
            Call("UpdateSiblingSyncActions");
            Check(!AdmissionSiblingSyncData.CanUse, "Student viewers do not gain shared-record editing permission");
        }
        finally { CurrentUser.SignIn(previous.UserId, previous.Username, previous.RoleName, previous.MustChangePassword, previous.Item5); }
    }
}
