using System.Data;
using System.Globalization;
using FMSC_SMS.Core;

namespace FMSC_SMS.Pages.Communication;

internal sealed class CommunicationTemplatesDialog : ApplicationWindowForm
{
    private const string EditTemplateText = "Edit Template…";
    private const string CustomizeTemplateText = "Customize Template…";
    private readonly DataGridView templates = HumanUi.Grid(false);
    private readonly ModernTextBox search = HumanUi.TextBox(false, 100);
    private readonly ModernComboBox category = HumanUi.Combo();
    private readonly ModernTextBox preview = HumanUi.TextBox(true, 1600);
    private readonly ModernButton edit = new() { Text = CustomizeTemplateText, Enabled = false };
    private readonly ModernButton add = new() { Text = "New Template…", ThemeRole = "Primary", Enabled = false };
    private readonly Label feedback = new() { Dock = DockStyle.Fill, AutoEllipsis = true };
    private readonly Func<Task<DataTable>> read;
    private readonly TableLayoutPanel layout;
    private bool busy;
    internal bool Changed { get; private set; }

    internal CommunicationTemplatesDialog(Func<Task<DataTable>>? read = null)
    {
        this.read = read ?? CommunicationManagementStore.ReadTemplatesAsync;
        Text = "Message Templates";
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        // Keep the list, the complete body preview and actions comfortably visible,
        // while allowing the administrator to resize the window if needed.
        ClientSize = new Size(AppScale.Scale(1000), AppScale.Scale(680));
        MinimumSize = new Size(AppScale.Scale(760), AppScale.Scale(540));
        BackColor = AppTheme.PageBackground;
        search.Name = "communicationTemplateSearch";
        category.Name = "communicationTemplateCategory";
        category.Items.AddRange(new object[] { "All Categories", "Fees & Payments", "Admissions & Students", "Examinations & Results", "Announcements & Greetings", "Staff & Other" });
        category.SelectedIndex = 0;
        var searchField = HumanUi.Field("Search templates", search);
        var categoryField = HumanUi.Field("Category", category);
        var filters = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty
        };
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        filters.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        searchField.Dock = DockStyle.Fill;
        categoryField.Dock = DockStyle.Fill;
        filters.Controls.Add(searchField, 0, 0);
        filters.Controls.Add(categoryField, 1, 0);
        templates.AutoGenerateColumns = false;
        foreach (var (field, title, weight) in new[]
        {
            ("Name", "Template", 210), ("Channel", "Channel", 75),
            ("Purpose", "Purpose", 105), ("Active", "Active", 55)
        })
            templates.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = field, DataPropertyName = field, HeaderText = title,
                FillWeight = weight, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
            });
        templates.CellFormatting += (_, e) =>
        {
            string column = templates.Columns[e.ColumnIndex].Name;
            if (column == "Active" && e.Value is bool active) e.Value = active ? "Yes" : "No";
            else if (column == "Purpose" && e.Value is string purpose) e.Value = CommunicationTemplateDialog.PurposeLabel(purpose);
            else if (column == "Channel" && templates.Rows[e.RowIndex].DataBoundItem is DataRowView row)
                e.Value = ChannelLabel(row.Row);
            else return;
            e.FormattingApplied = true;
        };
        preview.ReadOnly = true;
        preview.ScrollBars = ScrollBars.Vertical;
        var close = new ModernButton { Text = "Close", DialogResult = DialogResult.Cancel };
        CancelButton = close;
        var buttons = HumanUi.DialogButtons(add, edit, close);
        buttons.Dock = DockStyle.Fill;
        layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(AppScale.Scale(14))
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.FieldRowHeight));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(30)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.ButtonHeight + AppScale.Scale(10)));
        layout.Controls.Add(filters, 0, 0);
        layout.Controls.Add(templates, 0, 1);
        layout.Controls.Add(HumanUi.Field("Template preview", preview), 0, 2);
        layout.Controls.Add(feedback, 0, 3);
        layout.Controls.Add(buttons, 0, 4);
        Controls.Add(layout);
        search.TextChanged += (_, _) =>
        {
            if (templates.DataSource is not DataTable rows) return;
            ApplySearch(rows);
            ShowSelected();
        };
        category.SelectedIndexChanged += (_, _) =>
        {
            if (templates.DataSource is not DataTable rows) return;
            ApplySearch(rows);
            ShowSelected();
        };
        templates.CurrentCellChanged += (_, _) => ShowSelected();
        templates.CellDoubleClick += async (_, e) =>
        {
            if (e.RowIndex >= 0 && templates.Rows[e.RowIndex].DataBoundItem is DataRowView row)
                await RunAsync(() => EditAsync(row.Row));
        };
        edit.Click += async (_, _) =>
        {
            if (templates.CurrentRow?.DataBoundItem is DataRowView row) await RunAsync(() => EditAsync(row.Row));
        };
        add.Click += async (_, _) => await RunAsync(() => EditAsync(null));
        Shown += async (_, _) => await RunAsync(RefreshAsync);
        FormClosing += (_, e) => { if (busy) { e.Cancel = true; DialogResult = DialogResult.None; } };
        FormClosed += (_, _) => (templates.DataSource as DataTable)?.Dispose();
    }

    private void ShowSelected()
    {
        var row = (templates.CurrentRow?.DataBoundItem as DataRowView)?.Row;
        preview.Text = row?.Field<string>("Body") ?? "";
        edit.Enabled = !busy && row is not null;
        edit.Text = row?.Field<int>("TemplateId") > 0 ? EditTemplateText : CustomizeTemplateText;
    }

    private static string ChannelLabel(DataRow row) => row.Field<int>("TemplateId") < 0
        ? row.Field<string>("Channel") == "WhatsApp" ? "WhatsApp" : "WhatsApp / SMS"
        : row.Field<string>("Channel") is "Sms" ? "SMS" : row.Field<string>("Channel") ?? "";

    // Presentation-only grouping: existing SQL template purposes and saved
    // content remain unchanged. A newly created template is immediately
    // available in its appropriate category without schema migration.
    private static string TemplateCategory(DataRow row) => CategoryFor(
        row.Field<string>("Purpose") ?? "", row.Field<string>("Name") ?? "");

    internal static string CategoryFor(string purpose, string name)
    {
        if (purpose == "FeeReminder" ||
            new[] { "fee", "payment", "receipt", "bill", "outstanding", "dues" }
                .Any(term => name.Contains(term, StringComparison.OrdinalIgnoreCase)))
            return "Fees & Payments";
        if (new[] { "exam", "result", "test", "assessment" }
                .Any(term => name.Contains(term, StringComparison.OrdinalIgnoreCase)))
            return "Examinations & Results";
        if (new[] { "admission", "student", "transfer", "leaving", "certificate" }
                .Any(term => name.Contains(term, StringComparison.OrdinalIgnoreCase)))
            return "Admissions & Students";
        return purpose == "Staff" ? "Staff & Other" : "Announcements & Greetings";
    }

    private void ApplySearch(DataTable rows)
    {
        string[] terms = search.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        string selectedCategory = category.SelectedItem as string ?? "All Categories";
        UiHelper.SetGridEmptyMessage(templates, terms.Length == 0 && selectedCategory == "All Categories"
            ? "No templates available." : "No templates match your search or category.");
        var matches = rows.AsEnumerable().Where(row =>
        {
            if (selectedCategory != "All Categories" &&
                row.Field<string>("DisplayCategory") != selectedCategory)
                return false;
            string searchableText = string.Join(" ", row.Field<string>("Name"), ChannelLabel(row),
                CommunicationTemplateDialog.PurposeLabel(row.Field<string>("Purpose") ?? ""), row.Field<string>("Body"));
            return terms.All(term => searchableText.Contains(term, StringComparison.CurrentCultureIgnoreCase));
        }).Select(row => row.Field<int>("TemplateId").ToString(CultureInfo.InvariantCulture)).ToArray();
        // Only numeric IDs enter the expression, so user input is never interpreted as RowFilter syntax.
        rows.DefaultView.RowFilter = matches.Length == 0 ? "1 = 0" : $"TemplateId IN ({string.Join(",", matches)})";
        templates.Invalidate();
    }

    private async Task RefreshAsync()
    {
        var rows = await read();
        foreach (var (item, index) in CommunicationPreparedMessages.BuiltIns.Select((item, index) => (item, index)))
        {
            var row = rows.NewRow();
            row["TemplateId"] = -index - 1;
            row["Name"] = item.Name + " (built-in)";
            row["Channel"] = item.Channel ?? "All";
            row["Purpose"] = item.Purpose;
            row["Body"] = item.Body;
            row["Active"] = true;
            rows.Rows.InsertAt(row, index);
        }
        // Expose the resolved presentation category as metadata, so grid
        // filtering and behavioral UI tests share one classification source.
        if (!rows.Columns.Contains("DisplayCategory"))
            rows.Columns.Add("DisplayCategory",typeof(string));
        foreach (DataRow row in rows.Rows)
            row["DisplayCategory"]=TemplateCategory(row);
        var old = templates.DataSource as DataTable;
        ApplySearch(rows);
        templates.DataSource = rows;
        old?.Dispose();
        add.Enabled = true;
        ShowSelected();
    }

    private async Task EditAsync(DataRow? row)
    {
        int id = row?.Field<int>("TemplateId") ?? 0;
        string name = row?.Field<string>("Name") ?? "";
        using var dialog = new CommunicationTemplateDialog(
            id < 0 ? "Custom " + name.Replace(" (built-in)", "") : name,
            (id < 0 && row?.Field<string>("Channel") == "All" ? "WhatsApp" : row?.Field<string>("Channel")) ?? "WhatsApp",
            row?.Field<string>("Purpose") ?? "Announcement",
            row?.Field<string>("Body") ?? "", row?.Field<bool>("Active") ?? true);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await CommunicationManagementStore.SaveTemplateAsync(Math.Max(0, id), dialog.TemplateName,
            dialog.Channel, dialog.Purpose, dialog.Body, dialog.Active);
        Changed = true;
        await RefreshAsync();
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (busy || !CurrentUser.HasPermission("Communication.Configure")) return;
        busy = true;
        layout.Enabled = false;
        feedback.Text = "";
        try { await action(); }
        catch (Exception ex) { if (!IsDisposed) feedback.Text = DbMessage.UserMessage(ex); }
        finally
        {
            busy = false;
            if (!IsDisposed)
            {
                layout.Enabled = true;
                ShowSelected();
            }
        }
    }
}
