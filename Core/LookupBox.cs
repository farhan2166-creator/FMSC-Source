namespace FMSC_SMS.Core;

/// <summary>A consistently styled, data-bound lookup with a standard blank choice.</summary>
public sealed class LookupBox : ModernComboBox
{
    public string KeyColumn { get; set; } = "Id";
    public event EventHandler? UserSelectionChanged;
    internal event EventHandler? LookupBound;

    public LookupBox()
    {
        DropDownStyle = ComboBoxStyle.DropDownList;
        Dock = DockStyle.Fill;
        Height = AppMetrics.InputControlHeight;
        Font = AppTheme.NormalFont;
        SelectionChangeCommitted += LookupBox_SelectionChangeCommitted;
    }

    private void LookupBox_SelectionChangeCommitted(object? sender, EventArgs e) =>
        UserSelectionChanged?.Invoke(this, e);

    public int SelectedId =>
        SelectedValue is null or DBNull
            ? 0
            : int.TryParse(Convert.ToString(SelectedValue), out int value)
                ? value
                : 0;

    /// <param name="blankText">Text of the leading blank row.</param>
    public void Bind(DataTable dt, string id, string text, string blankText = "-- Select --", bool includeBlank = true)
    {
        ArgumentNullException.ThrowIfNull(dt);
        if (!dt.Columns.Contains(id))
            throw new InvalidOperationException($"Lookup key column '{id}' was not returned by the database.");
        if (!dt.Columns.Contains(text))
            throw new InvalidOperationException($"Lookup display column '{text}' was not returned by the database.");

        DataSource = null;
        DataTable lookup = dt.Clone();
        DataRow? blank = null;
        if (includeBlank)
        {
            blank = lookup.NewRow();
            if (lookup.Columns[id]!.DataType == typeof(int))
                blank[id] = 0;
            else if (lookup.Columns[id]!.AllowDBNull)
                blank[id] = DBNull.Value;
            else
                blank[id] = Convert.ChangeType(0, lookup.Columns[id]!.DataType);
            blank[text] = blankText;
            lookup.Rows.Add(blank);
        }
        foreach (DataRow row in LookupDisplayOrder.ForBinding(dt, id).Rows)
            lookup.ImportRow(row);

        if (string.Equals(id, "ClassId", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(text, "ClassName", StringComparison.OrdinalIgnoreCase) &&
            dt.Columns.Contains("ProgramName"))
        {
            foreach (DataRow row in lookup.Rows)
            {
                if (row == blank)
                    continue;
                string className = Convert.ToString(row[text]) ?? string.Empty;
                string programName = Convert.ToString(row["ProgramName"]) ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(programName))
                    row[text] = $"{className} ({programName})";
            }
        }

        ValueMember = id;
        DisplayMember = text;
        DataSource = lookup;
        if (Items.Count == 0)
        {
            DataSource = null;
            SelectedIndex = -1;
            LookupBound?.Invoke(this, EventArgs.Empty);
            return;
        }
        // The blank key may be a string or DBNull, rather than numeric zero.
        SelectedIndex = 0;
        LookupBound?.Invoke(this, EventArgs.Empty);
    }

    public void ClearLookup()
    {
        DataSource = null;
        SelectedIndex = -1;
    }
}
