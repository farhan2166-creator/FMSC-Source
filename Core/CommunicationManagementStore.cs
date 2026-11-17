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

    private static async Task<DataTable> ReadSessionsAsync(bool unfinished, int limit = 100)
    {
        int actor = CurrentUser.UserId;
        bool students = CurrentUser.HasPermission("Students.View");
        bool fees = students && CurrentUser.HasPermission("Fees.View");
        bool staff = CurrentUser.HasPermission("Staff.View");
        DataTable sessions = await (unfinished
            ? DatabaseHelper.QueryUserAsync("msg.ReadMyUnfinishedBatches")
            : DatabaseHelper.QueryUserAsync("msg.ReadHistory", DatabaseHelper.Int("@Limit", limit)));
        try
        {
            if (actor != CurrentUser.UserId)
                throw new UnauthorizedAccessException("The signed-in school operator changed.");
            LimitVisibleSessions(sessions, students, staff, fees);
            sessions.Columns.Add(new DataColumn("RecipientText", typeof(string)) { DefaultValue = "Restricted" });
            // Search only identities from already permission-filtered saved batches.
            sessions.Columns.Add(new DataColumn("RecipientSearchText", typeof(string)) { DefaultValue = "" });
            sessions.Columns.Add("SourceRowKind",typeof(string));
            sessions.Columns.Add("PlanRuleId",typeof(Guid));
            sessions.Columns.Add("PlanCandidateId",typeof(long));
            foreach(DataRow session in sessions.Rows)session["SourceRowKind"]="Batch";
            if(unfinished)
            {
                using var planned=await DatabaseHelper.QueryUserAsync("msg.ReadUnpreparedWork");
                if(actor!=CurrentUser.UserId)throw new UnauthorizedAccessException("The school sender changed.");
                foreach(DataRow plan in planned.Rows)
                {
                    var entry=sessions.NewRow();
                    foreach(DataColumn column in planned.Columns)
                        if(sessions.Columns.Contains(column.ColumnName))entry[column.ColumnName]=plan[column];
                    entry["BatchId"]=plan["QueueId"];
                    sessions.Rows.Add(entry);
                }
            }
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
                    WHERE (@Unfinished=0 OR B.CreatedBy=@UserId OR EXISTS(
                        SELECT 1 FROM dbo.Users U JOIN dbo.Roles Role ON Role.RoleId=U.RoleId
                        WHERE U.UserId=@UserId AND Role.RoleName=N'Administrator') OR EXISTS(
                        SELECT 1 FROM dbo.UserRoles UR JOIN dbo.Roles Role ON Role.RoleId=UR.RoleId
                        WHERE UR.UserId=@UserId AND Role.RoleName=N'Administrator'))
                      AND ((B.Audience='Staff' AND @Staff=1) OR
                           (B.Audience<>'Staff' AND @Students=1 AND
                            (@Fees=1 OR B.Audience NOT IN ('OutstandingFees','FeeStudent','FeeFamily'))))
                    GROUP BY B.BatchId,B.CampusId,B.ClassId
                )
                SELECT R.BatchId,R.RecipientCount,COALESCE(S.StudentName,T.StaffName) RecipientName,
                    COALESCE(S.AdmissionNumber,T.StaffNumber) RecordNumber,
                    CASE WHEN R.RecipientCount=1 THEN R.LastFour END LastFour,
                    C.CampusName,K.ClassName,Q.SearchText RecipientSearchText
                FROM SavedRecipients R
                LEFT JOIN dbo.Students S ON R.RecipientCount=1 AND S.AdmissionId=R.AdmissionId
                LEFT JOIN dbo.Staff T ON R.RecipientCount=1 AND T.StaffId=R.StaffId
                LEFT JOIN dbo.Campuses C ON C.CampusId=R.CampusId
                LEFT JOIN dbo.Classes K ON K.ClassId=R.ClassId
                OUTER APPLY (
                    SELECT STRING_AGG(CONVERT(nvarchar(max),
                        CONCAT(COALESCE(SS.StudentName,ST.StaffName,N''),N' ',
                               COALESCE(SS.AdmissionNumber,ST.StaffNumber,N''),N' ',
                               COALESCE(SS.FatherName,ST.FatherName,N''),N' ',
                               COALESCE(SS.GuardianName,N''))),N' ') SearchText
                    FROM msg.Recipient SR
                    LEFT JOIN dbo.Students SS ON SS.AdmissionId=SR.AdmissionId
                    LEFT JOIN dbo.Staff ST ON ST.StaffId=SR.StaffId
                    WHERE SR.BatchId=R.BatchId
                ) Q;
                """, DatabaseHelper.Int("@UserId", actor), DatabaseHelper.Bit("@Unfinished", unfinished),
                DatabaseHelper.Bit("@Students", students), DatabaseHelper.Bit("@Fees", fees), DatabaseHelper.Bit("@Staff", staff),
                DatabaseHelper.P("@BatchIds", JsonSerializer.Serialize(sessions.AsEnumerable().Where(row=>Convert.ToString(row["SourceRowKind"])=="Batch").Select(row => row.Field<Guid>("BatchId")))));
            if (actor != CurrentUser.UserId) return sessions;

            var labels = recipients.AsEnumerable().ToDictionary(row => row.Field<Guid>("BatchId"), FormatSessionRecipient);
            var searchable = recipients.AsEnumerable().ToDictionary(
                row => row.Field<Guid>("BatchId"),
                row => row.Field<string>("RecipientSearchText") ?? "");
            foreach (DataRow session in sessions.Rows)
            {
                var id = session.Field<Guid>("BatchId");
                if (labels.TryGetValue(id, out string? label)) session["RecipientText"] = label;
                if (searchable.TryGetValue(id, out string? names)) session["RecipientSearchText"] = names;
            }
            return sessions;
        }
        catch
        {
            sessions.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The SQL history grant is distinct from staff/student/finance grants.
    /// Never surface a session summary for a record type the viewer cannot
    /// see, even when it contains no recipient name or phone.
    /// </summary>
    internal static void LimitVisibleSessions(DataTable sessions,
        bool canSeeStudents, bool canSeeStaff, bool canSeeFees)
    {
        if (!sessions.Columns.Contains("Audience"))
            throw new InvalidDataException("Communication session audience is missing.");
        foreach (DataRow row in sessions.Rows.Cast<DataRow>().ToArray())
        {
            string audience = Convert.ToString(row["Audience"]) ?? "";
            bool visible = audience switch
            {
                "Staff" => canSeeStaff,
                "OutstandingFees" or "FeeStudent" or "FeeFamily" =>
                    canSeeStudents && canSeeFees,
                _ => canSeeStudents
            };
            if (!visible) row.Delete();
        }
        sessions.AcceptChanges();
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
                LanguageCode: row.Field<string>("LanguageCode") ?? "en"))).ToArray();
    }

    internal static Task<DataTable> ReadMessageLanguageAsync()
    {
        if (!CurrentUser.HasPermission("Communication.Send")
            && !CurrentUser.HasPermission("Communication.Configure"))
            throw new UnauthorizedAccessException("Communication permission is required.");
        return DatabaseHelper.QueryUserAsync("msg.ReadMessageLanguage");
    }

    internal static Task SaveMessageLanguageAsync(string languageCode)
    {
        if (!CurrentUser.HasPermission("Communication.Configure"))
            throw new UnauthorizedAccessException("Only a communication administrator may set the default language.");
        if (languageCode is not ("en" or "ur"))
            throw new ArgumentException("Choose English or Urdu.", nameof(languageCode));
        return DatabaseHelper.ExecuteUserAsync("msg.SaveMessageLanguage",
            DatabaseHelper.P("@LanguageCode", languageCode));
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

    internal static Task LinkCorrectionBatchAsync(Guid originalBatch, Guid correctionBatch, string reason)
    {
        if (!CurrentUser.HasPermission("Communication.Send") || !CurrentUser.HasPermission("Communication.View"))
            throw new UnauthorizedAccessException("A correction requires Send and History access.");
        if (originalBatch==Guid.Empty || correctionBatch==Guid.Empty || originalBatch==correctionBatch ||
            string.IsNullOrWhiteSpace(reason) || reason.Trim().Length is <8 or >200)
            throw new ArgumentException("Choose the original message and explain the correction.");
        return DatabaseHelper.ExecuteUserAsync("msg.LinkCorrectionBatch",
            DatabaseHelper.P("@OriginalBatchId",originalBatch),
            DatabaseHelper.P("@CorrectionBatchId",correctionBatch),
            DatabaseHelper.P("@Reason",reason.Trim()));
    }

    internal static Task<DataTable> ReadTemplateRevisionsAsync(int templateId)
    {
        if (!CurrentUser.HasPermission("Communication.Configure"))
            throw new UnauthorizedAccessException("Template version history requires communication administrator access.");
        if (templateId <= 0) throw new ArgumentOutOfRangeException(nameof(templateId));
        return DatabaseHelper.QueryUserAsync("msg.ReadTemplateRevisions",
            DatabaseHelper.Int("@TemplateId", templateId));
    }

    internal static Task RestoreTemplateRevisionAsync(int templateId, int revisionNumber)
    {
        if (!CurrentUser.HasPermission("Communication.Configure"))
            throw new UnauthorizedAccessException("Restoring a template requires communication administrator access.");
        if (templateId <= 0 || revisionNumber <= 0)
            throw new ArgumentException("Choose an existing template version to restore.");
        return DatabaseHelper.ExecuteUserAsync("msg.RestoreTemplateRevision",
            DatabaseHelper.Int("@TemplateId", templateId),
            DatabaseHelper.Int("@RevisionNumber", revisionNumber));
    }

    internal static async Task SaveTemplateAsync(
        int templateId, string name, string channel, string purpose, string body, bool active, string languageCode = "en")
    {
        if (!CurrentUser.HasPermission("Communication.Configure"))
            throw new UnauthorizedAccessException("Communication Settings permission is required.");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(body))
            throw new ArgumentException("Template name and message are required.");
        CommunicationPreparedMessages.ValidateFormat(body, purpose,allowEventContext:CommunicationPreparedMessages.HasEventContext(body));
        await DatabaseHelper.QueryUserAsync("msg.SaveTemplate",
            DatabaseHelper.Int("@TemplateId", templateId),
            DatabaseHelper.P("@Name", name.Trim()),
            DatabaseHelper.P("@Channel", channel),
            DatabaseHelper.P("@Purpose", purpose),
            DatabaseHelper.P("@Body", body),
            DatabaseHelper.Bit("@Active", active),
            DatabaseHelper.P("@LanguageCode", languageCode));
    }
}
