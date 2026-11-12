namespace FMSC_SMS.Services.Backup;

/// <summary>
/// Human-readable, ordered stages with optional technical detail. The caller
/// reports only milestones actually reached, never fabricated percentages.
/// </summary>
internal sealed record BackupProgressUpdate(string Stage, string Detail = "",
    string Destination = "", bool IsError = false)
{
    internal string Display => Destination.Length == 0
        ? Stage : Destination + " — " + Stage;
}
