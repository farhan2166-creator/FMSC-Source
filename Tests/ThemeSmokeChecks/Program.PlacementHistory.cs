using System.Data;
using System.Reflection;
using FMSC_SMS.Core;
using FMSC_SMS.Pages.Students.Admissions;

internal static partial class Program
{
    private static void CheckPlacementHistoryFlow()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(StudentPlacementCorrectionDialog);
        var history = new DataTable();
        foreach (string name in new[] { "EnrollmentId", "AcademicYearId", "ClassSectionId" }) history.Columns.Add(name, typeof(int));
        foreach (string name in new[] { "YearName", "ClassName", "SectionName", "Status" }) history.Columns.Add(name);
        foreach (string name in new[] { "EnrollmentDate", "LeavingDate", "BillingStartDate", "PlacementConfirmedAt" }) history.Columns.Add(name, typeof(DateTime));
        history.Rows.Add(1, 27, 3, "2025-2026", "Class 10", "F", "Left", new DateTime(2025, 4, 1), new DateTime(2026, 3, 31), new DateTime(2025, 4, 1), new DateTime(2025, 4, 1));
        history.Rows.Add(2, 28, 4, "2026-2027", "1st Year", "F", "Active", new DateTime(2026, 10, 23), DBNull.Value, new DateTime(2026, 10, 23), new DateTime(2026, 10, 23));
        Directory.CreateDirectory("artifacts/placement-history");
        using var browser = new StudentPlacementCorrectionDialog(0, "Placement preview", false);
        browser.StartPosition = FormStartPosition.Manual; browser.Location = new Point(-20000, -20000);
        Seed(browser, history);
        browser.Show(); Application.DoEvents();
        var list = Field<ListBox>(browser, "record");
        Check(list.Items.Count == 2 && Convert.ToInt32(list.SelectedValue) == 2, "History contains only existing records and selects the active placement.");
        Check(Field<ModernButton>(browser, "dates").Visible && !Field<ModernButton>(browser, "review").Visible && !Field<LookupBox>(browser, "year").Visible,
            "Confirmed placement shows date correction without entry fields.");
        Capture(browser, "confirmed");
        list.SelectedValue = 1;
        Check(Field<ModernTextBox>(browser, "impact").Text.Contains("31 Mar 2026"), "Selecting old history shows its recorded dates.");
        Check(browser.Text == "Placement History" && Field<ModernButton>(browser, "dates").Visible, "Switching history keeps the same screen and date action.");
        history.Rows[1]["PlacementConfirmedAt"] = DBNull.Value;
        list.SelectedValue = 2;
        Check(Field<ModernButton>(browser, "edit").Visible && !Field<ModernButton>(browser, "dates").Visible && !Field<ModernButton>(browser, "review").Visible,
            "A draft offers a separate review action without opening an inline editor.");
        Capture(browser, "draft");
        using (var draft = (StudentPlacementCorrectionDialog)Activator.CreateInstance(type, flags, null,
            new object?[] { 0, "Placement preview", (int?)2, false }, null)!)
        {
            draft.StartPosition = FormStartPosition.Manual; draft.Location = new Point(-20000, -20000);
            Seed(draft, history); draft.Show(); Application.DoEvents();
            Check(Field<LookupBox>(draft, "year").Visible && !Field<LookupBox>(draft, "year").Enabled &&
                Field<ModernButton>(draft, "review").Visible && !Field<ListBox>(draft, "record").Visible,
                "A separate draft editor preserves the recorded academic year and exposes review.");
            Capture(draft, "review-draft");
            draft.Close();
        }
        using var editor = (StudentPlacementCorrectionDialog)Activator.CreateInstance(type, flags, null,
            new object?[] { 0, "Placement preview", (int?)0, false }, null)!;
        editor.StartPosition = FormStartPosition.Manual; editor.Location = new Point(-20000, -20000);
        Seed(editor, history);
        editor.Show(); Application.DoEvents();
        var historicalPlacement = Field<PlacementPicker>(editor, "placement");
        Check(historicalPlacement.Source.Rows.Count == 2 && historicalPlacement.Source.AsEnumerable().All(row => Convert.ToInt32(row["ClassSectionId"]) != 102),
            "Missing placement picker excludes classes that reverse the recorded progression.");
        historicalPlacement.SelectedValue = 101;
        Check(!Field<ListBox>(editor, "record").Visible && Field<LookupBox>(editor, "year").Enabled,
            "Missing history opens a separate form with academic-year selection.");
        Check(Field<ModernComboBox>(editor, "status").Text == "Left", "Missing history defaults to a closed record when an active placement exists.");
        Check(!Field<ModernComboBox>(editor, "status").Items.Contains("Active"), "Missing history never offers Active status.");
        Check(!Field<CheckBox>(editor, "legacy").Visible, "Missing history offers no pre-admission override.");
        Check(!Field<ModernDatePicker>(editor, "billing").Enabled && !Field<ModernDatePicker>(editor, "billing").Checked,
            "Historical placement billing start remains disabled and empty.");
        Check(Field<ModernDatePicker>(editor, "start").MinDate == new DateTime(2024,4,1) &&
            Field<ModernDatePicker>(editor, "start").MaxDate == new DateTime(2025,3,31), "Historical dates are constrained to the selected gap.");
        Field<ModernComboBox>(editor, "status").SelectedItem = "Promoted";
        Check(Field<ModernDatePicker>(editor, "leave").MinDate == new DateTime(2025,4,1) &&
            Field<ModernDatePicker>(editor, "leave").MaxDate == new DateTime(2025,4,1), "Promotion uses exactly the recorded destination date.");
        Field<ModernComboBox>(editor, "status").SelectedItem = "Left";
        Check(Field<ModernDatePicker>(editor, "leave").MaxDate == new DateTime(2025,3,31), "Leaving cannot cross the historical gap's end.");
        historicalPlacement.SelectedValue = 103;
        Check(!Field<ModernComboBox>(editor, "status").Items.Contains("Promoted"), "Repeating the destination class is not offered as promotion.");
        historicalPlacement.SelectedValue = 101;
        Check(Field<ModernComboBox>(editor, "status").Items.Contains("Promoted"), "A lower class can be promoted to the recorded higher destination.");
        Check(Field<ModernButton>(editor, "review").Visible && !Field<ModernButton>(editor, "addHistory").Visible && !Field<ModernButton>(editor, "apply").Enabled,
            "Missing history requires review before applying and cannot recursively open history entry.");
        Capture(editor, "missing-history");
        var departureOption = Field<CheckBox>(editor, "recordDeparture");
        var displayReview = type.GetMethod("ShowHistoricalReview", flags)!;
        displayReview.Invoke(editor, new object[] { false });
        Check(!departureOption.Visible, "An ineligible historical placement offers no school departure action.");
        displayReview.Invoke(editor, new object[] { true });
        Check(departureOption.Visible && !departureOption.Checked, "An eligible historical placement requires explicit opt-in to school departure.");
        Capture(editor, "historical-departure-review");
        departureOption.Checked = true;
        Field<ModernTextBox>(editor, "reason").Text = "Changed after review";
        Check(!departureOption.Visible && !departureOption.Checked && !Field<ModernButton>(editor, "apply").Enabled,
            "Changing the reviewed placement details clears the departure choice and requires a new review.");
        editor.Close();
        history.Columns.Add("RepeatedClass",typeof(bool));history.Rows[0]["RepeatedClass"]=true;history.Rows[0]["Status"]="Promoted";
        Seed(browser,history);list.SelectedValue=1;
        Check(list.Text.Contains("Repeated Class")&&Field<ModernTextBox>(browser,"impact").Text.Contains("Moved to next year")
            &&history.Rows[0].Field<string>("Status")=="Promoted","A recorded repeat is labelled clearly in placement history while its saved lifecycle status stays unchanged.");
        Seed(browser, history.Clone());
        Check(list.Items.Count == 0 && !Field<ModernButton>(browser, "dates").Visible && !Field<ModernButton>(browser, "edit").Visible,
            "Empty history offers no correction of a nonexistent record.");
        Check(Field<ModernTextBox>(browser, "impact").Text.Contains("No placement records"), "Empty history gives clear guidance.");
        Check(!Field<ModernButton>(browser, "addHistory").Enabled, "No eligible gap disables missing history entry.");
        browser.Close();

