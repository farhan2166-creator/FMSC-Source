using System.Data;
using System.Reflection;
using FMSC_SMS.Core;
using FMSC_SMS.Pages.Students.Admissions;
using FMSC_SMS.Pages.Security;

internal static partial class Program
{
    private static void CheckAdmissionCompletion()
    {
        var submitted = new AdmissionSubmission("6590", "Synthetic Student", "Synthetic Parent", DateTime.Today, 2, false, 0);
        DataTable Saved(int id = 41)
        {
            var table = new DataTable();
            table.Columns.Add("AdmissionId", typeof(int)); table.Columns.Add("AdmissionNumber");
            table.Columns.Add("RegistrationNumber"); table.Columns.Add("StudentName"); table.Columns.Add("FatherName");
            table.Columns.Add("AdmissionDate", typeof(DateTime)); table.Columns.Add("AdmissionCampusId", typeof(int));
            table.Rows.Add(id, "6590", "R-SYNTHETIC", submitted.Student, submitted.Parent, submitted.Date, 2);
            return table;
        }
        int writes = 0, reads = 0;
        Task Write() { writes++; return Task.CompletedTask; }
        Task<DataTable> Read(AdmissionSubmission _) { reads++; return Task.FromResult(Saved()); }
        var normal = AdmissionSaveConfirmation.SubmitCompletionAsync(submitted, 41, Write, Read).GetAwaiter().GetResult();
        Check(normal.Admission?.Id == 41 && !normal.Reconciled && writes == 1 && reads == 1,
            "Save and confirm uses one write and verifies the existing student's final identity");
        var recovered = AdmissionSaveConfirmation.SubmitCompletionAsync(submitted, 41,
            () => { writes++; return Task.FromException(new IOException("Lost reply after commit")); }, Read).GetAwaiter().GetResult();
        Check(recovered.Admission?.Id == 41 && recovered.Reconciled && writes == 2,
            "A lost confirmation reply is recovered by reading, without repeating the write");
        var wrong = AdmissionSaveConfirmation.SubmitCompletionAsync(submitted, 41, Write,
            _ => Task.FromResult(Saved(42))).GetAwaiter().GetResult();
        Check(wrong.Admission is null, "A different student with the proposed number cannot confirm this registration");
        var unavailable = AdmissionSaveConfirmation.SubmitCompletionAsync(submitted, 41, Write,
            _ => Task.FromException<DataTable>(new IOException("Read unavailable"))).GetAwaiter().GetResult();
        Check(unavailable.Admission is null && unavailable.ReadError is not null,
            "An unavailable outcome remains unconfirmed for recovery");
        var pending = AdmissionSaveConfirmation.SubmitCompletionAsync(submitted, 41, Write,
            _ => { var table = Saved(); table.Rows.Clear(); return Task.FromResult(table); }).GetAwaiter().GetResult();
        Check(pending.Admission is null, "A registration without a completed identity is never reported as confirmed");

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var priorUser = (CurrentUser.UserId, CurrentUser.Username, CurrentUser.RoleName,
            CurrentUser.MustChangePassword, Permissions: CurrentUser.GetPermissions().ToArray());
        try
        {
            CurrentUser.SignIn(9104, "Synthetic admission reviewer", "Administrator", false, Array.Empty<string>());
            using var page = new StudentAdmissionPage(null, 0, true);
            var type = typeof(StudentAdmissionPage);
            T Field<T>(string name) => (T)type.GetField(name, flags)!.GetValue(page)!;
            type.GetField("currentAdmissionId", flags)!.SetValue(page, 41);
            type.GetField("_workspaceStudentStatus", flags)!.SetValue(page, "Pending");
            type.GetMethod("CaptureAdmissionBaseline", flags)!.Invoke(page, null);
            Field<ModernTextBox>("name").Text = "Unsaved completed student";
            type.GetMethod("UpdateStudentWorkspace", flags)!.Invoke(page, null);
            Check(page.HasUnsavedChanges && Field<ModernButton>("saveButton").Enabled,
                "Review retains unsaved edits and keeps the separate save action available");
            type.GetField("unconfirmedAdmission", flags)!.SetValue(page, submitted);
            type.GetMethod("UpdateStudentWorkspace", flags)!.Invoke(page, null);
            Check(!Field<ModernButton>("saveButton").Enabled && !Field<ModernButton>("confirmAdmissionButton").Enabled &&
                Field<ModernButton>("checkAdmissionButton").Visible,
                "An uncertain confirmation disables further saves and exposes Check Saved Admission");
            Check(!page.IsHandleCreated, "The admission review fixture starts no database loaders");
            type.GetField("registrationReadyToClose", flags)!.SetValue(page, true);
            type.GetField("saveBusy", flags)!.SetValue(page, true);
            int finished = 0; page.RegistrationFinished += () => finished++;
            type.GetMethod("FinishRegistrationReview", flags)!.Invoke(page, null);
            Check(finished == 0, "A confirmed admission cannot close its owner while the operation is busy");
            type.GetField("saveBusy", flags)!.SetValue(page, false);
            type.GetMethod("FinishRegistrationReview", flags)!.Invoke(page, null);
            type.GetMethod("FinishRegistrationReview", flags)!.Invoke(page, null);
            Check(finished == 1, "Completion closes the review once, after the busy scope has released");
        }
        finally
        {
            CurrentUser.SignIn(priorUser.UserId, priorUser.Username, priorUser.RoleName,
                priorUser.MustChangePassword, priorUser.Permissions);
        }

        string output = Path.GetFullPath("artifacts/admission-confirmation");
        Directory.CreateDirectory(output);
        foreach (float scale in new[] { 1f, 1.25f, 1.5f })
        {
            using var dialog = new CompleteAdmissionDialog("6590", "Synthetic Student · Synthetic Parent");
            if (scale != 1f) dialog.Scale(new SizeF(scale, scale));
            dialog.StartPosition = FormStartPosition.Manual; dialog.Location = new Point(-30000, -30000);
            dialog.Show(); Application.DoEvents();
            T Named<T>(string name) where T : Control => dialog.Controls.Find(name, true).OfType<T>().Single();
            var number = Named<ModernTextBox>("OfficialAdmissionNumber");
            var confirm = Named<ModernButton>("SaveAndConfirm");
            Check(number.Width > 300 * scale && number.Height >= AppMetrics.InputControlHeight,
                "The proposed admission number has a readable full-width input");
            Rectangle numberBounds = dialog.RectangleToClient(number.RectangleToScreen(number.ClientRectangle));
            Rectangle buttonBounds = dialog.RectangleToClient(confirm.RectangleToScreen(confirm.ClientRectangle));
            Check(numberBounds.Bottom < buttonBounds.Top && buttonBounds.Bottom <= dialog.ClientSize.Height,
                "The number review and confirmation button fit without overlap at each scale");
            var effect = Named<Label>("CompletionEffect");
            var effectBounds = dialog.RectangleToClient(effect.RectangleToScreen(effect.ClientRectangle));
            Check(effect.Height >= effect.GetPreferredSize(new Size(effect.Width, 0)).Height && effectBounds.Bottom <= buttonBounds.Top,
                "The complete explanation remains readable above the buttons");
            Check(confirm.ThemeRole == UiDesignSystem.Actions.Success, "Save & Confirm is styled as the primary saving action");
            number.Text = "invalid"; confirm.PerformClick();
            Check(dialog.DialogResult != DialogResult.OK && number.IsInvalid && Named<Label>("NumberValidation").Text.Contains("valid number"),
                "An invalid number shows an inline explanation and leaves review open");
            number.Text = "6590a";
            Check(!number.IsInvalid && Named<Label>("NumberValidation").Text == "", "Editing clears stale number validation");
            using (var bitmap = new Bitmap(dialog.Width, dialog.Height))
            {
                dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(Path.Combine(output, $"review-{scale}.png"));
            }
            confirm.PerformClick();
            Check(dialog.DialogResult == DialogResult.OK && dialog.AdmissionNumber == "6590a",
                "Save & Confirm returns the reviewed manual number");
        }
    }
}
