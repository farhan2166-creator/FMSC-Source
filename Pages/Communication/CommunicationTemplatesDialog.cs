using System.Data;
using FMSC_SMS.Core;

namespace FMSC_SMS.Pages.Communication;

internal sealed class CommunicationTemplatesDialog : ApplicationWindowForm
{
    private readonly DataGridView templates = HumanUi.Grid(false);
    private readonly ModernTextBox preview = HumanUi.TextBox(true, 1600);
    private readonly ModernButton edit = new() { Text = "Edit Template…", Enabled = false };
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
            else if (e.Value is "Sms" && column == "Channel") e.Value = "SMS";
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
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(AppScale.Scale(14))
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(30)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.ButtonHeight + AppScale.Scale(10)));
        layout.Controls.Add(templates, 0, 0);
        layout.Controls.Add(HumanUi.Field("Template preview", preview), 0, 1);
        layout.Controls.Add(feedback, 0, 2);
        layout.Controls.Add(buttons, 0, 3);
        Controls.Add(layout);
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
        edit.Text = row?.Field<int>("TemplateId") < 0 ? "Customize Template…" : "Edit Template…";
        AppMetrics.FitButtonWidth(edit, minWidth: AppScale.Scale(145));
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
            row?.Field<string>("Channel") ?? "WhatsApp", row?.Field<string>("Purpose") ?? "Announcement",
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
