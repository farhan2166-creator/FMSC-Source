using Microsoft.Data.SqlClient;
using FMSC_SMS.Core;
namespace FMSC_SMS.Pages.Reports.Finance;
public sealed class OutstandingAgingReportPage : CanonicalFinanceReportPage
{
    private readonly ModernComboBox dueStatus = CreateDueStatus();
    private static ModernComboBox CreateDueStatus()
    {
        var choice=HumanUi.Combo();
        choice.Items.AddRange(new object[] { "None", "Overdue", "Due Today", "Not Yet Due", "No Due Date" });
        choice.SelectedIndex=0;
        ReportUi.RegisterChoiceFilter(choice);
        return choice;
    }
    private readonly ModernNumericTextBox minimumBalance = new() { Minimum = 0, Maximum = 1000000000m, ZeroAsPlaceholder = true };
    private readonly ModernTextBox minimumDays = HumanUi.TextBox(false, 6), maximumDays = HumanUi.TextBox(false, 6);
    private ModernButton? openStatement, sendMessage;
    public OutstandingAgingReportPage() : base(
        "Outstanding & Aging", "Unpaid fees, split into overdue, due today and upcoming amounts. Students without linked siblings appear individually.",
        "GetOutstandingAndAgingReport", "FMSC_Outstanding_Aging",
        new[] { "Students", "Charges", "Aging", "Families" },
        Array.Empty<string>(), false, true, false, false, classOutstandingPrint: true)
    {
        SetToolTip(dueStatus, "None applies no due-status filter. Other choices include students or families with fees in that category, keeping their full balances visible.");
        minimumDays.PlaceholderText = "Any"; maximumDays.PlaceholderText = "Any";
        SetToolTip(minimumDays, "Student/family rows use the oldest unpaid charge. Amount columns keep the full balance and split its due status.");
        WholeNumberInput.Attach(minimumDays);WholeNumberInput.Attach(maximumDays);
        dueStatus.SelectedIndexChanged += (_, _) => FiltersChanged();
        minimumBalance.TextChanged += (_, _) => FiltersChanged();
        minimumDays.TextChanged += (_, _) => FiltersChanged(); maximumDays.TextChanged += (_, _) => FiltersChanged();
        Grid.SelectionChanged += (_, _) => UpdateStatementAction();
        View.SelectedIndexChanged += (_, _) => UpdateStatementAction();
    }
    protected override IEnumerable<ModernButton> AdditionalReportActions()
    {
        openStatement = AddBusyAction("Open Student Account", OpenStatementAsync, "Secondary");
        openStatement.Enabled = false;
        sendMessage = AddBusyAction("Message Student…", OpenFeeMessageAsync, "Secondary");
        sendMessage.Enabled = false;
        return new[] { openStatement, sendMessage };
    }
    protected override IEnumerable<(string, Control)> AdditionalReportFilters() => new (string, Control)[] {
        ("Due status", dueStatus), ("Minimum outstanding (Rs.)", minimumBalance),
        ("Days overdue from", minimumDays), ("Days overdue to", maximumDays)
    };
    protected override string AdditionalFilterSignature => $"{dueStatus.Text}|{NumericFilterSignature(minimumBalance.Text)}|{minimumDays.Text}|{maximumDays.Text}";
    protected override string AdditionalFilterDescription => ViewName == "Aging" ? "" :
        $"Due status: {dueStatus.Text}   Minimum outstanding: Rs. {minimumBalance.Value:N0}   Days overdue: {(minimumDays.Text.Trim().Length == 0 ? "Any" : minimumDays.Text)} to {(maximumDays.Text.Trim().Length == 0 ? "Any" : maximumDays.Text)}";
    protected override string AdditionalExportFileScope
    {
        get
        {
            if (ViewName == "Aging") return "";
            var parts = new List<string>();
            if (dueStatus.Text != "None") parts.Add(dueStatus.Text);
            if (minimumBalance.Value > 0) parts.Add("min Rs " + minimumBalance.Value.ToString("#,##0.##"));
            string first = minimumDays.Text.Trim(), last = maximumDays.Text.Trim();
            if (first.Length > 0 || last.Length > 0)
                parts.Add(first.Length > 0 && last.Length > 0 ? "days " + first + " to " + last
                    : first.Length > 0 ? "days from " + first : "days to " + last);
            return string.Join(" - ", parts);
        }
    }
    protected override void UpdateViewControls()
    {
        base.UpdateViewControls();
        foreach (Control control in new Control[] { dueStatus, minimumBalance, minimumDays, maximumDays }) control.Enabled = ViewName != "Aging";
    }
    private bool TryDays(out int from, out int? to)
    {
        from = 0; to = null;
        if (minimumDays.Text.Trim().Length > 0 && (!int.TryParse(minimumDays.Text, out from) || from < 0)) return false;
        if (maximumDays.Text.Trim().Length > 0)
        {
            if (!int.TryParse(maximumDays.Text, out int max) || max < from) return false;
            to = max;
        }
        return true;
    }
    protected override bool ValidateAdditionalFilters()
    {
        if (ViewName == "Aging" || TryDays(out _, out _)) return true;
        Warn("Days overdue must be non-negative whole numbers, with To on or after From. Leave blank for any value.");
        return false;
    }
    protected override Func<DataTable, DataTable?, (DataTable Rows, DataTable? Totals)> CaptureResultTransform(SqlParameter[] parameters)
    {
        bool aging = ViewName == "Aging";
        TryDays(out int from, out int? to);
        var filter = new OutstandingReportFilter(dueStatus.Text == "Overdue", minimumBalance.Value, from, to) { DueStatus = dueStatus.Text };
        return (rows, totals) => {
            PrepareReportData(rows, parameters);
            if (aging || !filter.Active) return (rows, totals);
            var filtered = filter.Apply(rows);
            return (filtered, OutstandingReportFilter.Totals(filtered));
        };
    }
    protected override void OnReportPageBound() { base.OnReportPageBound(); UpdateStatementAction(); }
    protected override bool CanPrintClassOutstanding => ViewName == "Aging" ||
        (TryDays(out int from,out int? to) && dueStatus.SelectedIndex == 0 && from==0 && to is null && NumericFilterSignature(minimumBalance.Text)=="0");
    protected override void ReportAvailabilityChanged() { base.ReportAvailabilityChanged(); UpdateStatementAction(); }
    internal static bool CanOpenStudentAccount => StudentAccountStatementPage.CanOpenAccount
        && CurrentUser.HasPermission("Fees.View");
    internal static bool CanComposeReportFeeMessage =>
        CommunicationAccess.CanMessageStudent(CurrentUser.HasPermission, fees: true)
        && (CurrentUser.HasPermission("Reports.View") || CurrentUser.HasPermission("Fees.Collect"));
    private void UpdateStatementAction()
    {
        if (openStatement is null) return;
        if (sendMessage is not null)
        {
            sendMessage.Visible = CanComposeReportFeeMessage;
            sendMessage.Enabled = CanComposeReportFeeMessage && CurrentReportAvailable
                && ViewName is ("Students" or "Charges")
                && Grid.CurrentRow?.DataBoundItem is DataRowView;
        }
        openStatement.Visible = CanOpenStudentAccount;
        openStatement.Enabled = CurrentReportAvailable && ViewName is "Students" or "Charges"
            && CanOpenStudentAccount && Grid.CurrentRow?.DataBoundItem is DataRowView;
    }
    /// <summary>
    /// This optional row action opens the same compact, permission-checked
    /// Fee Message flow used by Fee Collection. It never sends, writes, or
    /// changes report filters and never assumes an aggregate Aging row is
    /// one student.
    /// </summary>
    private async Task OpenFeeMessageAsync()
    {
        if (!CurrentReportAvailable || !CanComposeReportFeeMessage
            || ViewName is not ("Students" or "Charges")
            || Grid.CurrentRow?.DataBoundItem is not DataRowView selected) return;
        string reference=Convert.ToString(selected.Row["Adm. / QR No."])?.Trim() ?? "";
        if (reference.Length==0) { Warn("Select a student record first."); return; }
        string signature=ReportFilterSignature();
        int actor=CurrentUser.UserId;
        try
        {
            using var students=await Task.Run(() => DatabaseHelper.QuerySql("""
                EXEC dbo.RequireAnyPermission @UserId,N'Reports.View',N'Fees.Collect';
                EXEC dbo.RequirePermission @UserId,N'Fees.View';
                EXEC dbo.RequirePermission @UserId,N'Students.View';
                EXEC dbo.RequirePermission @UserId,N'Communication.Send';
                SELECT AdmissionId FROM dbo.Students WHERE
                    COALESCE(NULLIF(AdmissionNumber,N''),NULLIF(QuickReference,N''),
                    CONCAT(N'ID-',AdmissionId))=@Reference;
                """,DatabaseHelper.Int("@UserId",actor),
                DatabaseHelper.P("@Reference",reference)));
            if (IsDisposed || actor!=CurrentUser.UserId
                || !CanComposeReportFeeMessage || !CurrentReportAvailable
                || signature!=ReportFilterSignature()
                || Grid.CurrentRow?.DataBoundItem is not DataRowView current
                || Convert.ToString(current.Row["Adm. / QR No."])?.Trim()!=reference)
                return;
            if (students.Rows.Count!=1)
            {
                Warn("The selected student's record changed. Refresh the report before messaging.");
                return;
            }
            CommunicationRecordEntry.Open(this,
                Convert.ToInt32(students.Rows[0]["AdmissionId"]),
                "Admission " + reference, fees:true);
        }
        catch (Exception ex) { if (!IsDisposed) Fail(ex); }
    }

