using System.Text.Json;
using FMSC_SMS.Core;
using FMSC_SMS.Pages.Reports;

namespace FMSC_SMS.Pages.Finance.Collection;

/// <summary>Reconciliation records are separate from student payments.</summary>
// Owner policy (BL-08): closing is a revisable snapshot, not a lock on backdated transactions.
// Later payment changes require review and reapproval; retain previous revisions.
// BD-17: closing is a revisable reconciliation snapshot, not a lock on backdated transactions.
// Adjustment Actual Amount stays blank; ordinary inputs use whole rupees without rounding historical totals.
public sealed class DailyCollectionClosingPage : V2Page, IUnsavedChanges, IBusyPage
{
    private readonly ModernDatePicker day = HumanUi.Date();
    private readonly ModernTextBox notes = HumanUi.TextBox(true, 2000);
    private readonly ModernButton submit, approve, print;
    private DataTable? amounts;
    private DataRow? state;
    private DateTime loadedDay;
    private bool busy, loading, dirty;
    private readonly Label totals = new() { Dock = DockStyle.Fill, Font = AppTheme.BodyBoldFont, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label closingState = new() { Dock = DockStyle.Fill, Font = AppTheme.BodyBoldFont,
        TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.MutedText, AutoEllipsis = true };
    public bool IsBusy=>busy;
    public string BusyMessage=>"A daily closing operation is in progress. Wait for its result before leaving.";
    public bool HasUnsavedChanges => dirty;
    public string UnsavedChangesMessage => "The daily closing amounts or notes have not been submitted.";

    public DailyCollectionClosingPage() : base("Daily Collection Closing",
        "Reconcile daily cash and bank totals. Student payments stay unchanged.")
    {
        day.Value = DateTime.Today;
        FinanceHelp.Attach(this,Grid,FinanceHelp.Topic.Closing);
        FinanceHelp.Attach(this,notes,FinanceHelp.Topic.Closing);
        AddClosingAction("Load Day", LoadAsync, "Primary");
        submit = AddClosingAction("Submit Closing", SubmitAsync, "Success");
        approve = AddClosingAction("Approve", ApproveAsync, "Success");
        print = AddClosingAction("Print Summary", PrintAsync, "Secondary");
        FinanceHelp.Attach(this,submit,FinanceHelp.Topic.Closing);
        FinanceHelp.Attach(this,approve,"Approve the checked closing totals. This does not alter any student's payments or balance.","If counted cash matches the recorded Rs. 700, approve that closing after checking the other payment methods.");
        AddClosingAction("Closing History", HistoryAsync, "Secondary");
        UseWrappingToolbar();
        approve.Visible = CurrentUser.IsAdministrator;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.InputControlHeight + 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.TextHeight(AppTheme.NormalFont) * 5 + 12));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.TextHeight(AppTheme.BodyBoldFont) + 12));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(115)));
        var dayHeader = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        dayHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        dayHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        dayHeader.Controls.Add(HumanUi.Field("Closing Date", day), 0, 0);
        dayHeader.Controls.Add(closingState, 1, 0);
        layout.Controls.Add(dayHeader, 0, 0);
        layout.Controls.Add(new Label {
            Dock = DockStyle.Fill, Font = AppTheme.NormalFont,
            Text = "Enter counted cash excluding opening float, and confirmed net totals for other methods. Blank = unchecked; 0 = zero.\nFor Refund/Correction, leave Actual blank and explain the cash/bank effect in Notes.\nChanged payments require a new closing revision. Earlier revisions stay in Closing History."
        }, 0, 1);
        layout.Controls.Add(Grid, 0, 2);
        layout.Controls.Add(totals, 0, 3);
        notes.Multiline = true;
        layout.Controls.Add(HumanUi.Field("Closing Notes / Differences / Refund Review", notes), 0, 4);
        SetContent(layout);
        Grid.ReadOnly = false;
        Grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
        Grid.CellValueChanged += (_, e) => {
            if (loading || e.RowIndex < 0 || e.ColumnIndex < 0 || amounts is null ||
                Grid.Columns[e.ColumnIndex].Name != "Actual Amount") return;
            dirty = true; UpdateDifference(e.RowIndex); UpdateActions();
        };
        Grid.DataError += (_, e) => { e.ThrowException = false; Warn("Enter a valid whole-rupee amount. Fractions are not rounded."); };
        Grid.CellValidating += (_, e) => {
            if (loading || e.RowIndex < 0 || e.ColumnIndex < 0 ||
                Grid.Columns[e.ColumnIndex].Name != "Actual Amount" || Grid.Rows[e.RowIndex].Cells[e.ColumnIndex].ReadOnly) return;
            string text = Convert.ToString(e.FormattedValue) ?? "";
            bool valid = string.IsNullOrWhiteSpace(text) || InputParse.TryWholeAmount(text, out decimal _);
            e.Cancel = !valid;
            Grid.Rows[e.RowIndex].ErrorText = valid ? "" : "Enter a whole-rupee actual amount. Fractions are not rounded.";
        };
        Grid.CurrentCellDirtyStateChanged += (_, _) => {
            if (!loading && Grid.IsCurrentCellDirty &&
            Grid.CurrentCell?.OwningColumn.Name == "Actual Amount") { dirty = true; UpdateActions(); }
        };
        notes.TextChanged += (_, _) => { if (!loading) { dirty = true; UpdateActions(); } };
        day.ValueChanged += (_, _) => {
            UpdateActions();
            if (!Current) SetStatus("Date changed. Load Day before submitting, approving or printing.", true);
            else ShowClosingStatus();
        };
        Defer(LoadAsync);
        UpdateActions();
    }

    private bool Current => state is not null && loadedDay == day.Value.Date;
    private void ShowClosingStatus()
    {
        if (state is null) return;
        SetStatus($"{state["ClosingStatus"]} · Revision {state["Revision"]} · " +
            $"Submitted by: {state["SubmittedBy"]} · Approved by: {state["ApprovedBy"]}",
            Convert.ToString(state["ClosingStatus"])!.StartsWith("Needs review"));
    }
    // Business invariant (UFA-07/08): refund/correction rows are reconciliation adjustments,
    // not cash counted by the clerk. Their Actual Amount remains blank; ordinary methods
    // require whole-rupee entries, while existing historical fractional totals retain precision.
    // Changing this rule changes closing semantics.
    private static bool IsAdjustment(DataRow row) => DailyClosingSummary.IsAdjustment(row);
    private ModernButton AddClosingAction(string text, Func<Task> operation, string role) =>
        AddBusyAction(text, async () => {
            try { await operation(); }
            finally { if (!IsDisposed && IsHandleCreated) BeginInvoke((Action)UpdateActions); }
        }, role);
    private void UpdateActions()
    {
        // Mirror existing state for visibility; do not change submission eligibility.
        closingState.Text = !Current ? "Load selected day" : dirty ? "Unsubmitted changes" :
            $"{state!["ClosingStatus"]} · Revision {state["Revision"]}";
        closingState.ForeColor = !Current ? AppTheme.MutedText : dirty ? AppTheme.Text :
            Convert.ToString(state!["ClosingStatus"]) == "Approved" ? AppTheme.Success : AppTheme.HeadingText;
        submit.Enabled = !busy && Current && day.Value.Date <= DateTime.Today;
        approve.Enabled = !busy && Current && !dirty && Convert.ToString(state!["ClosingStatus"]) == "Submitted";
        print.Enabled = !busy && Current && !dirty;
        day.Enabled = notes.Enabled = Grid.Enabled = !busy;
    }
    private void UpdateDifference(int index)
    {
        if (amounts is null || index >= amounts.Rows.Count) return;
        // Use bound row identity, including after the user sorts the grid.
        if (Grid.Rows[index].DataBoundItem is not DataRowView view) return;
        bool wasLoading = loading;
        loading = true;
        try
        {
            view.Row["Difference"] = view.Row.IsNull("Actual Amount") ? DBNull.Value :
            Convert.ToDecimal(view.Row["Actual Amount"]) - Convert.ToDecimal(view.Row["Recorded Amount"]);
        }
        finally { loading = wasLoading; }
        UpdateTotals();
    }
    private void UpdateTotals()
    {
        if (amounts is null) { totals.Text = ""; return; }
        decimal recorded = amounts.AsEnumerable().Sum(r => Convert.ToDecimal(r["Recorded Amount"]));
        bool complete = amounts.AsEnumerable().Where(r => !IsAdjustment(r)).All(r => !r.IsNull("Actual Amount"));
        decimal actual = amounts.AsEnumerable().Where(r => !r.IsNull("Actual Amount")).Sum(r => Convert.ToDecimal(r["Actual Amount"]));
        totals.Text = $"Recorded net: Rs. {recorded:#,0.##} · Actual net: " + (complete ? $"Rs. {actual:#,0.##} · Difference: Rs. {actual - recorded:#,0.##}" : "Awaiting all amounts");
    }
    private async Task LoadAsync()
    {
        if (busy) return;
        if (dirty && !ModernMessageDialog.ConfirmDiscardOnce(this, () => dirty, ref discard,
            "Discard the unsubmitted closing amounts and notes and reload the selected day?")) return;
        discard = false;
        busy = true; UpdateActions();
        DateTime requested = day.Value.Date;
        try
        {
            var result = await DatabaseHelper.QueryUserSetAsync("GetDailyCollectionClosing",
                DatabaseHelper.Date("@ClosingDate", requested));
            if (IsDisposed) return;
            loading = true;
            amounts = result.Tables[0]; state = result.Tables[1].Rows[0]; loadedDay = requested;
            Bind(amounts);
            foreach (DataGridViewColumn column in Grid.Columns)
            {
                column.ReadOnly = column.Name != "Actual Amount";
                if (column.Name.Contains("Amount") || column.Name == "Difference") column.DefaultCellStyle.Format = "#,0.##";
            }
            Grid.Columns["Actual Amount"].DefaultCellStyle.BackColor = AppTheme.SurfaceSecondary;
            foreach (DataGridViewRow row in Grid.Rows)
                if (row.DataBoundItem is DataRowView view && IsAdjustment(view.Row))
                    row.Cells["Actual Amount"].ReadOnly = true;
            notes.Text = Convert.ToString(state["Notes"]) ?? "";
            for (int i = 0; i < Grid.Rows.Count; i++) UpdateDifference(i);
            dirty = false;
            UpdateTotals();
            ShowClosingStatus();
        }
        catch (Exception ex) { state = null; if (!IsDisposed) Fail(ex); }
        finally { loading = false; busy = false; if (!IsDisposed) UpdateActions(); }
    }
    private bool discard;
    private async Task SubmitAsync()
    {
        if (busy || !Current || amounts is null) return;
        if (!Grid.EndEdit()) { Warn("Finish entering a valid actual amount first."); return; }
        var actual = new List<object>();
        foreach (DataRow row in amounts.Rows)
        {
            if (IsAdjustment(row)) continue;
            if (row.IsNull("Actual Amount")) { Warn($"Enter the actual amount for {row["Payment Method"]}."); return; }
            decimal value = Convert.ToDecimal(row["Actual Amount"]);
            if (!WholeRupees.IsWhole(value)) { Warn("Closing actual amounts must be whole rupees. Fractions are not rounded."); return; }
            actual.Add(new { Method = Convert.ToString(row["Payment Method"]), Amount = value });
        }
        busy = true; UpdateActions();
        try
        {
            await DatabaseHelper.ExecuteUserAsync("SaveDailyCollectionClosing",
                DatabaseHelper.Date("@ClosingDate", loadedDay), DatabaseHelper.P("@ExpectedClosingId", state!["ClosingId"]),
                DatabaseHelper.P("@SnapshotHash", state["SnapshotHash"]),
                DatabaseHelper.P("@ActualAmounts", JsonSerializer.Serialize(actual)), DatabaseHelper.P("@Notes", notes.Text.Trim()));
            dirty = false;
        }
        catch (Exception ex) { if (!IsDisposed) Fail(ex); return; }
        finally { busy = false; if (!IsDisposed) UpdateActions(); }
        if (!IsDisposed) await LoadAsync();
    }
    private async Task ApproveAsync()
    {
        if (!Grid.EndEdit()) return;
        if (busy || !Current || dirty || !CurrentUser.IsAdministrator) return;
        busy = true; UpdateActions();
        try
        {
            await DatabaseHelper.ExecuteUserAsync("ApproveDailyCollectionClosing",
                DatabaseHelper.Date("@ClosingDate", loadedDay), DatabaseHelper.P("@ClosingId", state!["ClosingId"]),
                DatabaseHelper.P("@SnapshotHash", state["SnapshotHash"]));
        }
        catch (Exception ex) { if (!IsDisposed) Fail(ex); return; }
        finally { busy = false; if (!IsDisposed) UpdateActions(); }
        if (!IsDisposed) await LoadAsync();
    }
    private async Task PrintAsync()
    {
        if (!Grid.EndEdit()) return;
        if (!Current || dirty || amounts is null || busy) return;
        busy = true; UpdateActions();
        try
        {
            // Recheck before printing so an old approval cannot be presented as current.
            var latest = await DatabaseHelper.QueryUserSetAsync("GetDailyCollectionClosing", DatabaseHelper.Date("@ClosingDate", loadedDay));
            var current = latest.Tables[1].Rows[0];
            if (!Equals(current["SnapshotHash"], state!["SnapshotHash"]) || !Equals(current["ClosingId"], state["ClosingId"]) ||
                !Equals(current["ClosingStatus"], state["ClosingStatus"])) { Warn("This day changed. Load it again before printing."); return; }
            string school = await InstitutionProfile.GetNameAsync();
            if (string.IsNullOrWhiteSpace(school)) { Warn("Complete School Profile before printing."); return; }
            // UFA-07/08: blank actuals stay unchecked; print preserves historical decimal precision.
            var printable = DailyClosingSummary.CreatePrintable(amounts);
            GridPrintJob.ShowPreview(FindForm(), school, "Daily Collection Closing",
                $"{loadedDay:dd-MMM-yyyy} · {state["ClosingStatus"]} · Revision {state["Revision"]}\n" +
                $"Submitted: {state["SubmittedBy"]} {state["SubmittedAt"]} · Approved: {state["ApprovedBy"]} {state["ApprovedAt"]}\nNotes: {notes.Text}",
                printable, CurrentUser.Username, "#,0.##");
        }
        catch (Exception ex) { if (!IsDisposed) Fail(ex); }
        finally { busy = false; if (!IsDisposed) UpdateActions(); }
    }
    private async Task HistoryAsync()
    {
        if (busy) return;
        busy = true; UpdateActions();
        try
        {
            var history = await DatabaseHelper.QueryUserAsync("GetDailyCollectionClosingHistory",
                DatabaseHelper.Date("@ClosingDate", day.Value.Date));
            if (IsDisposed) return;
            using var dialog = new ClosingHistoryDialog(history, day.Value.Date);
            dialog.ShowDialog(FindForm());
        }
        catch (Exception ex) { if (!IsDisposed) Fail(ex); }
        finally { busy = false; if (!IsDisposed) UpdateActions(); }
    }
}
