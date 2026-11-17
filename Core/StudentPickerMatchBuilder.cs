namespace FMSC_SMS.Core;

/// <summary>Turns a student source into a bounded, highlighted picker result set.</summary>
internal static class StudentPickerMatchBuilder
{
    internal sealed record Result(List<StudentPicker.Entry> Entries, int Highlight, bool Capped);

    internal static Result Build(
        DataTable source,
        string term,
        int maxRows,
        Func<DataRow, bool>? allowed,
        string idColumn,
        int selectedId,
        Func<DataRow, string> display,
        Func<DataRow, string[]> cells)
    {
        List<DataRow> rows = HumanUi.FindStudentMatches(source, term, maxRows > 0 ? maxRows + 1 : 0, allowed);
        bool capped = maxRows > 0 && rows.Count > maxRows;
        if (capped) rows.RemoveAt(rows.Count - 1);
        var entries = new List<StudentPicker.Entry>(rows.Count);
        foreach (DataRow row in rows)
        {
            int id = ReadInt(row, idColumn);
            if (id <= 0)
                continue;
            entries.Add(new StudentPicker.Entry(
                id,
                display(row),
                ReadInt(row, "AdmissionId"),
                cells(row)));
        }
        int highlight = -1;
        if (term.Length > 0)
        {
            var exact = entries.Select((entry, index) => (entry, index)).Where(item =>
                (item.entry.Cells.Length > 0 && StudentSearchMatcher.IsExactReference(item.entry.Cells[0], term)) ||
                (item.entry.Cells.Length > 6 && string.Equals(item.entry.Cells[6], term, StringComparison.OrdinalIgnoreCase))).ToList();
            if (exact.Count == 1) highlight = exact[0].index;
        }
        if (term.Length == 0 && selectedId > 0)
            highlight = entries.FindIndex(entry => entry.Id == selectedId);
        if (highlight < 0 && entries.Count == 1)
            highlight = 0;
        return new Result(entries, highlight, capped);
    }

    private static int ReadInt(DataRow row, string column) =>
        row.Table.Columns.Contains(column) && row[column] != DBNull.Value &&
        int.TryParse(Convert.ToString(row[column], System.Globalization.CultureInfo.InvariantCulture), out int value)
            ? value
            : 0;
}
