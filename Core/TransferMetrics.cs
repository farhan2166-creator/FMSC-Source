namespace FMSC_SMS.Core;

internal static class TransferMetrics
{
    internal static double AverageSpeed(long bytes, TimeSpan elapsed) =>
        elapsed.TotalSeconds > 0 ? bytes / elapsed.TotalSeconds : 0;

    internal static TimeSpan? EstimateRemaining(long bytes, double speed) =>
        speed > 0 ? TimeSpan.FromSeconds(Math.Min(TimeSpan.MaxValue.TotalSeconds / 2, bytes / speed)) : null;

    internal static string SizeText(double bytes) => bytes >= 1048576 ? $"{bytes / 1048576:0.00} MB" :
        bytes >= 1024 ? $"{bytes / 1024:0.0} KB" : $"{bytes:0} B";

    internal static string DurationText(TimeSpan span) => span.TotalHours >= 1 ?
        $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}" : $"{(int)span.TotalMinutes:00}:{span.Seconds:00}";
}
