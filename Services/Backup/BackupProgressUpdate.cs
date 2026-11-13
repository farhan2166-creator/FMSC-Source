namespace FMSC_SMS.Services.Backup;

/// <summary>
/// Human-readable, ordered stages with optional technical detail. The caller
/// reports only milestones actually reached, never fabricated percentages.
/// </summary>
internal sealed record BackupProgressUpdate(string Stage, string Detail = "",
    string Destination = "", bool IsError = false, BackupTransferProgress? Transfer = null)
{
    internal string Display => Destination.Length == 0
        ? Stage : Destination + " — " + Stage;

    internal static string? NetworkFailureMessage(Exception ex) =>
        ex is HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError }
            ? "Windows could not find the cloud server. Check your network or DNS connection, then try again."
            : null;
}

internal sealed record BackupTransferProgress(long Bytes, long? TotalBytes, double BytesPerSecond);
