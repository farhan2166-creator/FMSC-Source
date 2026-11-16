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
            // Prepared fields are resolved from the same scoped preview as the live
            // handoff, not from an unrestricted student details query.
            var enriched = new CommunicationPreviewEntry(42, "Synthetic child", "Student",
                "+923001234567", "Included", null, "42", DateTime.Today)
            {
                FatherName = "Synthetic father",
                ClassName = "Grade 5"
            };
            string enrichedBody = CommunicationPreparedMessages.Render(
                "Dear [RecipientName], father [FatherName], class [ClassName]",
                CommunicationPreparedMessages.FromPreview(enriched));
            Check(enrichedBody.Contains("Synthetic child")
                && enrichedBody.Contains("Synthetic father") && enrichedBody.Contains("Grade 5"),
                "Authorized student and current-class fields resolve into the reviewed message");
            bool missingClassRejected = false;
            try
            {
                CommunicationPreparedMessages.Render("Class [ClassName]",
                    new CommunicationPreparedMessages.Values(42, "Synthetic child", "42", null, null));
            }
            catch (CommunicationValidationException) { missingClassRejected = true; }
            Check(missingClassRejected,
                "Missing class data prevents an inaccurate prepared message");
            bool staffFieldRejected = false;
            try { CommunicationPreparedMessages.ValidateFormat("[FatherName]", "Staff"); }
            catch (CommunicationValidationException) { staffFieldRejected = true; }
            Check(staffFieldRejected, "Staff messages cannot use student-family fields");
            var link = CommunicationManualHandoffStore.BuildClickToChatLink("+923001234567", rendered);
            Check(Uri.UnescapeDataString(link.Query).Contains(rendered) && link.Host == "wa.me", "Unicode, quotes, ampersands and newlines survive official WhatsApp handoff encoding");
            using (var template = new CommunicationTemplateDialog("Saved email template", "Email", "Announcement", "School notice"))
                Check(template.Channel == "Email", "Editing a saved email template preserves its channel rather than silently converting it to SMS");
            using (var urduTemplate = new CommunicationTemplateDialog(
                "Bilingual school notice", "WhatsApp", "Announcement",
                "محترم والدین", true, "ur"))
                Check(urduTemplate.LanguageCode == "ur" && urduTemplate.Channel == "WhatsApp",
                    "Saved Urdu templates preserve both language and sending channel independently.");
            Check(CommunicationPreparedMessages.BuiltInChoices.Any(item =>
                    item.LanguageCode == "ur" && item.Channel == "WhatsApp")
                && CommunicationPreparedMessages.BuiltInChoices.Any(item =>
                    item.LanguageCode == "en" && item.Name == "New admission welcome"),
                "English and Urdu prepared messages are separate curated versions.");
            using (var languageComposer = new CommunicationCenterPage(false))
            {
                var languageInput = Field<ModernComboBox>(languageComposer, "languageChoice");
                var draft = Field<ModernTextBox>(languageComposer, "message");
                draft.Text = "Original approved English draft";
                languageInput.SelectedIndex = 1;
                Check(draft.Text == "Original approved English draft",
                    "Changing the compose language never translates or overwrites an existing draft.");
            }

            Check(CommunicationHistoryDialog.OutcomeExplanation("MetaCloud", "Accepted")
                    .Contains("not yet confirmed", StringComparison.OrdinalIgnoreCase)
                && CommunicationHistoryDialog.OutcomeExplanation("Twilio", "Delivered")
                    .Contains("Provider-confirmed", StringComparison.Ordinal)
                && CommunicationHistoryDialog.OutcomeExplanation("WhatsAppBusinessManual", "ReportedSent")
                    .Contains("not provider-verified", StringComparison.Ordinal)
                && CommunicationHistoryDialog.OutcomeExplanation("Twilio", "Unknown")
                    .Contains("do not resend", StringComparison.OrdinalIgnoreCase),
                "History distinguishes provider acceptance, independently verified delivery, uncertain attempts and manually reported sends.");
            using (var browser = new CommunicationComposerTemplateBrowser(
                CommunicationPreparedMessages.BuiltInChoices.Where(option =>
                    option.Matches(new CommunicationTarget(CommunicationAudience.Student,0,0,42,0,"Sms")))))
            {
                var matching = (DataGridView)browser.Controls.Find("communicationComposerTemplateResults", true).Single();
                var templateSearch = (ModernTextBox)browser.Controls.Find("communicationComposerTemplateSearch", true).Single();
                Check(matching.Rows.Count > 0 && matching.Rows.Cast<DataGridViewRow>().All(row =>
                    !Convert.ToString(row.Cells["Name"].Value)!.Contains("(Urdu)", StringComparison.Ordinal)),
                    "Sender template browser keeps Urdu manual-WhatsApp drafts out of SMS.");
                templateSearch.Text = "new admission welcome";
                Check(matching.Rows.Count == 1
                    && Convert.ToString(matching.Rows[0].Cells["Name"].Value) == "New admission welcome",
                    "Sender template browser searches context-compatible templates without selecting a recipient or sending.");
            }
            Check(CommunicationHistoryDialog.CanCheckProviderDelivery("Twilio","Accepted",91)
                && CommunicationHistoryDialog.CanCheckProviderDelivery("Twilio","Unknown",91)
                && !CommunicationHistoryDialog.CanCheckProviderDelivery("MetaCloud","Accepted",91)
                && !CommunicationHistoryDialog.CanCheckProviderDelivery("Twilio","Delivered",91)
                && !CommunicationHistoryDialog.CanCheckProviderDelivery("WhatsAppBusinessManual","ReportedSent",91)
                && !CommunicationHistoryDialog.CanCheckProviderDelivery("Twilio","Accepted",null),
                "Only an identified Twilio provider attempt can be checked without resending; manual handoffs and delivered messages cannot.");
            string familyUrdu = CommunicationAutomationInboxStore.SuggestedBody(
                "OutstandingFees","ur",familyFinancial:true);
            string familyEnglish = CommunicationAutomationInboxStore.SuggestedBody(
                "OutstandingFees","en",familyFinancial:true);
            Check(familyUrdu.Contains("خاندان",StringComparison.Ordinal)
                && familyUrdu.Contains("[OutstandingAmount]",StringComparison.Ordinal)
                && !familyUrdu.Contains("[StudentName]",StringComparison.Ordinal)
                && familyEnglish.Contains("family",StringComparison.OrdinalIgnoreCase)
                && !familyEnglish.Contains("[StudentName]",StringComparison.Ordinal),
                "Family fee suggestions avoid misrepresenting the household's amount as one student's bill.");
            CheckProviderBodyDeadline();
            CheckCommunicationAsyncAndCancellation();
            CheckCommunicationSessionsAndOptions();
            CheckManualLaunchAndReporting();
            CheckCommunicationApiWorkflow();
            CheckOptionalCommunicationEntries();
            CheckRecordMessageDialog();
            CurrentUser.SignIn(7202, "Read-only fixture", "Messaging viewer", false, new[] { "Communication.View" });
            using var reader = new CommunicationCenterPage(false);
            Check(CommunicationAccess.CanOpen(CurrentUser.HasPermission) && !CommunicationAccess.CanCompose(CurrentUser.HasPermission)
                && Field<ModernButton>(reader, "historyButton").ThemeRole == "TabActive"
                && !Field<ModernButton>(reader, "unfinishedButton").Visible,
                "History-only access does not expose a composing/sending action");
            CurrentUser.SignIn(7203, "Settings fixture", "Messaging configuration", false, new[] { "Communication.Configure" });
            using var languageSettings = new CommunicationLanguageSettingsDialog();
            Check(languageSettings.Controls.Find("communicationDefaultMessageLanguage", true).Length==1,
                "A configuration-only administrator can edit the school language preference.");
            using var settings = new CommunicationCenterPage(false);
            Check(CommunicationAccess.CanOpen(CurrentUser.HasPermission)
                && Field<ModernButton>(settings, "configureButton").Visible
                && Field<ModernButton>(settings, "templatesButton").Visible
                && !Field<ModernButton>(settings, "historyButton").Visible,
                "A configuration-only operator can open both administration dialogs without history access");
            CurrentUser.SignIn(7204, "Administrator fixture", "Messaging administrator", false,
                new[] { "Communication.Configure", "Communication.Send", "Communication.View",
                    "Students.View", "Staff.View", "Fees.View" });
            using var takeover = new CommunicationTakeoverDialog();
            Check(takeover.Controls.Find("communicationTakeoverQueue", true).Length == 1
                && takeover.Controls.Find("communicationTakeoverReason", true).Length == 1,
                "Held communication takeover has a dedicated reasoned administrator review rather than silently reassigning the batch.");
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
        page.SelectedAudience = CommunicationAudience.School;
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
        singlePage.SelectedAudience = CommunicationAudience.Student;
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
        bulkPage.SelectedAudience = CommunicationAudience.School;
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
        excludedPage.SelectedAudience = CommunicationAudience.School;
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
        using var recipientFacts = new DataTable();
        recipientFacts.Columns.Add("RecipientCount", typeof(long));
        foreach (var column in new[] { "RecipientName", "RecordNumber", "LastFour", "ClassName", "CampusName" })
            recipientFacts.Columns.Add(column);
        string queuedRecipient = CommunicationManagementStore.FormatSessionRecipient(
            recipientFacts.Rows.Add(1L, "Synthetic child", "0042", "0123"));
        string sentRecipient = CommunicationManagementStore.FormatSessionRecipient(
            recipientFacts.Rows.Add(1L, "Fictional sibling", "0043", "4567"));
        string classRecipients = CommunicationManagementStore.FormatSessionRecipient(
            recipientFacts.Rows.Add(18L, DBNull.Value, DBNull.Value, DBNull.Value, "Class Five", "North Campus"));
        Check(classRecipients.Contains("Class Five") && classRecipients.Contains("North Campus") && classRecipients.Contains("18"),
            "Bulk history identifies the saved class and campus and shows the destination count");
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
            rows.Columns.Add("RecipientText");
            rows.Rows.Add(unfinished ? pending : completed, "WhatsApp Business (manual)", "FeeStudent",
                "Synthetic operator", unfinished ? 1 : 0, unfinished ? 0 : 1, 0, 0,
                unfinished ? queuedRecipient : sentRecipient);
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
        string queued = Convert.ToString(queue.Rows[0].Cells["RecipientText"].Value)!;
        Check(queued.Contains("Synthetic child") && queued.Contains("0042") && queued.Contains("0123"),
            "Unfinished identifies the exact selected student and preserves leading zeros in the masked destination");
        var queueSearch = page.Controls.Find("communicationUnfinishedSearch", true).Single();
        queueSearch.Text = "4567";
        Check(queue.Rows.Count == 0, "Unfinished filters out sessions with a different saved destination ending");
        queueSearch.Text = "0123";
        Check(queue.Rows.Count == 1, "Unfinished search finds a session by its saved destination ending");
        DoubleClick(queue);
        Check(resumed == pending && viewed is null && confirmations == 0 && !archive.Visible,
            "Double-clicking an unfinished session resumes that batch without opening a chat or confirming another send");
        Field<ModernButton>(page, "historyButton").PerformClick();
        PumpUntil(() => archive.Rows.Count == 1 && !page.IsBusy, "History loads its separate list");
        var openHistory = (ModernButton)page.Controls.Find("communicationViewHistory", true).Single();
        Check(openHistory.Enabled && !queue.Visible, "A selected history row can be viewed independently of the unfinished queue");
        string sent = Convert.ToString(archive.Rows[0].Cells["RecipientText"].Value)!;
        Check(sent.Contains("Fictional sibling") && sent.Contains("0043") && sent.Contains("4567"),
            "History identifies the student record and saved destination associated with the outcome");
        DoubleClick(archive);
        Check(viewed == completed && resumed == pending && confirmations == 0,
            "Double-clicking History opens the selected message record without resuming or sending");
        page.Controls.Find("communicationHistorySearch", true).Single().Text = "Unmatched fixture";
        Application.DoEvents();
        Check(archive.Rows.Count == 0 && !openHistory.Enabled, "Filtering history removes the open action when no session matches");
        page.Controls.Find("communicationHistorySearch", true).Single().Text = "fictional sibling";
        Check(archive.Rows.Count == 1 && openHistory.Enabled, "History search finds the recipient by name regardless of capitalization");
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
                "SelectedStaffName", "ProviderCode", "TemplateJson", "Outcome", "RecipientName", "RecordNumber", "LastFour" })
                rows.Columns.Add(column);
            foreach (var column in new[] { "CampusId", "ClassId", "StudentId", "StaffId" })
                rows.Columns.Add(column, typeof(int));
            rows.Rows.Add(savedBody, "Original message First", "School", "Sms", "", "", "Twilio", DBNull.Value,
                "Delivered", "First", "0042", "0123", 0, 0, 0, 0);
            rows.Rows.Add(savedBody, "Original message Second", "School", "Sms", "", "", "Twilio", DBNull.Value,
                "Failed", "Second", "0043", "4567", 0, 0, 0, 0);
            return Task.FromResult(rows);
        });
        history.Show();
        Application.DoEvents();
        var historyPreview = Field<ModernTextBox>(history, "preview");
        Check(historyPreview.ReadOnly && historyPreview.Text == "Original message First" && history.Draft?.Body == savedBody,
            "History shows the exact reviewed API message and keeps the saved format for a new draft");
        var recipients = Field<DataGridView>(history, "recipients");
        Check(Equals(recipients.Rows[0].Cells["RecordNumber"].Value, "0042")
            && Equals(recipients.Rows[1].Cells["RecordNumber"].Value, "0043"),
            "History details distinguish every recipient by their record number next to the destination ending and outcome");
        recipients.CurrentCell = recipients.Rows[1].Cells[0];
        Check(historyPreview.Text == "Original message Second", "History preview follows the selected recipient");
        var unsettled = (DataTable)recipients.DataSource!;
        unsettled.Rows[1]["Outcome"] = "Unknown";
        Check(CommunicationHistoryStore.CreateDraft(unsettled) is null,
            "An uncertain attempt keeps Send Again unavailable until the batch can be reviewed safely");
        using var acceptedMetaRows = unsettled.Copy();
        history.Close();

        var metaTemplate = new CommunicationMetaTransport.Selection(
            new("school_notice", "en", "Original message {{1}}", "", ""), new[] { "[StudentName]" });
        foreach (DataRow row in acceptedMetaRows.Rows)
        {
            row["Channel"] = "WhatsApp"; row["ProviderCode"] = "MetaCloud"; row["Outcome"] = "Accepted";
            row["TemplateJson"] = System.Text.Json.JsonSerializer.Serialize(metaTemplate);
        }
        using (var acceptedHistory = new CommunicationHistoryDialog(new(completed,"MetaCloud","Accepted","School"),
            _ => Task.FromResult(acceptedMetaRows.Copy())))
        {
            acceptedHistory.Show(); Application.DoEvents();
            var again = Field<ModernButton>(acceptedHistory,"sendAgain");
            Check(again.Visible && again.Enabled && acceptedHistory.Draft?.MetaTemplate?.Template == metaTemplate.Template
                && Field<ModernTextBox>(acceptedHistory,"preview").Text == "Original message First",
                "Accepted Meta history offers a new draft while preserving the saved preview and approved template");
            Check(acceptedMetaRows.AsEnumerable().All(row => row.Field<string>("Outcome") == "Accepted"),
                "Opening accepted Meta history never fabricates delivery or sends another message");
            acceptedHistory.Close();
        }

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
            rows.Rows.Add(47, "Office collection", "Sms", "Announcement", "Bring the coordinator's blue folder.", true);
            return Task.FromResult(rows);
        });
        templates.Show();
        Application.DoEvents();
        var templateGrid = Field<DataGridView>(templates, "templates");
        templateGrid.CurrentCell = templateGrid.Rows[1].Cells[0];
        Check(Field<ModernTextBox>(templates, "preview").Text == CommunicationPreparedMessages.BuiltIns[1].Body
            && Field<ModernButton>(templates, "edit").Enabled,
            "Selecting a template shows its full editable format in the dedicated dialog");
        var templateSearch = templates.Controls.Find("communicationTemplateSearch", true).Single();
        var templateEdit = Field<ModernButton>(templates, "edit");
        var templatePreview = Field<ModernTextBox>(templates, "preview");
        Rectangle editBounds = templateEdit.Bounds;
        templateSearch.Text = "SMS COORDINATOR'S";
        Application.DoEvents();
        Check(templateGrid.Rows.Count == 1
            && (templateGrid.CurrentRow?.DataBoundItem as DataRowView)?.Row.Field<int>("TemplateId") == 47
            && templatePreview.Text == "Bring the coordinator's blue folder."
            && templateEdit.Enabled && templateEdit.Bounds == editBounds,
            "Template search matches channel and body case-insensitively, selects the match and keeps actions aligned");
        templateSearch.Text = "[no matching draft]";
        Application.DoEvents();
        Check(templateGrid.Rows.Count == 0 && templatePreview.Text.Length == 0 && !templateEdit.Enabled,
            "No matching templates clears the preview and disables editing");
        templateSearch.Text = "";
        Application.DoEvents();
        Check(templateGrid.Rows.Count == CommunicationPreparedMessages.BuiltIns.Count + 1,
            "Clearing search restores built-in and school templates");
        var templateCategory = (ModernComboBox)templates.Controls
            .Find("communicationTemplateCategory", true).Single();
        templateCategory.SelectedItem = "Fees & Payments";
        Application.DoEvents();
        Check(templateGrid.Rows.Count > 0 &&
            templateGrid.Rows.Cast<DataGridViewRow>().All(row =>
                row.DataBoundItem is DataRowView item &&
                item.Row.Field<string>("DisplayCategory") == "Fees & Payments"),
            "Selecting Fees & Payments shows only templates classified in that category");
        templateCategory.SelectedItem = "All Categories";
        Application.DoEvents();
        Check(templateGrid.Rows.Count == CommunicationPreparedMessages.BuiltIns.Count + 1,
            "Clearing category restores built-in and school templates");
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
        var operations = new CommunicationManualHandoff.Operations(
            _ => Task.FromResult<IReadOnlyList<CommunicationManualRecipient>>(new[] { new CommunicationManualRecipient(1,"Synthetic child","4567",state) }),
            (_, _) => Task.FromResult(message),
            _ => { state = "Opened"; return Task.FromResult(new CommunicationManualOpen(true, new Uri("https://wa.me/923001234567?text=fixture"), null, message)); },
            (_, result) => { marks++; state = result; return Task.CompletedTask; },
            _ => { if (failLaunch) throw new System.ComponentModel.Win32Exception("Synthetic missing browser"); launches++; },
            _ => warnings++);
        using var dialog = new CommunicationManualHandoffDialog(batchId, operations);
        dialog.Show(); Application.DoEvents();
        Check(Field<ProgressBar>(dialog.Handoff, "processedProgress").Value == 0,
            "A pending recipient has no completed processing progress");
        var open = Field<ModernButton>(dialog.Handoff, "open");
        var report = Field<ModernButton>(dialog.Handoff, "reported");
        open.PerformClick(); Application.DoEvents();
        Check(warnings == 1 && !report.Enabled && launches == 0 && marks == 0,
            "Failed external launch cannot enable or record an operator-sent outcome");
        var mark = dialog.Handoff.GetType().GetMethod("MarkAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ((Task)mark.Invoke(dialog.Handoff, new object[] { "ReportedSent" })!).GetAwaiter().GetResult();
        Check(marks == 0, "Direct handler invocation also refuses reporting after a failed launch");
        failLaunch = false; open.PerformClick(); Application.DoEvents();
        Check(launches == 1 && report.Enabled && Field<ModernTextBox>(dialog.Handoff,"preparedMessage").Text == message,
            "Successful reopening exposes the exact handed-off message and permits explicit reporting");
        Check(Field<ProgressBar>(dialog.Handoff, "processedProgress").Value == 0,
            "Opening a chat alone does not advance completed processing or imply sending");
        Field<ModernButton>(dialog.Handoff, "close").PerformClick();

        using var reopened = new CommunicationManualHandoffDialog(batchId, operations);
        reopened.Show(); Application.DoEvents();
        Check(Field<ModernTextBox>(reopened.Handoff, "preparedMessage").Text == message
            && launches == 1 && marks == 0,
            "Reopening after Finish Later restores the message before any browser launch or operator report");
        report = Field<ModernButton>(reopened.Handoff, "reported");
        report.PerformClick(); Application.DoEvents();
        Check(marks == 1 && state == "ReportedSent" && !report.Enabled,
            "One explicit report finishes the recipient without a second confirmation or a delivery claim");
        Check(Field<ProgressBar>(reopened.Handoff, "processedProgress").Value == 1
            && Field<ModernTextBox>(reopened.Handoff, "preparedMessage").Text.Length == 0,
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
        var recovered = Field<ModernButton>(resumed.Handoff, "reported");
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
        Field<ModernButton>(direct.Handoff, "reported").PerformClick(); Application.DoEvents();
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
        int imageCopies = 0;
        bool clipboardBusy = false;
        using var consecutiveDialog = new CommunicationManualHandoffDialog(
            Guid.NewGuid(), consecutive with
            {
                Launch = _ =>
                {
                    Check(imageCopies == advancedLaunches + 1, "Each chat receives its image before the browser is launched");
                    advancedLaunches++;
                }
            }, openFirstChat: true, prepareImage: () =>
            {
                if (clipboardBusy) throw new System.Runtime.InteropServices.ExternalException("Synthetic busy clipboard");
                imageCopies++;
                return Task.CompletedTask;
            });
        consecutiveDialog.Show(); Application.DoEvents();
        Check(first == "Opened" && second == "Pending" && advancedLaunches == 1,
            "The first chat opens after the reviewed batch is confirmed; the second stays pending");
        clipboardBusy = true;
        Field<ModernButton>(consecutiveDialog.Handoff, "reported").PerformClick();
        Application.DoEvents();
        Check(first == "ReportedSent" && second == "Opened" && advancedLaunches == 1 && advancedMarks == 1
            && imageCopies == 1 && !Field<ModernButton>(consecutiveDialog.Handoff, "reported").Enabled,
            "Clipboard failure stops the next chat launch and cannot be reported as sent");
        clipboardBusy = false;
        Field<ModernButton>(consecutiveDialog.Handoff, "open").PerformClick(); Application.DoEvents();
        Check(first == "ReportedSent" && second == "Opened"
            && advancedLaunches == 2 && advancedMarks == 1 && imageCopies == 2,
            "Reporting the first chat opens the next without reporting it sent");
        consecutiveDialog.Close();

        int deniedCopies = 0;
        using var deniedImage = new CommunicationManualHandoffDialog(Guid.NewGuid(), consecutive with
        {
            Read = _ => Task.FromResult<IReadOnlyList<CommunicationManualRecipient>>(
                new[] { new CommunicationManualRecipient(1, "Ineligible child", "4567", "Pending") }),
            Open = _ => Task.FromResult(new CommunicationManualOpen(false, null, "ConsentRequired", null))
        }, openFirstChat: true, prepareImage: () => { deniedCopies++; return Task.CompletedTask; });
        deniedImage.Show(); Application.DoEvents();
        Check(deniedCopies == 0 && advancedLaunches == 2 && advancedMarks == 1,
            "A recipient rejected by the authoritative eligibility check never receives a clipboard image or chat launch");
        deniedImage.Close();
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
        bool fromFees = false;
        void Cancel(Control owner, int id, string display, bool fees) { opens++; captured = CommunicationRecordEntry.Target(id, fees); fromFees = fees; }
        using var admission = new StudentAdmissionPage(false);
        SetField(admission, "currentAdmissionId", 42);
        var name = Field<ModernTextBox>(admission, "name"); name.Text = "Unsubmitted admission edit";
        admission.OpenStudentMessage(Cancel);
        Check(opens == 1 && captured?.StudentId == 42 && captured.Audience == CommunicationAudience.Student
            && name.Text == "Unsubmitted admission edit", "Optional Admission messaging uses the saved student and cancel leaves unsaved details intact");
        using var fees = new FeeCollectionPage(false);
        SetField(fees, "admissionId", 42);
        var messageButton = fees.Controls.Find("feeCollectionMessage", true).Single();
        Check(messageButton.Parent is FlowLayoutPanel && messageButton.Parent == Field<ModernButton>(fees, "printBillButton").Parent,
            "The Fee Collection message action sits in the same header row as the bill actions");
        var reference = Field<ModernTextBox>(fees, "reference"); reference.Text = "Unsaved payment reference";
        var remarks = Field<ModernTextBox>(fees, "remarks"); remarks.Text = "Unsaved payment remarks";
        fees.OpenStudentMessage(Cancel);
        Check(opens == 2 && fromFees && captured?.StudentId == 42 && captured.Audience == CommunicationAudience.FeeStudent
            && reference.Text == "Unsaved payment reference" && remarks.Text == "Unsaved payment remarks",
            "Optional Collection messaging scopes current fees to the main student and never reloads or clears the payment draft");
        CurrentUser.SignIn(7204, "Viewer", "Read-only fixture", false, new[] { "Students.View", "Fees.View" });
        admission.OpenStudentMessage(Cancel); fees.OpenStudentMessage(Cancel);
        Check(opens == 2 && !CommunicationAccess.CanOpen(CurrentUser.HasPermission),
            "Opening either integration is independently refused after sender permission is removed");
    }

    private static void CheckRecordMessageDialog()
    {
        CurrentUser.SignIn(7201, "Synthetic sender", "Messaging fixture", false,
            new[] { "Communication.Send", "Students.View", "Fees.View" });
        CommunicationTarget? captured = null;
        int confirms = 0, dispatches = 0, renders = 0, copies = 0, documentRevision = 0;
        int launches = 0, reports = 0, warnings = 0;
        bool changeAtHandoff = false;
        string handoffState = "Pending";
        const int studentId = 42;
        var operations = new CommunicationRecordMessageDialog.Operations(
            option => Task.FromResult(option.Provider is null),
            () => Task.FromResult(CommunicationPreparedMessages.BuiltInChoices),
            target =>
            {
                captured = target;
                var student = new CommunicationPreviewEntry(studentId, "Synthetic student", "Student", "+923001234567",
                    "Included", 825m, "42", DateTime.Today);
                return Task.FromResult(new CommunicationPreview(new[] { student }));
            },
            (_, target, result, body, request, _) =>
            {
                Check(target == captured && result.Included == 1 && request != Guid.Empty && body.Length > 0,
                    "Record message confirmation receives the exact reviewed scope, eligible contacts and format");
                confirms++;
                handoffState = "Pending";
                return Task.FromResult(new CommunicationConfirmation(request, result.Included, false));
            },
            (_, _) => dispatches++,
            (id, family, receipt) =>
            {
                var data = new DataTable("FeeDocument");
                data.Columns.Add("ReceiptNumber");
                data.Columns.Add("PaymentDateText");
                data.Rows.Add(receipt ? "R-42" : "", documentRevision.ToString());
                var children = new List<CommunicationFeeDetails.Student>
                {
                    new(id, "Synthetic student", "42", "Eight - F", 825m)
                };
                if (family) children.Add(new(43, "Synthetic sibling", "43", "Nine - A", 425m));
                string details = receipt ? "" : CommunicationFeeDetails.Format(DateTime.Today, children,
                    family ? 1250m : 825m, family);
                return Task.FromResult(new FeeMessageDocument(data, id, new[] { id, 43 }, new[] { id }, family,
                    receipt, receipt ? 500m : family ? 1250m : 825m, receipt ? "R-42" : "", details));
            },
            _ => { renders++; return Task.FromResult(new Bitmap(10, 10)); },
            _ => copies++,
            new CommunicationManualHandoff.Operations(
                _ => Task.FromResult<IReadOnlyList<CommunicationManualRecipient>>(
                    new[] { new CommunicationManualRecipient(1, "Synthetic student", "4567", handoffState) }),
                (_, _) => Task.FromResult("Prepared fee message"),
                _ =>
                {
                    if (changeAtHandoff) documentRevision++;
                    handoffState = "Opened";
                    return Task.FromResult(new CommunicationManualOpen(true, new Uri("https://wa.me/923001234567?text=fixture"),
                        null, "Prepared fee message"));
                },
                (_, outcome) =>
                {
                    handoffState = outcome;
                    if (outcome == "ReportedSent") reports++;
                    return Task.CompletedTask;
                },
                _ => launches++, _ => warnings++));

        using (var admission = new CommunicationRecordMessageDialog(studentId, "Synthetic student", false, operations))
        {
            admission.Show(); Application.DoEvents();
            var action = Field<ModernButton>(admission, "action");
            var templates = Field<ModernComboBox>(admission, "templates");
            var scope = Field<ModernComboBox>(admission, "scope");
            var recordLanguage = Field<ModernComboBox>(admission, "language");
            var originalDraft = Field<ModernTextBox>(admission, "message");
            originalDraft.Text = "This record draft must not translate";
            recordLanguage.SelectedIndex = 1;
            Check(originalDraft.Text == "This record draft must not translate"
                && templates.Items.OfType<CommunicationPreparedMessages.ComposeTemplate>()
                    .All(item => item.LanguageCode == "ur"),
                "Admission context language switching preserves the draft and shows only independently approved Urdu formats.");
            recordLanguage.SelectedIndex = 0;
            var formats = templates.Items.OfType<CommunicationPreparedMessages.ComposeTemplate>().ToArray();
            Check(scope.Visible && scope.Items.Count == 2 && templates.Visible && action.Visible && action.Enabled
                && !Descendants(admission).OfType<CommunicationCenterPage>().Any()
                && confirms == 0 && dispatches == 0 && captured is null,
                "Admission opens a compact scoped composer without reviewing, confirming or dispatching");
            Check(formats.Length > 0 && formats.All(item => item.Purpose == "Individual" && !item.FamilyOnly),
                "The student scope offers only student message templates");
            templates.SelectedItem = formats.First(item => CommunicationPreparedMessages.HasFields(item.Body));
            scope.SelectedIndex = 1;
            formats = templates.Items.OfType<CommunicationPreparedMessages.ComposeTemplate>().ToArray();
            Check(formats.Any(item => item.FamilyOnly) && formats.All(item => !CommunicationPreparedMessages.HasFields(item.Body))
                && Field<ModernTextBox>(admission, "message").Text.Length == 0,
                "Switching to a linked family offers shared wording and clears the previous student's draft");
            templates.SelectedItem = formats[0];
            action.PerformClick(); Application.DoEvents();
            var recipients = Field<ModernComboBox>(admission, "recipients");
            Check(captured is { Audience: CommunicationAudience.Family, StudentId: studentId }
                && recipients.Visible && recipients.Items.Count == 1 && !scope.Visible && confirms == 0 && dispatches == 0,
                "Review resolves the selected family's single guardian number without sending");
            action.PerformClick(); Application.DoEvents();
            Check(confirms == 1 && dispatches == 1 && !admission.Visible, "Explicit confirmation dispatches the reviewed family message once");
        }

        using var fees = new CommunicationRecordMessageDialog(studentId, "Synthetic student", true, operations);
        fees.Show(); Application.DoEvents();
        var feeFormats = Field<ModernComboBox>(fees, "templates").Items.OfType<CommunicationPreparedMessages.ComposeTemplate>().ToArray();
        var feeScope = Field<ModernComboBox>(fees, "scope");
        var feeContent = Field<ModernComboBox>(fees, "feeContent");
        var feeLanguage = Field<ModernComboBox>(fees, "language");
        feeLanguage.SelectedIndex = 1;
        var urduFees = Field<ModernComboBox>(fees, "templates")
            .Items.OfType<CommunicationPreparedMessages.ComposeTemplate>().ToArray();
        Check(urduFees.Any(item => item.LanguageCode == "ur")
            && urduFees.All(item => item.LanguageCode == "ur"),
            "Fee context can offer a separately written Urdu fee notice without changing the ledger or sending.");
        feeLanguage.SelectedIndex = 0;
        Check(feeFormats.Length > 0 && feeFormats.All(item => item.Purpose == "FeeReminder") && feeScope.Items.Count == 2,
            "Fee Collection offers relevant fee templates and only the current student or linked family scopes");
        Field<ModernButton>(fees, "action").PerformClick(); Application.DoEvents();
        Check(captured is { Audience: CommunicationAudience.FeeStudent, StudentId: studentId }
            && new[] { "825.00", "Synthetic student", "Eight - F", "42" }
                .All(value => Field<ModernTextBox>(fees, "preview").Text.Contains(value)) && confirms == 1 && dispatches == 1,
            "The fee preview identifies the selected student, class and live balance without confirming a message");
        Field<ModernButton>(fees, "back").PerformClick();
        feeScope.SelectedIndex = 1;
        Field<ModernButton>(fees, "action").PerformClick(); Application.DoEvents();
        Check(captured is { Audience: CommunicationAudience.FeeFamily, StudentId: studentId }
            && new[] { "1,250.00", "825.00", "425.00", "Synthetic student", "Synthetic sibling", "Eight - F", "Nine - A" }
                .All(value => Field<ModernTextBox>(fees, "preview").Text.Contains(value)),
            "Family outstanding lists both children's own balances and classes before the total, using the same guardian scope");
        Field<ModernButton>(fees, "back").PerformClick();
        feeContent.SelectedIndex = 1;
        Field<ModernButton>(fees, "action").PerformClick(); Application.DoEvents();
        Check(Field<ModernTextBox>(fees, "preview").Text.Contains("R-42") && Field<ModernTextBox>(fees, "preview").Text.Contains("500.00")
            && confirms == 1 && dispatches == 1, "Payment preview uses the saved receipt number and amount without sending");
        Field<ModernButton>(fees, "back").PerformClick();
        feeScope.SelectedIndex = 0;
        Check(Field<CheckBox>(fees, "attachImage").Visible && Field<CheckBox>(fees, "attachImage").Enabled
            && Field<CheckBox>(fees, "attachImage").Checked,
            "Manual WhatsApp defaults to a pasteable receipt image without a file picker");
        Field<ModernButton>(fees, "action").PerformClick(); Application.DoEvents();
        Check(captured is { Audience: CommunicationAudience.FeeStudent, StudentId: studentId }
            && Field<ModernTextBox>(fees, "preview").Text.Contains("R-42"), "A student receipt uses the individual fee scope");
        fees.Size = fees.MinimumSize;
        Application.DoEvents();
        var viewport = Descendants(fees).OfType<ResponsiveViewport>().Single();
        Check(new[] { "back", "action", "cancel" }.Select(name => Field<ModernButton>(fees, name)).All(button =>
            button.Visible && viewport.RectangleToScreen(viewport.ClientRectangle)
                .Contains(button.RectangleToScreen(button.ClientRectangle))
                && button.Parent!.Parent!.RectangleToScreen(button.Parent.Parent.ClientRectangle)
                    .Contains(button.RectangleToScreen(button.ClientRectangle))),
            "Every fee review action remains fully visible at the minimum dialog size");
        Check(renders == 0 && copies == 0 && launches == 0, "Reviewing a fee message never prepares an attachment or launches the chat");
        Field<ModernButton>(fees, "action").PerformClick(); Application.DoEvents();
        var handoff = Descendants(fees).OfType<CommunicationManualHandoff>().Single();
        var report = Field<ModernButton>(handoff, "reported");
        Check(confirms == 2 && dispatches == 1 && renders == 1 && copies == 1 && launches == 1 && reports == 0
            && fees.Visible && handoff.Visible && report.Visible && report.Enabled && !Field<ModernButton>(fees, "action").Visible,
            "Confirmation copies the image, launches WhatsApp once and shows reporting in the same Fee Message dialog");
        Check(new[] { "open", "reported", "skip", "close" }.Select(name => Field<ModernButton>(handoff, name)).All(button =>
            button.Visible && viewport.RectangleToScreen(viewport.ClientRectangle)
                .Contains(button.RectangleToScreen(button.ClientRectangle))
                && button.Parent!.RectangleToScreen(button.Parent.ClientRectangle)
                    .Contains(button.RectangleToScreen(button.ClientRectangle))),
            "All WhatsApp follow-up actions fit in the Fee Message dialog at its minimum size");
        report.PerformClick(); Application.DoEvents();
        Check(reports == 1 && handoffState == "ReportedSent" && !fees.Visible && launches == 1,
            "Explicit reporting closes Fee Message without reopening WhatsApp");

        using (var changed = new CommunicationRecordMessageDialog(studentId, "Synthetic student", true, operations))
        {
            changed.Show(); Application.DoEvents();
            Field<ModernComboBox>(changed, "feeContent").SelectedIndex = 1;
            Field<ModernButton>(changed, "action").PerformClick(); Application.DoEvents();
            documentRevision++;
            Field<ModernButton>(changed, "action").PerformClick(); Application.DoEvents();
            Check(confirms == 2 && dispatches == 1 && renders == 1 && copies == 1 && changed.Visible,
                "A changed saved receipt blocks confirmation and clipboard access until it is reviewed again");
            changed.Close();
        }
        using (var failed = new CommunicationRecordMessageDialog(studentId, "Synthetic student", true, operations with
        {
            RenderImage = _ => throw new InvalidOperationException("Synthetic renderer failure")
        }))
        {
            failed.Show(); Application.DoEvents();
            Field<ModernButton>(failed, "action").PerformClick(); Application.DoEvents();
            Field<ModernButton>(failed, "action").PerformClick(); Application.DoEvents();
            Check(confirms == 2 && dispatches == 1 && copies == 1 && failed.Visible
                && Field<ModernButton>(failed, "action").Enabled,
                "Rendering failure is recoverable and never confirms or opens a messaging batch");
            failed.Close();
        }
        using (var textOnly = new CommunicationRecordMessageDialog(studentId, "Synthetic student", true, operations))
        {
            textOnly.Show(); Application.DoEvents();
            Field<CheckBox>(textOnly, "attachImage").Checked = false;
            Field<ModernButton>(textOnly, "action").PerformClick(); Application.DoEvents();
            Field<ModernButton>(textOnly, "action").PerformClick(); Application.DoEvents();
            Check(confirms == 3 && dispatches == 1 && launches == 2 && renders == 1 && copies == 1,
                "Text-only fee messaging remains optional and does not touch the clipboard");
            Field<ModernButton>(Descendants(textOnly).OfType<CommunicationManualHandoff>().Single(), "close").PerformClick();
            Application.DoEvents();
            Check(!textOnly.Visible && reports == 1 && handoffState == "Opened",
                "Finish Later returns to Fee Collection and leaves the opened message available to resume");
        }
        using (var cancelled = new CommunicationRecordMessageDialog(studentId, "Synthetic student", true, operations))
        {
            cancelled.Show(); Application.DoEvents();
            Field<ModernButton>(cancelled, "action").PerformClick(); Application.DoEvents();
            Field<ModernButton>(cancelled, "cancel").PerformClick(); Application.DoEvents();
            Check(confirms == 3 && dispatches == 1 && launches == 2 && renders == 1 && copies == 1,
                "Cancelling after fee review never confirms, renders, copies or dispatches");
        }
        using (var changedDuringRender = new CommunicationRecordMessageDialog(studentId, "Synthetic student", true, operations with
        {
            RenderImage = _ => { documentRevision++; return Task.FromResult(new Bitmap(10, 10)); }
        }))
        {
            changedDuringRender.Show(); Application.DoEvents();
            Field<ModernComboBox>(changedDuringRender, "feeContent").SelectedIndex = 1;
            Field<ModernButton>(changedDuringRender, "action").PerformClick(); Application.DoEvents();
            Field<ModernButton>(changedDuringRender, "action").PerformClick(); Application.DoEvents();
            Check(confirms == 3 && dispatches == 1 && copies == 1 && launches == 2 && changedDuringRender.Visible,
                "A receipt changed during rendering is rejected before the batch is confirmed");
            changedDuringRender.Close();
        }
        using (var changedDuringHandoff = new CommunicationRecordMessageDialog(studentId, "Synthetic student", true, operations))
        {
            changedDuringHandoff.Show(); Application.DoEvents();
            Field<ModernComboBox>(changedDuringHandoff, "feeContent").SelectedIndex = 1;
            Field<ModernButton>(changedDuringHandoff, "action").PerformClick(); Application.DoEvents();
            changeAtHandoff = true;
            Field<ModernButton>(changedDuringHandoff, "action").PerformClick(); Application.DoEvents();
            changeAtHandoff = false;
            var changedHandoff = Descendants(changedDuringHandoff).OfType<CommunicationManualHandoff>().Single();
            Check(confirms == 4 && copies == 1 && launches == 2 && warnings == 1 && changedDuringHandoff.Visible
                && !Field<ModernButton>(changedHandoff, "reported").Enabled,
                "A fee change during handoff blocks copying, opening the chat and reporting the stale receipt as sent");
            changedDuringHandoff.Close();
        }
        using (var sms = new CommunicationRecordMessageDialog(studentId, "Synthetic student", true, operations with
        {
            IsReady = _ => Task.FromResult(true)
        }))
        {
            sms.Show(); Application.DoEvents();
            var methods = Field<ModernComboBox>(sms, "method");
            methods.SelectedItem = methods.Items.OfType<CommunicationRecordMessageDialog.Method>().Single(item => item.Channel == "Sms");
            Check(!Field<CheckBox>(sms, "attachImage").Visible, "SMS never offers a manual WhatsApp image attachment");
            int previousRenders = renders;
            Field<ModernButton>(sms, "action").PerformClick(); Application.DoEvents();
            Field<ModernButton>(sms, "action").PerformClick(); Application.DoEvents();
            Check(confirms == 5 && dispatches == 2 && launches == 2 && renders == previousRenders && copies == 1,
                "API messaging keeps its existing confirmation path without image rendering or clipboard access");
        }
        using (var staleText = new CommunicationRecordMessageDialog(studentId, "Synthetic student", true, operations))
        {
            changeAtHandoff = true;
            int priorLaunches = launches, priorCopies = copies, priorWarnings = warnings;
            staleText.Show(); Application.DoEvents();
            Field<ModernComboBox>(staleText, "feeContent").SelectedIndex = 1;
            Field<CheckBox>(staleText, "attachImage").Checked = false;
            Field<ModernButton>(staleText, "action").PerformClick(); Application.DoEvents();
            Field<ModernButton>(staleText, "action").PerformClick(); Application.DoEvents();
            var textHandoff = Descendants(staleText).OfType<CommunicationManualHandoff>().Single();
            Check(launches == priorLaunches && copies == priorCopies && warnings > priorWarnings
                && !Field<ModernButton>(textHandoff, "reported").Enabled,
                "Text-only fee messages also reject changed financial data before launching or allowing a sent report");
            staleText.Close();
            changeAtHandoff = false;
        }
    }
}
