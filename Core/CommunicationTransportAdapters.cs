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
/// Transport mode is independent of channel. A school can keep the same
/// audience, consent, preview, audit and history while changing its provider.
/// Operator handoff NEVER means the desktop has sent or delivered a message.
/// </summary>
internal enum CommunicationDeliveryMode { DirectProvider, OperatorHandoff }

internal sealed record CommunicationDeliveryProfile(
    string Channel,string ProviderCode,string DisplayName,CommunicationDeliveryMode Mode);

/// <summary>
/// Central whitelist shared by settings and dispatch. A listed profile is a
/// possible future integration, not a production-enabled adapter. Manual
/// handoffs use a separate, operator-reviewed workflow; they cannot implement
/// the HTTP submit interface or claim a provider-delivered outcome.
/// </summary>
internal static class CommunicationTransportRegistry
{
    private static readonly IReadOnlyList<CommunicationDeliveryProfile> Catalog =
        Array.AsReadOnly(new[]
        {
            new CommunicationDeliveryProfile("Sms","Twilio","Twilio SMS",CommunicationDeliveryMode.DirectProvider),
            new CommunicationDeliveryProfile("WhatsApp","Twilio","Twilio WhatsApp",CommunicationDeliveryMode.DirectProvider),
            new CommunicationDeliveryProfile("WhatsApp","MetaCloud","Meta WhatsApp Cloud API",CommunicationDeliveryMode.DirectProvider),
            new CommunicationDeliveryProfile("WhatsApp","WhatsAppBusinessManual","WhatsApp Business (manual)",CommunicationDeliveryMode.OperatorHandoff),
            new CommunicationDeliveryProfile("Email","Smtp","School email (SMTP)",CommunicationDeliveryMode.DirectProvider),
            new CommunicationDeliveryProfile("Email","EmailClientManual","Email client (manual)",CommunicationDeliveryMode.OperatorHandoff)
        });

    internal static IReadOnlyList<CommunicationDeliveryProfile> PlannedProfiles => Catalog;

    internal static CommunicationDeliveryProfile? FindProfile(string channel,string providerCode) =>
        Catalog.FirstOrDefault(profile =>
            string.Equals(profile.Channel,channel,StringComparison.Ordinal)
            && string.Equals(profile.ProviderCode,providerCode,StringComparison.Ordinal));

    internal static bool IsPlannedProfile(string channel,string providerCode) =>
        FindProfile(channel,providerCode) is not null;

    internal static ICommunicationTransportAdapter ResolveForDispatch(
        string channel,string providerCode)
    {
        CommunicationDeliveryProfile profile=FindProfile(channel,providerCode)
            ?? throw new InvalidOperationException("Unsupported school communication channel/provider pair.");
        if (profile.Mode==CommunicationDeliveryMode.OperatorHandoff)
            throw new NotSupportedException("An operator handoff is not an API send or delivery receipt.");
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
