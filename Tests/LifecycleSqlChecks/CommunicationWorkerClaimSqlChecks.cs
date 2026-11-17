using System.Data;
using Microsoft.Data.SqlClient;

internal static class CommunicationWorkerClaimSqlChecks
{
    internal static async Task Run(Fixture f,Report r,Guid enrollment,Guid nonce,string sid,byte[] secretHash)
    {
        using var prior=Sql.Query("SELECT ProviderCode,Enabled FROM msg.ChannelSettings WHERE Channel='Sms'");
        try
        {
        Sql.Proc("msg.EnableApiChannel",Sql.P("@UserId",f.Admin),Sql.P("@Channel","Sms"),Sql.P("@ProviderCode","Twilio"));
        var args=f.AdmissionArgs("Fictional attempt actor");
        args.Single(parameter=>parameter.ParameterName=="@ContactNumber").Value="03008000422";
        using var admitted=Sql.Proc("AdmitStudent",args);
        int student=Convert.ToInt32(admitted.Rows[0]["AdmissionId"]);
        Sql.Proc("msg.RecordAdmissionConsent",Sql.P("@UserId",f.Admin),Sql.P("@AdmissionId",student),Sql.P("@Consent",true),Sql.P("@Authority","Students.Admit"));
        async Task<long> Confirm(string body)
        {
            var target=new CommunicationTarget(CommunicationAudience.Student,0,0,student,0,"Sms");
            var preview=await CommunicationRecipientResolver.ReadAsync(target);
            using var recipients=new DataTable();recipients.Columns.Add("RecipientKind");recipients.Columns.Add("RecordId",typeof(int));recipients.Columns.Add("PhoneE164");
            foreach(var entry in preview.Entries.Where(entry=>entry.Included))recipients.Rows.Add(entry.Kind,entry.RecordId,entry.PhoneE164);
            r.Check(recipients.Rows.Count==1,"Actor verification resolves one actual consented record in an isolated family.");
            using var saved=Sql.Proc("msg.ConfirmReviewedApiBatch",Sql.P("@UserId",f.Admin),Sql.P("@ClientRequestId",Guid.NewGuid()),
                Sql.P("@Channel","Sms"),Sql.P("@Purpose","Individual"),Sql.P("@Body",body),Sql.P("@Audience","Student"),
                Sql.P("@CampusId",0),Sql.P("@ClassId",0),Sql.P("@StudentId",student),Sql.P("@StaffId",0),
                new SqlParameter("@Recipients",SqlDbType.Structured){TypeName="msg.RecipientSelection",Value=recipients},
                Sql.P("@ProviderCode","Twilio"),Sql.P("@ProfileHash",new string('A',64)),
                Sql.P("@ReviewJson",CommunicationReviewedContent.ReviewJson(target,preview,body)),Sql.P("@TemplateJson",null));
            return Convert.ToInt64(Sql.Scalar("SELECT RecipientId FROM msg.Recipient WHERE BatchId=@Id",Sql.P("@Id",saved.Rows[0]["BatchId"])));
        }
        long pending=await Confirm("Synthetic enrolled processor notice.");
        DataTable Claim(Guid lease,byte[] secret,bool worker=true,int? actor=null)=>Sql.Proc("msg.ClaimRecipient",Sql.P("@UserId",actor ?? f.Admin),
            Sql.P("@RecipientId",pending),Sql.P("@AttemptKey",Guid.NewGuid()),Sql.P("@ExpectedProviderCode","Twilio"),Sql.P("@IsWorker",worker),
            Sql.P("@EnrollmentId",enrollment),Sql.P("@WindowsSid",sid),Sql.P("@SecretHash",secret),Sql.P("@LeaseNonce",lease));
        int before=Sql.Count("SELECT COUNT(*) FROM msg.Attempt");
        await r.Refused("A processor cannot reserve an attempt using forged enrollment proof",()=>{Claim(nonce,new byte[32]);return Task.CompletedTask;},51261);
        await r.Refused("A processor cannot reserve after losing its invocation lease",()=>{Claim(Guid.NewGuid(),secretHash);return Task.CompletedTask;},51263);
        await r.Refused("Worker proof cannot silently become an interactive claim",()=>{Claim(nonce,secretHash,false);return Task.CompletedTask;},51261);
        await r.Refused("An unattended claim requires complete proof at reservation",()=>{
            Sql.Proc("msg.ClaimRecipient",Sql.P("@UserId",f.Admin),Sql.P("@RecipientId",pending),Sql.P("@AttemptKey",Guid.NewGuid()),
                Sql.P("@ExpectedProviderCode","Twilio"),Sql.P("@IsWorker",true));return Task.CompletedTask;
        },51261);
        Sql.Query("UPDATE msg.BackgroundWorkerLease SET LeaseUntilUtc=DATEADD(second,-1,SYSUTCDATETIME()) WHERE EnrollmentId=@Id AND LeaseNonce=@Nonce",
            Sql.P("@Id",enrollment),Sql.P("@Nonce",nonce));
        await r.Refused("A previously verified processor cannot reserve after its lease expires",()=>{Claim(nonce,secretHash);return Task.CompletedTask;},51263);
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.Attempt")==before,"Invalid or expired worker proof leaves no provider attempt or consumed recipient.");
        using var acquired=Sql.Proc("msg.AcquireBackgroundLease",Sql.P("@UserId",f.Admin),Sql.P("@EnrollmentId",enrollment),
            Sql.P("@WindowsSid",sid),Sql.P("@SecretHash",secretHash),Sql.P("@LeaseNonce",nonce));
        r.Check(Convert.ToBoolean(acquired.Rows[0]["Acquired"]),"Current independent authorization can reacquire the expired school lease.");
        using var workerClaim=Claim(nonce,secretHash);
        r.Check(Convert.ToBoolean(workerClaim.Rows[0]["Claimed"]),"An enrolled processor with a current lease reserves exactly one attempt.");
        long attempt=Convert.ToInt64(workerClaim.Rows[0]["AttemptId"]);
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.Attempt WHERE AttemptId=@Id AND ClaimedBy=@User AND ClaimKind='Worker' AND WorkerEnrollmentId=@Enrollment",
            Sql.P("@Id",attempt),Sql.P("@User",f.Admin),Sql.P("@Enrollment",enrollment))==1,
            "Worker attempt evidence identifies its enrolled authority without claiming a staff member manually sent it.");
        await r.Refused("Later status updates cannot rewrite the actual attempt actor",()=>{
            Sql.Query("UPDATE msg.Attempt SET ClaimedBy=@Other WHERE AttemptId=@Id",Sql.P("@Other",f.Restricted),Sql.P("@Id",attempt));return Task.CompletedTask;
        },51344);
        Sql.Proc("msg.RecordProviderResult",Sql.P("@UserId",f.Admin),Sql.P("@AttemptKey",workerClaim.Rows[0]["AttemptKey"]),
            Sql.P("@Outcome","Accepted"),Sql.P("@ProviderMessageId","SM"+Guid.NewGuid().ToString("N")),Sql.P("@FailureCode",null));
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.Attempt WHERE AttemptId=@Id AND ClaimedBy=@User AND ClaimKind='Worker' AND Outcome='Accepted'",
            Sql.P("@Id",attempt),Sql.P("@User",f.Admin))==1,"A provider response preserves original attempt identity and does not imply delivered.");
        await r.Refused("Existing enrollment cannot be rebound to another restore boundary",()=>{
            Sql.Query("UPDATE msg.BackgroundWorkerEnrollment SET RestoreBoundaryHash=HASHBYTES('SHA2_256',N'changed') WHERE EnrollmentId=@Id",Sql.P("@Id",enrollment));return Task.CompletedTask;
        },51344);
        int secondary=Sql.Count("""
            INSERT dbo.Users(Username,PasswordHash,PasswordSalt,RoleId,MustChangePassword)
            SELECT N'msg_secondary_admin_fixture',PasswordHash,PasswordSalt,
             (SELECT RoleId FROM dbo.Roles WHERE RoleName=N'Accountant'),0 FROM dbo.Users WHERE UserId=@Admin;
            DECLARE @User int=CONVERT(int,SCOPE_IDENTITY());
            INSERT dbo.UserRoles(UserId,RoleId) SELECT @User,RoleId FROM dbo.Roles WHERE RoleName IN(N'Accountant',N'Administrator');
            SELECT @User;
            """,Sql.P("@Admin",f.Admin));
        Guid secondaryEnrollment=Guid.NewGuid();
        const string secondarySid="S-1-5-21-101-202-303-405";
        Sql.Proc("msg.RegisterBackgroundWorker",Sql.P("@UserId",secondary),Sql.P("@EnrollmentId",secondaryEnrollment),
            Sql.P("@WindowsSid",secondarySid),Sql.P("@SecretHash",secretHash));
        Sql.Proc("msg.SetUnattendedSending",Sql.P("@UserId",f.Admin),Sql.P("@Enabled",true));
        using var verified=Sql.Proc("msg.VerifyBackgroundWorker",Sql.P("@UserId",secondary),Sql.P("@EnrollmentId",secondaryEnrollment),
            Sql.P("@WindowsSid",secondarySid),Sql.P("@SecretHash",secretHash));
        r.Check(Convert.ToBoolean(verified.Rows[0]["Authorized"]),"Secondary Administrator membership follows the authoritative school permission policy for worker enrollment.");
        long interactive=await Confirm("Synthetic interactive operator notice.");
        using var interactiveClaim=Sql.Proc("msg.ClaimRecipient",Sql.P("@UserId",secondary),Sql.P("@RecipientId",interactive),
            Sql.P("@AttemptKey",Guid.NewGuid()),Sql.P("@ExpectedProviderCode","Twilio"));
        r.Check(Convert.ToBoolean(interactiveClaim.Rows[0]["Claimed"]) && Sql.Count("SELECT COUNT(*) FROM msg.Attempt WHERE AttemptId=@Id AND ClaimedBy=@User AND ClaimKind='Interactive' AND WorkerEnrollmentId IS NULL",
            Sql.P("@Id",interactiveClaim.Rows[0]["AttemptId"]),Sql.P("@User",secondary))==1,
            "Interactive history records the actual authorized operator rather than copying the batch approver.");
        Sql.Query("DELETE UR FROM dbo.UserRoles UR JOIN dbo.Roles R ON R.RoleId=UR.RoleId WHERE UR.UserId=@User AND R.RoleName=N'Administrator'",Sql.P("@User",secondary));
        await r.Refused("Revoked secondary administrator authority cannot keep an enrolled processor alive",()=>{
            Sql.Proc("msg.VerifyBackgroundWorker",Sql.P("@UserId",secondary),Sql.P("@EnrollmentId",secondaryEnrollment),
                Sql.P("@WindowsSid",secondarySid),Sql.P("@SecretHash",secretHash));return Task.CompletedTask;
        },50003);
        }
        finally
        {
            foreach(DataRow setting in prior.Rows)
                Sql.Query("UPDATE msg.ChannelSettings SET ProviderCode=@Provider,Enabled=@Enabled WHERE Channel='Sms'",
                    Sql.P("@Provider",setting["ProviderCode"]),Sql.P("@Enabled",setting["Enabled"]));
        }
    }
}
