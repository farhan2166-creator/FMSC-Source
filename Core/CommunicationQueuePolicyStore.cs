using System.Data;

namespace FMSC_SMS.Core;

/// <summary>
/// Permission-checked configuration boundary for future unattended communication.
/// Schema 23 starts with background automation Off, and SQL ClaimRecipient remains
/// the final authority for master pause and reviewed batch due dates.
/// </summary>
internal static class CommunicationQueuePolicyStore
{
    private static void RequireAdministrator()
    {
        if (!CurrentUser.HasPermission("Communication.Configure"))
            throw new UnauthorizedAccessException("Communication administrator permission is required.");
    }

    internal static Task<DataTable> ReadDispatchControlAsync()
    {
        RequireAdministrator();
        return DatabaseHelper.QueryUserAsync("msg.ReadDispatchControl");
    }

    internal static Task<DataTable> ReadProviderQuotasAsync()
    {
        RequireAdministrator();
        return DatabaseHelper.QueryUserAsync("msg.ReadProviderQuota");
    }

    internal static Task<DataTable> ReadAutomationRulesAsync()
    {
        RequireAdministrator();
        return DatabaseHelper.QueryUserAsync("msg.ReadAutomationRules");
    }

    internal static Task SetMasterPauseAsync(bool paused)
    {
        RequireAdministrator();
        return DatabaseHelper.ExecuteUserAsync("msg.SetMasterPause",
            DatabaseHelper.Bit("@Paused", paused));
    }

    internal static Task SaveSendingWindowAsync(
        TimeOnly start, TimeOnly end, string timeZoneId, int maximumPerMinute,
        bool enableUnattended = false)
    {
        RequireAdministrator();
        if (start >= end || maximumPerMinute is < 1 or > 600 ||
            string.IsNullOrWhiteSpace(timeZoneId) || timeZoneId.Length > 128)
            throw new ArgumentException("Choose valid school sending hours, timezone and maximum speed.");
        return DatabaseHelper.ExecuteUserAsync("msg.SaveDispatchControl",
            DatabaseHelper.Bit("@UnattendedEnabled", enableUnattended),
            DatabaseHelper.P("@WindowStart", start.ToTimeSpan()),
            DatabaseHelper.P("@WindowEnd", end.ToTimeSpan()),
            DatabaseHelper.P("@TimeZoneId", timeZoneId),
            DatabaseHelper.Int("@MaxPerMinute", maximumPerMinute));
    }

    internal static Task SaveProviderAllowanceAsync(
        string channel, string provider, int dailyLimit, int reservedImportant)
    {
        RequireAdministrator();
        if (!((channel == "Sms" && provider == "Twilio") ||
              (channel == "WhatsApp" && provider == "MetaCloud")) ||
            dailyLimit is < 1 or > 1000000 ||
            reservedImportant < 0 || reservedImportant >= dailyLimit)
            throw new ArgumentException("Choose a supported API provider and a valid daily allowance.");
        return DatabaseHelper.ExecuteUserAsync("msg.SaveProviderQuota",
            DatabaseHelper.P("@Channel", channel),
            DatabaseHelper.P("@ProviderCode", provider),
            DatabaseHelper.Int("@DailyLimit", dailyLimit),
            DatabaseHelper.Int("@ReservedImportant", reservedImportant));
    }

    internal static Task SaveAutomationModeAsync(
        string eventCode, string mode, int campusId = 0, int classId = 0)
    {
        RequireAdministrator();
        if (!new[] { "Off", "Review", "Automatic" }.Contains(mode, StringComparer.Ordinal) ||
            string.IsNullOrWhiteSpace(eventCode) || campusId < 0 || classId < 0 ||
            (classId > 0 && campusId == 0))
            throw new ArgumentException("Choose a valid automation rule, mode and audience.");
        return DatabaseHelper.ExecuteUserAsync("msg.SaveAutomationMode",
            DatabaseHelper.P("@EventCode", eventCode),
            DatabaseHelper.P("@Mode", mode),
            DatabaseHelper.Int("@CampusId", campusId),
            DatabaseHelper.Int("@ClassId", classId));
    }

    internal static Task SaveFeeReminderPolicyAsync(
        string eventCode, int gapDays, int monthlyLimit, int familyDailyLimit)
    {
        RequireAdministrator();
        if (eventCode is not ("OutstandingFees" or "UpcomingDue" or "OverdueFollowUp")
            || gapDays is < 1 or > 90 || monthlyLimit is < 1 or > 12
            || familyDailyLimit is < 1 or > 20)
            throw new ArgumentException("Choose a valid fee event and safe per-family reminder limits.");
        return DatabaseHelper.ExecuteUserAsync("msg.SaveFeeReminderPolicy",
            DatabaseHelper.P("@EventCode", eventCode),
            DatabaseHelper.Int("@GapDays", gapDays),
            DatabaseHelper.Int("@PerMonth", monthlyLimit),
            DatabaseHelper.Int("@PerDay", familyDailyLimit));
    }

