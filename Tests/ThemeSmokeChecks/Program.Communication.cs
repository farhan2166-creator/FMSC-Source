using System.Reflection;
using FMSC_SMS.Core;

internal static partial class Program
{
    // Synthetic, SQL-free regression checks: admission consent is final and
    // campus/class targets never require selecting each child individually.
    private static void CheckCommunicationWorkflow()
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
            catch (TargetInvocationException ex) when (ex.InnerException is NotSupportedException or InvalidOperationException)
            { return true; }
        }
        Check(NoLiveAdapter("WhatsApp", "MetaCloud") && NoLiveAdapter("Email", "Smtp")
            && NoLiveAdapter("WhatsApp", "WhatsAppBusinessManual")
            && NoLiveAdapter("Email", "EmailClientManual")
            && NoLiveAdapter("Sms", "Smtp"),
            "Manual handoff and unimplemented transports reject API dispatch before any claim or network request.");

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

        var type = assembly.GetType("FMSC_SMS.Pages.Communication.CommunicationCenterPage")!;
        using var page = (Control)Activator.CreateInstance(type,
            BindingFlags.NonPublic | BindingFlags.Instance, null, new object[] { false }, null)!;
        var audience = (ModernComboBox)type.GetField("audience", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
        var audienceOptions = audience.Items.Cast<object>().Select(item => item.ToString()).ToArray();
        Check(audience.SelectedItem?.ToString() == "Entire school"
            && audienceOptions.Contains("Campus") && audienceOptions.Contains("Class")
            && audienceOptions.Distinct().Count() == audienceOptions.Length,
            "Communication defaults to whole-school audience and offers distinct bulk campus/class targets");
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
        var manualActivation = (ModernButton)type.GetField("enableFreeWhatsAppButton", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
        var settingsCard = Descendants(page).OfType<HumanCard>()
            .Single(card => Descendants(card).Contains(manualActivation));
        var settingsActions = Descendants(settingsCard).OfType<ModernButton>().ToArray();
        Check(Descendants(settingsCard).OfType<DataGridView>().Count() >= 2
            && settingsActions.Contains(manualActivation)
            && settingsActions.Count(button => button != manualActivation) >= 2,
            "Settings provides manual WhatsApp and independent template management without fixing layout rows");
        var decorate = type.GetMethod("PrepareSettingsForDisplay", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using var modeRows = new DataTable();
        modeRows.Columns.Add("Channel", typeof(string));
        modeRows.Columns.Add("ProviderCode", typeof(string));
        modeRows.Columns.Add("Enabled", typeof(bool));
        modeRows.Rows.Add("WhatsApp", "WhatsAppBusinessManual", false);
        decorate.Invoke(page, new object[] { modeRows });
        Check(manualActivation.Text == "Resume Free WhatsApp"
            && (string)modeRows.Rows[0]["StatusText"] == "Paused / unavailable",
            "Paused manual WhatsApp clearly offers resumption without implying delivery");
        modeRows.Rows[0]["Enabled"] = true;
        decorate.Invoke(page, new object[] { modeRows });
        Check(manualActivation.Text == "Free WhatsApp Active"
            && !manualActivation.Enabled
            && (string)modeRows.Rows[0]["StatusText"] == "Ready for manual chats",
            "Already active manual WhatsApp is displayed plainly and cannot be re-enabled by mistake");
        Check(!send.Enabled, "An unreviewed page cannot send or open a chat before preview and confirmation");

        // Render at dashboard scale. Merely having seven audience options in
        // memory did not catch the blank Select Recipients card seen in the app.
        using var host = new Form { ClientSize = new Size(1200, 780), ShowInTaskbar = false };
        host.Controls.Add(page);
        host.Show();
        Application.DoEvents();
        // Render the Settings workspace to verify user-visible, unclipped
        // actions without freezing its internal panel or row structure.
        var setStep = type.GetMethod("SetStep", BindingFlags.Instance | BindingFlags.NonPublic)!;
        setStep.Invoke(page, new object[] { 3 });
        Application.DoEvents();
        var settingsScroller = settingsCard.Content.Controls.OfType<ScrollableControl>()
            .SingleOrDefault(control => control.AutoScroll);
        Check(settingsCard.Visible && settingsScroller is not null,
            "Communication Settings has a scrollable workspace for smaller windows");
        // Test the action a user can reach, not that every action shares one
        // row or simultaneously fits into a short viewport.
        foreach (var button in settingsActions)
        {
            settingsScroller!.ScrollControlIntoView(button);
            Application.DoEvents();
            Rectangle bounds = button.RectangleToScreen(button.ClientRectangle);
            Rectangle visibleContent = Rectangle.Intersect(
                settingsCard.Content.RectangleToScreen(settingsCard.Content.ClientRectangle),
                settingsScroller.RectangleToScreen(settingsScroller.ClientRectangle));
            Rectangle exposed = Rectangle.Intersect(bounds, visibleContent);
            Check(button.Visible && button.Width > 0 && button.Height > 0
                && exposed.Width >= button.Width - 2 && exposed.Height >= button.Height - 2,
                "Communication Settings action is reachable without clipping: "
                + button.Text + "; bounds=" + bounds + "; exposed=" + exposed
                + "; viewport=" + visibleContent);
        }
        setStep.Invoke(page, new object[] { 0 });
        Application.DoEvents();
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
        var valuesType = prepared.GetNestedType("Values", BindingFlags.NonPublic)!;
        object Details(decimal? amount) => Activator.CreateInstance(valuesType,
            23, "Sample Student", "F-0023", new DateTime(2026, 11, 9), amount)!;
        var render = prepared.GetMethod("Render", BindingFlags.Static | BindingFlags.NonPublic)!;
        string RenderFormat(string format, object data) =>
            (string)render.Invoke(null, new[] { format, data })!;
        string feeText = RenderFormat(TemplateProperty(FeeTemplate(), "Body"), Details(1250.50m));
        string admissionText = RenderFormat(TemplateProperty(AdmissionTemplate(), "Body"), Details(1250.50m));
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
            && audienceField.Dock == DockStyle.Fill
            && audience.Width > 100 && audience.Height > 15
            && visibleInput.Width > 100 && visibleInput.Height > 15,
            "Recipients page visibly renders the Send to selector below its header at dashboard size");
        var campusPanel = (Control)type.GetField("campusField", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
        var classPanel = (Control)type.GetField("classField", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
        var studentPanel = (Control)type.GetField("studentField", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
        var staffPanel = (Control)type.GetField("staffField", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
        Check(!campusPanel.Visible && !classPanel.Visible && !studentPanel.Visible && !staffPanel.Visible,
            "Entire-school audience needs no extra pickers");
        void ChooseAudience(string caption)
        {
            int index = Array.IndexOf(audienceOptions, caption);
            Check(index >= 0, "Communication includes the " + caption + " audience");
            audience.SelectedIndex = index;
            Application.DoEvents();
        }
        ChooseAudience("Campus");
        Check(campusPanel.Visible && !classPanel.Visible && campusPanel.Top > audienceField.Top,
            "Campus audience visibly adds the campus selector below Send to");
        ChooseAudience("Class");
        Check(campusPanel.Visible && classPanel.Visible && classPanel.Top > campusPanel.Top,
            "Class audience visibly adds campus and class selectors in order");
        ChooseAudience("One student");
        Check(studentPanel.Visible && !campusPanel.Visible && !classPanel.Visible,
            "Individual audience displays the existing student picker");
        ChooseAudience("One staff member");
        Check(staffPanel.Visible && !studentPanel.Visible,
            "Staff audience displays the existing staff picker");
        ChooseAudience("Entire school");
        Check(audience.Visible && !campusPanel.Visible && !classPanel.Visible
            && !studentPanel.Visible && !staffPanel.Visible,
            "Switching back to entire school keeps the selector visible and collapses optional fields");
        Check(Descendants(targetCard).Contains(templates)
            && Descendants(targetCard).OfType<ModernTextBox>().Any(),
            "Audience, prepared templates and message editing are reachable in the composition step");
        host.Close();
    }
}
