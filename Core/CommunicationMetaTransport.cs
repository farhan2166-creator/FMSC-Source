using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FMSC_SMS.Core;

/// <summary>Approved text templates only: school-initiated WhatsApp messages
/// cannot rely on an open 24-hour customer-service window. No webhook server,
/// automatic retries or fabricated delivery receipts are introduced.</summary>
internal static class CommunicationMetaTransport
{
    internal sealed record Template(string Name, string Language, string Body, string Header, string Footer)
    {
        public override string ToString() => Name + " · " + Language;
        internal int ParameterCount => Slots.Matches(Body).Select(m => int.Parse(m.Groups[1].Value))
            .DefaultIfEmpty(0).Max();
    }
    internal sealed record Selection(Template Template, string[] Fields)
    {
        internal string Format()=>Format(false);
        internal string Format(bool allowEventContext)
        {
            ValidateTemplate(Template);
            if (Fields.Length != Template.ParameterCount)
                throw new CommunicationValidationException("Map every approved template field before reviewing.");
            foreach (string field in Fields)
                CommunicationPreparedMessages.ValidateFormat(field,allowEventContext:allowEventContext);
            string body = Slots.Replace(Template.Body, m => Fields[int.Parse(m.Groups[1].Value)-1]);
            string text = string.Join("\n", new[] { Template.Header, body, Template.Footer }.Where(s => s.Length > 0));
            CommunicationPreparedMessages.ValidateFormat(text,allowEventContext:allowEventContext);
            return text;
        }
        internal string Payload(CommunicationPreparedMessages.Values values)
        {
            _ = Format();
            return JsonSerializer.Serialize(new {
                name = Template.Name, language = new { code = Template.Language },
                components = Fields.Length == 0 ? Array.Empty<object>() : new object[] {
                    new { type = "body", parameters = Fields.Select(field => new {
                        type = "text", text = CommunicationPreparedMessages.Render(field, values) }).ToArray() }
                }
            });
        }
    }
    private static readonly Regex Slots = new(@"\{\{([1-9][0-9]?)\}\}", RegexOptions.CultureInvariant);
    internal static void ValidateCredentials(CommunicationProviderCredentialStore.Credential value)
    {
        CommunicationProviderCredentialStore.Validate(value);
        if (value.Channel != "WhatsApp" || value.ProviderCode != "MetaCloud"
            || !NumericId(value.AccountId) || !NumericId(value.SenderId)
            || string.IsNullOrWhiteSpace(value.GraphVersion)
            || !Regex.IsMatch(value.GraphVersion, @"^v[0-9]{1,2}\.0$", RegexOptions.CultureInvariant))
            throw new CommunicationValidationException("Enter the Meta business account ID, phone number ID, access token and Graph API version.");
    }
    private static bool NumericId(string value) => value.Length is >= 5 and <= 32 && value.All(char.IsAsciiDigit);
    internal static void ValidateTemplate(Template template)
    {
        if (!Regex.IsMatch(template.Name, "^[a-z0-9_]{1,512}$")
            || !Regex.IsMatch(template.Language, "^[a-zA-Z_]{2,16}$")
            || template.ParameterCount > 24
            || Slots.Replace(template.Body, "").ContainsAnyBrace()
            || template.Header.ContainsAnyBrace() || template.Footer.ContainsAnyBrace()
            || Enumerable.Range(1,template.ParameterCount).Any(n => !Slots.Matches(template.Body)
                .Any(m => m.Groups[1].Value == n.ToString(System.Globalization.CultureInfo.InvariantCulture))))
            throw new CommunicationValidationException("Use an approved text template with consecutive numbered body fields. Media, buttons and variable headers are not supported.");
    }
    private static bool ContainsAnyBrace(this string value) => value.Contains('{') || value.Contains('}');

