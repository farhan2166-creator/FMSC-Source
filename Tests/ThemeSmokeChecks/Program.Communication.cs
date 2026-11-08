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

        var type = assembly.GetType("FMSC_SMS.Pages.Communication.CommunicationCenterPage")!;
        using var page = (Control)Activator.CreateInstance(type,
            BindingFlags.NonPublic | BindingFlags.Instance, null, new object[] { false }, null)!;
        var audience = (ModernComboBox)type.GetField("audience", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
        Check(audience.Items.Count == 7 && audience.Items[0]!.ToString() == "Entire school"
            && audience.Items[1]!.ToString() == "Campus" && audience.Items[2]!.ToString() == "Class",
            "Communication target defaults to automatic school-wide audience and offers bulk campus/class");
        var send = (ModernButton)type.GetField("confirm", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
        Check(!send.Enabled, "A preview-only rebuild must never enable delivery before provider acceptance");
    }
}
