using Microsoft.Data.SqlClient;
using FMSC_SMS.Core;

// Production C# readers and production SQL procedures, on the runner's marked
// disposable school only. No browser, real contact or network dispatch exists here.
internal static class CommunicationAuditSqlChecks
{
    internal static async Task Run(Fixture f, Report r)
    {
        CurrentUser.UserId = f.Admin;
        foreach (string? input in new string?[] { null, "", "03001234567", "+923001234567", "00923001234567",
            "923001234567", "(0300) 123-4567", "0300(1234567", "0300)1234567", "+92+3001234567",
            "\u00a0+92\u202f300\u20031234567\u00a0", "02134567890", "0300123456", "abc", "0300\r\n1234567" })
        {
            string? sql = Sql.Scalar("SELECT msg.NormalizeMobile(@Value)", Sql.P("@Value", input)) as string;
            r.Check(sql == CommunicationRecipientResolver.NormalizeMobile(input),
                "SQL and Admission/C# agree on a synthetic mobile-format boundary.");
        }
        int before = Sql.Count("SELECT COUNT(*) FROM msg.Batch");
        (int Id, int Enrollment) Admit(string name, string? phone, bool? consent, int? family = null, int days = 0)
        {
            var args = f.AdmissionArgs(name, family);
            args.Single(p => p.ParameterName == "@AdmissionDate").Value = f.PromotionDate.AddDays(days);
            args.Single(p => p.ParameterName == "@ContactNumber").Value = (object?)phone ?? DBNull.Value;
            var row = Sql.Proc("AdmitStudent", args).Rows[0];
            int id = Convert.ToInt32(row["AdmissionId"]);
            if (consent.HasValue) Sql.Proc("msg.RecordAdmissionConsent", Sql.P("@UserId", f.Admin),
                Sql.P("@AdmissionId", id), Sql.P("@Consent", consent.Value), Sql.P("@Authority", "Students.Admit"));
            return (id, Convert.ToInt32(row["EnrollmentId"]));
        }
        var a = Admit("Audit A primary", "03008000001", true);
        var b = Admit("Audit B sibling", "+923008000001", true, a.Id, days: 1);
        var otherClass = Admit("Audit C another class", "03008000002", true);
        var otherCampus = Admit("Audit D another campus", "03008000003", true);
        var declined = Admit("Audit E declined", "03008000001", false, a.Id);
        var unknown = Admit("Audit F unknown", "03008000004", null);
        var invalid = Admit("Audit G invalid", null, true);
        var inactive = Admit("Audit H inactive", "03008000005", true);
        Sql.Query("UPDATE dbo.Students SET Status=N'Left' WHERE AdmissionId=@Id", Sql.P("@Id", inactive.Id));
        Sql.Query("UPDATE dbo.Enrollments SET ClassSectionId=@Class WHERE EnrollmentId=@Id", Sql.P("@Class", f.Placement7), Sql.P("@Id", otherClass.Enrollment));
        int campus = Sql.Key("SaveCampus", Sql.P("@UserId", f.Admin), Sql.P("@CampusCode", "MSG_TEST"),
            Sql.P("@CampusName", "Fictional messaging boundary"), Sql.P("@CampusType", "School"));
        int placement = Sql.Count("INSERT dbo.CampusClassSections(CampusId,ClassId,SectionId) VALUES(@Campus,@Class,@Section); SELECT CONVERT(int,SCOPE_IDENTITY());",
            Sql.P("@Campus", campus), Sql.P("@Class", f.Class6), Sql.P("@Section", f.Section));
        Sql.Query("UPDATE dbo.Enrollments SET ClassSectionId=@Class WHERE EnrollmentId=@Id", Sql.P("@Class", placement), Sql.P("@Id", otherCampus.Enrollment));
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.Batch") == before, "Admissions and consent changes never queue communication.");
        var target = new CommunicationTarget(CommunicationAudience.Class, f.Campus, f.Class6, 0, 0, "WhatsApp");
        var preview = await CommunicationRecipientResolver.ReadAsync(target);
        r.Check(preview.Entries.Where(e => e.Included).Select(e => e.RecordId).SequenceEqual(new[] { a.Id }),
            "Class discovery automatically deduplicates the guardian and excludes another class/campus without per-student selection.");
        r.Check(preview.Entries.Any(e => e.RecordId == declined.Id && e.Reason == "Consent declined" && e.PhoneE164 is null)
            && preview.Entries.Any(e => e.RecordId == unknown.Id && e.Reason == "Consent not recorded")
            && preview.Entries.Any(e => e.RecordId == invalid.Id && e.Reason == "Invalid or missing mobile")
            && preview.Entries.All(e => e.RecordId != inactive.Id),
            "Preview excludes No, unknown consent, missing phone and inactive school records.");
        var campusPreview = await CommunicationRecipientResolver.ReadAsync(target with { Audience = CommunicationAudience.Campus, ClassId = 0 });
        r.Check(campusPreview.Entries.Where(e => e.Included).Select(e => e.RecordId).ToHashSet().SetEquals(new[] { a.Id, otherClass.Id }),
            "Campus discovery includes its other classes but never the other campus.");
        var empty = await CommunicationRecipientResolver.ReadAsync(target with { CampusId = int.MaxValue });
        r.Check(empty.Included == 0 && empty.Entries.Count == 0, "An empty scope yields a usable empty preview.");
        var fees = target with { Audience = CommunicationAudience.OutstandingFees };
        var feePreview = await CommunicationRecipientResolver.ReadAsync(fees);
        var combined = CommunicationPreparedMessages.FromPreview(feePreview.Entries.Single(e => e.Included));
        decimal Due(int id) => Sql.Money("SELECT COALESCE(SUM(dbo.GetFeeChargeBalance(BillingChargeId)),0) FROM dbo.BillingCharges WHERE AdmissionId=@Id AND Status<>N'Cancelled'", Sql.P("@Id", id));
        r.Check(combined.Outstanding == Due(a.Id) + Due(b.Id) && combined.RecordName.Contains("Audit A") && combined.RecordName.Contains("Audit B")
            && !combined.RecordName.Contains("Audit E"), "Fee preview matches the whole eligible family balance and never includes declined siblings.");
        bool mixedDateRefused = false;
        try { CommunicationPreparedMessages.Render("[StudentName] admitted [AdmissionDate]", combined); }
        catch (CommunicationValidationException) { mixedDateRefused = true; }
        r.Check(mixedDateRefused, "A family reminder cannot falsely apply one sibling's admission date to children admitted on different dates.");
        var single = await CommunicationRecipientResolver.ReadAsync(fees with { StudentId = b.Id });
        r.Check(single.Included == 1 && single.Entries.Single(e => e.Included).RecordId == b.Id
            && CommunicationPreparedMessages.FromPreview(single.Entries.Single(e => e.Included)).Outstanding == Due(b.Id),
            "Collection's selected-student fee scope excludes other children, including a shared guardian's sibling.");
        var ambiguous = Admit("Audit I unrelated guardian", "03008000001", true);
        bool refused = false;
        try { await CommunicationRecipientResolver.ReadAsync(fees); }
        catch (CommunicationValidationException) { refused = true; }
        r.Check(refused, "Unrelated students sharing a phone cannot leak combined financial identities or balances.");
        Sql.Proc("msg.RecordAdmissionConsent", Sql.P("@UserId", f.Admin), Sql.P("@AdmissionId", ambiguous.Id), Sql.P("@Consent", false), Sql.P("@Authority", "Students.Admit"));
        Sql.Proc("msg.EnableManualWhatsApp", Sql.P("@UserId", f.Admin));
        DataTable Confirm(CommunicationTarget scope, CommunicationPreview reviewed, string body = "Synthetic reviewed notice", Guid? key = null, int? actor = null)
        {
            using var rows = new DataTable(); rows.Columns.Add("RecipientKind"); rows.Columns.Add("RecordId", typeof(int)); rows.Columns.Add("PhoneE164");
            foreach (var entry in reviewed.Entries.Where(e => e.Included)) rows.Rows.Add(entry.Kind, entry.RecordId, entry.PhoneE164);
            return Sql.Proc("msg.ConfirmManualBatch", Sql.P("@UserId", actor ?? f.Admin), Sql.P("@ClientRequestId", key ?? Guid.NewGuid()),
                Sql.P("@Channel", scope.Channel), Sql.P("@Purpose", scope.Purpose), Sql.P("@Body", body), Sql.P("@Audience", scope.Audience.ToString()),
                Sql.P("@CampusId", scope.CampusId), Sql.P("@ClassId", scope.ClassId), Sql.P("@StudentId", scope.StudentId), Sql.P("@StaffId", scope.StaffId),
                new SqlParameter("@Recipients", SqlDbType.Structured) { TypeName = "msg.RecipientSelection", Value = rows });
        }
        long Recipient(Guid batch) => Convert.ToInt64(Sql.Scalar("SELECT RecipientId FROM msg.Recipient WHERE BatchId=@Id", Sql.P("@Id", batch)));
        await r.Refused("A non-sender cannot confirm or create a manual batch", () => { Confirm(target, preview, actor: f.Restricted); return Task.CompletedTask; }, 50003);
        // The selected-student preview must match SQL's independently resolved scope.
        var outsidePreview = new CommunicationPreview(new[] { new CommunicationPreviewEntry(otherCampus.Id,
            "Synthetic outsider", "Student", "+923008000003", "Included", 1) });
        await r.Refused("SQL refuses a caller-supplied recipient outside the reviewed campus/class",
            () => { Confirm(target, outsidePreview); return Task.CompletedTask; }, 51086);
        await r.Refused("A selected-student fee notice cannot expand to a sibling representative",
            () => { Confirm(fees with { StudentId = b.Id }, feePreview); return Task.CompletedTask; }, 51086);
        int senderOnly = Sql.Count("""
            INSERT dbo.Roles(RoleName,Description) VALUES(N'msg_source_fixture',N'Synthetic sender without source access');
            DECLARE @Role int=SCOPE_IDENTITY();
            INSERT dbo.Users(Username,PasswordHash,PasswordSalt,RoleId,MustChangePassword)
            SELECT N'msg_source_fixture',PasswordHash,PasswordSalt,@Role,0 FROM dbo.Users WHERE UserId=@Admin;
            DECLARE @User int=SCOPE_IDENTITY();
            INSERT dbo.UserRoles(UserId,RoleId) VALUES(@User,@Role);
            INSERT dbo.RolePermissions(RoleId,PermissionId) SELECT @Role,PermissionId FROM dbo.Permissions WHERE PermissionCode=N'Communication.Send';
            SELECT @User;
            """, Sql.P("@Admin", f.Admin));
        await r.Refused("Sender authority never substitutes for student-record authority",
            () => { Confirm(target, preview, actor: senderOnly); return Task.CompletedTask; }, 50003);
        try
        {
            CurrentUser.UserId = senderOnly;
            await r.Refused("Actual production recipient reads enforce source permission",
                () => CommunicationRecipientResolver.ReadAsync(target), 50003);
        }
        finally { CurrentUser.UserId = f.Admin; }
        Guid concurrentKey = Guid.NewGuid();
        var concurrent = await Task.WhenAll(Task.Run(() => Confirm(target, preview, key: concurrentKey)), Task.Run(() => Confirm(target, preview, key: concurrentKey)));
        r.Check((Guid)concurrent[0].Rows[0]["BatchId"] == (Guid)concurrent[1].Rows[0]["BatchId"]
            && Sql.Count("SELECT COUNT(*) FROM msg.Batch WHERE ClientRequestId=@Key", Sql.P("@Key", concurrentKey)) == 1,
            "Simultaneous confirmations serialize to one persisted manual batch");
        var optedBatch = Confirm(target, preview);
        long optedRecipient = Recipient((Guid)optedBatch.Rows[0]["BatchId"]);
        Sql.Query("INSERT msg.Suppression(Channel,PhoneE164,Source) VALUES('WhatsApp','+923008000001','Administrator')");
        var optedPreview = await CommunicationRecipientResolver.ReadAsync(target);
        r.Check(optedPreview.Included == 0 && optedPreview.Suppressed >= 2,
            "Explicit channel opt-out suppresses the whole shared destination without a second consent workflow");
        var optedOpen = Sql.Proc("msg.OpenManualHandoff", Sql.P("@UserId", f.Admin), Sql.P("@RecipientId", optedRecipient));
        r.Check(!Convert.ToBoolean(optedOpen.Rows[0]["Opened"]) && Convert.ToString(optedOpen.Rows[0]["ExclusionCode"]) == "ChannelOptOut",
            "A channel opt-out added after confirmation blocks handoff before exposing its destination");
        Sql.Query("DELETE msg.Suppression WHERE Channel='WhatsApp' AND PhoneE164='+923008000001' AND Source='Administrator'");
        var template = Sql.Proc("msg.SaveTemplate", Sql.P("@UserId", f.Admin), Sql.P("@TemplateId", 0), Sql.P("@Name", "Audit template"),
            Sql.P("@Channel", "WhatsApp"), Sql.P("@Purpose", "Announcement"), Sql.P("@Body", "Old synthetic notice"), Sql.P("@Active", true));
        int templateId = Convert.ToInt32(template.Rows[0]["TemplateId"]);
        Sql.Proc("msg.SaveTemplate", Sql.P("@UserId", f.Admin), Sql.P("@TemplateId", templateId), Sql.P("@Name", "Audit template"),
            Sql.P("@Channel", "WhatsApp"), Sql.P("@Purpose", "Announcement"), Sql.P("@Body", "New synthetic notice"), Sql.P("@Active", false));
        r.Check(!Sql.Proc("msg.ReadComposeTemplates", Sql.P("@UserId", f.Admin)).AsEnumerable().Any(row => Convert.ToInt32(row["TemplateId"]) == templateId)
            && Sql.Proc("msg.ReadTemplates", Sql.P("@UserId", f.Admin)).AsEnumerable().Any(row => Convert.ToInt32(row["TemplateId"]) == templateId && (string)row["Body"] == "New synthetic notice"),
            "Edited templates persist their new content and inactive templates leave the sender list");
        var one = Confirm(fees with { StudentId = b.Id }, single);
        r.Check(Convert.ToInt64(one.Rows[0]["RecipientCount"]) == 1, "SQL confirms exactly the single-student fee audience reviewed in Collection.");
        Guid key = Guid.NewGuid();
        var saved = Confirm(target, preview, key: key);
        var replay = Confirm(target, preview, key: key);
        Guid batch = (Guid)saved.Rows[0]["BatchId"];
        r.Check((Guid)replay.Rows[0]["BatchId"] == batch && Convert.ToBoolean(replay.Rows[0]["AlreadyConfirmed"]),
            "A repeated confirmation key preserves one batch and one destination.");
        long recipient = Recipient(batch);
        Sql.Proc("msg.OpenManualHandoff", Sql.P("@UserId", f.Admin), Sql.P("@RecipientId", recipient));
        Sql.Query("UPDATE dbo.Enrollments SET ClassSectionId=@Class WHERE EnrollmentId=@Id", Sql.P("@Class", placement), Sql.P("@Id", a.Enrollment));
        bool movedRefused = false;
        try { await CommunicationPreparedMessages.ReadLiveAsync(recipient, false); }
        catch (CommunicationValidationException) { movedRefused = true; }
        r.Check(movedRefused, "Prepared-message reads recheck campus/class membership even after SQL prepared an opened handoff.");
        Sql.Query("UPDATE dbo.Enrollments SET ClassSectionId=@Class WHERE EnrollmentId=@Id", Sql.P("@Class", f.Placement6), Sql.P("@Id", a.Enrollment));
        var feeBatch = Confirm(fees, feePreview, "[StudentName] [AdmissionNumber] [OutstandingAmount]");
        long feeRecipient = Recipient((Guid)feeBatch.Rows[0]["BatchId"]);
        // Simulate a pre-upgrade bulk fee batch that retained an unused picker ID.
        // Its default marker must keep the reviewed bulk meaning after upgrade.
        Sql.Query("UPDATE msg.Batch SET StudentId=@Student WHERE BatchId=@Batch", Sql.P("@Student", b.Id), Sql.P("@Batch", (Guid)feeBatch.Rows[0]["BatchId"]));
        long bill = Convert.ToInt64(Sql.Scalar("SELECT TOP(1) BillingChargeId FROM dbo.BillingCharges WHERE EnrollmentId=@E AND FeeTypeId=@F", Sql.P("@E", a.Enrollment), Sql.P("@F", f.Tuition)));
        await CollectionDiscountWriter.CollectAsync("ProcessFeeTransaction", Array.Empty<CollectionDiscountWriter.Edit>(), "Synthetic messaging balance check",
            Lifecycle.CollectionArgs(a.Id, a.Enrollment, DateTime.Today, 500m, new[] { (bill, 500m) }));
        Sql.Proc("msg.OpenManualHandoff", Sql.P("@UserId", f.Admin), Sql.P("@RecipientId", feeRecipient));
        var current = await CommunicationPreparedMessages.ReadLiveAsync(feeRecipient, true);
        r.Check(current.Outstanding == combined.Outstanding - 500m && current.Outstanding == Due(a.Id) + Due(b.Id),
            "A resumed fee handoff refreshes real remaining balances after payment, rather than quoting gross charges or stale totals.");
        r.Check(current.RecordName.Contains("Audit A") && current.RecordName.Contains("Audit B"),
            "Existing bulk fee batches retain their original scope despite a historical unused student-picker ID.");
        Sql.Proc("msg.RecordAdmissionConsent", Sql.P("@UserId", f.Admin), Sql.P("@AdmissionId", a.Id), Sql.P("@Consent", false), Sql.P("@Authority", "Students.Admit"));
        var excluded = Sql.Proc("msg.OpenManualHandoff", Sql.P("@UserId", f.Admin), Sql.P("@RecipientId", recipient));
        r.Check(!Convert.ToBoolean(excluded.Rows[0]["Opened"]), "Withdrawal between preparation and reopening exposes neither destination nor message.");
        var history = Sql.Proc("msg.ReadHistory", Sql.P("@UserId", f.Admin)).AsEnumerable().Single(row => (Guid)row["BatchId"] == batch);
        r.Check((string)history["State"] == "Completed" && Convert.ToInt32(history["ManualSkipped"]) == 1
            && Convert.ToInt32(history["InProgress"]) == 0 && Convert.ToInt32(history["Delivered"]) == 0 && Convert.ToInt32(history["ManualReported"]) == 0,
            "Excluding the last recipient completes history, accounts for the target and never fabricates sending/delivery.");
        Sql.Proc("msg.PauseChannel", Sql.P("@UserId", f.Admin), Sql.P("@Channel", "WhatsApp"));
        await r.Refused("Paused manual messaging cannot open an otherwise eligible chat", () => {
            Sql.Proc("msg.OpenManualHandoff", Sql.P("@UserId", f.Admin), Sql.P("@RecipientId", Recipient((Guid)one.Rows[0]["BatchId"]))); return Task.CompletedTask;
        }, 51114);
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.Attempt A JOIN msg.Recipient R ON R.RecipientId=A.RecipientId JOIN msg.ManualBatch M ON M.BatchId=R.BatchId") == 0,
            "All manual tests leave direct API attempts empty.");
    }
}
