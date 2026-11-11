namespace FMSC_SMS.Services.Backup;

/// <summary>
/// Pure backup scheduling policy, using the database host's local wall-clock
/// times. The last successful verified SQL backup is the watermark. If the
/// host was off at a scheduled time, the next eligible run catches it up.
/// The very first run is due immediately so a newly enrolled school is not
/// left without an off-device recovery point until the following week.
/// </summary>
internal static class CloudBackupSchedulePolicy
{
    internal static bool IsDue(
        bool enabled,
        CloudBackupFrequency frequency,
        TimeOnly localTime,
        DayOfWeek weeklyDay,
        DateTime localNow,
        DateTime? lastSuccessfulLocal)
    {
        if (!enabled || frequency == CloudBackupFrequency.Manual)
            return false;
        if (!Enum.IsDefined(frequency) || !Enum.IsDefined(weeklyDay))
            throw new ArgumentOutOfRangeException(nameof(frequency),
                "Unsupported automatic backup schedule.");
        if (lastSuccessfulLocal is null) return true;

        DateTime mostRecentDue = MostRecentScheduledTime(
            frequency, localTime, weeklyDay, localNow);
        return lastSuccessfulLocal.Value < mostRecentDue;
    }

    internal static DateTime MostRecentScheduledTime(
        CloudBackupFrequency frequency,
        TimeOnly localTime,
        DayOfWeek weeklyDay,
        DateTime localNow)
    {
        DateTime due = localNow.Date.Add(localTime.ToTimeSpan());
        switch (frequency)
        {
            case CloudBackupFrequency.Daily:
                if (due > localNow) due = due.AddDays(-1);
                break;
            case CloudBackupFrequency.Every12Hours:
                // Consider the second occurrence today as well as missed runs.
                if (due.AddHours(12) <= localNow) due = due.AddHours(12);
                if (due > localNow) due = due.AddHours(-12);
                if (due > localNow) due = due.AddHours(-12);
                break;
            case CloudBackupFrequency.Weekly:
                int daysSince = ((int)localNow.DayOfWeek - (int)weeklyDay + 7) % 7;
                due = due.AddDays(-daysSince);
                if (due > localNow) due = due.AddDays(-7);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(frequency),
                    "Automatic scheduling requires a supported interval.");
        }
        return due;
    }
}

internal enum CloudBackupFrequency
{
    Manual = 0,
    Every12Hours = 1,
    Daily = 2,
    Weekly = 3
}
