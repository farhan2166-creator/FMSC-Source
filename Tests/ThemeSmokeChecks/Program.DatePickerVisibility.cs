using System.Reflection;
using FMSC_SMS.Core;
using FMSC_SMS.Pages.Reports;
using FMSC_SMS.Pages.Students.Admissions;

internal static partial class Program
{
    private static void CheckDatePickerVisibility()
    {
        var originalTheme = AppTheme.CurrentTheme;
        var originalScale = (AppScale.Dpi, AppScale.RawFactor);
        var admissionField = typeof(StudentAdmissionPage).GetMethod("AdmissionField", BindingFlags.Static | BindingFlags.NonPublic)!;
        var builders = new (string Name, Func<ModernDatePicker, Panel> Build)[] {
            ("Admission", date => (Panel)admissionField.Invoke(null, new object[] { "Date of Birth", date, false })!),
            ("Shared", date => HumanUi.Field("Date", date)),
            ("Report", date => ReportUi.Field("Date", date)),
            ("Certificate", date => CertificateUi.Field("Date", date))
        };
        try
        {
            foreach (var theme in AppTheme.SelectableThemes)
            foreach (int dpi in new[] { 96, 120 })
            {
                AppScale.Recompute(new Size(1080, 620), dpi);
                AppTheme.SetTheme(theme);
                foreach (var builder in builders)
                {
                    using var date = HumanUi.Date();
                    date.ShowCheckBox = builder.Name == "Admission";
                    date.Value = new DateTime(2026, 1, 15);
                    date.Checked = true;
                    using var field = builder.Build(date);
                    using var owner = new Form { AutoScaleMode = AutoScaleMode.None,
                        ClientSize = new Size(AppScale.Scale(360), AppMetrics.FieldRowHeight + AppScale.Scale(24)) };
                    owner.Controls.Add(field); owner.Show(); Application.DoEvents();
                    // Exercise the public scale/theme layout contract. On newer
                    // WinForms the private _deviceDpi field was removed, so
                    // setting it by reflection is not a valid usability test.
                    UiTheme.ApplyTree(field); field.PerformLayout(); Application.DoEvents();
                    var native = date.Controls.OfType<DateTimePicker>().Single();
                    var label = field.Controls.OfType<Label>().Single();
                    string context = $"{builder.Name}, {theme}, DPI {dpi}";
                    Console.WriteLine($"DATE_LAYOUT {context}: field={field.ClientSize}; label={label.Bounds}; wrapper={date.Bounds}; native={native.Bounds}; visible={date.Visible}/{native.Visible}");
                    Check(label.Visible && label.Width > 0 && label.Height >= AppMetrics.TextHeight(label.Font), context + ": date caption remains visible");
                    Check(date.Visible && native.Visible && native.IsHandleCreated && date.Width > AppScale.Scale(100) && date.Height > 0,
                        context + ": the date field and its native editor remain visible");
                    Check(field.ClientRectangle.Contains(date.Bounds) && date.ClientRectangle.Contains(native.Bounds),
                        context + ": the date field and native editor fit inside their containers");
                    Check(!label.Bounds.IntersectsWith(date.Bounds), context + ": the date input does not overlap its caption");
                    Check(native.Width >= TextRenderer.MeasureText(native.Text, native.Font).Width + SystemInformation.VerticalScrollBarWidth,
                        context + ": the current date format and calendar button have usable space");
                    Check(date.Value.Date == new DateTime(2026, 1, 15) && date.Checked,
                        context + ": layout and theme application preserve the selected date");
                    owner.Close();
                }
            }
        }
        finally
        {
            AppScale.Recompute(new Size((int)(1280 * originalScale.RawFactor * DpiLayout.Factor(originalScale.Dpi)),
                (int)(720 * originalScale.RawFactor * DpiLayout.Factor(originalScale.Dpi))), originalScale.Dpi);
            AppTheme.SetTheme(originalTheme);
        }
    }
}
