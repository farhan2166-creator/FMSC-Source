using Microsoft.Data.SqlClient;
using System.Text.Json;

// Real SQL on the runner-owned disposable school. No credential or provider POST.
internal static class CommunicationApiSqlChecks
{
    internal static async Task Run(Fixture f,Report r)
    {
        int before=Sql.Count("SELECT COUNT(*) FROM msg.Attempt");
        Sql.Proc("msg.ConfigureChannel",Sql.P("@UserId",f.Admin),Sql.P("@Channel","Sms"),Sql.P("@ProviderCode","Twilio"));
        await r.Refused("Provider activation requires configuration permission",()=>{
            Sql.Proc("msg.EnableApiChannel",Sql.P("@UserId",f.Restricted),Sql.P("@Channel","Sms"),Sql.P("@ProviderCode","Twilio"));return Task.CompletedTask;
        },50003);
        Sql.Proc("msg.EnableApiChannel",Sql.P("@UserId",f.Admin),Sql.P("@Channel","Sms"),Sql.P("@ProviderCode","Twilio"));
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.Attempt")==before,"Enabling SMS never creates a send or provider attempt.");
        var args=f.AdmissionArgs("API fictional student");
        args.Single(p=>p.ParameterName=="@ContactNumber").Value="03008000077";
        int student=Convert.ToInt32(Sql.Proc("AdmitStudent",args).Rows[0]["AdmissionId"]);
        Sql.Proc("msg.RecordAdmissionConsent",Sql.P("@UserId",f.Admin),Sql.P("@AdmissionId",student),Sql.P("@Consent",true),Sql.P("@Authority","Students.Admit"));
        using var recipients=new DataTable();recipients.Columns.Add("RecipientKind");recipients.Columns.Add("RecordId",typeof(int));recipients.Columns.Add("PhoneE164");
        recipients.Rows.Add("Student",student,"+923008000077");
        string Review(string hash)=>JsonSerializer.Serialize(new[]{new {Kind="Student",RecordId=student,Phone="+923008000077",Hash=hash,Text="Hello API fictional student"}});
        DataTable Confirm(Guid key,string hash,string? review=null,int? actor=null)=>Sql.Proc("msg.ConfirmReviewedApiBatch",
            Sql.P("@UserId",actor ?? f.Admin),Sql.P("@ClientRequestId",key),Sql.P("@Channel","Sms"),Sql.P("@Purpose","Individual"),
            Sql.P("@Body","Hello [StudentName]"),Sql.P("@Audience","Student"),Sql.P("@CampusId",0),Sql.P("@ClassId",0),
            Sql.P("@StudentId",student),Sql.P("@StaffId",0),new SqlParameter("@Recipients",SqlDbType.Structured){TypeName="msg.RecipientSelection",Value=recipients},
            Sql.P("@ProviderCode","Twilio"),Sql.P("@ProfileHash",new string('A',64)),Sql.P("@ReviewJson",review ?? Review(hash)),Sql.P("@TemplateJson",null));
        Guid key=Guid.NewGuid();
        await r.Refused("Unprivileged caller cannot confirm a reviewed API batch",()=>{Confirm(key,new string('B',64),actor:f.Restricted);return Task.CompletedTask;},50003);
        var saved=Confirm(key,new string('B',64));Guid batch=(Guid)saved.Rows[0]["BatchId"];
        r.Check(!Convert.ToBoolean(saved.Rows[0]["AlreadyConfirmed"]) && Convert.ToInt64(saved.Rows[0]["RecipientCount"])==1
            && Sql.Count("SELECT COUNT(*) FROM msg.ApiBatch WHERE BatchId=@Batch",Sql.P("@Batch",batch))==1,"SQL atomically saves the API provider and exact recipient review.");
        var repeated=Confirm(key,new string('B',64));
        r.Check(Convert.ToBoolean(repeated.Rows[0]["AlreadyConfirmed"]) && (Guid)repeated.Rows[0]["BatchId"]==batch,"Retrying the confirmation acknowledgement does not create another batch.");
        await r.Refused("An idempotency key cannot silently change reviewed content",()=>{Confirm(key,new string('C',64));return Task.CompletedTask;},51131);
        await r.Refused("API review must contain the exact SQL-validated destinations",()=>{Confirm(Guid.NewGuid(),new string('B',64),"[]");return Task.CompletedTask;},51131);
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.Attempt")==before,"Reviewing and confirming do not dispatch automatically.");
        long recipient=Convert.ToInt64(Sql.Scalar("SELECT RecipientId FROM msg.Recipient WHERE BatchId=@Batch",Sql.P("@Batch",batch)));
        var read=Sql.Proc("msg.ReadApiBatch",Sql.P("@UserId",f.Admin),Sql.P("@BatchId",batch));
        r.Check(read.Rows.Count==1 && Convert.ToString(read.Rows[0]["Body"])=="Hello [StudentName]"
            && Convert.ToString(read.Rows[0]["State"])=="Pending","Resuming reads the saved message format and pending state without sending.");
        await r.Refused("API history content is permission controlled",()=>{Sql.Proc("msg.ReadApiBatch",Sql.P("@UserId",f.Restricted),Sql.P("@BatchId",batch));return Task.CompletedTask;},50003);
        await r.Refused("A reviewed API batch cannot be converted into manual WhatsApp",()=>{
            Sql.Query("INSERT msg.ManualBatch(BatchId,CreatedBy) VALUES(@Batch,@User)",Sql.P("@Batch",batch),Sql.P("@User",f.Admin));return Task.CompletedTask;
        },51131);
        var attempt=Guid.NewGuid();
        var claim=Sql.Proc("msg.ClaimRecipient",Sql.P("@UserId",f.Admin),Sql.P("@RecipientId",recipient),Sql.P("@AttemptKey",attempt),Sql.P("@ExpectedProviderCode","Twilio"));
        r.Check(Convert.ToBoolean(claim.Rows[0]["Claimed"]) && Sql.Count("SELECT COUNT(*) FROM msg.Recipient WHERE RecipientId=@Id AND State='Unknown'",Sql.P("@Id",recipient))==1,
            "SQL persists uncertainty before the external request can happen.");
        await r.Refused("An uncertain API attempt cannot be claimed twice",()=>{
            Sql.Proc("msg.ClaimRecipient",Sql.P("@UserId",f.Admin),Sql.P("@RecipientId",recipient),Sql.P("@AttemptKey",Guid.NewGuid()),Sql.P("@ExpectedProviderCode","Twilio"));return Task.CompletedTask;
        },51093);
        Sql.Proc("msg.CancelPendingApiMessages",Sql.P("@UserId",f.Admin),Sql.P("@BatchId",batch));
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.Recipient WHERE RecipientId=@Id AND State='Unknown'",Sql.P("@Id",recipient))==1,"Cancel pending cannot erase an uncertain attempted message.");
        Sql.Proc("msg.RecordProviderResult",Sql.P("@UserId",f.Admin),Sql.P("@AttemptKey",attempt),Sql.P("@Outcome","Accepted"),Sql.P("@ProviderMessageId","SM"+new string('c',32)),Sql.P("@FailureCode",null));
        var history=Sql.Proc("msg.ReadHistory",Sql.P("@UserId",f.Admin)).AsEnumerable().Single(row=>(Guid)row["BatchId"]==batch);
        r.Check(Convert.ToInt32(history["Accepted"])==1 && Convert.ToInt32(history["Delivered"])==0 && Convert.ToInt32(history["InProgress"])==0,
            "History separates API acceptance from delivery and unsent work.");
        var cancel=Confirm(Guid.NewGuid(),new string('D',64));Guid cancelBatch=(Guid)cancel.Rows[0]["BatchId"];
        Sql.Proc("msg.CancelPendingApiMessages",Sql.P("@UserId",f.Admin),Sql.P("@BatchId",cancelBatch));
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.Recipient WHERE BatchId=@Batch AND State='Excluded' AND ExclusionCode='OperatorCancelled'",Sql.P("@Batch",cancelBatch))==1,
            "Cancelling unsent API messages preserves the reviewed batch and records exclusion.");
        // A shared-number fee review pins all current children and balances,
        // not just the one representative selected by destination deduplication.
        var siblingArgs=f.AdmissionArgs("API fictional sibling",student);
        siblingArgs.Single(p=>p.ParameterName=="@ContactNumber").Value="03008000077";
        int sibling=Convert.ToInt32(Sql.Proc("AdmitStudent",siblingArgs).Rows[0]["AdmissionId"]);
        Sql.Proc("msg.RecordAdmissionConsent",Sql.P("@UserId",f.Admin),Sql.P("@AdmissionId",sibling),Sql.P("@Consent",true),Sql.P("@Authority","Students.Admit"));
        var feeTarget=new CommunicationTarget(CommunicationAudience.OutstandingFees,f.Campus,f.Class6,0,0,"Sms");
        var feePreview=await CommunicationRecipientResolver.ReadAsync(feeTarget);
        string feeReview=CommunicationReviewedContent.ReviewJson(feeTarget,feePreview,"[StudentName] [OutstandingAmount]");
        using var feeRecipients=recipients.Clone();
        foreach(var item in feePreview.Entries.Where(e=>e.Included))feeRecipients.Rows.Add(item.Kind,item.RecordId,item.PhoneE164);
        var fees=Sql.Proc("msg.ConfirmReviewedApiBatch",Sql.P("@UserId",f.Admin),Sql.P("@ClientRequestId",Guid.NewGuid()),
            Sql.P("@Channel","Sms"),Sql.P("@Purpose","FeeReminder"),Sql.P("@Body","[StudentName] [OutstandingAmount]"),
            Sql.P("@Audience","OutstandingFees"),Sql.P("@CampusId",f.Campus),Sql.P("@ClassId",f.Class6),Sql.P("@StudentId",0),Sql.P("@StaffId",0),
            new SqlParameter("@Recipients",SqlDbType.Structured){TypeName="msg.RecipientSelection",Value=feeRecipients},
            Sql.P("@ProviderCode","Twilio"),Sql.P("@ProfileHash",new string('A',64)),Sql.P("@ReviewJson",feeReview),Sql.P("@TemplateJson",null));
        Guid feeBatch=(Guid)fees.Rows[0]["BatchId"];
        long feeId=Convert.ToInt64(Sql.Scalar("SELECT RecipientId FROM msg.Recipient WHERE BatchId=@Batch AND PhoneE164='+923008000077'",Sql.P("@Batch",feeBatch)));
        int feeRepresentative=Sql.Count("SELECT AdmissionId FROM msg.Recipient WHERE RecipientId=@Id",Sql.P("@Id",feeId));
        int changedSibling=feeRepresentative==student ? sibling : student;
        Sql.Proc("msg.RecordAdmissionConsent",Sql.P("@UserId",f.Admin),Sql.P("@AdmissionId",changedSibling),Sql.P("@Consent",false),Sql.P("@Authority","Students.Admit"));
        await r.Refused("Withdrawn sibling consent is rechecked inside the atomic provider claim",()=>{
            Sql.Proc("msg.ClaimRecipient",Sql.P("@UserId",f.Admin),Sql.P("@RecipientId",feeId),Sql.P("@AttemptKey",Guid.NewGuid()),Sql.P("@ExpectedProviderCode","Twilio"));return Task.CompletedTask;
        },51133);
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.Attempt WHERE RecipientId=@Id",Sql.P("@Id",feeId))==0
            && Sql.Count("SELECT COUNT(*) FROM msg.Recipient WHERE RecipientId=@Id AND State='Pending'",Sql.P("@Id",feeId))==1,
            "A changed shared-guardian fee review rolls back the claim before any external request.");
        Sql.Proc("msg.RecordAdmissionConsent",Sql.P("@UserId",f.Admin),Sql.P("@AdmissionId",changedSibling),Sql.P("@Consent",true),Sql.P("@Authority","Students.Admit"));
        Guid feeAttempt=Guid.NewGuid();
        var validFee=Sql.Proc("msg.ClaimRecipient",Sql.P("@UserId",f.Admin),Sql.P("@RecipientId",feeId),Sql.P("@AttemptKey",feeAttempt),Sql.P("@ExpectedProviderCode","Twilio"));
        r.Check(Convert.ToBoolean(validFee.Rows[0]["Claimed"]),"An unchanged, consented shared-guardian fee review can be claimed.");
        Sql.Proc("msg.RecordProviderResult",Sql.P("@UserId",f.Admin),Sql.P("@AttemptKey",feeAttempt),Sql.P("@Outcome","Failed"),Sql.P("@ProviderMessageId",null),Sql.P("@FailureCode","HTTP400"));
        Sql.Proc("msg.CancelPendingApiMessages",Sql.P("@UserId",f.Admin),Sql.P("@BatchId",feeBatch));
        var withdrawn=Confirm(Guid.NewGuid(),new string('D',64));Guid withdrawnBatch=(Guid)withdrawn.Rows[0]["BatchId"];
        long withdrawnId=Convert.ToInt64(Sql.Scalar("SELECT RecipientId FROM msg.Recipient WHERE BatchId=@Batch",Sql.P("@Batch",withdrawnBatch)));
        Sql.Proc("msg.RecordAdmissionConsent",Sql.P("@UserId",f.Admin),Sql.P("@AdmissionId",student),Sql.P("@Consent",false),Sql.P("@Authority","Students.Admit"));
        var excluded=Sql.Proc("msg.ClaimRecipient",Sql.P("@UserId",f.Admin),Sql.P("@RecipientId",withdrawnId),Sql.P("@AttemptKey",Guid.NewGuid()),Sql.P("@ExpectedProviderCode","Twilio"));
        r.Check(!Convert.ToBoolean(excluded.Rows[0]["Claimed"]),"Admission consent withdrawal remains authoritative after API confirmation.");
        Sql.Proc("msg.PauseChannel",Sql.P("@UserId",f.Admin),Sql.P("@Channel","Sms"));
        var smsTarget=new CommunicationTarget(CommunicationAudience.Student,0,0,sibling,0,"Sms");
        r.Check((await CommunicationRecipientResolver.ReadAsync(smsTarget)).Included==1,"Consenting sibling is independently eligible before an SMS opt-out.");
        await r.Refused("SMS opt-outs require communication configuration permission",()=>{
            Sql.Proc("msg.RecordChannelOptOut",Sql.P("@UserId",f.Restricted),Sql.P("@Channel","Sms"),Sql.P("@Phone","03008000077"));return Task.CompletedTask;
        },50003);
        Sql.Proc("msg.RecordChannelOptOut",Sql.P("@UserId",f.Admin),Sql.P("@Channel","Sms"),Sql.P("@Phone","03008000077"));
        Sql.Proc("msg.RecordChannelOptOut",Sql.P("@UserId",f.Admin),Sql.P("@Channel","Sms"),Sql.P("@Phone","03008000077"));
        r.Check((await CommunicationRecipientResolver.ReadAsync(smsTarget)).Included==0
            && (await CommunicationRecipientResolver.ReadAsync(smsTarget with {Channel="WhatsApp"})).Included==1,
            "SMS opt-out is idempotent, applies to the shared number and does not invent a WhatsApp opt-out.");
        Sql.Proc("msg.ConfigureChannel",Sql.P("@UserId",f.Admin),Sql.P("@Channel","WhatsApp"),Sql.P("@ProviderCode","MetaCloud"));
        await r.Refused("Switching to Meta cannot reinterpret unfinished manual chats as API messages",()=>{
            Sql.Proc("msg.EnableApiChannel",Sql.P("@UserId",f.Admin),Sql.P("@Channel","WhatsApp"),Sql.P("@ProviderCode","MetaCloud"));return Task.CompletedTask;
        },51130);
        Sql.Proc("msg.ConfigureChannel",Sql.P("@UserId",f.Admin),Sql.P("@Channel","WhatsApp"),Sql.P("@ProviderCode","WhatsAppBusinessManual"));
    }
}
