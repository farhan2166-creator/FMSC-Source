using System.Data;
using Microsoft.Data.SqlClient;

internal static class CommunicationFamilyFeeSqlChecks
{
    internal static async Task Run(Fixture f,Report r)
    {
        int Admit(string name,int? related=null)
        {
            var args=f.AdmissionArgs(name,related);
            args.Single(p=>p.ParameterName=="@ContactNumber").Value="03008000361";
            using var result=Sql.Proc("AdmitStudent",args);
            int id=Convert.ToInt32(result.Rows[0]["AdmissionId"]);
            Sql.Proc("msg.RecordAdmissionConsent",Sql.P("@UserId",f.Admin),Sql.P("@AdmissionId",id),
                Sql.P("@Consent",true),Sql.P("@Authority","Students.Admit"));
            return id;
        }
        int first=Admit("Family snapshot first"),second=Admit("Family snapshot second",first),
            third=Admit("Family snapshot third",first),replacement=Admit("Family snapshot replacement");
        var target=new CommunicationTarget(CommunicationAudience.FeeFamily,0,0,first,0,"WhatsApp");
        var preview=await CommunicationRecipientResolver.ReadAsync(target);
        var entry=preview.Entries.Single(e=>e.Included);
        var fields=CommunicationPreparedMessages.FromPreview(entry);
        r.Check(entry.FamilyFees.Count==3 && fields.RecordName.Contains("Family snapshot first")
            && fields.RecordName.Contains("Family snapshot second") && fields.RecordName.Contains("Family snapshot third"),
            "Family fee personalization identifies the whole reviewed family instead of attributing its total to one child.");
        using var settings=Sql.Query("SELECT Channel,ProviderCode,Enabled FROM msg.ChannelSettings WHERE Channel IN ('Sms','WhatsApp')");
        Guid Confirm(bool manual)
        {
            using var recipients=new DataTable();recipients.Columns.Add("RecipientKind");recipients.Columns.Add("RecordId",typeof(int));recipients.Columns.Add("PhoneE164");
            recipients.Rows.Add("Student",first,entry.PhoneE164);
            using var members=recipients.Clone();
            foreach(var member in entry.FamilyFees)members.Rows.Add("Student",member.RecordId,entry.PhoneE164);
            const string body="Reviewed family fee notice.";
            var args=new List<SqlParameter>{Sql.P("@UserId",f.Admin),Sql.P("@ClientRequestId",Guid.NewGuid()),
                Sql.P("@Channel",manual ? "WhatsApp" : "Sms"),Sql.P("@Purpose","FeeReminder"),Sql.P("@Body",body),Sql.P("@Audience","FeeFamily"),
                Sql.P("@CampusId",0),Sql.P("@ClassId",0),Sql.P("@StudentId",first),Sql.P("@StaffId",0),
                new("@Recipients",SqlDbType.Structured){TypeName="msg.RecipientSelection",Value=recipients}};
            if(manual)args.Add(new("@ReviewedFees",SqlDbType.Structured){TypeName="msg.RecipientSelection",Value=members});
            else args.AddRange(new[]{Sql.P("@ProviderCode","Twilio"),Sql.P("@ProfileHash",new string('A',64)),Sql.P("@TemplateJson",null),
                Sql.P("@ReviewJson",CommunicationReviewedContent.ReviewJson(target with {Channel="Sms"},preview,body))});
            using var confirmed=Sql.Proc(manual ? "msg.ConfirmReviewedManualBatch" : "msg.ConfirmReviewedApiBatch",args.ToArray());
            return (Guid)confirmed.Rows[0]["BatchId"];
        }
        try
        {
            Sql.Query("UPDATE msg.ChannelSettings SET Enabled=1,ProviderCode=CASE Channel WHEN 'Sms' THEN 'Twilio' ELSE 'WhatsAppBusinessManual' END WHERE Channel IN ('Sms','WhatsApp')");
            Guid manual=Confirm(true),api=Confirm(false);
            long Recipient(Guid batch)=>Convert.ToInt64(Sql.Scalar("SELECT RecipientId FROM msg.Recipient WHERE BatchId=@Id",Sql.P("@Id",batch)));
            long manualRecipient=Recipient(manual),apiRecipient=Recipient(api);
            Sql.Proc("msg.OpenManualHandoff",Sql.P("@UserId",f.Admin),Sql.P("@RecipientId",manualRecipient));
            string original=await CommunicationPreparedMessages.ReadLiveAsync(manualRecipient,true) is { } originalFields
                ? originalFields.RecordName : "";
            r.Check(original==fields.RecordName,"Manual resumption uses the same family identities as the reviewed preview.");
            // Simulate an authoritative school-record family edit after confirmation.
            // All balances and the guardian number remain identical, so total-only
            // comparison would miss this membership replacement.
            int family=Sql.Count("SELECT FamilyId FROM dbo.FamilyMembers WHERE AdmissionId=@Id",Sql.P("@Id",first));
            Sql.Query("BEGIN TRANSACTION; DELETE dbo.FamilyMembers WHERE AdmissionId IN (@Old,@New); INSERT dbo.FamilyMembers(FamilyId,AdmissionId,IsPrimaryStudent,CreatedAt) VALUES(@Family,@New,0,SYSDATETIME()); COMMIT;",
                Sql.P("@Old",second),Sql.P("@Family",family),Sql.P("@New",replacement));
            var changed=await CommunicationRecipientResolver.ReadAsync(target);
            r.Check(changed.Entries.Single(e=>e.Included).Outstanding==entry.Outstanding,
                "The synthetic family edit preserves the exact reviewed total and contact destination.");
            bool previewRefused=false;
            try { await CommunicationPreparedMessages.ReadLiveAsync(manualRecipient,true); }
            catch(CommunicationValidationException){previewRefused=true;}
            r.Check(previewRefused,"A manual family message cannot silently expand or replace its reviewed sibling list.");
            using var opened=Sql.Proc("msg.OpenManualHandoff",Sql.P("@UserId",f.Admin),Sql.P("@RecipientId",manualRecipient));
            r.Check(!Convert.ToBoolean(opened.Rows[0]["Opened"])
                && Convert.ToString(opened.Rows[0]["ExclusionCode"])=="FamilyChanged",
                "SQL independently refuses a changed family before any new external handoff.");
            await r.Refused("Atomic API claim detects a changed family even with unchanged total and literal content",()=>{
                Sql.Proc("msg.ClaimRecipient",Sql.P("@UserId",f.Admin),Sql.P("@RecipientId",apiRecipient),
                    Sql.P("@AttemptKey",Guid.NewGuid()),Sql.P("@ExpectedProviderCode","Twilio"));return Task.CompletedTask;
            },51133);
            r.Check(Sql.Count("SELECT COUNT(*) FROM msg.Attempt WHERE RecipientId=@Id",Sql.P("@Id",apiRecipient))==0,
                "A family review mismatch rolls back before persisting any provider attempt.");
        }
        finally
        {
            foreach(DataRow row in settings.Rows)Sql.Query("UPDATE msg.ChannelSettings SET Enabled=@Enabled,ProviderCode=@Provider WHERE Channel=@Channel",
                Sql.P("@Enabled",row["Enabled"]),Sql.P("@Provider",row["ProviderCode"]),Sql.P("@Channel",row["Channel"]));
        }
    }
}
