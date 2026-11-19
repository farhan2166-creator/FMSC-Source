using System.Data;
using FMSC_SMS.Core;

internal static partial class Program
{
    private static void CheckReportClassSectionFilters()
    {
        CheckCampusClassFilters();
        using var campuses = new DataTable();
        campuses.Columns.Add("CampusId", typeof(int));
        campuses.Columns.Add("CampusName", typeof(string));
        campuses.Rows.Add(1, "Boys");
        campuses.Rows.Add(2, "Girls");

        using var classes = new DataTable();
        classes.Columns.Add("ClassId", typeof(int));
        classes.Columns.Add("ClassName", typeof(string));
        classes.Rows.Add(10, "Play Group");
        classes.Rows.Add(20, "Grade One");
        classes.Rows.Add(30, "Grade Two");
        classes.Rows.Add(40, "Unassigned Class");

        using var sections = new DataTable();
        sections.Columns.Add("SectionId", typeof(int));
        sections.Columns.Add("SectionName", typeof(string));
        sections.Rows.Add(100, "A");
        sections.Rows.Add(200, "B");
        sections.Rows.Add(300, "C");
        sections.Rows.Add(400, "Unassigned Section");

        using var placements = new DataTable();
        placements.Columns.Add("CampusId", typeof(int));
        placements.Columns.Add("ClassId", typeof(int));
        placements.Columns.Add("SectionId", typeof(int));
        placements.Rows.Add(1, 10, 100);
        placements.Rows.Add(1, 20, 200);
        placements.Rows.Add(2, 20, 300);
        placements.Rows.Add(2, 30, 200);

        using var campus = new LookupBox();
        using var cls = new LookupBox();
        using var section = new LookupBox();
        campus.Bind(campuses, "CampusId", "CampusName", "All");
        var filters = new ReportClassSectionFilters(campus, cls, section);
        filters.Bind(classes, sections, placements);
        static int[] Choices(LookupBox box) => box.Items.Cast<DataRowView>()
            .Select(row => Convert.ToInt32(row[box.ValueMember]))
            .Where(id => id > 0).OrderBy(id => id).ToArray();

        Check(Choices(cls).SequenceEqual(new[] { 10, 20, 30 }) &&
            Choices(section).SequenceEqual(new[] { 100, 200, 300 }),
            "All campuses offers only assigned classes/sections once, omitting unused catalog entries");

        campus.SelectedValue = 1;
        Check(Choices(cls).SequenceEqual(new[] { 10, 20 }) &&
            Choices(section).SequenceEqual(new[] { 100, 200 }),
            "Choosing a campus excludes classes and sections assigned only elsewhere");

        cls.SelectedValue = 20;
        Check(Choices(section).SequenceEqual(new[] { 200 }),
            "Choosing a class narrows sections within its selected campus");
        section.SelectedValue = 200;
        campus.SelectedValue = 2;
        Check(cls.SelectedId == 20 && section.SelectedId == 0 &&
            Choices(cls).SequenceEqual(new[] { 20, 30 }) &&
            Choices(section).SequenceEqual(new[] { 300 }),
            "Changing campus preserves a valid shared class and clears a stale section");

        cls.SelectedValue = 30;
        Check(section.SelectedId == 0 && Choices(section).SequenceEqual(new[] { 200 }),
            "Changing class clears stale sections and offers only its own sections");
        campus.SelectedValue = 1;
        Check(cls.SelectedId == 0 && Choices(cls).SequenceEqual(new[] { 10, 20 }),
            "Changing campus clears a class not assigned to the new campus");

        campus.SelectedIndex = 0;
        Check(Choices(cls).SequenceEqual(new[] { 10, 20, 30 }) &&
            Choices(section).SequenceEqual(new[] { 100, 200, 300 }),
            "Resetting to All campuses restores active assigned report options, not unassigned catalog entries");
    }

