using System.Data;
using Microsoft.Data.SqlClient;

// Exercise authoritative sources and the real confirmation/handoff boundary.
// All rows belong to the runner's owned disposable school; no provider is called.
internal static class CommunicationDispatchBoundarySqlChecks
{
    internal static async Task Run(Fixture f, Report r)
    {
        var args=f.AdmissionArgs("Fictional payment source boundary");
        args.Single(p=>p.ParameterName=="@ContactNumber").Value="03008000351";
        using var admitted=Sql.Proc("AdmitStudent",args);
        int student=Convert.ToInt32(admitted.Rows[0]["AdmissionId"]);
        int enrollment=Convert.ToInt32(admitted.Rows[0]["EnrollmentId"]);
        Sql.Proc("msg.RecordAdmissionConsent",Sql.P("@UserId",f.Admin),
            Sql.P("@AdmissionId",student),Sql.P("@Consent",true),Sql.P("@Authority","Students.Admit"));
        Sql.Proc("msg.SaveAutomationMode",Sql.P("@UserId",f.Admin),
            Sql.P("@EventCode","PaymentReceived"),Sql.P("@Mode","Review"));
        long charge=Convert.ToInt64(Sql.Scalar(
            "SELECT TOP(1) BillingChargeId FROM dbo.BillingCharges WHERE EnrollmentId=@Id AND NetAmount>=1000 ORDER BY BillingChargeId",
            Sql.P("@Id",enrollment)));
        int attemptsBefore=Sql.Count("SELECT COUNT(*) FROM msg.Attempt");
        using var settings=Sql.Query("SELECT Channel,Enabled,ProviderCode FROM msg.ChannelSettings WHERE Channel IN ('Sms','WhatsApp')");
        try
        {
            // Synthetic provider availability only. No real credential or HTTP adapter exists here.
            Sql.Query("UPDATE msg.ChannelSettings SET Enabled=1,ProviderCode=CASE Channel WHEN 'Sms' THEN 'Twilio' ELSE 'WhatsAppBusinessManual' END WHERE Channel IN ('Sms','WhatsApp')");
            foreach (bool manual in new[] { true,false })
            {
                using var payment=await CollectionDiscountWriter.CollectAsync("ProcessFeeTransaction",
                    Array.Empty<CollectionDiscountWriter.Edit>(),"Synthetic source boundary",
                    Lifecycle.CollectionArgs(student,enrollment,f.PromotionDate,500m,new[]{(charge,500m)}));
                int paymentId=Convert.ToInt32(payment.Rows[0]["FeePaymentId"]);
                Sql.Proc("msg.ScanAutomationCandidates",Sql.P("@UserId",f.Admin));
                long candidate=Convert.ToInt64(Sql.Scalar(
                    "SELECT CandidateId FROM msg.AutomationCandidate WHERE EventCode='PaymentReceived' AND SourceRecordId=@Id",
                    Sql.P("@Id",paymentId)));
                var target=new CommunicationTarget(CommunicationAudience.Student,0,0,student,0,manual ? "WhatsApp" : "Sms");
                var preview=await FMSC_SMS.Core.CommunicationRecipientResolver.ReadAsync(target);
                using var recipients=new DataTable();
                recipients.Columns.Add("RecipientKind");recipients.Columns.Add("RecordId",typeof(int));recipients.Columns.Add("PhoneE164");
                foreach(var entry in preview.Entries.Where(e=>e.Included))
                    recipients.Rows.Add(entry.Kind,entry.RecordId,entry.PhoneE164);
                using var feeMembers=recipients.Clone();
                var parameters=new List<SqlParameter>{Sql.P("@UserId",f.Admin),Sql.P("@ClientRequestId",Guid.NewGuid()),
                    Sql.P("@Channel",target.Channel),Sql.P("@Purpose",target.Purpose),Sql.P("@Body","Payment recorded."),
                    Sql.P("@Audience","Student"),Sql.P("@CampusId",0),Sql.P("@ClassId",0),Sql.P("@StudentId",student),Sql.P("@StaffId",0),
                    new("@Recipients",SqlDbType.Structured){TypeName="msg.RecipientSelection",Value=recipients}};
                if(manual)
                    parameters.Add(new("@ReviewedFees",SqlDbType.Structured){TypeName="msg.RecipientSelection",Value=feeMembers});
                else
                    parameters.AddRange(new[]{Sql.P("@ProviderCode","Twilio"),Sql.P("@ProfileHash",new string('A',64)),
                        Sql.P("@ReviewJson",CommunicationReviewedContent.ReviewJson(target,preview,"Payment recorded.")),Sql.P("@TemplateJson",null)});
                using var confirmed=Sql.Proc(manual ? "msg.ConfirmReviewedManualBatch" : "msg.ConfirmReviewedApiBatch",parameters.ToArray());
                Guid batch=(Guid)confirmed.Rows[0]["BatchId"];
                Sql.Proc("msg.MarkAutomationCandidatePrepared",Sql.P("@UserId",f.Admin),
                    Sql.P("@CandidateId",candidate),Sql.P("@BatchId",batch));
                r.Check(Sql.Count("SELECT COUNT(*) FROM msg.PreparedEventEligible(@Id)",Sql.P("@Id",batch))==1,
                    "An unchanged posted payment remains eligible after explicit message review.");
                var refund=new[]{Sql.P("@AdmissionId",student),Sql.P("@EnrollmentId",enrollment),
                    Sql.P("@TransactionType","Refund"),Sql.P("@Amount",200m),Sql.P("@TransactionDate",f.PromotionDate),
                    Sql.P("@PaymentMethod",null),Sql.P("@ReferenceNumber",null),Sql.P("@Remarks","Synthetic post-review refund"),
                    Sql.P("@FeePaymentId",paymentId),new SqlParameter("@BillingChargeId",SqlDbType.BigInt){Value=DBNull.Value},
                    Sql.TvpParameter("@Allocations","FeePaymentAllocationType",Tvp.FeeAllocations(Array.Empty<(long,decimal)>()))};
                string scope=await RecoverableMutationWriter.GetScopeAsync();
                var intent=new MutationIntent(Guid.NewGuid(),MutationKind.FeeAdjustment,f.Admin,scope,
                    RecoverableMutationWriter.PayloadHash(MutationKind.FeeAdjustment,f.Admin,refund),"Synthetic post-review refund",DateTime.UtcNow);
                var adjusted=await RecoverableMutationWriter.SaveAsync(intent,refund);
                long recipient=Convert.ToInt64(Sql.Scalar("SELECT RecipientId FROM msg.Recipient WHERE BatchId=@Id",Sql.P("@Id",batch)));
                using var refused=manual
                    ? Sql.Proc("msg.OpenManualHandoff",Sql.P("@UserId",f.Admin),Sql.P("@RecipientId",recipient))
                    : Sql.Proc("msg.ClaimRecipient",Sql.P("@UserId",f.Admin),Sql.P("@RecipientId",recipient),
                        Sql.P("@AttemptKey",Guid.NewGuid()),Sql.P("@ExpectedProviderCode","Twilio"));
                r.Check(!Convert.ToBoolean(refused.Rows[0][manual ? "Opened" : "Claimed"])
                    && Convert.ToString(refused.Rows[0]["ExclusionCode"])=="SourceChanged",
                    "A real refund after review blocks the old payment acknowledgment at the final handoff or API claim.");
                r.Check(Sql.Count("SELECT COUNT(*) FROM msg.AutomationCandidate WHERE CandidateId=@Id AND State='Prepared' AND BatchId=@Batch",
                    Sql.P("@Id",candidate),Sql.P("@Batch",batch))==1,
                    "Source invalidation preserves the original approval and immutable source link.");
            }
            await CommunicationWorkerQueueSqlChecks.Run(f,r,student);
            r.Check(Sql.Count("SELECT COUNT(*) FROM msg.Attempt")==attemptsBefore,
                "Post-review financial corrections create no provider requests or fabricated delivery evidence.");
        }
        finally
        {
            Sql.Proc("msg.SaveAutomationMode",Sql.P("@UserId",f.Admin),Sql.P("@EventCode","PaymentReceived"),Sql.P("@Mode","Off"));
            foreach(DataRow row in settings.Rows)
                Sql.Query("UPDATE msg.ChannelSettings SET Enabled=@Enabled,ProviderCode=@Provider WHERE Channel=@Channel",
                    Sql.P("@Enabled",row["Enabled"]),Sql.P("@Provider",row["ProviderCode"]),Sql.P("@Channel",row["Channel"]));
        }
    }
}
