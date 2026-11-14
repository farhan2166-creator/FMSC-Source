using System.Data;

namespace FMSC_SMS.Core;

/// <summary>
/// Separate permission-controlled reads for Communication History and Settings.
/// No credential values are returned or persisted in the school database.
/// Saving credentials never activates sending. Activation is a separate verified action.
/// </summary>
internal static class CommunicationManagementStore
{
    internal static async Task<DataTable> ReadHistoryAsync(int limit = 100)
    {
        if (limit < 1 || limit > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        if (!CurrentUser.HasPermission("Communication.View"))
            throw new UnauthorizedAccessException("Communication History permission is required.");
        return await DatabaseHelper.QueryUserAsync("msg.ReadHistory", DatabaseHelper.Int("@Limit", limit));
    }

    internal static async Task<DataTable> ReadSettingsAsync()
    {
        if (!CurrentUser.HasPermission("Communication.Configure"))
            throw new UnauthorizedAccessException("Communication Settings permission is required.");
        return await DatabaseHelper.QueryUserAsync("msg.ReadChannelSettings");
    }

    internal static Task<DataTable> ReadMyUnfinishedBatchesAsync() =>
        DatabaseHelper.QueryUserAsync("msg.ReadMyUnfinishedBatches");

    internal static Task RecordChannelOptOutAsync(string channel,string phone) =>
        DatabaseHelper.ExecuteUserAsync("msg.RecordChannelOptOut",DatabaseHelper.P("@Channel",channel),DatabaseHelper.P("@Phone",phone));

    internal static async Task<DataTable> ReadComposeTemplatesAsync()
    {
        if (!CurrentUser.HasPermission("Communication.Send"))
            throw new UnauthorizedAccessException("A sender permission is required to read message templates.");
        return await DatabaseHelper.QueryUserAsync("msg.ReadComposeTemplates");
    }

    internal static async Task<DataTable> ReadTemplatesAsync()
    {
        if (!CurrentUser.HasPermission("Communication.Configure"))
            throw new UnauthorizedAccessException("Communication Settings permission is required.");
        return await DatabaseHelper.QueryUserAsync("msg.ReadTemplates");
    }

    internal static async Task ConfigureProviderAsync(
        CommunicationProviderCredentialStore.Credential credential)
    {
        if (!CurrentUser.HasPermission("Communication.Configure"))
            throw new UnauthorizedAccessException("Administrator settings permission is required.");
        if(credential.ProviderCode=="MetaCloud") CommunicationMetaTransport.ValidateCredentials(credential);
        else if(credential.Channel=="Sms" && credential.ProviderCode=="Twilio") CommunicationTwilioTransport.ValidateCredentials(credential);
        else throw new CommunicationValidationException("Choose Meta WhatsApp or Twilio SMS.");
        // Pause the channel BEFORE changing a provider secret or identity.
        // A partial failure can never activate messaging.
        await PauseAsync(credential.Channel);
        CommunicationProviderCredentialStore.Save(credential);
        await DatabaseHelper.ExecuteUserAsync("msg.ConfigureChannel",
            DatabaseHelper.P("@Channel", credential.Channel),
            DatabaseHelper.P("@ProviderCode", credential.ProviderCode));
    }

    internal static async Task VerifyAndEnableAsync(string channel,CancellationToken token=default)
    {
        if(!CurrentUser.HasPermission("Communication.Configure")) throw new UnauthorizedAccessException("Communication.Configure is required.");
        int actor=CurrentUser.UserId;
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));token=deadline.Token;
        var c=CommunicationProviderCredentialStore.LoadForConfiguration(channel);
        using var handler=new HttpClientHandler {AllowAutoRedirect=false,UseCookies=false};
        using var client=new HttpClient(handler) {Timeout=TimeSpan.FromSeconds(25)};
        if(c.ProviderCode=="MetaCloud") await CommunicationMetaTransport.VerifyAsync(c,client,token);
        else
        {
            CommunicationTwilioTransport.ValidateCredentials(c);
            using var request=new HttpRequestMessage(HttpMethod.Get,"https://api.twilio.com/2010-04-01/Accounts/"+c.AccountId+
                "/IncomingPhoneNumbers.json?PhoneNumber="+Uri.EscapeDataString(c.SenderId)+"&PageSize=1");
            request.Headers.Authorization=new System.Net.Http.Headers.AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(c.AccountId+":"+c.ApiToken)));
            using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token);
            if(!response.IsSuccessStatusCode) throw new CommunicationValidationException("Twilio could not verify this account and SMS number. Check the profile in Settings.");
            using var stream=await response.Content.ReadAsStreamAsync(token);
            var bytes=new byte[65537]; int total=0,count;
            while(total<bytes.Length && (count=await stream.ReadAsync(bytes.AsMemory(total),token))>0) total+=count;
            if(total>65536) throw new CommunicationValidationException("The provider verification response was too large.");
            using var json=System.Text.Json.JsonDocument.Parse(bytes.AsMemory(0,total));
            bool valid=json.RootElement.TryGetProperty("incoming_phone_numbers",out var numbers)
                && numbers.ValueKind==System.Text.Json.JsonValueKind.Array && numbers.EnumerateArray().Any(n=>
                    n.TryGetProperty("phone_number",out var phone) && phone.GetString()==c.SenderId
                    && n.TryGetProperty("capabilities",out var capabilities) && capabilities.TryGetProperty("sms",out var sms)
                    && sms.ValueKind==System.Text.Json.JsonValueKind.True);
            if(!valid) throw new CommunicationValidationException("This SMS-capable sender was not found in the Twilio account.");
        }
        token.ThrowIfCancellationRequested();
        if(actor!=CurrentUser.UserId) throw new UnauthorizedAccessException("The signed-in user changed.");
        var current=CommunicationProviderCredentialStore.LoadForConfiguration(channel);
        if(CommunicationProviderCredentialStore.IdentityHash(current)!=CommunicationProviderCredentialStore.IdentityHash(c)
            || current.ApiToken!=c.ApiToken)
            throw new CommunicationValidationException("The protected provider profile changed during verification. Verify the current profile again.");
        await DatabaseHelper.ExecuteUserAsync("msg.EnableApiChannel",DatabaseHelper.P("@Channel",channel),DatabaseHelper.P("@ProviderCode",c.ProviderCode));
    }

    /// <summary>Explicit administrator activation of operator-controlled
    /// WhatsApp Business handoff. No credentials, API or automatic sender.</summary>
    internal static Task EnableManualWhatsAppAsync()
    {
        if (!CurrentUser.HasPermission("Communication.Configure"))
            throw new UnauthorizedAccessException("Communication.Configure is required.");
        return DatabaseHelper.ExecuteUserAsync("msg.EnableManualWhatsApp");
    }

    internal static async Task PauseAsync(string channel)
    {
        if (!CurrentUser.HasPermission("Communication.Configure"))
            throw new UnauthorizedAccessException("Communication Settings permission is required.");
        if (channel is not ("Sms" or "WhatsApp" or "Email"))
            throw new ArgumentException("Unknown messaging channel.", nameof(channel));
        await DatabaseHelper.ExecuteUserAsync("msg.PauseChannel",
            DatabaseHelper.P("@Channel", channel));
    }

    internal static async Task SaveTemplateAsync(
        int templateId, string name, string channel, string purpose, string body, bool active)
    {
        if (!CurrentUser.HasPermission("Communication.Configure"))
            throw new UnauthorizedAccessException("Communication Settings permission is required.");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(body))
            throw new ArgumentException("Template name and message are required.");
        CommunicationPreparedMessages.ValidateFormat(body, purpose);
        await DatabaseHelper.QueryUserAsync("msg.SaveTemplate",
            DatabaseHelper.Int("@TemplateId", templateId),
            DatabaseHelper.P("@Name", name.Trim()),
            DatabaseHelper.P("@Channel", channel),
            DatabaseHelper.P("@Purpose", purpose),
            DatabaseHelper.P("@Body", body),
            DatabaseHelper.Bit("@Active", active));
    }
}
