using System.Data;
using System.Text.Json;

// Queue-selection tests on owned SQL rows. No OS enrollment or provider requests.
internal static class CommunicationWorkerQueueSqlChecks
{
    internal static Task Run(Fixture f,Report r,int student)
    {
        using var control=Sql.Query("SELECT WindowStart,WindowEnd,TimeZoneId,MasterPaused,UnattendedEnabled FROM msg.DispatchControl WHERE ControlId=1");
        using var days=Sql.Query("SELECT IsoWeekday,Enabled FROM msg.NonWorkingWeekday");
        using var channels=Sql.Query("SELECT Channel,ProviderCode,Enabled FROM msg.ChannelSettings");
        var batches=new List<Guid>();
        var enrollment=Guid.NewGuid();var nonce=Guid.NewGuid();const string sid="S-1-5-21-101-202-303-405";
        var proof=System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("synthetic-queue-isolation"));
        bool acquired=false;
        try
        {
            Sql.Proc("msg.RegisterBackgroundWorker",Sql.P("@UserId",f.Admin),Sql.P("@EnrollmentId",enrollment),
                Sql.P("@WindowsSid",sid),Sql.P("@SecretHash",proof));
            Sql.Proc("msg.SetUnattendedSending",Sql.P("@UserId",f.Admin),Sql.P("@Enabled",true));
            using(var lease=Sql.Proc("msg.AcquireBackgroundLease",Sql.P("@UserId",f.Admin),Sql.P("@EnrollmentId",enrollment),
                Sql.P("@WindowsSid",sid),Sql.P("@SecretHash",proof),Sql.P("@LeaseNonce",nonce)))
                acquired=Convert.ToBoolean(lease.Rows[0]["Acquired"]);
            r.Check(acquired,"A verified disposable worker owns the queue-selection lease.");
            // Open only the owned fixture's calendar to make assertions independent of CI time/day.
            Sql.Query("UPDATE msg.DispatchControl SET WindowStart='00:00',WindowEnd='23:59:59',TimeZoneId='UTC',MasterPaused=0 WHERE ControlId=1; UPDATE msg.NonWorkingWeekday SET Enabled=1;");
            Guid Seed(string channel,string provider)
            {
                Guid batch=Guid.NewGuid();batches.Add(batch);
                Sql.Query("""
                    INSERT msg.Batch(BatchId,ClientRequestId,Channel,Purpose,Body,Audience,State,CreatedBy,ConfirmedBy,ConfirmedUtc,StudentId)
                    VALUES(@Batch,@Request,@Channel,'Individual',N'Synthetic queue selection.','Student','Confirmed',@User,@User,SYSUTCDATETIME(),@Student);
                    INSERT msg.ApiBatch(BatchId,ProviderCode,ProfileHash,ReviewJson,TemplateJson)
                    VALUES(@Batch,@Provider,@Hash,N'[]',CASE WHEN @Provider='MetaCloud' THEN N'{}' END);
                    INSERT msg.Recipient(BatchId,RecipientKind,AdmissionId,PhoneE164)
                    VALUES(@Batch,'Student',@Student,'+923008000351');
                    INSERT msg.BatchSchedule(BatchId,DueUtc,ExpiresUtc,Priority,HoldForReview,UpdatedBy)
                    VALUES(@Batch,DATEADD(minute,-1,SYSUTCDATETIME()),DATEADD(hour,1,SYSUTCDATETIME()),'Normal',0,@User);
                    """,Sql.P("@Batch",batch),Sql.P("@Request",Guid.NewGuid()),Sql.P("@Channel",channel),
                    Sql.P("@User",f.Admin),Sql.P("@Student",student),Sql.P("@Provider",provider),Sql.P("@Hash",new string('A',64)));
                return batch;
            }
            Guid sms=Seed("Sms","Twilio"),whatsApp=Seed("WhatsApp","MetaCloud");
            Sql.Query("UPDATE msg.ChannelSettings SET Enabled=1,ProviderCode=CASE Channel WHEN 'Sms' THEN 'Twilio' ELSE 'MetaCloud' END WHERE Channel IN ('Sms','WhatsApp')");
            DataTable Read(bool skipSms=false,bool skipWhatsApp=false,string deferred="[]") =>
                Sql.Proc("msg.ReadNextScheduledRecipient",Sql.P("@UserId",f.Admin),Sql.P("@EnrollmentId",enrollment),
                    Sql.P("@LeaseNonce",nonce),Sql.P("@SkipSms",skipSms),Sql.P("@SkipWhatsApp",skipWhatsApp),Sql.P("@DeferredBatches",deferred));
            using(var next=Read(skipSms:true))
                r.Check(next.Rows.Count==1 && (Guid)next.Rows[0]["BatchId"]==whatsApp,
                    "A throttled SMS channel cannot starve a healthy WhatsApp queue.");
            using(var next=Read(skipWhatsApp:true))
                r.Check(next.Rows.Count==1 && (Guid)next.Rows[0]["BatchId"]==sms,
                    "A throttled WhatsApp channel cannot starve a healthy SMS queue.");
            Sql.Query("UPDATE msg.ChannelSettings SET Enabled=0 WHERE Channel='Sms'");
            using(var next=Read())
                r.Check(next.Rows.Count==1 && (Guid)next.Rows[0]["BatchId"]==whatsApp,
                    "A paused channel is excluded before selecting the next scheduled recipient.");
            using(var next=Read(deferred:JsonSerializer.Serialize(new[]{whatsApp})))
                r.Check(next.Rows.Count==0,"Deferring a family-limited batch leaves its pending recipient untouched until a later pass.");
            Sql.Query("UPDATE msg.DispatchControl SET TimeZoneId='Pakistan Standard Time' WHERE ControlId=1");
            using(var shown=Sql.Proc("msg.ReadMyUnfinishedBatches",Sql.P("@UserId",f.Admin)))
            {
                var scheduled=shown.AsEnumerable().Single(row=>(Guid)row["BatchId"]==sms);
                r.Check(Convert.ToDateTime(scheduled["DueSchool"])==Convert.ToDateTime(scheduled["DueUtc"]).AddHours(5),
                    "Queue due dates use the configured school timezone instead of the operator computer timezone.");
            }
            r.Check(Sql.Count("SELECT COUNT(*) FROM msg.Recipient WHERE BatchId IN (@First,@Second) AND State='Pending'",
                Sql.P("@First",sms),Sql.P("@Second",whatsApp))==2,
                "Queue selection never claims, retries, cancels or submits a recipient.");
        }
        finally
        {
            foreach(Guid batch in batches)
                Sql.Proc("msg.HoldScheduledBatch",Sql.P("@UserId",f.Admin),Sql.P("@BatchId",batch));
            if(acquired)Sql.Proc("msg.ReleaseBackgroundLease",Sql.P("@UserId",f.Admin),Sql.P("@EnrollmentId",enrollment),Sql.P("@LeaseNonce",nonce));
            var c=control.Rows[0];
            Sql.Query("UPDATE msg.DispatchControl SET WindowStart=@Start,WindowEnd=@End,TimeZoneId=@Zone,MasterPaused=@Pause,UnattendedEnabled=@Enabled WHERE ControlId=1",
                Sql.P("@Start",c["WindowStart"]),Sql.P("@End",c["WindowEnd"]),Sql.P("@Zone",c["TimeZoneId"]),Sql.P("@Pause",c["MasterPaused"]),Sql.P("@Enabled",c["UnattendedEnabled"]));
            foreach(DataRow d in days.Rows)Sql.Query("UPDATE msg.NonWorkingWeekday SET Enabled=@Enabled WHERE IsoWeekday=@Day",Sql.P("@Enabled",d["Enabled"]),Sql.P("@Day",d["IsoWeekday"]));
            foreach(DataRow ch in channels.Rows)Sql.Query("UPDATE msg.ChannelSettings SET ProviderCode=@Provider,Enabled=@Enabled WHERE Channel=@Channel",Sql.P("@Provider",ch["ProviderCode"]),Sql.P("@Enabled",ch["Enabled"]),Sql.P("@Channel",ch["Channel"]));
        }
        return Task.CompletedTask;
    }
}
