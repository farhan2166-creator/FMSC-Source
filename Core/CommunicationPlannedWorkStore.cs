using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace FMSC_SMS.Core;

/// <summary>Prepares authorized occurrences through the common reviewed batch pipeline.
/// It makes no provider submission; dispatch, claiming and journals remain shared.</summary>
internal static class CommunicationPlannedWorkStore
{
    internal sealed record Claim(Guid WorkId,Guid Nonce,CommunicationBackgroundEnrollment.Enrollment Enrollment,Guid SchoolLease);

    internal static SqlParameter[] Parameters(Claim claim) => new[] {
        DatabaseHelper.P("@PlannedWorkId",claim.WorkId),DatabaseHelper.P("@WorkClaim",claim.Nonce)
    }.Concat(CommunicationBackgroundEnrollment.Proof(claim.Enrollment,claim.SchoolLease)).ToArray();

    internal static async Task PrepareDueAsync(CommunicationBackgroundEnrollment.Enrollment enrollment,Guid lease,CancellationToken token)
    {
        int actor=CurrentUser.UserId;
        // Safe internal maintenance on the same authorized worker path. Never
        // clear confirmed batches, attempts, source keys or unresolved evidence.
        await DatabaseHelper.ExecuteUserAsync("msg.PruneTerminalCommunication",token,
            DatabaseHelper.Int("@MaxRows",200));
        await DatabaseHelper.ExecuteUserAsync("msg.RefreshAutomaticSources",token,CommunicationBackgroundEnrollment.Proof(enrollment,lease));
        await DatabaseHelper.ExecuteUserAsync("msg.CreateAutomaticWork",token,CommunicationBackgroundEnrollment.Proof(enrollment,lease));

        using(var due=await DatabaseHelper.QueryUserAsync("msg.ReadDueRecurrences",token,CommunicationBackgroundEnrollment.Proof(enrollment,lease)))
        {
            foreach(DataRow row in due.Rows)
            {
                Guard(actor,token);
                var recurrence=new CommunicationRecurrence(Convert.ToString(row["Frequency"])!,Convert.ToInt32(row["Every"]),
                    Convert.ToDateTime(row["FirstLocal"]),Convert.ToString(row["TimeZoneId"])!,
                    new DateTimeOffset(DateTime.SpecifyKind(Convert.ToDateTime(row["EndUtc"]),DateTimeKind.Utc)),
                    Convert.ToInt32(row["MaxOccurrences"]),Convert.ToInt32(row["ExpiryHours"]));
                try
                {
                    DateTimeOffset next=recurrence.NextAfter(DateTimeOffset.UtcNow);
                    var args=CommunicationBackgroundEnrollment.Proof(enrollment,lease).Concat(new[]{
                        DatabaseHelper.P("@RuleId",row["RuleId"]),DatabaseHelper.P("@ExpectedDueUtc",row["NextDueUtc"]),
                        DatabaseHelper.P("@NextDueUtc",next.UtcDateTime)}).ToArray();
                    await DatabaseHelper.ExecuteUserAsync("msg.CreateRecurringOccurrence",token,args);
                }
                catch(CommunicationValidationException)
                {
                    await DatabaseHelper.ExecuteUserAsync("msg.HoldRecurringPreparation",token,
                        CommunicationBackgroundEnrollment.Proof(enrollment,lease).Concat(new[]{DatabaseHelper.P("@RuleId",row["RuleId"])}).ToArray());
                }
            }
        }
        var preparationStarted=DateTime.UtcNow;
        bool skipSms=false,skipWhatsApp=false;
        for(int prepared=0;prepared<20 && DateTime.UtcNow-preparationStarted<TimeSpan.FromSeconds(15);prepared++)
        {
            await DatabaseHelper.ExecuteUserAsync("msg.RenewBackgroundLease",token,CommunicationBackgroundEnrollment.Proof(enrollment,lease));
        Guard(actor,token);
        Guid nonce=Guid.NewGuid();
        using var work=await DatabaseHelper.QueryUserAsync("msg.ClaimPlannedWork",token,
            CommunicationBackgroundEnrollment.Proof(enrollment,lease).Concat(new[]{DatabaseHelper.P("@ClaimNonce",nonce),DatabaseHelper.Bit("@SkipSms",skipSms),DatabaseHelper.Bit("@SkipWhatsApp",skipWhatsApp)}).ToArray());
        if(work.Rows.Count==0)return;
        DataRow current=work.Rows[0];
        var claim=new Claim((Guid)current["WorkId"],nonce,enrollment,lease);
        try
        {
            Guard(actor,token);
            var target=new CommunicationTarget(Enum.Parse<CommunicationAudience>(Convert.ToString(current["Audience"])!),
                Convert.ToInt32(current["CampusId"]),Convert.ToInt32(current["ClassId"]),Convert.ToInt32(current["StudentId"]),
                Convert.ToInt32(current["StaffId"]),Convert.ToString(current["Channel"])!);
            string provider=Convert.ToString(current["ProviderCode"])!,body=Convert.ToString(current["Body"])!;
            var credential=CommunicationProviderCredentialStore.LoadForDispatch(target.Channel,provider);
            if(CommunicationProviderCredentialStore.IdentityHash(credential)!=Convert.ToString(current["ProfileHash"]))
                {
                await HoldAsync(claim,"ProviderChanged",token);
                if(target.Channel=="Sms")skipSms=true;else skipWhatsApp=true;
                continue;
            }
            var preview=await CommunicationRecipientResolver.ReadAsync(target);
            if(preview.Included==0)
            {
                await HoldAsync(claim,"NoEligibleRecipients",token);continue;
            }
            var template=current.IsNull("TemplateJson") ? null
                : JsonSerializer.Deserialize<CommunicationMetaTransport.Selection>(Convert.ToString(current["TemplateJson"])!);
            if(Convert.ToString(current["SourceKind"])=="Event")
                (body,template)=CommunicationAutomaticMessages.Expand(current,body,template);
            Guard(actor,token);
            await CommunicationApiBatchStore.ConfirmAsync(target,preview,body,claim.WorkId,provider,template,claim);
        }
        catch(CommunicationValidationException ex) when(ex.IsTemporary)
        {if(Convert.ToString(current["Channel"])=="Sms")skipSms=true;else skipWhatsApp=true;}
        catch(SqlException ex) when(ex.Number is 1205 or 1222 or -2){return;}
        catch(SqlException ex) when(ex.Number is 50003 or 51086 or 51131 or 51133 or 51401 or 51431)
        {
            await HoldAsync(claim,"SourceChanged",token);
        }
        catch(OperationCanceledException){throw;}
        catch(Exception ex) when(ex is CommunicationValidationException or InvalidOperationException or System.Security.Cryptography.CryptographicException or IOException)
        {
            await HoldAsync(claim,"PreparationFailed",token);
            AppExceptionHandler.LogNonFatal(new InvalidOperationException("An authorized communication occurrence needs review."),"Communication preparation held");
        }
        }
    }
    private static Task HoldAsync(Claim claim,string reason,CancellationToken token)=>DatabaseHelper.ExecuteUserAsync("msg.HoldPlannedWork",token,
        CommunicationBackgroundEnrollment.Proof(claim.Enrollment,claim.SchoolLease).Concat(new[]{DatabaseHelper.P("@WorkId",claim.WorkId),
            DatabaseHelper.P("@ClaimNonce",claim.Nonce),DatabaseHelper.P("@ReasonCode",reason)}).ToArray());
    private static void Guard(int actor,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(actor!=CurrentUser.UserId || !CurrentUser.HasPermission("Communication.Send"))
            throw new UnauthorizedAccessException("The authorized preparation identity changed.");
    }
    internal static Task RegisterAsync(Guid batch,CommunicationRecurrence rule,DateTimeOffset firstDue)
    {
        if(!CurrentUser.HasPermission("Communication.Send"))throw new UnauthorizedAccessException();
        DateTimeOffset next=rule.NextAfter(firstDue);
        return DatabaseHelper.ExecuteUserAsync("msg.RegisterRecurrence",DatabaseHelper.P("@OriginBatchId",batch),
            DatabaseHelper.P("@Frequency",rule.Frequency),DatabaseHelper.P("@Every",rule.Every),DatabaseHelper.P("@FirstLocal",rule.FirstLocal),
            DatabaseHelper.P("@TimeZoneId",rule.TimeZoneId),DatabaseHelper.P("@NextDueUtc",next.UtcDateTime),DatabaseHelper.P("@EndUtc",rule.End.UtcDateTime),
            DatabaseHelper.Int("@ExpiryHours",rule.ExpiryHours),DatabaseHelper.Int("@MaxOccurrences",rule.MaximumOccurrences));
    }
    internal static Task<DataTable> ReadRulesAsync()=>DatabaseHelper.QueryUserAsync("msg.ReadRecurringRules");
    internal static async Task ResumeAsync(DataRow row,DateTimeOffset next)
    {
        int actor=CurrentUser.UserId;
        string channel=Convert.ToString(row["Channel"])!,provider=Convert.ToString(row["ProviderCode"])!;
        var credential=CommunicationProviderCredentialStore.LoadForDispatch(channel,provider);
        if(CommunicationProviderCredentialStore.IdentityHash(credential)!=Convert.ToString(row["ProfileHash"]))
            throw new CommunicationValidationException("The approved sending account changed. Prepare a new recurrence with the current provider.");
        if(!await CommunicationApiBatchStore.IsReadyAsync(channel,provider))
            throw new CommunicationValidationException("Configure and enable the approved provider before resuming.");
        if(provider=="MetaCloud")
        {
            var selected=JsonSerializer.Deserialize<CommunicationMetaTransport.Selection>(Convert.ToString(row["TemplateJson"])!);
            var approved=await CommunicationApiBatchStore.ReadMetaTemplatesAsync();
            if(selected is null || !approved.Contains(selected.Template))throw new CommunicationValidationException("The approved Meta template changed. Review a new recurrence.");
        }
        if(actor!=CurrentUser.UserId)throw new UnauthorizedAccessException("The sending account changed.");
        await SetStateAsync((Guid)row["RuleId"],"Active",next);
    }
    internal static Task SetStateAsync(Guid rule,string state,DateTimeOffset? next=null)=>DatabaseHelper.ExecuteUserAsync("msg.SetRecurringState",
        DatabaseHelper.P("@RuleId",rule),DatabaseHelper.P("@State",state),DatabaseHelper.P("@NextDueUtc",next?.UtcDateTime));
}
