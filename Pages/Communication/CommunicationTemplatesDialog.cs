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
    private readonly ModernButton editUrdu = new() { Text = "Urdu Version…", Enabled = false };
    private readonly ModernButton languageSettings = new() { Text = "Default Language…", Enabled = false };
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
        var searchField = HumanUi.Field("Search templates", search);
        category.Name = "communicationTemplateCategory";
        category.Items.AddRange(new object[] { "All templates", "Fees & Payments", "Admissions & Students",
            "Announcements & Examinations", "Staff" });
        category.SelectedIndex = 0;
        var categoryField = HumanUi.Field("Category", category);
        var filters = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        searchField.Dock = categoryField.Dock = DockStyle.Fill;
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
        var buttons = HumanUi.DialogButtons(add, edit, editUrdu, languageSettings, close);
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
            if (templates.DataSource is DataTable rows) { ApplySearch(rows); ShowSelected(); }
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
        editUrdu.Click += async (_, _) =>
        {
            if (templates.CurrentRow?.DataBoundItem is DataRowView row && row.Row.Field<int>("TemplateId") > 0)
                await RunAsync(() => EditUrduAsync(row.Row));
        };
        languageSettings.Click += async (_, _) => await RunAsync(ChangeDefaultLanguageAsync);
        Shown += async (_, _) => await RunAsync(RefreshAsync);
        FormClosing += (_, e) => { if (busy) { e.Cancel = true; DialogResult = DialogResult.None; } };
        FormClosed += (_, _) => (templates.DataSource as DataTable)?.Dispose();
    }

    private void ShowSelected()
    {
        var row = (templates.CurrentRow?.DataBoundItem as DataRowView)?.Row;
        preview.Text = row?.Field<string>("Body") ?? "";
        edit.Enabled = !busy && row is not null;
        editUrdu.Enabled = !busy && row is not null && row.Field<int>("TemplateId") > 0;
        edit.Text = row?.Field<int>("TemplateId") > 0 ? EditTemplateText : CustomizeTemplateText;
    }

    private static string ChannelLabel(DataRow row) => row.Field<int>("TemplateId") < 0
        ? "WhatsApp / SMS"
        : row.Field<string>("Channel") is "Sms" ? "SMS" : row.Field<string>("Channel") ?? "";

    private static string CategoryForPurpose(string purpose) => purpose switch
    {
        "FeeReminder" => "Fees & Payments",
        "Individual" => "Admissions & Students",
        "Staff" => "Staff",
        _ => "Announcements & Examinations"
    };

    private void ApplySearch(DataTable rows)
    {
        string[] terms = search.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        UiHelper.SetGridEmptyMessage(templates, terms.Length == 0 ? "No templates available." : "No templates match your search.");
        string selected = category.Text;
        if (terms.Length == 0 && selected == "All templates") rows.DefaultView.RowFilter = "";
        else
        {
            var matches = rows.AsEnumerable().Where(row =>
            {
                string purpose = row.Field<string>("Purpose") ?? "";
                if (selected != "All templates" && CategoryForPurpose(purpose) != selected) return false;
                string searchableText = string.Join(" ", row.Field<string>("Name"), ChannelLabel(row),
                    CommunicationTemplateDialog.PurposeLabel(purpose), row.Field<string>("Body"));
                return terms.All(term => searchableText.Contains(term, StringComparison.CurrentCultureIgnoreCase));
            }).Select(row => row.Field<int>("TemplateId").ToString(CultureInfo.InvariantCulture)).ToArray();
            // Only numeric IDs enter the expression, so punctuation in the search is matched literally.
            rows.DefaultView.RowFilter = matches.Length == 0 ? "1 = 0" : $"TemplateId IN ({string.Join(",", matches)})";
        }
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
            row["Channel"] = "WhatsApp";
            row["Purpose"] = item.Purpose;
            row["Body"] = item.Body;
            row["Active"] = true;
            rows.Rows.InsertAt(row, index);
        }
        var old = templates.DataSource as DataTable;
        ApplySearch(rows);
        templates.DataSource = rows;
        old?.Dispose();
        add.Enabled = true;
        languageSettings.Enabled = true;
        ShowSelected();
    }

    private async Task EditAsync(DataRow? row)
    {
        int id = row?.Field<int>("TemplateId") ?? 0;
        string name = row?.Field<string>("Name") ?? "";
        using var dialog = new CommunicationTemplateDialog(
            id < 0 ? "Custom " + name.Replace(" (built-in)", "") : name,
            row?.Field<string>("Channel") ?? "WhatsApp", row?.Field<string>("Purpose") ?? "Announcement",
            row?.Field<string>("Body") ?? "", row?.Field<bool>("Active") ?? true);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await CommunicationManagementStore.SaveTemplateAsync(Math.Max(0, id), dialog.TemplateName,
            dialog.Channel, dialog.Purpose, dialog.Body, dialog.Active);
        Changed = true;
        await RefreshAsync();
    }

    private async Task EditUrduAsync(DataRow row)
    {
        int id = row.Field<int>("TemplateId");
        if (id <= 0) return;
        using var window = new ApplicationWindowForm
        {
            Text = "Urdu Message — " + Convert.ToString(row["Name"]),
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(AppScale.Scale(610), AppScale.Scale(360)),
            MinimumSize = new Size(AppScale.Scale(480), AppScale.Scale(300)),
            ShowInTaskbar = false, Padding = new Padding(AppScale.Scale(14))
        };
        var body = HumanUi.TextBox(true, 1600);
        body.Multiline = true;
        body.ScrollBars = ScrollBars.Vertical;
        body.RightToLeft = RightToLeft.Yes;
        body.Text = row.Table.Columns.Contains("BodyUrdu") ? Convert.ToString(row["BodyUrdu"]) ?? "" : "";
        var field = HumanUi.Field("Urdu version (leave empty to remove)", body);
        field.Dock = DockStyle.Fill;
        var save = new ModernButton { Text = "Save Urdu", ThemeRole = "Primary" };
        var cancel = new ModernButton { Text = "Cancel", DialogResult = DialogResult.Cancel };
        save.Click += (_, _) =>
        {
            try
            {
                if (body.Text.Trim().Length > 0) CommunicationPreparedMessages.ValidateFormat(body.Text);
                window.DialogResult = DialogResult.OK;
            }
            catch (CommunicationValidationException ex) { ModernMessageDialog.ShowWarning(window,ex.Message); }
        };
        var actions = HumanUi.DialogButtons(save,cancel);
        actions.Dock = DockStyle.Bottom;
        window.Controls.Add(field);
        window.Controls.Add(actions);
        window.CancelButton = cancel;
        if (window.ShowDialog(this) != DialogResult.OK) return;
        await CommunicationManagementStore.SaveUrduTemplateAsync(id,
            body.Text.Trim().Length == 0 ? null : body.Text);
        Changed = true;
        await RefreshAsync();
    }

    private async Task ChangeDefaultLanguageAsync()
    {
        string current = await CommunicationManagementStore.ReadDefaultTemplateLanguageAsync();
        using var window = new ApplicationWindowForm
        {
            Text = "Default Message Language", StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(AppScale.Scale(430), AppScale.Scale(155)),
            ShowInTaskbar = false, Padding = new Padding(AppScale.Scale(14)),
            FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false
        };
        var choice = HumanUi.Combo();
        choice.Items.AddRange(new object[] { "English", "Urdu" });
        choice.SelectedIndex = current == "ur" ? 1 : 0;
        var field = HumanUi.Field("Default language for new messages", choice);
        field.Dock = DockStyle.Top; field.Height = AppMetrics.FieldRowHeight;
        var save = new ModernButton { Text = "Save", DialogResult = DialogResult.OK, ThemeRole = "Primary" };
        var cancel = new ModernButton { Text = "Cancel", DialogResult = DialogResult.Cancel };
        var actions = HumanUi.DialogButtons(save,cancel);
        actions.Dock = DockStyle.Bottom;
        window.Controls.Add(field);
        window.Controls.Add(actions);
        window.CancelButton = cancel;
        if (window.ShowDialog(this) != DialogResult.OK) return;
        await CommunicationManagementStore.SetDefaultTemplateLanguageAsync(
            choice.SelectedIndex == 1 ? "ur" : "en");
        Changed = true;
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
