using System.Reflection;
using FMSC_SMS.Core;
using FMSC_SMS.Pages.Setup;
using FMSC_SMS.Pages.Setup.Academics;

internal static partial class Program
{
    private static void CheckSetupValidationLayout()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Directory.CreateDirectory("artifacts/setup-validation");
        foreach (bool campus in new[] { true, false })
        // Campus now uses the full-width stacked editor: 710px can fit two
        // columns. Use 560px to exercise the actual one-column fallback too.
        foreach (int width in new[] { 560, 1700 })
        {
            using MasterPage page = campus ? new CampusesPage(false) : new AcademicYearsPage(false);
            var fields = (Dictionary<string, Control>)typeof(MasterPage).GetField("_controls", flags)!.GetValue(page)!;
            using var host = new Form { ClientSize = new Size(width, 760) };
            page.Dock = DockStyle.Fill;
            host.Controls.Add(page);
            host.Show();
            Application.DoEvents();
            var editor = (TableLayoutPanel)typeof(MasterPage).GetField("_editor", flags)!.GetValue(page)!;
            Check(editor.ColumnCount >= 1 && editor.Width > 0 && editor.Height > 0,
                "Setup editor remains usable without prescribing its number of columns");
            if (campus)
            {
                fields["CampusName"].Text = "Test Campus";
                ((ModernComboBox)fields["CampusType"]).SelectedIndex = 0;
                fields["ContactNumber"].Text = "443333";
                fields["Email"].Text = "eee222";
            }
            else
            {
                fields["YearName"].Text = "4457777";
                ((ModernDatePicker)fields["StartDate"]).Value = new DateTime(2028, 10, 31);
                ((ModernDatePicker)fields["EndDate"]).Value = new DateTime(2027, 10, 31);
            }
            bool valid = (bool)page.GetType().GetMethod("CanSaveRecord", flags)!.Invoke(page, null)!;
            Check(!valid, "Malformed " + (campus ? "campus contacts" : "academic year/date range") + " refuses Save before SQL");
            Application.DoEvents();
            var labels = (Dictionary<Control, Label>)typeof(MasterPage).GetField("_editorValidationLabels", flags)!.GetValue(page)!;
            foreach (string key in campus ? new[] { "ContactNumber", "Email" } : new[] { "YearName", "EndDate" })
            {
                Label label = labels[fields[key]];
                Check(label.Visible && label.Text.Length > 0, key + " has a dedicated validation message");
                Check(label.Parent!.ClientRectangle.Contains(label.Bounds),
                    key + " validation message is fully visible within its container");
                var measured = TextRenderer.MeasureText(label.Text, label.Font, new Size(label.Width, int.MaxValue),
                    TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
                Check(measured.Height <= label.Height, key + " message fits its reserved height");
            }
            using var image = new Bitmap(host.ClientSize.Width, host.ClientSize.Height);
            host.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
            image.Save("artifacts/setup-validation/" + (campus ? "campus" : "academic-year") + "-" + width + ".png");
            if (!campus)
            {
                fields["YearName"].Text = "2027-2028";
                ((ModernDatePicker)fields["StartDate"]).Value = new DateTime(2027, 4, 1);
                ((ModernDatePicker)fields["EndDate"]).Value = new DateTime(2028, 3, 31);
                Check((bool)page.GetType().GetMethod("CanSaveRecord", flags)!.Invoke(page, null)!, "Correcting the name and date order permits validation");
            }
        }
    }

    private static void CheckCompactCatalogLayouts()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        foreach (var size in new[] { new Size(1100, 670), new Size(850, 530) })
        foreach (var catalog in Enum.GetValues<SetupCatalog>())
        {
            using MasterPage page = catalog switch {
                SetupCatalog.Campuses => new CampusesPage(false),
                SetupCatalog.Classes => new ClassesPage(false, initialize: false),
                _ => new SectionsPage(false, initialize: false)
            };
            page.PrepareForEmbedding(keepActions: true);
            page.HideRecordsChrome();
            page.UseCompactCatalogEditor();
            using var host = new Form { ClientSize = size };
            page.Dock = DockStyle.Fill; host.Controls.Add(page); host.Show(); Application.DoEvents();
            var card = (HumanCard)typeof(MasterPage).GetField("_editorCard", flags)!.GetValue(page)!;
            var editor = (TableLayoutPanel)typeof(MasterPage).GetField("_editor", flags)!.GetValue(page)!;
            Check(card.Content.ClientSize.Height > 0,
                catalog + " retains a usable editor viewport at " + size.Width);
            Check(editor.Bottom <= card.Content.ClientSize.Height - card.Content.Padding.Bottom ||
                  card.Content.VerticalScroll.Visible,
                catalog + " fields fit or can be reached by scrolling");
            var messages = (Dictionary<Control, Label>)typeof(MasterPage).GetField("_editorValidationLabels", flags)!.GetValue(page)!;
            Check(messages.Values.All(label => !label.Visible || string.IsNullOrWhiteSpace(label.Text)),
                catalog + " shows no stale validation errors before editing");
            if (catalog == SetupCatalog.Campuses)
            {
                var fields = (Dictionary<string, Control>)typeof(MasterPage).GetField("_controls", flags)!.GetValue(page)!;
                fields["CampusName"].Text = "Fictional campus";
                ((ModernComboBox)fields["CampusType"]).SelectedIndex = 0;
                fields["ContactNumber"].Text = "123"; fields["Email"].Text = "invalid";
                Check(!(bool)page.GetType().GetMethod("CanSaveRecord", flags)!.Invoke(page, null)!, "Compact campus editor retains contact validation");
                Application.DoEvents();
                Check(messages.Values.Count(label => label.Visible && !string.IsNullOrWhiteSpace(label.Text)) >= 2,
                    "Invalid campus contacts display both field-level error messages regardless of editor height");
                foreach (string key in new[] { "ContactNumber", "Email" })
                {
                    var label = messages[fields[key]];
                    var measured = TextRenderer.MeasureText(label.Text, label.Font, new Size(label.Width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
                    Check(label.Visible && measured.Height <= label.Height, key + " validation remains readable in the compact editor");
                }
                fields["ContactNumber"].Text = "03001234567"; fields["Email"].Text = "office@example.com";
                Application.DoEvents();
                Check((bool)page.GetType().GetMethod("CanSaveRecord", flags)!.Invoke(page, null)!,
                    "Correcting campus contacts restores validation without requiring a fixed card height");
            }
            host.ClientSize = new Size(size.Width == 1100 ? 850 : 1100, size.Height);
            Application.DoEvents();
            Check(editor.Bottom <= card.Content.ClientSize.Height || card.Content.VerticalScroll.Visible,
                catalog + " remains fully visible or scrollable after resizing");
            host.ClientSize = size; Application.DoEvents();
            Directory.CreateDirectory("artifacts/compact-catalogs");
            using var picture = new Bitmap(host.ClientSize.Width, host.ClientSize.Height);
            host.DrawToBitmap(picture, new Rectangle(Point.Empty, picture.Size));
            picture.Save("artifacts/compact-catalogs/" + catalog + "-" + size.Width + ".png");
        }
    }
}
