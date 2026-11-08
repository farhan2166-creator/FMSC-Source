namespace FMSC_SMS.Core;

/// <summary>
/// Provider-neutral manual transport contract. A claimed attempt is always
/// persisted before an adapter sends. No adapter starts work on its own.
/// </summary>
internal sealed record CommunicationDeliveryOutcome(string State, string? ProviderMessageId, string? FailureCode);

internal interface ICommunicationTransportAdapter
{
    string ProviderCode { get; }
    Task<CommunicationDeliveryOutcome> SendOnceAsync(
        string destination,string body,HttpClient client,CancellationToken token);
    Task<CommunicationDeliveryOutcome> ReadStatusAsync(
        string providerMessageId,HttpClient client,CancellationToken token);
}

/// <summary>
/// Fail-closed provider selection. Meta Cloud API and school-controlled email
/// are future adapters, not aliases for Twilio. Unimplemented providers can
/// be recorded as disabled profiles but can never dispatch from this registry.
/// </summary>
internal static class CommunicationTransportRegistry
{
    internal static bool IsPlannedProfile(string channel,string providerCode) =>
        (channel,providerCode) is
            ("Sms","Twilio") or ("WhatsApp","Twilio") or
            ("WhatsApp","MetaCloud") or ("Email","Smtp");

    internal static ICommunicationTransportAdapter ResolveForDispatch(
        string channel,string providerCode)
    {
        if (!IsPlannedProfile(channel,providerCode))
            throw new InvalidOperationException("Unsupported school communication channel/provider pair.");
        if (providerCode!="Twilio")
            throw new NotSupportedException("This messaging provider has no accepted live transport. Sending remains unavailable.");
        var credential=CommunicationProviderCredentialStore.LoadForDispatch(channel,providerCode);
        CommunicationTwilioTransport.ValidateCredentials(credential);
        return new TwilioAdapter(credential);
    }

    private sealed class TwilioAdapter(
        CommunicationProviderCredentialStore.Credential credential) : ICommunicationTransportAdapter
    {
        public string ProviderCode => CommunicationTwilioTransport.ProviderCode;

        public async Task<CommunicationDeliveryOutcome> SendOnceAsync(
            string destination,string body,HttpClient client,CancellationToken token)
        {
            CommunicationTwilioTransport.Outcome result=
                await CommunicationTwilioTransport.SendOnceAsync(
                    credential,destination,body,client,token).ConfigureAwait(false);
            return new CommunicationDeliveryOutcome(result.State,result.MessageSid,result.ErrorCode);
        }

        public async Task<CommunicationDeliveryOutcome> ReadStatusAsync(
            string providerMessageId,HttpClient client,CancellationToken token)
        {
            CommunicationTwilioTransport.Outcome result=
                await CommunicationTwilioTransport.ReadStatusAsync(
                    credential,providerMessageId,client,token).ConfigureAwait(false);
            return new CommunicationDeliveryOutcome(result.State,result.MessageSid,result.ErrorCode);
        }
    }
}
