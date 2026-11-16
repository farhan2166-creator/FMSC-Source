using System.Data;
using FMSC_SMS.Core;

namespace FMSC_SMS.Pages.Communication;

/// <summary>
/// One administrative entry point. Operational defaults are explicit; no automation is
/// activated by constructing or opening this form. Background worker enrollment has
/// a separate, auditable activation gate before any non-Off rule can be persisted.
/// </summary>
internal sealed class CommunicationSettingsDialog : ApplicationWindowForm
{
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    private readonly ModernButton manageProviders = new() { Text = "Manage Sending Methods…", ThemeRole = "Secondary" };
    private readonly ModernComboBox preferred = HumanUi.Combo();
    private readonly ModernButton savePreferred = new() { Text = "Save Preferred Method", ThemeRole = "Primary" };
    private readonly ModernButton savePolicy = new() { Text = "Save Sending Rules", ThemeRole = "Primary" };
    private readonly ModernButton enableWorker = new() { Text = "Enable Background Sending", ThemeRole = "Secondary" };
    private readonly ModernButton disableWorker = new() { Text = "Disable Background Sending", ThemeRole = "Secondary" };
    private readonly ModernButton saveRule = new() { Text = "Save Selected Rule", ThemeRole = "Primary" };
    private readonly ModernButton close = new() { Text = "Close", DialogResult = DialogResult.Cancel };
    private readonly Label feedback = new() { Dock = DockStyle.Fill, AutoEllipsis = true };
    private readonly NumericUpDown rate = Number(60, 1, 600);
    private readonly NumericUpDown smsLimit = Number(0, 0, 100000);
    private readonly NumericUpDown whatsappLimit = Number(0, 0, 100000);
    private readonly NumericUpDown dailyFamily = Number(3, 1, 20);
    private readonly NumericUpDown priorityReserve = Number(10, 0, 50);
    private readonly DateTimePicker from = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Width = AppScale.Scale(120) };
    private readonly DateTimePicker until = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Width = AppScale.Scale(120) };
    private readonly CheckBox[] days = Enumerable.Range(0, 7).Select(i => new CheckBox {
        Text = new[] { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" }[i],
        AutoSize = true, Checked = true, Margin = new Padding(AppScale.Scale(5))
    }).ToArray();
    private readonly DataGridView rules = HumanUi.Grid(false);
    private readonly ModernComboBox selectedMode = HumanUi.Combo();
    private readonly Label selectedEvent = new() { AutoSize = true };
    private bool busy, changed;
    internal bool Changed => changed;

    private static NumericUpDown Number(decimal initial, decimal min, decimal max) =>
        new() { Minimum = min, Maximum = max, Value = initial, Width = AppScale.Scale(140),
            ThousandsSeparator = true, TextAlign = HorizontalAlignment.Right };

    internal CommunicationSettingsDialog()
    {
        if (!CurrentUser.HasPermission("Communication.Configure"))
            throw new UnauthorizedAccessException("Communication administrator permission is required.");
        Text = "Communication Settings";
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimumSize = new Size(AppScale.Scale(630), AppScale.Scale(490));
        ClientSize = new Size(AppScale.Scale(815), AppScale.Scale(620));
        BackColor = AppTheme.PageBackground;
        Padding = new Padding(AppScale.Scale(14));

        var sending = new TabPage("Sending") { Padding = new Padding(AppScale.Scale(16)) };
        var queue = new TabPage("Queue & Schedule") { Padding = new Padding(AppScale.Scale(16)) };
        var automation = new TabPage("Automation") { Padding = new Padding(AppScale.Scale(16)) };
        tabs.TabPages.AddRange(new[] { sending, queue, automation });
        BuildSending(sending);
        BuildQueue(queue);
        BuildAutomation(automation);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(34)));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.ButtonHeight + AppScale.Scale(8)));
        root.Controls.Add(tabs, 0, 0);
        feedback.ForeColor = AppTheme.MutedText;
        root.Controls.Add(feedback, 0, 1);
        var footer = new Panel { Dock = DockStyle.Fill };
        close.Dock = DockStyle.Right; close.Width = AppScale.Scale(120);
        footer.Controls.Add(close);
        root.Controls.Add(footer, 0, 2);
        Controls.Add(root);
        CancelButton = close;
        FormClosing += (_, e) => e.Cancel |= busy;
        Shown += async (_, _) => await PerformAsync(LoadAsync);
        savePreferred.Click += async (_, _) => await PerformAsync(async () =>
        {
            string method = preferred.SelectedIndex switch { 1 => "MetaCloud", 2 => "Twilio", _ => "ManualWhatsApp" };
            await CommunicationManagementStore.SavePreferredMethodAsync(method);
            changed = true; feedback.Text = "Preferred sending method saved.";
        });
        manageProviders.Click += (_, _) =>
        {
            if (busy) return;
            using var dialog = new CommunicationSendingOptionsDialog();
            dialog.ShowDialog(this);
            changed |= dialog.Changed;
        };
        savePolicy.Click += async (_, _) => await PerformAsync(SavePolicyAsync);
        enableWorker.Click += async (_, _) => await PerformAsync(async () =>
        {
            if (ModernMessageDialog.Show(this,
                "Authorize this Windows account to send previously reviewed and scheduled API messages while FMSC is closed? Sending limits and permissions still apply.",
                "Enable Background Sending", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            await FMSC_SMS.Services.Communication.CommunicationWorkerEnrollment.EnableAsync();
            feedback.Text = "Background sending enrolled for this Windows account.";
            changed = true;
        });
        disableWorker.Click += async (_, _) => await PerformAsync(async () =>
        {
            if (ModernMessageDialog.Show(this,
                "Disable all school background communication processors? Unsent messages remain in Queue.",
                "Disable Background Sending", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            await FMSC_SMS.Services.Communication.CommunicationWorkerEnrollment.DisableAsync();
            feedback.Text = "Background sending disabled.";
            changed = true;
        });
        saveRule.Click += async (_, _) => await PerformAsync(SaveSelectedRuleAsync);
        rules.SelectionChanged += (_, _) => ShowSelectedRule();
    }

    private void BuildSending(TabPage page)
    {
        var panel = new FlowLayoutPanel {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AutoScroll = true, Padding = new Padding(AppScale.Scale(12))
        };
        panel.Controls.Add(new Label {
            AutoSize = true, Text = "Sending methods",
            Font = AppTheme.HeadingFont, Margin = new Padding(0, 0, 0, AppScale.Scale(12))
        });
        panel.Controls.Add(new Label {
            AutoSize = true, MaximumSize = new Size(AppScale.Scale(680), 0),
            Text = "Configure free WhatsApp Business, WhatsApp API, or SMS. Provider-specific setup and test verification stay in the existing sending dialog.",
            Margin = new Padding(0, 0, 0, AppScale.Scale(18))
        });
        manageProviders.Width = AppScale.Scale(245);
        preferred.Items.AddRange(new object[] { "WhatsApp Business (free manual)", "WhatsApp API", "SMS API" });
        preferred.SelectedIndex = 0;
        preferred.Width = AppScale.Scale(300);
        panel.Controls.Add(Heading("Preferred sending method"));
        panel.Controls.Add(preferred);
        savePreferred.Width = AppScale.Scale(210);
        panel.Controls.Add(savePreferred);
        panel.Controls.Add(manageProviders);
        page.Controls.Add(panel);
    }

    private void BuildQueue(TabPage page)
    {
        var panel = new FlowLayoutPanel {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AutoScroll = true, Padding = new Padding(AppScale.Scale(10))
        };
        panel.Controls.Add(Heading("Sending schedule"));
        panel.Controls.Add(Pair("Start sending at", from));
        panel.Controls.Add(Pair("Stop sending at", until));
        panel.Controls.Add(Heading("Permitted days"));
        var week = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true, WrapContents = true, Width = AppScale.Scale(650) };
        foreach (var day in days) week.Controls.Add(day);
        panel.Controls.Add(week);
        panel.Controls.Add(Heading("Daily limits and priorities"));
        panel.Controls.Add(Pair("Maximum messages / minute", rate));
        panel.Controls.Add(Pair("SMS API maximum / day (0 = disabled)", smsLimit));
        panel.Controls.Add(Pair("WhatsApp API maximum / day (0 = disabled)", whatsappLimit));
        panel.Controls.Add(Pair("Routine messages per family / day", dailyFamily));
        panel.Controls.Add(Pair("Reserved capacity for important messages (%)", priorityReserve));
        panel.Controls.Add(new Label {
            AutoSize = true, MaximumSize = new Size(AppScale.Scale(650), 0),
            Text = "Settings do not enable unattended sending. The authorized worker must be enrolled separately.",
            ForeColor = AppTheme.MutedText, Margin = new Padding(0, AppScale.Scale(8), 0, AppScale.Scale(8))
        });
        savePolicy.Width = AppScale.Scale(210);
        panel.Controls.Add(savePolicy);
        panel.Controls.Add(Heading("Background processor"));
        enableWorker.Width = AppScale.Scale(235);
        disableWorker.Width = AppScale.Scale(235);
        panel.Controls.Add(enableWorker);
        panel.Controls.Add(disableWorker);
        page.Controls.Add(panel);
    }

    private void BuildAutomation(TabPage page)
    {
        rules.AutoGenerateColumns = false;
        rules.Columns.Add(new DataGridViewTextBoxColumn {
            Name = "EventCode", DataPropertyName = "EventCode", HeaderText = "Event",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true });
        rules.Columns.Add(new DataGridViewTextBoxColumn {
            Name = "Mode", DataPropertyName = "Mode", HeaderText = "Current mode",
            Width = AppScale.Scale(175), ReadOnly = true });
        var choice = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        selectedEvent.ForeColor = AppTheme.Text;
        selectedEvent.Margin = new Padding(AppScale.Scale(8));
        selectedMode.Items.AddRange(new object[] { "Off", "Prepare for Review", "Send Automatically" });
        selectedMode.SelectedIndex = 0;
        selectedMode.Width = AppScale.Scale(190);
        choice.Controls.Add(selectedEvent);
        choice.Controls.Add(selectedMode);
        choice.Controls.Add(saveRule);
        saveRule.Width = AppScale.Scale(175);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(50)));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(55)));
        layout.Controls.Add(new Label {
            Text = "All events start Off. Select a rule to change its mode after background sending is authorized.",
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true
        }, 0, 0);
        layout.Controls.Add(rules, 0, 1);
        layout.Controls.Add(choice, 0, 2);
        page.Controls.Add(layout);
    }

    private static Label Heading(string text) =>
        new() { Text = text, AutoSize = true, Font = AppTheme.HeadingFont,
            Margin = new Padding(0, AppScale.Scale(12), 0, AppScale.Scale(9)) };

    private static FlowLayoutPanel Pair(string label, Control input)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, AppScale.Scale(3), 0, AppScale.Scale(3)) };
        row.Controls.Add(new Label { Text = label, Width = AppScale.Scale(390), Height = AppScale.Scale(31),
            TextAlign = ContentAlignment.MiddleLeft });
        row.Controls.Add(input);
        return row;
    }

    private async Task LoadAsync()
    {
        using var policy = await CommunicationManagementStore.ReadDispatchSettingsAsync();
        using var events = await CommunicationManagementStore.ReadAutomationRulesAsync();
        string preferredMethod = await CommunicationManagementStore.ReadPreferredMethodAsync();
        if (IsDisposed || policy.Rows.Count != 1) return;
        preferred.SelectedIndex = preferredMethod switch { "MetaCloud" => 1, "Twilio" => 2, _ => 0 };
        DataRow row = policy.Rows[0];
        from.Value = DateTime.Today.Add((TimeSpan)row["SendFrom"]);
        until.Value = DateTime.Today.Add((TimeSpan)row["SendUntil"]);
        int mask = Convert.ToInt32(row["WorkingDaysMask"]);
        for (int i = 0; i < days.Length; i++) days[i].Checked = (mask & (1 << i)) != 0;
        rate.Value = Convert.ToDecimal(row["MaximumPerMinute"]);
        smsLimit.Value = Convert.ToDecimal(row["MaximumSmsDaily"]);
        whatsappLimit.Value = Convert.ToDecimal(row["MaximumWhatsAppDaily"]);
        dailyFamily.Value = Convert.ToDecimal(row["MaximumRoutineFamilyDaily"]);
        priorityReserve.Value = Convert.ToDecimal(row["PriorityReservePercent"]);
        rules.DataSource = events.Copy();
        ShowSelectedRule();
    }

    private async Task SavePolicyAsync()
    {
        byte mask = 0;
        for (int i = 0; i < days.Length; i++) if (days[i].Checked) mask |= (byte)(1 << i);
        await CommunicationManagementStore.SaveDispatchSettingsAsync(from.Value.TimeOfDay, until.Value.TimeOfDay,
            mask,(int)rate.Value,(int)smsLimit.Value,(int)whatsappLimit.Value,
            (byte)dailyFamily.Value,(byte)priorityReserve.Value);
        changed = true;
        feedback.Text = "Sending schedule and daily limits saved.";
    }

    private void ShowSelectedRule()
    {
        DataRow? row = (rules.CurrentRow?.DataBoundItem as DataRowView)?.Row;
        selectedEvent.Text = row is null ? "Choose an event" : Convert.ToString(row["EventCode"]) ?? "Event";
        saveRule.Enabled = row is not null && !busy;
        selectedMode.SelectedIndex = row is null ? 0 : Convert.ToString(row["Mode"]) switch {
            "Review" => 1, "Automatic" => 2, _ => 0 };
    }

    private async Task SaveSelectedRuleAsync()
    {
        if (rules.CurrentRow?.DataBoundItem is not DataRowView selected) return;
        DataRow row = selected.Row;
        string code = Convert.ToString(row["EventCode"]) ?? "";
        string mode = selectedMode.SelectedIndex switch {
            1 => "Review", 2 => "Automatic", _ => "Off"
        };
        await CommunicationManagementStore.SaveAutomationRuleAsync(code,mode,
            Convert.ToInt32(row["CampusId"]),Convert.ToInt32(row["ClassId"]),
            row["Channel"] == DBNull.Value ? null : Convert.ToString(row["Channel"]));
        row["Mode"] = mode;
        changed = true;
        feedback.Text = "Automation mode saved for " + code + ".";
    }

    private async Task PerformAsync(Func<Task> action)
    {
        if (busy || !CurrentUser.HasPermission("Communication.Configure")) return;
        busy = true;
        tabs.Enabled = false;
        feedback.Text = "";
        try { await action(); }
        catch (Exception ex) { if (!IsDisposed) feedback.Text = DbMessage.UserMessage(ex); }
        finally
        {
            busy = false;
            if (!IsDisposed) { tabs.Enabled = true; ShowSelectedRule(); }
        }
    }
}
