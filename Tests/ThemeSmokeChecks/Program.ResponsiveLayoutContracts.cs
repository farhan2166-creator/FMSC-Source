using System.Drawing;
using System.Windows.Forms;

internal static partial class Program
{
    // UI contracts should catch off-center or clipped controls, not differences of
    // one or two physical pixels caused by DPI scaling, fonts or table layout rounding.
    // Keep containment strict and use a small fraction of the available width for alignment.
    private static bool HorizontallyCentered(Rectangle bounds, Rectangle container)
    {
        if (bounds.Width <= 0 || container.Width <= 0 ||
            bounds.Left < container.Left || bounds.Right > container.Right)
            return false;

        double actualCenter = (bounds.Left + bounds.Right) / 2d;
        double expectedCenter = (container.Left + container.Right) / 2d;
        double tolerance = Math.Max(FMSC_SMS.Core.AppScale.Scale(8), container.Width * 0.05d);
        return Math.Abs(actualCenter - expectedCenter) <= tolerance;
    }

    private static Rectangle BoundsWithin(Control control, Control container) =>
        container.RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
}
