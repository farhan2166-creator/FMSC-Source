using FMSC_SMS.Core;

namespace FMSC_SMS.Pages.Staff;

/// <summary>Read-only employment cards; all writes use the existing employment dialog.</summary>
internal sealed class StaffEmploymentOverview : UserControl
{
    private readonly TableLayoutPanel details;
    private readonly DataGridView history;
    private readonly Label historyMessage;
    private readonly ModernButton editJob;
    private readonly ModernButton addJob;
    private StaffProfilePage.Selection? selected;
    private int generation;
    private int activeId;
    internal event Action? EmploymentChanged;
    internal Func<bool>? CanOpenEditor { get; set; }

    internal StaffEmploymentOverview()
    {
        Height = AppScale.Scale(340);
        Padding = new Padding(0, AppMetrics.SpacingMd, 0, 0);
        editJob = new ModernButton { Text = "Edit Job", Width = AppScale.Scale(110), ThemeRole = "Secondary", Enabled = false };
        addJob = new ModernButton { Text = "Add Job", Width = AppScale.Scale(110), ThemeRole = "Secondary", Enabled = false };
        var jobCard = new HumanCard("Job Details", "Current active employment", accessory: editJob);
        var historyCard = new HumanCard("Employment History", "Saved jobs for the selected staff member", accessory: addJob);
        // Dock the actions before the header's top/bottom labels consume its height.
        jobCard.Header.Controls.SetChildIndex(editJob, jobCard.Header.Controls.Count - 1);
        historyCard.Header.Controls.SetChildIndex(addJob, historyCard.Header.Controls.Count - 1);
        details = new TableLayoutPanel {
            Dock = DockStyle.Top, ColumnCount = 2, RowCount = 0,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty
        };
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        var detailScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        detailScroll.Controls.Add(details);
        jobCard.Content.Controls.Add(detailScroll);
        history = new DataGridView {
            Dock = DockStyle.Fill, AutoGenerateColumns = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
            ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            AllowUserToOrderColumns = false, RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
            BackgroundColor = AppTheme.Surface, BorderStyle = BorderStyle.None
        };
        foreach (var (key, caption) in new[] {
            ("Designation", "Designation"), ("Department", "Department"),
            ("HireDate", "Hire Date"), ("LeavingDate", "Leaving Date"), ("Status", "Status") })
        {
            var column = new DataGridViewTextBoxColumn { Name = key, DataPropertyName = key, HeaderText = caption };
            if (key.EndsWith("Date")) column.DefaultCellStyle.Format = "dd-MMM-yyyy";
            history.Columns.Add(column);
        }
        history.Columns.Add(new DataGridViewButtonColumn {
            Name = "Edit", HeaderText = "", Text = "Edit", UseColumnTextForButtonValue = true,
            Width = AppScale.Scale(65), MinimumWidth = AppScale.Scale(65)
        });
        UiHelper.ApplyGridPresentation(history);
        historyMessage = new Label {
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
            Font = AppTheme.NormalFont, ForeColor = AppTheme.MutedText,
            Text = "Choose a staff member to view employment."
        };
        historyCard.Content.Controls.Add(history);
        historyCard.Content.Controls.Add(historyMessage);
        var cards = new TableLayoutPanel {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty
        };
        cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        cards.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        jobCard.Margin = new Padding(0, 0, AppMetrics.SpacingSm, 0);
        historyCard.Margin = new Padding(AppMetrics.SpacingSm, 0, 0, 0);
        cards.Controls.Add(jobCard, 0, 0);
        cards.Controls.Add(historyCard, 1, 0);
        Controls.Add(cards);
        editJob.Click += async (_, _) => await OpenEditorAsync(activeId);
        addJob.Click += async (_, _) => await OpenEditorAsync(0);
        history.CellContentClick += async (_, e) => {
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || history.Columns[e.ColumnIndex].Name != "Edit") return;
            if (history.Rows[e.RowIndex].DataBoundItem is DataRowView row)
                await OpenEditorAsync(Convert.ToInt32(row["StaffEmploymentId"]));
        };
        SetDetailMessage("Choose a staff member to view job details.");
    }

    internal async Task SelectStaffAsync(StaffProfilePage.Selection? staff)
    {
        selected = staff;
        int request = ++generation;
        activeId = 0;
        editJob.Enabled = addJob.Enabled = false;
        history.DataSource = null;
        history.Visible = false;
        historyMessage.Visible = true;
        historyMessage.Text = staff is null ? "Choose a staff member to view employment." : "Loading employment…";
        SetDetailMessage(staff is null ? "Choose a staff member to view job details." : "Loading job details…");
        if (staff is null) return;
        try
        {
            var records = await DatabaseHelper.QueryUserAsync("GetStaffEmployment", DatabaseHelper.Int("@StaffId", staff.Id));
            if (IsDisposed || request != generation) return;
            DisplayRecords(records);
        }
        catch (Exception ex)
        {
            if (IsDisposed || request != generation) return;
            AppExceptionHandler.LogNonFatal(ex, "StaffEmploymentOverview: load");
            SetDetailMessage("Employment could not be loaded. Select the staff member again to retry.");
            historyMessage.Text = "Employment could not be loaded.";
        }
    }

    private void DisplayRecords(DataTable records)
    {
        activeId = 0;
        editJob.Enabled = false;
        var ordered = records.AsEnumerable()
            .OrderBy(row => CellText(row, "Status") == "Active" ? 0 : 1)
            .ThenByDescending(row => row["HireDate"] is DBNull ? DateTime.MinValue : Convert.ToDateTime(row["HireDate"]));
        var active = ordered.FirstOrDefault(row => CellText(row, "Status") == "Active");
        bool canManage = CurrentUser.HasPermission("Staff.Manage");
        addJob.Enabled = canManage;
        history.Columns["Edit"].Visible = canManage;
        history.DataSource = records.Rows.Count == 0 ? records : ordered.CopyToDataTable();
        history.Visible = records.Rows.Count > 0;
        historyMessage.Visible = records.Rows.Count == 0;
        historyMessage.Text = "No employment history. Use Add Job to enter the first job.";
        if (active is null)
        {
            SetDetailMessage("No active job recorded.");
            return;
        }
        activeId = Convert.ToInt32(active["StaffEmploymentId"]);
        editJob.Enabled = canManage;
        ClearDetails();
        int index = 0;
        foreach (var (key, caption) in new[] {
            ("Designation", "Designation"), ("Department", "Department"),
            ("EmploymentType", "Employment Type"), ("HireDate", "Hire Date"),
            ("Status", "Status"), ("Remarks", "Remarks") })
        {
            int row = index / 2;
            int column = index % 2;
            if (row >= details.RowCount)
            {
                details.RowCount = row + 1;
                details.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            }
            var field = new TableLayoutPanel {
                Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1, RowCount = 2,
                Margin = new Padding(0, 0, column == 0 ? AppMetrics.SpacingMd : 0, AppMetrics.SpacingSm)
            };
            field.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            field.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            field.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            field.Controls.Add(new Label {
                Text = caption, AutoSize = true, Font = AppTheme.FieldCaptionFont,
                Dock = DockStyle.Fill, ForeColor = AppTheme.Text, Margin = new Padding(0, 0, 0, 2)
            }, 0, 0);
            field.Controls.Add(new Label {
                Text = CellText(active, key) is { Length: > 0 } value ? value : "—",
                AutoSize = true, Dock = DockStyle.Fill, Font = AppTheme.NormalFont,
                ForeColor = AppTheme.Text, UseMnemonic = false, Margin = Padding.Empty
            }, 0, 1);
            details.Controls.Add(field, column, row);
            index++;
        }
    }

    private static string CellText(DataRow row, string key) => !row.Table.Columns.Contains(key) || row[key] is DBNull ? "" :
        row[key] is DateTime date ? date.ToString("dd-MMM-yyyy") : Convert.ToString(row[key]) ?? "";

    private void ClearDetails()
    {
        foreach (Control child in details.Controls.Cast<Control>().ToArray()) child.Dispose();
        details.Controls.Clear(); details.RowStyles.Clear(); details.RowCount = 0;
    }
    private void SetDetailMessage(string text)
    {
        ClearDetails();
        details.RowCount = 1;
        details.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(80)));
        var message = new Label {
            Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = AppTheme.MutedText, Font = AppTheme.NormalFont
        };
        details.Controls.Add(message, 0, 0); details.SetColumnSpan(message, 2);
    }
    private async Task OpenEditorAsync(int employmentId)
    {
        if (selected is null || !CurrentUser.HasPermission("Staff.Manage") || CanOpenEditor?.Invoke() == false) return;
        var staff = selected;
        using var dialog = new StaffEmploymentDialog(staff, employmentId);
        bool changed = false;
        dialog.EmploymentChanged += () => changed = true;
        dialog.ShowDialog(FindForm());
        if (!changed || IsDisposed || selected?.Id != staff.Id) return;
        await SelectStaffAsync(staff);
        EmploymentChanged?.Invoke();
    }
}

