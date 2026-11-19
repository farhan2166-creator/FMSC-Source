using System.Data;

namespace FMSC_SMS.Core;

/// <summary>One-way Campus to Class options based on assigned class-section rows; no query scope changes.</summary>
internal sealed class CampusClassFilters
{
    private readonly LookupBox campus, cls;
    private DataTable? classes, assignments;
    private string blank = "All";
    private int displayedCampus = int.MinValue;
    private bool binding;

    internal CampusClassFilters(LookupBox campus, LookupBox cls)
    {
        this.campus = campus;
        this.cls = cls;
        campus.SelectedIndexChanged += (_, _) => Refresh();
    }

    internal void Bind(DataTable allClasses, DataTable allAssignments, string blankText = "All")
    {
        if (!allAssignments.Columns.Contains("CampusId") || !allAssignments.Columns.Contains("ClassId"))
            throw new InvalidOperationException("Campus/class assignments are missing required identifiers.");
        classes = allClasses;
        assignments = allAssignments;
        blank = blankText;
        displayedCampus = int.MinValue;
        Refresh();
    }

    internal void Refresh()
    {
        if (binding || classes is null || assignments is null) return;
        int campusId = campus.SelectedId;
        if (displayedCampus == campusId) return;
        binding = true;
        try
        {
            int previous = cls.SelectedId;
            var allowed = assignments.AsEnumerable()
                .Where(row => campusId == 0 || Convert.ToInt32(row["CampusId"]) == campusId)
                .Select(row => Convert.ToInt32(row["ClassId"])).ToHashSet();
            var choices = classes.Clone();
            foreach (DataRow row in classes.Rows)
                if (allowed.Contains(Convert.ToInt32(row["ClassId"]))) choices.ImportRow(row);
            cls.Bind(choices, "ClassId", "ClassName", blank);
            cls.SelectedValue = previous > 0 && allowed.Contains(previous) ? previous : 0;
            displayedCampus = campusId;
        }
        finally { binding = false; }
    }
}
