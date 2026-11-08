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
            && Planned("WhatsApp", "Twilio") && !Planned("Sms", "Smtp")
            && !Planned("Email", "Twilio"),
            "Provider-neutral registry accepts only reviewed channel/provider pairings.");
        var adapterSelector = registry.GetMethod("ResolveForDispatch", BindingFlags.NonPublic | BindingFlags.Static)!;
        bool NoLiveAdapter(string channel, string provider)
        {
            try { adapterSelector.Invoke(null, new object[] { channel, provider }); return false; }
            catch (TargetInvocationException ex) when (ex.InnerException is NotSupportedException or InvalidOperationException)
            { return true; }
        }
        Check(NoLiveAdapter("WhatsApp", "MetaCloud") && NoLiveAdapter("Email", "Smtp")
            && NoLiveAdapter("Sms", "Smtp"),
            "Unimplemented Meta and Email transports reject dispatch before any provider credential or network request.");

        var type = assembly.GetType("FMSC_SMS.Pages.Communication.CommunicationCenterPage")!;
        using var page = (Control)Activator.CreateInstance(type,
            BindingFlags.NonPublic | BindingFlags.Instance, null, new object[] { false }, null)!;
        var audience = (ModernComboBox)type.GetField("audience", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
        Check(audience.Items.Count == 7 && audience.Items[0]!.ToString() == "Entire school"
            && audience.Items[1]!.ToString() == "Campus" && audience.Items[2]!.ToString() == "Class",
            "Communication target defaults to automatic school-wide audience and offers bulk campus/class");
        var send = (ModernButton)type.GetField("confirm", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
        var templates = (ModernComboBox)type.GetField("templateChoice",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
        Check(templates.Items.Count == 1, "Composition has one optional template picker without a separate workflow page");
        Check(!send.Enabled, "A preview-only rebuild must never enable delivery before provider acceptance");
    }
}
