using System.Data;
using Microsoft.Data.SqlClient;

internal static class CommunicationFinancialSourceSqlChecks
{
    internal static async Task Run(Fixture f,Report r)
    {
        var args=f.AdmissionArgs("Fictional receipt review source");
        args.Single(p=>p.ParameterName=="@ContactNumber").Value="03008000371";
        using var admitted=Sql.Proc("AdmitStudent",args);
        int student=Convert.ToInt32(admitted.Rows[0]["AdmissionId"]),enrollment=Convert.ToInt32(admitted.Rows[0]["EnrollmentId"]);
        Sql.Proc("msg.RecordAdmissionConsent",Sql.P("@UserId",f.Admin),Sql.P("@AdmissionId",student),Sql.P("@Consent",true),Sql.P("@Authority","Students.Admit"));
        long charge=Convert.ToInt64(Sql.Scalar("SELECT TOP(1) BillingChargeId FROM dbo.BillingCharges WHERE EnrollmentId=@Id AND NetAmount>=1000 ORDER BY BillingChargeId",Sql.P("@Id",enrollment)));
        using var channels=Sql.Query("SELECT Channel,ProviderCode,Enabled FROM msg.ChannelSettings WHERE Channel IN ('Sms','WhatsApp')");
        try
        {
            Sql.Query("UPDATE msg.ChannelSettings SET Enabled=1,ProviderCode=CASE Channel WHEN 'Sms' THEN 'Twilio' ELSE 'WhatsAppBusinessManual' END WHERE Channel IN ('Sms','WhatsApp')");
            foreach(bool manual in new[]{true,false})
            {
                decimal due=Sql.Money("SELECT dbo.GetFeeChargeBalance(@Id)",Sql.P("@Id",charge));
                var receipt=new CollectionDiscountWriter.ReceiptDraft(student,new[]{student},f.PromotionDate,"Cash","Synthetic receipt source",500m,0m,
                    new[]{new CollectionDiscountWriter.ReceiptLine(charge,student,enrollment,due,0m,500m,0m)});
                using var payment=await CollectionDiscountWriter.CollectWithReceiptAsync("ProcessFeeTransaction",Array.Empty<CollectionDiscountWriter.Edit>(),
                    Array.Empty<CollectionDiscountWriter.CreditEdit>(),f.PromotionDate,"Synthetic receipt source",receipt,
                    Lifecycle.CollectionArgs(student,enrollment,f.PromotionDate,500m,new[]{(charge,500m)}));
                int paymentId=Convert.ToInt32(payment.Rows[0]["FeePaymentId"]);
                string number=Convert.ToString(payment.Rows[0]["ReceiptNumber"])!;
                byte[] Source(string sourceKind="Receipt")
                {
                    using var rows=Sql.Proc("msg.ReadFinancialReviewSource",Sql.P("@UserId",f.Admin),Sql.P("@ReceiptNumber",number),
                        Sql.P("@AdmissionId",student),Sql.P("@WholeFamily",false),Sql.P("@SourceKind",sourceKind));
                    r.Check(rows.Rows.Count==1,"The original posted receipt has one permission-scoped financial source.");
                    return (byte[])rows.Rows[0]["SourceHash"];
                }
                void Reserve(Guid request,byte[] hash,string sourceKind="Receipt") => Sql.Proc("msg.ReserveFinancialReview",Sql.P("@UserId",f.Admin),Sql.P("@ClientRequestId",request),
                    Sql.P("@ReceiptNumber",number),Sql.P("@AdmissionId",student),Sql.P("@WholeFamily",false),Sql.P("@SourceHash",hash),Sql.P("@SourceKind",sourceKind));
                var target=new CommunicationTarget(CommunicationAudience.FeeStudent,0,0,student,0,manual ? "WhatsApp" : "Sms");
                Guid Confirm(Guid request,bool required=true,string? documentText=null)
                {
                    var preview=CommunicationRecipientResolver.ReadAsync(target).GetAwaiter().GetResult();
                    using var recipients=new DataTable();recipients.Columns.Add("RecipientKind");recipients.Columns.Add("RecordId",typeof(int));recipients.Columns.Add("PhoneE164");
                    foreach(var entry in preview.Entries.Where(e=>e.Included))recipients.Rows.Add("Student",entry.RecordId,entry.PhoneE164);
                    using var members=recipients.Clone();
                    string body=documentText ?? "Reviewed receipt "+number+".";
                    var parameters=new List<SqlParameter>{Sql.P("@UserId",f.Admin),Sql.P("@ClientRequestId",request),Sql.P("@Channel",target.Channel),
                        Sql.P("@Purpose","FeeReminder"),Sql.P("@Body",body),Sql.P("@Audience","FeeStudent"),Sql.P("@CampusId",0),Sql.P("@ClassId",0),
                        Sql.P("@StudentId",student),Sql.P("@StaffId",0),Sql.P("@FinancialReviewRequired",required),
                        new("@Recipients",SqlDbType.Structured){TypeName="msg.RecipientSelection",Value=recipients}};
                    if(manual)parameters.Add(new("@ReviewedFees",SqlDbType.Structured){TypeName="msg.RecipientSelection",Value=members});
                    else parameters.AddRange(new[]{Sql.P("@ProviderCode","Twilio"),Sql.P("@ProfileHash",new string('A',64)),Sql.P("@TemplateJson",null),
                        Sql.P("@ReviewJson",CommunicationReviewedContent.ReviewJson(target,preview,body))});
                    using var result=Sql.Proc(manual ? "msg.ConfirmReviewedManualBatch" : "msg.ConfirmReviewedApiBatch",parameters.ToArray());
                    return (Guid)result.Rows[0]["BatchId"];
                }
                Guid request=Guid.NewGuid();byte[] original=Source();
                int before=Sql.Count("SELECT COUNT(*) FROM msg.Batch"),attempts=Sql.Count("SELECT COUNT(*) FROM msg.Attempt");
                await r.Refused("Receipt messages cannot confirm without their required source proof",()=>{Confirm(request);return Task.CompletedTask;},51330);
                r.Check(Sql.Count("SELECT COUNT(*) FROM msg.Batch")==before,"A missing receipt proof rolls back the whole reviewed confirmation.");
                Reserve(request,original);Reserve(request,original);
                r.Check(Sql.Count("SELECT COUNT(*) FROM msg.Batch")==before && Sql.Count("SELECT COUNT(*) FROM msg.Attempt")==attempts,
                    "Receipt preview reservation is idempotent and never confirms or dispatches communication.");
                Guid batch=Confirm(request);
                r.Check(Confirm(request)==batch && Sql.Count("SELECT COUNT(*) FROM msg.Batch WHERE FinancialReviewKey=@Key",Sql.P("@Key",request))==1,
                    "The exact receipt proof binds atomically to one idempotent reviewed batch.");
                await r.Refused("A receipt approval cannot be downgraded to an unbound message",()=>{Confirm(request,false);return Task.CompletedTask;},51331);
                Guid billRequest=Guid.NewGuid();byte[] billBefore=Source("Bill");Reserve(billRequest,billBefore,"Bill");
                Guid billBatch=Confirm(billRequest,documentText:"Reviewed outstanding balance from the fee bill.");
                r.Check(Sql.Count("SELECT COUNT(*) FROM msg.FinancialReviewProof WHERE ClientRequestId=@Key AND SourceKind='Bill' AND CollectionReceiptId IS NULL",Sql.P("@Key",billRequest))==1,
                    "Bills bind their own reviewed financial state without fabricating a saved receipt.");
                var refund=new[]{Sql.P("@AdmissionId",student),Sql.P("@EnrollmentId",enrollment),Sql.P("@TransactionType","Refund"),Sql.P("@Amount",200m),
                    Sql.P("@TransactionDate",f.PromotionDate),Sql.P("@PaymentMethod",null),Sql.P("@ReferenceNumber",null),Sql.P("@Remarks","Synthetic receipt correction"),
                    Sql.P("@FeePaymentId",paymentId),new SqlParameter("@BillingChargeId",SqlDbType.BigInt){Value=DBNull.Value},
                    Sql.TvpParameter("@Allocations","FeePaymentAllocationType",Tvp.FeeAllocations(Array.Empty<(long,decimal)>()))};
                string scope=await RecoverableMutationWriter.GetScopeAsync();
                var intent=new MutationIntent(Guid.NewGuid(),MutationKind.FeeAdjustment,f.Admin,scope,
                    RecoverableMutationWriter.PayloadHash(MutationKind.FeeAdjustment,f.Admin,refund),"Synthetic receipt correction",DateTime.UtcNow);
                await RecoverableMutationWriter.SaveAsync(intent,refund);
                long recipient=Convert.ToInt64(Sql.Scalar("SELECT RecipientId FROM msg.Recipient WHERE BatchId=@Id",Sql.P("@Id",batch)));
                using var refused=manual
                    ? Sql.Proc("msg.OpenManualHandoff",Sql.P("@UserId",f.Admin),Sql.P("@RecipientId",recipient))
                    : Sql.Proc("msg.ClaimRecipient",Sql.P("@UserId",f.Admin),Sql.P("@RecipientId",recipient),Sql.P("@AttemptKey",Guid.NewGuid()),Sql.P("@ExpectedProviderCode","Twilio"));
                r.Check(!Convert.ToBoolean(refused.Rows[0][manual ? "Opened" : "Claimed"]) && Convert.ToString(refused.Rows[0]["ExclusionCode"])=="SourceChanged",
                    "A receipt changed after review cannot be opened or API-claimed from the saved Queue.");
                long billRecipient=Convert.ToInt64(Sql.Scalar("SELECT RecipientId FROM msg.Recipient WHERE BatchId=@Id",Sql.P("@Id",billBatch)));
                using var staleBill=manual
                    ? Sql.Proc("msg.OpenManualHandoff",Sql.P("@UserId",f.Admin),Sql.P("@RecipientId",billRecipient))
                    : Sql.Proc("msg.ClaimRecipient",Sql.P("@UserId",f.Admin),Sql.P("@RecipientId",billRecipient),Sql.P("@AttemptKey",Guid.NewGuid()),Sql.P("@ExpectedProviderCode","Twilio"));
                r.Check(!Convert.ToBoolean(staleBill.Rows[0][manual ? "Opened" : "Claimed"]) && Convert.ToString(staleBill.Rows[0]["ExclusionCode"])=="SourceChanged",
                    "Saved literal bill text is refused at both manual and API Queue boundaries after the balance changes.");
                await r.Refused("Stale bill evidence cannot be reserved after financial changes",()=>{Reserve(billRequest,billBefore,"Bill");return Task.CompletedTask;},51330);
                byte[] freshBill=Source("Bill");Guid freshBillRequest=Guid.NewGuid();Reserve(freshBillRequest,freshBill,"Bill");
                Guid freshBillBatch=Confirm(freshBillRequest,documentText:"Newly reviewed current fee bill.");
                r.Check(!freshBill.SequenceEqual(billBefore) && Sql.Count("SELECT COUNT(*) FROM msg.PreparedEventEligible(@Id)",Sql.P("@Id",freshBillBatch))==1,
                    "Reviewing the current bill creates a new eligible message without modifying its accounting records.");
                await r.Refused("Stale receipt evidence cannot be reused after a real refund",()=>{Reserve(request,original);return Task.CompletedTask;},51330);
                byte[] corrected=Source();Guid fresh=Guid.NewGuid();Reserve(fresh,corrected);Guid correctedBatch=Confirm(fresh);
                r.Check(!corrected.SequenceEqual(original) && Sql.Count("SELECT COUNT(*) FROM msg.PreparedEventEligible(@Id)",Sql.P("@Id",correctedBatch))==1,
                    "A freshly reviewed already-corrected historical receipt remains shareable without rewriting its original evidence.");
                r.Check(Sql.Count("SELECT COUNT(*) FROM msg.Attempt")==attempts,
                    "Receipt source checks and corrections make no provider requests or fabricated delivery claims.");
                Guid abandoned=Guid.NewGuid();Reserve(abandoned,corrected);
                Sql.Query("UPDATE msg.FinancialReviewProof SET LastCheckedUtc=DATEADD(day,-8,SYSUTCDATETIME()) WHERE ClientRequestId IN (@Unbound,@Bound)",Sql.P("@Unbound",abandoned),Sql.P("@Bound",fresh));
                Reserve(Guid.NewGuid(),corrected);
                r.Check(Sql.Count("SELECT COUNT(*) FROM msg.FinancialReviewProof WHERE ClientRequestId=@Id",Sql.P("@Id",abandoned))==0
                    && Sql.Count("SELECT COUNT(*) FROM msg.FinancialReviewProof WHERE ClientRequestId=@Id",Sql.P("@Id",fresh))==1,
                    "Bounded preview cleanup removes only abandoned unconfirmed proofs and preserves confirmed financial evidence.");
            }
            VerifyBillScope(f,r,student);
        }
        finally
        {
            foreach(DataRow row in channels.Rows)Sql.Query("UPDATE msg.ChannelSettings SET Enabled=@Enabled,ProviderCode=@Provider WHERE Channel=@Channel",
                Sql.P("@Enabled",row["Enabled"]),Sql.P("@Provider",row["ProviderCode"]),Sql.P("@Channel",row["Channel"]));
        }
    }
    private static void VerifyBillScope(Fixture f,Report r,int anchor)
    {
        using var sibling=Sql.Proc("AdmitStudent",f.AdmissionArgs("Bill scope sibling",anchor));
        using var unrelated=Sql.Proc("AdmitStudent",f.AdmissionArgs("Bill scope unrelated"));
        int siblingId=Convert.ToInt32(sibling.Rows[0]["AdmissionId"]),otherId=Convert.ToInt32(unrelated.Rows[0]["AdmissionId"]);
        byte[] Read(bool family)
        {
            using var source=Sql.Proc("msg.ReadFinancialReviewSource",Sql.P("@UserId",f.Admin),Sql.P("@SourceKind","Bill"),
                Sql.P("@AdmissionId",anchor),Sql.P("@WholeFamily",family));
            r.Check(source.Rows.Count==1 && source.Rows[0].IsNull("CollectionReceiptId"),
                "A bill review reads one source for exactly the chosen individual or family scope.");
            return (byte[])source.Rows[0]["SourceHash"];
        }
        byte[] child=Read(false),family=Read(true);
        Sql.Query("UPDATE dbo.Students SET StudentName=N'Bill scope sibling corrected' WHERE AdmissionId=@Id",Sql.P("@Id",siblingId));
        r.Check(Read(false).SequenceEqual(child) && !Read(true).SequenceEqual(family),
            "A sibling record edit invalidates the family bill without invalidating an unchanged individual bill.");
        family=Read(true);
        Sql.Query("UPDATE dbo.Students SET StudentName=N'Bill scope unrelated corrected' WHERE AdmissionId=@Id",Sql.P("@Id",otherId));
        r.Check(Read(false).SequenceEqual(child) && Read(true).SequenceEqual(family),
            "Unrelated school-record changes do not invalidate the selected child or family financial document.");
    }

}
