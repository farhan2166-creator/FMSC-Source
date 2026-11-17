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
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.RoutineFamilyDailyEligible(@Student,SYSUTCDATETIME())",
            Sql.P("@Student",student))==1,
            "A newly admitted family starts below the routine school-day message ceiling.");
        int allowedAfterThree = Sql.Count("""
            SET NOCOUNT ON; SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            DECLARE @Batch uniqueidentifier=NEWID();
            INSERT msg.Batch(BatchId,ClientRequestId,Channel,Purpose,Body,
                Audience,StudentId,State,CreatedBy,ConfirmedBy,ConfirmedUtc)
            VALUES(@Batch,NEWID(),'Sms','Announcement',N'Fixture routine notice',
                'Student',@Student,'Confirmed',@Admin,@Admin,SYSUTCDATETIME());
            INSERT msg.Recipient(BatchId,RecipientKind,AdmissionId,PhoneE164)
            VALUES(@Batch,'Student',@Student,'+923008000077');
            DECLARE @Recipient bigint=CONVERT(bigint,SCOPE_IDENTITY());
            INSERT msg.Attempt(RecipientId,AttemptNumber,AttemptKey,
                Channel,ProviderCode,Outcome)
            VALUES(@Recipient,1,NEWID(),'Sms','Twilio','Unknown'),
                  (@Recipient,2,NEWID(),'Sms','Twilio','Accepted'),
                  (@Recipient,3,NEWID(),'Sms','Twilio','Failed');
            DECLARE @Allowed int=(SELECT COUNT(*) FROM msg.RoutineFamilyDailyEligible(
                @Student,SYSUTCDATETIME()));
            ROLLBACK TRANSACTION;
            SELECT @Allowed;
            """,Sql.P("@Student",student),Sql.P("@Admin",f.Admin));
        r.Check(allowedAfterThree==0 && Sql.Count("SELECT COUNT(*) FROM msg.Attempt")==before,
            "Three routine attempts use the family ceiling, including uncertain and failed submissions; synthetic attempts roll back.");
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
        // Real, owned SQL fixture: scheduling must be inert until its due
        // time, an administrator must be able to pause every API claim, and
        // neither action may create a phantom provider attempt.
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.DispatchControl WHERE ControlId=1 AND MasterPaused=0 AND UnattendedEnabled=0")==1 &&
            Sql.Count("SELECT COUNT(*) FROM msg.AutomationRule WHERE Mode='Off'")==8,
            "New communication automation and background processing start disabled.");
        await r.Refused("Unimplemented automatic event sources cannot be activated by saving a mode",()=>{
            Sql.Proc("msg.SaveAutomationMode",Sql.P("@UserId",f.Admin),
                Sql.P("@EventCode","Admission"),Sql.P("@Mode","Automatic"),
                Sql.P("@CampusId",0),Sql.P("@ClassId",0));
            return Task.CompletedTask;
        },51247);
        var scheduled=Confirm(Guid.NewGuid(),new string('E',64));
        Guid scheduleBatch=(Guid)scheduled.Rows[0]["BatchId"];
        long scheduleRecipient=Convert.ToInt64(Sql.Scalar(
            "SELECT RecipientId FROM msg.Recipient WHERE BatchId=@Batch",
            Sql.P("@Batch",scheduleBatch)));
        DateTime dueUtc=DateTime.UtcNow.AddHours(3);
        Sql.Proc("msg.ScheduleReviewedBatch",
            Sql.P("@UserId",f.Admin),Sql.P("@BatchId",scheduleBatch),
            Sql.P("@DueUtc",dueUtc),Sql.P("@ExpiresUtc",dueUtc.AddHours(24)),
            Sql.P("@Priority","Normal"));
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.BatchSchedule WHERE BatchId=@Batch AND DueUtc>@Now",
            Sql.P("@Batch",scheduleBatch),Sql.P("@Now",DateTime.UtcNow))==1,
            "Scheduling persists a future due time without dispatching a provider request.");
        Sql.Proc("msg.HoldScheduledBatch",
            Sql.P("@UserId",f.Admin),Sql.P("@BatchId",scheduleBatch));
        using (var held=Sql.Proc("msg.ReadTakeoverCandidates",Sql.P("@UserId",f.Admin)))
            r.Check(held.AsEnumerable().Any(row=>(Guid)row["BatchId"]==scheduleBatch),
                "A held, previously reviewed API batch is available for administrator takeover.");
        await r.Refused("Unprivileged staff cannot take over reviewed communication",()=>{
            Sql.Proc("msg.TakeOverScheduledBatch",Sql.P("@UserId",f.Restricted),
                Sql.P("@BatchId",scheduleBatch),Sql.P("@Reason","Synthetic owner review"));
            return Task.CompletedTask;
        },50003);
        await r.Refused("A takeover without a recorded reason cannot release held work",()=>{
            Sql.Proc("msg.TakeOverScheduledBatch",Sql.P("@UserId",f.Admin),
                Sql.P("@BatchId",scheduleBatch),Sql.P("@Reason",""));
            return Task.CompletedTask;
        },51296);
        Sql.Proc("msg.SetMasterPause",Sql.P("@UserId",f.Admin),Sql.P("@Paused",true));
        await r.Refused("Administrator takeover cannot bypass Master Pause",()=>{
            Sql.Proc("msg.TakeOverScheduledBatch",Sql.P("@UserId",f.Admin),
                Sql.P("@BatchId",scheduleBatch),Sql.P("@Reason","Synthetic owner review"));
            return Task.CompletedTask;
        },51296);
        Sql.Proc("msg.SetMasterPause",Sql.P("@UserId",f.Admin),Sql.P("@Paused",false));
        Sql.Proc("msg.TakeOverScheduledBatch",Sql.P("@UserId",f.Admin),
            Sql.P("@BatchId",scheduleBatch),Sql.P("@Reason","Synthetic owner review"));
        r.Check(Sql.Count("""
            SELECT COUNT(*) FROM msg.BatchTakeover T
            JOIN msg.Batch B ON B.BatchId=T.BatchId
            JOIN msg.BatchSchedule S ON S.BatchId=B.BatchId
            WHERE T.BatchId=@Batch AND T.PriorOwnerId=@Actor
              AND T.NewOwnerId=@Actor AND T.ApprovedBy=@Actor
              AND B.CreatedBy=@Actor AND B.ConfirmedBy=@Actor
              AND S.HoldForReview=0
            """,Sql.P("@Batch",scheduleBatch),Sql.P("@Actor",f.Admin))==1
            && Sql.Count("SELECT COUNT(*) FROM msg.Attempt WHERE RecipientId=@Recipient",
                Sql.P("@Recipient",scheduleRecipient))==0,
            "A reasoned administrator takeover preserves original audit identity and releases only unattempted reviewed work.");
        await r.Refused("A scheduled API recipient cannot be claimed before its due time",()=>{
            Sql.Proc("msg.ClaimRecipient",Sql.P("@UserId",f.Admin),
                Sql.P("@RecipientId",scheduleRecipient),Sql.P("@AttemptKey",Guid.NewGuid()),
                Sql.P("@ExpectedProviderCode","Twilio"));
            return Task.CompletedTask;
        },51241);
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.Attempt WHERE RecipientId=@Id",
            Sql.P("@Id",scheduleRecipient))==0,
            "Premature scheduled claims do not write an attempt or spend a provider allowance.");
        Sql.Proc("msg.SetMasterPause",Sql.P("@UserId",f.Admin),Sql.P("@Paused",true));
        await r.Refused("Master pause blocks API claims even on confirmed unscheduled batches",()=>{
            Sql.Proc("msg.ClaimRecipient",Sql.P("@UserId",f.Admin),
                Sql.P("@RecipientId",recipient),Sql.P("@AttemptKey",Guid.NewGuid()),
                Sql.P("@ExpectedProviderCode","Twilio"));
            return Task.CompletedTask;
        },51240);
        Sql.Proc("msg.SetMasterPause",Sql.P("@UserId",f.Admin),Sql.P("@Paused",false));
        Sql.Proc("msg.CancelPendingApiMessages",Sql.P("@UserId",f.Admin),Sql.P("@BatchId",scheduleBatch));
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.Attempt WHERE RecipientId=@Id",
            Sql.P("@Id",scheduleRecipient))==0,
            "Cancelling scheduled unsent work does not create an API request.");

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
        // A correction is a separate, confirmed, unsent batch with a durable
        // original reference. Never update the earlier provider result.
        var correction=Confirm(Guid.NewGuid(),new string('E',64));
        Guid correctedBatch=(Guid)correction.Rows[0]["BatchId"];
        const string correctionReason="Correcting an earlier fictional school notice";
        Sql.Proc("msg.LinkCorrectionBatch",Sql.P("@UserId",f.Admin),
            Sql.P("@OriginalBatchId",batch),Sql.P("@CorrectionBatchId",correctedBatch),
            Sql.P("@Reason",correctionReason));
        Sql.Proc("msg.LinkCorrectionBatch",Sql.P("@UserId",f.Admin),
            Sql.P("@OriginalBatchId",batch),Sql.P("@CorrectionBatchId",correctedBatch),
            Sql.P("@Reason",correctionReason));
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.BatchCorrection WHERE CorrectionBatchId=@New AND OriginalBatchId=@Old",
            Sql.P("@New",correctedBatch),Sql.P("@Old",batch))==1,
            "An approved correction has exactly one durable reference to the original.");
        await r.Refused("A sender without History permission cannot attach a correction",()=>{
            Sql.Proc("msg.LinkCorrectionBatch",Sql.P("@UserId",f.Restricted),
                Sql.P("@OriginalBatchId",batch),Sql.P("@CorrectionBatchId",correctedBatch),
                Sql.P("@Reason",correctionReason));return Task.CompletedTask;
        },50003);
        await r.Refused("A correction retry cannot silently replace its approved reason",()=>{
            Sql.Proc("msg.LinkCorrectionBatch",Sql.P("@UserId",f.Admin),
                Sql.P("@OriginalBatchId",batch),Sql.P("@CorrectionBatchId",correctedBatch),
                Sql.P("@Reason","Different reason on repeated request"));return Task.CompletedTask;
        },51324);
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.Attempt A JOIN msg.Recipient R ON R.RecipientId=A.RecipientId WHERE R.BatchId=@New",
            Sql.P("@New",correctedBatch))==0,
            "Creating a correction audit link never sends or records a provider attempt.");
        Sql.Proc("msg.CancelPendingApiMessages",Sql.P("@UserId",f.Admin),
            Sql.P("@BatchId",correctedBatch));
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
        Sql.Query("UPDATE dbo.Students SET Status=N'Left' WHERE AdmissionId=@Id",Sql.P("@Id",changedSibling));
        await r.Refused("A sibling leaving school is rechecked inside the atomic provider claim",()=>{
            Sql.Proc("msg.ClaimRecipient",Sql.P("@UserId",f.Admin),Sql.P("@RecipientId",feeId),Sql.P("@AttemptKey",Guid.NewGuid()),Sql.P("@ExpectedProviderCode","Twilio"));return Task.CompletedTask;
        },51133);
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.Attempt WHERE RecipientId=@Id",Sql.P("@Id",feeId))==0
            && Sql.Count("SELECT COUNT(*) FROM msg.Recipient WHERE RecipientId=@Id AND State='Pending'",Sql.P("@Id",feeId))==1,
            "A changed shared-guardian fee review rolls back the claim before any external request.");
        Sql.Query("UPDATE dbo.Students SET Status=N'Active' WHERE AdmissionId=@Id",Sql.P("@Id",changedSibling));
        Guid feeAttempt=Guid.NewGuid();
        var validFee=Sql.Proc("msg.ClaimRecipient",Sql.P("@UserId",f.Admin),Sql.P("@RecipientId",feeId),Sql.P("@AttemptKey",feeAttempt),Sql.P("@ExpectedProviderCode","Twilio"));
        r.Check(Convert.ToBoolean(validFee.Rows[0]["Claimed"]),"An unchanged, consented shared-guardian fee review can be claimed.");
        Sql.Proc("msg.RecordProviderResult",Sql.P("@UserId",f.Admin),Sql.P("@AttemptKey",feeAttempt),Sql.P("@Outcome","Failed"),Sql.P("@ProviderMessageId",null),Sql.P("@FailureCode","HTTP400"));
        Sql.Proc("msg.CancelPendingApiMessages",Sql.P("@UserId",f.Admin),Sql.P("@BatchId",feeBatch));
        var withdrawn=Confirm(Guid.NewGuid(),new string('D',64));Guid withdrawnBatch=(Guid)withdrawn.Rows[0]["BatchId"];
        long withdrawnId=Convert.ToInt64(Sql.Scalar("SELECT RecipientId FROM msg.Recipient WHERE BatchId=@Batch",Sql.P("@Batch",withdrawnBatch)));
        Sql.Proc("msg.RecordAdmissionConsent",Sql.P("@UserId",f.Admin),Sql.P("@AdmissionId",student),Sql.P("@Consent",false),Sql.P("@Authority","Students.Admit"));
        // Admission consent is shared across the family. One child withdrawing
        // cannot revoke the separate affirmative consent of a linked sibling.
        var studentTarget=new CommunicationTarget(CommunicationAudience.Student,0,0,student,0,"Sms");
        r.Check((await CommunicationRecipientResolver.ReadAsync(studentTarget)).Included==1,
            "A consenting sibling still authorizes the family's shared guardian destination.");

        // Conversely, a previously confirmed API batch must not be claimed if
        // the family's final affirmative consent is withdrawn before dispatch.
        Sql.Proc("msg.RecordAdmissionConsent",Sql.P("@UserId",f.Admin),Sql.P("@AdmissionId",sibling),Sql.P("@Consent",false),Sql.P("@Authority","Students.Admit"));
        Guid withdrawnAttempt=Guid.NewGuid();
        var excluded=Sql.Proc("msg.ClaimRecipient",Sql.P("@UserId",f.Admin),Sql.P("@RecipientId",withdrawnId),Sql.P("@AttemptKey",withdrawnAttempt),Sql.P("@ExpectedProviderCode","Twilio"));
        r.Check(!Convert.ToBoolean(excluded.Rows[0]["Claimed"])
            && Convert.ToString(excluded.Rows[0]["ExclusionCode"])=="NoLongerEligible"
            && Sql.Count("SELECT COUNT(*) FROM msg.Attempt WHERE RecipientId=@Id",Sql.P("@Id",withdrawnId))==0,
            "With all family consent withdrawn, the confirmed API batch is excluded without a provider attempt.");
        Sql.Proc("msg.RecordAdmissionConsent",Sql.P("@UserId",f.Admin),Sql.P("@AdmissionId",sibling),Sql.P("@Consent",true),Sql.P("@Authority","Students.Admit"));
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
        await CommunicationMetaQueueSqlChecks.Run(f,r,student,sibling,batch);

        // The worker's SQL identity and per-school lease are tested without
        // Windows task registration, real credentials or any provider network.
        Guid enrollment=Guid.NewGuid(),nonce=Guid.NewGuid();
        string syntheticSid="S-1-5-21-101-202-303-404";
        byte[] secretHash=System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("fictional-worker-proof-no-credentials"));
        Sql.Proc("msg.RegisterBackgroundWorker",
            Sql.P("@UserId",f.Admin),Sql.P("@EnrollmentId",enrollment),
            Sql.P("@WindowsSid",syntheticSid),Sql.P("@SecretHash",secretHash));
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.DispatchControl WHERE UnattendedEnabled=0")==1,
            "Enrolling a worker never enables unattended sending without administrator opt-in.");
        await r.Refused("Unprivileged user cannot enable unattended dispatch",()=>{
            Sql.Proc("msg.SetUnattendedSending",Sql.P("@UserId",f.Restricted),Sql.P("@Enabled",true));
            return Task.CompletedTask;
        },50003);
        Sql.Proc("msg.SetUnattendedSending",Sql.P("@UserId",f.Admin),Sql.P("@Enabled",true));
        DataTable verified=Sql.Proc("msg.VerifyBackgroundWorker",
            Sql.P("@UserId",f.Admin),Sql.P("@EnrollmentId",enrollment),
            Sql.P("@WindowsSid",syntheticSid),Sql.P("@SecretHash",secretHash));
        r.Check(verified.Rows.Count==1 && Convert.ToBoolean(verified.Rows[0]["Authorized"]),
            "An authorized enrolled school administrator can verify a bounded headless worker.");
        var lease=Sql.Proc("msg.AcquireBackgroundLease",
            Sql.P("@UserId",f.Admin),Sql.P("@EnrollmentId",enrollment),
            Sql.P("@WindowsSid",syntheticSid),Sql.P("@SecretHash",secretHash),
            Sql.P("@LeaseNonce",nonce));
        r.Check(lease.Rows.Count==1 && Convert.ToBoolean(lease.Rows[0]["Acquired"]),
            "School-wide SQL lease can be acquired by exactly one verified worker.");
        var competed=Sql.Proc("msg.AcquireBackgroundLease",
            Sql.P("@UserId",f.Admin),Sql.P("@EnrollmentId",enrollment),
            Sql.P("@WindowsSid",syntheticSid),Sql.P("@SecretHash",secretHash),
            Sql.P("@LeaseNonce",Guid.NewGuid()));
        r.Check(competed.Rows.Count==1 && !Convert.ToBoolean(competed.Rows[0]["Acquired"]),
            "A second worker cannot acquire an active school lease.");
        await CommunicationWorkerClaimSqlChecks.Run(f,r,enrollment,nonce,syntheticSid,secretHash);
        Sql.Proc("msg.ReleaseBackgroundLease",
            Sql.P("@UserId",f.Admin),Sql.P("@EnrollmentId",enrollment),
            Sql.P("@LeaseNonce",nonce));
        Sql.Proc("msg.SetUnattendedSending",Sql.P("@UserId",f.Admin),Sql.P("@Enabled",false));
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.DispatchControl WHERE UnattendedEnabled=0")==1,
            "Unattended sending can be disabled without any messages being created.");
        var startingMethod=Sql.Proc("msg.ReadPreferredMethod",Sql.P("@UserId",f.Admin));
        r.Check(startingMethod.Rows.Count==1 &&
            Convert.ToString(startingMethod.Rows[0]["PreferredProviderCode"])=="WhatsAppBusinessManual",
            "School preferred method starts as the free manual WhatsApp handoff.");
        Sql.Proc("msg.SavePreferredMethod",Sql.P("@UserId",f.Admin),
            Sql.P("@Channel","Sms"),Sql.P("@ProviderCode","Twilio"));
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.DispatchControl WHERE PreferredChannel='Sms' AND PreferredProviderCode='Twilio'")==1,
            "Administrator can choose SMS as the preferred new-message channel.");
        await r.Refused("Unsupported or cross-channel provider default is rejected",()=>{
            Sql.Proc("msg.SavePreferredMethod",Sql.P("@UserId",f.Admin),
                Sql.P("@Channel","Sms"),Sql.P("@ProviderCode","MetaCloud"));
            return Task.CompletedTask;
        },51276);
        Sql.Proc("msg.SavePreferredMethod",Sql.P("@UserId",f.Admin),
            Sql.P("@Channel","WhatsApp"),Sql.P("@ProviderCode","WhatsAppBusinessManual"));

        var weekdays=Sql.Proc("msg.ReadSchoolWeekdays",Sql.P("@UserId",f.Admin));
        r.Check(weekdays.Rows.Count==7 &&
            weekdays.Rows.Cast<DataRow>().Count(row=>Convert.ToBoolean(row["Enabled"]))>=1,
            "The sending calendar defines seven weekdays and at least one working day.");
        Sql.Proc("msg.SetSchoolWeekday",Sql.P("@UserId",f.Admin),
            Sql.P("@IsoWeekday",7),Sql.P("@Enabled",true));
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.NonWorkingWeekday WHERE IsoWeekday=7 AND Enabled=1")==1,
            "Administrator can make Sunday a school sending day.");
        Sql.Proc("msg.SetSchoolWeekday",Sql.P("@UserId",f.Admin),
            Sql.P("@IsoWeekday",7),Sql.P("@Enabled",false));
        var holidayDate=new DateTime(2027,2,15);
        Sql.Proc("msg.SetSchoolHoliday",Sql.P("@UserId",f.Admin),
            Sql.P("@HolidayDate",holidayDate),Sql.P("@Description","Synthetic school closure"),
            Sql.P("@Remove",false));
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.SchoolHoliday WHERE HolidayDate=@Date",
            Sql.P("@Date",holidayDate))==1,
            "School holiday date is stored for independent worker and SQL-claim checks.");
        Sql.Proc("msg.SetSchoolHoliday",Sql.P("@UserId",f.Admin),
            Sql.P("@HolidayDate",holidayDate),Sql.P("@Description",DBNull.Value),
            Sql.P("@Remove",true));
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.SchoolHoliday WHERE HolidayDate=@Date",
            Sql.P("@Date",holidayDate))==0,
            "Removing a holiday does not change school messages or any financial records.");
        await CommunicationAutomationSqlChecks.Run(f,r);
        await CommunicationDispatchBoundarySqlChecks.Run(f,r);
        await CommunicationFamilyFeeSqlChecks.Run(f,r);
        await CommunicationFinancialSourceSqlChecks.Run(f,r);
        await CommunicationFeeReviewSqlChecks.Run(f,r);

    }
}
