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
            CheckCommunicationSessionsAndOptions();
            CheckManualLaunchAndReporting();
            CheckCommunicationApiWorkflow();
            CheckOptionalCommunicationEntries();
            CurrentUser.SignIn(7202, "Read-only fixture", "Messaging viewer", false, new[] { "Communication.View" });
            using var reader = new CommunicationCenterPage(false);
            Check(CommunicationAccess.CanOpen(CurrentUser.HasPermission) && !CommunicationAccess.CanCompose(CurrentUser.HasPermission)
                && Field<ModernButton>(reader, "historyButton").ThemeRole == "TabActive"
                && !Field<ModernButton>(reader, "unfinishedButton").Visible,
                "History-only access does not expose a composing/sending action");
            CurrentUser.SignIn(7203, "Settings fixture", "Messaging configuration", false, new[] { "Communication.Configure" });
            using var settings = new CommunicationCenterPage(false);
            Check(CommunicationAccess.CanOpen(CurrentUser.HasPermission)
                && Field<ModernButton>(settings, "configureButton").Visible
                && Field<ModernButton>(settings, "templatesButton").Visible
                && !Field<ModernButton>(settings, "historyButton").Visible,
                "A configuration-only operator can open both administration dialogs without history access");
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
            new CommunicationPreviewEntry(43,"Z Synthetic child","Student","+923011234567","Included",null,"43",DateTime.Today),
            new CommunicationPreviewEntry(44,"Excluded fixture","Student",null,"Consent not recorded",null)
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
        Field<ModernButton>(page, "newMessageButton").PerformClick();
        // Explicit school target keeps this synthetic async read independent
        // of the safer class-by-default choice in the actual user experience.
        Field<ModernComboBox>(page, "audience").SelectedIndex = (int)CommunicationAudience.School;
        var body = Field<ModernTextBox>(page, "message");
        var next = Field<ModernButton>(page, "next");
        next.PerformClick();
        Check(reads == 0 && Field<Label>(page, "messageIssue").Text.Length > 0,
            "Reviewing an empty message gives feedback beside the editor before any recipient read");
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
        var excluded = Field<CheckBox>(page, "showExcluded");
        Check(!excluded.Checked && grid.Rows.Count == 2 && Field<Label>(page, "summary").Text.Contains("1 consent not recorded"),
            "Review shows eligible rows and counts their exclusions before opening details");
        excluded.Checked = true;
        grid.CurrentCell = grid.Rows[2].Cells[0]; Application.DoEvents();
        var sample = Field<ModernTextBox>(page, "sampleMessage");
        Check(grid.Rows.Count == 3 && sample.Text.Contains("A Synthetic child") && !sample.Text.Contains("Excluded fixture"),
            "Expanding excluded records keeps the preview tied to an eligible recipient");
        grid.CurrentCell = grid.Rows[1].Cells[0]; Application.DoEvents();
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
        Field<ModernButton>(singlePage, "newMessageButton").PerformClick();
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
        Field<ModernButton>(bulkPage, "newMessageButton").PerformClick();
        Field<ModernComboBox>(bulkPage, "audience").SelectedIndex = (int)CommunicationAudience.School;
        Field<ModernTextBox>(bulkPage, "message").Text = "School notice";
        Field<ModernButton>(bulkPage, "next").PerformClick();
        PumpUntil(() => !bulkPage.IsBusy, "Bulk communication preview finishes");
        Field<ModernButton>(bulkPage, "confirm").PerformClick();
        Application.DoEvents();
        Check(bulkApprovals == 1 && openedFirstBulkChat,
            "One bulk confirmation opens the first reviewed chat without an extra Open action");

        using var excludedHost = new Form { ClientSize = new Size(1100, 780), ShowInTaskbar = false };
        using var excludedPage = new CommunicationCenterPage(false, operations with
        {
            ReadRecipients = _ => Task.FromResult(new CommunicationPreview(new[] { entries.Entries[2] }))
        });
        excludedHost.Controls.Add(excludedPage);
        excludedHost.Show();
        Application.DoEvents();
        Field<ModernButton>(excludedPage, "newMessageButton").PerformClick();
        Field<ModernComboBox>(excludedPage, "audience").SelectedIndex = (int)CommunicationAudience.School;
        Field<ModernTextBox>(excludedPage, "message").Text = "School notice";
        Field<ModernButton>(excludedPage, "next").PerformClick();
        PumpUntil(() => !excludedPage.IsBusy, "Review with only excluded records finishes");
        var excludedToggle = Field<CheckBox>(excludedPage, "showExcluded");
        var excludedGrid = Field<DataGridView>(excludedPage, "previewGrid");
        Check(excludedToggle.Visible && !excludedToggle.Checked && !excludedGrid.Visible
            && !Field<ModernButton>(excludedPage, "confirm").Enabled
            && Field<ModernTextBox>(excludedPage, "sampleMessage").Text == "School notice",
            "A review with zero eligible recipients shows the draft and keeps sending disabled");
        excludedToggle.Checked = true;
        Check(excludedGrid.Visible && excludedGrid.Rows.Count == 1,
            "The zero-eligible review can expand its excluded records");
    }

    private static void CheckCommunicationSessionsAndOptions()
    {
        Guid pending = Guid.NewGuid(), completed = Guid.NewGuid();
        Guid? resumed = null, viewed = null;
        int confirmations = 0;
        const string savedBody = "Original message [StudentName]";
        CommunicationHistoryDraft? newDraft = null;
        DataTable Sessions(bool unfinished)
        {
            var rows = new DataTable();
            rows.Columns.Add("BatchId", typeof(Guid));
            rows.Columns.Add("Method");
            rows.Columns.Add("Audience");
            rows.Columns.Add("OperatorName");
            rows.Columns.Add("InProgress", typeof(int));
            foreach (var column in new[] { "ManualReported", "ManualOpened", "ManualSkipped" })
                rows.Columns.Add(column, typeof(int));
            rows.Rows.Add(unfinished ? pending : completed, "WhatsApp Business (manual)", "School",
                "Synthetic operator", unfinished ? 1 : 0, unfinished ? 0 : 1, 0, 0);
            return rows;
        }
        var operations = new CommunicationCenterPage.Operations(
            _ => throw new Exception("Viewing a session does not run a new review."),
            () => Task.FromResult(true),
            (_, _, _, _) => { confirmations++; throw new Exception("Opening a session does not confirm a send."); },
            (batch, openFirstChat) => { if (!openFirstChat) resumed = batch; },
            () => false, unfinished => Task.FromResult(Sessions(unfinished)), row =>
            {
                viewed = row.BatchId;
                return newDraft;
            });
        CurrentUser.SignIn(7204, "Session fixture", "Administrator", false, Array.Empty<string>());
        using var host = new Form { ClientSize = new Size(1100, 700), ShowInTaskbar = false };
        using var page = new CommunicationCenterPage(false, operations);
        host.Controls.Add(page);
        host.Show();
        Application.DoEvents();
        var queue = Field<DataGridView>(page, "unfinishedGrid");
        var archive = Field<DataGridView>(page, "historyGrid");
        void DoubleClick(DataGridView grid)
        {
            typeof(DataGridView).GetMethod("OnCellDoubleClick", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(grid, new object[] { new DataGridViewCellEventArgs(0, 0) });
            Application.DoEvents();
        }
        Field<ModernButton>(page, "unfinishedButton").PerformClick();
        PumpUntil(() => queue.Rows.Count == 1 && !page.IsBusy, "Unfinished loads its own session list");
        DoubleClick(queue);
        Check(resumed == pending && viewed is null && confirmations == 0 && !archive.Visible,
            "Double-clicking an unfinished session resumes that batch without opening a chat or confirming another send");
        Field<ModernButton>(page, "historyButton").PerformClick();
        PumpUntil(() => archive.Rows.Count == 1 && !page.IsBusy, "History loads its separate list");
        var openHistory = (ModernButton)page.Controls.Find("communicationViewHistory", true).Single();
        Check(openHistory.Enabled && !queue.Visible, "A selected history row can be viewed independently of the unfinished queue");
        DoubleClick(archive);
        Check(viewed == completed && resumed == pending && confirmations == 0,
            "Double-clicking History opens the selected message record without resuming or sending");
        page.Controls.Find("communicationHistorySearch", true).Single().Text = "Unmatched fixture";
        Application.DoEvents();
        Check(archive.Rows.Count == 0 && !openHistory.Enabled, "Filtering history removes the open action when no session matches");
        page.Controls.Find("communicationHistorySearch", true).Single().Text = "";
        newDraft = new CommunicationHistoryDraft(new(CommunicationAudience.School, 0, 0, 0, 0, "WhatsApp"),
            "WhatsAppBusinessManual", savedBody, "", "", null);
        DoubleClick(archive);
        Check(Field<ModernTextBox>(page, "message").Text == savedBody
            && Field<ModernButton>(page, "next").Visible
            && Field<CommunicationPreview?>(page, "preview") is null
            && confirmations == 0 && !archive.Visible,
            "Send Again brings the saved format into a new draft and requires a fresh review");
        host.Close();

        using var history = new CommunicationHistoryDialog(new(completed, "Twilio", "Delivered", "School"), _ =>
        {
            var rows = new DataTable();
            foreach (var column in new[] { "Body", "RenderedBody", "Audience", "Channel", "SelectedStudentName",
                "SelectedStaffName", "ProviderCode", "TemplateJson", "Outcome", "RecipientName", "LastFour" })
                rows.Columns.Add(column);
            foreach (var column in new[] { "CampusId", "ClassId", "StudentId", "StaffId" })
                rows.Columns.Add(column, typeof(int));
            rows.Rows.Add(savedBody, "Original message First", "School", "Sms", "", "", "Twilio", DBNull.Value,
                "Delivered", "First", "0123", 0, 0, 0, 0);
            rows.Rows.Add(savedBody, "Original message Second", "School", "Sms", "", "", "Twilio", DBNull.Value,
                "Failed", "Second", "4567", 0, 0, 0, 0);
            return Task.FromResult(rows);
        });
        history.Show();
        Application.DoEvents();
        var historyPreview = Field<ModernTextBox>(history, "preview");
        Check(historyPreview.ReadOnly && historyPreview.Text == "Original message First" && history.Draft?.Body == savedBody,
            "History shows the exact reviewed API message and keeps the saved format for a new draft");
        var recipients = Field<DataGridView>(history, "recipients");
        recipients.CurrentCell = recipients.Rows[1].Cells[0];
        Check(historyPreview.Text == "Original message Second", "History preview follows the selected recipient");
        var unsettled = (DataTable)recipients.DataSource!;
        unsettled.Rows[1]["Outcome"] = "Unknown";
        Check(CommunicationHistoryStore.CreateDraft(unsettled) is null,
            "An uncertain attempt keeps Send Again unavailable until the batch can be reviewed safely");
        history.Close();

        using var settings = new CommunicationSendingOptionsDialog(() =>
        {
            var rows = new DataTable();
            rows.Columns.Add("Channel");
            rows.Columns.Add("ProviderCode");
            rows.Columns.Add("Enabled", typeof(bool));
            rows.Rows.Add("Sms", "Twilio", true);
            rows.Rows.Add("WhatsApp", "WhatsAppBusinessManual", false);
            return Task.FromResult(rows);
        });
        settings.Show();
        Application.DoEvents();
        Control Manual(string name) => settings.Controls.Find(name, true).Single();
        Check(Manual("WhatsAppBusinessManualToggle").Enabled && Manual("TwilioToggle").Enabled
            && !Manual("MetaCloudToggle").Enabled && Manual("MetaCloudSetup").Enabled
            && !Descendants(settings).OfType<DataGridView>().Any(),
            "Configure Sending presents independent actions for active, paused and unconfigured methods");
        settings.Close();

        using var templates = new CommunicationTemplatesDialog(() =>
        {
            var rows = new DataTable();
            rows.Columns.Add("TemplateId", typeof(int));
            foreach (var column in new[] { "Name", "Channel", "Purpose", "Body" }) rows.Columns.Add(column);
            rows.Columns.Add("Active", typeof(bool));
            return Task.FromResult(rows);
        });
        templates.Show();
        Application.DoEvents();
        var templateGrid = Field<DataGridView>(templates, "templates");
        templateGrid.CurrentCell = templateGrid.Rows[1].Cells[0];
        Check(Field<ModernTextBox>(templates, "preview").Text == CommunicationPreparedMessages.BuiltIns[1].Body
            && Field<ModernButton>(templates, "edit").Enabled,
            "Selecting a template shows its full editable format in the dedicated dialog");
        templates.Close();
        CurrentUser.SignIn(7201, "Synthetic sender", "Messaging fixture", false,
            new[] { "Communication.Send", "Students.View", "Fees.View" });
    }

    private static void CheckManualLaunchAndReporting()
    {
        string state = "Pending";
        Guid batchId = Guid.NewGuid();
        int marks = 0, launches = 0, warnings = 0;
        bool failLaunch = true;
        string message = "Current prepared message & اطلاع";
        var operations = new CommunicationManualHandoffDialog.Operations(
            _ => Task.FromResult<IReadOnlyList<CommunicationManualRecipient>>(new[] { new CommunicationManualRecipient(1,"Synthetic child","4567",state) }),
            (_, _) => Task.FromResult(message),
            _ => { state = "Opened"; return Task.FromResult(new CommunicationManualOpen(true, new Uri("https://wa.me/923001234567?text=fixture"), null, message)); },
            (_, result) => { marks++; state = result; return Task.CompletedTask; },
            _ => { if (failLaunch) throw new System.ComponentModel.Win32Exception("Synthetic missing browser"); launches++; },
            _ => warnings++);
        using var dialog = new CommunicationManualHandoffDialog(batchId, operations);
        dialog.Show(); Application.DoEvents();
        Check(Field<ProgressBar>(dialog, "processedProgress").Value == 0,
            "A pending recipient has no completed processing progress");
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
        Check(Field<ProgressBar>(dialog, "processedProgress").Value == 0,
            "Opening a chat alone does not advance completed processing or imply sending");
        Field<ModernButton>(dialog, "close").PerformClick();

        using var reopened = new CommunicationManualHandoffDialog(batchId, operations);
        reopened.Show(); Application.DoEvents();
        Check(Field<ModernTextBox>(reopened, "preparedMessage").Text == message
            && launches == 1 && marks == 0,
            "Reopening after Finish Later restores the message before any browser launch or operator report");
        report = Field<ModernButton>(reopened, "reported");
        report.PerformClick(); Application.DoEvents();
        Check(marks == 1 && state == "ReportedSent" && !report.Enabled,
            "One explicit report finishes the recipient without a second confirmation or a delivery claim");
        Check(Field<ProgressBar>(reopened, "processedProgress").Value == 1
            && Field<ModernTextBox>(reopened, "preparedMessage").Text.Length == 0,
            "Explicit operator reporting advances the processed-recipient progress");
        reopened.Close();

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
