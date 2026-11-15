using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace FMSC_SMS.Core;

/// <summary>All printable pages in one clipboard image, without changing the RDLC content.</summary>
internal static class FeeMessageImage
{
    internal const int Dpi = 200;

    internal static Bitmap Combine(IEnumerable<MemoryStream> streams)
    {
        var pages = new List<Metafile>();
        try
        {
            foreach (var stream in streams)
            {
                stream.Position = 0;
                pages.Add(new Metafile(stream));
            }
            return Combine(pages);
        }
        finally { foreach (var page in pages) page.Dispose(); }
    }

    internal static Bitmap Combine(IReadOnlyList<Image> pages)
    {
        if (pages.Count == 0) throw new InvalidOperationException("The fee document has no printable pages.");
        var sizes = pages.Select(page => new Size(
            (int)Math.Ceiling(page.Width / (double)page.HorizontalResolution * Dpi),
            (int)Math.Ceiling(page.Height / (double)page.VerticalResolution * Dpi))).ToArray();
        int width = sizes.Max(size => size.Width);
        long height = sizes.Sum(size => (long)size.Height);
        // Refuse an oversized image rather than cropping a child or the financial totals.
        if (sizes.Any(size => size.Width <= 0 || size.Height <= 0) || width > 32767 || height > 32767
            || (long)width * height > 32_000_000)
            throw new CommunicationValidationException("This document is too large for one image. Send a text message or export it from the fee report instead.");
        var result = new Bitmap(width, (int)height, PixelFormat.Format24bppRgb);
        try
        {
            result.SetResolution(Dpi, Dpi);
            using var graphics = Graphics.FromImage(result);
            graphics.Clear(Color.White);
            graphics.PageUnit = GraphicsUnit.Pixel;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            int top = 0;
            for (int index = 0; index < pages.Count; index++)
            {
                graphics.DrawImage(pages[index], new Rectangle(0, top, sizes[index].Width, sizes[index].Height));
                top += sizes[index].Height;
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }
}
