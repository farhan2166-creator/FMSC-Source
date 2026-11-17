using System.Data;
using FMSC_SMS.Core;

namespace FMSC_SMS.Pages.Communication;

internal sealed class CommunicationSendingOptionsDialog : ApplicationWindowForm
{
    private readonly record struct Method(string Channel, string Provider, string Title);

    private sealed class MethodRow(Method method, Label status, ModernButton setup, ModernButton toggle)
    {
        internal Method Method { get; } = method;
        internal Label Status { get; } = status;
        internal ModernButton Setup { get; } = setup;
        internal ModernButton Toggle { get; } = toggle;
        internal bool Active { get; set; }
    }

    private readonly List<MethodRow> methods = new();
    private readonly ModernButton close = new() { Text = "Close" };
    private readonly ModernButton block = new() { Text = "Block a Number…", Enabled = false };
    private readonly ModernButton messageLanguage = new()
    {
        Name = "communicationLanguageSettings", Text = "Default Language…",
        ThemeRole = "Secondary"
    };
    private readonly Label feedback = new() { Dock = DockStyle.Fill, AutoEllipsis = true };
    private readonly Func<Task<DataTable>> read;
    private readonly bool useLivePreferences;
    private readonly ModernComboBox preferredMethod = HumanUi.Combo();
    private readonly ModernButton savePreferred = new()
    {
        Name="communicationSavePreferredMethod",Text="Save Default",ThemeRole="Secondary"
    };
    private readonly TableLayoutPanel layout;
    private readonly TabControl settingsTabs = new() { Dock = DockStyle.Fill };
    private readonly Label queueFeedback = new() { AutoSize = true, ForeColor = AppTheme.MutedText };
    private readonly Label automationFeedback = new() { AutoSize = true, ForeColor = AppTheme.MutedText };
    private readonly Label masterState = new() { AutoSize = true, ForeColor = AppTheme.Text };
    private readonly ModernButton masterToggle = new() { Name = "communicationMasterPause", ThemeRole = "Secondary", Text = "Pause All Sending" };
    private readonly ModernButton workerToggle = new() { Name = "communicationBackgroundToggle", ThemeRole = "Secondary", Text = "Enable Background Sending" };
    private readonly Label workerState = new() { AutoSize = true, ForeColor = AppTheme.MutedText };
    private readonly DateTimePicker startTime = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true };
    private readonly DateTimePicker endTime = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true };
    private readonly NumericUpDown speed = new() { Minimum = 1, Maximum = 600, Value = 60 };
    private readonly ModernComboBox quotaProvider = HumanUi.Combo();
    private readonly NumericUpDown dailyLimit = new() { Minimum = 1, Maximum = 1000000, Value = 500 };
    private readonly NumericUpDown importantReserve = new() { Minimum = 0, Maximum = 999999, Value = 50 };
    private readonly ModernComboBox automationEvent = HumanUi.Combo();
    private readonly ModernComboBox automationMode = HumanUi.Combo();
    private DataTable? ruleData;
    private DataTable? quotaData;
    private bool masterPaused, backgroundEnabled;
    private string schoolTimeZoneId = "";
    private bool busy;
    internal bool Changed { get; private set; }

    internal CommunicationSendingOptionsDialog(Func<Task<DataTable>>? read = null)
    {
        useLivePreferences = read is null;
        this.read = read ?? CommunicationManagementStore.ReadSettingsAsync;
        Text = "Communication Settings";
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ClientSize = new Size(AppScale.Scale(800), AppScale.Scale(610));
        BackColor = AppTheme.PageBackground;
        layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(AppScale.Scale(14))
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var method in new[]
        {
            new Method("WhatsApp", "WhatsAppBusinessManual", "WhatsApp Business · manual"),
            new Method("WhatsApp", "MetaCloud", "WhatsApp · Meta API"),
            new Method("Sms", "Twilio", "SMS · Twilio")
        })
        {
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / 3));
            layout.Controls.Add(BuildMethod(method), 0, methods.Count - 1);
        }
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(60)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(40)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.ButtonHeight + AppScale.Scale(8)));
        var preferenceRow = new TableLayoutPanel
        {
            Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=Padding.Empty
        };
        preferenceRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,75));
        preferenceRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,25));
        preferredMethod.Name="communicationPreferredMethod";
        preferredMethod.Items.AddRange(new object[]
        {
            "WhatsApp Business · manual", "WhatsApp · Meta API", "SMS · Twilio"
        });
        preferredMethod.SelectedIndex=0;
        savePreferred.Dock=DockStyle.Fill;
        savePreferred.Enabled=useLivePreferences;
        preferenceRow.Controls.Add(HumanUi.Field("Default sending method",preferredMethod),0,0);
        preferenceRow.Controls.Add(savePreferred,1,0);
        layout.Controls.Add(preferenceRow,0,3);
        layout.Controls.Add(feedback, 0, 4);
        var footer = new Panel { Dock = DockStyle.Fill };
        block.Dock = DockStyle.Left;
        block.Width = AppScale.Scale(175);
        close.Dock = DockStyle.Right;
        close.Width = AppScale.Scale(100);
        messageLanguage.Dock = DockStyle.Left;
        messageLanguage.Width = AppScale.Scale(180);
        messageLanguage.Enabled = useLivePreferences;
        footer.Controls.AddRange(new Control[] { block, messageLanguage, close });
        layout.Controls.Add(footer, 0, 5);
        var sendingTab = new TabPage("Sending") { BackColor = AppTheme.PageBackground };
        sendingTab.Controls.Add(layout);
        settingsTabs.TabPages.Add(sendingTab);
        settingsTabs.TabPages.Add(BuildQueueTab());
        settingsTabs.TabPages.Add(BuildAutomationTab());
        Controls.Add(settingsTabs);
        settingsTabs.SelectedIndexChanged += async (_, _) =>
        {
            if (busy) return;
            if (settingsTabs.SelectedIndex == 1)
                await RunAsync(RefreshQueueAsync);
            else if (settingsTabs.SelectedIndex == 2)
                await RunAsync(RefreshAutomationAsync);
        };
        close.DialogResult = DialogResult.Cancel;
        CancelButton = close;
        FormClosing += (_, e) => e.Cancel |= busy;
        Shown += async (_, _) => await RunAsync(RefreshAsync);
        block.Click += async (_, _) => await RunAsync(RecordOptOutAsync);
        messageLanguage.Click += (_, _) =>
        {
            if (busy || !CurrentUser.HasPermission("Communication.Configure")) return;
            using var dialog = new CommunicationLanguageSettingsDialog();
            dialog.ShowDialog(this);
            if (dialog.Changed) Changed = true;
        };
        savePreferred.Click += async (_, _) => await RunAsync(async () =>
        {
            var choice = preferredMethod.SelectedIndex switch
            {
                0 => ("WhatsApp","WhatsAppBusinessManual"),
                1 => ("WhatsApp","MetaCloud"),
                2 => ("Sms","Twilio"),
                _ => throw new InvalidOperationException("Select a supported sending method.")
            };
            await CommunicationQueuePolicyStore.SavePreferredMethodAsync(
                choice.Item1,choice.Item2);
            Changed=true;
            feedback.Text="Default sending method saved. Users may still change it for a message.";
        });
        FormClosed += (_, _) => { ruleData?.Dispose(); quotaData?.Dispose(); };
    }

    private static Control Labelled(string title, Control control)
    {
        var field = HumanUi.Field(title, control);
        field.Dock = DockStyle.Fill;
        return field;
    }

    private TabPage BuildQueueTab()
    {
        var page = new TabPage("Queue & Schedule") { BackColor = AppTheme.PageBackground };
        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 8,
            Padding = new Padding(AppScale.Scale(15)), Margin = Padding.Empty
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        for (int i = 0; i < 8; i++)
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(i == 0 ? 58 : 65)));

        var masterRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        masterToggle.Width = AppScale.Scale(190);
        masterToggle.Height = AppMetrics.ButtonHeight;
        masterRow.Controls.Add(masterToggle);
        masterRow.Controls.Add(masterState);
        content.Controls.Add(masterRow, 0, 0);
        content.SetColumnSpan(masterRow, 2);

        startTime.Value = DateTime.Today.AddHours(8);
        endTime.Value = DateTime.Today.AddHours(18);
        content.Controls.Add(Labelled("Sending starts", startTime), 0, 1);
        content.Controls.Add(Labelled("Sending ends", endTime), 1, 1);
        content.Controls.Add(Labelled("Maximum API sends per minute", speed), 0, 2);
        var hoursHint = new Label { Dock = DockStyle.Fill, Text = "Sending hours use this computer's local time until the school timezone is configured.", AutoSize = false, ForeColor = AppTheme.MutedText };
        content.Controls.Add(hoursHint, 1, 2);

        quotaProvider.Items.AddRange(new object[] { "SMS · Twilio", "WhatsApp · Meta API" });
        quotaProvider.SelectedIndex = 0;
        content.Controls.Add(Labelled("Provider daily allowance", quotaProvider), 0, 3);
        content.Controls.Add(Labelled("Maximum messages per day", dailyLimit), 1, 3);
        content.Controls.Add(Labelled("Reserve for important messages", importantReserve), 0, 4);
        var saveWindow = new ModernButton { Text = "Save Sending Hours", ThemeRole = "Secondary" };
        var saveQuota = new ModernButton { Text = "Save Daily Limit", ThemeRole = "Primary" };
        var calendarButton = new ModernButton
        {
            Name = "communicationSchoolCalendar", Text = "Working Days & Holidays",
            ThemeRole = "Secondary"
        };
        var takeover = new ModernButton
        {
            Name = "communicationAdminTakeover",
            Text = "Held Messages…", ThemeRole = "Secondary"
        };
        takeover.Enabled = new[] { "Communication.Configure", "Communication.Send",
            "Communication.View", "Students.View", "Staff.View", "Fees.View" }
            .All(CurrentUser.HasPermission);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true };
        foreach (var button in new[] { saveWindow, saveQuota, calendarButton, takeover })
        {
            button.Width = AppScale.Scale(button == calendarButton ? 200 : button == takeover ? 150 : 165);
            button.Height = AppMetrics.ButtonHeight;
            actions.Controls.Add(button);
        }
        takeover.Click += (_, _) =>
        {
            if (busy || !takeover.Enabled) return;
            using var dialog = new CommunicationTakeoverDialog();
            dialog.ShowDialog(this);
            if (dialog.Changed)
            {
                Changed = true;
                queueFeedback.Text = "Reviewed held batch transferred to the administrator. Sending remains subject to the live safety checks.";
            }
        };
        content.Controls.Add(actions, 0, 5);
        content.SetColumnSpan(actions, 2);
        var workerRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true };
        workerToggle.Width = AppScale.Scale(235);
        workerToggle.Height = AppMetrics.ButtonHeight;
        workerRow.Controls.Add(workerToggle);
        workerRow.Controls.Add(workerState);
        content.Controls.Add(workerRow, 0, 6);
        content.SetColumnSpan(workerRow, 2);
        content.Controls.Add(queueFeedback, 0, 7);
        content.SetColumnSpan(queueFeedback, 2);

        masterToggle.Click += async (_, _) => await RunAsync(async () =>
        {
            bool desired = !masterPaused;
            if (ModernMessageDialog.Show(this, desired
                ? "Pause new API sending attempts for the whole school?"
                : "Resume approved sending? All current consent and scheduling rules still apply.",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            await CommunicationQueuePolicyStore.SetMasterPauseAsync(desired);
            Changed = true;
            await RefreshQueueAsync();
        });
        saveWindow.Click += async (_, _) => await RunAsync(async () =>
        {
            await CommunicationQueuePolicyStore.SaveSendingWindowAsync(
                TimeOnly.FromDateTime(startTime.Value), TimeOnly.FromDateTime(endTime.Value),
                schoolTimeZoneId, Decimal.ToInt32(speed.Value), backgroundEnabled);
            Changed = true;
            queueFeedback.Text = "Sending hours saved. Background processing remains opt-in.";
        });
        saveQuota.Click += async (_, _) => await RunAsync(async () =>
        {
            var (channel, provider) = quotaProvider.SelectedIndex == 0
                ? ("Sms", "Twilio") : ("WhatsApp", "MetaCloud");
            await CommunicationQueuePolicyStore.SaveProviderAllowanceAsync(
                channel, provider, Decimal.ToInt32(dailyLimit.Value),
                Decimal.ToInt32(importantReserve.Value));
            Changed = true;
            queueFeedback.Text = "Daily provider allowance saved.";
        });
        workerToggle.Click += async (_, _) => await RunAsync(async () =>
        {
            if (backgroundEnabled)
            {
                if (ModernMessageDialog.Show(this,
                    "Disable unattended API sending on this Windows account?",
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                await CommunicationBackgroundEnrollment.DisableAsync();
            }
            else
            {
                if (ModernMessageDialog.Show(this,
                    "Authorize this Windows computer to process confirmed scheduled API messages while FMSC is closed? " +
                    "A previously unfinished batch stays held for review and provider charges may apply.",
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                await CommunicationBackgroundEnrollment.EnrollAsync();
            }
            Changed = true;
            await RefreshQueueAsync();
        });
        calendarButton.Click += (_, _) =>
        {
            using var dialog = new CommunicationSchoolCalendarDialog();
            dialog.ShowDialog(this);
            if (dialog.Changed) Changed = true;
        };
        quotaProvider.SelectedIndexChanged += (_, _) => ApplySelectedQuota();
        page.Controls.Add(content);
        return page;
    }

    private async Task RefreshQueueAsync()
    {
        using var rows = await CommunicationQueuePolicyStore.ReadDispatchControlAsync();
        if (rows.Rows.Count != 1) throw new InvalidOperationException("Sending controls are not available.");
        DataRow row = rows.Rows[0];
        schoolTimeZoneId = Convert.ToString(row["TimeZoneId"]) ?? "";
        masterPaused = row.Field<bool>("MasterPaused");
        backgroundEnabled = row.Field<bool>("UnattendedEnabled");
        workerToggle.Text = backgroundEnabled ? "Disable Background Sending" : "Enable Background Sending";
        workerToggle.Enabled = CurrentUser.IsAdministrator;
        workerState.Text = backgroundEnabled ? "  Authorized periodic queue processing" : "  Off until administrator enrollment";
        masterToggle.Text = masterPaused ? "Resume All Sending" : "Pause All Sending";
        masterState.Text = masterPaused ? "  All API sending paused" : "  API sending allowed when approved";
        var start = (TimeSpan)row["WindowStart"];
        var end = (TimeSpan)row["WindowEnd"];
        startTime.Value = DateTime.Today.Add(start);
        endTime.Value = DateTime.Today.Add(end);
        speed.Value = Convert.ToDecimal(row["MaxPerMinute"]);
        var quotas = await CommunicationQueuePolicyStore.ReadProviderQuotasAsync();
        var old = quotaData;
        quotaData = quotas;
        old?.Dispose();
        ApplySelectedQuota();
    }

    private void ApplySelectedQuota()
    {
        string provider = quotaProvider.SelectedIndex == 0 ? "Twilio" : "MetaCloud";
        DataRow? row = quotaData?.AsEnumerable().FirstOrDefault(r =>
            string.Equals(r.Field<string>("ProviderCode"), provider, StringComparison.Ordinal));
        dailyLimit.Value = row is null ? 500 : Convert.ToDecimal(row["DailyLimit"]);
        importantReserve.Value = row is null ? 50 : Convert.ToDecimal(row["ReservedImportant"]);
    }

    private TabPage BuildAutomationTab()
    {
        var page = new TabPage("Automation") { BackColor = AppTheme.PageBackground };
        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 6,
            Padding = new Padding(AppScale.Scale(15)), Margin = Padding.Empty
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (int height in new[] { 50, 64, 64, 54, 45, 80 })
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(height)));
        content.Controls.Add(new Label
        {
            Text = "Each event starts Off. Prepare for Review requires an operator; Send Automatically needs a separate administrator approval for its API method, template and limits.",
            Dock = DockStyle.Fill, ForeColor = AppTheme.MutedText, AutoSize = false
        }, 0, 0);
        var rule = Labelled("School event", automationEvent);
        var mode = Labelled("Automation mode", automationMode);
        automationMode.Items.AddRange(new object[] { "Off", "Prepare for Review", "Send Automatically" });
        automationMode.SelectedIndex = 0;
        content.Controls.Add(rule, 0, 1);
        content.Controls.Add(mode, 0, 2);
        var save = new ModernButton { Text = "Save Automation Mode", ThemeRole = "Primary", Width = AppScale.Scale(205) };
        var exceptions = new ModernButton
        {
            Name = "communicationFamilyExceptions", Text = "Family Exceptions…",
            ThemeRole = "Secondary", Width = AppScale.Scale(185)
        };
        var feeLimits = new ModernButton
        {
            Name = "communicationFeeReminderLimits", Text = "Fee Reminder Limits…",
            ThemeRole = "Secondary", Width = AppScale.Scale(187)
        };
        var automationActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty
        };
        save.Height = exceptions.Height = feeLimits.Height = AppMetrics.ButtonHeight;
        automationActions.Controls.Add(save);
        automationActions.Controls.Add(exceptions);
        automationActions.Controls.Add(feeLimits);
        content.Controls.Add(automationActions, 0, 3);
        var schoolNotices=new ModernButton
        {
            Name="communicationSchoolNotices",Text="School Notices…",
            ThemeRole="Secondary",Width=AppScale.Scale(185),Height=AppMetrics.ButtonHeight
        };
        var noticeActions=new FlowLayoutPanel {Dock=DockStyle.Fill,WrapContents=false,Margin=Padding.Empty};
        noticeActions.Controls.Add(schoolNotices);
        content.Controls.Add(noticeActions,0,4);
        content.Controls.Add(automationFeedback, 0, 5);
        schoolNotices.Click+=(_,_) =>
        {
            if(busy || !CurrentUser.HasPermission("Communication.Configure"))return;
            using var dialog=new CommunicationSchoolNoticeDialog();
            dialog.ShowDialog(this);
            if(dialog.Changed)Changed=true;
        };
        exceptions.Click += (_, _) =>
        {
            if (busy || !CurrentUser.HasPermission("Communication.Configure")) return;
            using var dialog = new CommunicationFamilyExceptionsDialog();
            dialog.ShowDialog(this);
            if (dialog.Changed) Changed = true;
        };
        feeLimits.Click += (_, _) =>
        {
            if (busy || !CurrentUser.HasPermission("Communication.Configure")) return;
            using var dialog = new CommunicationFeeReminderPolicyDialog();
            dialog.ShowDialog(this);
            if (dialog.Changed) Changed = true;
        };
        automationEvent.SelectedIndexChanged += (_, _) => ApplySelectedRule();
        save.Click += async (_, _) => await RunAsync(async () =>
        {
            if (automationEvent.SelectedItem is not AutomationChoice selected)
                throw new InvalidOperationException("Choose an automation event.");
            bool review=automationMode.SelectedIndex==1;
            if(automationMode.SelectedIndex==2 && selected.Code is ("Admission" or "PaymentReceived" or "Birthday" or "OutstandingFees" or "UpcomingDue" or "OverdueFollowUp"))
            {
                using var authorization=new CommunicationAutomaticRuleDialog(selected.Code);
                if(authorization.ShowDialog(this)==DialogResult.OK && authorization.Changed)
                {
                    Changed=true;await RefreshAutomationAsync();
                    automationFeedback.Text="Automatic event authorization saved. The enrolled processor rechecks each source, consent, pause and allowance before sending.";
                }
                return;
            }
            if(automationMode.SelectedIndex==2 ||
               (review && selected.Code is not ("Admission" or "PaymentReceived" or "Birthday"
                    or "OutstandingFees" or "UpcomingDue" or "OverdueFollowUp")))
            {
                automationFeedback.Text="The selected event needs an approved school notice source before it can be enabled.";
                return;
            }
            await CommunicationQueuePolicyStore.SaveAutomationModeAsync(
                selected.Code,review ? "Review" : "Off");
            Changed = true;
            await RefreshAutomationAsync();
            automationFeedback.Text=review
                ? "Review suggestions enabled. Open Queue → Event Suggestions to prepare messages; nothing sends automatically."
                : "This event's suggestions are paused. No automatic messages will be sent.";
        });
        page.Controls.Add(content);
        return page;
    }

    private sealed record AutomationChoice(string Code, string Caption)
    {
        public override string ToString() => Caption;
    }

    private async Task RefreshAutomationAsync()
    {
        var rows = await CommunicationQueuePolicyStore.ReadAutomationRulesAsync();
        var old = ruleData;
        ruleData = rows;
        old?.Dispose();
        string? selected = (automationEvent.SelectedItem as AutomationChoice)?.Code;
        automationEvent.Items.Clear();
        foreach (DataRow row in rows.Rows)
        {
            string code = Convert.ToString(row["EventCode"]) ?? "";
            string caption = code switch
            {
                "Admission" => "Student admitted",
                "PaymentReceived" => "Fee payment received",
                "OutstandingFees" => "Outstanding fee reminders",
                "UpcomingDue" => "Upcoming fee due date",
                "OverdueFollowUp" => "Overdue fee follow-up",
                "Announcement" => "School announcements",
                "ExaminationResult" => "Examinations and results",
                "Birthday" => "Birthdays and greetings",
                _ => code
            };
            automationEvent.Items.Add(new AutomationChoice(code, caption));
        }
        automationEvent.SelectedItem = automationEvent.Items.Cast<AutomationChoice>()
            .FirstOrDefault(item => item.Code == selected) ?? automationEvent.Items.Cast<AutomationChoice>().FirstOrDefault();
        ApplySelectedRule();
    }

    private void ApplySelectedRule()
    {
        if (automationEvent.SelectedItem is not AutomationChoice selected) return;
        var row = ruleData?.AsEnumerable().FirstOrDefault(r =>
            string.Equals(r.Field<string>("EventCode"), selected.Code, StringComparison.Ordinal));
        automationMode.SelectedIndex = Convert.ToString(row?["Mode"]) switch
        {
            "Review" => 1, "Automatic" => 2, _ => 0
        };
    }

    private Control BuildMethod(Method method)
    {
        var status = new Label
        {
            Dock = DockStyle.Fill, AutoEllipsis = true, Text = "Loading…",
            TextAlign = ContentAlignment.MiddleLeft
        };
        var setup = new ModernButton
        {
            Name = method.Provider + "Setup", Text = "Set Up…", Dock = DockStyle.Top,
            Height = AppMetrics.ButtonHeight, Enabled = false
        };
        var toggle = new ModernButton
        {
            Name = method.Provider + "Toggle", Dock = DockStyle.Top, Height = AppMetrics.ButtonHeight,
            Enabled = false, ThemeRole = "Secondary"
        };
        bool manual = method.Provider == "WhatsAppBusinessManual";
        setup.Visible = !manual;
        var row = new MethodRow(method, status, setup, toggle);
        methods.Add(row);
        var title = HumanUi.GroupHeading(method.Title);
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2,
            BackColor = AppTheme.Surface, Padding = new Padding(AppScale.Scale(8), 0, AppScale.Scale(8), 0)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, AppScale.Scale(100)));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, AppScale.Scale(150)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, HumanUi.GroupHeadingHeight));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.ButtonHeight + AppScale.Scale(8)));
        layout.Controls.Add(title, 0, 0);
        layout.SetColumnSpan(title, 3);
        layout.Controls.Add(status, 0, 1);
        layout.Controls.Add(setup, 1, 1);
        layout.Controls.Add(toggle, 2, 1);
        setup.Click += async (_, _) => await RunAsync(() => ConfigureAsync(method));
        toggle.Click += async (_, _) => await RunAsync(() => ToggleAsync(row));
        return layout;
    }

    private async Task RefreshAsync()
    {
        using var channels = await read();
        foreach (var row in methods)
        {
            Method method = row.Method;
            DataRow? channel = channels.AsEnumerable().FirstOrDefault(r => r.Field<string>("Channel") == method.Channel);
            bool enabled = channel?.Field<bool>("Enabled") == true;
            bool selected = channel?.Field<string>("ProviderCode") == method.Provider;
            bool manual = method.Provider == "WhatsAppBusinessManual";
            bool hasProfile = !manual && HasProfile(method);
            row.Active = enabled && selected;
            row.Status.Text = row.Active
                ? manual ? "Ready for manual chats" : hasProfile ? "Enabled" : "Enabled · set up this computer"
                : enabled ? "Another method is active for this channel"
                : !manual && (!selected || !hasProfile) ? "Setup required" : selected ? "Paused" : "Available";
            row.Status.ForeColor = row.Active ? AppTheme.Success : AppTheme.MutedText;
            row.Setup.Enabled = channel is not null;
            row.Toggle.Text = row.Active ? "Pause" : manual ? "Enable" : "Verify & Enable";
            row.Toggle.Enabled = row.Active || (!enabled && channel is not null && (manual || (selected && hasProfile)));
        }
        block.Enabled = true;
        if (useLivePreferences)
        {
            using var preferred = await CommunicationQueuePolicyStore.ReadPreferredMethodAsync();
            if (preferred.Rows.Count == 1)
            {
                var row = preferred.Rows[0];
                preferredMethod.SelectedIndex = (Convert.ToString(row["PreferredChannel"]),
                    Convert.ToString(row["PreferredProviderCode"])) switch
                {
                    ("WhatsApp","WhatsAppBusinessManual") => 0,
                    ("WhatsApp","MetaCloud") => 1,
                    ("Sms","Twilio") => 2,
                    _ => 0
                };
            }
        }
    }

    private static bool HasProfile(Method method)
    {
        try { return CommunicationProviderCredentialStore.ReadMetadata(method.Channel).ProviderCode == method.Provider; }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException
            or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException)
        { return false; }
    }

    private async Task ConfigureAsync(Method method)
    {
        bool switching = methods.Any(row => row.Active && row.Method.Channel == method.Channel && row.Method != method);
        if (switching && ModernMessageDialog.Show(this,
                "Setting up this provider will pause the currently active method for this channel. Continue?",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2)
            != DialogResult.Yes) return;
        using var dialog = new CommunicationProviderDialog(method.Channel);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        Changed = true; // The channel can be paused even if saving the profile fails.
        try { await CommunicationManagementStore.ConfigureProviderAsync(dialog.Profile); }
        finally { await RefreshAsync(); }
    }

    private async Task ToggleAsync(MethodRow row)
    {
        Method method = row.Method;
        bool manual = method.Provider == "WhatsAppBusinessManual";
        string question = row.Active ? $"Pause {method.Title}?"
            : manual ? "Enable manual WhatsApp chats? The operator presses Send in WhatsApp."
            : $"Verify and enable {method.Title}? Provider charges may apply when you send.";
        if (ModernMessageDialog.Show(this, question, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        if (row.Active) await CommunicationManagementStore.PauseAsync(method.Channel);
        else if (manual) await CommunicationManagementStore.EnableManualWhatsAppAsync();
        else await CommunicationManagementStore.VerifyAndEnableAsync(method.Channel);
        Changed = true;
        await RefreshAsync();
    }

    private async Task RecordOptOutAsync()
    {
        using var dialog = new ApplicationWindowForm
        {
            Text = "Block a Number", StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(AppScale.Scale(460), AppScale.Scale(205)),
            FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
            Padding = new Padding(AppScale.Scale(14)), ShowInTaskbar = false
        };
        var channel = HumanUi.Combo();
        channel.Items.AddRange(new object[] { "WhatsApp", "SMS" });
        channel.SelectedIndex = 0;
        var phone = HumanUi.TextBox(false, 100);
        var save = new ModernButton { Text = "Block Number", ThemeRole = "Primary" };
        var cancel = new ModernButton { Text = "Cancel", DialogResult = DialogResult.Cancel };
        string? number = null;
        save.Click += (_, _) =>
        {
            number = CommunicationRecipientResolver.NormalizeMobile(phone.Text);
            if (number is null) ModernMessageDialog.ShowWarning(dialog, "Enter a valid Pakistan mobile number.");
            else dialog.DialogResult = DialogResult.OK;
        };
        var actions = HumanUi.DialogButtons(save, cancel);
        actions.Dock = DockStyle.Bottom;
        var methodField = HumanUi.Field("Channel", channel);
        var phoneField = HumanUi.Field("Mobile number", phone, true);
        foreach (var field in new[] { methodField, phoneField })
        {
            field.Dock = DockStyle.Top;
            field.Height = AppMetrics.FieldRowHeight;
        }
        dialog.Controls.AddRange(new Control[] { phoneField, methodField, actions });
        dialog.CancelButton = cancel;
        if (dialog.ShowDialog(this) != DialogResult.OK || number is null) return;
        await CommunicationManagementStore.RecordChannelOptOutAsync(channel.SelectedIndex == 0 ? "WhatsApp" : "Sms", number);
        Changed = true;
        feedback.Text = "Number blocked for " + channel.Text + ".";
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (busy || !CurrentUser.HasPermission("Communication.Configure")) return;
        busy = true;
        settingsTabs.Enabled = false;
        feedback.Text = "";
        queueFeedback.Text = "";
        automationFeedback.Text = "";
        try { await action(); }
        catch (Exception ex)
        {
            if (!IsDisposed)
                feedback.Text = queueFeedback.Text = automationFeedback.Text = DbMessage.UserMessage(ex);
        }
        finally
        {
            busy = false;
            if (!IsDisposed) settingsTabs.Enabled = true;
        }
    }
}