        T Field<T>(object instance, string name) => (T)type.GetField(name, flags)!.GetValue(instance)!;
        void Seed(object instance, DataTable data)
        {
            type.GetField("loading", flags)!.SetValue(instance, true);
            type.GetField("enrollments", flags)!.SetValue(instance, data);
            type.GetField("currentAcademicYear", flags)!.SetValue(instance, 28);
            var windows = new DataTable();
            windows.Columns.Add("WindowId", typeof(int)); windows.Columns.Add("AcademicYearId", typeof(int)); windows.Columns.Add("YearName");
            windows.Columns.Add("GapStart", typeof(DateTime)); windows.Columns.Add("GapEnd", typeof(DateTime)); windows.Columns.Add("PromotionDate", typeof(DateTime));
            if (data.Rows.Count > 0) windows.Rows.Add(1, 26, "2024-2025", new DateTime(2024,4,1), new DateTime(2025,3,31), new DateTime(2025,4,1));
            type.GetField("missingWindows", flags)!.SetValue(instance, windows);
            var choices = new DataTable(); choices.Columns.Add("WindowId", typeof(int));
            choices.Columns.Add("ClassSectionId", typeof(int)); choices.Columns.Add("CanPromote", typeof(bool));
            if (data.Rows.Count > 0) { choices.Rows.Add(1,101,true); choices.Rows.Add(1,103,false); }
            type.GetField("missingPlacementChoices", flags)!.SetValue(instance, choices);
            var placements = new DataTable(); placements.Columns.Add("ClassSectionId", typeof(int)); placements.Columns.Add("ClassId", typeof(int));
            placements.Columns.Add("CampusName"); placements.Columns.Add("ClassName"); placements.Columns.Add("SectionName");
            placements.Rows.Add(101,9,"Fixture campus","Class 9","F");
            placements.Rows.Add(102,11,"Fixture campus","Class 11","F");
            placements.Rows.Add(103,10,"Fixture campus","Class 10","F");
            type.GetField("allPlacements", flags)!.SetValue(instance, placements);
            var years = new DataTable(); years.Columns.Add("AcademicYearId", typeof(int)); years.Columns.Add("YearName");
            years.Rows.Add(27, "2025-2026"); years.Rows.Add(28, "2026-2027");
            type.GetMethod("BindYears", flags)!.Invoke(instance, new object[] { years });
            type.GetMethod("BindHistory", flags)!.Invoke(instance, new object[] { -1 });
            type.GetField("loading", flags)!.SetValue(instance, false);
            type.GetMethod("LoadSelected", flags)!.Invoke(instance, null);
        }
        void Capture(Form form, string name)
        {
            foreach (var size in new[] { new Size(740, 600), new Size(850, 650) })
            {
                form.Size = size; form.PerformLayout(); Application.DoEvents();
                Check(!Descendants(form).OfType<TableLayoutPanel>().Single(panel => panel.AutoScroll).HorizontalScroll.Visible,
                    $"{name}: content fits without horizontal scrolling at {size.Width}px.");
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(Path.GetFullPath($"artifacts/placement-history/{name}-{size.Width}.png"));
                foreach (var button in Descendants(form).OfType<ModernButton>().Where(button => button.Visible))
                {
                    var bounds = form.RectangleToClient(button.RectangleToScreen(button.ClientRectangle));
                    Check(form.ClientRectangle.Contains(bounds), $"{name}: {button.Text} fits at {size.Width}px ({bounds} within {form.ClientRectangle}).");
                }
            }
        }
    }
}
