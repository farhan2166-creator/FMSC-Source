using System.Reflection;
using FMSC_SMS.Core;
using FMSC_SMS.Pages.Setup.Academics;

internal static partial class Program
{
    private static void CheckAssignmentLayout()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        using var page = new CampusClassSectionAssignmentsPage(false);
        using var host = new Form { ClientSize = new Size(1600, 850) };
        page.Dock = DockStyle.Fill;
        host.Controls.Add(page);
        var grid = (DataGridView)typeof(CampusClassSectionAssignmentsPage).GetField("matrix", flags)!.GetValue(page)!;
        foreach (string name in new[] { "F", "M", "S1" })
            grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = name, HeaderText = name, MinimumWidth = 80 });
        grid.Rows.Add("Play Group", false, true, false);
        grid.Rows.Add("Nursery", true, false, true);
        host.Show();
        Application.DoEvents();
        var tools = page.Controls.Find("AssignmentTools", true).Single();
        foreach (int width in new[] { 1600, 1200 })
        {
            host.ClientSize = new Size(width, 850);
            Application.DoEvents();
            UiHelper.FitColumnsToWidth(grid);
            Check(grid.Columns.Cast<DataGridViewColumn>().Where(column => column.Visible)
                    .All(column => column.Width >= column.MinimumWidth),
                "Assignment columns stay readable after resizing without requiring identical widths");
            Check(tools.Controls.Cast<Control>().Where(control => control.Visible)
                    .All(control => control.Width > 0 && control.Height > 0 &&
                          tools.ClientRectangle.Contains(control.Bounds)),
                "Campus selection and assignment actions remain accessible regardless of their arrangement");
            Check((bool)grid.Rows[0].Cells[2].Value! && !(bool)grid.Rows[0].Cells[1].Value!, "Sizing preserves assignment values");
        }
        host.ClientSize = new Size(1600, 850);
        Application.DoEvents();
        Directory.CreateDirectory("artifacts/assignment-layout");
        using var picture = new Bitmap(host.Width, host.Height);
        host.DrawToBitmap(picture, new Rectangle(Point.Empty, picture.Size));
        picture.Save("artifacts/assignment-layout/assignment-layout.png");
        // Catalog refresh restores differences by IDs, despite rename/reorder or
        // removed entries. New combinations retain the freshly loaded baseline.
        using (var refreshed = new CampusClassSectionAssignmentsPage(false))
        {
            var kind = typeof(CampusClassSectionAssignmentsPage);
            var refreshedGrid = (DataGridView)kind.GetField("matrix", flags)!.GetValue(refreshed)!;
            var sectionType = kind.GetNestedType("SectionInfo", BindingFlags.NonPublic)!;
            var sectionList = (System.Collections.IList)kind.GetField("sections", flags)!.GetValue(refreshed)!;
            foreach (int id in new[] { 20, 10, 30 })
            {
                var section = Activator.CreateInstance(sectionType)!;
                sectionType.GetProperty("SectionId")!.SetValue(section, id);
                sectionType.GetProperty("SectionName")!.SetValue(section, "Renamed " + id);
                sectionList.Add(section);
                refreshedGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Section_" + id });
            }
            refreshedGrid.Rows.Add("Renamed Class", false, true, false);
            refreshedGrid.Rows[0].Tag = 7;
            var baseline = (Dictionary<(int,int),bool>)kind.GetField("savedAssignments", flags)!.GetValue(refreshed)!;
            baseline[(7,20)] = false; baseline[(7,10)] = true; baseline[(7,30)] = false;
            kind.GetField("matrixReady", flags)!.SetValue(refreshed, true);
            var pending = (Dictionary<(int,int),bool>)kind.GetField("catalogDraft", flags)!.GetValue(refreshed)!;
            pending[(7,20)] = true; pending[(7,10)] = false; pending[(99,10)] = true;
            kind.GetMethod("RestoreCatalogDraft", flags)!.Invoke(refreshed, null);
            Check((bool)refreshedGrid.Rows[0].Cells[1].Value! && !(bool)refreshedGrid.Rows[0].Cells[2].Value!,
                "Catalog rename/reorder retains pending additions and removals by stable IDs");
            Check(!(bool)refreshedGrid.Rows[0].Cells[3].Value!, "New sections are not implicitly assigned");
            Check(baseline[(7,10)] && !baseline[(7,20)] && pending.Count == 0,
                "Restoring catalog drafts preserves saved baseline and drops deleted keys");
            kind.GetMethod("ResetChanges", flags)!.Invoke(refreshed, null);
            Check(!(bool)refreshedGrid.Rows[0].Cells[1].Value! && (bool)refreshedGrid.Rows[0].Cells[2].Value!,
                "Reset after catalog refresh restores freshly loaded saved assignments");
            refreshed.BindingContext = new BindingContext();
            var lookup = (LookupBox)kind.GetField("campus", flags)!.GetValue(refreshed)!;
            using var campusRows = new DataTable();
            campusRows.Columns.Add("CampusId", typeof(int)); campusRows.Columns.Add("CampusName");
            campusRows.Rows.Add(10, "Original campus"); campusRows.Rows.Add(20, "Other campus");
            var refreshCampus = kind.GetMethod("RefreshCampusLookup", flags)!;
            refreshCampus.Invoke(refreshed, new object[] { campusRows });
            pending[(7,20)] = true;
            campusRows.Rows[0]["CampusName"] = "Renamed campus";
            refreshCampus.Invoke(refreshed, new object[] { campusRows });
            Check(lookup.SelectedId == 10 && pending.Count == 1,
                "Renaming a campus retains its selection and unapplied assignments");
            campusRows.Rows.RemoveAt(0);
            refreshCampus.Invoke(refreshed, new object[] { campusRows });
            Check(lookup.SelectedId == 20 && pending.Count == 0,
                "Deleting a campus cannot transfer its assignment draft to another campus");
        }
        using var years = new AcademicYearsPage(false);
        var yearGrid = (DataGridView)typeof(V2Page).GetField("Grid", flags | BindingFlags.Public)!.GetValue(years)!;
        yearGrid.Columns.Add("YearName", "Year Name");
        yearGrid.Columns.Add("Remarks", "Remarks");
        yearGrid.Columns.Add("DatesLocked", "DatesLocked");
        typeof(AcademicYearsPage).GetMethod("LayoutRecordColumns", flags)!.Invoke(years, null);
        Check(!yearGrid.Columns["Remarks"].Visible && !yearGrid.Columns["DatesLocked"].Visible, "Academic-year grid hides Remarks and the internal lock flag");
    }
}
