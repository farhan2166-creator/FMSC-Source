using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FMSC_SMS.Core;

/// <summary>
/// Explicit Twilio adapter for SMS and WhatsApp. No scheduling, worker,
/// automatic retries or public callback listener. It is only called after
/// msg.ClaimRecipient atomically reserves an attempt.
/// </summary>
internal static class CommunicationTwilioTransport
{
    internal const string ProviderCode = "Twilio";

    internal sealed record Outcome(string State, string? MessageSid, string? ErrorCode);

    private static bool IsSid(string? value, string prefix) =>
        value is { Length: 34 } && value.StartsWith(prefix, StringComparison.Ordinal)
        && value.Skip(2).All(Uri.IsHexDigit);

    internal static void ValidateCredentials(CommunicationProviderCredentialStore.Credential c)
    {
        CommunicationProviderCredentialStore.Validate(c);
        if (c.ProviderCode != ProviderCode || !IsSid(c.AccountId, "AC"))
            throw new InvalidOperationException("Twilio account or approved provider configuration is invalid.");
        string sender = c.Channel == "WhatsApp"
            ? c.SenderId.StartsWith("whatsapp:", StringComparison.Ordinal) ? c.SenderId[9..] : ""
            : c.SenderId;
        if (sender.Length < 8 || sender.Length > 16
            || sender[0]!='+' || !sender.Skip(1).All(char.IsAsciiDigit))
            throw new InvalidOperationException("Twilio sender must be an approved E.164 number (WhatsApp requires whatsapp: prefix).");
        // Twilio signs callbacks using its Auth Token. A different signing key
        // must not be accepted as an independently trusted webhook secret.
        if (!string.Equals(c.ApiToken,c.CallbackSigningSecret,StringComparison.Ordinal))
            throw new InvalidOperationException("Twilio callback signature key must equal its account Auth Token.");
    }

    internal static async Task<Outcome> SendOnceAsync(
        CommunicationProviderCredentialStore.Credential credential,
        string recipientE164,string message,HttpClient client,CancellationToken token)
    {
        ValidateCredentials(credential);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(client.Timeout == Timeout.InfiniteTimeSpan || client.Timeout > TimeSpan.FromSeconds(25)
            ? TimeSpan.FromSeconds(25) : client.Timeout);
        token = deadline.Token;
        if (recipientE164 is not { Length: 13 } || !recipientE164.StartsWith("+923",StringComparison.Ordinal)
            || !recipientE164.Skip(1).All(char.IsAsciiDigit)
            || string.IsNullOrWhiteSpace(message) || message.Length>1600)
            throw new ArgumentException("Invalid reviewed message or destination.");
        string to=credential.Channel=="WhatsApp" ? "whatsapp:"+recipientE164 : recipientE164;
        using var request=new HttpRequestMessage(HttpMethod.Post,
            "https://api.twilio.com/2010-04-01/Accounts/"+credential.AccountId+"/Messages.json");
        string authorization=Convert.ToBase64String(
            Encoding.UTF8.GetBytes(credential.AccountId+":"+credential.ApiToken));
        request.Headers.Authorization=new AuthenticationHeaderValue("Basic",authorization);
        request.Content=new FormUrlEncodedContent(new Dictionary<string,string>
        {
            ["To"]=to,["From"]=credential.SenderId,["Body"]=message
        });
        try
        {
            using HttpResponseMessage response=await client.SendAsync(
                request,HttpCompletionOption.ResponseHeadersRead,token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.BadRequest
                or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity)
                return new Outcome("Failed",null,"HTTP"+(int)response.StatusCode);
            if (response.StatusCode!=HttpStatusCode.Created && response.StatusCode!=HttpStatusCode.OK)
                return new Outcome("Unknown",null,"HTTP"+(int)response.StatusCode);
            // Cap response parsing. Unexpected, incomplete or uncorrelatable
            // responses stay Unknown; a second POST is NEVER issued.
            byte[]? buffer=await ReadBoundedResponseAsync(response,token).ConfigureAwait(false);
            if (buffer is null) return new Outcome("Unknown",null,"ResponseTooLarge");
            using JsonDocument doc=JsonDocument.Parse(buffer);
            string? sid=Field(doc.RootElement,"sid");
            if (!IsSid(sid,"SM") && !IsSid(sid,"MM"))
                return new Outcome("Unknown",null,"MissingMessageSid");
            string? status=Field(doc.RootElement,"status");
            string state=MapStatus(status);
            // API create response is provider submission evidence, never proof
            // that the destination received the message.
            if (state=="Delivered") state="Accepted";
            return new Outcome(state,sid, state=="Failed" ? Field(doc.RootElement,"error_code") : null);
        }
        catch (HttpRequestException)
        {
            return new Outcome("Unknown",null,"TransportUncertain");
        }
        catch (OperationCanceledException)
        {
            return new Outcome("Unknown",null,"TransportUncertain");
        }
        catch (JsonException)
        {
            return new Outcome("Unknown",null,"InvalidProviderResponse");
        }
    }

