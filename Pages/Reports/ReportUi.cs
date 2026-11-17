using FMSC_SMS.Core;
namespace FMSC_SMS.Pages.Reports;
internal static class ReportUi
{
    private sealed record ChoiceFilter(int DefaultIndex);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ModernComboBox, ChoiceFilter> ChoiceFilters = new();
    /// <summary>Opt an actual filter into counting/resetting. Report views and
    /// grouping choices stay intact unless explicitly registered as filters.</summary>
    public static void RegisterChoiceFilter(ModernComboBox choice, int defaultIndex = 0)
    {
        ChoiceFilters.Remove(choice);
        ChoiceFilters.Add(choice, new ChoiceFilter(defaultIndex));
    }
    /// <summary>Validate From/To order inline on the To picker.</summary>
    public static bool ValidDateRange(FormValidator validator, ModernDatePicker from, ModernDatePicker to)
    {
        validator.Begin();
        bool Valid() => (from.ShowCheckBox && !from.Checked) ||
            (to.ShowCheckBox && !to.Checked) || from.Value.Date <= to.Value.Date;
        bool ok = Valid();
        validator.Check(to, ok, "Earlier than From", Valid);
        if (!ok)
        {
            EventHandler? clearWhenFixed = null;
            clearWhenFixed = (_, _) => {
                if (Valid())
                {
                    validator.Clear(to);
                    from.ValueChanged -= clearWhenFixed;
                }
            };
            from.ValueChanged += clearWhenFixed;
        }
        return validator.Finish();
    }
    public static DataTable SectionFilterList() =>
        DatabaseHelper.QueryUser("GetSections");
    public static async Task<DataTable> SectionFilterListAsync() =>
        await DatabaseHelper.QueryUserAsync("GetSections");
    public static Panel Field(string caption, Control control)
    {
        var panel = new TableLayoutPanel {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0),
            Padding = new Padding(0, 0, 10, 6)
        };
        panel.RowStyles.Add(new RowStyle(
            SizeType.Absolute,
            Math.Max(16, AppMetrics.TextHeight(AppTheme.NormalFont))));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(new Label {
            Text = caption,
            Dock = DockStyle.Fill,
            Font = AppTheme.NormalFont,
            ForeColor = AppTheme.Text,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);
        HumanUi.ApplyFieldInputLayout(control);
        panel.Controls.Add(control, 0, 1);
        ReportFilterLayout.ConstrainField(panel, control);
        return panel;
    }
    internal static Panel FormField(string caption, Control control, bool required = false)
    {
        var field = HumanUi.Field(caption, control, required);
        ReportFilterLayout.ConstrainField((TableLayoutPanel)field, control);
        return field;
    }
    public static TableLayoutPanel Bar(params (string Caption, Control Control)[] fields)
        => Bar(int.MaxValue, fields);
    internal static TableLayoutPanel Bar(int maximumColumns, params (string Caption, Control Control)[] fields)
    {
        int columns = Math.Max(1, fields.Length);
        int rowHeight =
            Math.Max(16, AppMetrics.TextHeight(AppTheme.NormalFont)) +
            AppMetrics.InputControlHeight + 6;
        var table = new TableLayoutPanel {
            Dock = DockStyle.Top,
            Height = rowHeight + 8,
            ColumnCount = columns,
            RowCount = 1,
            Padding = new Padding(0, 4, 0, 4)
        };
        for (int i = 0; i < columns; i++)
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / columns));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, rowHeight));
        for (int i = 0; i < fields.Length; i++)
        {
            WorkspaceState.Remember(fields[i].Control, $"ReportFilter:{i}:{fields[i].Caption}");
            var fieldControl = Field(fields[i].Caption, fields[i].Control);
            fieldControl.Margin = new Padding(0);
            table.Controls.Add(fieldControl, i, 0);
        }
        ReportFilterLayout.Fit(table, rowHeight, maximumColumns);
        return table;
    }
    public static void BindSimple(LookupBox box, DataTable table, string id, string text)
    {
        box.Bind(table, id, text);
    }
    internal static Panel CenteredBar(params (string Caption, Control Control)[] fields)
    {
        var bar = Bar(fields);
        bar.Dock = DockStyle.None;
        bar.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        var host = new Panel { Dock = DockStyle.Top, Height = bar.Height, Margin = Padding.Empty };
        host.Controls.Add(bar);
        // Match the normal input width and keep captions with their controls.
        int fieldWidth = Math.Max(AppScale.Scale(150), fields.Max(field => field.Control.MaximumSize.Width));
        int preferredWidth = fields.Length * (fieldWidth + 10);
        bool arranging = false;
        void Arrange()
        {
            if (arranging || host.IsDisposed) return;
            arranging = true;
            try
            {
                bar.Width = Math.Min(preferredWidth, host.ClientSize.Width);
                bar.Left = Math.Max(0, (host.ClientSize.Width - bar.Width) / 2);
                host.Height = bar.Height;
            }
            finally { arranging = false; }
        }
        host.SizeChanged += (_, _) => Arrange();
        bar.SizeChanged += (_, _) => Arrange();
        Arrange();
        return host;
    }
    /// <summary>The vertical chrome a filter card adds around its content - padding, the card header and the gaps.</summary>
    public static int FilterCardChromeHeight =>
        10 + 10 + AppMetrics.CardHeaderHeight() + 6 + 10;
    public static Control Layout(Control filters, DataGridView grid, string resultTitle = "Report Results", params Control[] actionButtons)
        => BuildLayout(filters, grid, resultTitle, true, actionButtons);
    public static Control CompactLayout(Control filters, DataGridView grid, string resultTitle, params Control[] actionButtons)
        => BuildLayout(filters, grid, resultTitle, true, actionButtons);
    public static Control CompactLayout(Control filters, DataGridView grid, string resultTitle, Action filtersCleared, params Control[] actionButtons)
        => BuildLayout(filters, grid, resultTitle, true, actionButtons, filtersCleared);
    // Keeps dense register filters on one compact band; only Run/Reset belong beneath filters.
    // Print, Export and Columns are actions on the results, not additional filter controls.
    internal static Control CompactRegisterLayout(Control filters, DataGridView grid, Action filtersCleared,
        Control run, params Control[] resultActions)
        => BuildLayout(filters, grid, "Certificate records", true, new[] { run }, filtersCleared,
            filterTitle: "Filters", filterGuidance: "", resultGuidance: "",
            resultActions: resultActions, showHeaderClear: false);
    internal static Control WorkflowLayout(Control filters, DataGridView grid, string filterTitle, string filterGuidance,
        string resultTitle, string resultGuidance, params Control[] actionButtons)
        => BuildLayout(filters, grid, resultTitle, true, actionButtons, filterTitle: filterTitle,
            filterGuidance: filterGuidance, resultGuidance: resultGuidance, includeReset: false);
    internal static Action CaptureFilterDefaults(Control filters)
    {
        var restore = new List<Action>();
        foreach (Control control in Descendants(filters))
        {
            switch (control)
            {
                case LookupBox lookup:
                    restore.Add(() => lookup.SelectedIndex = lookup.Items.Count > 0 ? 0 : -1);
                    break;
                case ModernComboBox choice:
                    int index = ChoiceFilters.TryGetValue(choice, out var filter) ? filter.DefaultIndex : Math.Max(0, choice.SelectedIndex);
                    restore.Add(() => choice.SelectedIndex = index < choice.Items.Count ? index : -1);
                    break;
                case ModernDatePicker date:
                    DateTime value = date.Value; bool selected = date.Checked;
                    restore.Add(() => { date.Value = value; if (date.ShowCheckBox) date.Checked = selected; });
                    break;
                case ModernTextBox text:
                    string original = text.Text;
                    restore.Add(() => text.Text = original);
                    break;
                case CheckBox check:
                    bool originalCheck = check.Checked;
                    restore.Add(() => check.Checked = originalCheck);
                    break;
            }
        }
        return () => { foreach (var reset in restore) reset(); };
    }
    private static Control BuildLayout(Control filters, DataGridView grid, string resultTitle, bool compact, Control[] actionButtons, Action? filtersCleared = null,
        string filterTitle = "Report Filters", string filterGuidance = "Choose the scope below, then run the report. All and unchecked optional dates include every value.",
        string resultGuidance = "Review the generated report below.", bool includeReset = true,
        Control[]? resultActions = null, bool showHeaderClear = true)
    {
        var clear = new ModernButton {
            Text = "Clear Filters",
            Image = UiHelper.CreateGlyphIcon("↺", AppTheme.MutedText, 16),
            ImageAlign = ContentAlignment.MiddleLeft,
            TextImageRelation = TextImageRelation.ImageBeforeText,
            Width = AppScale.Scale(128, AppScale.ControlSizeScale),
            Height = AppMetrics.ButtonHeight,
            ThemeRole = "Outline"
        };
        clear.SetOutlineColors(AppTheme.Border, AppTheme.Text, AppTheme.Surface);
        var active = new Label {
            AutoSize = true,
            Font = AppTheme.NormalFont,
            ForeColor = AppTheme.MutedText,
            TextAlign = ContentAlignment.MiddleRight,
            Margin = new Padding(8, 0, 0, 0)
        };
        var accessory = new FlowLayoutPanel {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0),
            Padding = new Padding(0),
            BackColor = Color.Transparent
        };
        accessory.Controls.Add(active);
        if (showHeaderClear) accessory.Controls.Add(clear);
        var filterCard = HumanUi.Card(filterTitle, filterGuidance, accessory: accessory);
        filterCard.Content.Controls.Add(filters);
        /* each date picker's value as the page built it. Captured here, at construction, because this is the one moment those defaults are on screen. */
        var dateBaselines = Descendants(filters)
            .OfType<ModernDatePicker>()
            .ToDictionary(picker => picker, picker => picker.Value.Date);
        void RefreshFilterState()
        {
            int count = 0;
            foreach (Control control in Descendants(filters))
            {
                if (!control.Enabled) continue;
                if (control is LookupBox lookup && lookup.SelectedId > 0)
                    count++;
                else if (control is ModernComboBox choice && ChoiceFilters.TryGetValue(choice, out var filter) &&
                         choice.SelectedIndex >= 0 && choice.SelectedIndex != filter.DefaultIndex)
                    count++;
                else if (control is ModernNumericTextBox number)
                {
                    if (!string.IsNullOrWhiteSpace(number.Text) && (!decimal.TryParse(number.Text,out decimal value) || value != 0)) count++;
                }
                else if (control is ModernTextBox textBox && !string.IsNullOrWhiteSpace(textBox.Text))
                    count++;
                else if (control is CheckBox checkBox && checkBox.Checked)
                    count++;
                else if (control is ModernDatePicker picker &&
                         (picker.ShowCheckBox ? picker.Checked :
                         dateBaselines.TryGetValue(picker, out var baseline) && picker.Value.Date != baseline))
                {
                    /* The counter used to ignore the date range entirely, so "No optional filters" could sit above a report that was still scoped to last month. */
                    count++;
                }
            }
            active.Text = count == 0 ? "No optional filters" : $"{count} filter{(count == 1 ? "" : "s")} active";
        }
        clear.Click += (_, _) => {
            foreach (Control control in Descendants(filters))
            {
                if (control is LookupBox lookup)
                    lookup.SelectedIndex = 0;
                else if (control is ModernComboBox choice && ChoiceFilters.TryGetValue(choice, out var filter))
                    choice.SelectedIndex = filter.DefaultIndex < choice.Items.Count ? filter.DefaultIndex : -1;
                else if (control is ModernTextBox textBox)
                    textBox.Text = string.Empty;
                else if (control is CheckBox checkBox)
                    checkBox.Checked = false;
                /* The date range goes back to the range the page opened with. */
                else if (control is ModernDatePicker picker &&
                         dateBaselines.TryGetValue(picker, out var baseline))
                {
                    picker.Value = baseline;
                    if (picker.ShowCheckBox) picker.Checked = false;
                }
            }
            RefreshFilterState();
            filtersCleared?.Invoke();
        };
        foreach (Control control in Descendants(filters))
        {
            control.EnabledChanged += (_, _) => RefreshFilterState();
            if (control is LookupBox lookup)
                lookup.SelectedIndexChanged += (_, _) => RefreshFilterState();
            else if (control is ModernComboBox choice && ChoiceFilters.TryGetValue(choice, out _))
                choice.SelectedIndexChanged += (_, _) => RefreshFilterState();
            else if (control is ModernTextBox textBox)
                textBox.TextChanged += (_, _) => RefreshFilterState();
            else if (control is CheckBox checkBox)
                checkBox.CheckedChanged += (_, _) => RefreshFilterState();
            else if (control is ModernDatePicker picker)
                picker.ValueChanged += (_, _) => RefreshFilterState();
        }
        RefreshFilterState();
        if (includeReset && !actionButtons.Any(button => button.Text == "Reset Filters"))
        {
            var resetDefaults = CaptureFilterDefaults(filters);
            var reset = new ModernButton { Text = "Reset Filters", ThemeRole = "Secondary" };
            reset.Click += (_, _) => {
                resetDefaults();
                grid.DataSource = null;
                UiHelper.SetGridPlaceholderMessage(grid, UiText.EmptyGrid.RunReport);
                UiHelper.SetGridEmptyMessage(grid, UiText.EmptyGrid.RunReport);
                filtersCleared?.Invoke();
                // The bar wraps this layout, so find it within the owning report page.
                for (Control? owner = filters.Parent; owner is not null; owner = owner.Parent)
                    if (owner is V2Page)
                    {
                        foreach (var bar in Descendants(owner).OfType<ReportSavedViewsBar>()) bar.ResetSelection();
                        break;
                    }
                RefreshFilterState();
            };
            actionButtons = actionButtons.Take(1).Concat(new Control[] { reset }).Concat(actionButtons.Skip(1)).ToArray();
        }
        int buttonRowHeight = 0;
        TableLayoutPanel? compactBody = null;
        TableLayoutPanel? compactButtons = null;
        if (compact)
        {
            filterCard.Content.Controls.Remove(filters);
            compactBody = new TableLayoutPanel {
                Dock = DockStyle.Top, AutoSize = false,
                ColumnCount = 1, RowCount = actionButtons.Length > 0 ? 2 : 1,
                Margin = Padding.Empty, Padding = Padding.Empty
            };
            compactBody.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            compactBody.RowStyles.Add(new RowStyle(SizeType.Absolute, filters.Height));
            filters.Margin = Padding.Empty;
            compactBody.Controls.Add(filters, 0, 0);
            filterCard.Content.Controls.Add(compactBody);
        }
        if (actionButtons.Length > 0)
        {
            var buttonRow = HumanUi.ButtonRow(actionButtons);
            buttonRow.Dock = DockStyle.Top;
            if (compactBody is not null)
            {
                compactButtons = buttonRow;
                buttonRow.AutoSize = false;
                buttonRow.Dock = DockStyle.Fill;
                buttonRow.Margin = new Padding(0, UiDesignSystem.Spacing.Xs, 0, 0);
                compactBody.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.ButtonRowHeight));
                compactBody.Controls.Add(buttonRow, 0, 1);
            }
            else filterCard.Content.Controls.Add(buttonRow);
            buttonRowHeight = AppMetrics.ButtonRowHeight;
        }
        FlowLayoutPanel? resultTools = null;
        if (resultActions is { Length: > 0 })
        {
            resultTools = new FlowLayoutPanel {
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
                Margin = Padding.Empty, Padding = Padding.Empty
            };
            foreach (Control action in resultActions)
            {
                action.Margin = new Padding(UiDesignSystem.Spacing.Xs, 0, UiDesignSystem.Spacing.Xs, 0);
                resultTools.Controls.Add(action);
            }
        }
        var resultCard = HumanUi.Card(resultTitle, resultGuidance, accessory: resultTools);
        if (resultTools is not null)
        {
            // WinForms docks in reverse z-order. Let the right-hand buttons claim the
            // full header height before the top-docked title consumes any space.
            // This is the same header accessory ordering used by other FMSC pages.
            resultCard.Header.Controls.SetChildIndex(resultTools, resultCard.Header.Controls.Count - 1);
        }
        resultCard.Content.Controls.Add(grid);
        var root = new TableLayoutPanel {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(0),
            Margin = new Padding(0)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, filters.Height + FilterCardChromeHeight + buttonRowHeight));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(filterCard, 0, 0);
        root.Controls.Add(resultCard, 0, 1);
        if (compactBody is not null)
        {
            bool fitting = false;
            void FitFilterCard()
            {
                if (fitting || root.IsDisposed) return;
                fitting = true;
                try
                {
                    // Calculate wrapping from the buttons themselves. A flow panel's
                    // preferred height can retain a narrow startup width constraint.
                    int bodyHeight = filters.Height;
                    compactBody.RowStyles[0].Height = bodyHeight;
                    if (compactButtons is not null)
                    {
                        var flow = compactButtons.Controls.OfType<FlowLayoutPanel>().Single();
                        // The inner panel can still have its startup width while the
                        // table is laying out. Use the owning row's available width
                        // so that startup wrapping does not leave a tall empty card.
                        int width = Math.Max(1, root.ClientSize.Width - root.Padding.Horizontal -
                            filterCard.Margin.Horizontal - filterCard.Padding.Horizontal -
                            filterCard.Content.Padding.Horizontal - compactBody.Padding.Horizontal -
                            compactButtons.Margin.Horizontal - compactButtons.Padding.Horizontal);
                        int buttonsHeight = WrappedButtonHeight(flow, width, !root.Visible) +
                            flow.Margin.Vertical + compactButtons.Padding.Vertical;
                        compactBody.RowStyles[1].Height = buttonsHeight + compactButtons.Margin.Vertical;
                        bodyHeight += buttonsHeight + compactButtons.Margin.Vertical;
                    }
                    compactBody.Height = bodyHeight;
                    int height = filterCard.Padding.Vertical + filterCard.Header.Height +
                        filterCard.Content.Padding.Vertical + bodyHeight;
                    if (Math.Abs(root.RowStyles[0].Height - height) > 1)
                        root.RowStyles[0].Height = height;
                }
                finally { fitting = false; }
            }
            bool fitQueued = false;
            void QueueFit()
            {
                if (fitQueued || !root.IsHandleCreated || root.IsDisposed) return;
                fitQueued = true;
                root.BeginInvoke(new Action(() => {
                    fitQueued = false;
                    if (root.IsDisposed) return;
                    FitFilterCard();
                    int[] heights = root.GetRowHeights();
                    if (heights.Length > 0 && heights[0] != (int)root.RowStyles[0].Height)
                        root.PerformLayout();
                }));
            }
            compactBody.SizeChanged += (_, _) => QueueFit();
            compactBody.Layout += (_, _) => QueueFit();
            filters.SizeChanged += (_, _) => QueueFit();
            filterCard.Content.SizeChanged += (_, _) => QueueFit();
            filterCard.Header.SizeChanged += (_, _) => QueueFit();
            if (compactButtons is not null)
                compactButtons.Layout += (_, _) => QueueFit();
            root.SizeChanged += (_, _) => QueueFit();
            root.HandleCreated += (_, _) => QueueFit();
            root.VisibleChanged += (_, _) => QueueFit();
            FitFilterCard();
        }
        return root;
    }
    private static int WrappedButtonHeight(FlowLayoutPanel flow, int availableWidth, bool includeHidden)
    {
        int width = Math.Max(1, availableWidth - flow.Padding.Horizontal);
        int usedWidth = 0, rowHeight = 0, totalHeight = 0;
        foreach (Control button in flow.Controls)
        {
            if (!includeHidden && !button.Visible) continue;
            int buttonWidth = button.Width + button.Margin.Horizontal;
            int buttonHeight = button.Height + button.Margin.Vertical;
            if (usedWidth > 0 && usedWidth + buttonWidth > width)
            {
                totalHeight += rowHeight;
                usedWidth = rowHeight = 0;
            }
            usedWidth += buttonWidth;
            rowHeight = Math.Max(rowHeight, buttonHeight);
            if (flow.GetFlowBreak(button))
            {
                totalHeight += rowHeight;
                usedWidth = rowHeight = 0;
            }
        }
        return totalHeight + rowHeight + flow.Padding.Vertical;
    }
    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (Control descendant in Descendants(child))
                yield return descendant;
        }
    }
}
