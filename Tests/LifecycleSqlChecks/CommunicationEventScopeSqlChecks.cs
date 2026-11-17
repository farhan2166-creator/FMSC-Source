using Microsoft.Data.SqlClient;

internal static class CommunicationEventScopeSqlChecks
{
    internal static async Task Run(Fixture f,Report r)
    {
        string[] events={"Admission","PaymentReceived","Birthday"};
        using var priorChannel=Sql.Query("SELECT Enabled,ProviderCode FROM msg.ChannelSettings WHERE Channel='Sms'");
        using var prior=Sql.Query("SELECT EventCode,Mode,CampusId,ClassId FROM msg.AutomationRule WHERE EventCode IN ('Admission','PaymentReceived','Birthday')");
        int otherCampus=Sql.Key("SaveCampus",Sql.P("@UserId",f.Admin),Sql.P("@CampusCode","MSG_SCOPE"),
            Sql.P("@CampusName","Fictional event scope campus"),Sql.P("@CampusType","School"));
        int otherPlacement=Sql.Count("INSERT dbo.CampusClassSections(CampusId,ClassId,SectionId) VALUES(@Campus,@Class,@Section);SELECT CONVERT(int,SCOPE_IDENTITY());",
            Sql.P("@Campus",otherCampus),Sql.P("@Class",f.Class6),Sql.P("@Section",f.Section));
        void Save(string code,string mode,int campus,int classroom)=>Sql.Proc("msg.SaveAutomationMode",Sql.P("@UserId",f.Admin),
            Sql.P("@EventCode",code),Sql.P("@Mode",mode),Sql.P("@CampusId",campus),Sql.P("@ClassId",classroom));
        try
        {
            Sql.Proc("msg.EnableApiChannel",Sql.P("@UserId",f.Admin),Sql.P("@Channel","Sms"),Sql.P("@ProviderCode","Twilio"));
            foreach(string code in events)Save(code,"Review",f.Campus,f.Class6);
            var students=new List<(int Id,int Enrollment)>();
            for(int i=0;i<3;i++)
            {
                var args=f.AdmissionArgs("Fictional scoped event "+i);
                args.Single(arg=>arg.ParameterName=="@ContactNumber").Value="0300800060"+i;
                using var admitted=Sql.Proc("AdmitStudent",args);
                int id=Convert.ToInt32(admitted.Rows[0]["AdmissionId"]),enrollment=Convert.ToInt32(admitted.Rows[0]["EnrollmentId"]);
                students.Add((id,enrollment));
                Sql.Proc("msg.RecordAdmissionConsent",Sql.P("@UserId",f.Admin),Sql.P("@AdmissionId",id),Sql.P("@Consent",true),Sql.P("@Authority","Students.Admit"));
                Sql.Query("""
                    UPDATE dbo.Students SET DateOfBirth=DATEADD(year,-12,CONVERT(date,
                      (SYSUTCDATETIME() AT TIME ZONE 'UTC') AT TIME ZONE
                       (SELECT TimeZoneId FROM msg.DispatchControl WHERE ControlId=1))) WHERE AdmissionId=@Id;
                    """,Sql.P("@Id",id));
                long charge=Convert.ToInt64(Sql.Scalar("SELECT TOP(1) BillingChargeId FROM dbo.BillingCharges WHERE EnrollmentId=@Id AND FeeTypeId=@Fee ORDER BY BillingChargeId",
                    Sql.P("@Id",enrollment),Sql.P("@Fee",f.Tuition)));
                using var paid=await CollectionDiscountWriter.CollectAsync("ProcessFeeTransaction",Array.Empty<CollectionDiscountWriter.Edit>(),"Synthetic scoped payment",
                    Lifecycle.CollectionArgs(id,enrollment,DateTime.Today,100m,new[]{(charge,100m)}));
            }
            Sql.Query("UPDATE dbo.Enrollments SET ClassSectionId=@Placement WHERE EnrollmentId=@Id",Sql.P("@Placement",f.Placement7),Sql.P("@Id",students[1].Enrollment));
            Sql.Query("UPDATE dbo.Enrollments SET ClassSectionId=@Placement WHERE EnrollmentId=@Id",Sql.P("@Placement",otherPlacement),Sql.P("@Id",students[2].Enrollment));
            Sql.Proc("msg.ScanAutomationCandidates",Sql.P("@UserId",f.Admin));
            using var inbox=Sql.Proc("msg.ReadAutomationCandidates",Sql.P("@UserId",f.Admin));
            var ids=students.Select(student=>student.Id).ToHashSet();
            foreach(string code in events)
                r.Check(inbox.AsEnumerable().Where(row=>Convert.ToString(row["EventCode"])==code && ids.Contains(Convert.ToInt32(row["AdmissionId"])))
                    .Select(row=>Convert.ToInt32(row["AdmissionId"])).SequenceEqual(new[]{students[0].Id}),
                    "The actual "+code+" source discovers only the configured campus and class.");
            r.Check(Sql.Count("SELECT COUNT(*) FROM msg.AutomationCandidate WHERE AdmissionId IN(@OtherClass,@OtherCampus) AND EventCode IN('Admission','PaymentReceived','Birthday')",
                Sql.P("@OtherClass",students[1].Id),Sql.P("@OtherCampus",students[2].Id))==0,
                "Excluded campuses and classes never enter the review suggestion ledger.");
            Sql.Proc("msg.SaveAutomationMode",Sql.P("@UserId",f.Admin),Sql.P("@EventCode","Admission"),Sql.P("@Mode","Review"));
            r.Check(Sql.Count("SELECT COUNT(*) FROM msg.AutomationRule WHERE EventCode='Admission' AND CampusId=@Campus AND ClassId=@Class",
                Sql.P("@Campus",f.Campus),Sql.P("@Class",f.Class6))==1,
                "A mode-only settings save preserves a scoped audience instead of silently selecting the whole school.");
            Save("Admission","Review",otherCampus,0);
            Sql.Proc("msg.ScanAutomationCandidates",Sql.P("@UserId",f.Admin));
            using(var changed=Sql.Proc("msg.ReadAutomationCandidates",Sql.P("@UserId",f.Admin)))
                r.Check(changed.AsEnumerable().Where(row=>Convert.ToString(row["EventCode"])=="Admission" && ids.Contains(Convert.ToInt32(row["AdmissionId"])))
                    .Select(row=>Convert.ToInt32(row["AdmissionId"])).SequenceEqual(new[]{students[2].Id}),
                    "A campus-only rule discovers its classes and hides earlier suggestions outside the new campus.");
            Save("Admission","Review",f.Campus,f.Class6);
            int anchor=students[0].Id;
            var target=new CommunicationTarget(CommunicationAudience.Student,0,0,anchor,0,"Sms");
            var preview=await CommunicationRecipientResolver.ReadAsync(target);
            using var recipients=new DataTable();recipients.Columns.Add("RecipientKind");recipients.Columns.Add("RecordId",typeof(int));recipients.Columns.Add("PhoneE164");
            foreach(var entry in preview.Entries.Where(entry=>entry.Included))recipients.Rows.Add(entry.Kind,entry.RecordId,entry.PhoneE164);
            using var confirmed=Sql.Proc("msg.ConfirmReviewedApiBatch",Sql.P("@UserId",f.Admin),Sql.P("@ClientRequestId",Guid.NewGuid()),
                Sql.P("@Channel","Sms"),Sql.P("@Purpose","Individual"),Sql.P("@Body","Synthetic scoped admission note."),Sql.P("@Audience","Student"),
                Sql.P("@CampusId",0),Sql.P("@ClassId",0),Sql.P("@StudentId",anchor),Sql.P("@StaffId",0),
                new SqlParameter("@Recipients",SqlDbType.Structured){TypeName="msg.RecipientSelection",Value=recipients},
                Sql.P("@ProviderCode","Twilio"),Sql.P("@ProfileHash",new string('A',64)),
                Sql.P("@ReviewJson",CommunicationReviewedContent.ReviewJson(target,preview,"Synthetic scoped admission note.")),Sql.P("@TemplateJson",null));
            Guid batch=(Guid)confirmed.Rows[0]["BatchId"];
            long candidate=Convert.ToInt64(Sql.Scalar("SELECT CandidateId FROM msg.AutomationCandidate WHERE AdmissionId=@Id AND EventCode='Admission'",Sql.P("@Id",anchor)));
            Sql.Proc("msg.MarkAutomationCandidatePrepared",Sql.P("@UserId",f.Admin),Sql.P("@CandidateId",candidate),Sql.P("@BatchId",batch));
            r.Check(Sql.Count("SELECT COUNT(*) FROM msg.PreparedEventEligible(@Id)",Sql.P("@Id",batch))==1,
                "A currently in-scope reviewed source remains eligible without contacting a provider.");
            Sql.Query("UPDATE dbo.Enrollments SET ClassSectionId=@Placement WHERE EnrollmentId=@Id",Sql.P("@Placement",f.Placement7),Sql.P("@Id",students[0].Enrollment));
            r.Check(Sql.Count("SELECT COUNT(*) FROM msg.PreparedEventEligible(@Id)",Sql.P("@Id",batch))==0,
                "Moving class after approval invalidates the event at the shared manual/API sending boundary.");
            long birthday=Convert.ToInt64(Sql.Scalar("SELECT CandidateId FROM msg.AutomationCandidate WHERE AdmissionId=@Id AND EventCode='Birthday'",Sql.P("@Id",anchor)));
            await r.Refused("A student outside the rule scope cannot resolve a suggested event through an alternative reviewed batch",()=>{
                Sql.Proc("msg.MarkAutomationCandidatePrepared",Sql.P("@UserId",f.Admin),Sql.P("@CandidateId",birthday),Sql.P("@BatchId",batch));return Task.CompletedTask;
            },51285);
            using(var moved=Sql.Proc("msg.ReadAutomationCandidates",Sql.P("@UserId",f.Admin)))
                r.Check(!moved.AsEnumerable().Any(row=>Convert.ToInt32(row["AdmissionId"])==anchor),
                    "Changing placement immediately removes no-longer-eligible pending suggestions.");
            long recipient=Convert.ToInt64(Sql.Scalar("SELECT RecipientId FROM msg.Recipient WHERE BatchId=@Id",Sql.P("@Id",batch)));
            int attempts=Sql.Count("SELECT COUNT(*) FROM msg.Attempt");
            using var refused=Sql.Proc("msg.ClaimRecipient",Sql.P("@UserId",f.Admin),Sql.P("@RecipientId",recipient),
                Sql.P("@AttemptKey",Guid.NewGuid()),Sql.P("@ExpectedProviderCode","Twilio"));
            r.Check(!Convert.ToBoolean(refused.Rows[0]["Claimed"]) && Convert.ToString(refused.Rows[0]["ExclusionCode"])=="SourceChanged" &&
                Sql.Count("SELECT COUNT(*) FROM msg.Attempt")==attempts,
                "A changed event scope excludes the reviewed provider claim without attempts or fabricated delivery.");
        }
        finally
        {
            foreach(DataRow channel in priorChannel.Rows)Sql.Query("UPDATE msg.ChannelSettings SET Enabled=@Enabled,ProviderCode=@Provider WHERE Channel='Sms'",
                Sql.P("@Enabled",channel["Enabled"]),Sql.P("@Provider",channel["ProviderCode"]));
            foreach(DataRow row in prior.Rows)Save(Convert.ToString(row["EventCode"])!,Convert.ToString(row["Mode"])!,Convert.ToInt32(row["CampusId"]),Convert.ToInt32(row["ClassId"]));
        }
    }
}
