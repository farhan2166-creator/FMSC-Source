using System.Data;

namespace FMSC_SMS.Core;

/// <summary>
/// Separate permission-controlled reads for Communication History and Settings.
/// No credential values are returned or persisted in the school database.
/// Provider activation and message dispatch are intentionally unavailable.
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
        CommunicationTwilioTransport.ValidateCredentials(credential);
        // Pause the channel BEFORE changing a provider secret or identity.
        // A partial failure can never activate messaging.
        await PauseAsync(credential.Channel);
        CommunicationProviderCredentialStore.Save(credential);
        await DatabaseHelper.ExecuteUserAsync("msg.ConfigureChannel",
            DatabaseHelper.P("@Channel", credential.Channel),
            DatabaseHelper.P("@ProviderCode", credential.ProviderCode));
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
