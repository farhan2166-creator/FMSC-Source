namespace FMSC_SMS.Core;

/// <summary>Anchored school-time recurrence. Missed dates never become a replay loop.</summary>
internal sealed record CommunicationRecurrence(string Frequency,int Every,DateTime FirstLocal,
    string TimeZoneId,DateTimeOffset End,int MaximumOccurrences,int ExpiryHours)
{
    internal DateTimeOffset NextAfter(DateTimeOffset after)
    {
        if(Frequency is not ("Days" or "Weeks" or "Months") || Every<1 ||
            Every>(Frequency=="Months" ? 12 : Frequency=="Weeks" ? 52 : 365) ||
            MaximumOccurrences is <2 or >366 || ExpiryHours is <1 or >168)
            throw new CommunicationValidationException("Choose a valid bounded recurrence.");
        var zone=TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
        DateTime first=DateTime.SpecifyKind(FirstLocal,DateTimeKind.Unspecified);
        DateTime wall=TimeZoneInfo.ConvertTime(after,zone).DateTime;
        int n=Frequency=="Months" ? Math.Max(0,((wall.Year-first.Year)*12+wall.Month-first.Month)/Every)
            : Math.Max(0,(int)Math.Floor((wall-first).TotalDays/(Every*(Frequency=="Weeks" ? 7 : 1))));
        // The estimate is at most one interval early; keep a small defensive bound.
        for(int attempt=0;attempt<3;attempt++,n++)
        {
            DateTime candidate=Frequency=="Months" ? first.AddMonths(checked(n*Every))
                : first.AddDays(checked(n*Every*(Frequency=="Weeks" ? 7 : 1)));
            var instant=CommunicationSchoolTime.ToInstant(candidate,zone);
            if(instant>after)return instant;
        }
        throw new CommunicationValidationException("The next school-time occurrence could not be resolved. Review the schedule.");
    }
}
