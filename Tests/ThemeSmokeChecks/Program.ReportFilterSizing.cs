using FMSC_SMS.Core;
using FMSC_SMS.Pages.Reports;

internal static partial class Program
{
    private static void CheckReportFilterSizing()
    {
        using var window = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
            Location = new Point(-30000,-30000), ClientSize = new Size(1600,500) };
        var fields = Enumerable.Range(0,9).Select(index =>
            (Caption: "Filter " + index, Control: (Control)new ModernComboBox())).ToArray();
        using var bar = ReportUi.Bar(fields);
        window.Controls.Add(bar);window.Show();Application.DoEvents();
        foreach (int width in new[] {1600,1200,900,680,1600})
        {
            window.ClientSize = new Size(width,500);Application.DoEvents();
            foreach (var (_,input) in fields)
            {
                var field = input.Parent!;
                Check(input.Left>=field.Padding.Left && input.Right<=field.ClientSize.Width-field.Padding.Right+1,
                    "Shared report inputs stay within their cells at width " + width);
                Check(field.Right<=bar.ClientSize.Width+1,"Report filter rows fit the available width");
            }
            Check(fields.All(pair => pair.Control.Visible && pair.Control.Width > 0 && pair.Control.Height > 0),
                "All nine report filters stay usable across width changes, regardless of row count");
        }
    }
}
