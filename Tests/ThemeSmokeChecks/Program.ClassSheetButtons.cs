using FMSC_SMS.Core;
using FMSC_SMS.Pages.Reports.Students;

internal static partial class Program
{
    private static void CheckClassSheetButtons()
    {
        var originalTheme = AppTheme.CurrentTheme;
        try
        {
            foreach (var theme in new[] { AppThemeType.OceanBlue, AppThemeType.ProfessionalSimple })
            {
                AppTheme.SetTheme(theme);
                using var page = new ClassSheetsPage();
                var captions=Descendants(page).OfType<Label>()
                    .Select(label=>label.Text.Trim().TrimEnd('*').Trim())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                Check(new[]{"Campus","Academic Year","Class","Section","Sheet"}.All(captions.Contains),
                    "Class sheets retain the required school, placement and sheet-selection inputs regardless of layout");
                var load = Descendants(page).OfType<ModernButton>().Single(b => b.Text == "Load Students");
                var row = load.Parent!.Parent!;
                var root = (TableLayoutPanel)row.Parent!;
                root.Parent!.Controls.Remove(root);
                using var form = new Form { AutoScaleMode = AutoScaleMode.None, ShowInTaskbar = false,
                    StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000) };
                form.Controls.Add(root);
                form.Show();
                foreach (int width in new[] { 1000, 650, 1280 })
                {
                    form.ClientSize = new Size(width, 620);
                    Application.DoEvents();
                    foreach (var button in Descendants(row).OfType<ModernButton>())
                    {
                        Check(button.Visible, $"{theme}/{width}: {button.Text} is visible");
                        for (Control child = button; child != root; child = child.Parent!)
                            Check(child.Parent!.ClientRectangle.Contains(child.Bounds),
                                $"{theme}/{width}: {button.Text} fits {child.Parent.GetType().Name}: {child.Bounds} in {child.Parent.ClientRectangle}");
                    }
                }
                Check(!page.IsHandleCreated, "Layout verification starts no database lookup");
            }
        }
        finally { AppTheme.SetTheme(originalTheme); }
    }
}
