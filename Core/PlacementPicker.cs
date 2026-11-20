namespace FMSC_SMS.Core;

/// <summary>Find a valid campus/class/section using the shared search picker.</summary>
public sealed class PlacementPicker : StudentPicker
{
    private DataTable source = new();

    public PlacementPicker()
    {
        IdColumn = "ClassSectionId";
        PlaceholderText = "Search campus, class or section";
        NoMatchText = "No matching campus / class / section";
        ShowDropDownButton = true;
        DisplayFormatter = row => string.Join(" · ", CellsFor(row));
        UserSelectionChanged += (_, _) => SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        SelectionCleared += (_, _) => SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? SelectedIndexChanged;

    public object SelectedValue
    {
        get => SelectedId;
        set
        {
            int previous = SelectedId;
            int id = value is null or DBNull ? 0 : Convert.ToInt32(value);
            var row = source.AsEnumerable().FirstOrDefault(row => Convert.ToInt32(row[IdColumn]) == id && id > 0);
            if (row is null) Clear(); else SetStudent(row);
            if (previous != SelectedId) SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Bind(DataTable placements)
    {
        source = LookupDisplayOrder.ForBinding(placements, IdColumn);
        SetSource(source);
    }

    internal override string EntityPlural => "placements";
    internal override bool FillFromHighlightedRow => true;
    internal override Task<DataTable> LoadSourceAsync() => Task.FromResult(source);
    internal override string[] CellsFor(DataRow row) => new[] { Read(row, "CampusName"), Read(row, "ClassName"), Read(row, "SectionName") };

    internal override StudentPickerMatchBuilder.Result FindMatches(DataTable all, string term)
    {
        string[] words = term.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // Filtering retains the shared campus, class and section display order.
        var matches = all.AsEnumerable().Where(row =>
            int.TryParse(Read(row, IdColumn), out int id) && id > 0 &&
            (RowFilter is null || RowFilter(row)) &&
            words.All(word => CellsFor(row).Any(cell => cell.Contains(word, StringComparison.OrdinalIgnoreCase))))
            .GroupBy(row => Read(row, IdColumn)).Select(group => group.First()).Take(151).ToList();
        var entries = matches.Take(150).Select(row => new Entry(Convert.ToInt32(row[IdColumn]), DisplayFormatter!(row), cells: CellsFor(row))).ToList();
        int highlight = term.Length == 0 ? entries.FindIndex(entry => entry.Id == SelectedId) : entries.Count == 1 ? 0 : -1;
        return new(entries, highlight, matches.Count > 150);
    }

    internal override List<ColumnSpec> PlanColumns(List<Entry> entries, Font font)
    {
        var columns = new List<ColumnSpec> { new("Campus", 220, flex: true), new("Class", 240, flex: true), new("Section", 160, flex: true) };
        for (int index = 0; index < columns.Count; index++)
        {
            var column = columns[index];
            // The shared row renderer draws its first column in ButtonFont.
            // Measure that same font so campus names are not clipped in bold.
            Font cellFont = index == 0 ? AppTheme.ButtonFont : font;
            int widest = TextRenderer.MeasureText(column.Header, AppTheme.FontSmallBold).Width;
            foreach (var entry in entries) widest = Math.Max(widest, TextRenderer.MeasureText(entry.Cells[index], cellFont).Width);
            column.Width = Math.Min(AppScale.Scale(column.MaxWidth), widest) + 4;
        }
        return columns;
    }

    private static string Read(DataRow row, string column) => row.Table.Columns.Contains(column) && !row.IsNull(column)
        ? Convert.ToString(row[column]) ?? string.Empty : string.Empty;
}
