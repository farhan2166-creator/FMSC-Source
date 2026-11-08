using System.Text.Json;
using FMSC_SMS.Core;

internal static class CompatibilityGuidanceChecks
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            checks++;
        }
        var older = new DatabaseCompatibilityFailure(DatabaseCompatibilityIssue.SchemaVersion, "2", "1");
        Check(older.Title == "Database update required" && older.Message.Contains("FMSC Setup"), "Older databases need the migration route");
        Check(older.Message.Contains("back up"), "Migration advice must include a backup");
        var newer = new DatabaseCompatibilityFailure(DatabaseCompatibilityIssue.SchemaVersion, "2", "3");
        Check(newer.Title == "Application update required" && newer.Message.Contains("Install the FMSC release"), "Newer databases need a matching application");
        Check(newer.Message.Contains("Do not downgrade") && !newer.Message.Contains("apply this release's database migrations"), "Newer data must never be advised to migrate backward");
        foreach (var failure in new[] { older, newer })
        {
            Check(failure.Message.Contains($"Database schema version: {failure.FoundVersion}") && failure.Message.Contains("Required by this application: 2"), "Show actual and required versions in the main explanation");
            Check(failure.ConnectionMessage.Contains("Restart Connection and Repair Connection do not resolve"), "Explain why connection repair cannot resolve a version mismatch");
        }
        foreach (string? version in new string?[] { null, "", " ", "unknown", "release-a", "9223372036854775808" })
        {
            var unknown = new DatabaseCompatibilityFailure(DatabaseCompatibilityIssue.SchemaVersion, "2", version);
            Check(unknown.Title == "Database version could not be verified" && unknown.Message.Contains("before applying any updates"), "Unknown/non-numeric versions must not guess an upgrade direction");
            Check(!unknown.Message.Contains("is older") && !unknown.Message.Contains("is newer"), "Unknown versions must remain neutral");
        }
        var multiDigit = new DatabaseCompatibilityFailure(DatabaseCompatibilityIssue.SchemaVersion, "2", "10");
        Check(multiDigit.Title == "Application update required", "Schema versions compare numerically, not lexically");
        var structure = new DatabaseCompatibilityFailure(DatabaseCompatibilityIssue.SchemaStructure, "2", "2", "Missing U: dbo.Students");
        Check(structure.Title == "Database structure does not match" && structure.Message.Contains("The version matches"), "Matching version stamps do not imply matching structure");
        var error = new DatabaseCompatibilityException(structure);
        Check(ReferenceEquals(error.Failure, structure) && error.Message.Contains("Missing U: dbo.Students"), "Preserve typed failure and exact structural diagnostics");
        var sql = new DatabaseCompatibilityFailure(DatabaseCompatibilityIssue.SqlCompatibility, "2", SqlCompatibilityLevel: 120);
        Check(sql.Message.Contains("level: 120") && sql.Message.Contains("130 or later") && !sql.Message.Contains("Database schema version: 120"), "SQL compatibility level is distinct from schema version");
        var identity = new DatabaseCompatibilityFailure(DatabaseCompatibilityIssue.DatabaseIdentity, "2", Details: "Expected file A; found B");
        Check(identity.Message.Contains("connect the correct school records") && !identity.Message.Contains("migrations"), "A different database requires identity correction, not migration");
        Check(new DatabaseCompatibilityException(identity).Message.Contains("Expected file A; found B"), "Keep expected and actual file diagnostics");
        var definition = new DatabaseCompatibilityFailure(DatabaseCompatibilityIssue.ApplicationDefinition, "2");
        Check(definition.Message.Contains("complete FMSC application package") && !definition.Message.Contains("restore"), "Missing application definition requires application repair");

        // Audit Issue 01: verify essential schema-10 objects stay in the
        // embedded contract; do not freeze unrelated forms or reports.
        using var resource = typeof(CompatibilityGuidanceChecks).Assembly.GetManifestResourceStream(
            "FMSC_SMS.Core.DatabaseCompatibilitySchema10Requirements.json");
        Check(resource is not null, "Schema-10 compatibility contract must be embedded.");
        using var doc = JsonDocument.Parse(resource!);
        var root = doc.RootElement;
        bool Has(string section, string name, string? parent = null) =>
            root.GetProperty(section).EnumerateArray().Any(x =>
                x.GetProperty("name").GetString() == name &&
                (parent is null || x.GetProperty("objectName").GetString() == parent));
        foreach (var name in new[] { "ConsentAudit", "ChannelSettings", "Suppression", "Template",
            "Batch", "Recipient", "Attempt", "Receipt", "EmailSuppression", "ManualBatch", "ManualHandoff",
            "NormalizeMobile", "RecordAdmissionConsent", "ConfirmBatch", "ClaimRecipient",
            "RecordProviderResult", "ApplyVerifiedReceipt", "ApproveDefinitiveRetry",
            "ReadAttemptForReconciliation", "ReadHistory", "ReadChannelSettings",
            "ConfigureChannel", "PauseChannel", "ReadComposeTemplates", "ReadTemplates",
            "SaveTemplate", "ReadDispatchProvider", "EnableManualWhatsApp", "ReadManualWhatsAppMode",
            "ConfirmManualBatch", "ReadManualHandoff", "OpenManualHandoff", "MarkManualHandoff",
            "TR_Attempt_RejectManualProvider" })
            Check(Has("objects", name), "Missing mandatory messaging object in compatibility contract: " + name);
        foreach (var (parent, name) in new[] {
            ("Students", "SchoolMessageConsent"), ("Students", "SchoolMessageConsentUtc"),
            ("Students", "SchoolMessageConsentBy"), ("Students", "GuardianEmail"),
            ("Staff", "CommunicationEmail"), ("ManualHandoff", "OpenCount"),
            ("ManualHandoff", "ReportedUtc"), ("Recipient", "PhoneE164") })
            Check(Has("columns", name, parent), "Missing mandatory messaging column: " + parent + "." + name);
        foreach (var (parent, name) in new[] {
            ("Students", "CK_Students_SchoolMessageConsentEvidence"),
            ("ManualHandoff", "CK_MsgManualHandoffReported"),
            ("Recipient", "CK_MsgRecipientKind"), ("Attempt", "CK_MsgAttemptOutcome") })
            Check(Has("checkConstraints", name, parent), "Missing messaging safety check: " + name);
        Check(Has("triggers", "TR_Attempt_RejectManualProvider"), "Manual API-attempt rejection trigger must be verified.");
        Check(root.GetProperty("foreignKeys").GetArrayLength() == 5, "Messaging foreign keys must be verified.");
        Check(root.GetProperty("tableTypes").GetArrayLength() == 3, "Recipient-selection TVP must be verified.");
        Check(root.GetProperty("parameters").GetArrayLength() == 74, "Messaging procedure signatures must be verified.");
        return checks;
    }
}
