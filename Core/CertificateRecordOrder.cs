namespace FMSC_SMS.Core;

// Keep confirmed/uncertain-save guidance visible through DbMessage's
// operator-facing exception policy; a generic retry prompt could duplicate a copy.
internal sealed class CertificateSaveResultException(string message) : InvalidOperationException(message);

internal static class CertificateRecordOrder
{
    internal static void RequireSavedCopy(DataTable rows, string key, int id)
    {
        // A date-sorted history can place a backdated or deduplicated save
        // anywhere. Never substitute the first row for the confirmed identity.
        if (id > 0 && rows.Columns.Contains(key) && rows.AsEnumerable().Any(row => Convert.ToInt32(row[key]) == id)) return;
        throw new CertificateSaveResultException($"Certificate {id} was saved, but its history could not be refreshed. Reload history before printing or collecting; do not create it again.");
    }

    internal static void FitColumns(DataGridView grid)
    {
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        UiHelper.FitColumnsToWidth(grid);
    }

    internal static DataTable NewestFirst(DataTable table, string key)
    {
        if (!table.Columns.Contains(key)) return table.Copy();
        string date = table.Columns.Contains("IssuedAt") ? "IssuedAt"
            : table.Columns.Contains("CertificateDate") ? "CertificateDate" : "";
        var view = new DataView(table) { Sort = date.Length == 0 ? $"[{key}] DESC" : $"[{date}] DESC, [{key}] DESC" };
        return view.ToTable();
    }

    internal static void ArrangeColumns(DataGridView grid, string key)
    {
        int index = 0;
        foreach (string name in new[] { "IssuedAt", "CertificateDate", key, "AdmissionNumber", "StaffName",
            "StudentName", "FatherName", "ClassName", "CollectionStatus" })
            if (grid.Columns.Contains(name)) grid.Columns[name].DisplayIndex = index++;
    }

    internal static void Select(DataGridView grid, string key, int id, Action<int> loadRow)
    {
        var rows = grid.Rows.Cast<DataGridViewRow>().Where(row => row.DataBoundItem is DataRowView).ToList();
        var selected = id <= 0 ? rows.FirstOrDefault()
            : rows.FirstOrDefault(row => Convert.ToInt32(((DataRowView)row.DataBoundItem)[key]) == id);
        if (selected is null) return;
        grid.ClearSelection();
        grid.CurrentCell = selected.Cells.Cast<DataGridViewCell>().Where(cell => cell.Visible)
            .OrderBy(cell => cell.OwningColumn.DisplayIndex).First();
        selected.Selected = true;
        // A workspace editor can bind and select its history with no viewport.
        // Load the selected copy even when WinForms cannot scroll a row into view.
        ScrollToRowIfDisplayed(grid, selected.Index);
        loadRow(selected.Index);
    }

    internal static void ScrollToRowIfDisplayed(DataGridView grid, int rowIndex)
    {
        if (grid.IsHandleCreated && grid.DisplayedRowCount(includePartialRow: true) > 0)
            grid.FirstDisplayedScrollingRowIndex = rowIndex;
    }
}
