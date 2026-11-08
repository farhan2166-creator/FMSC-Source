using System.Reflection;
using FMSC_SMS.Core;
using FMSC_SMS.Pages.Students.Admissions;

internal static partial class Program
{
    private static void CheckAdmissionHeader()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        using var page = new StudentAdmissionPage();
        var type = typeof(StudentAdmissionPage);
        T Field<T>(string name) => (T)type.GetField(name, flags)!.GetValue(page)!;
        Field<ModernTextBox>("registrationNo").Text = "FMSC-2026-0018";
        Field<ModernTextBox>("admissionIdValue").Text = "6592";
        var badge = Field<Label>("studentStatusBadge");
        badge.Text = "Active";
        badge.ForeColor = AppTheme.StatusText(Color.Green);
        badge.BackColor = AppTheme.Tint(Color.Green, .88f);
        var row = (TableLayoutPanel)badge.Parent!.Parent!;
        var header = (Panel)row.Parent!;
        var title = row.Controls.OfType<Label>().Single();
        Check(!string.IsNullOrWhiteSpace(title.Text) && badge.Text == "Active",
            "Admission header displays the title and current student status");
        using var host = new Form { AutoScaleMode = AutoScaleMode.None, ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000) };
        host.Controls.Add(header);
        host.Show();
        foreach (int width in new[] { 1040, 1576 })
        {
            host.ClientSize = new Size(width, header.Height);
            host.PerformLayout(); header.PerformLayout(); row.PerformLayout();
            Application.DoEvents();
            var visibleCells = row.Controls.Cast<Control>()
                .Where(control => control.Visible && control.Width > 0 && control.Height > 0)
                .OrderBy(control => control.Left).ToArray();
            Check(visibleCells.Length >= 3 &&
                  visibleCells.Zip(visibleCells.Skip(1),
                      (left, right) => left.Right <= right.Left).All(clear => clear),
                "Visible admission identity fields do not overlap at width " + width);
            Check(title.Height >= AppMetrics.TextHeight(title.Font) &&
                  badge.Parent!.ClientRectangle.Contains(badge.Bounds),
                "Admission title and status remain readable at width " + width);
        }
        Directory.CreateDirectory("artifacts");
        using var bitmap = new Bitmap(header.Width, header.Height);
        header.DrawToBitmap(bitmap, header.ClientRectangle);
        bitmap.Save(Path.GetFullPath("artifacts/admission-header.png"));
        Check(!page.IsHandleCreated, "Header preview never starts the admission SQL loader");
        host.Close();
    }
}
