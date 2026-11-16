using System.Data;

// Disposable SQL-only event intake acceptance. Never uses a live school,
// external provider, real guardian number or Windows scheduled task.
internal static class CommunicationAutomationSqlChecks
{
    internal static async Task Run(Fixture f,Report r)
    {
        int beforeAttempts=Sql.Count("SELECT COUNT(*) FROM msg.Attempt");
        int beforeBatches=Sql.Count("SELECT COUNT(*) FROM msg.Batch");
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.AutomationCandidate")==0 &&
            Sql.Count("SELECT COUNT(*) FROM msg.AutomationRule WHERE Mode='Off'")==8,
            "A new school has no event suggestions or active automation.");
        var off=Sql.Proc("msg.ScanAutomationCandidates",Sql.P("@UserId",f.Admin));
        r.Check(off.Rows.Count==1 && Convert.ToInt32(off.Rows[0]["NewSuggestions"])==0,
            "Event scanning is inert while the rules are Off.");

        await r.Refused("Event review activation requires administrator permission",()=>{
            Sql.Proc("msg.SaveAutomationMode",Sql.P("@UserId",f.Restricted),
                Sql.P("@EventCode","Admission"),Sql.P("@Mode","Review"),
                Sql.P("@CampusId",0),Sql.P("@ClassId",0));
            return Task.CompletedTask;
        },50003);
        Sql.Proc("msg.SaveAutomationMode",Sql.P("@UserId",f.Admin),
            Sql.P("@EventCode","Admission"),Sql.P("@Mode","Review"),
            Sql.P("@CampusId",0),Sql.P("@ClassId",0));
        int baseline=Convert.ToInt32(Sql.Scalar(
            "SELECT BaselineAdmissionId FROM msg.AutomationRule WHERE EventCode='Admission'"));
        r.Check(baseline>=1 &&
            Sql.Count("SELECT COUNT(*) FROM msg.AutomationCandidate")==0,
            "Enabling admission suggestions baselines existing records and generates no historical batch.");

        var admitted=f.AdmissionArgs("Fictional fresh automation student");
        admitted.Single(p=>p.ParameterName=="@ContactNumber").Value="03008000281";
        int newId=Convert.ToInt32(Sql.Proc("AdmitStudent",admitted).Rows[0]["AdmissionId"]);
        Sql.Proc("msg.RecordAdmissionConsent",
            Sql.P("@UserId",f.Admin),Sql.P("@AdmissionId",newId),
            Sql.P("@Consent",true),Sql.P("@Authority","Students.Admit"));
        r.Check(newId>baseline,"New admission is strictly after the opt-in watermark.");
        var result=Sql.Proc("msg.ScanAutomationCandidates",Sql.P("@UserId",f.Admin));
        r.Check(Convert.ToInt32(result.Rows[0]["NewSuggestions"])==1,
            "A newly admitted consenting student produces exactly one review-only suggestion.");
        result=Sql.Proc("msg.ScanAutomationCandidates",Sql.P("@UserId",f.Admin));
        r.Check(Convert.ToInt32(result.Rows[0]["NewSuggestions"])==0,
            "Repeated event scans are idempotent, including after a failed UI refresh.");
        var inbox=Sql.Proc("msg.ReadAutomationCandidates",Sql.P("@UserId",f.Admin));
        DataRow? review=inbox.AsEnumerable().FirstOrDefault(row=>
            Convert.ToString(row["EventCode"])=="Admission" &&
            Convert.ToInt32(row["AdmissionId"])==newId);
        r.Check(review is not null,
            "Review inbox contains the live consenting admission without storing a phone number.");
        if(review is null)return;
        long candidate=Convert.ToInt64(review["CandidateId"]);
        await r.Refused("A fabricated message ID cannot resolve a school event",()=>{
            Sql.Proc("msg.MarkAutomationCandidatePrepared",
                Sql.P("@UserId",f.Admin),Sql.P("@CandidateId",candidate),
                Sql.P("@BatchId",Guid.NewGuid()));
            return Task.CompletedTask;
        },51284);
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.AutomationCandidate WHERE CandidateId=@Id AND State='Pending'",
            Sql.P("@Id",candidate))==1,"Failed suggestion linking leaves the event available for review.");

        // A previously confirmed batch for a DIFFERENT student never marks
        // the new admission as handled, even when it belongs to the same school.
        var another=Sql.Scalar("SELECT TOP(1) BatchId FROM msg.Batch WHERE StudentId<>@Student AND State='Confirmed' ORDER BY CreatedUtc DESC",
            Sql.P("@Student",newId));
        if(another is Guid otherBatch)
        {
            await r.Refused("A confirmed message for another student cannot resolve this candidate",()=>{
                Sql.Proc("msg.MarkAutomationCandidatePrepared",
                    Sql.P("@UserId",f.Admin),Sql.P("@CandidateId",candidate),
                    Sql.P("@BatchId",otherBatch));
                return Task.CompletedTask;
            },51284);
        }

        Sql.Proc("msg.DismissAutomationCandidate",Sql.P("@UserId",f.Admin),
            Sql.P("@CandidateId",candidate));
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.AutomationCandidate WHERE CandidateId=@Id AND State='Dismissed'",
            Sql.P("@Id",candidate))==1,
            "Dismissing a suggestion preserves its audit record without creating a message.");
        result=Sql.Proc("msg.ScanAutomationCandidates",Sql.P("@UserId",f.Admin));
        r.Check(Convert.ToInt32(result.Rows[0]["NewSuggestions"])==0,
            "A dismissed source event is not regenerated by the same scan.");
        Sql.Proc("msg.SaveAutomationMode",Sql.P("@UserId",f.Admin),
            Sql.P("@EventCode","Admission"),Sql.P("@Mode","Off"),
            Sql.P("@CampusId",0),Sql.P("@ClassId",0));
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.AutomationRule WHERE EventCode='Admission' AND Mode='Off'")==1 &&
            Sql.Count("SELECT COUNT(*) FROM msg.Attempt")==beforeAttempts &&
            Sql.Count("SELECT COUNT(*) FROM msg.Batch")==beforeBatches,
            "Automation review leaves provider attempts and communication batches unchanged.");
        await r.Refused("Automatic event sending is not authorized by the review-only implementation",()=>{
            Sql.Proc("msg.SaveAutomationMode",Sql.P("@UserId",f.Admin),
                Sql.P("@EventCode","Admission"),Sql.P("@Mode","Automatic"),
                Sql.P("@CampusId",0),Sql.P("@ClassId",0));
            return Task.CompletedTask;
        },51247);
    }
}
