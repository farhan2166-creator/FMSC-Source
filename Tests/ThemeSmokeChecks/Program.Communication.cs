using System.Reflection;
using FMSC_SMS.Core;
using FMSC_SMS.Pages.Communication;
using FMSC_SMS.Pages.Security;

internal static partial class Program
{
    // Synthetic, SQL-free regression checks: admission consent is final and
    // campus/class targets never require selecting each child individually.
    private static void CheckCommunicationWorkflow()
    {
        var previous = (CurrentUser.UserId, CurrentUser.Username, CurrentUser.RoleName,
            CurrentUser.MustChangePassword, CurrentUser.GetPermissions().ToArray());
        try
        {
            CurrentUser.SignIn(7204, "Communication UI fixture", "Administrator", false, Array.Empty<string>());
            CheckCommunicationLayoutAndDomainRules();
        }
        finally
        {
            CurrentUser.SignIn(previous.UserId, previous.Username, previous.RoleName,
                previous.MustChangePassword, previous.Item5);
        }
    }

    private static void CheckCommunicationLayoutAndDomainRules()
    {
        var assembly = typeof(StudentPicker).Assembly;
        var resolver = assembly.GetType("FMSC_SMS.Core.CommunicationRecipientResolver")!;
        var normalize = resolver.GetMethod("NormalizeMobile", BindingFlags.NonPublic | BindingFlags.Static)!;
        string? Phone(string number) => (string?)normalize.Invoke(null, new object?[] { number });
        Check(Phone("03001234567") == "+923001234567", "Pakistani mobile numbers normalize to a single E164 destination");
        Check(Phone("+923001234567") == "+923001234567", "Stored international mobile numbers are recognized");
        Check(Phone("00923001234567") == "+923001234567", "International dialing prefix is recognized");
        Check(Phone("02134567890") is null, "Landlines must not become SMS/WhatsApp destinations");

        var candidates = new DataTable();
        candidates.Columns.Add("RecordId", typeof(int));
        candidates.Columns.Add("RecordName", typeof(string));
        candidates.Columns.Add("Kind", typeof(string));
        candidates.Columns.Add("ContactNumber", typeof(string));
        candidates.Columns.Add("SchoolMessageConsent", typeof(bool));
        candidates.Columns.Add("Outstanding", typeof(decimal));
        void Add(int id, string name, string number, bool? consent)
            => candidates.Rows.Add(id, name, "Student", number, consent.HasValue ? (object)consent.Value : DBNull.Value, DBNull.Value);

        Add(1, "Consenting student", "03001234567", true);
        Add(2, "Consenting sibling", "+923001234567", true);
        Add(3, "Declined", "03011234567", false);
        Add(4, "Unrecorded", "03021234567", null);
        Add(5, "Invalid mobile", "02134567890", true);
        Add(6, "Opted out", "03031234567", true);
        Add(7, "Another consenting student", "03041234567", true);
        var suppressed = new HashSet<string>(StringComparer.Ordinal) { "+923031234567" };
        var method = resolver.GetMethod("BuildPreview", BindingFlags.NonPublic | BindingFlags.Static)!;
        object preview = method.Invoke(null, new object[] { candidates, suppressed, false })!;
        int Get(string key) => (int)preview.GetType()
            .GetProperty(key, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(preview)!;

        Check(Get("Included") == 2, "Whole-school consent Yes targets are automatic and shared phones deduplicate");
        Check(Get("Declined") == 1 && Get("NotRecorded") == 1,
            "Admission No and Not Recorded are excluded without separate consent prompts");
        Check(Get("InvalidNumber") == 1 && Get("Suppressed") == 1 && Get("Duplicates") == 1,
            "Invalid phones, channel opt-outs and repeated sibling destinations are excluded");
        Check(Get("Excluded") == 5, "Preview explains all excluded school records");

        var vault = assembly.GetType("FMSC_SMS.Core.CommunicationProviderCredentialStore")!;
        var credential = vault.GetNestedType("Credential", BindingFlags.NonPublic)!;
        var validate = vault.GetMethod("Validate", BindingFlags.NonPublic | BindingFlags.Static)!;
        var valid = Activator.CreateInstance(credential, 1, "Sms", "Adapter-Test", "account", "sender",
            "synthetic-token-not-real", "synthetic-callback-signature-key")!;
        validate.Invoke(null, new[] { valid });
        var wrongChannel = Activator.CreateInstance(credential, 1, "../Sms", "Adapter-Test", "account", "sender",
            "synthetic-token-not-real", "synthetic-callback-signature-key")!;
        bool refusedUnsafeChannel = false;
        try { validate.Invoke(null, new[] { wrongChannel }); }
        catch (TargetInvocationException ex) when (ex.InnerException is ArgumentException)
        { refusedUnsafeChannel = true; }
        Check(refusedUnsafeChannel, "Protected provider credentials reject unsupported channels and path traversal");

        var twilio = assembly.GetType("FMSC_SMS.Core.CommunicationTwilioTransport")!;
        var status = twilio.GetMethod("MapStatus", BindingFlags.NonPublic | BindingFlags.Static)!;
        string Map(string value) => (string)status.Invoke(null, new object?[] { value })!;
        Check(Map("queued") == "Accepted" && Map("sent") == "Accepted"
            && Map("delivered") == "Delivered" && Map("undelivered") == "Failed"
            && Map("nonsense") == "Unknown",
            "Provider states distinguish acceptance, verified delivery and definitive failures");
        var verifySignature = twilio.GetMethod("VerifySignedForm", BindingFlags.NonPublic | BindingFlags.Static)!;
        var url = new Uri("https://example.invalid/webhooks/twilio?source=school");
        var fields = new Dictionary<string, string>
        {
            ["AccountSid"] = "AC" + new string('a', 32),
            ["MessageSid"] = "SM" + new string('b', 32),
            ["MessageStatus"] = "delivered"
        };
        string secret = "synthetic-auth-token-not-real";
        string payload = url.AbsoluteUri + string.Concat(fields.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Key + kv.Value));
        using var hmac = new System.Security.Cryptography.HMACSHA1(System.Text.Encoding.UTF8.GetBytes(secret));
        string header = Convert.ToBase64String(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(payload)));
        bool Verified(Uri u, string sign) => (bool)verifySignature.Invoke(null,
            new object?[] { u, fields, sign, secret })!;
        Check(Verified(url, header), "Verified provider callback accepts correct HMAC over original HTTPS URL and all form fields");
        Check(!Verified(url, "wrong") && !Verified(new Uri("http://example.invalid/webhooks/twilio?source=school"), header),
            "Forged callback signatures or non-HTTPS endpoints are rejected");
        fields["MessageStatus"] = "failed";
        Check(!Verified(url, header), "Callback body modification fails signed-payload verification");