    internal static Task<DataTable> ReadTakeoverCandidatesAsync()
    {
        RequireAdministrator();
        foreach (string permission in new[] { "Communication.Send", "Communication.View",
            "Students.View", "Staff.View", "Fees.View" })
            if (!CurrentUser.HasPermission(permission))
                throw new UnauthorizedAccessException("Administrator review requires school record and communication access.");
        return DatabaseHelper.QueryUserAsync("msg.ReadTakeoverCandidates");
    }

    internal static Task TakeOverScheduledBatchAsync(Guid batchId, string reason)
    {
        RequireAdministrator();
        foreach (string permission in new[] { "Communication.Send", "Communication.View",
            "Students.View", "Staff.View", "Fees.View" })
            if (!CurrentUser.HasPermission(permission))
                throw new UnauthorizedAccessException("Administrator takeover requires school record and communication access.");
        if (batchId == Guid.Empty || string.IsNullOrWhiteSpace(reason)
            || reason.Trim().Length is < 5 or > 200)
            throw new ArgumentException("Choose a held batch and enter a reason of 5–200 characters.");
        return DatabaseHelper.ExecuteUserAsync("msg.TakeOverScheduledBatch",
            DatabaseHelper.P("@BatchId", batchId), DatabaseHelper.P("@Reason", reason.Trim()));
    }

    internal static Task<DataTable> ReadPreferredMethodAsync()
    {
        if (!CurrentUser.HasPermission("Communication.Send"))
            throw new UnauthorizedAccessException("Communication.Send is required.");
        return DatabaseHelper.QueryUserAsync("msg.ReadPreferredMethod");
    }

    internal static Task SavePreferredMethodAsync(string channel, string provider)
    {
        RequireAdministrator();
        if (!((channel=="WhatsApp" && provider is "WhatsAppBusinessManual" or "MetaCloud") ||
            (channel=="Sms" && provider=="Twilio")))
            throw new ArgumentException("Choose a supported preferred sending method.");
        return DatabaseHelper.ExecuteUserAsync("msg.SavePreferredMethod",
            DatabaseHelper.P("@Channel",channel),
            DatabaseHelper.P("@ProviderCode",provider));
    }

    internal static Task<DataTable> ReadSchoolWeekdaysAsync()
    {
        RequireAdministrator();
        return DatabaseHelper.QueryUserAsync("msg.ReadSchoolWeekdays");
    }

    internal static Task SetSchoolWeekdayAsync(int isoWeekday, bool enabled)
    {
        RequireAdministrator();
        if (isoWeekday is < 1 or > 7)
            throw new ArgumentOutOfRangeException(nameof(isoWeekday));
        return DatabaseHelper.ExecuteUserAsync("msg.SetSchoolWeekday",
            DatabaseHelper.Int("@IsoWeekday", isoWeekday),
            DatabaseHelper.Bit("@Enabled", enabled));
    }

    internal static Task<DataTable> ReadSchoolHolidaysAsync()
    {
        RequireAdministrator();
        return DatabaseHelper.QueryUserAsync("msg.ReadSchoolHolidays");
    }

    internal static Task SetSchoolHolidayAsync(DateOnly date, string? description, bool remove)
    {
        RequireAdministrator();
        if (date.Year is < 2000 or > 2100 || (!remove &&
            (string.IsNullOrWhiteSpace(description) || description.Length > 150)))
            throw new ArgumentException("Choose a valid date and short holiday description.");
        return DatabaseHelper.ExecuteUserAsync("msg.SetSchoolHoliday",
            DatabaseHelper.P("@HolidayDate", date.ToDateTime(TimeOnly.MinValue)),
            DatabaseHelper.P("@Description", description),
            DatabaseHelper.Bit("@Remove", remove));
    }

    internal static Task<DataTable> ReadScheduledBatchesAsync()
    {
        if (!CurrentUser.HasPermission("Communication.Send"))
            throw new UnauthorizedAccessException("Communication.Send is required.");
        return DatabaseHelper.QueryUserAsync("msg.ReadScheduledBatches");
    }

    internal static Task ScheduleReviewedBatchAsync(
        Guid batchId, DateTimeOffset due, DateTimeOffset? expires, string priority)
    {
        if (!CurrentUser.HasPermission("Communication.Send"))
            throw new UnauthorizedAccessException("Communication.Send is required.");
        if (batchId == Guid.Empty || due <= DateTimeOffset.UtcNow ||
            (expires is not null && expires <= due) ||
            priority is not ("Low" or "Normal" or "High"))
            throw new ArgumentException("Choose a future schedule, valid expiry and priority.");
        return DatabaseHelper.ExecuteUserAsync("msg.ScheduleReviewedBatch",
            DatabaseHelper.P("@BatchId", batchId),
            DatabaseHelper.P("@DueUtc", due.UtcDateTime),
            DatabaseHelper.P("@ExpiresUtc", expires?.UtcDateTime),
            DatabaseHelper.P("@Priority", priority));
    }
}
