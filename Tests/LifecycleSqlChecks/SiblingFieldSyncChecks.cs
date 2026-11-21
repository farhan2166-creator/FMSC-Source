using System.Text.Json;

internal static class SiblingFieldSyncChecks
{
    internal static async Task Run(Fixture f, Report r)
    {
        CurrentUser.UserId = f.Admin;
        int Admit(string name, int? family = null) => Convert.ToInt32(Sql.Proc("AdmitStudent", f.AdmissionArgs(name, family)).Rows[0]["AdmissionId"]);
        int anchor = Admit("Sync Anchor"), sibling = Admit("Sync Sibling", anchor), second = Admit("Sync Second", anchor);
        int outsider = Admit("Sync Unrelated");
        string Value(int id, string field) => Convert.ToString(Sql.Scalar($"SELECT [{field}] FROM dbo.Students WHERE AdmissionId=@Id", Sql.P("@Id", id))) ?? "";
        DataTable Read(string field) => Sql.Query(SiblingFieldSyncSql.Read(field), Sql.P("@UserId", f.Admin), Sql.P("@Anchor", anchor));
        object[] Expected(DataTable rows) => rows.AsEnumerable().Select(row => (object)new
        {
            AdmissionId = row.Field<int>("AdmissionId"), OldValue = row.Field<string>("OldValue") ?? ""
        }).ToArray();
        DataTable Apply(string field, DataTable rows, string value, int? actor = null, object[]? expected = null) => Sql.Query(SiblingFieldSyncSql.Apply(field),
            Sql.P("@UserId", actor ?? f.Admin), Sql.P("@Anchor", anchor), Sql.P("@FamilyId", rows.Rows[0]["FamilyId"]),
            Sql.P("@Reviewed", JsonSerializer.Serialize(expected ?? Expected(rows))), Sql.P("@NewValue", value));
        string OtherFields(string field) => Convert.ToString(Sql.Scalar($"""
            SELECT AdmissionId,{string.Join(",", new[] { "Address", "ContactNumber", "AlternateContactNumber", "FatherName", "PreviousSchool", "FatherOccupation", "CasteTribe", "PlaceOfBirth" }
                .Where(name => name != field).Select(name => "[" + name + "]"))},SchoolMessageConsent,Status
            FROM dbo.Students WHERE AdmissionId IN (@A,@B,@C,@D) ORDER BY AdmissionId FOR JSON PATH,INCLUDE_NULL_VALUES
            """, Sql.P("@A", anchor), Sql.P("@B", sibling), Sql.P("@C", second), Sql.P("@D", outsider))) ?? "";
        foreach (var (field, value) in new[]
        {
            ("Address", "Owner's Street, Fictional Town"), ("ContactNumber", "03001234567"),
            ("AlternateContactNumber", "0911234567"), ("FatherName", "Shared Fictional Parent"), ("PreviousSchool", "Fictional Previous School"),
            ("FatherOccupation", "Fictional Teacher"), ("CasteTribe", "Fictional Tribe"),
            ("PlaceOfBirth", "Fictional Birth Town")
        })
        {
            using var rows = Read(field);
            r.Check(rows.Rows.Count == 3 && rows.AsEnumerable().All(row => row.Field<int>("AdmissionId") != outsider),
                "The preview includes exactly the saved student and their linked siblings.");
            string untouched = OtherFields(field), outside = Value(outsider, field);
            using var applied = Apply(field, rows, value);
            r.Check(Value(anchor, field) == value && Value(sibling, field) == value && Value(second, field) == value,
                "Confirming copies the reviewed field to every listed family record, including the edited source.");
            r.Check(OtherFields(field) == untouched && Value(outsider, field) == outside,
                "The update preserves all other fields, consent, status and unrelated families.");
            using var retry = Apply(field, rows, value);
            r.Check(Convert.ToInt32(retry.Rows[0]["Changed"]) == 0, "An exact retry after a lost reply is harmless and idempotent.");
        }

        // Consent is NOT a free-text field: the source can only explicitly
        // copy Yes/No, and each actual change must retain the admission audit.
        Sql.Proc("msg.RecordAdmissionConsent", Sql.P("@UserId", f.Admin),
            Sql.P("@AdmissionId", anchor), Sql.P("@Consent", false), Sql.P("@Authority", "Students.Administer"));
        Sql.Proc("msg.RecordAdmissionConsent", Sql.P("@UserId", f.Admin),
            Sql.P("@AdmissionId", sibling), Sql.P("@Consent", true), Sql.P("@Authority", "Students.Administer"));
        using var consentRows = Read("SchoolMessageConsent");
        r.Check(consentRows.Rows.Count == 3 &&
            consentRows.AsEnumerable().Any(row => row.Field<int>("AdmissionId") == anchor && row.Field<string>("OldValue") == "No") &&
            consentRows.AsEnumerable().Any(row => row.Field<int>("AdmissionId") == sibling && row.Field<string>("OldValue") == "Yes") &&
            consentRows.AsEnumerable().Any(row => row.Field<int>("AdmissionId") == second && row.Field<string>("OldValue") == "Not recorded"),
            "Consent preview distinguishes Not recorded, Yes and No without inferring approval.");
        int auditBefore = Sql.Count("SELECT COUNT(*) FROM msg.ConsentAudit WHERE AdmissionId IN (@A,@B,@C);",
            Sql.P("@A", anchor), Sql.P("@B", sibling), Sql.P("@C", second));
        using var consentApplied = Apply("SchoolMessageConsent", consentRows, "Yes");
        r.Check(Convert.ToInt32(consentApplied.Rows[0]["Changed"]) == 2 &&
            Sql.Count("SELECT COUNT(*) FROM dbo.Students WHERE AdmissionId IN (@A,@B,@C) AND SchoolMessageConsent=1 AND SchoolMessageConsentBy=@Actor AND SchoolMessageConsentUtc IS NOT NULL;",
                Sql.P("@A", anchor), Sql.P("@B", sibling), Sql.P("@C", second), Sql.P("@Actor", f.Admin)) == 3 &&
            Sql.Count("SELECT COUNT(*) FROM msg.ConsentAudit WHERE AdmissionId IN (@A,@B,@C);",
                Sql.P("@A", anchor), Sql.P("@B", sibling), Sql.P("@C", second)) == auditBefore + 2,
            "Only changed family consent choices gain new authority evidence and audit entries.");
        r.Check(Sql.Count("SELECT COUNT(*) FROM dbo.Students WHERE AdmissionId=@Id AND SchoolMessageConsent IS NULL;",
            Sql.P("@Id", outsider)) == 1, "Consent sync cannot change an unrelated student.");
        using var consentRetry = Apply("SchoolMessageConsent", consentRows, "Yes");
        r.Check(Convert.ToInt32(consentRetry.Rows[0]["Changed"]) == 0 &&
            Sql.Count("SELECT COUNT(*) FROM msg.ConsentAudit WHERE AdmissionId IN (@A,@B,@C);",
                Sql.P("@A", anchor), Sql.P("@B", sibling), Sql.P("@C", second)) == auditBefore + 2,
            "An exact consent retry creates neither extra changes nor duplicate audit evidence.");
        await r.Refused("Not recorded cannot be propagated as consent",
            () => { Apply("SchoolMessageConsent", consentRows, "Not recorded"); return Task.CompletedTask; }, 51210);
        using var staleConsent = Read("SchoolMessageConsent");
        Sql.Proc("msg.RecordAdmissionConsent", Sql.P("@UserId", f.Admin),
            Sql.P("@AdmissionId", sibling), Sql.P("@Consent", false), Sql.P("@Authority", "Students.Administer"));
        int auditAfterConcurrent = Sql.Count("SELECT COUNT(*) FROM msg.ConsentAudit WHERE AdmissionId IN (@A,@B,@C);",
            Sql.P("@A", anchor), Sql.P("@B", sibling), Sql.P("@C", second));
        await r.Refused("Concurrent consent changes abort an entire reviewed family update",
            () => { Apply("SchoolMessageConsent", staleConsent, "Yes"); return Task.CompletedTask; }, 51210);
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.ConsentAudit WHERE AdmissionId IN (@A,@B,@C);",
                Sql.P("@A", anchor), Sql.P("@B", sibling), Sql.P("@C", second)) == auditAfterConcurrent &&
            Sql.Count("SELECT COUNT(*) FROM dbo.Students WHERE AdmissionId=@Id AND SchoolMessageConsent=0;",
                Sql.P("@Id", sibling)) == 1,
            "A stale consent preview cannot overwrite a new refusal or fabricate audit records.");
        await r.Refused("Consent sync requires student administrator permission",
            () => { Apply("SchoolMessageConsent", staleConsent, "No", f.Restricted); return Task.CompletedTask; }, 50003);
        using var stale = Read("Address");
        Sql.Query("UPDATE dbo.Students SET Address=N'Changed elsewhere' WHERE AdmissionId=@Id", Sql.P("@Id", sibling));
        string before = Value(anchor, "Address");
        await r.Refused("A changed reviewed value aborts the complete update", () => { Apply("Address", stale, "Reviewed New Address"); return Task.CompletedTask; }, 51210);
        r.Check(Value(anchor, "Address") == before && Value(sibling, "Address") == "Changed elsewhere",
            "A stale review never partially changes the source or overwrites another operator's value.");
        using var membership = Read("Address");
        int added = Admit("Sync Added During Review", anchor);
        await r.Refused("An added sibling invalidates the original scope", () => { Apply("Address", membership, "Stale Family Address"); return Task.CompletedTask; }, 51210);
        using var fresh = Read("Address");
        var injected = Expected(fresh).Append((object)new { AdmissionId = outsider, OldValue = Value(outsider, "Address") }).ToArray();
        await r.Refused("A supplied outsider cannot enter the family update", () => { Apply("Address", fresh, "Injected Address", expected: injected); return Task.CompletedTask; }, 51210);
        await r.Refused("Student viewing alone cannot perform a shared update", () => { Apply("Address", fresh, "Unauthorized Address", f.Restricted); return Task.CompletedTask; }, 50003);
        await r.Refused("Required address cannot be cleared", () => { Apply("Address", fresh, ""); return Task.CompletedTask; }, 51210);
        await r.Refused("Oversized values are rejected without truncation", () => { Apply("Address", fresh, new string('a', 501)); return Task.CompletedTask; }, 51210);
        foreach (var (field, length) in new[] { ("FatherOccupation", 250), ("CasteTribe", 100) })
        {
            using var reviewed = Read(field);
            await r.Refused("New shared fields retain their database length limits", () =>
            {
                Apply(field, reviewed, new string('a', length + 1));
                return Task.CompletedTask;
            }, 51210);
        }
        using var clear = Read("AlternateContactNumber");
        using var cleared = Apply("AlternateContactNumber", clear, "");
        r.Check(Value(anchor, "AlternateContactNumber") == "" && Value(added, "AlternateContactNumber") == "",
            "An optional phone can be explicitly cleared for the reviewed family.");
        using var removed = Read("Address");
        Sql.Query("DELETE dbo.FamilyMembers WHERE AdmissionId=@Id", Sql.P("@Id", added));
        await r.Refused("A removed sibling also invalidates the reviewed scope", () => { Apply("Address", removed, "Removed Family Address"); return Task.CompletedTask; }, 51210);
        using var atomic = Read("Address");
        string original = Value(anchor, "Address");
        Sql.Query("CREATE TRIGGER dbo.FMSC_FailSiblingSync ON dbo.Students AFTER UPDATE AS BEGIN SET NOCOUNT ON; IF EXISTS(SELECT 1 FROM inserted WHERE Address=N'Trigger Failure Address') THROW 51211,'Injected sibling sync failure',1; END;");
        try
        {
            await r.Refused("A database failure rolls back the complete shared update", () => { Apply("Address", atomic, "Trigger Failure Address"); return Task.CompletedTask; }, 51211);
        }
        finally { Sql.Query("DROP TRIGGER dbo.FMSC_FailSiblingSync;"); }
        r.Check(Value(anchor, "Address") == original && Value(sibling, "Address") == "Changed elsewhere",
            "No family record retains a partial update after a database failure.");
        bool refused = false;
        try { _ = SiblingFieldSyncSql.Apply("Address]; DELETE FROM dbo.Students;--"); }
        catch (ArgumentException) { refused = true; }
        r.Check(refused, "SQL column selection is restricted to the supported shared fields.");
    }
}