    private async Task OpenStatementAsync()
    {
        if (!CurrentReportAvailable || !CanOpenStudentAccount || ViewName is not ("Students" or "Charges")
            || Grid.CurrentRow?.DataBoundItem is not DataRowView selected) return;
        string reference = Convert.ToString(selected.Row["Adm. / QR No."]) ?? "";
        string signature = ReportFilterSignature();
        int actor = CurrentUser.UserId;
        var rows = await Task.Run(() => DatabaseHelper.QuerySql("""
            EXEC dbo.RequireAnyPermission @UserId,N'Reports.View',N'Fees.Collect';
            EXEC dbo.RequirePermission @UserId,N'Fees.View';
            SELECT AdmissionId FROM dbo.Students
            WHERE COALESCE(NULLIF(AdmissionNumber,N''),NULLIF(QuickReference,N''),CONCAT(N'ID-',AdmissionId))=@Reference;
            """, DatabaseHelper.Int("@UserId",actor), DatabaseHelper.P("@Reference",reference)));
        if (IsDisposed || CurrentUser.UserId != actor || !CurrentReportAvailable || signature != ReportFilterSignature() || !CanOpenStudentAccount
            || Grid.CurrentRow?.DataBoundItem is not DataRowView current || Convert.ToString(current.Row["Adm. / QR No."]) != reference) return;
        if (rows.Rows.Count != 1) { Warn("The selected student's account could not be identified. Refresh the report."); return; }
        using var dialog = new ApplicationWindowForm { Text = PageTitles.StudentFeeAccount, StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(AppScale.Scale(1250),AppScale.Scale(760)), ShowInTaskbar = false };
        var statement = StudentAccountStatementPage.CreateAccount(Convert.ToInt32(rows.Rows[0]["AdmissionId"]));
        statement.Dock=DockStyle.Fill;
        dialog.Controls.Add(statement);
        dialog.FormClosing += (_, e) => { if (statement.IsBusy) e.Cancel=true; };
        dialog.ShowDialog(FindForm());
    }
    protected override IEnumerable<SqlParameter> BuildParameters()
    {
        yield return DatabaseHelper.P("@View", ViewName);
        yield return DatabaseHelper.Int("@CampusId", Campus.SelectedId);
        yield return DatabaseHelper.Int("@AcademicYearId", Year.SelectedId);
        yield return DatabaseHelper.Int("@ClassId", Class.SelectedId);
        yield return DatabaseHelper.Int("@SectionId", Section.SelectedId);
        yield return DatabaseHelper.Date("@AsOfDate", DateTime.Today);
    }
    protected override void PrepareReportData(DataTable data, SqlParameter[] parameters)
    {
        string? dueColumn = data.Columns.Contains("Oldest Due Date") ? "Oldest Due Date"
            : data.Columns.Contains("Due Date") ? "Due Date" : null;
        // The grouped Aging view combines dates into buckets, so it has no single day count.
        if (dueColumn is null) return;
        DateTime asOf = ((DateTime)parameters.Single(p => p.ParameterName == "@AsOfDate").Value).Date;
        var days = data.Columns.Add("Days Overdue", typeof(int));
        days.SetOrdinal(data.Columns[dueColumn]!.Ordinal + 1);
        foreach (DataRow row in data.Rows)
            if (row[dueColumn] is DateTime due)
                row[days] = Math.Max(0, (asOf - due.Date).Days);
    }
    protected override async Task PreviewClassOutstandingAsync()
    {
        if (!CanPrintClassOutstanding) { Warn("Use Print for the exact-day or minimum-balance filtered report. Class / Section A4 includes all unpaid charges within the location filters."); return; }
        if (!CurrentUser.HasPermission("Reports.View")) return;
        var scope = (Campus: Campus.SelectedId, Year: Year.SelectedId, Class: Class.SelectedId, Section: Section.SelectedId);
        string yearSelection = scope.Year == 0 ? "All academic years" : Year.Text;
        DateTime generated = DateTime.Now;
        try
        {
            var students = await Task.Run(() => DatabaseHelper.QuerySql(ClassOutstandingReportData.Query,
                DatabaseHelper.Int("@UserId", CurrentUser.UserId), DatabaseHelper.Int("@CampusId", scope.Campus),
                DatabaseHelper.Int("@AcademicYearId", scope.Year), DatabaseHelper.Int("@ClassId", scope.Class),
                DatabaseHelper.Int("@SectionId", scope.Section)));
            var school = await InstitutionProfile.GetNameAsync();
            if (IsDisposed) return;
            if (scope != (Campus.SelectedId, Year.SelectedId, Class.SelectedId, Section.SelectedId))
            { Warn("Filters changed. Open Class / Section A4 again for the selected filters."); return; }
            if (students.Rows.Count == 0) { Warn("No outstanding charges were found for the selected filters."); return; }
            if (string.IsNullOrWhiteSpace(school)) { Warn("Check School Profile before printing outstanding charges."); return; }
            var data = ClassOutstandingReportData.Create(students, school, yearSelection, generated);
            RdlcReportPreview.Show(FindForm(), "Outstanding Charges by Class and Section",
                Path.Combine(AppContext.BaseDirectory, "Reports", "ClassOutstandingCharges.rdlc"), "ClassOutstandingData", data);
        }
        catch (Exception ex) { if (!IsDisposed) Fail(ex); }
    }

}
