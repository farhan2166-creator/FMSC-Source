using System.Data;

// SQL-only synthetic maintenance acceptance. Never accesses a production
// database, sends a provider request, or removes historical school evidence.
internal static class CommunicationRetentionSqlChecks
{
    internal static async Task Run(Fixture f,Report r)
    {
        Sql.ValidateTarget();
        int pupil=Convert.ToInt32(Sql.Scalar("SELECT TOP(1) AdmissionId FROM dbo.Students ORDER BY AdmissionId"));
        int schoolBefore=Sql.Count("SELECT COUNT(*) FROM dbo.Students");
        int feesBefore=Sql.Count("SELECT COUNT(*) FROM dbo.FeePayments");
        int batchesBefore=Sql.Count("SELECT COUNT(*) FROM msg.Batch");
        int attemptsBefore=Sql.Count("SELECT COUNT(*) FROM msg.Attempt");

        long InsertCandidate(string key,string state,int ageDays)
        {
            return Convert.ToInt64(Sql.Scalar("""
                INSERT msg.AutomationCandidate(EventCode,SourceKey,AdmissionId,SourceRecordId,State,CreatedUtc)
                OUTPUT inserted.CandidateId
                VALUES('Birthday',@Key,@Student,@Student,@State,DATEADD(day,-@Age,SYSUTCDATETIME()));
                """,Sql.P("@Key",key),Sql.P("@Student",pupil),
                     Sql.P("@State",state),Sql.P("@Age",ageDays)));
        }
        string stale="BD:1990:"+pupil, recent="BD:1991:"+pupil, pending="BD:1992:"+pupil;
        long staleId=InsertCandidate(stale,"Expired",400);
        long recentId=InsertCandidate(recent,"Expired",12);
        long pendingId=InsertCandidate(pending,"Pending",400);

        // A synthetic terminal event occurrence references a valid authorization
        // but has no batch/recipient/provider outcome. Only this disposable row
        // is eligible for safe pruning; its source key remains forever.
        Sql.Proc("msg.SaveTemplate",Sql.P("@UserId",f.Admin),
            Sql.P("@TemplateId",0),Sql.P("@Name","Synthetic maintenance SMS acceptance"),
            Sql.P("@Channel","Sms"),Sql.P("@Purpose","Announcement"),
            Sql.P("@Body","Synthetic maintenance only."),Sql.P("@Active",true),
            Sql.P("@LanguageCode","en"));
        int template=Sql.Count("SELECT TemplateId FROM msg.Template WHERE Name=N'Synthetic maintenance SMS acceptance'");
        int revision=Sql.Count("SELECT MAX(RevisionNumber) FROM msg.TemplateRevision WHERE TemplateId=@Template",
            Sql.P("@Template",template));
        Guid authorization=Guid.NewGuid(), work=Guid.NewGuid();
        Sql.Query("""
            INSERT msg.AutomaticAuthorization(AuthorizationId,EventCode,TemplateId,TemplateRevision,
              Body,LanguageCode,Channel,ProviderCode,ProfileHash,TemplateJson,CampusId,ClassId,
              DailyFamilyLimit,ExpiryHours,Priority,ApprovedBy,RestoreBoundaryHash,AcademicToken)
            VALUES(@Authorization,'Birthday',@Template,@Revision,N'Synthetic test only.',
              'en','Sms','Twilio',REPLICATE('A',64),NULL,0,0,3,24,'Normal',@Admin,msg.RestoreBoundary(),NULL);
            INSERT msg.PlannedWork(WorkId,SourceKind,CandidateId,AuthorizationId,
              DueUtc,ExpiresUtc,State)
            VALUES(@Work,'Event',@Candidate,@Authorization,
              DATEADD(day,-45,SYSUTCDATETIME()),DATEADD(day,-44,SYSUTCDATETIME()),'Expired');
            """,Sql.P("@Authorization",authorization),Sql.P("@Template",template),
                Sql.P("@Revision",revision),Sql.P("@Admin",f.Admin),
                Sql.P("@Work",work),Sql.P("@Candidate",staleId));

        await r.Refused("Only a school administrator may prune communication operational data",()=>{
            Sql.Proc("msg.PruneTerminalCommunication",Sql.P("@UserId",f.Restricted),
                Sql.P("@MaxRows",200));return Task.CompletedTask;
        },50003);
        await r.Refused("Unbounded maintenance calls are rejected before touching school data",()=>{
            Sql.Proc("msg.PruneTerminalCommunication",Sql.P("@UserId",f.Admin),
                Sql.P("@MaxRows",201));return Task.CompletedTask;
        },51510);

        using(var result=Sql.Proc("msg.PruneTerminalCommunication",
            Sql.P("@UserId",f.Admin),Sql.P("@MaxRows",20)))
            r.Check(result.Rows.Count==1 &&
                Convert.ToInt32(result.Rows[0]["PrunedTerminalPlans"])==1 &&
                Convert.ToInt32(result.Rows[0]["ArchivedExpiredSuggestions"])==1,
                "Only the terminal unbound occurrence and older-than-one-year suggestion are retired.");
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.PlannedWork WHERE WorkId=@Id",Sql.P("@Id",work))==0 &&
            Sql.Count("SELECT COUNT(*) FROM msg.AutomationCandidate WHERE CandidateId=@Id",Sql.P("@Id",staleId))==0 &&
            Sql.Count("SELECT COUNT(*) FROM msg.AutomationSourceTombstone WHERE EventCode='Birthday' AND SourceKey=@Key",
                Sql.P("@Key",stale))==1,
            "The original key survives archival even after both disposable rows are gone.");
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.AutomationCandidate WHERE CandidateId IN (@Recent,@Pending)",
            Sql.P("@Recent",recentId),Sql.P("@Pending",pendingId))==2,
            "Recent and unresolved suggestions never qualify for pruning.");

        await r.Refused("Archived source keys can never be inserted as a new sending opportunity",()=>{
            InsertCandidate(stale,"Pending",0);return Task.CompletedTask;
        },51512);
        await r.Refused("Archived source keys cannot be deleted to reenable dispatch",()=>{
            Sql.Query("DELETE msg.AutomationSourceTombstone WHERE EventCode='Birthday' AND SourceKey=@Key",
                Sql.P("@Key",stale));return Task.CompletedTask;
        },51511);
        await r.Refused("Archive timestamps cannot be changed to evade historical auditing",()=>{
            Sql.Query("UPDATE msg.AutomationSourceTombstone SET ArchivedUtc=DATEADD(day,1,ArchivedUtc) WHERE EventCode='Birthday' AND SourceKey=@Key",
                Sql.P("@Key",stale));return Task.CompletedTask;
        },51511);
        using(var second=Sql.Proc("msg.PruneTerminalCommunication",
            Sql.P("@UserId",f.Admin),Sql.P("@MaxRows",20)))
            r.Check(Convert.ToInt32(second.Rows[0]["PrunedTerminalPlans"])==0 &&
                Convert.ToInt32(second.Rows[0]["ArchivedExpiredSuggestions"])==0,
                "Repeated cleanup is idempotent and cannot erase older replay evidence.");
        r.Check(schoolBefore==Sql.Count("SELECT COUNT(*) FROM dbo.Students") &&
            feesBefore==Sql.Count("SELECT COUNT(*) FROM dbo.FeePayments") &&
            batchesBefore==Sql.Count("SELECT COUNT(*) FROM msg.Batch") &&
            attemptsBefore==Sql.Count("SELECT COUNT(*) FROM msg.Attempt"),
            "School, financial, reviewed message and claimed provider history remain untouched.");
    }
}
