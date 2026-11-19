using System.Data;
using FMSC_SMS.Core;

internal static partial class Program
{
    private static void CheckCampusClassFilters()
    {
        using var campuses = new DataTable();
        campuses.Columns.Add("CampusId", typeof(int));campuses.Columns.Add("CampusName");
        campuses.Rows.Add(1, "Boys");campuses.Rows.Add(2, "Girls");
        using var classes = new DataTable();
        classes.Columns.Add("ClassId", typeof(int));classes.Columns.Add("ClassName");
        classes.Rows.Add(10, "Grade One");classes.Rows.Add(20, "Grade Two");classes.Rows.Add(30, "Unused");
        using var assignments = new DataTable();
        assignments.Columns.Add("CampusId", typeof(int));assignments.Columns.Add("ClassId", typeof(int));
        assignments.Rows.Add(1, 10); assignments.Rows.Add(2, 20);
        using var campus = new LookupBox();
        using var cls = new LookupBox();
        campus.Bind(campuses, "CampusId", "CampusName", "All campuses");
        var filter = new CampusClassFilters(campus, cls);
        filter.Bind(classes, assignments);
        static int[] Ids(LookupBox lookup) => lookup.Items.Cast<DataRowView>()
            .Select(row => Convert.ToInt32(row[lookup.ValueMember]))
            .Where(id => id > 0).ToArray();
        Check(Ids(cls).SequenceEqual(new[] { 10,20 }), "All campuses omits unassigned classes");
        campus.SelectedValue = 1;
        Check(Ids(cls).SequenceEqual(new[] { 10 }), "Campus filters classes immediately");
        cls.SelectedValue = 10;
        campus.SelectedValue = 2;
        Check(cls.SelectedId == 0 && Ids(cls).SequenceEqual(new[] { 20 }),
            "Changing campus clears stale class and loads valid choices");
        cls.SelectedValue = 20;
        campus.SelectedIndex = 0;
        Check(cls.SelectedId == 20 && Ids(cls).SequenceEqual(new[] { 10,20 }),
            "All campuses restores all assigned classes and keeps a still-valid selection");
    }
}
