using System.Data;
using FMSC_SMS.Core;

namespace FMSC_SMS.Pages.Communication;

/// <summary>
/// Communication Center with a separately authorized FREE manual WhatsApp
/// handoff and protected History / Settings. Direct API delivery remains
/// disabled until real provider acceptance; opening a chat never means Sent.
/// </summary>
public sealed class CommunicationCenterPage : V2Page, IBusyPage
{
    private readonly ModernComboBox audience = HumanUi.Combo();
    private readonly LookupBox campus = new(), cls = new();
    private readonly StudentPicker student = new();
    private readonly StaffPicker staff = new();
    private readonly ModernComboBox channel = HumanUi.Combo();
    private readonly ModernComboBox templateChoice = HumanUi.Combo();
    private readonly List<TemplateChoice> composeTemplates = new();

    private sealed record TemplateChoice(int Id,string Name,string Channel,string Purpose,string Body)
    {
        public override string ToString() => Name;
    }
    private readonly ModernTextBox message = HumanUi.TextBox(true, 1600);
    private readonly Label heading = new()
    {
        Dock = DockStyle.Fill, Font = AppTheme.ButtonFont,
        ForeColor = AppTheme.HeadingText, TextAlign = ContentAlignment.MiddleLeft
    };
    private readonly Label summary = new()
    {
        Dock = DockStyle.Top, Height = 94, AutoEllipsis = true,
        ForeColor = AppTheme.Text, Padding = new Padding(12, 8, 12, 8)
    };
    private readonly Label confirmation = new()
    {
        Dock = DockStyle.Fill, AutoEllipsis = true,
        Font = AppTheme.NormalFont, ForeColor = AppTheme.Text,
        TextAlign = ContentAlignment.TopLeft, Padding = new Padding(16)
    };
    private readonly DataGridView previewGrid = HumanUi.Grid(false);
    private readonly DataGridView historyGrid = HumanUi.Grid(false);
    private readonly DataGridView settingsGrid = HumanUi.Grid(false);
    private readonly DataGridView templateGrid = HumanUi.Grid(false);
    private readonly Panel steps = new() { Dock = DockStyle.Fill };
    private readonly List<HumanCard> cards = new();
    private readonly Panel campusField, classField, studentField, staffField;
    private readonly ModernButton previous, next, confirm, historyButton, settingsButton,
        newMessageButton, pauseSmsButton, pauseWhatsAppButton;
    private CommunicationPreview? preview;
    private CommunicationTarget? previewTarget;
    private string? previewMessage;
    private DataTable? previewTable;
    private bool manualModeAvailable;
    private Guid manualRequestId = Guid.Empty;
    private int step;
    private int previewReadVersion;
    private int managementReadVersion;

    public bool IsBusy => BusyOperationRunning;
    public string BusyMessage => "School records are being checked for current recipient eligibility.";

    public CommunicationCenterPage() : this(true) { }

