using System.ComponentModel;
using FMSC_SMS.Core;

namespace FMSC_SMS.Pages.Communication;

/// <summary>
/// An in-memory, context-filtered template browser for ordinary senders.
/// Selecting a template only edits the draft; recipient review and explicit
/// confirmation remain mandatory.
 /// </summary>
internal sealed class CommunicationComposerTemplateBrowser : ApplicationWindowForm
{
    private sealed record Choice(CommunicationPreparedMessages.ComposeTemplate Template, string Category)
    {
        public string Name => Template.Name;
    }

    private readonly IReadOnlyList<Choice> choices;
    private readonly DataGridView templates = HumanUi.Grid(false);
    private readonly ModernTextBox search = HumanUi.TextBox(false, 150);
    private readonly ModernComboBox category = HumanUi.Combo();
    private readonly ModernTextBox preview = HumanUi.TextBox(true, 1600);
    private readonly ModernButton select = new() { Text = "Use This Template", ThemeRole = "Primary" };
    internal CommunicationPreparedMessages.ComposeTemplate? SelectedTemplate { get; private set; }

    internal CommunicationComposerTemplateBrowser(
        IEnumerable<CommunicationPreparedMessages.ComposeTemplate> compatible)
    {
        if (!CurrentUser.HasPermission("Communication.Send"))
            throw new UnauthorizedAccessException("Communication sending permission is required.");
        choices = compatible.Select(item => new Choice(item,
            CommunicationTemplatesDialog.CategoryFor(item.Purpose, item.Name)))
            .OrderBy(option => option.Category, StringComparer.CurrentCulture)
            .ThenBy(option => option.Name, StringComparer.CurrentCulture)
            .ToArray();
        Text = "Choose a Message Template";
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        ClientSize = new Size(AppScale.Scale(740), AppScale.Scale(550));
        MinimumSize = new Size(AppScale.Scale(600), AppScale.Scale(450));
        BackColor = AppTheme.PageBackground;

        search.Name = "communicationComposerTemplateSearch";
        search.PlaceholderText = "Search matching templates";
        category.Name = "communicationComposerTemplateCategory";
        category.Items.AddRange(new object[]
        {
            "All Categories", "Fees & Payments", "Admissions & Students",
            "Examinations & Results", "Announcements & Greetings", "Staff & Other"
        });
        category.SelectedIndex = 0;

        templates.Name = "communicationComposerTemplateResults";
        templates.AutoGenerateColumns = false;
        templates.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Name", HeaderText = "Template", DataPropertyName = "Name",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 190
        });
        templates.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Category", HeaderText = "Category", DataPropertyName = "Category",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 105
        });
        templates.CurrentCellChanged += (_, _) => UpdateSelection();
        templates.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) Accept(); };
        search.TextChanged += (_, _) => Filter();
        category.SelectedIndexChanged += (_, _) => Filter();

        preview.ReadOnly = true;
        preview.ScrollBars = ScrollBars.Vertical;
        var filters = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty
        };
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 63));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 37));
        filters.Controls.Add(HumanUi.Field("Find template", search), 0, 0);
        filters.Controls.Add(HumanUi.Field("Category", category), 1, 0);

        var close = new ModernButton { Text = "Cancel", DialogResult = DialogResult.Cancel };
        CancelButton = close;
        select.Enabled = false;
        select.Click += (_, _) => Accept();
        var buttons = HumanUi.DialogButtons(select, close);
        buttons.Dock = DockStyle.Fill;
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1,
            Padding = new Padding(AppScale.Scale(14))
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.FieldRowHeight));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 56));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.ButtonHeight + AppScale.Scale(12)));
        layout.Controls.Add(filters, 0, 0);
        layout.Controls.Add(templates, 0, 1);
        layout.Controls.Add(HumanUi.Field("Message draft preview", preview), 0, 2);
        layout.Controls.Add(buttons, 0, 3);
        Controls.Add(layout);
        Filter();
    }

    private void Filter()
    {
        if (templates is null) return;
        string[] terms = search.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        string group = category.SelectedItem as string ?? "All Categories";
        var shown = choices.Where(option =>
            (group == "All Categories" || option.Category == group) &&
            terms.All(term => (option.Name + " " + option.Category + " " + option.Template.Body)
                .Contains(term, StringComparison.CurrentCultureIgnoreCase))).ToArray();
        templates.DataSource = new BindingList<Choice>(shown);
        UiHelper.SetGridEmptyMessage(templates, "No matching templates for this audience and sending method.");
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        var row = templates.CurrentRow?.DataBoundItem as Choice;
        preview.Text = row?.Template.Body ?? "";
        select.Enabled = row is not null;
    }

    private void Accept()
    {
        if (templates.CurrentRow?.DataBoundItem is not Choice selected) return;
        SelectedTemplate = selected.Template;
        DialogResult = DialogResult.OK;
    }
}