    internal static async Task VerifyAsync(CommunicationProviderCredentialStore.Credential c, HttpClient client, CancellationToken token)
    {
        ValidateCredentials(c);
        // Only follow opaque cursors on our fixed Graph host, never provider-supplied URLs.
        bool found = false;
        await ForPagesAsync(c, c.AccountId + "/phone_numbers", client, token, row => {
            if (Get(row,"id") == c.SenderId && !string.IsNullOrWhiteSpace(Get(row,"display_phone_number"))) found = true;
        });
        if (!found) throw new CommunicationValidationException("The selected phone number ID was not found in this WhatsApp business account.");
    }
    internal static async Task<IReadOnlyList<Template>> ReadTemplatesAsync(
        CommunicationProviderCredentialStore.Credential c, HttpClient client, CancellationToken token)
    {
        ValidateCredentials(c);
        var templates = new List<Template>();
        await ForPagesAsync(c, c.AccountId + "/message_templates", client, token, row => {
            if (Get(row,"status") != "APPROVED" || !row.TryGetProperty("components",out var components)
                || components.ValueKind != JsonValueKind.Array) return;
            string body="",header="",footer=""; bool supported=true;
            foreach(var component in components.EnumerateArray())
            {
                switch(Get(component,"type"))
                {
                    case "BODY": body=Get(component,"text") ?? ""; break;
                    case "HEADER" when Get(component,"format")=="TEXT": header=Get(component,"text") ?? ""; break;
                    case "FOOTER": footer=Get(component,"text") ?? ""; break;
                    default: supported=false; break;
                }
            }
            if (!supported || string.IsNullOrWhiteSpace(body)) return;
            var template=new Template(Get(row,"name") ?? "",Get(row,"language") ?? "",body,header,footer);
            try { ValidateTemplate(template); templates.Add(template); }
            catch(CommunicationValidationException) { /* Unsupported approved formats remain unavailable, never converted into free text. */ }
        });
        return templates.OrderBy(t=>t.Name,StringComparer.Ordinal).ThenBy(t=>t.Language,StringComparer.Ordinal).ToArray();
    }
    private static async Task ForPagesAsync(CommunicationProviderCredentialStore.Credential c,string path,
        HttpClient client,CancellationToken token,Action<JsonElement> read)
    {
        var callerToken=token;
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(client.Timeout==Timeout.InfiniteTimeSpan || client.Timeout>TimeSpan.FromSeconds(25)
            ? TimeSpan.FromSeconds(25) : client.Timeout);token=deadline.Token;
        string? after=null;
        try
        {
            for(int page=0;page<10;page++)
            {
                using var request=Request(c,HttpMethod.Get,path+"?limit=100"+(after is null ? "" : "&after="+Uri.EscapeDataString(after)));
                using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token).ConfigureAwait(false);
                if(response.StatusCode==HttpStatusCode.TooManyRequests || (int)response.StatusCode>=500)
                    throw new CommunicationValidationException("Meta account and template checks are temporarily unavailable. Try again later; nothing was sent.",true);
                if(!response.IsSuccessStatusCode) throw new CommunicationValidationException("Meta could not verify the account or templates (HTTP "+(int)response.StatusCode+"). Check the token and permissions in Settings.");
                using var doc=JsonDocument.Parse(await ReadBoundedAsync(response,token));
                if(!doc.RootElement.TryGetProperty("data",out var data) || data.ValueKind!=JsonValueKind.Array)
                    throw new CommunicationValidationException("Meta returned an incomplete account response. Nothing was enabled or sent.");
                foreach(var row in data.EnumerateArray()) read(row);
                after=null;
                if(doc.RootElement.TryGetProperty("paging",out var paging) && paging.TryGetProperty("next",out _)
                    && paging.TryGetProperty("cursors",out var cursors)) after=Get(cursors,"after");
                if(string.IsNullOrWhiteSpace(after)) return;
            }
            throw new CommunicationValidationException("The Meta account exceeds the supported template or phone list size. Nothing was enabled or sent.");
        }
        catch(HttpRequestException)
        {
            // Only this read-only GET can be retried after a network failure.
            // An uncertain message POST keeps its durable Unknown outcome.
            throw new CommunicationValidationException("Meta account and template checks could not connect. Try again later; nothing was sent.",true);
        }
        catch(OperationCanceledException) when(!callerToken.IsCancellationRequested)
        {
            throw new CommunicationValidationException("Meta account and template checks timed out. Try again later; nothing was sent.",true);
        }
    }
    internal static async Task<CommunicationDeliveryOutcome> SendOnceAsync(
        CommunicationProviderCredentialStore.Credential c,string destination,string templatePayload,HttpClient client,CancellationToken token)
    {
        ValidateCredentials(c);
        if(destination.Length!=13 || !destination.StartsWith("+923",StringComparison.Ordinal) || !destination.Skip(1).All(char.IsAsciiDigit))
            throw new ArgumentException("Invalid reviewed destination.");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(client.Timeout==Timeout.InfiniteTimeSpan || client.Timeout>TimeSpan.FromSeconds(25)
            ? TimeSpan.FromSeconds(25) : client.Timeout);token=deadline.Token;
        using var document=JsonDocument.Parse(templatePayload);
        using var request=Request(c,HttpMethod.Post,c.SenderId+"/messages");
        request.Content=new StringContent(JsonSerializer.Serialize(new {
            messaging_product="whatsapp",recipient_type="individual",to=destination[1..],
            type="template",template=document.RootElement
        }),Encoding.UTF8,"application/json");
        try
        {
            using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token).ConfigureAwait(false);
            if(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity)
                return new("Failed",null,"HTTP"+(int)response.StatusCode);
            if(!response.IsSuccessStatusCode) return new("Unknown",null,"HTTP"+(int)response.StatusCode);
            using var result=JsonDocument.Parse(await ReadBoundedAsync(response,token));
            if(result.RootElement.TryGetProperty("messages",out var messages) && messages.ValueKind==JsonValueKind.Array
                && messages.GetArrayLength()==1 && Get(messages[0],"id") is { Length: > 6 and <=150 } id
                && id.StartsWith("wamid.",StringComparison.Ordinal) && !id.Any(char.IsControl))
                return new("Accepted",id,null);
            return new("Unknown",null,"MissingMessageId");
        }
        catch(OperationCanceledException) { return new("Unknown",null,"TransportUncertain"); }
        catch(HttpRequestException) { return new("Unknown",null,"TransportUncertain"); }
        catch(JsonException) { return new("Unknown",null,"InvalidProviderResponse"); }
        catch(InvalidOperationException) { return new("Unknown",null,"InvalidProviderResponse"); }
        catch(InvalidDataException) { return new("Unknown",null,"ResponseTooLarge"); }
    }
    private static HttpRequestMessage Request(CommunicationProviderCredentialStore.Credential c,HttpMethod method,string path)
    {
        var request=new HttpRequestMessage(method,"https://graph.facebook.com/"+c.GraphVersion+"/"+path);
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",c.ApiToken);
        return request;
    }
    private static string? Get(JsonElement element,string field) => element.TryGetProperty(field,out var value)
        && value.ValueKind==JsonValueKind.String ? value.GetString() : null;
    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response,CancellationToken token)
    {
        if(response.Content.Headers.ContentLength>1024*1024) throw new InvalidDataException();
        using var stream=await response.Content.ReadAsStreamAsync(token);
        using var buffer=new MemoryStream(); var block=new byte[8192]; int count;
        while((count=await stream.ReadAsync(block,token))>0)
        { if(buffer.Length+count>1024*1024) throw new InvalidDataException(); buffer.Write(block,0,count); }
        return buffer.ToArray();
    }
}
