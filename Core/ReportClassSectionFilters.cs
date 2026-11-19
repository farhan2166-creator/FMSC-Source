using System.Data;

namespace FMSC_SMS.Core;

/// <summary>
/// Keeps report Class and Section choices valid for the selected campus/class,
/// including historical campus/class/section assignments.
/// This changes the available filter choices, not the report's SQL scope.
/// </summary>
internal sealed class ReportClassSectionFilters
{
    private readonly LookupBox campus;
    private readonly LookupBox cls;
    private readonly LookupBox section;
    private DataTable? classes;
    private DataTable? sections;
    private DataTable? placements;
    private string classBlank = "All";
    private string sectionBlank = "All";
    private int displayedCampus = int.MinValue;
    private int sectionCampus = int.MinValue;
    private int displayedClass = int.MinValue;
    private bool rebinding;

    internal ReportClassSectionFilters(LookupBox campus, LookupBox cls, LookupBox section)
    {
        this.campus = campus;
        this.cls = cls;
        this.section = section;
        campus.SelectedIndexChanged += (_, _) => Refresh();
        cls.SelectedIndexChanged += (_, _) => Refresh();
    }

    internal void Bind(DataTable allClasses, DataTable allSections, DataTable allPlacements,
        string allClassesText = "All", string allSectionsText = "All")
    {
        if (!allPlacements.Columns.Contains("CampusId") ||
            !allPlacements.Columns.Contains("ClassId") ||
            !allPlacements.Columns.Contains("SectionId"))
            throw new InvalidOperationException("Report class/section assignments are missing required identifiers.");
        classes = allClasses;
        sections = allSections;
        placements = allPlacements;
        classBlank = allClassesText;
        sectionBlank = allSectionsText;
        displayedCampus = sectionCampus = displayedClass = int.MinValue;
        Refresh();
    }

    private HashSet<int> AssignedIds(string key, int campusId, int classId)
    {
        var ids = new HashSet<int>();
        foreach (DataRow row in placements!.Rows)
        {
            if (campusId > 0 && Convert.ToInt32(row["CampusId"]) != campusId) continue;
            if (classId > 0 && Convert.ToInt32(row["ClassId"]) != classId) continue;
            ids.Add(Convert.ToInt32(row[key]));
        }
        return ids;
    }

    private static DataTable Restrict(DataTable all, string key, HashSet<int>? allowed)
    {
        if (allowed is null) return all;
        var filtered = all.Clone();
        foreach (DataRow row in all.Rows)
            if (allowed.Contains(Convert.ToInt32(row[key])))
                filtered.ImportRow(row);
        return filtered;
    }

    private static void Rebind(LookupBox lookup, DataTable rows, string key, string label,
        string blank, int previousId)
    {
        lookup.Bind(rows, key, label, blank);
        if (previousId <= 0) return;
        lookup.SelectedValue = previousId;
        if (lookup.SelectedId != previousId) lookup.SelectedIndex = 0;
    }

    internal void Refresh()
    {
        if (rebinding || classes is null || sections is null || placements is null) return;
        rebinding = true;
        try
        {
            int campusId = campus.SelectedId;
            if (displayedCampus != campusId)
            {
                int previousClass = cls.SelectedId;
                // Even "All campuses" must omit unassigned/inactive catalog entries.
                var allowed = AssignedIds("ClassId", campusId, 0);
                Rebind(cls, Restrict(classes, "ClassId", allowed), "ClassId", "ClassName",
                    classBlank, previousClass);
                displayedCampus = campusId;
                displayedClass = int.MinValue; // Refresh sections even when the same class remains valid.
            }

            int classId = cls.SelectedId;
            if (sectionCampus != campusId || displayedClass != classId)
            {
                int previousSection = section.SelectedId;
                var allowed = AssignedIds("SectionId", campusId, classId);
                Rebind(section, Restrict(sections, "SectionId", allowed), "SectionId", "SectionName",
                    sectionBlank, previousSection);
                sectionCampus = campusId;
                displayedClass = classId;
            }
        }
        finally { rebinding = false; }
    }
}
