using System.Data;

// Synthetic, read-only finance-event acceptance: actual charge balances and
// due dates are consulted; no payment, admission or fee row is modified.
internal static class CommunicationFeeReviewSqlChecks
{
    internal static async Task Run(Fixture f,Report r)
    {
        int student=Sql.Count("""
            SELECT TOP(1) S.AdmissionId
            FROM dbo.Students S
            OUTER APPLY msg.RecordGuardian(S.AdmissionId) G
            WHERE S.Status=N'Active' AND G.HasConsent=1 AND G.PhoneE164 IS NOT NULL
              AND EXISTS(SELECT 1 FROM dbo.Enrollments E
                WHERE E.AdmissionId=S.AdmissionId AND E.Status=N'Active')
              AND EXISTS(SELECT 1 FROM dbo.BillingCharges B
                WHERE B.AdmissionId=S.AdmissionId AND B.Status<>N'Cancelled'
                  AND B.DueDate IS NOT NULL AND dbo.GetFeeChargeBalance(B.BillingChargeId)>0)
            ORDER BY S.AdmissionId;
            """);
        r.Check(student>0,"Disposable school has an active, consenting student with an unpaid posted charge.");
        if(student<=0)return;
        var date=Sql.Scalar("""
            SELECT TOP(1) DueDate FROM dbo.BillingCharges
            WHERE AdmissionId=@Id AND Status<>N'Cancelled'
              AND DueDate IS NOT NULL AND dbo.GetFeeChargeBalance(BillingChargeId)>0
            ORDER BY DueDate;
            """,Sql.P("@Id",student));
        r.Check(date is DateTime,"Fee-event testing is anchored to an actual posted due date.");
        if(date is not DateTime due)return;
        int Eligible(string code,DateTime on)=>Sql.Count(
            "SELECT COUNT(*) FROM msg.LiveFeeEventEligible(@Id,@Event,@On);",
            Sql.P("@Id",student),Sql.P("@Event",code),Sql.P("@On",on));
        r.Check(Eligible("OutstandingFees",due)>0,
            "Outstanding notices use an actual positive posted balance, independent of a past-due label.");
        r.Check(Eligible("UpcomingDue",due.AddDays(-3))>0,
            "Upcoming notices require a posted charge due within the next seven days.");
        r.Check(Eligible("OverdueFollowUp",due.AddDays(1))>0,
            "Overdue notices require a positive-balance charge with a past due date.");
        r.Check(Eligible("UpcomingDue",new DateTime(2099,1,1))==0 &&
                Eligible("OverdueFollowUp",new DateTime(1900,1,1))==0 &&
                Eligible("OutstandingFees",DateTime.Today)>0,
            "An unpaid balance remains outstanding without pretending it is due at unrelated dates.");
        int initialBatches=Sql.Count("SELECT COUNT(*) FROM msg.Batch"),
            initialAttempts=Sql.Count("SELECT COUNT(*) FROM msg.Attempt");
        using(var before=Sql.Proc("msg.ReadFeeReviewCandidates",Sql.P("@UserId",f.Admin)))
            r.Check(before.Rows.Count==0,
                "A new school creates no fee-review candidates while every event rule is Off.");

        Sql.Proc("msg.SaveAutomationMode",
            Sql.P("@UserId",f.Admin),Sql.P("@EventCode","OutstandingFees"),
            Sql.P("@Mode","Review"),Sql.P("@CampusId",0),Sql.P("@ClassId",0));
        var scan=Sql.Proc("msg.ScanFeeReviewCandidates",Sql.P("@UserId",f.Admin));
        r.Check(scan.Rows.Count==1 && Convert.ToInt32(scan.Rows[0]["NewSuggestions"])>=1,
            "Enabling fee review collects current outstanding balances without replaying paid charges.");
        scan=Sql.Proc("msg.ScanFeeReviewCandidates",Sql.P("@UserId",f.Admin));
        r.Check(scan.Rows.Count==1 && Convert.ToInt32(scan.Rows[0]["NewSuggestions"])==0,
            "Repeated fee review scans produce no duplicate household suggestion.");
        using(var current=Sql.Proc("msg.ReadFeeReviewCandidates",Sql.P("@UserId",f.Admin)))
        {
            r.Check(current.AsEnumerable().Any(row=>Convert.ToString(row["EventCode"])=="OutstandingFees") &&
                    current.AsEnumerable().GroupBy(row=>Convert.ToInt64(row["CandidateId"])).All(g=>g.Count()==1),
                "Outstanding reminders appear only in the finance review inbox, without duplicated ledger entries.");
            var candidate=current.AsEnumerable().FirstOrDefault(row=>
                Convert.ToString(row["EventCode"])=="OutstandingFees");
            if(candidate is not null)
            {
                long candidateId=Convert.ToInt64(candidate["CandidateId"]);
                await r.Refused("A fee suggestion cannot be resolved with an invented communication batch",()=>{
                    Sql.Proc("msg.MarkAutomationCandidatePrepared",
                        Sql.P("@UserId",f.Admin),Sql.P("@CandidateId",candidateId),
                        Sql.P("@BatchId",Guid.NewGuid()));
                    return Task.CompletedTask;
                },51284);
            }
        }
        await r.Refused("Automatic fee dispatch remains disabled even for a valid review-only event",()=>{
            Sql.Proc("msg.SaveAutomationMode",
                Sql.P("@UserId",f.Admin),Sql.P("@EventCode","OutstandingFees"),
                Sql.P("@Mode","Automatic"),Sql.P("@CampusId",0),Sql.P("@ClassId",0));
            return Task.CompletedTask;
        },51247);
        Sql.Proc("msg.SaveAutomationMode",
            Sql.P("@UserId",f.Admin),Sql.P("@EventCode","OutstandingFees"),
            Sql.P("@Mode","Off"),Sql.P("@CampusId",0),Sql.P("@ClassId",0));
        using(var hidden=Sql.Proc("msg.ReadFeeReviewCandidates",Sql.P("@UserId",f.Admin)))
            r.Check(hidden.Rows.Count==0,
                "Disabling a fee review rule immediately hides its unapproved pending work.");
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.Batch")==initialBatches &&
                Sql.Count("SELECT COUNT(*) FROM msg.Attempt")==initialAttempts,
            "Review-only fee intake creates neither provider attempts nor communication batches.");
    }
}
