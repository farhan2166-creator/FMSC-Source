namespace FMSC_SMS.Core;

/// <summary>
/// Quiet, content-free desktop Queue alert policy. The Queue itself remains
/// actionable whether Windows notifications are enabled or not.
/// </summary>
internal sealed class CommunicationQueueAlertPolicy
{
    private int previousAttention;
    private DateTime mostRecentAlertUtc=DateTime.MinValue;

    internal bool ShouldAlert(int attention,DateTime nowUtc)
    {
        if(attention<0 || nowUtc.Kind!=DateTimeKind.Utc)
            throw new ArgumentOutOfRangeException(nameof(attention));
        // A previously seen backlog never generates repeated notifications.
        // Quietly reset after the backlog is resolved.
        bool increased=attention>previousAttention;
        previousAttention=attention;
        if(!increased || nowUtc-mostRecentAlertUtc<TimeSpan.FromMinutes(15))return false;
        mostRecentAlertUtc=nowUtc;
        return true;
    }
}