    // SQL-free construction supports synthetic Windows UI tests.
    internal CommunicationCenterPage(bool initialize)
        : base("Communication Center", "Choose a group once. Admission Yes is the sole guardian consent.")
    {
        audience.Items.AddRange(new object[]
        {
            "Entire school", "Campus", "Class", "Outstanding fees",
            "One student", "Family / siblings", "One staff member"
        });
        audience.SelectedIndex = 0;
        channel.Items.AddRange(new object[] { "SMS", "WhatsApp" });
        channel.SelectedIndex = 0;
        templateChoice.Items.Add("Write your own message");
        templateChoice.SelectedIndex = 0;

        campusField = HumanUi.Field("Campus", campus);
        classField = HumanUi.Field("Class", cls);
        studentField = HumanUi.Field("Student / family reference", student);
        staffField = HumanUi.Field("Staff member", staff);

        cards.Add(TargetCard());
        cards.Add(ComposeCard());
        cards.Add(PreviewCard());
        cards.Add(ConfirmCard());
        cards.Add(HistoryCard());
        cards.Add(SettingsCard());
        foreach (var card in cards)
        {
            card.Dock = DockStyle.Fill;
            steps.Controls.Add(card);
        }
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
            Margin = Padding.Empty, Padding = Padding.Empty
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(48)));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(heading, 0, 0);
        root.Controls.Add(steps, 0, 1);

        previous = AddAction("Back", (_, _) =>
        {
            if (step > 0) SetStep(step - 1);
        }, "Secondary");
        next = AddBusyAction("Continue", ContinueAsync, "Primary");
        confirm = AddBusyAction("Open WhatsApp Chats", StartManualHandoffAsync, "Primary");
        // Only the operator-controlled handoff may be activated in this version.
        confirm.Enabled = false;
        historyButton = AddBusyAction("History", LoadHistoryAsync, "Secondary");
        settingsButton = AddBusyAction("Settings", LoadSettingsAsync, "Secondary");
        newMessageButton = AddAction("New Message", (_, _) =>
        {
            InvalidatePreview();
            SetStep(0);
        }, "Secondary");
        pauseSmsButton = AddBusyAction("Pause SMS", () => PauseChannelAsync("Sms"), "Secondary");
        pauseWhatsAppButton = AddBusyAction("Pause WhatsApp",
            () => PauseChannelAsync("WhatsApp"), "Secondary");
        GateAction(historyButton, "Communication.View");
        GateAction(settingsButton, "Communication.Configure");
        GateAction(pauseSmsButton, "Communication.Configure");
        GateAction(pauseWhatsAppButton, "Communication.Configure");
        SetContent(root);

        audience.SelectedIndexChanged += (_, _) => {
            ChangeAudienceFields(); RefreshTemplateChoices(); InvalidatePreview();
        };
        campus.SelectedIndexChanged += (_, _) => InvalidatePreview();
        cls.SelectedIndexChanged += (_, _) => InvalidatePreview();
        student.UserSelectionChanged += (_, _) => InvalidatePreview();
        student.SelectionCleared += (_, _) => InvalidatePreview();
        staff.UserSelectionChanged += (_, _) => InvalidatePreview();
        staff.SelectionCleared += (_, _) => InvalidatePreview();
        channel.SelectedIndexChanged += (_, _) => {
            RefreshTemplateChoices(); InvalidatePreview();
        };
        templateChoice.SelectedIndexChanged += (_, _) =>
        {
            if (templateChoice.SelectedItem is TemplateChoice selected)
                message.Text = selected.Body;
        };
        message.TextChanged += (_, _) => InvalidatePreview();

        ChangeAudienceFields();
        SetStep(0);
        if (initialize) Defer(BindLookupsAsync);
        Disposed += (_, _) =>
        {
            previewTable?.Dispose();
            (historyGrid.DataSource as DataTable)?.Dispose();
            (settingsGrid.DataSource as DataTable)?.Dispose();
            (templateGrid.DataSource as DataTable)?.Dispose();
        };
    }

    private HumanCard TargetCard()
    {
        var card = HumanUi.Card("1. Select Recipients",
            "Whole school, campus, class and fee targets are automatic. Individual pickers are only for specific records.");
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AutoScroll = true, Padding = new Padding(12)
        };
        var fields = new Control[]
        {
            HumanUi.Field("Send to", audience, true),
            campusField, classField, studentField, staffField
        };
        foreach (Control field in fields)
        {
            field.Height = AppScale.Scale(65);
            field.Margin = new Padding(0, 0, 0, AppScale.Scale(8));
            flow.Controls.Add(field);
        }
        void FitFields()
        {
            foreach (Control field in fields)
                field.Width = Math.Max(AppScale.Scale(250), flow.ClientSize.Width - AppScale.Scale(44));
        }
        flow.SizeChanged += (_, _) => FitFields();
        card.Content.Controls.Add(flow);
        return card;
    }

    private HumanCard ComposeCard()
    {
        var card = HumanUi.Card("2. Compose Message",
            "One message for the chosen group. No automated delivery or second guardian consent.");
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1, RowCount = 3, Padding = new Padding(12)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(65)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(65)));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(HumanUi.Field("Channel", channel, true), 0, 0);
        layout.Controls.Add(HumanUi.Field("Template (optional)", templateChoice), 0, 1);
        layout.Controls.Add(HumanUi.Field("Message", message, true), 0, 2);
        card.Content.Controls.Add(layout);
        return card;
    }

    private HumanCard PreviewCard()
    {
        var card = HumanUi.Card("3. Preview",
            "Live school records and Admission consent determine eligibility; shared phone numbers appear once.");
        previewGrid.AutoGenerateColumns = false;
        foreach (var (field, name, size) in new[]
        {
            ("RecordName", "School record", 190),
            ("Kind", "Type", 80),
            ("Mobile", "Destination", 135),
            ("Reason", "Eligibility", 180)
        })
            previewGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = field, DataPropertyName = field, HeaderText = name,
                MinimumWidth = size, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                SortMode = DataGridViewColumnSortMode.NotSortable
            });
        card.Content.Controls.Add(previewGrid);
        card.Content.Controls.Add(summary);
        return card;
    }

    private HumanCard ConfirmCard()
    {
        var card = HumanUi.Card("4. Confirm",
            "Free WhatsApp mode opens one prepared chat at a time. You must press Send manually. Direct provider delivery remains disabled.");
        card.Content.Controls.Add(confirmation);
        return card;
    }

    private HumanCard HistoryCard()
    {
        var card = HumanUi.Card("History",
            "Confirmed is not Delivered. Distinguish pending, provider acknowledgements, failures and unknown outcomes.");
        historyGrid.AutoGenerateColumns = false;
        foreach (var (field, caption, width) in new[]
        {
            ("CreatedUtc", "Created (UTC)", 155),
            ("Channel", "Channel", 75),
            ("Purpose", "Purpose", 115),
            ("State", "Batch state", 100),
            ("Destinations", "Targets", 70),
            ("InProgress", "Pending", 75),
            ("Delivered", "Delivered", 85),
            ("Failed", "Failed", 70),
            ("Unknown", "Unknown", 75),
            ("Method", "Method", 160),
            ("ManualReported", "Operator reported", 105),
            ("ManualSkipped", "Manual skipped", 95)
        })
            historyGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = field, DataPropertyName = field, HeaderText = caption,
                MinimumWidth = width, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                SortMode = DataGridViewColumnSortMode.NotSortable
            });
        var resume = new ModernButton
        {
            Text = "Resume Manual WhatsApp", ThemeRole = "Secondary",
            Width = AppScale.Scale(225), Height = AppMetrics.ButtonHeight,
            Enabled = CurrentUser.HasPermission("Communication.Send")
        };
        resume.Click += async (_, _) => await ResumeManualFromHistoryAsync();
        var footer = HumanUi.ButtonRow(resume);
        footer.Dock = DockStyle.Bottom;
        card.Content.Controls.Add(historyGrid);
        card.Content.Controls.Add(footer);
        return card;
    }

    private HumanCard SettingsCard()
    {
        var card = HumanUi.Card("Settings",
            "Administrator-controlled SMS and WhatsApp status, pause controls and school templates. Credentials are never stored here.");
        settingsGrid.AutoGenerateColumns = false;
        foreach (var (field, caption, width) in new[]
        {
            ("Channel", "Channel", 85), ("ProviderCode", "Provider", 175),
            ("Enabled", "Enabled", 90), ("UpdatedUtc", "Updated (UTC)", 160)
        })
            settingsGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = field, DataPropertyName = field, HeaderText = caption,
                MinimumWidth = width, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                SortMode = DataGridViewColumnSortMode.NotSortable
            });
        templateGrid.AutoGenerateColumns = false;
        foreach (var (field, caption, width) in new[]
        {
            ("Name", "Template", 150), ("Channel", "Channel", 85),
            ("Purpose", "Purpose", 110), ("Body", "Message", 260),
            ("Active", "Active", 70)
        })
            templateGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = field, DataPropertyName = field, HeaderText = caption,
                MinimumWidth = width, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                SortMode = DataGridViewColumnSortMode.NotSortable
            });
        var tables = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5,
            Padding = new Padding(8), Margin = Padding.Empty
        };
        tables.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        tables.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(30)));
        tables.RowStyles.Add(new RowStyle(SizeType.Percent, 35));
        tables.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(30)));
        tables.RowStyles.Add(new RowStyle(SizeType.Percent, 65));
        tables.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(52)));
        tables.Controls.Add(HumanUi.GroupHeading("Provider status — direct delivery disabled; free manual WhatsApp available"), 0, 0);
        tables.Controls.Add(settingsGrid, 0, 1);
        tables.Controls.Add(HumanUi.GroupHeading("Templates (read-only until editor acceptance)"), 0, 2);
        tables.Controls.Add(templateGrid, 0, 3);
        var configureProvider = new ModernButton
        {
            Text = "Configure Provider", ThemeRole = "Secondary",
            Width = AppScale.Scale(160), Height = AppMetrics.ButtonHeight
        };
        configureProvider.Enabled = CurrentUser.HasPermission("Communication.Configure");
        configureProvider.Click += async (_, _) => await ConfigureProviderAsync();
        var enableFreeWhatsApp = new ModernButton
        {
            Text = "Use Free WhatsApp", ThemeRole = "Secondary",
            Width = AppScale.Scale(170), Height = AppMetrics.ButtonHeight,
            Enabled = CurrentUser.HasPermission("Communication.Configure")
        };
        enableFreeWhatsApp.Click += async (_, _) => await EnableFreeWhatsAppAsync();
        var addTemplate = new ModernButton
        {
            Text = "New Template", ThemeRole = "Primary",
            Width = AppScale.Scale(150), Height = AppMetrics.ButtonHeight
        };
        var editTemplate = new ModernButton
        {
            Text = "Edit Selected", ThemeRole = "Secondary",
            Width = AppScale.Scale(150), Height = AppMetrics.ButtonHeight
        };
        addTemplate.Enabled = editTemplate.Enabled =
            CurrentUser.HasPermission("Communication.Configure");
        addTemplate.Click += async (_, _) => await EditTemplateAsync(false);
        editTemplate.Click += async (_, _) => await EditTemplateAsync(true);
        tables.Controls.Add(HumanUi.ButtonRow(configureProvider, enableFreeWhatsApp, addTemplate, editTemplate), 0, 4);
        card.Content.Controls.Add(tables);
        return card;
    }

    private async Task EnableFreeWhatsAppAsync()
    {
        if (!CurrentUser.HasPermission("Communication.Configure"))
        {
            Warn("Communication administrator permission is required.");
            return;
        }
        if (!Confirm("Activate the free, MANUAL WhatsApp Business method? FMSC will only open chats. "
            + "The operator must send each message; this is not automatic delivery.",
            "Enable Free WhatsApp", false))
            return;
        try
        {
            await CommunicationManagementStore.EnableManualWhatsAppAsync();
            if (!IsDisposed) await LoadSettingsAsync();
        }
        catch (Exception ex) { if (!IsDisposed) Fail(ex); }
    }

    private async Task ConfigureProviderAsync()
    {
        if (!CurrentUser.HasPermission("Communication.Configure"))
        {
            Warn("Administrator configuration permission is required.");
            return;
        }
        string chosenChannel = settingsGrid.CurrentRow?.DataBoundItem is DataRowView row
            ? Convert.ToString(row["Channel"]) ?? "Sms" : "Sms";
        if (chosenChannel == "Email")
        {
            Warn("School email is staged but its secure provider setup and delivery are not enabled.");
            return;
        }
        using var dialog = new CommunicationProviderDialog(chosenChannel);
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
            return;
        try
        {
            await CommunicationManagementStore.ConfigureProviderAsync(dialog.Profile);
            if (!IsDisposed) await LoadSettingsAsync();
        }
        catch (Exception ex) { if (!IsDisposed) Fail(ex); }
    }

    private async Task EditTemplateAsync(bool existing)
    {
        if (!CurrentUser.HasPermission("Communication.Configure"))
        {
            Warn("Administrator configuration permission is required.");
            return;
        }
        DataRowView? selected = existing
            ? templateGrid.CurrentRow?.DataBoundItem as DataRowView : null;
        if (existing && selected is null)
        {
            Warn("Select an existing template to edit.");
            return;
        }
        int id = selected is null ? 0 : Convert.ToInt32(selected["TemplateId"]);
        using var dialog = new CommunicationTemplateDialog(
            selected is null ? "" : Convert.ToString(selected["Name"]) ?? "",
            selected is null ? "Sms" : Convert.ToString(selected["Channel"]) ?? "Sms",
            selected is null ? "Announcement" : Convert.ToString(selected["Purpose"]) ?? "Announcement",
            selected is null ? "" : Convert.ToString(selected["Body"]) ?? "",
            selected is null || Convert.ToBoolean(selected["Active"]));
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
            return;
        try
        {
            await CommunicationManagementStore.SaveTemplateAsync(
                id, dialog.TemplateName, dialog.Channel, dialog.Purpose,
                dialog.Body, dialog.Active);
            if (!IsDisposed) await LoadSettingsAsync();
        }
        catch (Exception ex) { if (!IsDisposed) Fail(ex); }
    }

    private async Task LoadComposeTemplatesAsync()
    {
        composeTemplates.Clear();
        if (CurrentUser.HasPermission("Communication.Send"))
        {
            int actor = CurrentUser.UserId;
            using DataTable table = await CommunicationManagementStore.ReadComposeTemplatesAsync();
            if (actor != CurrentUser.UserId)
                throw new InvalidOperationException("Account changed while loading message templates.");
            foreach (DataRow row in table.Rows)
                composeTemplates.Add(new TemplateChoice(
                    Convert.ToInt32(row["TemplateId"]),
                    Convert.ToString(row["Name"]) ?? "",
                    Convert.ToString(row["Channel"]) ?? "",
                    Convert.ToString(row["Purpose"]) ?? "",
                    Convert.ToString(row["Body"]) ?? ""));
        }
        RefreshTemplateChoices();
    }

    private void RefreshTemplateChoices()
    {
        if (templateChoice is null) return;
        string purpose = ((CommunicationAudience)Math.Max(audience.SelectedIndex, 0)) switch
        {
            CommunicationAudience.OutstandingFees => "FeeReminder",
            CommunicationAudience.Staff => "Staff",
            CommunicationAudience.Student or CommunicationAudience.Family => "Individual",
            _ => "Announcement"
        };
        string selectedChannel = channel.SelectedIndex == 1 ? "WhatsApp" : "Sms";
        templateChoice.Items.Clear();
        templateChoice.Items.Add("Write your own message");
        foreach (TemplateChoice item in composeTemplates)
            if (item.Channel == selectedChannel && item.Purpose == purpose)
                templateChoice.Items.Add(item);
        templateChoice.SelectedIndex = 0;
    }

    private async Task LoadHistoryAsync()
    {
        try
        {
            int actor = CurrentUser.UserId;
            int version = ++managementReadVersion;
            DataTable table = await CommunicationManagementStore.ReadHistoryAsync();
            if (IsDisposed || actor != CurrentUser.UserId || version != managementReadVersion)
            {
                table.Dispose(); return;
            }
            var old = historyGrid.DataSource as DataTable;
            historyGrid.DataSource = null;
            historyGrid.DataSource = table;
            old?.Dispose();
            SetStep(4);
            SetStatus($"{table.Rows.Count} historical batches loaded. Provider submission does not imply delivery.");
        }
        catch (Exception ex) { if (!IsDisposed) Fail(ex); }
    }

    private async Task LoadSettingsAsync()
    {
        try
        {
            int actor = CurrentUser.UserId;
            int version = ++managementReadVersion;
            var channelTask = CommunicationManagementStore.ReadSettingsAsync();
            var templateTask = CommunicationManagementStore.ReadTemplatesAsync();
            await Task.WhenAll(channelTask, templateTask);
            DataTable channels = channelTask.Result, templates = templateTask.Result;
            if (IsDisposed || actor != CurrentUser.UserId || version != managementReadVersion)
            {
                channels.Dispose(); templates.Dispose(); return;
            }
            var oldChannels = settingsGrid.DataSource as DataTable;
            var oldTemplates = templateGrid.DataSource as DataTable;
            settingsGrid.DataSource = null;
            templateGrid.DataSource = null;
            settingsGrid.DataSource = channels;
            templateGrid.DataSource = templates;
            oldChannels?.Dispose(); oldTemplates?.Dispose();
            SetStep(5);
            SetStatus("Only free/manual WhatsApp can be activated. Automatic provider delivery remains disabled.");
        }
        catch (Exception ex) { if (!IsDisposed) Fail(ex); }
    }

    private async Task PauseChannelAsync(string name)
    {
        if (!Confirm("Pause " + name + " school messaging? No queued message will be dispatched while paused.",
                "Pause Channel", false))
            return;
        try
        {
            await CommunicationManagementStore.PauseAsync(name);
            if (!IsDisposed) await LoadSettingsAsync();
        }
        catch (Exception ex) { if (!IsDisposed) Fail(ex); }
    }

    private void ChangeAudienceFields()
    {
        var target = (CommunicationAudience)Math.Max(audience.SelectedIndex, 0);
        campusField.Visible = target is CommunicationAudience.Campus
            or CommunicationAudience.Class or CommunicationAudience.OutstandingFees;
        classField.Visible = target is CommunicationAudience.Class
            or CommunicationAudience.OutstandingFees;
        studentField.Visible = target is CommunicationAudience.Student or CommunicationAudience.Family;
        staffField.Visible = target == CommunicationAudience.Staff;
    }

    private void InvalidatePreview()
    {
        preview = null;
        previewTarget = null;
        previewMessage = null;
        manualModeAvailable = false;
        manualRequestId = Guid.Empty;
        previewReadVersion++;
    }

    private CommunicationTarget CurrentTarget() => new(
        (CommunicationAudience)Math.Max(audience.SelectedIndex, 0),
        campus.SelectedId, cls.SelectedId, student.SelectedId, staff.SelectedId,
        channel.SelectedIndex == 1 ? "WhatsApp" : "Sms");

    private void SetStep(int number)
    {
        step = Math.Clamp(number, 0, 5);
        string[] stages = { "1 / 4   Select Recipients", "2 / 4   Compose Message",
            "3 / 4   Preview", "4 / 4   Confirm & Send",
            "Communication History", "Communication Settings" };
        heading.Text = stages[step];
        for (int i = 0; i < cards.Count; i++) cards[i].Visible = i == step;
        cards[step].BringToFront();
        previous.Visible = step < 4;
        previous.Enabled = step is > 0 and < 4;
        next.Visible = step < 3;
        confirm.Visible = step == 3;
        confirm.Text = manualModeAvailable ? "Open WhatsApp Chats" : "Confirm & Send";
        confirm.Enabled = step == 3 && manualModeAvailable
            && CurrentUser.HasPermission("Communication.Send");
        newMessageButton.Visible = step >= 4;
        pauseSmsButton.Visible = step == 5 && CurrentUser.HasPermission("Communication.Configure");
        pauseWhatsAppButton.Visible = step == 5 && CurrentUser.HasPermission("Communication.Configure");
        if (step == 3)
            SetStatus(manualModeAvailable
                ? "Free/manual WhatsApp: open each chat and press Send yourself. Not verified delivery."
                : "Direct sending remains disabled until provider acceptance.",
                PageStatusKind.Warning);
    }

    private async Task StartManualHandoffAsync()
    {
        try
        {
            CommunicationTarget? target = previewTarget;
            CommunicationPreview? reviewed = preview;
            string? body = previewMessage;
            if (!manualModeAvailable || target?.Channel != "WhatsApp"
                || target != CurrentTarget() || reviewed is null
                || body != message.Text || manualRequestId == Guid.Empty)
            {
                Warn("Free WhatsApp is unavailable or the preview changed. Review recipients again.");
                return;
            }
            RequirePermission("Communication.Send");
            if (!Confirm("FMSC will open one WhatsApp chat at a time. YOU must press Send "
                + "in your school's WhatsApp Business account; FMSC cannot verify delivery. "
                + "Your progress will be recorded for later resumption. Continue?",
                "Start Manual WhatsApp", false))
                return;
            int actor = CurrentUser.UserId;
            CommunicationConfirmation saved = await CommunicationBatchStore.ConfirmPreviewAsync(
                target, reviewed, body, manualRequestId, manualWhatsApp: true);
            if (IsDisposed) return;
            if (actor != CurrentUser.UserId)
                throw new InvalidOperationException("Signed-in user changed. Reopen History.");
            using (var handoff = new CommunicationManualHandoffDialog(saved.BatchId))
                handoff.ShowDialog(FindForm());
            if (!IsDisposed && CurrentUser.HasPermission("Communication.View"))
                await LoadHistoryAsync();
            else if (!IsDisposed) SetStep(0);
        }
        catch (Exception ex) { if (!IsDisposed) Fail(ex); }
    }

    private async Task ResumeManualFromHistoryAsync()
    {
        if (!CurrentUser.HasPermission("Communication.Send"))
        {
            Warn("Communication.Send permission is required to resume a handoff.");
            return;
        }
        if (historyGrid.CurrentRow?.DataBoundItem is not DataRowView row
            || Convert.ToString(row["Method"]) != "WhatsApp Business (manual)")
        {
            Warn("Choose a manual WhatsApp batch from History.");
            return;
        }
        try
        {
            var batchId = (Guid)row["BatchId"];
            using (var handoff = new CommunicationManualHandoffDialog(batchId))
                handoff.ShowDialog(FindForm());
            if (!IsDisposed) await LoadHistoryAsync();
        }
        catch (Exception ex) { if (!IsDisposed) Fail(ex); }
    }

    private async Task ContinueAsync()
    {
        try
        {
            if (step == 0)
            {
                CommunicationTarget target = CurrentTarget();
                CommunicationRecipientResolver.Validate(target);
                RequirePermission(target.Audience == CommunicationAudience.Staff
                    ? "Staff.View" : "Students.View");
                if (target.Audience == CommunicationAudience.OutstandingFees)
                    RequirePermission("Fees.View");
                await LoadComposeTemplatesAsync();
                if (!IsDisposed) SetStep(1);
            }
            else if (step == 1)
            {
                if (string.IsNullOrWhiteSpace(message.Text))
                {
                    Warn("Write a message before previewing the recipients.");
                    return;
                }
                await LoadPreviewAsync();
            }
            else if (step == 2)
            {
                // Preview does not confer sending authority. SQL independently
                // checks this permission when confirmation is eventually enabled.
                RequirePermission("Communication.Send");
                if (preview is null || previewTarget != CurrentTarget()
                    || previewMessage != message.Text)
                {
                    Warn("The message or target changed. Refresh the preview.");
                    SetStep(1);
                    return;
                }
                if (preview.Included == 0)
                {
                    Warn("No eligible destinations. Review the Admission consent and phone numbers.");
                    return;
                }
                manualModeAvailable = previewTarget.Channel == "WhatsApp"
                    && await CommunicationManualHandoffStore.IsEnabledAsync();
                if (IsDisposed || previewTarget != CurrentTarget()
                    || previewMessage != message.Text) return;
                if (manualModeAvailable && manualRequestId == Guid.Empty)
                    manualRequestId = Guid.NewGuid();
                confirmation.Text = "Channel: " + previewTarget.Channel
                    + Environment.NewLine + "Selected school records: " + preview.Entries.Count
                    + Environment.NewLine + "Unique eligible destinations: " + preview.Included
                    + Environment.NewLine + "Excluded: " + preview.Excluded
                    + Environment.NewLine + Environment.NewLine + "Message:"
                    + Environment.NewLine + (previewMessage ?? "")
                    + Environment.NewLine + Environment.NewLine
                    + (manualModeAvailable
                        ? "Free WhatsApp mode: you will open one chat at a time, press Send in WhatsApp, "
                          + "then report your action. FMSC cannot verify delivery."
                        : "Direct sending is currently DISABLED. No message has been queued or delivered.");
                SetStep(3);
            }
        }
        catch (Exception ex) { if (!IsDisposed) Fail(ex); }
    }

    private async Task LoadPreviewAsync()
    {
        CommunicationTarget target = CurrentTarget();
        CommunicationRecipientResolver.Validate(target);
        int version = ++previewReadVersion;
        string capturedText = message.Text;
        CommunicationPreview result = await CommunicationRecipientResolver.ReadAsync(target);
        if (IsDisposed || version != previewReadVersion
            || target != CurrentTarget() || capturedText != message.Text)
            return;
        preview = result;
        previewTarget = target;
        previewMessage = capturedText;
        var table = new DataTable();
        table.Columns.Add("RecordName", typeof(string));
        table.Columns.Add("Kind", typeof(string));
        table.Columns.Add("Mobile", typeof(string));
        table.Columns.Add("Reason", typeof(string));
        foreach (var entry in result.Entries)
        {
            string maskedNumber = entry.PhoneE164 is { Length: >= 4 } number
                ? "•••• " + number[^4..] : "";
            table.Rows.Add(entry.RecordName, entry.Kind, maskedNumber, entry.Reason);
        }
        previewGrid.DataSource = null;
        previewTable?.Dispose();
        previewTable = table;
        previewGrid.DataSource = table;
        summary.Text = "Eligible destinations: " + result.Included
            + "   |   Excluded: " + result.Excluded
            + Environment.NewLine + "No consent recorded: " + result.NotRecorded
            + "   |   Declined: " + result.Declined
            + "   |   Invalid mobile: " + result.InvalidNumber
            + Environment.NewLine + "Opted out: " + result.Suppressed
            + "   |   Duplicate phone: " + result.Duplicates;
        SetStep(2);
        SetStatus("Preview loaded from current school records. Nothing has been sent.");
    }

    private async Task BindLookupsAsync()
    {
        try
        {
            var campuses = DatabaseHelper.QueryUserAsync("GetCampuses");
            var classes = DatabaseHelper.QueryUserAsync("GetClasses");
            await Task.WhenAll(campuses, classes);
            if (IsDisposed) return;
            campus.Bind(campuses.Result, "CampusId", "CampusName", "Choose campus");
            cls.Bind(classes.Result, "ClassId", "ClassName", "All classes");
        }
        catch (Exception ex) { if (!IsDisposed) Fail(ex); }
    }
}
