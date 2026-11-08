using Microsoft.Data.SqlClient;

// Executed only in the existing owned, disposable SQL Server lifecycle fixture.
// Exercises production schema-9 consent authorization and evidence, never live data.
internal static class CommunicationV2SqlChecks
{
    internal static async Task Run(Fixture f, Report r, int admissionId)
    {
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.ChannelSettings WHERE Channel IN ('Sms','WhatsApp') AND Enabled=0;") == 2
            && Sql.Count("SELECT COUNT(*) FROM msg.ChannelSettings WHERE Channel='Email' AND Enabled=0 AND ProviderCode IS NULL;") == 1,
            "SMS, WhatsApp and newly staged Email channels all start disabled.");
        r.Check(Sql.Count("SELECT COUNT(*) FROM dbo.Students WHERE AdmissionId=@Id AND GuardianEmail IS NULL;",
            Sql.P("@Id", admissionId)) == 1
            && Sql.Count("SELECT COUNT(*) FROM msg.EmailSuppression;") == 0,
            "Email starts with no inferred guardian address or opt-out evidence.");
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


        // The first claim is Unknown after reservation; a timeout cannot
        // authorize another network request. Definitive outcomes and verified
        // receipts are separate, serializable transitions.
        DataTable unknown = Sql.Proc("msg.RecordProviderResult",
            Sql.P("@UserId", f.Admin),Sql.P("@AttemptKey", attemptKey),
            Sql.P("@Outcome", "Unknown"),Sql.P("@ProviderMessageId", null),
            Sql.P("@FailureCode", null));
        r.Check(Convert.ToString(unknown.Rows[0]["Outcome"]) == "Unknown",
            "An uncertain provider response remains Unknown, with no implicit retry.");
        await r.Refused("Unknown cannot be explicitly requeued as a definite failure",
            () => { Sql.Proc("msg.ApproveDefinitiveRetry",
                Sql.P("@UserId", f.Admin), Sql.P("@RecipientId", recipientId),
                Sql.P("@Reason", "Operator reviewed")); return Task.CompletedTask; }, 51097);
        string sid = "SM" + new string('a', 32);
        DataTable acknowledged = Sql.Proc("msg.RecordProviderResult",
            Sql.P("@UserId", f.Admin),Sql.P("@AttemptKey", attemptKey),
            Sql.P("@Outcome", "Accepted"),Sql.P("@ProviderMessageId", sid),
            Sql.P("@FailureCode", null));
        r.Check(Convert.ToString(acknowledged.Rows[0]["Outcome"]) == "Accepted",
            "Provider acceptance is stored separately from delivered evidence.");
        DataTable delivered = Sql.Proc("msg.ApplyVerifiedReceipt",
            Sql.P("@UserId", f.Admin), Sql.P("@ProviderCode", "FMSC-Synthetic-NoNetwork"),
            Sql.P("@EventId", "test-delivered-one"),Sql.P("@ProviderMessageId", sid),
            Sql.P("@Outcome", "Delivered"));
        r.Check(!Convert.ToBoolean(delivered.Rows[0]["AlreadyRecorded"])
            && Sql.Count("SELECT COUNT(*) FROM msg.Recipient WHERE RecipientId=@Id AND State='Delivered';",
                Sql.P("@Id", recipientId))==1,
            "A verified delivery event advances an accepted recipient to Delivered.");
        DataTable replayedDelivery=Sql.Proc("msg.ApplyVerifiedReceipt",
            Sql.P("@UserId", f.Admin), Sql.P("@ProviderCode", "FMSC-Synthetic-NoNetwork"),
            Sql.P("@EventId", "test-delivered-one"),Sql.P("@ProviderMessageId", sid),
            Sql.P("@Outcome", "Delivered"));
        r.Check(Convert.ToBoolean(replayedDelivery.Rows[0]["AlreadyRecorded"]),
            "Signed receipt replay is idempotent.");
        Sql.Proc("msg.ApplyVerifiedReceipt",
            Sql.P("@UserId", f.Admin), Sql.P("@ProviderCode", "FMSC-Synthetic-NoNetwork"),
            Sql.P("@EventId", "test-delayed-accepted"), Sql.P("@ProviderMessageId", sid),
            Sql.P("@Outcome", "Accepted"));
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.Recipient WHERE RecipientId=@Id AND State='Delivered';",
            Sql.P("@Id", recipientId))==1,"Late acceptance must never regress a verified delivery.");
        await r.Refused("A replay key cannot be reassigned to a different delivery result",
            () => { Sql.Proc("msg.ApplyVerifiedReceipt",
                Sql.P("@UserId", f.Admin), Sql.P("@ProviderCode", "FMSC-Synthetic-NoNetwork"),
                Sql.P("@EventId", "test-delivered-one"),Sql.P("@ProviderMessageId", sid),
                Sql.P("@Outcome", "Failed"));return Task.CompletedTask; },51096);
        await r.Refused("A delivered attempt cannot be approved for retransmission",
            () => { Sql.Proc("msg.ApproveDefinitiveRetry",
                Sql.P("@UserId", f.Admin),Sql.P("@RecipientId", recipientId),
                Sql.P("@Reason", "Incorrect manual retry"));return Task.CompletedTask; },51097);

        // Another disposable batch exercises explicit, bounded retries.
        Sql.Query("UPDATE dbo.Students SET ContactNumber=N'03001234567' WHERE AdmissionId=@Id;",
            Sql.P("@Id", admissionId));
        DataTable retryConfirmed = Sql.Proc("msg.ConfirmBatch", Args(f.Admin, id: Guid.NewGuid()));
        Guid retryBatch=(Guid)retryConfirmed.Rows[0]["BatchId"];
        long retryRecipient=Convert.ToInt64(Sql.Scalar(
            "SELECT TOP(1) RecipientId FROM msg.Recipient WHERE BatchId=@Batch;",
            Sql.P("@Batch",retryBatch)));
        var retryClaimArgs = new[] { Sql.P("@UserId",f.Admin),
            Sql.P("@RecipientId",retryRecipient),
            Sql.P("@AttemptKey",Guid.NewGuid()),
            Sql.P("@ExpectedProviderCode","FMSC-Synthetic-NoNetwork") };
        DataTable firstRetryClaim=Sql.Proc("msg.ClaimRecipient",retryClaimArgs);
        r.Check(Convert.ToBoolean(firstRetryClaim.Rows[0]["Claimed"]),
            "A second independent recipient can be reserved for a failed-send case.");
        Guid failureKey=(Guid)firstRetryClaim.Rows[0]["AttemptKey"];
        Sql.Proc("msg.RecordProviderResult",Sql.P("@UserId",f.Admin),
            Sql.P("@AttemptKey",failureKey),Sql.P("@Outcome","Failed"),
            Sql.P("@ProviderMessageId",null),Sql.P("@FailureCode","HTTP400"));
        Sql.Proc("msg.ApproveDefinitiveRetry",Sql.P("@UserId",f.Admin),
            Sql.P("@RecipientId",retryRecipient),
            Sql.P("@Reason","Definitive failure reviewed"));
        var secondKey=Guid.NewGuid();
        DataTable secondClaim=Sql.Proc("msg.ClaimRecipient",
            Sql.P("@UserId",f.Admin),Sql.P("@RecipientId",retryRecipient),
            Sql.P("@AttemptKey",secondKey),
            Sql.P("@ExpectedProviderCode","FMSC-Synthetic-NoNetwork"));
        r.Check(Convert.ToBoolean(secondClaim.Rows[0]["Claimed"])
            && Sql.Count("SELECT COUNT(*) FROM msg.Attempt WHERE RecipientId=@Id AND AttemptNumber=2;",
                Sql.P("@Id",retryRecipient))==1,
            "An explicitly approved definite failure may be reclaimed using ordinal two.");
        await r.Refused("An unapproved uncertain second attempt cannot be retried",
            () => { Sql.Proc("msg.ApproveDefinitiveRetry",
                Sql.P("@UserId", f.Admin),Sql.P("@RecipientId",retryRecipient),
                Sql.P("@Reason","Avoid unknown replay"));return Task.CompletedTask; },51097);
        Sql.Proc("msg.RecordProviderResult",Sql.P("@UserId",f.Admin),
            Sql.P("@AttemptKey",secondKey),Sql.P("@Outcome","Failed"),
            Sql.P("@ProviderMessageId",null),Sql.P("@FailureCode","HTTP400"));
        Sql.Proc("msg.ApproveDefinitiveRetry",
            Sql.P("@UserId",f.Admin),Sql.P("@RecipientId",retryRecipient),
            Sql.P("@Reason","Second confirmed failure reviewed"));
        Guid thirdKey=Guid.NewGuid();
        DataTable thirdClaim=Sql.Proc("msg.ClaimRecipient",
            Sql.P("@UserId",f.Admin),Sql.P("@RecipientId",retryRecipient),
            Sql.P("@AttemptKey",thirdKey),
            Sql.P("@ExpectedProviderCode","FMSC-Synthetic-NoNetwork"));
        r.Check(Convert.ToBoolean(thirdClaim.Rows[0]["Claimed"]) &&
            Sql.Count("SELECT COUNT(*) FROM msg.Attempt WHERE RecipientId=@Id;",
                Sql.P("@Id",retryRecipient))==3,
            "A third attempt is the strict upper bound for manual retries.");
        Sql.Proc("msg.RecordProviderResult",Sql.P("@UserId",f.Admin),
            Sql.P("@AttemptKey",thirdKey),Sql.P("@Outcome","Failed"),
            Sql.P("@ProviderMessageId",null),Sql.P("@FailureCode","HTTP400"));
        await r.Refused("Even confirmed failures are not retried beyond three attempts",
            () => { Sql.Proc("msg.ApproveDefinitiveRetry",
                Sql.P("@UserId",f.Admin),Sql.P("@RecipientId",retryRecipient),
                Sql.P("@Reason","Attempt four must be blocked"));return Task.CompletedTask; },51097);


        // A signed callback may arrive before the HTTP response has been
        // saved. Preserve it for later SID correlation, never discard it.
        DataTable earlyBatch=Sql.Proc("msg.ConfirmBatch",Args(f.Admin,id:Guid.NewGuid()));
        long earlyRecipient=Convert.ToInt64(Sql.Scalar(
            "SELECT RecipientId FROM msg.Recipient WHERE BatchId=@Batch;",
            Sql.P("@Batch",(Guid)earlyBatch.Rows[0]["BatchId"])));
        Guid earlyKey=Guid.NewGuid();
        DataTable earlyClaim=Sql.Proc("msg.ClaimRecipient",
            Sql.P("@UserId",f.Admin),Sql.P("@RecipientId",earlyRecipient),
            Sql.P("@AttemptKey",earlyKey),
            Sql.P("@ExpectedProviderCode","FMSC-Synthetic-NoNetwork"));
        r.Check(Convert.ToBoolean(earlyClaim.Rows[0]["Claimed"]),
            "A separate synthetic batch can reserve an attempt for early callback ordering.");
        string earlySid="SM"+new string('c',32);
        Sql.Proc("msg.ApplyVerifiedReceipt",
            Sql.P("@UserId",f.Admin),Sql.P("@ProviderCode","FMSC-Synthetic-NoNetwork"),
            Sql.P("@EventId","early-delivered-synthetic"),
            Sql.P("@ProviderMessageId",earlySid),Sql.P("@Outcome","Delivered"));
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.Receipt WHERE EventId='early-delivered-synthetic' AND AppliedAttemptId IS NULL;")==1,
            "Verified evidence is durably retained when the API SID has not yet been recorded.");
        Sql.Proc("msg.RecordProviderResult",Sql.P("@UserId",f.Admin),
            Sql.P("@AttemptKey",earlyKey),Sql.P("@Outcome","Accepted"),
            Sql.P("@ProviderMessageId",earlySid),Sql.P("@FailureCode",null));
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.Recipient WHERE RecipientId=@Id AND State='Delivered';",
            Sql.P("@Id",earlyRecipient))==1
            && Sql.Count("SELECT COUNT(*) FROM msg.Receipt WHERE EventId='early-delivered-synthetic' AND AppliedAttemptId IS NOT NULL;")==1,
            "Early verified delivery is reconciled after the provider SID becomes known.");

        await r.Refused("Ordinary student viewer cannot read communication history",
            () => { Sql.Proc("msg.ReadHistory", Sql.P("@UserId", f.Restricted)); return Task.CompletedTask; }, 50003);
        DataTable history = Sql.Proc("msg.ReadHistory", Sql.P("@UserId", f.Admin));
        r.Check(history.Rows.Cast<DataRow>().Any(row => (Guid)row["BatchId"] == batch),
            "Authorized History returns the confirmed batch without exposing a raw contact list.");
        await r.Refused("A non-sender may not view sender-only active templates",
            () => { Sql.Proc("msg.ReadComposeTemplates", Sql.P("@UserId", f.Restricted));
                return Task.CompletedTask; }, 50003);
        await r.Refused("Ordinary user cannot manage communication configuration",
            () => { Sql.Proc("msg.ReadChannelSettings", Sql.P("@UserId", f.Restricted)); return Task.CompletedTask; }, 50003);
        DataTable setting = Sql.Proc("msg.ReadChannelSettings", Sql.P("@UserId", f.Admin));
        r.Check(setting.Rows.Count == 3 && setting.Rows.Cast<DataRow>().Any(row =>
            Convert.ToString(row["Channel"]) == "Email" && !Convert.ToBoolean(row["Enabled"])),
            "SMS, WhatsApp and staged Email are visible; Email starts paused.");
        DataTable template = Sql.Proc("msg.SaveTemplate",
            Sql.P("@UserId", f.Admin),Sql.P("@TemplateId", 0),Sql.P("@Name", "Synthetic notice"),
            Sql.P("@Channel", "Sms"),Sql.P("@Purpose", "Announcement"),
            Sql.P("@Body", "Synthetic test content only"),Sql.P("@Active", true));
        r.Check(Convert.ToInt32(template.Rows[0]["TemplateId"]) > 0
            && Sql.Proc("msg.ReadTemplates", Sql.P("@UserId", f.Admin)).Rows.Count == 1,
            "Template save and read require configuration permission.");
        r.Check(Sql.Proc("msg.ReadComposeTemplates", Sql.P("@UserId", f.Admin))
                .Rows.Cast<DataRow>().Any(row => Convert.ToString(row["Name"]) == "Synthetic notice"),
            "Active administrator templates are offered to senders without a second consent step.");


        Sql.Proc("msg.PauseChannel", Sql.P("@UserId", f.Admin), Sql.P("@Channel", "Sms"));
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.ChannelSettings WHERE Channel='Sms' AND Enabled=0;") == 1,
            "Administrator can pause a configured channel without external sending.");
        await r.Refused("A paused channel blocks new confirmations",
            () => { Sql.Proc("msg.ConfirmBatch", Args(f.Admin, id: Guid.NewGuid())); return Task.CompletedTask; }, 51085);
        await r.Refused("Only administrators can configure messaging providers",
            () => { Sql.Proc("msg.ConfigureChannel",
                Sql.P("@UserId",f.Restricted),Sql.P("@Channel","Sms"),
                Sql.P("@ProviderCode","Twilio"));return Task.CompletedTask; },50003);
        await r.Refused("Unknown provider cannot be installed as a sending profile",
            () => { Sql.Proc("msg.ConfigureChannel",
                Sql.P("@UserId",f.Admin),Sql.P("@Channel","Sms"),
                Sql.P("@ProviderCode","Unknown"));return Task.CompletedTask; },51098);
        Sql.Proc("msg.ConfigureChannel",
            Sql.P("@UserId",f.Admin),Sql.P("@Channel","Sms"),
            Sql.P("@ProviderCode","Twilio"));
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.ChannelSettings WHERE Channel='Sms' AND ProviderCode='Twilio' AND Enabled=0;")==1,
            "Provider configuration cannot silently activate message delivery.");
        Sql.Proc("msg.ConfigureChannel",
            Sql.P("@UserId",f.Admin),Sql.P("@Channel","WhatsApp"),
            Sql.P("@ProviderCode","MetaCloud"));
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.ChannelSettings WHERE Channel='WhatsApp' AND ProviderCode='MetaCloud' AND Enabled=0;")==1,
            "School-owned Meta WhatsApp provider can be staged but not activated.");
        Sql.Proc("msg.ConfigureChannel",
            Sql.P("@UserId",f.Admin),Sql.P("@Channel","WhatsApp"),
            Sql.P("@ProviderCode","WhatsAppBusinessManual"));
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.ChannelSettings WHERE Channel='WhatsApp' AND ProviderCode='WhatsAppBusinessManual' AND Enabled=0;")==1,
            "Free WhatsApp handoff is a disabled operator-only profile, never a silent API sender.");
        Sql.Proc("msg.ConfigureChannel",
            Sql.P("@UserId",f.Admin),Sql.P("@Channel","Email"),
            Sql.P("@ProviderCode","Smtp"));
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.ChannelSettings WHERE Channel='Email' AND ProviderCode='Smtp' AND Enabled=0;")==1,
            "School-controlled SMTP is a disabled provider profile only.");
        Sql.Proc("msg.ConfigureChannel",
            Sql.P("@UserId",f.Admin),Sql.P("@Channel","Email"),
            Sql.P("@ProviderCode","EmailClientManual"));
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.ChannelSettings WHERE Channel='Email' AND ProviderCode='EmailClientManual' AND Enabled=0;")==1,
            "Manual email client handoff stays separate from authenticated SMTP submission.");
        await r.Refused("SMTP cannot be misconfigured as an SMS gateway",
            () => { Sql.Proc("msg.ConfigureChannel",
                Sql.P("@UserId",f.Admin),Sql.P("@Channel","Sms"),
                Sql.P("@ProviderCode","Smtp"));return Task.CompletedTask; },51098);
        await r.Refused("Email cannot be claimed through the phone-only dispatch procedure",
            () => { Sql.Proc("msg.ReadDispatchProvider",
                Sql.P("@UserId",f.Admin),Sql.P("@Channel","Email"));return Task.CompletedTask; },51092);
        await r.Refused("Unauthorized users cannot select an active dispatch provider",
            () => { Sql.Proc("msg.ReadDispatchProvider",
                Sql.P("@UserId",f.Restricted),Sql.P("@Channel","Sms"));return Task.CompletedTask; },50003);
        Sql.Proc("msg.PauseChannel",Sql.P("@UserId",f.Admin),Sql.P("@Channel","Email"));
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.ChannelSettings WHERE Channel='Email' AND Enabled=0;")==1,
            "Email is independently pausable without a delivery implementation.");
        Sql.Query("UPDATE msg.ChannelSettings SET ProviderCode=NULL WHERE Channel='Sms';");

        // New free WhatsApp Business path. This uses SQL only: absolutely no
        // actual WhatsApp account, browser or network is touched by tests.
        await r.Refused("Ordinary sender cannot activate WhatsApp Business handoff",
            () => { Sql.Proc("msg.EnableManualWhatsApp", Sql.P("@UserId",f.Restricted));
                return Task.CompletedTask; },50003);
        r.Check(!Convert.ToBoolean(Sql.Proc("msg.ReadManualWhatsAppMode",
                Sql.P("@UserId",f.Admin)).Rows[0]["Enabled"]),
            "Manual WhatsApp starts disabled even though the provider is installed in schema 10.");
        Sql.Proc("msg.EnableManualWhatsApp",Sql.P("@UserId",f.Admin));
        r.Check(Convert.ToBoolean(Sql.Proc("msg.ReadManualWhatsAppMode",
                Sql.P("@UserId",f.Admin)).Rows[0]["Enabled"])
            && Sql.Count("SELECT COUNT(*) FROM msg.ChannelSettings WHERE Channel='WhatsApp' AND Enabled=1 AND ProviderCode='WhatsAppBusinessManual';")==1,
            "The administrator explicitly enables the free/manual mode without credentials.");
        await r.Refused("Manual WhatsApp cannot be selected as an HTTP dispatch provider",
            () => { Sql.Proc("msg.ReadDispatchProvider",Sql.P("@UserId",f.Admin),
                Sql.P("@Channel","WhatsApp"));return Task.CompletedTask; },51092);
        SqlParameter[] ManualArgs() {
            var args=Args(f.Admin,id:Guid.NewGuid());
            args.Single(p=>p.ParameterName=="@Channel").Value="WhatsApp";
            return args;
        }
        DataTable manual=Sql.Proc("msg.ConfirmManualBatch",ManualArgs());
        Guid manualBatch=(Guid)manual.Rows[0]["BatchId"];
        long manualRecipient=Convert.ToInt64(Sql.Scalar(
            "SELECT R.RecipientId FROM msg.Recipient R WHERE R.BatchId=@Batch;",
            Sql.P("@Batch",manualBatch)));
        r.Check(Convert.ToInt64(manual.Rows[0]["RecipientCount"])==1
            && Sql.Count("SELECT COUNT(*) FROM msg.ManualBatch WHERE BatchId=@Batch;",
                Sql.P("@Batch",manualBatch))==1
            && Sql.Count("SELECT COUNT(*) FROM msg.ManualHandoff WHERE RecipientId=@Id AND State='Pending';",
                Sql.P("@Id",manualRecipient))==1,
            "Reviewed manual WhatsApp batch and per-recipient ledger are saved atomically.");
        await r.Refused("Manual batches cannot create provider attempts through direct SQL",
            () => { Sql.Proc("msg.ClaimRecipient",
                Sql.P("@UserId",f.Admin),Sql.P("@RecipientId",manualRecipient),
                Sql.P("@AttemptKey",Guid.NewGuid()),
                Sql.P("@ExpectedProviderCode","WhatsAppBusinessManual"));
                return Task.CompletedTask; },51116);
        await r.Refused("Cannot report manual sending before a WhatsApp chat was opened",
            () => { Sql.Proc("msg.MarkManualHandoff",
                Sql.P("@UserId",f.Admin),Sql.P("@RecipientId",manualRecipient),
                Sql.P("@Outcome","ReportedSent"));return Task.CompletedTask; },51115);
        await r.Refused("Unauthorized user cannot open a confirmed guardian WhatsApp chat",
            () => { Sql.Proc("msg.OpenManualHandoff",
                Sql.P("@UserId",f.Restricted),Sql.P("@RecipientId",manualRecipient));
                return Task.CompletedTask; },50003);
        DataTable manualOpened=Sql.Proc("msg.OpenManualHandoff",
            Sql.P("@UserId",f.Admin),Sql.P("@RecipientId",manualRecipient));
        r.Check(Convert.ToBoolean(manualOpened.Rows[0]["Opened"])
            && Convert.ToString(manualOpened.Rows[0]["PhoneE164"])=="+923001234567"
            && Sql.Count("SELECT COUNT(*) FROM msg.ManualHandoff WHERE RecipientId=@Id AND State='Opened' AND OpenCount=1;",
                Sql.P("@Id",manualRecipient))==1,
            "Opening a single eligible chat records Opened, never Accepted or Delivered.");
        Sql.Proc("msg.MarkManualHandoff",
            Sql.P("@UserId",f.Admin),Sql.P("@RecipientId",manualRecipient),
            Sql.P("@Outcome","ReportedSent"));
        Sql.Proc("msg.MarkManualHandoff",
            Sql.P("@UserId",f.Admin),Sql.P("@RecipientId",manualRecipient),
            Sql.P("@Outcome","ReportedSent"));
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.ManualHandoff WHERE RecipientId=@Id AND State='ReportedSent' AND ReportedUtc IS NOT NULL;",
                Sql.P("@Id",manualRecipient))==1
            && Sql.Count("SELECT COUNT(*) FROM msg.Attempt A WHERE A.RecipientId=@Id;",
                Sql.P("@Id",manualRecipient))==0,
            "An operator-reported click is idempotent and never creates a provider delivery record.");
        await r.Refused("A reported manual message cannot be reopened for an accidental duplicate",
            () => { Sql.Proc("msg.OpenManualHandoff",
                Sql.P("@UserId",f.Admin),Sql.P("@RecipientId",manualRecipient));
                return Task.CompletedTask; },51114);
        DataTable manualHistory=Sql.Proc("msg.ReadHistory",Sql.P("@UserId",f.Admin));
        var manualRow=manualHistory.Rows.Cast<DataRow>()
            .Single(row=> (Guid)row["BatchId"]==manualBatch);
        r.Check(Convert.ToString(manualRow["Method"])=="WhatsApp Business (manual)"
            && Convert.ToInt32(manualRow["ManualReported"])==1
            && Convert.ToInt32(manualRow["Delivered"])==0,
            "History distinguishes operator-reported sends from verified delivery.");

        DataTable staleManual=Sql.Proc("msg.ConfirmManualBatch",ManualArgs());
        long staleRecipient=Convert.ToInt64(Sql.Scalar(
            "SELECT R.RecipientId FROM msg.Recipient R WHERE R.BatchId=@Batch;",
            Sql.P("@Batch",(Guid)staleManual.Rows[0]["BatchId"])));
        Sql.Proc("msg.RecordAdmissionConsent",Sql.P("@UserId",f.Admin),
            Sql.P("@AdmissionId",admissionId),Sql.P("@Consent",false),
            Sql.P("@Authority","Students.Admit"));
        DataTable blockedManual=Sql.Proc("msg.OpenManualHandoff",
            Sql.P("@UserId",f.Admin),Sql.P("@RecipientId",staleRecipient));
        r.Check(!Convert.ToBoolean(blockedManual.Rows[0]["Opened"])
            && Sql.Count("SELECT COUNT(*) FROM msg.ManualHandoff WHERE RecipientId=@Id AND State='Excluded';",
                Sql.P("@Id",staleRecipient))==1
            && Sql.Count("SELECT COUNT(*) FROM msg.Attempt A WHERE A.RecipientId=@Id;",
                Sql.P("@Id",staleRecipient))==0,
            "Withdrawal of Admission consent blocks manual WhatsApp at handoff time.");
        Sql.Proc("msg.PauseChannel",Sql.P("@UserId",f.Admin),Sql.P("@Channel","WhatsApp"));
        r.Check(!Convert.ToBoolean(Sql.Proc("msg.ReadManualWhatsAppMode",
                Sql.P("@UserId",f.Admin)).Rows[0]["Enabled"]),
            "Administrator pause immediately disables new WhatsApp handoffs.");
    }
}
