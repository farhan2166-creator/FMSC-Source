namespace FMSC_SMS.Core;

/// <summary>Convert an operator's school wall-clock choice independently of the PC timezone.</summary>
internal static class CommunicationSchoolTime
{
    internal static DateTimeOffset ToInstant(DateTime local, TimeZoneInfo schoolZone)
    {
        var wall = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (schoolZone.IsInvalidTime(wall))
            throw new CommunicationValidationException("This time does not exist in the school timezone. Choose another time.");
        if (schoolZone.IsAmbiguousTime(wall))
            throw new CommunicationValidationException("This time occurs twice in the school timezone. Choose an unambiguous time.");
        return new DateTimeOffset(wall, schoolZone.GetUtcOffset(wall));
    }

    internal static DateTime LocalNow(TimeZoneInfo schoolZone) =>
        TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, schoolZone).DateTime;
}
