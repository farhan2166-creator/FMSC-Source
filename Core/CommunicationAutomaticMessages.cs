using System.Data;
using System.Globalization;
using System.Text.Json;

namespace FMSC_SMS.Core;

/// <summary>Event-specific authorization and literal financial context for the shared composer.</summary>
internal static class CommunicationAutomaticMessages
{
    internal static bool IsFeeEvent(string code)=>code is "OutstandingFees" or "UpcomingDue" or "OverdueFollowUp";
    internal static string DefaultBody(string code,string language)=>code=="PaymentReceived"
        ? language=="ur" ? "محترم والدین/سرپرست،\n[PaymentDetails]\nرسید نمبر: [ReceiptNumber]\nRegards: FMSC"
          : "Dear parent/guardian,\n[PaymentDetails]\nReceipt: [ReceiptNumber]\nRegards: FMSC"
        : CommunicationAutomationInboxStore.SuggestedBody(code,language,IsFeeEvent(code));

    internal static void Validate(string code,string body)
    {
        if(code!="PaymentReceived" && new[]{"[PaymentDetails]","[PaymentAmount]","[CreditApplied]","[ReceiptNumber]"}.Any(body.Contains))
            throw new CommunicationValidationException("Payment context is available only for a payment event.");
        if(code is not ("Announcement" or "ExaminationResult") && new[]{"[NoticeTitle]","[NoticeText]"}.Any(body.Contains))
            throw new CommunicationValidationException("Notice context requires an approved school notice.");
        CommunicationPreparedMessages.ValidateFormat(body,IsFeeEvent(code) ? "FeeReminder" : "Individual",allowEventContext:true);
    }
    internal static (string Body,CommunicationMetaTransport.Selection? Meta) Expand(DataRow row,string body,CommunicationMetaTransport.Selection? meta)
    {
        if(Convert.ToString(row["EventCode"])!="PaymentReceived")return(body,meta);
        decimal total=Convert.ToDecimal(row["PaymentAmount"]);
        bool credit=Convert.ToBoolean(row["IsCreditApplication"]);
        string receipt=Convert.ToString(row["ReceiptNumber"])??"";
        if(total<=0 || receipt.Length==0)throw new CommunicationValidationException("The authoritative payment/receipt is unavailable.");
        string language=Convert.ToString(row["LanguageCode"])!;
        string Replace(string text)=>text.Replace("[PaymentDetails]",FeeMessageReceiptSource.PaymentDetails(credit ? 0 : total,credit ? total : 0,language),StringComparison.Ordinal)
            .Replace("[PaymentAmount]",(credit ? 0 : total).ToString("N2",CultureInfo.InvariantCulture),StringComparison.Ordinal)
            .Replace("[CreditApplied]",(credit ? total : 0).ToString("N2",CultureInfo.InvariantCulture),StringComparison.Ordinal)
            .Replace("[ReceiptNumber]",receipt,StringComparison.Ordinal);
        if(meta is not null)
        {
            meta=meta with {Fields=meta.Fields.Select(Replace).ToArray()};
            return(meta.Format(),meta);
        }
        return(Replace(body),null);
    }
    internal static async Task SaveAsync(Guid request,string code,int templateId,string body,string language,string channel,string provider,
        CommunicationMetaTransport.Selection? meta,int dailyFamilyLimit,int expiryHours,int expectedCampus,int expectedClass)
    {
        RequireAdministrator();int actor=CurrentUser.UserId;
        Validate(code,body);
        if(!await CommunicationApiBatchStore.IsReadyAsync(channel,provider))
            throw new CommunicationValidationException("Configure and enable this API method in Settings first.");
        Guard(actor);
        var credential=CommunicationProviderCredentialStore.LoadForConfiguration(channel);
        if(credential.ProviderCode!=provider)throw new CommunicationValidationException("The configured provider changed.");
        if(provider=="MetaCloud")
        {
            if(meta is null || meta.Format(true)!=body || !(meta.Template.Language==language || meta.Template.Language.StartsWith(language+"_",StringComparison.Ordinal)))
                throw new CommunicationValidationException("Choose an approved Meta template in the selected language.");
            var approved=await CommunicationApiBatchStore.ReadMetaTemplatesAsync();Guard(actor);
            if(!approved.Contains(meta.Template))throw new CommunicationValidationException("The Meta template changed or is not approved.");
        }
        if(templateId==0)
        {
            // Approving a curated/API format also creates a normal durable school
            // template revision; later edits invalidate this exact authorization.
            using var saved=await CommunicationManagementStore.ReadTemplatesAsync();Guard(actor);
            DataRow? matching=saved.AsEnumerable().FirstOrDefault(row=>Convert.ToBoolean(row["Active"]) && Convert.ToString(row["Body"])==body
                && Convert.ToString(row["Purpose"])==(IsFeeEvent(code) ? "FeeReminder" : "Individual") && Convert.ToString(row["Channel"])==channel && Convert.ToString(row["LanguageCode"])==language);
            if(matching is not null)templateId=Convert.ToInt32(matching["TemplateId"]);
            else
            {
                string name="Automatic "+CommunicationAutomationInboxStore.EventLabel(code)+" "+language+" "+request.ToString("N")[..8];
                await CommunicationManagementStore.SaveTemplateAsync(0,name,channel,IsFeeEvent(code) ? "FeeReminder" : "Individual",body,true,language);Guard(actor);
                using var read=await CommunicationManagementStore.ReadTemplatesAsync();Guard(actor);
                templateId=Convert.ToInt32(read.AsEnumerable().Single(row=>Convert.ToString(row["Name"])==name && Convert.ToString(row["Channel"])==channel)["TemplateId"]);
            }
        }
        Guard(actor);
        await DatabaseHelper.ExecuteUserAsync("msg.SaveAutomaticAuthorization",DatabaseHelper.P("@AuthorizationId",request),DatabaseHelper.P("@EventCode",code),
            DatabaseHelper.Int("@TemplateId",templateId),DatabaseHelper.P("@LanguageCode",language),DatabaseHelper.P("@Channel",channel),DatabaseHelper.P("@ProviderCode",provider),
            DatabaseHelper.P("@ProfileHash",CommunicationProviderCredentialStore.IdentityHash(credential)),DatabaseHelper.P("@Body",body),
            DatabaseHelper.P("@TemplateJson",meta is null ? null : JsonSerializer.Serialize(meta)),DatabaseHelper.Int("@DailyFamilyLimit",dailyFamilyLimit),
            DatabaseHelper.Int("@ExpiryHours",expiryHours),DatabaseHelper.Bit("@PaidApproval",true),DatabaseHelper.Int("@ExpectedCampusId",expectedCampus),DatabaseHelper.Int("@ExpectedClassId",expectedClass));
        Guard(actor);
    }
    private static void RequireAdministrator()
    {
        if(!CurrentUser.IsAdministrator || !CurrentUser.HasPermission("Communication.Configure") || !CurrentUser.HasPermission("Communication.Send"))
            throw new UnauthorizedAccessException("An administrator must authorize automatic communication.");
    }
    private static void Guard(int actor)
    {
        RequireAdministrator();if(actor!=CurrentUser.UserId)throw new UnauthorizedAccessException("The authorizing administrator changed.");
    }
}
