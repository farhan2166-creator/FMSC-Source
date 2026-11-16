using System.Data;
using System.Text.Json;

namespace FMSC_SMS.Core;

/// <summary>
/// Separate permission-controlled reads for Communication History and Settings.
/// No credential values are returned or persisted in the school database.
/// Saving credentials never activates sending. Activation is a separate verified action.
/// </summary>
internal static class CommunicationManagementStore
{
    internal static Task<DataTable> ReadHistoryAsync(int limit = 100)
    {
        if (limit < 1 || limit > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        if (!CurrentUser.HasPermission("Communication.View"))
            throw new UnauthorizedAccessException("Communication History permission is required.");
        return ReadSessionsAsync(unfinished: false, limit);
    }

    internal static async Task<DataTable> ReadSettingsAsync()
    {
        if (!CurrentUser.HasPermission("Communication.Configure"))
            throw new UnauthorizedAccessException("Communication Settings permission is required.");
        return await DatabaseHelper.QueryUserAsync("msg.ReadChannelSettings");
    }

    internal static Task<DataTable> ReadMyUnfinishedBatchesAsync() =>
        ReadSessionsAsync(unfinished: true);

    internal static Task<DataTable> ReadWorkerIdentityAsync(Guid workerId, byte[] secret, byte[] windows, byte[] device) =>
        DatabaseHelper.QuerySqlAsync("EXEC msg.ReadWorkerIdentity @WorkerId,@Token,@WindowsProof,@DeviceProof",
            DatabaseHelper.P("@WorkerId",workerId), DatabaseHelper.P("@Token",secret),
            DatabaseHelper.P("@WindowsProof",windows), DatabaseHelper.P("@DeviceProof",device));

    internal static Task<DataTable> ReadReadyWorkerRecipientsAsync(Guid workerId, byte[] secret, byte[] windows, byte[] device) =>
        DatabaseHelper.QuerySqlAsync("EXEC msg.ReadReadyWorkerRecipients @WorkerId,@Token,@WindowsProof,@DeviceProof",
            DatabaseHelper.P("@WorkerId",workerId), DatabaseHelper.P("@Token",secret),
            DatabaseHelper.P("@WindowsProof",windows), DatabaseHelper.P("@DeviceProof",device));

    internal static Task ExcludeChangedPendingAsync(long recipientId, string reason) =>
        DatabaseHelper.ExecuteUserAsync("msg.ExcludeChangedPending", DatabaseHelper.P("@RecipientId",recipientId),
            DatabaseHelper.P("@ExclusionCode",reason));

    internal static Task SaveBatchScheduleAsync(Guid batchId, DateTime sendAtUtc, DateTime expiresUtc)
    {
        if (!CurrentUser.HasPermission("Communication.Send"))
            throw new UnauthorizedAccessException("School message sending is not authorized.");
        if (batchId == Guid.Empty || sendAtUtc >= expiresUtc || expiresUtc <= DateTime.UtcNow)
            throw new ArgumentException("Choose a valid future schedule and expiry.");
        return DatabaseHelper.ExecuteUserAsync("msg.SaveBatchSchedule",
            DatabaseHelper.P("@BatchId", batchId),
            DatabaseHelper.P("@SendAtUtc", sendAtUtc),
            DatabaseHelper.P("@ExpiresUtc", expiresUtc));
    }

    internal static async Task<string> ReadPreferredMethodAsync()
    {
        if (!CurrentUser.HasPermission("Communication.Send"))
            throw new UnauthorizedAccessException("Messaging permission is required.");
        using var settings = await DatabaseHelper.QueryUserAsync("msg.ReadPreferredMethod");
        return settings.Rows.Count == 1 ? Convert.ToString(settings.Rows[0]["PreferredMethod"]) ?? "ManualWhatsApp"
            : "ManualWhatsApp";
    }

    internal static Task SavePreferredMethodAsync(string method)
    {
        if (!CurrentUser.HasPermission("Communication.Configure"))
            throw new UnauthorizedAccessException("Communication administrator permission is required.");
        if (method is not ("ManualWhatsApp" or "MetaCloud" or "Twilio"))
            throw new ArgumentException("Choose a supported sending method.");
        return DatabaseHelper.ExecuteUserAsync("msg.SavePreferredMethod",DatabaseHelper.P("@Method",method));
    }

    internal static Task<DataTable> ReadDispatchSettingsAsync()
    {
        if (!CurrentUser.HasPermission("Communication.Configure"))
            throw new UnauthorizedAccessException("Communication administrator permission is required.");
        return DatabaseHelper.QueryUserAsync("msg.ReadDispatchSettings");
    }

    internal static Task SaveDispatchSettingsAsync(TimeSpan from, TimeSpan until, byte workingDays,
        int ratePerMinute, int smsDaily, int whatsappDaily, byte familyDaily, byte priorityReserve)
    {
        if (!CurrentUser.HasPermission("Communication.Configure"))
            throw new UnauthorizedAccessException("Communication administrator permission is required.");
        if (from < TimeSpan.Zero || until > TimeSpan.FromHours(24) || from >= until
            || workingDays is < 1 or > 127 || ratePerMinute is < 1 or > 600
            || smsDaily is < 0 or > 100000 || whatsappDaily is < 0 or > 100000
            || familyDaily is < 1 or > 20 || priorityReserve > 50)
            throw new ArgumentException("Choose valid sending hours, days, rate and limits.");
        return DatabaseHelper.ExecuteUserAsync("msg.SaveDispatchSettings",
            DatabaseHelper.P("@SendFrom",from),
            DatabaseHelper.P("@SendUntil",until),
            DatabaseHelper.P("@WorkingDaysMask",workingDays),
            DatabaseHelper.Int("@MaximumPerMinute",ratePerMinute),
            DatabaseHelper.Int("@MaximumSmsDaily",smsDaily),
            DatabaseHelper.Int("@MaximumWhatsAppDaily",whatsappDaily),
            DatabaseHelper.P("@MaximumRoutineFamilyDaily",familyDaily),
            DatabaseHelper.P("@PriorityReservePercent",priorityReserve));
    }

    internal static Task<DataTable> ReadAutomationRulesAsync()
    {
        if (!CurrentUser.HasPermission("Communication.Configure"))
            throw new UnauthorizedAccessException("Communication administrator permission is required.");
        return DatabaseHelper.QueryUserAsync("msg.ReadAutomationRules");
    }

    internal static Task SaveAutomationRuleAsync(string code,string mode,int campus,int cls,string? channel)
    {
        if (!CurrentUser.HasPermission("Communication.Configure"))
            throw new UnauthorizedAccessException("Communication administrator permission is required.");
        if (mode is not ("Off" or "Review" or "Automatic"))
            throw new ArgumentException("Choose Off, Prepare for Review, or Send Automatically.");
        return DatabaseHelper.ExecuteUserAsync("msg.SaveAutomationRule",
            DatabaseHelper.P("@EventCode",code), DatabaseHelper.P("@Mode",mode),
            DatabaseHelper.Int("@CampusId",campus), DatabaseHelper.Int("@ClassId",cls),
            DatabaseHelper.P("@Channel",channel));
    }

    internal static Task<DataTable> ReadSendControlAsync()
    {
        if (!CurrentUser.HasPermission("Communication.Configure"))
            throw new UnauthorizedAccessException("Communication administrator permission is required.");
        return DatabaseHelper.QueryUserAsync("msg.ReadSendControl");
    }

    internal static Task SetSendPausedAsync(bool paused)
    {
        if (!CurrentUser.HasPermission("Communication.Configure"))
            throw new UnauthorizedAccessException("Communication administrator permission is required.");
        return DatabaseHelper.ExecuteUserAsync("msg.SetSendPaused", DatabaseHelper.Bit("@Paused", paused));
    }

    internal static Task RecordHistoryExportAsync(int rowCount, string format)
    {
        if (!CurrentUser.HasPermission("Communication.Configure"))
            throw new UnauthorizedAccessException("Only communication administrators can export history.");
        if (rowCount < 1 || format is not ("CSV" or "PDF"))
            throw new ArgumentException("A nonempty PDF or CSV export is required.");
        return DatabaseHelper.ExecuteUserAsync("msg.RecordHistoryExport",
            DatabaseHelper.Int("@RowCount", rowCount), DatabaseHelper.P("@FileType", format));
    }

    private static async Task<DataTable> ReadSessionsAsync(bool unfinished, int limit = 100)
    {
        int actor = CurrentUser.UserId;
        bool students = CurrentUser.HasPermission("Students.View");
        bool fees = students && CurrentUser.HasPermission("Fees.View");
        bool staff = CurrentUser.HasPermission("Staff.View");
        DataTable sessions = await (unfinished
            ? DatabaseHelper.QueryUserAsync("msg.ReadQueue")
            : DatabaseHelper.QueryUserAsync("msg.ReadHistory", DatabaseHelper.Int("@Limit", limit)));
        try
        {
            sessions.Columns.Add(new DataColumn("RecipientText", typeof(string)) { DefaultValue = "Restricted" });
            if (sessions.Rows.Count == 0 || actor != CurrentUser.UserId || (!students && !staff)) return sessions;

            // Keep the queue and outcome rules in their existing procedures.
            // Resolve the listed batch IDs together and use the saved destination, which can differ from today's contact.
            using var recipients = await DatabaseHelper.QuerySqlAsync("""
                SET NOCOUNT ON;
                IF @Unfinished=1 EXEC dbo.RequirePermission @UserId,N'Communication.Send';
                ELSE EXEC dbo.RequirePermission @UserId,N'Communication.View';
                IF @Students=1 EXEC dbo.RequirePermission @UserId,N'Students.View';
                IF @Fees=1 EXEC dbo.RequirePermission @UserId,N'Fees.View';
                IF @Staff=1 EXEC dbo.RequirePermission @UserId,N'Staff.View';

                ;WITH SavedRecipients AS (
                    SELECT B.BatchId,B.CampusId,B.ClassId,COUNT_BIG(R.RecipientId) RecipientCount,
                        MAX(R.AdmissionId) AdmissionId,MAX(R.StaffId) StaffId,MAX(RIGHT(R.PhoneE164,4)) LastFour
                    FROM OPENJSON(@BatchIds) WITH(BatchId uniqueidentifier '$') I
                    JOIN msg.Batch B ON B.BatchId=I.BatchId
                    LEFT JOIN msg.Recipient R ON R.BatchId=B.BatchId
                    WHERE (@Unfinished=0 OR B.CreatedBy=@UserId)
                      AND ((B.Audience='Staff' AND @Staff=1) OR
                           (B.Audience<>'Staff' AND @Students=1 AND
                            (@Fees=1 OR B.Audience NOT IN ('OutstandingFees','FeeStudent','FeeFamily'))))
                    GROUP BY B.BatchId,B.CampusId,B.ClassId
                )
                SELECT R.BatchId,R.RecipientCount,COALESCE(S.StudentName,T.StaffName) RecipientName,
                    COALESCE(S.AdmissionNumber,T.StaffNumber) RecordNumber,
                    CASE WHEN R.RecipientCount=1 THEN R.LastFour END LastFour,
                    C.CampusName,K.ClassName
                FROM SavedRecipients R
                LEFT JOIN dbo.Students S ON R.RecipientCount=1 AND S.AdmissionId=R.AdmissionId
                LEFT JOIN dbo.Staff T ON R.RecipientCount=1 AND T.StaffId=R.StaffId
                LEFT JOIN dbo.Campuses C ON C.CampusId=R.CampusId
                LEFT JOIN dbo.Classes K ON K.ClassId=R.ClassId;
                """, DatabaseHelper.Int("@UserId", actor), DatabaseHelper.Bit("@Unfinished", unfinished),
                DatabaseHelper.Bit("@Students", students), DatabaseHelper.Bit("@Fees", fees), DatabaseHelper.Bit("@Staff", staff),
                DatabaseHelper.P("@BatchIds", JsonSerializer.Serialize(sessions.AsEnumerable().Select(row => row.Field<Guid>("BatchId")))));
            if (actor != CurrentUser.UserId) return sessions;

            var labels = recipients.AsEnumerable().ToDictionary(row => row.Field<Guid>("BatchId"), FormatSessionRecipient);
            foreach (DataRow session in sessions.Rows)
                if (labels.TryGetValue(session.Field<Guid>("BatchId"), out string? label)) session["RecipientText"] = label;
            return sessions;
        }
        catch
        {
            sessions.Dispose();
            throw;
        }
    }

    internal static string FormatSessionRecipient(DataRow row)
    {
        string Read(string column) => Convert.ToString(row[column])?.Trim() ?? "";
        long count = row.Field<long>("RecipientCount");
        if (count == 1)
        {
            string name = Read("RecipientName"), number = Read("RecordNumber"), lastFour = Read("LastFour");
            string identity = name.Length == 0 ? "School record" : name;
            if (number.Length > 0) identity += $" ({number})";
            return lastFour.Length == 0 ? identity : $"{identity} · •••• {lastFour}";
        }
        return string.Join(" · ", new[] { Read("ClassName"), Read("CampusName"), $"{count:N0} recipients" }
            .Where(part => part.Length > 0));
    }

    internal static Task RecordChannelOptOutAsync(string channel,string phone) =>
        DatabaseHelper.ExecuteUserAsync("msg.RecordChannelOptOut",DatabaseHelper.P("@Channel",channel),DatabaseHelper.P("@Phone",phone));

    internal static async Task<IReadOnlyList<CommunicationPreparedMessages.ComposeTemplate>> ReadComposeTemplatesAsync()
    {
        if (!CurrentUser.HasPermission("Communication.Send"))
            throw new UnauthorizedAccessException("A sender permission is required to read message templates.");
        using var rows = await DatabaseHelper.QueryUserAsync("msg.ReadComposeTemplates");
        return CommunicationPreparedMessages.BuiltInChoices.Concat(rows.AsEnumerable().Select(row =>
            new CommunicationPreparedMessages.ComposeTemplate(row.Field<string>("Name") ?? "", row.Field<string>("Purpose") ?? "",
                row.Field<string>("Body") ?? "", row.Field<string>("Channel") ?? "",
                Language: row.Field<string>("Language") ?? "en"))).ToArray();
    }

    internal static async Task<string> ReadDefaultTemplateLanguageAsync()
    {
        if (!CurrentUser.HasPermission("Communication.Send"))
            throw new UnauthorizedAccessException("Messaging permission is required.");
        using var settings = await DatabaseHelper.QueryUserAsync("msg.ReadDefaultTemplateLanguage");
        return settings.Rows.Count == 1 ? Convert.ToString(settings.Rows[0]["DefaultLanguage"]) ?? "en" : "en";
    }

    internal static Task SetDefaultTemplateLanguageAsync(string language)
    {
        if (!CurrentUser.HasPermission("Communication.Configure"))
            throw new UnauthorizedAccessException("Communication administrator permission is required.");
        if (language is not ("en" or "ur")) throw new ArgumentException("Choose English or Urdu.");
        return DatabaseHelper.ExecuteUserAsync("msg.SetDefaultTemplateLanguage",
            DatabaseHelper.P("@Language",language));
    }

    internal static Task SaveUrduTemplateAsync(int templateId,string? body)
    {
        if (!CurrentUser.HasPermission("Communication.Configure"))
            throw new UnauthorizedAccessException("Communication administrator permission is required.");
        if (templateId<=0) throw new ArgumentOutOfRangeException(nameof(templateId));
        if (body is not null) CommunicationPreparedMessages.ValidateFormat(body);
        return DatabaseHelper.ExecuteUserAsync("msg.SaveTemplateUrdu",
            DatabaseHelper.Int("@TemplateId",templateId),DatabaseHelper.P("@BodyUrdu",body));
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