    /// <summary>Authenticated provider GET for manual reconciliation, never a resend.</summary>
    internal static async Task<Outcome> ReadStatusAsync(
        CommunicationProviderCredentialStore.Credential credential,
        string sid,HttpClient client,CancellationToken token)
    {
        ValidateCredentials(credential);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(client.Timeout == Timeout.InfiniteTimeSpan || client.Timeout > TimeSpan.FromSeconds(25)
            ? TimeSpan.FromSeconds(25) : client.Timeout);
        token = deadline.Token;
        if (!IsSid(sid,"SM") && !IsSid(sid,"MM"))
            throw new ArgumentException("Invalid provider message correlation.",nameof(sid));
        using var request=new HttpRequestMessage(HttpMethod.Get,
            "https://api.twilio.com/2010-04-01/Accounts/"+credential.AccountId+"/Messages/"+sid+".json");
        request.Headers.Authorization=new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(credential.AccountId+":"+credential.ApiToken)));
        try
        {
            using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token)
                .ConfigureAwait(false);
            if (response.StatusCode!=HttpStatusCode.OK)
                return new Outcome("Unknown",sid,"ReconcileHTTP"+(int)response.StatusCode);
            byte[]? bytes=await ReadBoundedResponseAsync(response,token).ConfigureAwait(false);
            if (bytes is null) return new Outcome("Unknown",sid,"ResponseTooLarge");
            using JsonDocument doc=JsonDocument.Parse(bytes);
            if (Field(doc.RootElement,"sid")!=sid)
                return new Outcome("Unknown",sid,"CorrelationMismatch");
            return new Outcome(MapStatus(Field(doc.RootElement,"status")),sid,Field(doc.RootElement,"error_code"));
        }
        catch (HttpRequestException) { return new Outcome("Unknown",sid,"ReconcileUncertain"); }
        catch (OperationCanceledException) { return new Outcome("Unknown",sid,"ReconcileUncertain"); }
        catch (JsonException) { return new Outcome("Unknown",sid,"InvalidProviderResponse"); }
    }

    private static async Task<byte[]?> ReadBoundedResponseAsync(
        HttpResponseMessage response,CancellationToken token)
    {
        const int limit=32768;
        if (response.Content.Headers.ContentLength is > limit)
            return null;
        using var stream=await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var memory=new MemoryStream();
        byte[] buffer=new byte[4096];
        int read;
        while ((read=await stream.ReadAsync(buffer.AsMemory(),token).ConfigureAwait(false))>0)
        {
            if (memory.Length+read>limit) return null;
            memory.Write(buffer,0,read);
        }
        return memory.ToArray();
    }

    private static string? Field(JsonElement value,string property)
    {
        if (!value.TryGetProperty(property,out var node) || node.ValueKind==JsonValueKind.Null)
            return null;
        return node.ValueKind==JsonValueKind.String ? node.GetString() : node.ToString();
    }

    internal static string MapStatus(string? status) => status?.ToLowerInvariant() switch
    {
        "delivered" or "read" => "Delivered",
        "failed" or "undelivered" or "canceled" => "Failed",
        "queued" or "accepted" or "sending" or "sent" or "scheduled" => "Accepted",
        _ => "Unknown"
    };

    /// <summary>
    /// Validates Twilio's full URL + alphabetically sorted form HMAC-SHA1.
    /// The exact trusted public HTTPS URL, including its original query string,
    /// must be supplied by the hosting ingress; never trust forwarded Host
    /// headers or use a reconstructed attacker-controlled URL.
    /// </summary>
    internal static bool VerifySignedForm(
        Uri publicUrl,IReadOnlyDictionary<string,string> form,
        string? headerSignature,string authToken)
    {
        if (publicUrl is null || !publicUrl.IsAbsoluteUri || publicUrl.Scheme!=Uri.UriSchemeHttps
            || publicUrl.UserInfo.Length!=0 || publicUrl.Fragment.Length!=0
            || form is null || form.Count>48 || string.IsNullOrWhiteSpace(headerSignature)
            || headerSignature.Length>128 || string.IsNullOrWhiteSpace(authToken)
            || publicUrl.AbsoluteUri.Length>4096)
            return false;
        var payload=new StringBuilder(publicUrl.AbsoluteUri);
        foreach (var (key,value) in form.OrderBy(p=>p.Key,StringComparer.Ordinal))
        {
            if (key.Length>128 || value is null || value.Length>16000)
                return false;
            payload.Append(key).Append(value);
        }
        byte[] provided=new byte[64];
        if (!Convert.TryFromBase64String(headerSignature,provided,out int length))
            return false;
        using var mac=new HMACSHA1(Encoding.UTF8.GetBytes(authToken));
        byte[] expected=mac.ComputeHash(Encoding.UTF8.GetBytes(payload.ToString()));
        try { return length==expected.Length &&
            CryptographicOperations.FixedTimeEquals(expected,provided.AsSpan(0,length)); }
        finally {
            CryptographicOperations.ZeroMemory(expected);
            CryptographicOperations.ZeroMemory(provided);
        }
    }

    internal sealed record VerifiedReceipt(string MessageSid,string Status,string EventId);

    /// <summary>Pure validation only. Caller is responsible for persisting a
    /// verified receipt using msg.ApplyVerifiedReceipt after authentication.</summary>
    internal static VerifiedReceipt VerifyCallback(
        CommunicationProviderCredentialStore.Credential credential,
        Uri trustedPublicUrl,IReadOnlyDictionary<string,string> form,string? signature)
    {
        ValidateCredentials(credential);
        if (form.Count>48 || !VerifySignedForm(trustedPublicUrl,form,signature,credential.ApiToken)
            || !form.TryGetValue("AccountSid",out string? account)
            || account!=credential.AccountId
            || !form.TryGetValue("MessageSid",out string? sid)
            || (!IsSid(sid,"SM") && !IsSid(sid,"MM"))
            || !form.TryGetValue("MessageStatus",out string? status)
            || status is null || status.Length>25)
            throw new UnauthorizedAccessException("Messaging delivery evidence failed authentication.");
        string outcome=MapStatus(status);
        if (outcome=="Unknown")
            throw new InvalidDataException("Unrecognized delivery status was not recorded.");
        // Twilio callbacks need not carry a stable unique event ID. An
        // identical signed SID+status+error combination is one replay class.
        string error=form.TryGetValue("ErrorCode",out string? code)?code:"";
        if (error.Length>50) throw new InvalidDataException("Invalid provider error code.");
        byte[] digest=SHA256.HashData(Encoding.UTF8.GetBytes(
            credential.AccountId+"|"+sid+"|"+status+"|"+error));
        string eventId="twilio-"+Convert.ToHexString(digest);
        return new VerifiedReceipt(sid!,outcome,eventId);
    }
}
