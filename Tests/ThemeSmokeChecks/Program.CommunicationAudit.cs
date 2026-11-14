using System.Reflection;
using FMSC_SMS.Core;
using FMSC_SMS.Pages.Communication;
using FMSC_SMS.Pages.Security;
using FMSC_SMS.Pages.Finance.Collection;
using FMSC_SMS.Pages.Students.Admissions;

internal static partial class Program
{
    private static void CheckCommunicationAudit()
    {
        var previous = (CurrentUser.UserId, CurrentUser.Username, CurrentUser.RoleName, CurrentUser.MustChangePassword, CurrentUser.GetPermissions().ToArray());
        try
        {
            CurrentUser.SignIn(7201, "Synthetic sender", "Messaging fixture", false,
                new[] { "Communication.Send", "Students.View", "Fees.View" });
            Check(CommunicationAccess.CanOpen(CurrentUser.HasPermission) && !CurrentUser.HasPermission("Communication.View"),
                "A sender can open messaging without being granted history access");
            foreach (string bad in new[] { "Hello [Typo]", "Hello [StudentName", "Hello StudentName]", "Hello {{StudentName}}" })
            {
                bool refused = false;
                try { CommunicationPreparedMessages.ValidateFormat(bad); }
                catch (CommunicationValidationException ex) { refused = DbMessage.UserMessage(ex) == ex.Message; }
                Check(refused, "Invalid prepared formats are rejected with recoverable operator guidance");
            }
            bool missingTargetRefused = false;
            try { CommunicationRecipientResolver.Validate(new(CommunicationAudience.Class, 0, 0, 0, 0, "WhatsApp")); }
            catch (CommunicationValidationException ex) { missingTargetRefused = DbMessage.UserMessage(ex).Contains("Select the required"); }
            Check(missingTargetRefused, "An incomplete audience gives actionable selection guidance rather than an unexpected-error message");
            string unicode = "اطلاع & 'notice'\n[StudentName]";
            string rendered = CommunicationPreparedMessages.Render(unicode, new(42, "Synthetic child", "42", DateTime.Today, 250));
            var link = CommunicationManualHandoffStore.BuildClickToChatLink("+923001234567", rendered);
            Check(Uri.UnescapeDataString(link.Query).Contains(rendered) && link.Host == "wa.me", "Unicode, quotes, ampersands and newlines survive official WhatsApp handoff encoding");
            using (var template = new CommunicationTemplateDialog("Saved email template", "Email", "Announcement", "School notice"))
                Check(template.Channel == "Email", "Editing a saved email template preserves its channel rather than silently converting it to SMS");
            CheckProviderBodyDeadline();
            CheckCommunicationAsyncAndCancellation();
            CheckManualLaunchAndReporting();
            CheckOptionalCommunicationEntries();
            CurrentUser.SignIn(7202, "Read-only fixture", "Messaging viewer", false, new[] { "Communication.View" });
            using var reader = new CommunicationCenterPage(false);
            Check(CommunicationAccess.CanOpen(CurrentUser.HasPermission) && !CommunicationAccess.CanCompose(CurrentUser.HasPermission)
                && Field<int>(reader, "step") == 2 && !Field<ModernButton>(reader, "next").Visible,
                "History-only access does not expose a composing/sending action");
            CurrentUser.SignIn(7203, "Settings fixture", "Messaging configuration", false, new[] { "Communication.Configure" });
            using var settings = new CommunicationCenterPage(false);
            Check(CommunicationAccess.CanOpen(CurrentUser.HasPermission) && Field<int>(settings, "step") == 3,
                "A configuration-only operator opens Settings without student or history access");
        }
        finally { CurrentUser.SignIn(previous.UserId, previous.Username, previous.RoleName, previous.MustChangePassword, previous.Item5); }
    }

    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static void SetField(object owner, string name, object value) =>
        owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);

    private static void CheckCommunicationAsyncAndCancellation()
    {
        var entries = new CommunicationPreview(new[] {
            new CommunicationPreviewEntry(42,"A Synthetic child","Student","+923001234567","Included",null,"42",DateTime.Today),
            new CommunicationPreviewEntry(43,"Z Synthetic child","Student","+923011234567","Included",null,"43",DateTime.Today)
        });
        var read = new TaskCompletionSource<CommunicationPreview>();
        var confirm = new TaskCompletionSource<CommunicationConfirmation>();
        bool delayed = true, approve = false;
        int reads = 0, writes = 0, opens = 0;
        var operations = new CommunicationCenterPage.Operations(
            _ => { reads++; return delayed ? read.Task : Task.FromResult(entries); },
            () => Task.FromResult(true),
            (_, _, _, _) => { writes++; return confirm.Task; }, (_, _) => opens++, () => approve);
        using var page = new CommunicationCenterPage(false, operations);
        using var host = new Form { ClientSize = new Size(1100, 780), ShowInTaskbar = false };
        page.Dock = DockStyle.Fill; host.Controls.Add(page); ModalPageCloseGuard.Attach(host, page);
        host.Show(); Application.DoEvents();
        var body = Field<ModernTextBox>(page, "message");
        var next = Field<ModernButton>(page, "next");
        body.Text = "Notice [StudentName] " + new string('x', 600);
        next.PerformClick();
        Check(page.IsBusy && !page.Enabled && reads == 1, "Preview holds navigation and conflicting input while its actual read is pending");
        next.PerformClick();
        Check(reads == 1, "A repeated Continue cannot start a second recipient read");
        var closing = new FormClosingEventArgs(CloseReason.UserClosing, false);
        typeof(Form).GetMethod("OnFormClosing", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(host, new object[] { closing });
        Check(closing.Cancel, "Hosted optional messaging cannot close during submitted work");
        body.Text = "Changed while a synthetic read is delayed";
        read.SetResult(entries);
        PumpUntil(() => !page.IsBusy, "Delayed communication read finishes");
        Check(Field<int>(page, "step") == 0 && opens == 0 && writes == 0, "A stale recipient read cannot advance or dispatch after a target/message change");
        delayed = false;
        body.Text = "Notice [StudentName] " + new string('x', 600);
        next.PerformClick(); PumpUntil(() => !page.IsBusy, "Fresh communication preview finishes");
        var grid = Field<DataGridView>(page, "previewGrid");
        grid.CurrentCell = grid.Rows[1].Cells[0]; Application.DoEvents();
        var sample = Field<ModernTextBox>(page, "sampleMessage");
        Check(sample.ReadOnly && sample.ScrollBars == ScrollBars.Vertical, "The complete recipient preview is read-only and scrollable");
        Check(sample.Text.Contains("Z Synthetic child"), "Preview follows the current recipient after the cell changes: " + sample.Text[..Math.Min(140, sample.Text.Length)]);
        Check(sample.Text.Contains(new string('x', 600)), "The complete rendered message remains available without ellipsis");
        var start = Field<ModernButton>(page, "confirm");
        Check(start.Visible && start.Enabled, "The reviewed message can proceed directly to WhatsApp");
        start.PerformClick(); PumpUntil(() => !page.IsBusy, "Canceled confirmation returns");
        Check(writes == 0 && opens == 0, "Cancel before confirmation persists no batch and launches no handoff");
        approve = true;
        start.PerformClick();
        Check(page.IsBusy && writes == 1 && opens == 0, "Confirmation persists the reviewed batch before any handoff is allowed");
        start.PerformClick(); Check(writes == 1, "An in-flight confirmation cannot be submitted twice");
        body.Text = "Synthetic late programmatic change";
        confirm.SetResult(new(Guid.NewGuid(), 2, false));
        PumpUntil(() => !page.IsBusy, "Delayed confirmation finishes");
        Check(opens == 0 && writes == 1, "A late completion never opens a different/stale message after the current intent changes");
        host.Close();

        int bulkPrompts = 0;
        bool requestedDirectChat = false;
        CommunicationCenterPage? singlePage = null;
        using var singleHost = new Form { ClientSize = new Size(1100, 780), ShowInTaskbar = false };
        singlePage = new CommunicationCenterPage(false, operations with
        {
            ReadRecipients = _ => Task.FromResult(new CommunicationPreview(new[] { entries.Entries[0] })),
            ConfirmBatch = (_, _, _, _) => Task.FromResult(new CommunicationConfirmation(Guid.NewGuid(), 1, false)),
            OpenHandoff = (_, openFirstChat) => { requestedDirectChat = openFirstChat; singlePage!.Dispose(); },
            ConfirmStart = () => { bulkPrompts++; return false; }
        });
        singleHost.Controls.Add(singlePage);
        singleHost.Show(); Application.DoEvents();
        Field<ModernComboBox>(singlePage, "audience").SelectedIndex = (int)CommunicationAudience.Student;
        Field<StudentPicker>(singlePage, "student").SetStudent(42, "Synthetic child");
        Field<ModernTextBox>(singlePage, "message").Text = "Hello [StudentName]";
        Field<ModernButton>(singlePage, "next").PerformClick();
        PumpUntil(() => !singlePage.IsBusy, "Single recipient preview finishes");
        Field<ModernButton>(singlePage, "confirm").PerformClick();
        Check(requestedDirectChat && bulkPrompts == 0, "One reviewed student opens WhatsApp directly with no bulk prompt");

        // Bulk delivery still needs explicit operator confirmation, but that
        // confirmation also opens the first chat. There is no second Open click.
        int bulkApprovals = 0;
        bool openedFirstBulkChat = false;
        CommunicationCenterPage? bulkPage = null;
        using var bulkHost = new Form { ClientSize = new Size(1100, 780), ShowInTaskbar = false };
        bulkPage = new CommunicationCenterPage(false, operations with
        {
            ReadRecipients = _ => Task.FromResult(entries),
            ConfirmBatch = (_, _, _, _) => Task.FromResult(
                new CommunicationConfirmation(Guid.NewGuid(), 2, false)),
            ConfirmStart = () => { bulkApprovals++; return true; },
            OpenHandoff = (_, openFirstChat) =>
            {
                openedFirstBulkChat = openFirstChat;
                bulkPage!.Dispose();
            }
        });
        bulkHost.Controls.Add(bulkPage);
        bulkHost.Show(); Application.DoEvents();
        Field<ModernTextBox>(bulkPage, "message").Text = "School notice";
        Field<ModernButton>(bulkPage, "next").PerformClick();
        PumpUntil(() => !bulkPage.IsBusy, "Bulk communication preview finishes");
        Field<ModernButton>(bulkPage, "confirm").PerformClick();
        Application.DoEvents();
        Check(bulkApprovals == 1 && openedFirstBulkChat,
            "One bulk confirmation opens the first reviewed chat without an extra Open action");
    }

    private static void CheckManualLaunchAndReporting()
    {
        string state = "Pending";
        int marks = 0, launches = 0, warnings = 0;
        bool failLaunch = true;
        string message = "Current prepared message & اطلاع";
        var operations = new CommunicationManualHandoffDialog.Operations(
            _ => Task.FromResult<IReadOnlyList<CommunicationManualRecipient>>(new[] { new CommunicationManualRecipient(1,"Synthetic child","4567",state) }),
            _ => { state = "Opened"; return Task.FromResult(new CommunicationManualOpen(true, new Uri("https://wa.me/923001234567?text=fixture"), null, message)); },
            (_, result) => { marks++; state = result; return Task.CompletedTask; },
            _ => { if (failLaunch) throw new System.ComponentModel.Win32Exception("Synthetic missing browser"); launches++; },
            _ => warnings++);
        using var dialog = new CommunicationManualHandoffDialog(Guid.NewGuid(), operations);
        dialog.Show(); Application.DoEvents();
        var open = Field<ModernButton>(dialog, "open");
        var report = Field<ModernButton>(dialog, "reported");
        open.PerformClick(); Application.DoEvents();
        Check(warnings == 1 && !report.Enabled && launches == 0 && marks == 0,
            "Failed external launch cannot enable or record an operator-sent outcome");
        var mark = dialog.GetType().GetMethod("MarkAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ((Task)mark.Invoke(dialog, new object[] { "ReportedSent" })!).GetAwaiter().GetResult();
        Check(marks == 0, "Direct handler invocation also refuses reporting after a failed launch");
        failLaunch = false; open.PerformClick(); Application.DoEvents();
        Check(launches == 1 && report.Enabled && Field<ModernTextBox>(dialog,"preparedMessage").Text == message,
            "Successful reopening exposes the exact handed-off message and permits explicit reporting");
        report.PerformClick(); Application.DoEvents();
        Check(marks == 1 && state == "ReportedSent" && !report.Enabled,
            "One explicit report finishes the recipient without a second confirmation or a delivery claim");
        dialog.Close();

        int recoveredReports = 0;
        bool checkedWhatsApp = false;
        using var resumed = new CommunicationManualHandoffDialog(Guid.NewGuid(), operations with
        {
            Read = _ => Task.FromResult<IReadOnlyList<CommunicationManualRecipient>>(
                new[] { new CommunicationManualRecipient(1, "Synthetic child", "4567", "Opened") }),
            ConfirmRecoveredSend = () => checkedWhatsApp,
            MarkRecovered = _ => { recoveredReports++; return Task.CompletedTask; }
        });
        resumed.Show(); Application.DoEvents();
        var recovered = Field<ModernButton>(resumed, "reported");
        recovered.PerformClick();
        Check(recoveredReports == 0, "A prior preparation without shell acknowledgement requires the operator to check WhatsApp.");
        checkedWhatsApp = true;
        recovered.PerformClick();
        Check(recoveredReports == 1 && marks == 1,
            "An interrupted launch records a sent report only through the explicit recovery action.");
        resumed.Close();

        state = "Pending";
        using var direct = new CommunicationManualHandoffDialog(Guid.NewGuid(), operations, openFirstChat: true);
        direct.Show(); Application.DoEvents();
        Check(launches == 2 && state == "Opened" && marks == 1,
            "A new single-recipient handoff opens the chat without a second Open click");
        Field<ModernButton>(direct, "reported").PerformClick(); Application.DoEvents();
        Check(marks == 2 && !direct.Visible, "Reporting the single message closes its completed handoff");

        // Marking a contact never marks the next contact Sent. The explicit
        // "I Sent It · Next" action only opens the next reviewed WhatsApp chat.
        string first = "Pending", second = "Pending";
        int advancedLaunches = 0, advancedMarks = 0;
        var consecutive = operations with
        {
            Read = _ => Task.FromResult<IReadOnlyList<CommunicationManualRecipient>>(
                new[] {
                    new CommunicationManualRecipient(1, "First child", "4567", first),
                    new CommunicationManualRecipient(2, "Second child", "5678", second)
                }),
            Open = id =>
            {
                if (id == 1) first = "Opened";
                else if (id == 2) second = "Opened";
                return Task.FromResult(new CommunicationManualOpen(true,
                    new Uri("https://wa.me/923001234567?text=fixture"), null, message));
            },
            Mark = (id, outcome) =>
            {
                if (id == 1) first = outcome;
                else if (id == 2) second = outcome;
                advancedMarks++;
                return Task.CompletedTask;
            },
            Launch = _ => advancedLaunches++,
            ConfirmRecoveredSend = null,
            MarkRecovered = null
        };
        using var consecutiveDialog = new CommunicationManualHandoffDialog(
            Guid.NewGuid(), consecutive, openFirstChat: true);
        consecutiveDialog.Show(); Application.DoEvents();
        Check(first == "Opened" && second == "Pending" && advancedLaunches == 1,
            "The first chat opens after the reviewed batch is confirmed; the second stays pending");
        Field<ModernButton>(consecutiveDialog, "reported").PerformClick();
        Application.DoEvents();
        Check(first == "ReportedSent" && second == "Opened"
            && advancedLaunches == 2 && advancedMarks == 1,
            "Reporting the first chat opens the next without reporting it sent");
        consecutiveDialog.Close();
    }

    private static void CheckProviderBodyDeadline()
    {
        string token = "synthetic-auth-token-not-real";
        var credential = new CommunicationProviderCredentialStore.Credential(1, "Sms", "Twilio", "AC" + new string('a', 32),
            "+923001234567", token, token);
        using var handler = new StalledProviderReply();
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(150) };
        var send = CommunicationTwilioTransport.SendOnceAsync(credential, "+923001234567", "Synthetic notice", client, default);
        PumpUntil(() => send.IsCompleted, "Provider response body deadline expires");
        Check(send.GetAwaiter().GetResult().State == "Unknown" && handler.Requests == 1,
            "Stalled API response bodies stay Unknown within a deadline and are never resent");
        var read = CommunicationTwilioTransport.ReadStatusAsync(credential, "SM" + new string('b', 32), client, default);
        PumpUntil(() => read.IsCompleted, "Provider status response deadline expires");
        Check(read.GetAwaiter().GetResult().State == "Unknown" && handler.Requests == 2,
            "Status polling also bounds streaming after HTTP headers, without a submission retry");
    }

    private sealed class StalledProviderReply : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(request.Method == HttpMethod.Post
                ? System.Net.HttpStatusCode.Created : System.Net.HttpStatusCode.OK) { Content = new StreamContent(new StalledProviderBody()) });
        }
    }
    private sealed class StalledProviderBody : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false); return 0; }
    }

    private static void CheckOptionalCommunicationEntries()
    {
        int opens = 0;
        CommunicationTarget? captured = null;
        void Cancel(Control owner, int id, string display, bool fees) { opens++; captured = CommunicationRecordEntry.Target(id, fees); }
        using var admission = new StudentAdmissionPage(false);
        SetField(admission, "currentAdmissionId", 42);
        var name = Field<ModernTextBox>(admission, "name"); name.Text = "Unsubmitted admission edit";
        admission.OpenStudentMessage(Cancel);
        Check(opens == 1 && captured?.StudentId == 42 && captured.Audience == CommunicationAudience.Student
            && name.Text == "Unsubmitted admission edit", "Optional Admission messaging uses the saved student and cancel leaves unsaved details intact");
        using var fees = new FeeCollectionPage(false);
        SetField(fees, "admissionId", 42);
        var reference = Field<ModernTextBox>(fees, "reference"); reference.Text = "Unsaved payment reference";
        var remarks = Field<ModernTextBox>(fees, "remarks"); remarks.Text = "Unsaved payment remarks";
        fees.OpenStudentMessage(Cancel);
        Check(opens == 2 && captured?.StudentId == 42 && captured.Audience == CommunicationAudience.OutstandingFees
            && reference.Text == "Unsaved payment reference" && remarks.Text == "Unsaved payment remarks",
            "Optional Collection messaging scopes current fees to the main student and never reloads or clears the payment draft");
        CurrentUser.SignIn(7204, "Viewer", "Read-only fixture", false, new[] { "Students.View", "Fees.View" });
        admission.OpenStudentMessage(Cancel); fees.OpenStudentMessage(Cancel);
        Check(opens == 2 && !CommunicationAccess.CanOpen(CurrentUser.HasPermission),
            "Opening either integration is independently refused after sender permission is removed");
    }
}
