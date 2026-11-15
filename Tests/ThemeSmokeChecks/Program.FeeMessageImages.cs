using FMSC_SMS.Core;

internal static partial class Program
{
    private static void CheckFeeMessageImages()
    {
        using var first = new Bitmap(150, 75);
        using var last = new Bitmap(75, 150);
        first.SetResolution(150, 150);
        last.SetResolution(150, 150);
        using (var graphics = Graphics.FromImage(first)) graphics.Clear(Color.Black);
        using (var graphics = Graphics.FromImage(last)) graphics.Clear(Color.Gray);
        using var combined = FeeMessageImage.Combine(new Image[] { first, last });
        Check(combined.Size == new Size(200, 300) && combined.GetPixel(10, 10).R == 0
            && combined.GetPixel(10, 290).R == 128 && combined.GetPixel(190, 290).R == 255,
            "Image sharing preserves every page in order, its physical scale, final content and white unused space");

        bool emptyRejected = false;
        try { using var empty = FeeMessageImage.Combine(Array.Empty<Image>()); }
        catch (InvalidOperationException) { emptyRejected = true; }
        using var oversized = new Bitmap(1, 200);
        oversized.SetResolution(1, 1);
        bool oversizedRejected = false;
        try { using var large = FeeMessageImage.Combine(new Image[] { oversized }); }
        catch (CommunicationValidationException) { oversizedRejected = true; }
        Check(emptyRejected && oversizedRejected,
            "Missing or oversized image documents fail clearly instead of silently dropping pages or totals");
    }
}
