using System.Data;
using FMSC_SMS.Core;

internal static partial class Program
{
    private static void CheckReportClassSectionFilters()
    {
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

        using var sections = new DataTable();
        sections.Columns.Add("SectionId", typeof(int));
        sections.Columns.Add("SectionName", typeof(string));
        sections.Rows.Add(100, "A");
        sections.Rows.Add(200, "B");
        sections.Rows.Add(300, "C");

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
            "All campuses offers the combined class and section catalog once each");

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
            "Resetting to All campuses restores combined report options");
    }
}