    private static void CheckStudentReportParentCascade()
    {
        // Exercise the real Student Reports change events, not only the shared filter helper.
        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        using var page = new FMSC_SMS.Pages.Reports.Students.StudentReportsPage
        {
            BindingContext = new BindingContext()
        };
        T Field<T>(string name) => (T)typeof(FMSC_SMS.Pages.Reports.Students.StudentReportsPage)
            .GetField(name, flags)!.GetValue(page)!;
        void SetField(string name, object value) => typeof(FMSC_SMS.Pages.Reports.Students.StudentReportsPage)
            .GetField(name, flags)!.SetValue(page, value);
        using var campuses = new DataTable();
        campuses.Columns.Add("CampusId", typeof(int));
        campuses.Columns.Add("CampusName");
        campuses.Rows.Add(1, "Boys");
        campuses.Rows.Add(2, "Girls");

        using var classes = new DataTable();
        classes.Columns.Add("ClassId", typeof(int));
        classes.Columns.Add("ClassName");
        classes.Rows.Add(10, "Play Group");
        classes.Rows.Add(20, "Grade One");
        classes.Rows.Add(30, "Grade Two");

        using var sections = new DataTable();
        sections.Columns.Add("SectionId", typeof(int));
        sections.Columns.Add("SectionName");
        sections.Rows.Add(100, "A");
        sections.Rows.Add(200, "B");
        sections.Rows.Add(300, "C");

        using var assignments = new DataTable();
        assignments.Columns.Add("CampusId", typeof(int));
        assignments.Columns.Add("ClassId", typeof(int));
        assignments.Columns.Add("SectionId", typeof(int));
        assignments.Rows.Add(1, 10, 100);
        assignments.Rows.Add(1, 20, 200);
        assignments.Rows.Add(2, 20, 300);
        assignments.Rows.Add(2, 30, 200);

        var campus = Field<LookupBox>("campus");
        var cls = Field<LookupBox>("cls");
        var section = Field<LookupBox>("section");
        static int[] Choices(LookupBox box) => box.Items.Cast<DataRowView>()
            .Select(row => Convert.ToInt32(row[box.ValueMember]))
            .Where(id => id > 0).OrderBy(id => id).ToArray();

        SetField("_suppress", true);
        try
        {
            campus.Bind(campuses, "CampusId", "CampusName", "All campuses");
            cls.Bind(classes, "ClassId", "ClassName", "All classes");
            section.Bind(sections, "SectionId", "SectionName", "All sections");
            SetField("_allClasses", classes);
            SetField("_allSections", sections);
            SetField("_combos", assignments);
            typeof(FMSC_SMS.Pages.Reports.Students.StudentReportsPage)
                .GetMethod("RefreshClassSectionLists", flags)!
                .Invoke(page, new object[] { true, true });
        }
        finally { SetField("_suppress", false); }

        campus.SelectedValue = 1;
        Check(Choices(cls).SequenceEqual(new[] { 10, 20 }) &&
              Choices(section).SequenceEqual(new[] { 100, 200 }),
            "Student Reports cascades the selected campus into both child lists");
        cls.SelectedValue = 20;
        Check(Choices(section).SequenceEqual(new[] { 200 }),
            "Student Reports narrows sections when class changes");
        section.SelectedValue = 200;
        Check(Choices(cls).SequenceEqual(new[] { 10, 20 }),
            "Selecting a section never hides another class in the same campus");

        cls.SelectedValue = 10;
        Check(cls.SelectedId == 10 && section.SelectedId == 0 &&
              Choices(section).SequenceEqual(new[] { 100 }),
            "Changing to another class clears its stale section without blocking the class");
        campus.SelectedValue = 2;
        Check(cls.SelectedId == 0 && Choices(cls).SequenceEqual(new[] { 20, 30 }) &&
              Choices(section).SequenceEqual(new[] { 200, 300 }),
            "Changing campuses resets an unavailable class and rebuilds sections");
        cls.SelectedValue = 30;
        section.SelectedValue = 200;
        Check(Choices(cls).SequenceEqual(new[] { 20, 30 }),
            "Selecting section B on Girls campus does not trap the class picker");
        cls.SelectedValue = 20;
        Check(cls.SelectedId == 20 && section.SelectedId == 0 &&
              Choices(section).SequenceEqual(new[] { 300 }),
            "Switching classes after a child selection repopulates the correct section list");
        campus.SelectedIndex = 0;
        cls.SelectedIndex = 0;
        Check(Choices(cls).SequenceEqual(new[] { 10, 20, 30 }) &&
              Choices(section).SequenceEqual(new[] { 100, 200, 300 }),
            "Returning to All campuses and All classes restores every assigned option");
    }
}
