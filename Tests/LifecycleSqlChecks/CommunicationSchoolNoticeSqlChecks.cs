using System.Data;

// SQL-only official notice acceptance. All records are synthetic, in the
// owned disposable fixture. No provider credentials, message calls or live
// school data are touched.
internal static class CommunicationSchoolNoticeSqlChecks
{
    internal static async Task Run(Fixture f,Report r)
    {
        var modes=new[]{"Announcement","ExaminationResult"};
        int attemptsBefore=Sql.Count("SELECT COUNT(*) FROM msg.Attempt");
        int batchesBefore=Sql.Count("SELECT COUNT(*) FROM msg.Batch");
        var args=f.AdmissionArgs("Fictional official notice pupil");
        args.Single(x=>x.ParameterName=="@ContactNumber").Value="03008000981";
        int student=Convert.ToInt32(Sql.Proc("AdmitStudent",args).Rows[0]["AdmissionId"]);
        Sql.Proc("msg.RecordAdmissionConsent",
            Sql.P("@UserId",f.Admin),Sql.P("@AdmissionId",student),
            Sql.P("@Consent",true),Sql.P("@Authority","Students.Admit"));

        foreach(string kind in modes)
        {
            Sql.Proc("msg.SaveAutomationMode",Sql.P("@UserId",f.Admin),
                Sql.P("@EventCode",kind),Sql.P("@Mode","Review"),
                Sql.P("@CampusId",0),Sql.P("@ClassId",0));
            int scope=Sql.Count("SELECT COUNT(*) FROM msg.AutomationRule WHERE EventCode=@Kind AND Mode='Review'",
                Sql.P("@Kind",kind));
            r.Check(scope==1,"Official "+kind+" source is review-only by explicit school choice.");
            string title="Fictional "+kind+" school approval";
            string body="School office approved this synthetic notice for the current eligible class roster.";
            string reference=kind=="ExaminationResult" ? "EXAM-SYNTHETIC-2026" : "SCHOOL-SYNTHETIC-2026";
            DateTime expiry=DateTime.UtcNow.AddHours(24);
            Microsoft.Data.SqlClient.SqlParameter[] Params(int actor,string? record= null,string? titleOverride=null)=>
                new[]{Sql.P("@UserId",actor),Sql.P("@EventCode",kind),
                    Sql.P("@Title",titleOverride??title),Sql.P("@NoticeText",body),
                    Sql.P("@LanguageCode","en"),Sql.P("@SourceReference",record??reference),
                    Sql.P("@ExpiresUtc",expiry),Sql.P("@ExpectedCampusId",0),Sql.P("@ExpectedClassId",0)};
            await r.Refused("A restricted operator cannot publish an official notice",()=>{
                Sql.Proc("msg.ApproveSchoolNotice",Params(f.Restricted));return Task.CompletedTask;
            },50003,51450);
            await r.Refused("Approved text cannot contain dynamic message placeholders",()=>{
                Sql.Proc("msg.ApproveSchoolNotice",Params(f.Admin,titleOverride:"[StudentName]"));return Task.CompletedTask;
            },51450);
            if(kind=="ExaminationResult")
                await r.Refused("An examination notice requires an official source reference",()=>{
                    Sql.Proc("msg.ApproveSchoolNotice",Params(f.Admin,record:" "));return Task.CompletedTask;
                },51450);
            long id=Convert.ToInt64(Sql.Proc("msg.ApproveSchoolNotice",Params(f.Admin)).Rows[0]["NoticeId"]);
            r.Check(id>0 && Sql.Count("SELECT COUNT(*) FROM msg.SchoolNoticeMember WHERE NoticeId=@Notice AND AdmissionId=@Student",
                Sql.P("@Notice",id),Sql.P("@Student",student))==1,
                "Approval pins the real currently consenting roster before suggestions are discovered.");
            using(var found=Sql.Proc("msg.ReadSchoolNotices",Sql.P("@UserId",f.Admin)))
                r.Check(found.AsEnumerable().Any(x=>Convert.ToInt64(x["NoticeId"])==id && Convert.ToString(x["Title"])==title),
                    "An approved notice is visible in the school's audit list.");
            await r.Refused("An approved notice title is immutable",()=>{
                Sql.Query("UPDATE msg.SchoolNotice SET Title=N'Altered' WHERE NoticeId=@Id",Sql.P("@Id",id));
                return Task.CompletedTask;
            },51452);
            await r.Refused("An approved notice roster cannot silently expand or shrink",()=>{
                Sql.Query("DELETE FROM msg.SchoolNoticeMember WHERE NoticeId=@Id AND AdmissionId=@Student",
                    Sql.P("@Id",id),Sql.P("@Student",student));return Task.CompletedTask;
            },51452);
            Sql.Proc("msg.ScanAutomationCandidates",Sql.P("@UserId",f.Admin));
            r.Check(Sql.Count("SELECT COUNT(*) FROM msg.AutomationCandidate WHERE EventCode=@Kind AND SourceRecordId=@Id AND AdmissionId=@Student",
                Sql.P("@Kind",kind),Sql.P("@Id",id),Sql.P("@Student",student))==1,
                "Approved official source produces one idempotent review suggestion per roster pupil.");
            Sql.Proc("msg.ScanAutomationCandidates",Sql.P("@UserId",f.Admin));
            r.Check(Sql.Count("SELECT COUNT(*) FROM msg.AutomationCandidate WHERE EventCode=@Kind AND SourceRecordId=@Id AND AdmissionId=@Student",
                Sql.P("@Kind",kind),Sql.P("@Id",id),Sql.P("@Student",student))==1,
                "Repeated intake never doubles an existing notice suggestion.");

            var newcomer=f.AdmissionArgs("Fictional pupil added after published notice");
            newcomer.Single(x=>x.ParameterName=="@ContactNumber").Value=kind=="Announcement"?"03008000982":"03008000983";
            int later=Convert.ToInt32(Sql.Proc("AdmitStudent",newcomer).Rows[0]["AdmissionId"]);
            Sql.Proc("msg.RecordAdmissionConsent",Sql.P("@UserId",f.Admin),
                Sql.P("@AdmissionId",later),Sql.P("@Consent",true),Sql.P("@Authority","Students.Admit"));
            Sql.Proc("msg.ScanAutomationCandidates",Sql.P("@UserId",f.Admin));
            r.Check(Sql.Count("SELECT COUNT(*) FROM msg.SchoolNoticeMember WHERE NoticeId=@Id AND AdmissionId=@Later",
                Sql.P("@Id",id),Sql.P("@Later",later))==0,
                "Later students never join the roster of a notice already approved.");

            Sql.Proc("msg.RevokeSchoolNotice",Sql.P("@UserId",f.Admin),Sql.P("@NoticeId",id),
                Sql.P("@Reason","Synthetic notice retired by an administrator"));
            r.Check(Sql.Count("SELECT COUNT(*) FROM msg.SchoolNotice WHERE NoticeId=@Id AND RevokedUtc IS NOT NULL",
                Sql.P("@Id",id))==1,
                "Revocation remains recorded without destroying the original message source.");
            r.Check(Sql.Count("SELECT COUNT(*) FROM msg.AutomationCandidate C CROSS APPLY msg.NoticeCandidateEligible(C.CandidateId) E WHERE C.SourceRecordId=@Id AND C.EventCode=@Kind",
                Sql.P("@Id",id),Sql.P("@Kind",kind))==0,
                "A revoked notice is ineligible for a new provider claim, even if its candidate still exists.");
            await r.Refused("A revoked notice cannot be revoked again or modified silently",()=>{
                Sql.Proc("msg.RevokeSchoolNotice",Sql.P("@UserId",f.Admin),Sql.P("@NoticeId",id),Sql.P("@Reason","Duplicate revocation"));
                return Task.CompletedTask;
            },51451);
            Sql.Proc("msg.SaveAutomationMode",Sql.P("@UserId",f.Admin),Sql.P("@EventCode",kind),Sql.P("@Mode","Off"));
        }
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.Attempt")==attemptsBefore &&
                Sql.Count("SELECT COUNT(*) FROM msg.Batch")==batchesBefore,
            "Official notice approval, idempotent discovery and revocation never send or confirm messages.");
    }
}