        var registry = assembly.GetType("FMSC_SMS.Core.CommunicationTransportRegistry")!;
        var planned = registry.GetMethod("IsPlannedProfile", BindingFlags.NonPublic | BindingFlags.Static)!;
        bool Planned(string channel, string provider) => (bool)planned.Invoke(null, new object[] { channel, provider })!;
        Check(Planned("WhatsApp", "MetaCloud") && Planned("Email", "Smtp")
            && Planned("WhatsApp", "Twilio") && Planned("WhatsApp", "WhatsAppBusinessManual")
            && Planned("Email", "EmailClientManual") && !Planned("Sms", "Smtp")
            && !Planned("Email", "Twilio") && !Planned("Sms", "WhatsAppBusinessManual"),
            "Provider-neutral registry accepts only reviewed channel/provider and free-manual pairings.");
        var adapterSelector = registry.GetMethod("ResolveForDispatch", BindingFlags.NonPublic | BindingFlags.Static)!;
        bool NoLiveAdapter(string channel, string provider)
        {
            try { adapterSelector.Invoke(null, new object[] { channel, provider }); return false; }
            catch (TargetInvocationException ex) when (ex.InnerException is NotSupportedException or InvalidOperationException or UnauthorizedAccessException)
            { return true; }
        }
        Check(NoLiveAdapter("WhatsApp", "MetaCloud") && NoLiveAdapter("Email", "Smtp")
            && NoLiveAdapter("WhatsApp", "WhatsAppBusinessManual")
            && NoLiveAdapter("Email", "EmailClientManual")
            && NoLiveAdapter("Sms", "Smtp"),
            "Manual handoff, unavailable transports and unconfigured Meta reject dispatch before any claim or network request.");

