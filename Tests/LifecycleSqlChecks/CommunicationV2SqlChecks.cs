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
        await CheckManualConfirmation(f, r, admissionId);
    }

    private static async Task CheckManualConfirmation(Fixture f, Report r, int admissionId)
    {
        // Only the disposable, data-free SQL fixture may enable synthetic channel
        // settings. The app itself has NO provider adapter or network dispatch.
        Sql.Query("UPDATE dbo.Students SET ContactNumber=N'03001234567' WHERE AdmissionId=@Id;",
            Sql.P("@Id", admissionId));
        Sql.Proc("msg.RecordAdmissionConsent", Sql.P("@UserId", f.Admin),
            Sql.P("@AdmissionId", admissionId), Sql.P("@Consent", true),
            Sql.P("@Authority", "Students.Admit"));

        var rows = new DataTable();
        rows.Columns.Add("RecipientKind", typeof(string));
        rows.Columns.Add("RecordId", typeof(int));
        rows.Columns.Add("PhoneE164", typeof(string));
        rows.Rows.Add("Student", admissionId, "+923001234567");

        SqlParameter Selection(DataTable table) => new("@Recipients", SqlDbType.Structured)
        {
            TypeName = "msg.RecipientSelection", Value = table
        };
        Guid request = Guid.NewGuid();
        SqlParameter[] Args(int actor, DataTable? selected = null, Guid? id = null, string? body = null) => new[]
        {
            Sql.P("@UserId", actor),
            Sql.P("@ClientRequestId", id ?? request),
            Sql.P("@Channel", "Sms"),
            Sql.P("@Purpose", "Individual"),
            Sql.P("@Body", body ?? "Synthetic school notice (no external sender)"),
            Sql.P("@Audience", "Student"),
            Sql.P("@CampusId", 0),
            Sql.P("@ClassId", 0),
            Sql.P("@StudentId", admissionId),
            Sql.P("@StaffId", 0),
            Selection(selected ?? rows)
        };

        await r.Refused("Ordinary student viewer may not authorize communication",
            () => { Sql.Proc("msg.ConfirmBatch", Args(f.Restricted)); return Task.CompletedTask; }, 50003);
        await r.Refused("A disabled provider never accepts a new confirmation",
            () => { Sql.Proc("msg.ConfirmBatch", Args(f.Admin)); return Task.CompletedTask; }, 51085);
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.Batch;") == 0,
            "Refused confirmation created neither a batch nor any outgoing work.");

        // Simulate a configured provider only inside the owned local SQL catalog.
        Sql.Query("UPDATE msg.ChannelSettings SET Enabled=1,ProviderCode='FMSC-Synthetic-NoNetwork' WHERE Channel='Sms';");
        DataTable saved = Sql.Proc("msg.ConfirmBatch", Args(f.Admin));
        Guid batch = (Guid)saved.Rows[0]["BatchId"];
        r.Check(Convert.ToInt64(saved.Rows[0]["RecipientCount"]) == 1
            && !Convert.ToBoolean(saved.Rows[0]["AlreadyConfirmed"]),
            "Exact live Admission Yes audience can be confirmed once in a transaction.");
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.Recipient WHERE BatchId=@Batch AND State='Pending';",
            Sql.P("@Batch", batch)) == 1
            && Sql.Count("SELECT COUNT(*) FROM msg.Attempt;") == 0,
            "Confirmation persists only a pending destination; it never calls a provider.");

        DataTable replay = Sql.Proc("msg.ConfirmBatch", Args(f.Admin));
        r.Check((Guid)replay.Rows[0]["BatchId"] == batch
            && Convert.ToBoolean(replay.Rows[0]["AlreadyConfirmed"])
            && Sql.Count("SELECT COUNT(*) FROM msg.Recipient WHERE BatchId=@Batch;",
                Sql.P("@Batch", batch)) == 1,
            "Double clicking Confirm with the same client key cannot duplicate recipients.");
        await r.Refused("Reusing a request key for different content must fail closed",
            () => { Sql.Proc("msg.ConfirmBatch", Args(f.Admin, body: "Wrong-content replay")); return Task.CompletedTask; }, 51084);

        long recipientId = Convert.ToInt64(Sql.Scalar(
            "SELECT RecipientId FROM msg.Recipient WHERE BatchId=@Batch;",
            Sql.P("@Batch", batch)));
        SqlParameter[] Claim(int actor, Guid? key = null, string provider = "FMSC-Synthetic-NoNetwork") => new[]
        {
            Sql.P("@UserId", actor), Sql.P("@RecipientId", recipientId),
            Sql.P("@AttemptKey", key ?? Guid.NewGuid()),
            Sql.P("@ExpectedProviderCode", provider)
        };
        await r.Refused("A viewer without Communication.Send cannot claim messages",
            () => { Sql.Proc("msg.ClaimRecipient", Claim(f.Restricted)); return Task.CompletedTask; }, 50003);
        await r.Refused("Claim refuses a provider not matching approved settings",
            () => { Sql.Proc("msg.ClaimRecipient", Claim(f.Admin, provider: "Wrong-Provider")); return Task.CompletedTask; }, 51092);
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.Attempt;") == 0,
            "Authorization and provider mismatch refuse to reserve external attempts.");
        Guid attemptKey = Guid.NewGuid();
        DataTable claimed = Sql.Proc("msg.ClaimRecipient", Claim(f.Admin, attemptKey));
        r.Check(Convert.ToBoolean(claimed.Rows[0]["Claimed"])
            && (Guid)claimed.Rows[0]["AttemptKey"] == attemptKey
            && Sql.Count("SELECT COUNT(*) FROM msg.Attempt A JOIN msg.Recipient R ON R.RecipientId=A.RecipientId WHERE A.AttemptKey=@Key AND A.Outcome='Unknown' AND R.State='Unknown';",
                Sql.P("@Key", attemptKey)) == 1,
            "Atomic claim persists a stable attempt as Unknown before any possible external request.");
        await r.Refused("Concurrent or repeated claiming cannot send an Unknown destination again",
            () => { Sql.Proc("msg.ClaimRecipient", Claim(f.Admin, attemptKey)); return Task.CompletedTask; }, 51093);

        // Bulk school selection is ONE group action. A second consenting
        // student without a mobile must be excluded rather than failing SQL.
        int siblingId = Sql.Count("""
            SELECT TOP(1) AdmissionId FROM dbo.Students
            WHERE AdmissionId<>@Id AND Status=N'Active' ORDER BY AdmissionId;
            """, Sql.P("@Id", admissionId));
        r.Check(siblingId > 0, "Synthetic school fixture has a second active student for bulk communication checks.");
        Sql.Query("UPDATE dbo.Students SET ContactNumber=NULL WHERE AdmissionId=@Id;",
            Sql.P("@Id", siblingId));
        Sql.Proc("msg.RecordAdmissionConsent", Sql.P("@UserId", f.Admin),
            Sql.P("@AdmissionId", siblingId), Sql.P("@Consent", true),
            Sql.P("@Authority", "Students.Admit"));
        SqlParameter[] groupArgs = Args(f.Admin, id: Guid.NewGuid());
        groupArgs.Single(p => p.ParameterName == "@Audience").Value = "School";
        groupArgs.Single(p => p.ParameterName == "@Purpose").Value = "Announcement";
        DataTable schoolBatch = Sql.Proc("msg.ConfirmBatch", groupArgs);
        r.Check(Convert.ToInt64(schoolBatch.Rows[0]["RecipientCount"]) == 1,
            "One school-wide confirmation automatically includes Yes-consent students with valid phones and excludes missing phones.");
        Sql.Proc("msg.RecordAdmissionConsent", Sql.P("@UserId", f.Admin),
            Sql.P("@AdmissionId", siblingId), Sql.P("@Consent", false),
            Sql.P("@Authority", "Students.Admit"));

        Sql.Query("INSERT msg.Suppression(Channel,PhoneE164,Source) VALUES('Sms','+923001234567','Administrator');");
        await r.Refused("A new confirmation cannot send to an opted-out phone",
            () => { Sql.Proc("msg.ConfirmBatch", Args(f.Admin, id: Guid.NewGuid())); return Task.CompletedTask; }, 51086);
        Sql.Query("DELETE msg.Suppression WHERE Channel='Sms' AND PhoneE164='+923001234567';");

        Sql.Proc("msg.RecordAdmissionConsent", Sql.P("@UserId", f.Admin),
            Sql.P("@AdmissionId", admissionId), Sql.P("@Consent", false),
            Sql.P("@Authority", "Students.Admit"));
        await r.Refused("A withdrawn Admission Yes cancels a stale confirmation snapshot",
            () => { Sql.Proc("msg.ConfirmBatch", Args(f.Admin, id: Guid.NewGuid())); return Task.CompletedTask; }, 51086);
        long schoolRecipient = Convert.ToInt64(Sql.Scalar(
            "SELECT RecipientId FROM msg.Recipient WHERE BatchId=@Batch;",
            Sql.P("@Batch", (Guid)schoolBatch.Rows[0]["BatchId"])));
        DataTable excluded = Sql.Proc("msg.ClaimRecipient",
            Sql.P("@UserId", f.Admin),Sql.P("@RecipientId", schoolRecipient),
            Sql.P("@AttemptKey", Guid.NewGuid()),
            Sql.P("@ExpectedProviderCode", "FMSC-Synthetic-NoNetwork"));
        r.Check(!Convert.ToBoolean(excluded.Rows[0]["Claimed"])
            && Sql.Count("SELECT COUNT(*) FROM msg.Recipient WHERE RecipientId=@Id AND State='Excluded' AND ExclusionCode='NoLongerEligible';",
                Sql.P("@Id", schoolRecipient)) == 1
            && Sql.Count("SELECT COUNT(*) FROM msg.Attempt;") == 1,
            "A previously confirmed school-wide recipient loses eligibility immediately when Admission consent is withdrawn.");
        Sql.Proc("msg.RecordAdmissionConsent", Sql.P("@UserId", f.Admin),
            Sql.P("@AdmissionId", admissionId), Sql.P("@Consent", true),
            Sql.P("@Authority", "Students.Admit"));
        Sql.Query("UPDATE dbo.Students SET ContactNumber=N'03021234567' WHERE AdmissionId=@Id;",
            Sql.P("@Id", admissionId));
        await r.Refused("A changed guardian phone invalidates an old preview",
            () => { Sql.Proc("msg.ConfirmBatch", Args(f.Admin, id: Guid.NewGuid())); return Task.CompletedTask; }, 51086);

        await r.Refused("Ordinary student viewer cannot read communication history",
            () => { Sql.Proc("msg.ReadHistory", Sql.P("@UserId", f.Restricted)); return Task.CompletedTask; }, 50003);
        DataTable history = Sql.Proc("msg.ReadHistory", Sql.P("@UserId", f.Admin));
        r.Check(history.Rows.Cast<DataRow>().Any(row => (Guid)row["BatchId"] == batch),
            "Authorized History returns the confirmed batch without exposing a raw contact list.");
        await r.Refused("Ordinary user cannot manage communication configuration",
            () => { Sql.Proc("msg.ReadChannelSettings", Sql.P("@UserId", f.Restricted)); return Task.CompletedTask; }, 50003);
        DataTable setting = Sql.Proc("msg.ReadChannelSettings", Sql.P("@UserId", f.Admin));
        r.Check(setting.Rows.Count == 2, "Only SMS and WhatsApp settings are visible to the administrator.");
        DataTable template = Sql.Proc("msg.SaveTemplate",
            Sql.P("@UserId", f.Admin),Sql.P("@TemplateId", 0),Sql.P("@Name", "Synthetic notice"),
            Sql.P("@Channel", "Sms"),Sql.P("@Purpose", "Announcement"),
            Sql.P("@Body", "Synthetic test content only"),Sql.P("@Active", true));
        r.Check(Convert.ToInt32(template.Rows[0]["TemplateId"]) > 0
            && Sql.Proc("msg.ReadTemplates", Sql.P("@UserId", f.Admin)).Rows.Count == 1,
            "Template save and read require configuration permission.");

        Sql.Proc("msg.PauseChannel", Sql.P("@UserId", f.Admin), Sql.P("@Channel", "Sms"));
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.ChannelSettings WHERE Channel='Sms' AND Enabled=0;") == 1,
            "Administrator can pause a configured channel without external sending.");
        await r.Refused("A paused channel blocks new confirmations",
            () => { Sql.Proc("msg.ConfirmBatch", Args(f.Admin, id: Guid.NewGuid())); return Task.CompletedTask; }, 51085);
        Sql.Query("UPDATE msg.ChannelSettings SET ProviderCode=NULL WHERE Channel='Sms';");
    }
}
