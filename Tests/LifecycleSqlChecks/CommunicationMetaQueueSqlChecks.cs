using System.Data;
using System.Text.Json;

// Historical provider evidence belongs only to the harness's owned disposable
// school. No credential, provider request or real message is used by these reads.
internal static class CommunicationMetaQueueSqlChecks
{
    internal static async Task Run(Fixture f,Report r,int student,int sibling,Guid twilioBatch)
    {
        const string body="Fictional notice.";
        Guid HistoricalMeta(params (int Student,string Phone,string State)[] recipients)
        {
            Guid batch=Guid.NewGuid();
            Sql.Query("""
                INSERT msg.Batch(BatchId,ClientRequestId,Channel,Purpose,Body,Audience,State,CreatedBy,ConfirmedBy,ConfirmedUtc)
                VALUES(@Batch,@Request,'WhatsApp','Announcement',@Body,'School','Sending',@User,@User,SYSUTCDATETIME());
                INSERT msg.ApiBatch(BatchId,ProviderCode,ProfileHash,ReviewJson,TemplateJson)
                VALUES(@Batch,'MetaCloud',@Hash,@Review,@Template);
                """,Sql.P("@Batch",batch),Sql.P("@Request",Guid.NewGuid()),Sql.P("@Body",body),Sql.P("@User",f.Admin),
                Sql.P("@Hash",new string('A',64)),Sql.P("@Review",JsonSerializer.Serialize(recipients.Select(item=>new {
                    Kind="Student",RecordId=item.Student,Phone=item.Phone,Hash=new string('B',64),Text=body,FeeMembers=Array.Empty<object>()
                }))),Sql.P("@Template",JsonSerializer.Serialize(new {
                    Template=new {Name="school_notice",Language="en",Body=body,Header="",Footer=""},Fields=Array.Empty<string>()
                })));
            foreach(var item in recipients)
            {
                long recipient=Convert.ToInt64(Sql.Scalar("""
                    INSERT msg.Recipient(BatchId,RecipientKind,AdmissionId,PhoneE164,State)
                    VALUES(@Batch,'Student',@Student,@Phone,@State);
                    SELECT CONVERT(bigint,SCOPE_IDENTITY());
                    """,Sql.P("@Batch",batch),Sql.P("@Student",item.Student),Sql.P("@Phone",item.Phone),Sql.P("@State",item.State)));
                if(item.State=="Pending")continue;
                Sql.Query("""
                    INSERT msg.Attempt(RecipientId,AttemptNumber,AttemptKey,Channel,ProviderCode,Outcome,ProviderMessageId,ResponseUtc)
                    VALUES(@Recipient,1,@Key,'WhatsApp','MetaCloud',@State,@Message,
                        CASE WHEN @State='Unknown' THEN NULL ELSE SYSUTCDATETIME() END);
                    """,Sql.P("@Recipient",recipient),Sql.P("@Key",Guid.NewGuid()),Sql.P("@State",item.State),
                    Sql.P("@Message",item.State=="Accepted" ? "wamid."+Guid.NewGuid().ToString("N") : null));
            }
            return batch;
        }
        Guid accepted=HistoricalMeta((student,"+923008000077","Accepted"));
        Guid pending=HistoricalMeta((student,"+923008000077","Pending"));
        Guid unknown=HistoricalMeta((student,"+923008000077","Unknown"));
        Guid submitted=HistoricalMeta((student,"+923008000077","Submitted"));
        Guid failed=HistoricalMeta((student,"+923008000077","Failed"));
        Guid partial=HistoricalMeta((student,"+923008000077","Accepted"),(sibling,"+923008000078","Pending"));
        using DataTable unfinished=Sql.Proc("msg.ReadMyUnfinishedBatches",Sql.P("@UserId",f.Admin));
        var queues=unfinished.AsEnumerable().ToDictionary(row=>(Guid)row["BatchId"]);
        r.Check(!queues.ContainsKey(accepted),"Meta batches with all recipients Accepted leave the unfinished work queue.");
        r.Check(new[]{pending,unknown,submitted,failed}.All(queues.ContainsKey),
            "Meta Pending, Unknown, Submitted and Failed records continue to require attention.");
        r.Check(queues.ContainsKey(partial) && Convert.ToInt64(queues[partial]["InProgress"])==1,
            "A partially submitted Meta batch remains unfinished and counts only the recipient needing attention.");
        r.Check(queues.ContainsKey(twilioBatch),"Twilio acceptance keeps its existing delivery-status reconciliation path.");
        using DataTable history=Sql.Proc("msg.ReadHistory",Sql.P("@UserId",f.Admin));
        DataRow original=history.AsEnumerable().Single(row=>(Guid)row["BatchId"]==accepted);
        r.Check(Convert.ToInt32(original["Accepted"])==1 && Convert.ToInt32(original["Delivered"])==0,
            "Meta submission completion leaves history Accepted and never claims delivery.");
        long acceptedRecipient=Convert.ToInt64(Sql.Scalar("SELECT RecipientId FROM msg.Recipient WHERE BatchId=@Batch;",Sql.P("@Batch",accepted)));
        await r.Refused("The original Accepted Meta recipient still cannot be claimed again",()=>{
            Sql.Proc("msg.ClaimRecipient",Sql.P("@UserId",f.Admin),Sql.P("@RecipientId",acceptedRecipient),
                Sql.P("@AttemptKey",Guid.NewGuid()),Sql.P("@ExpectedProviderCode","MetaCloud"));
            return Task.CompletedTask;
        },51093);
        r.Check(Sql.Count("SELECT COUNT(*) FROM msg.Attempt WHERE RecipientId=@Recipient;",Sql.P("@Recipient",acceptedRecipient))==1,
            "Reading accepted Meta history and refusing replay preserve the single original attempt.");
    }
}