        var manualStore = assembly.GetType("FMSC_SMS.Core.CommunicationManualHandoffStore")!;
        var click = manualStore.GetMethod("BuildClickToChatLink",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var chat = (Uri)click.Invoke(null,new object[] { "+923001234567", "School notice & fee #1\nReady?" })!;
        Check(chat.Scheme == "https" && chat.Host == "wa.me"
            && chat.AbsolutePath == "/923001234567"
            && chat.Query.Contains("School%20notice",StringComparison.Ordinal)
            && chat.Query.Contains("%26",StringComparison.Ordinal)
            && Uri.UnescapeDataString(chat.Query).Contains("fee #1\nReady?",StringComparison.Ordinal),
            "Free WhatsApp handoff generates only an official digits-only, URI-encoded click-to-chat link.");
        bool invalidManualDestination = false;
        try { click.Invoke(null,new object[] { "+9212345", "Hello" }); }
        catch(TargetInvocationException ex) when (ex.InnerException is ArgumentException)
        { invalidManualDestination = true; }
        Check(invalidManualDestination,
            "Manual WhatsApp cannot open a malformed or non-Pakistan-mobile destination.");
        var manualDialogType = assembly.GetType("FMSC_SMS.Pages.Communication.CommunicationManualHandoffDialog")!;
        Check(manualDialogType is not null,
            "Operator-controlled WhatsApp session can be resumed with a dedicated native dialog.");

        var type = typeof(CommunicationCenterPage);
        // A SQL-free layout contract must not open the live school database
        // when navigating Queue. Session records are provided by a deliberately
        // empty synthetic reader, while navigation and filtering remain real.
        using var page = new CommunicationCenterPage(false,
            new CommunicationCenterPage.Operations(
                _ => throw new InvalidOperationException("No SQL in UI contract"),
                () => Task.FromResult(true),
                (_, _, _, _) => throw new InvalidOperationException("No sends in UI contract"),
                (_, _) => throw new InvalidOperationException("No chat in UI contract"),
                () => false,
                _ =>
                {
                    var rows = new DataTable();
                    rows.Columns.Add("BatchId", typeof(Guid));
                    rows.Columns.Add("Channel", typeof(string));
                    rows.Columns.Add("Audience", typeof(string));
                    rows.Columns.Add("CreatedUtc", typeof(DateTime));
                    rows.Columns.Add("Method", typeof(string));
                    return Task.FromResult(rows);
                }));
        var audience = (ModernComboBox)type.GetField("audience", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
        var audienceScopes = audience.Items.Cast<object>()
            .Select(item => (CommunicationAudience)item.GetType().GetProperty("Scope")!.GetValue(item)!)
            .ToArray();
        var expectedScopes = new[] {
            CommunicationAudience.Class,CommunicationAudience.Campus,CommunicationAudience.School,
            CommunicationAudience.Student,CommunicationAudience.Family,CommunicationAudience.Staff,
            CommunicationAudience.OutstandingFees,CommunicationAudience.FeeStudent,
            CommunicationAudience.FeeFamily
        };
        Check(page.SelectedAudience == CommunicationAudience.Class
            && expectedScopes.All(audienceScopes.Contains)
            && audienceScopes.Distinct().Count() == audienceScopes.Length,
            "New communication starts with a class and supports every configured recipient audience without a fixed list length");
        var send = (ModernButton)type.GetField("confirm", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
        var templates = (ModernComboBox)type.GetField("templateChoice",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
        Check(templates.Items.Count >= 2
            && templates.Items.Cast<object>().Any(item => item.ToString() == "School announcement"),
            "Composition offers a built-in prepared notice without entering Settings");
        var defaultTarget = (CommunicationTarget)type.GetMethod("CurrentTarget",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, null)!;
        Check(defaultTarget.Channel == "WhatsApp", "Composition uses the available manual WhatsApp channel");
        // Discover the controls the user interacts with, not private card-list
        // indices, a particular number of cards, or template-action row numbers.
        var targetCard = Descendants(page).OfType<HumanCard>()
            .Single(card => Descendants(card).Contains(audience));
        Check(!send.Enabled, "An unreviewed page cannot send or open a chat before preview and confirmation");

        using var host = new Form { ClientSize = new Size(1200, 780), ShowInTaskbar = false };
        host.Controls.Add(page);
        host.Show();
        Application.DoEvents();
        var navigation = new[] { "communicationNewMessage", "communicationUnfinished", "communicationHistory",
            "communicationTemplates", "communicationConfigureSending" }
            .Select(name => Descendants(page).Single(control => control.Name == name)).ToArray();
        Check(navigation.All(button => button.Visible)
            && navigation[2].PointToScreen(Point.Empty).X < navigation[3].PointToScreen(Point.Empty).X,
            "Daily tabs and separate administration actions are visible in the navigation");
        bool ButtonsFit() => navigation.All(button =>
        {
            var parent = button.Parent;
            return parent is not null
                && parent.RectangleToScreen(parent.ClientRectangle)
                    .Contains(button.RectangleToScreen(button.ClientRectangle));
        });
        Check(ButtonsFit(), "All communication actions are fully visible, not clipped by the navigation container");
        Check(navigation.Select(button => button.Top).Distinct().Count() == 1
            && navigation.Select(button => button.Height).Distinct().Count() == 1,
            "Communication actions share a top edge and height when they fit on one row");
        host.ClientSize = new Size(670, 780);
        Application.DoEvents();
        Check(ButtonsFit(), "Communication actions wrap without clipping on a narrower page. " +
            $"Host={host.ClientSize}, page={page.ClientSize}; " +
            string.Join("; ", navigation.Select(button =>
                $"{button.Name}: button={button.Bounds}, panel={button.Parent?.ClientRectangle}")));
        host.ClientSize = new Size(1200, 780);
        Application.DoEvents();
        var liveDraft = (ModernTextBox)Descendants(page)
            .Single(control => control.Name == "communicationDraftPreview");
        Check(targetCard.Visible && liveDraft.Visible && liveDraft.ReadOnly,
            "New Message opens on the composer and shows a read-only live draft view");
        var draftEditor = (ModernTextBox)type.GetField("message",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
        draftEditor.Text = "FMSC draft preview fixture";
        Check(liveDraft.Text == draftEditor.Text && !send.Enabled,
            "Typing updates the live preview but never authorizes sending without review");
        draftEditor.Clear();
        ((ModernButton)navigation[1]).PerformClick();
        Application.DoEvents();
        Check(Descendants(page).Single(control => control.Name == "communicationUnfinishedGrid").Visible,
            "Queue navigation opens the dedicated unfinished-batch list");
        var unfinishedSearch = page.Controls.Find("communicationUnfinishedSearch", true).Single();
        int buttonBottom = navigation.Max(button => button.RectangleToScreen(button.ClientRectangle).Bottom);
        int gap = unfinishedSearch.PointToScreen(Point.Empty).Y - buttonBottom;
        Check(gap >= 0 && gap < navigation[0].Height,
            $"The queue search starts directly below navigation (gap: {gap}px).");
        ((ModernButton)navigation[0]).PerformClick();
        Application.DoEvents();
        var workspace = (TableLayoutPanel)type.GetField("composerLayout",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
        Check(workspace.Visible && targetCard.Visible
            && workspace.Controls.Contains(targetCard)
            && workspace.ColumnStyles[0].Width == 100,
            "New Message uses the whole workspace rather than a short form beside an empty preview");
        var reviewAction = Descendants(targetCard).Single(control => control.Name == "communicationReviewMessage");
        Check(reviewAction.Visible && reviewAction.RectangleToScreen(reviewAction.ClientRectangle).Bottom
            <= targetCard.Content.RectangleToScreen(targetCard.Content.ClientRectangle).Bottom,
            "The review action is reachable inside the composer");
        var setStep = type.GetMethod("SetStep", BindingFlags.Instance | BindingFlags.NonPublic)!;
        setStep.Invoke(page, new object[] { 1 });
        Application.DoEvents();
        var review = Descendants(page).OfType<HumanCard>()
            .Single(card => Descendants(card).OfType<DataGridView>().Any(grid =>
                ReferenceEquals(grid, type.GetField("previewGrid",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page))));
        Check(workspace.Visible && targetCard.Visible && review.Visible
            && workspace.Controls.Contains(review)
            && workspace.ColumnStyles[1].Width > 0,
            "Review appears beside the editable message, without replacing the composer");
        setStep.Invoke(page, new object[] { 0 });
        Application.DoEvents();
        // Search across every saved recipient (not only the group's display
        // label); exports must respect the filtered rows and neutralize formulas.
        var historyGridForSearch = (DataGridView)type.GetField("historyGrid",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
        var historyFixture = new DataTable();
        historyFixture.Columns.Add("CreatedUtc", typeof(DateTime));
        foreach (string column in new[] { "MethodText", "RecipientText", "RecipientSearchText",
            "AudienceText", "OperatorName", "StateText" })
            historyFixture.Columns.Add(column, typeof(string));
        historyFixture.Rows.Add(new DateTime(2026,11,16), "WhatsApp · manual",
            "=HYPERLINK(\"https://example.com\")", "Ayesha 352 guardian Shama",
            "Class", "Admin", "Sent, \"confirmed\"");
        historyFixture.Rows.Add(new DateTime(2026,11,15), "SMS · Twilio",
            "Other student", "Bilal 900 guardian Salma", "Class", "Admin", "Accepted");
        historyGridForSearch.DataSource = historyFixture;
        var filterHistory = type.GetMethod("FilterSessions",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        filterHistory.Invoke(page, new object[] { historyGridForSearch, "Shama 352", false });
        Check(historyFixture.DefaultView.Count == 1,
            "History searches multiple terms across saved group member and guardian identities.");
        var csvFormatter = type.GetMethod("FormatHistoryCsv",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        string historyCsv = (string)csvFormatter.Invoke(null,
            new object[] { historyFixture.DefaultView })!;
        Check(historyCsv.Contains("'=HYPERLINK", StringComparison.Ordinal)
            && historyCsv.Contains("Sent, \"\"confirmed\"\"", StringComparison.Ordinal)
            && !historyCsv.Contains("Bilal", StringComparison.Ordinal)
            && !historyCsv.Contains("https://wa.me", StringComparison.Ordinal),
            "Filtered history CSV quotes content, neutralizes formulas and excludes nonmatching recipients.");

        // The production formatter is internal: exercise it through reflection
        // without weakening accessibility or exposing student data to tests.
        var prepared = assembly.GetType("FMSC_SMS.Core.CommunicationPreparedMessages")!;
        var builtinArray = ((System.Collections.IEnumerable)prepared.GetProperty(
            "BuiltIns", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!)
            .Cast<object>().ToArray();
        string TemplateProperty(object obj, string property) =>
            Convert.ToString(obj.GetType().GetProperty(property)!.GetValue(obj)) ?? "";
        object FeeTemplate() => builtinArray.First(t => TemplateProperty(t, "Purpose") == "FeeReminder");
        object AdmissionTemplate() => builtinArray.First(t => TemplateProperty(t, "Name") == "New admission welcome");
        Check(builtinArray.Any(t => TemplateProperty(t, "Purpose") == "FeeReminder")
            && builtinArray.Any(t => TemplateProperty(t, "Name") == "New admission welcome"),
            "Prebuilt fee reminder and student-admission formats are available");
        var builtinChoices = ((System.Collections.IEnumerable)prepared.GetProperty(
            "BuiltInChoices", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!)
            .Cast<object>().ToArray();
        var urduChoices = builtinChoices.Where(item =>
            TemplateProperty(item, "Name").EndsWith("(Urdu)", StringComparison.Ordinal)).ToArray();
        Check(urduChoices.Length >= 5 && urduChoices.All(item =>
            TemplateProperty(item, "Channel") == "WhatsApp")
            && builtinChoices.Any(item => TemplateProperty(item, "Name") == "New admission welcome"
                && TemplateProperty(item, "Channel").Length == 0),
            "Urdu prepared formats are explicitly selectable only on free WhatsApp, never paid SMS.");
        var smsChargeDisclosure = type.GetMethod("SmsChargeDisclosure",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var unicodeStudent = new CommunicationPreviewEntry(23, "عائشہ", "Student",
            "+923001234567", "Included", null, "F-0023");
        var smsAudience = new CommunicationTarget(CommunicationAudience.Student,0,0,23,0,"Sms");
        var waAudience = smsAudience with { Channel = "WhatsApp" };
        var unicodePreview = new CommunicationPreview(new[] { unicodeStudent });
        string smsNotice = (string)smsChargeDisclosure.Invoke(null,
            new object[] { smsAudience, unicodePreview, "Dear [StudentName]" })!;
        string waNotice = (string)smsChargeDisclosure.Invoke(null,
            new object[] { waAudience, unicodePreview, "Dear [StudentName]" })!;
        Check(smsNotice.Contains("Unicode", StringComparison.Ordinal)
            && smsNotice.Contains("billed", StringComparison.Ordinal)
            && !waNotice.Contains("Unicode", StringComparison.Ordinal),
            "SMS confirmation warns about Unicode even when it comes only from a personalized student name.");

        var valuesType = prepared.GetNestedType("Values", BindingFlags.NonPublic)!;
        object Details(decimal? amount) => Activator.CreateInstance(valuesType,
            23, "Sample Student", "F-0023", new DateTime(2026, 11, 9), amount)!;
        var render = prepared.GetMethod("Render", BindingFlags.Static | BindingFlags.NonPublic)!;
        string RenderFormat(string format, object data) =>
            (string)render.Invoke(null, new[] { format, data })!;
        string feeText = RenderFormat(TemplateProperty(FeeTemplate(), "Body"), Details(1250.50m));
        string admissionText = RenderFormat(TemplateProperty(AdmissionTemplate(), "Body"), Details(1250.50m));
        string urduFeeText = RenderFormat(TemplateProperty(builtinArray.First(item =>
            TemplateProperty(item, "Name") == "Outstanding fee reminder (Urdu)"), "Body"), Details(1250.50m));
        Check(urduFeeText.Contains("بقایا", StringComparison.Ordinal)
            && urduFeeText.Contains("1,250.50", StringComparison.Ordinal),
            "Urdu fee draft renders the same guarded live financial placeholder values as English.");
        Check(feeText.Contains("Sample Student") && feeText.Contains("1,250.50")
            && feeText.Contains("F-0023") && !feeText.Contains("[OutstandingAmount]"),
            "Fee reminders use real per-recipient balance and admission identifiers");
        // One guardian phone: two approved sibling balances must appear in
        // the ONE manually opened message, not silently report just the first.
        var feeStudentType = prepared.GetNestedType("FeeStudent", BindingFlags.NonPublic)!;
        object FeeStudent(int id, string name, string admission, decimal balance, bool linked) =>
            Activator.CreateInstance(feeStudentType, id, name, admission, balance, linked)!;
        var array = Array.CreateInstance(feeStudentType, 2);
        array.SetValue(FeeStudent(23, "Sample Student", "F-0023", 500m, true), 0);
        array.SetValue(FeeStudent(24, "Sibling Student", "F-0024", 700m, true), 1);
        var aggregate = prepared.GetMethod("AggregateFeeReminder", BindingFlags.NonPublic | BindingFlags.Static)!;
        object consolidated = aggregate.Invoke(null, new object[]{ Details(500m), array })!;
        string combined = RenderFormat(TemplateProperty(FeeTemplate(), "Body"), consolidated);
        Check(combined.Contains("Sample Student") && combined.Contains("Sibling Student")
            && combined.Contains("F-0023") && combined.Contains("F-0024")
            && combined.Contains("1,200.00") && !combined.Contains("500.00"),
            "One guardian chat includes BOTH linked siblings and their combined current outstanding fee.");
        array.SetValue(FeeStudent(24, "Unrelated Student", "F-0024", 700m, false), 1);
        bool unrelatedRefused = false;
        try { aggregate.Invoke(null, new object[]{ Details(500m), array }); }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException)
        { unrelatedRefused = true; }
        Check(unrelatedRefused,
            "Shared phone without proven family relationship cannot disclose unrelated student debt.");
        array.SetValue(FeeStudent(24, "Sibling Student", "F-0024", 0m, true), 1);
        bool settledRefused = false;
        try { aggregate.Invoke(null, new object[]{ Details(500m), array }); }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException)
        { settledRefused = true; }
        Check(settledRefused, "Changed or settled sibling fee records cannot be silently aggregated.");
        Check(admissionText.Contains("09 Nov 2026") && admissionText.Contains("F-0023"),
            "Admission welcomes use the saved student admission date and number");
        bool rejectedMissingBalance = false;
        try { RenderFormat(TemplateProperty(FeeTemplate(), "Body"), Details(null)); }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException)
        { rejectedMissingBalance = true; }
        Check(rejectedMissingBalance, "Fee reminder refuses missing or settled balance rather than displaying a fake amount");
        bool unknownFieldRejected = false;
        try { RenderFormat("Hello [PrivateSqlField]", Details(1250.50m)); }
        catch (TargetInvocationException ex) when (ex.InnerException is CommunicationValidationException error)
        { unknownFieldRejected = DbMessage.UserMessage(error).Contains("[PrivateSqlField]"); }
        Check(unknownFieldRejected, "Prepared messages reject unknown record fields");
        var audienceField = audience.Parent!;
        Rectangle visibleInput = Rectangle.Intersect(
            audience.RectangleToScreen(audience.ClientRectangle),
            targetCard.Content.RectangleToScreen(targetCard.Content.ClientRectangle));
        Check(targetCard.Visible && audience.Visible && audienceField.Visible
            && audience.Width > 100 && audience.Height > 15
            && visibleInput.Width > 100 && visibleInput.Height > 15,
            "Recipients page visibly renders the Send to selector below its header at dashboard size");
        var campusPanel = (Control)type.GetField("campusField", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
        var classPanel = (Control)type.GetField("classField", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
        var studentPanel = (Control)type.GetField("studentField", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
        var staffPanel = (Control)type.GetField("staffField", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
        page.SelectedAudience = CommunicationAudience.School;
        Application.DoEvents();
        Check(!campusPanel.Visible && !classPanel.Visible && !studentPanel.Visible && !staffPanel.Visible,
            "Entire-school audience needs no extra pickers");
        void ChooseAudience(CommunicationAudience choice)
        {
            page.SelectedAudience = choice;
            Application.DoEvents();
            var selected = (CommunicationTarget)type.GetMethod("CurrentTarget",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, null)!;
            Check(selected.Audience == choice, "Audience choice maps to its intended target: " + choice);
        }
        bool Precedes(Control first, Control second)
        {
            Point a = first.PointToScreen(Point.Empty), b = second.PointToScreen(Point.Empty);
            return a.Y < b.Y || a.Y == b.Y && a.X < b.X;
        }
        var sendingMethod = Field<ModernComboBox>(page, "deliveryMethod");
        var body = Field<ModernTextBox>(page, "message");
        ChooseAudience(CommunicationAudience.Campus);
        Check(campusPanel.Visible && !classPanel.Visible && Precedes(audienceField, campusPanel)
            && Precedes(campusPanel, sendingMethod.Parent!) && Precedes(sendingMethod.Parent!, templates.Parent!)
            && Precedes(templates.Parent!, body.Parent!) && Precedes(body.Parent!, reviewAction),
            "Campus composition follows recipient, campus, method, template, message and review order");
        ChooseAudience(CommunicationAudience.Class);
        var campus = Field<LookupBox>(page, "campus");
        var classes = Field<LookupBox>(page, "cls");
        using var campuses = new DataTable();
        campuses.Columns.Add("CampusId", typeof(int)); campuses.Columns.Add("CampusName");
        campuses.Rows.Add(42, "Synthetic campus");
        campus.Bind(campuses, "CampusId", "CampusName");
        Check(campusPanel.Visible && classPanel.Visible && Precedes(campusPanel, classPanel) && !classes.Enabled,
            "The Class scope shows campus first and waits for a campus before enabling class selection");
        campus.SelectedValue = 42;
        Check(classes.Enabled, "Selecting a campus enables class selection");
        ChooseAudience(CommunicationAudience.OutstandingFees);
        Check(campus.SelectedId == 0 && classes.Enabled && studentPanel.Visible && Precedes(classPanel, studentPanel),
            "Outstanding fees starts with optional campus, class and student filters without inheriting the previous scope");
        host.ClientSize = new Size(500, 740);
        Application.DoEvents();
        Check(Precedes(audienceField, campusPanel) && Precedes(campusPanel, classPanel) && Precedes(classPanel, studentPanel)
            && Precedes(studentPanel, sendingMethod.Parent!) && Precedes(sendingMethod.Parent!, templates.Parent!)
            && new Control[] { audience, campus, classes, sendingMethod, templates, body, reviewAction }.All(control =>
            {
                var bounds = control.RectangleToScreen(control.ClientRectangle);
                var content = targetCard.Content.RectangleToScreen(targetCard.Content.ClientRectangle);
                return bounds.Width > 0 && bounds.Left >= content.Left && bounds.Right <= content.Right;
            }), "A narrow composer wraps the fields in order and keeps the inputs and Review action within its width");
        var composeScroll = targetCard.Content.Controls.OfType<Panel>().Single();
        composeScroll.ScrollControlIntoView(reviewAction);
        Application.DoEvents();
        Check(composeScroll.RectangleToScreen(composeScroll.ClientRectangle)
            .Contains(reviewAction.RectangleToScreen(reviewAction.ClientRectangle)),
            "The Review action can be scrolled fully into view on a narrow composer");
        composeScroll.AutoScrollPosition = Point.Empty;
        host.ClientSize = new Size(1200, 780);
        Application.DoEvents();
        ChooseAudience(CommunicationAudience.Student);
        Check(studentPanel.Visible && !campusPanel.Visible && !classPanel.Visible,
            "Individual audience displays the existing student picker");
        ChooseAudience(CommunicationAudience.Staff);
        Check(staffPanel.Visible && !studentPanel.Visible,
            "Staff audience displays the existing staff picker");
        ChooseAudience(CommunicationAudience.School);
        Check(audience.Visible && !campusPanel.Visible && !classPanel.Visible
            && !studentPanel.Visible && !staffPanel.Visible,
            "Switching back to entire school keeps the selector visible and collapses optional fields");
        Check(Descendants(targetCard).Contains(templates)
            && Descendants(targetCard).OfType<ModernTextBox>().Any(),
            "Audience, prepared templates and message editing are reachable in the composition step");
        using (var events = new CommunicationAutomationInboxDialog(
            () => Task.FromResult<IReadOnlyList<CommunicationAutomationInboxStore.Suggestion>>(
                new[] { new CommunicationAutomationInboxStore.Suggestion(
                    91,"Admission",101,101,"Synthetic admissions student","FMSC-101",DateTime.UtcNow) }),
            _ => Task.CompletedTask))
        {
            events.Show(host);
            Application.DoEvents();
            var eventGrid=(DataGridView)events.Controls.Find("communicationAutomationInbox",true).Single();
            var prepare=(ModernButton)events.Controls.Find("communicationPrepareEvent",true).Single();
            Check(eventGrid.Rows.Count==1 && prepare.Enabled,
                "School event suggestions show an actionable, permission-independent synthetic preview.");
            prepare.PerformClick();
            Application.DoEvents();
            Check(events.SelectedSuggestion?.CandidateId==91,
                "Preparing an event only returns its source identity to the normal composer.");
            events.Close();
        }
        host.Close();
    }
}
