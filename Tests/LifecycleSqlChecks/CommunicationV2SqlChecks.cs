using Microsoft.Data.SqlClient;

// Executed only in the existing owned, disposable SQL Server lifecycle fixture.
// Exercises production schema-9 consent authorization and evidence, never live data.
internal static class CommunicationV2SqlChecks
{
    internal static async Task Run(Fixture f, Report r, int admissionId)
    {
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.ChannelSettings WHERE Channel IN ('Sms','WhatsApp') AND Enabled=0;") == 2,
            "Both communication providers are disabled on schema-9 installation.");
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.Batch;") == 0
            && Sql.Count("SELECT COUNT(*) FROM msg.Attempt;") == 0
            && Sql.Count("SELECT COUNT(*) FROM msg.Receipt;") == 0,
            "Schema migration does not queue, submit or invent any messages.");
        r.Check(Sql.Count("SELECT COUNT(*) FROM sys.schemas WHERE name=N'comm';") == 0
            && Sql.Count("SELECT COUNT(*) FROM sys.tables WHERE object_id=OBJECT_ID(N'dbo.StudentGuardianCommunication');") == 0,
            "No old Communication Center schema or duplicate guardian preference table survived.");
        r.Check(Sql.Count("SELECT COUNT(*) FROM dbo.Students WHERE AdmissionId=@Id AND SchoolMessageConsent IS NULL;",
            Sql.P("@Id", admissionId)) == 1,
            "An existing Admission with a phone or family relationship never acquires implied consent.");

        SqlParameter[] Args(int user, bool? consent, string authority = "Students.Admit") => new[]
        {
            Sql.P("@UserId", user),
            Sql.P("@AdmissionId", admissionId),
            Sql.P("@Consent", consent),
            Sql.P("@Authority", authority)
        };
        Sql.Proc("msg.RecordAdmissionConsent", Args(f.Admin, true));
        r.Check(Sql.Count("SELECT COUNT(*) FROM dbo.Students WHERE AdmissionId=@Id AND SchoolMessageConsent=1 AND SchoolMessageConsentBy=@Actor AND SchoolMessageConsentUtc IS NOT NULL;",
            Sql.P("@Id", admissionId), Sql.P("@Actor", f.Admin)) == 1,
            "One Yes on Admission is stored in the student record, with actor and UTC evidence.");
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.ConsentAudit WHERE AdmissionId=@Id AND Consent=1;",
            Sql.P("@Id", admissionId)) == 1,
            "The first affirmative choice creates one audit record.");
        Sql.Proc("msg.RecordAdmissionConsent", Args(f.Admin, true));
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.ConsentAudit WHERE AdmissionId=@Id;",
            Sql.P("@Id", admissionId)) == 1,
            "Repeating an unchanged choice is idempotent, not a second consent event.");

        await r.Refused("An operator without Admission authority cannot record guardian consent",
            () => { Sql.Proc("msg.RecordAdmissionConsent", Args(f.Restricted, false)); return Task.CompletedTask; }, 50003);
        await r.Refused("Consent cannot be changed by an unrecognized permission",
            () => { Sql.Proc("msg.RecordAdmissionConsent", Args(f.Admin, false, "Fees.Collect")); return Task.CompletedTask; }, 51012);
        await r.Refused("Consent cannot be recorded against a nonexistent Admission",
            () => { Sql.Proc("msg.RecordAdmissionConsent", new[] {
                Sql.P("@UserId", f.Admin), Sql.P("@AdmissionId", int.MaxValue),
                Sql.P("@Consent", true), Sql.P("@Authority", "Students.Admit") });
                return Task.CompletedTask; }, 51012);

        Sql.Proc("msg.RecordAdmissionConsent", Args(f.Admin, false));
        r.Check(Sql.Count("SELECT COUNT(*) FROM dbo.Students WHERE AdmissionId=@Id AND SchoolMessageConsent=0;",
            Sql.P("@Id", admissionId)) == 1
            && Sql.Count("SELECT COUNT(*) FROM msg.ConsentAudit WHERE AdmissionId=@Id;",
                Sql.P("@Id", admissionId)) == 2,
            "Explicit withdrawal immediately overrides prior Yes and remains auditable.");
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.Batch;") == 0,
            "Admission consent alone never creates an outbound message.");
    }
}
