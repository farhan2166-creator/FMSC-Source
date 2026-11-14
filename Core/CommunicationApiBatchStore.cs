using System.Data;
using System.Security.Cryptography;
using System.Text.Json;

namespace FMSC_SMS.Core;

/// <summary>One reviewed API batch, no scheduler. The persisted review pins
/// provider identity, template and exact rendered content. Resuming only offers
/// Pending recipients; an uncertain POST can never become an automatic retry.</summary>
internal static class CommunicationApiBatchStore
{
    internal sealed record Prepared(string ProviderCode,string Payload);

    internal static async Task<bool> IsReadyAsync(string channel,string provider)
    {
        try
        {
            using var rows=await DatabaseHelper.QueryUserAsync("msg.ReadDispatchProvider",DatabaseHelper.P("@Channel",channel));
            if(rows.Rows.Count!=1 || Convert.ToString(rows.Rows[0]["ProviderCode"])!=provider) return false;
            _=CommunicationTransportRegistry.ResolveForDispatch(channel,provider);
            return true;
        }
        catch(InvalidOperationException) { return false; }
        catch(IOException) { return false; }
        catch(ArgumentException) { return false; }
        catch(CryptographicException) { return false; }
        catch(Microsoft.Data.SqlClient.SqlException ex) when(ex.Number==51092) { return false; }
    }
    internal static async Task<IReadOnlyList<CommunicationMetaTransport.Template>> ReadMetaTemplatesAsync(CancellationToken token=default)
    {
        var c=CommunicationProviderCredentialStore.LoadForDispatch("WhatsApp","MetaCloud");
        using var handler=new HttpClientHandler {AllowAutoRedirect=false,UseCookies=false};
        using var client=new HttpClient(handler) {Timeout=TimeSpan.FromSeconds(25)};
        return await CommunicationMetaTransport.ReadTemplatesAsync(c,client,token);
    }
    internal static async Task<CommunicationConfirmation> ConfirmAsync(CommunicationTarget target,
        CommunicationPreview preview,string body,Guid request,string provider,CommunicationMetaTransport.Selection? template)
    {
        int actor=CurrentUser.UserId;
        if(!await IsReadyAsync(target.Channel,provider))
            throw new CommunicationValidationException("The selected method is paused or needs configuration in Settings.");
        var credential=CommunicationProviderCredentialStore.LoadForDispatch(target.Channel,provider);
        if(provider=="MetaCloud")
        {
            if(template is null || template.Format()!=body)
                throw new CommunicationValidationException("Choose an approved Meta template and review it again.");
            await ValidateMetaSelectionAsync(template);
        }
        else if(target.Channel!="Sms" || provider!="Twilio" || template is not null)
            throw new CommunicationValidationException("Unsupported sending method.");
        string reviewed=CommunicationReviewedContent.ReviewJson(target,preview,body);
        var current=await CommunicationRecipientResolver.ReadAsync(target);
        if(actor!=CurrentUser.UserId || reviewed!=CommunicationReviewedContent.ReviewJson(target,current,body))
            throw new CommunicationValidationException("Recipients or message values changed. Review the message again; nothing was sent.");
        return await CommunicationBatchStore.ConfirmPreviewAsync(target,preview,body,request,
            apiProvider:provider,profileHash:CommunicationProviderCredentialStore.IdentityHash(credential),
            reviewJson:reviewed,templateJson:template is null ? null : JsonSerializer.Serialize(template));
    }
    internal static Task<DataTable> ReadAsync(Guid batch) => DatabaseHelper.QueryUserAsync(
        "msg.ReadApiBatch",DatabaseHelper.P("@BatchId",batch));

    internal static async Task<Prepared> PrepareRecipientAsync(long recipientId,CancellationToken token)
    {
        // Resolve the batch ID without accepting an arbitrary caller-supplied scope.
        const string sql="""
            EXEC dbo.RequirePermission @UserId,N'Communication.Send';
            SELECT BatchId FROM msg.Recipient WHERE RecipientId=@RecipientId;
            """;
        int actor=CurrentUser.UserId;
        using var identity=await DatabaseHelper.QuerySqlAsync(sql,DatabaseHelper.Int("@UserId",actor),
            DatabaseHelper.P("@RecipientId",recipientId));
        if(identity.Rows.Count!=1) throw new CommunicationValidationException("The selected recipient no longer exists.");
        using var rows=await ReadAsync((Guid)identity.Rows[0]["BatchId"]);
        DataRow? row=rows.AsEnumerable().FirstOrDefault(r=>Convert.ToInt64(r["RecipientId"])==recipientId);
        if(row is null || Convert.ToString(row["State"])!="Pending")
            throw new CommunicationValidationException("Only an unattempted pending recipient can be sent. Review uncertain attempts in History.");
        string provider=Convert.ToString(row["ProviderCode"])!,channel=Convert.ToString(row["Channel"])!;
        var credential=CommunicationProviderCredentialStore.LoadForDispatch(channel,provider);
        if(CommunicationProviderCredentialStore.IdentityHash(credential)!=Convert.ToString(row["ProfileHash"]))
            throw new CommunicationValidationException("The sending account changed. Restore its reviewed profile or prepare a new message.");
        var target=new CommunicationTarget(Enum.Parse<CommunicationAudience>(Convert.ToString(row["Audience"])!),
            Convert.ToInt32(row["CampusId"]),Convert.ToInt32(row["ClassId"]),
            Convert.ToInt32(row["StudentId"]),Convert.ToInt32(row["StaffId"]),channel);
        var current=await CommunicationRecipientResolver.ReadAsync(target);
        var entry=current.Entries.FirstOrDefault(e=>e.Included && e.Kind==Convert.ToString(row["RecipientKind"])
            && e.RecordId==Convert.ToInt32(row["RecordId"]) && e.PhoneE164==Convert.ToString(row["PhoneE164"]));
        var reviewed=JsonSerializer.Deserialize<CommunicationReviewedContent.Reviewed[]>(Convert.ToString(row["ReviewJson"])!) ?? Array.Empty<CommunicationReviewedContent.Reviewed>();
        string body=Convert.ToString(row["Body"])!;
        if(entry is null || !reviewed.Any(r=>r.Kind==entry.Kind && r.RecordId==entry.RecordId && r.Phone==entry.PhoneE164
            && r.Hash==CommunicationReviewedContent.Signature(target,current,entry,body)))
            throw new CommunicationValidationException("Consent, contact, scope or message values changed. Prepare a new review; nothing was sent.");
        string payload=CommunicationPreparedMessages.Render(body,CommunicationPreparedMessages.FromPreview(entry));
        if(provider=="MetaCloud")
        {
            var template=JsonSerializer.Deserialize<CommunicationMetaTransport.Selection>(Convert.ToString(row["TemplateJson"])!)
                ?? throw new CommunicationValidationException("The approved Meta template is missing.");
            if(template.Format()!=body) throw new CommunicationValidationException("The Meta template no longer matches this review.");
            await ValidateMetaSelectionAsync(template,token);
            payload=template.Payload(CommunicationPreparedMessages.FromPreview(entry));
        }
        token.ThrowIfCancellationRequested();
        if(actor!=CurrentUser.UserId) throw new UnauthorizedAccessException("The signed-in user changed. Nothing was sent.");
        return new(provider,payload);
    }
    private static async Task ValidateMetaSelectionAsync(CommunicationMetaTransport.Selection template,CancellationToken token=default)
    {
        var approved=await ReadMetaTemplatesAsync(token);
        if(!approved.Contains(template.Template))
            throw new CommunicationValidationException("This Meta template changed or is no longer approved. Choose it again and review the message.");
    }
}
