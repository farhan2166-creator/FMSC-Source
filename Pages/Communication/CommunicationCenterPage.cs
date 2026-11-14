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
    private const string ComposeChannel = "WhatsApp";
    private readonly ModernComboBox templateChoice = HumanUi.Combo();
    private readonly List<TemplateChoice> composeTemplates = new();

    private sealed record TemplateChoice(int Id,string Name,string Channel,string Purpose,string Body)
    {
        public override string ToString() => Name;
    }
    private readonly ModernTextBox message = HumanUi.TextBox(true, 1600);
    private readonly CheckBox showExcluded = new()
    {
        Text = "Show excluded records (not sendable)",
        Dock = DockStyle.Top, Height = AppScale.Scale(32),
        ForeColor = AppTheme.MutedText, Padding = new Padding(12, 0, 0, 0)
    };
    private readonly ModernTextBox sampleMessage = HumanUi.TextBox(true, 2400);
    private readonly Label summary = new()
    {
        Dock = DockStyle.Top, Height = 94, AutoEllipsis = true,
        ForeColor = AppTheme.Text, Padding = new Padding(12, 8, 12, 8)
    };
    private readonly ModernTextBox confirmation = HumanUi.TextBox(true, 5000);
    private readonly DataGridView previewGrid = HumanUi.Grid(false);
    private readonly DataGridView historyGrid = HumanUi.Grid(false);
    private readonly DataGridView settingsGrid = HumanUi.Grid(false);
    private readonly DataGridView templateGrid = HumanUi.Grid(false);
    private readonly Panel steps = new() { Dock = DockStyle.Fill };
    private readonly List<HumanCard> cards = new();
    private readonly Panel campusField, classField, studentField, staffField;
    private TableLayoutPanel targetFieldsLayout = null!;
    private readonly ModernButton previous, next, confirm, historyButton, settingsButton,
        newMessageButton, pauseSmsButton, unfinishedButton;
    private readonly ModernButton pauseWhatsAppButton = new() { Text = "Pause WhatsApp", ThemeRole = "Secondary", Width = AppScale.Scale(180), Height = AppMetrics.ButtonHeight, Enabled = false };
    private bool showingUnfinished;
    // Built by SettingsCard during the page constructor; cannot be readonly.
    private ModernButton enableFreeWhatsAppButton = null!;
    private CommunicationPreview? preview;
    private CommunicationTarget? previewTarget;
    private string? previewMessage;
    private DataTable? previewTable;
    private bool manualModeAvailable;
    private Guid manualRequestId = Guid.Empty;
    private int step;
    private int previewReadVersion;
    private int managementReadVersion;
    internal sealed record Operations(
        Func<CommunicationTarget, Task<CommunicationPreview>> ReadRecipients,
        Func<Task<bool>> ReadManualMode,
        Func<CommunicationTarget, CommunicationPreview, string, Guid, Task<CommunicationConfirmation>> ConfirmBatch,
        Action<Guid> OpenHandoff,
        Func<bool> ConfirmStart);
    private readonly Operations operations;


    public bool IsBusy => BusyOperationRunning;
    public string BusyMessage => "School records are being checked for current recipient eligibility.";

    public CommunicationCenterPage() : this(true) { }

    // SQL-free construction supports synthetic Windows UI tests.
    internal CommunicationCenterPage(bool initialize) : this(initialize, null, null, null, null) { }
    internal CommunicationCenterPage(bool initialize, Operations operations) : this(initialize, null, null, null, operations) { }
    internal CommunicationCenterPage(CommunicationTarget target, string body, string display)
        : this(true, target, body, display, null) { }
    private CommunicationCenterPage(bool initialize, CommunicationTarget? initialTarget, string? initialBody,
        string? initialDisplay, Operations? operations)
        : base("Communication Center", "Prepare WhatsApp Business messages and send them manually.")
    {
        this.operations = operations ?? new Operations(CommunicationRecipientResolver.ReadAsync,
            CommunicationManualHandoffStore.IsEnabledAsync,
            (target, preview, body, key) => CommunicationBatchStore.ConfirmPreviewAsync(target, preview, body, key, manualWhatsApp: true),
            batch => { using var dialog = new CommunicationManualHandoffDialog(batch); dialog.ShowDialog(FindForm()); },
            () => Confirm("FMSC will open one WhatsApp chat at a time. YOU must press Send "
                + "in your school's WhatsApp Business account; FMSC cannot verify delivery. "
                + "Your progress will be recorded for later resumption. Continue?", "Start Manual WhatsApp", false));
        sampleMessage.ReadOnly = true;
        sampleMessage.ScrollBars = ScrollBars.Vertical;
        sampleMessage.Dock = DockStyle.Bottom;
        sampleMessage.Height = AppScale.Scale(150);
        confirmation.Dock = DockStyle.Fill;
        confirmation.ReadOnly = true;
        confirmation.ScrollBars = ScrollBars.Vertical;
        // SelectionChanged can fire before CurrentRow moves to the clicked cell.
        previewGrid.CurrentCellChanged += (_, _) => ShowSelectedMessage();
        audience.Items.AddRange(new object[]
        {
            "Entire school", "Campus", "Class", "Outstanding fees",
            "One student", "Family / siblings", "One staff member"
        });
        audience.SelectedIndex = 0;
        templateChoice.Items.Add("Write your own message");
        templateChoice.SelectedIndex = 0;
        foreach (var builtIn in CommunicationPreparedMessages.BuiltIns.Select((item, index) => (item, index)))
            composeTemplates.Add(new TemplateChoice(-builtIn.index - 1, builtIn.item.Name,
                "WhatsApp", builtIn.item.Purpose, builtIn.item.Body));

        campusField = HumanUi.Field("Campus", campus);
        classField = HumanUi.Field("Class", cls);
        studentField = HumanUi.Field("Student / family reference", student);
        staffField = HumanUi.Field("Staff member", staff);

        cards.Add(TargetCard());
        cards.Add(PreviewCard());
        cards.Add(ConfirmCard());
        cards.Add(HistoryCard());
        cards.Add(SettingsCard());
        foreach (var card in cards)
        {
            card.Dock = DockStyle.Fill;
            steps.Controls.Add(card);
        }
        previous = AddAction("Back", (_, _) =>
        {
            if (!IsBusy && step > 0) SetStep(step - 1);
        }, "Secondary");
        next = AddBusyAction("Preview Recipients", () => RunBusyAsync(BusyMessage, ContinueAsync, this), "Primary");
        next.MinimumSize = new Size(AppScale.Scale(165), AppMetrics.ButtonHeight);
        next.Width = AppScale.Scale(165);
        confirm = AddBusyAction("Open WhatsApp Chats", () => RunBusyAsync("Confirming the reviewed manual batch…", StartManualHandoffAsync, this), "Primary");
        confirm.MinimumSize = new Size(AppScale.Scale(230), AppMetrics.ButtonHeight);
        confirm.Width = AppScale.Scale(230);
        // Only the operator-controlled handoff may be activated in this version.
        confirm.Enabled = false;
        historyButton = AddBusyAction("History", () => RunBusyAsync("Reading communication history…", LoadHistoryAsync, this), "Secondary");
        unfinishedButton = AddBusyAction("My Unfinished Batches",
            () => RunBusyAsync("Reading unfinished WhatsApp batches.", LoadUnfinishedAsync, this), "Secondary");
        unfinishedButton.Width = AppScale.Scale(200);
        GateAction(unfinishedButton, "Communication.Send");
        settingsButton = AddBusyAction("Settings", () => RunBusyAsync("Reading communication settings…", LoadSettingsAsync, this), "Secondary");
        newMessageButton = AddAction("New Message", (_, _) =>
        {
            if (IsBusy) return;
            InvalidatePreview();
            SetStep(0);
        }, "Secondary");
        pauseSmsButton = AddBusyAction("Pause SMS", () => RunBusyAsync("Pausing SMS…", () => PauseChannelAsync("Sms"), this), "Secondary");
        pauseWhatsAppButton.Click += async (_, _) => await RunBusyAsync("Pausing WhatsApp.", () => PauseChannelAsync("WhatsApp"), this);
        GateAction(historyButton, "Communication.View");
        GateAction(settingsButton, "Communication.Configure");
        GateAction(pauseSmsButton, "Communication.Configure");
        SetContent(steps);

        audience.SelectedIndexChanged += (_, _) => {
            student.SetStudent(0, ""); staff.SetStaff(0, "");
            ChangeAudienceFields(); RefreshTemplateChoices(); InvalidatePreview();
        };
        campus.SelectedIndexChanged += (_, _) => {
            if (cls.Items.Count > 0) cls.SelectedIndex = 0;
            InvalidatePreview();
        };
        cls.SelectedIndexChanged += (_, _) => InvalidatePreview();
        student.UserSelectionChanged += (_, _) => InvalidatePreview();
        student.SelectionCleared += (_, _) => InvalidatePreview();
        staff.UserSelectionChanged += (_, _) => InvalidatePreview();
        staff.SelectionCleared += (_, _) => InvalidatePreview();
        templateChoice.SelectedIndexChanged += (_, _) =>
        {
            if (templateChoice.SelectedItem is TemplateChoice selected)
                message.Text = selected.Body;
        };
        message.TextChanged += (_, _) => InvalidatePreview();

        ChangeAudienceFields();
        SetStep(0);
        RefreshTemplateChoices();
        if (!CurrentUser.HasPermission("Students.View") && CurrentUser.HasPermission("Staff.View"))
            audience.SelectedIndex = (int)CommunicationAudience.Staff;
        if (initialTarget is not null)
        {
            audience.SelectedIndex = (int)initialTarget.Audience;
            student.SetStudent(initialTarget.StudentId, initialDisplay ?? "Selected student");
            message.Text = initialBody ?? "";
        }
        bool compose = CommunicationAccess.CanCompose(CurrentUser.HasPermission);
        if (initialize)
        {
            if (compose)
            {
                if (CurrentUser.HasPermission("Students.View")) Defer(BindLookupsAsync);
                Defer(LoadComposeTemplatesAsync);
            }
            else if (CurrentUser.HasPermission("Communication.Configure")) Defer(LoadSettingsAsync);
            else if (CurrentUser.HasPermission("Communication.View")) Defer(LoadHistoryAsync);
        }
        if (!compose) SetStep(CurrentUser.HasPermission("Communication.Configure") ? 4 : 3);
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
        var card = HumanUi.Card("1 of 3 · Write Message");
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        targetFieldsLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, RowCount = 8, Padding = new Padding(12),
            Margin = Padding.Empty
        };
        targetFieldsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var fields = new Control[]
        {
            HumanUi.Field("Send to", audience, true),
            campusField, classField, studentField, staffField,
            HumanUi.Field("Prepared message (optional)", templateChoice),
            HumanUi.Field("Message", message, true)
        };
        for (int row = 0; row < fields.Length; row++)
        {
            fields[row].Dock = DockStyle.Fill;
            fields[row].Margin = Padding.Empty;
            targetFieldsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute,
                AppScale.Scale(row == 6 ? 240 : 72)));
            targetFieldsLayout.Controls.Add(fields[row], 0, row);
        }
        targetFieldsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(46)));
        targetFieldsLayout.Controls.Add(new Label
        {
            Text = "Prepared fields: [StudentName], [AdmissionNumber], [AdmissionDate], [OutstandingAmount]. " +
                   "Amounts and admission details come from current school records.",
            Dock = DockStyle.Fill, AutoEllipsis = true,
            ForeColor = AppTheme.MutedText
        }, 0, 7);
        scroll.Controls.Add(targetFieldsLayout);
        card.Content.Controls.Add(scroll);
        return card;
    }

    private HumanCard PreviewCard()
    {
        var card = HumanUi.Card("2 of 3 · Preview",
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
        showExcluded.CheckedChanged += (_, _) => BindPreviewRows();
        card.Content.Controls.Add(previewGrid);
        card.Content.Controls.Add(sampleMessage);
        card.Content.Controls.Add(showExcluded);
        card.Content.Controls.Add(summary);
        return card;
    }

    private HumanCard ConfirmCard()
    {
        var card = HumanUi.Card("3 of 3 · Confirm",
            "Open one prepared chat at a time and press Send in WhatsApp.");
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
            ("Audience", "Audience", 100),
            ("OperatorName", "Started by", 100),
            ("StateText", "Outcome", 145),
            ("InProgress", "Remaining", 85),
            ("ManualReported", "Reported sent", 105),
            ("ManualSkipped", "Skipped / excluded", 120)
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
        resume.Click += async (_, _) => await RunBusyAsync("Opening the saved manual session…", ResumeManualFromHistoryAsync, this);
        var footer = HumanUi.ButtonRow(resume);
        footer.Dock = DockStyle.Bottom;
        card.Content.Controls.Add(historyGrid);
        card.Content.Controls.Add(footer);
        return card;
    }

    private HumanCard SettingsCard()
    {
        var card = HumanUi.Card("Settings",
            "Review manual WhatsApp and prepared templates. Provider API and school email sending are unavailable.");
        settingsGrid.AutoGenerateColumns = false;
        foreach (var (field, caption, width) in new[]
        {
            ("Channel", "Channel", 85), ("SendingMethod", "Sending method", 205),
            ("StatusText", "Status", 190), ("UpdatedUtc", "Updated (UTC)", 160)
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
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6,
            Padding = new Padding(8), Margin = Padding.Empty
        };
        tables.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        tables.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(30)));
        tables.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(150)));
        tables.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(40)));
        tables.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(190)));
        // Keep administrative and template actions in separate rows so the
        // shared wrapping button layout cannot hide a second line of controls.
        tables.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(64)));
        tables.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(64)));
        tables.Controls.Add(HumanUi.GroupHeading("Sending methods — only manual WhatsApp can be activated"), 0, 0);
        tables.Controls.Add(settingsGrid, 0, 1);
        tables.Controls.Add(HumanUi.GroupHeading(
            "Message templates — use [StudentName], [AdmissionNumber], [AdmissionDate] or [OutstandingAmount]"), 0, 2);
        tables.Controls.Add(templateGrid, 0, 3);
        var configureProvider = new ModernButton
        {
            Text = "Configure Provider", ThemeRole = "Secondary",
            Width = AppScale.Scale(160), Height = AppMetrics.ButtonHeight
        };
        configureProvider.Enabled = CurrentUser.HasPermission("Communication.Configure");
        configureProvider.Click += async (_, _) => await RunBusyAsync("Saving provider settings…", ConfigureProviderAsync, this);
        enableFreeWhatsAppButton = new ModernButton
        {
            Text = "Use Free WhatsApp", ThemeRole = "Primary",
            Width = AppScale.Scale(245), MinimumSize = new Size(AppScale.Scale(245), AppMetrics.ButtonHeight),
            Height = AppMetrics.ButtonHeight,
            Enabled = CurrentUser.HasPermission("Communication.Configure")
        };
        enableFreeWhatsAppButton.Click += async (_, _) => await RunBusyAsync("Saving manual WhatsApp settings…", EnableFreeWhatsAppAsync, this);
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
        addTemplate.Click += async (_, _) => await RunBusyAsync("Editing a message template…", () => EditTemplateAsync(false), this);
        editTemplate.Click += async (_, _) => await RunBusyAsync("Editing a message template…", () => EditTemplateAsync(true), this);
        // Fixed-width, left-aligned actions avoid the shared three-column
        // button row shrinking/ellipsizing their actual status text.
        var modeActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false, AutoScroll = true, Margin = Padding.Empty
        };
        modeActions.Controls.Add(enableFreeWhatsAppButton);
        modeActions.Controls.Add(pauseWhatsAppButton);
        var optOut = new ModernButton
        {
            Text = "Record Phone Opt-Out", ThemeRole = "Secondary",
            Width = AppScale.Scale(190), Height = AppMetrics.ButtonHeight,
            Enabled = CurrentUser.HasPermission("Communication.Configure")
        };
        optOut.Click += async (_, _) => await RunBusyAsync("Recording a WhatsApp opt-out.", RecordOptOutAsync, this);
        modeActions.Controls.Add(optOut);
        tables.Controls.Add(modeActions, 0, 4);
        var templateActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false, AutoScroll = true, Margin = Padding.Empty
        };
        templateActions.Controls.Add(addTemplate);
        templateActions.Controls.Add(editTemplate);
        tables.Controls.Add(templateActions, 0, 5);
        // Settings can be shorter than these data grids and action rows on
        // ordinary laptop windows. Keep the action sizes usable, but make
        // the entire surface scrollable instead of clipping the final row.
        int requiredHeight = tables.Padding.Vertical + tables.RowStyles.Cast<RowStyle>()
            .Sum(row => (int)Math.Ceiling(row.Height));
        tables.Dock = DockStyle.Top;
        tables.Height = requiredHeight;
        var scroller = new Panel {
            Dock = DockStyle.Fill, AutoScroll = true,
            Padding = Padding.Empty, Margin = Padding.Empty
        };
        scroller.Controls.Add(tables);
        card.Content.Controls.Add(scroller);
        return card;
    }

    private async Task RecordOptOutAsync()
    {
        RequirePermission("Communication.Configure");
        using var dialog = new ApplicationWindowForm
        {
            Text = "Record WhatsApp Opt-Out", StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(AppScale.Scale(510), AppScale.Scale(175)),
            FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false
        };
        var phone = HumanUi.TextBox(false, 100);
        var save = new ModernButton { Text = "Block WhatsApp Messages", DialogResult = DialogResult.OK, ThemeRole = "Primary" };
        var cancel = new ModernButton { Text = "Cancel", DialogResult = DialogResult.Cancel };
        var actions = HumanUi.DialogButtons(save, cancel);
        actions.Dock = DockStyle.Bottom;
        var field = HumanUi.Field("Mobile number shared by all records to block", phone, true);
        field.Dock = DockStyle.Top;
        field.Height = AppMetrics.FieldRowHeight;
        dialog.Padding = new Padding(AppScale.Scale(16));
        dialog.Controls.Add(actions);
        dialog.Controls.Add(field);
        dialog.CancelButton = cancel;
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
        if (CommunicationRecipientResolver.NormalizeMobile(phone.Text) is not { } number)
        {
            Warn("Enter a valid Pakistan mobile number to record the opt-out.");
            return;
        }
        await CommunicationManagementStore.RecordWhatsAppOptOutAsync(number);
        if (!IsDisposed) SetStatus("WhatsApp opt-out recorded for the phone number.");
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
        catch (CommunicationValidationException ex) { if (!IsDisposed) Warn(ex.Message); }
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
        catch (CommunicationValidationException ex) { if (!IsDisposed) Warn(ex.Message); }
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
        bool customizeBuiltIn = id < 0;
        if (customizeBuiltIn) id = 0; // never alter the shipped examples
        using var dialog = new CommunicationTemplateDialog(
            selected is null ? "" : customizeBuiltIn
                ? "Custom " + (Convert.ToString(selected["Name"]) ?? "").Replace(" (built-in)", "")
                : Convert.ToString(selected["Name"]) ?? "",
            selected is null ? "WhatsApp" : Convert.ToString(selected["Channel"]) ?? "WhatsApp",
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
            if (!IsDisposed && CurrentUser.HasPermission("Communication.Send")) await LoadComposeTemplatesAsync();
            if (!IsDisposed) await LoadSettingsAsync();
        }
        catch (CommunicationValidationException ex) { if (!IsDisposed) Warn(ex.Message); }
        catch (Exception ex) { if (!IsDisposed) Fail(ex); }
    }

    private async Task LoadComposeTemplatesAsync()
    {
        composeTemplates.Clear();
        foreach (var builtIn in CommunicationPreparedMessages.BuiltIns.Select((item, index) => (item, index)))
            composeTemplates.Add(new TemplateChoice(-builtIn.index - 1, builtIn.item.Name,
                "WhatsApp", builtIn.item.Purpose, builtIn.item.Body));
        if (CurrentUser.HasPermission("Communication.Send"))
        {
            int actor = CurrentUser.UserId;
            using DataTable table = await CommunicationManagementStore.ReadComposeTemplatesAsync();
            if (IsDisposed || actor != CurrentUser.UserId) return;
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
        string purpose = CurrentTarget().Purpose;
        templateChoice.Items.Clear();
        templateChoice.Items.Add("Write your own message");
        foreach (TemplateChoice item in composeTemplates)
            if (item.Channel == ComposeChannel && item.Purpose == purpose)
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
            if (!table.Columns.Contains("StateText"))
                table.Columns.Add("StateText", typeof(string));
            foreach (DataRow row in table.Rows)
            {
                bool manual = Convert.ToString(row["Method"]) == "WhatsApp Business (manual)";
                string state = Convert.ToString(row["State"]) ?? "";
                row["StateText"] = manual && state == "Completed"
                    ? "Manual session complete (unverified)"
                    : manual && state == "Confirmed" ? "Manual session pending" : state;
            }
            ShowHistory(table, unfinished: false);
            SetStatus($"{table.Rows.Count} historical batches loaded. Operator-reported sent is not verified delivery.");
        }
        catch (CommunicationValidationException ex) { if (!IsDisposed) Warn(ex.Message); }
        catch (Exception ex) { if (!IsDisposed) Fail(ex); }
    }

    private async Task LoadUnfinishedAsync()
    {
        int actor = CurrentUser.UserId;
        int version = ++managementReadVersion;
        DataTable table = await CommunicationManagementStore.ReadMyUnfinishedBatchesAsync();
        if (IsDisposed || actor != CurrentUser.UserId || version != managementReadVersion)
        {
            table.Dispose();
            return;
        }
        ShowHistory(table, unfinished: true);
        SetStatus(table.Rows.Count == 0 ? "You have no unfinished WhatsApp batches."
            : "Select one of your unfinished batches to resume.");
    }

    private void ShowHistory(DataTable table, bool unfinished)
    {
        showingUnfinished = unfinished;
        var old = historyGrid.DataSource as DataTable;
        historyGrid.DataSource = table;
        old?.Dispose();
        foreach (string column in new[] { "OperatorName", "StateText", "ManualReported", "ManualSkipped" })
            historyGrid.Columns[column].Visible = !unfinished;
        cards[3].SetHeaderText(unfinished ? "My Unfinished WhatsApp Batches" : "History",
            "WhatsApp reports are recorded by the operator. Delivery is unverified.");
        SetStep(3);
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
            PrepareSettingsForDisplay(channels);
            // Built-in formats are always available; choosing Edit Selected
            // creates a custom copy instead of changing shipped examples.
            foreach (var builtIn in CommunicationPreparedMessages.BuiltIns.Select((item, index) => (item, index)))
            {
                DataRow row = templates.NewRow();
                row["TemplateId"] = -builtIn.index - 1;
                row["Name"] = builtIn.item.Name + " (built-in)";
                row["Channel"] = "WhatsApp";
                row["Purpose"] = builtIn.item.Purpose;
                row["Body"] = builtIn.item.Body;
                row["Active"] = true;
                templates.Rows.InsertAt(row, builtIn.index);
            }
            var oldChannels = settingsGrid.DataSource as DataTable;
            var oldTemplates = templateGrid.DataSource as DataTable;
            settingsGrid.DataSource = null;
            templateGrid.DataSource = null;
            settingsGrid.DataSource = channels;
            templateGrid.DataSource = templates;
            oldChannels?.Dispose(); oldTemplates?.Dispose();
            SetStep(4);
            SetStatus("Manual WhatsApp requires you to press Send in WhatsApp. School email and API delivery are unavailable.");
        }
        catch (CommunicationValidationException ex) { if (!IsDisposed) Warn(ex.Message); }
        catch (Exception ex) { if (!IsDisposed) Fail(ex); }
    }

    // UI-only presentation: SQL remains the authority for actual mode and permissions.
    // Keep raw settings untouched while displaying plain-language statuses.
    private void PrepareSettingsForDisplay(DataTable channels)
    {
        if (!channels.Columns.Contains("SendingMethod"))
            channels.Columns.Add("SendingMethod", typeof(string));
        if (!channels.Columns.Contains("StatusText"))
            channels.Columns.Add("StatusText", typeof(string));

        bool whatsappActive = false, whatsappManual = false;
        bool smsActive = false;
        foreach (DataRow row in channels.Rows)
        {
            string name = Convert.ToString(row["Channel"]) ?? "";
            string provider = Convert.ToString(row["ProviderCode"]) ?? "";
            bool enabled = Convert.ToBoolean(row["Enabled"]);
            row["SendingMethod"] = (name, provider) switch
            {
                ("WhatsApp", "WhatsAppBusinessManual") => "WhatsApp Business — manual",
                ("WhatsApp", "MetaCloud") => "WhatsApp Cloud API — future",
                ("WhatsApp", "Twilio") => "Twilio WhatsApp — future",
                ("Sms", "Twilio") => "Twilio SMS — unavailable",
                ("Email", "Smtp") => "School email — future",
                ("Email", "EmailClientManual") => "Email app — future",
                _ => "Not configured"
            };
            row["StatusText"] = name == "Email" ? "Not available yet"
                : enabled && name == "WhatsApp" && provider == "WhatsAppBusinessManual"
                    ? "Ready for manual chats"
                    : enabled ? "Check provider (not approved)" : "Paused / unavailable";
            if (name == "WhatsApp")
            {
                whatsappActive = enabled;
                whatsappManual = provider == "WhatsAppBusinessManual";
            }
            if (name == "Sms") smsActive = enabled;
        }
        enableFreeWhatsAppButton.Text = whatsappActive && whatsappManual
            ? "Free WhatsApp Active"
            : whatsappManual ? "Resume Free WhatsApp" : "Use Free WhatsApp";
        enableFreeWhatsAppButton.Width = AppScale.Scale(245);
        enableFreeWhatsAppButton.Enabled = CurrentUser.HasPermission("Communication.Configure")
            && !(whatsappActive && whatsappManual);
        pauseWhatsAppButton.Enabled = CurrentUser.HasPermission("Communication.Configure")
            && whatsappActive;
        pauseSmsButton.Enabled = CurrentUser.HasPermission("Communication.Configure")
            && smsActive;
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
        catch (CommunicationValidationException ex) { if (!IsDisposed) Warn(ex.Message); }
        catch (Exception ex) { if (!IsDisposed) Fail(ex); }
    }

    private void ChangeAudienceFields()
    {
        var target = (CommunicationAudience)Math.Max(audience.SelectedIndex, 0);
        bool showCampus = target is CommunicationAudience.Campus
            or CommunicationAudience.Class or CommunicationAudience.OutstandingFees;
        bool showClass = target is CommunicationAudience.Class
            or CommunicationAudience.OutstandingFees;
        bool showStudent = target is CommunicationAudience.Student or CommunicationAudience.Family or CommunicationAudience.OutstandingFees;
        bool showStaff = target == CommunicationAudience.Staff;
        targetFieldsLayout.SuspendLayout();
        campusField.Visible = showCampus;
        classField.Visible = showClass;
        studentField.Visible = showStudent;
        staffField.Visible = showStaff;
        // Hidden rows must not reserve empty vertical space; table row
        // positioning remains stable even after switching audiences.
        int rowHeight = AppScale.Scale(72);
        targetFieldsLayout.RowStyles[1].Height = showCampus ? rowHeight : 0;
        targetFieldsLayout.RowStyles[2].Height = showClass ? rowHeight : 0;
        targetFieldsLayout.RowStyles[3].Height = showStudent ? rowHeight : 0;
        targetFieldsLayout.RowStyles[4].Height = showStaff ? rowHeight : 0;
        targetFieldsLayout.ResumeLayout(true);
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

    private CommunicationTarget CurrentTarget()
    {
        var selected = (CommunicationAudience)Math.Max(audience.SelectedIndex, 0);
        bool scoped = selected is CommunicationAudience.Campus or CommunicationAudience.Class or CommunicationAudience.OutstandingFees;
        return new(selected, scoped ? campus.SelectedId : 0,
            selected is CommunicationAudience.Class or CommunicationAudience.OutstandingFees ? cls.SelectedId : 0,
            selected is CommunicationAudience.Student or CommunicationAudience.Family or CommunicationAudience.OutstandingFees ? student.SelectedId : 0,
            selected == CommunicationAudience.Staff ? staff.SelectedId : 0, ComposeChannel);
    }

    private void SetStep(int number)
    {
        step = Math.Clamp(number, 0, 4);
        for (int i = 0; i < cards.Count; i++) cards[i].Visible = i == step;
        cards[step].BringToFront();
        bool compose = CommunicationAccess.CanCompose(CurrentUser.HasPermission);
        previous.Visible = compose && step < 3;
        previous.Enabled = step is > 0 and < 3;
        next.Text = step == 0 ? "Preview Recipients" : "Review & Confirm";
        next.Visible = compose && step < 2;
        confirm.Visible = compose && step == 2;
        confirm.Text = manualModeAvailable ? "Open WhatsApp Chats" : "Sending Unavailable";
        confirm.Enabled = step == 2 && manualModeAvailable
            && CurrentUser.HasPermission("Communication.Send");
        newMessageButton.Visible = compose && step >= 3;
        // SMS cannot be activated in this release, so do not display a dead action.
        pauseSmsButton.Visible = false;
        if (step == 2)
            SetStatus(manualModeAvailable
                ? "Free/manual WhatsApp: open each chat and press Send yourself. Not verified delivery."
                : "Manual WhatsApp is paused or disabled. A communication administrator can enable it in Settings.",
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
            if (!operations.ConfirmStart()) return;
            int actor = CurrentUser.UserId;
            Guid request = manualRequestId;
            CommunicationConfirmation saved = await operations.ConfirmBatch(target, reviewed, body, request);
            if (IsDisposed) return;
            if (actor != CurrentUser.UserId)
                throw new InvalidOperationException("Signed-in user changed. Reopen History.");
            if (request != manualRequestId || target != CurrentTarget() || body != message.Text)
            {
                Warn("The reviewed batch was saved, but the current message changed. Review that batch in History; no chat was opened.");
                return;
            }
            operations.OpenHandoff(saved.BatchId);
            if (!IsDisposed && CurrentUser.HasPermission("Communication.View"))
                await LoadHistoryAsync();
            else if (!IsDisposed) await LoadUnfinishedAsync();
        }
        catch (CommunicationValidationException ex) { if (!IsDisposed) Warn(ex.Message); }
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
            || Convert.ToString(row["Method"]) != "WhatsApp Business (manual)"
            || Convert.ToInt64(row["InProgress"]) == 0)
        {
            Warn("Choose an unfinished WhatsApp batch to resume.");
            return;
        }
        try
        {
            var batchId = (Guid)row["BatchId"];
            using (var handoff = new CommunicationManualHandoffDialog(batchId))
                handoff.ShowDialog(FindForm());
            if (!IsDisposed)
            {
                if (showingUnfinished) await LoadUnfinishedAsync();
                else await LoadHistoryAsync();
            }
        }
        catch (CommunicationValidationException ex) { if (!IsDisposed) Warn(ex.Message); }
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
                if (string.IsNullOrWhiteSpace(message.Text))
                {
                    Warn("Write or choose a prepared message before previewing.");
                    return;
                }
                await LoadPreviewAsync();
            }
            else if (step == 1)
            {
                RequirePermission("Communication.Send");
                if (preview is null || previewTarget != CurrentTarget()
                    || previewMessage != message.Text)
                {
                    Warn("The message or target changed. Refresh the preview.");
                    SetStep(0);
                    return;
                }
                if (preview.Included == 0)
                {
                    Warn("No eligible destinations. Review Admission consent and phone numbers.");
                    return;
                }
                manualModeAvailable = await operations.ReadManualMode();
                if (IsDisposed || previewTarget != CurrentTarget()
                    || previewMessage != message.Text) return;
                if (manualModeAvailable && manualRequestId == Guid.Empty)
                    manualRequestId = Guid.NewGuid();
                string example = CommunicationPreparedMessages.Render(previewMessage!,
                    CommunicationPreparedMessages.FromPreview(preview.Entries.First(e => e.Included)));
                confirmation.Text = "Send to: " + AudienceDescription(previewTarget)
                    + Environment.NewLine + "Channel: WhatsApp Business (manual)"
                    + Environment.NewLine + "Eligible destinations: " + preview.Included
                    + Environment.NewLine + "School records reviewed: " + preview.Entries.Count
                    + Environment.NewLine + "Excluded (never sent): " + preview.Excluded
                    + Environment.NewLine + Environment.NewLine + "Reviewed message format:"
                    + Environment.NewLine + previewMessage
                    + Environment.NewLine + Environment.NewLine + "Example for an eligible recipient:"
                    + Environment.NewLine + example
                    + Environment.NewLine + Environment.NewLine
                    + (manualModeAvailable
                        ? "Press Send yourself in WhatsApp. Prepared fields are refreshed from live school records when each chat opens. " +
                          "You must report the action; FMSC cannot verify delivery."
                        : "A communication administrator can enable manual WhatsApp in Settings.");
                confirmation.SelectionStart = 0;
                confirmation.SelectionLength = 0;
                SetStep(2);
            }
        }
        catch (CommunicationValidationException ex) { if (!IsDisposed) Warn(ex.Message); }
        catch (Exception ex) { if (!IsDisposed) Fail(ex); }
    }

    private string AudienceDescription(CommunicationTarget target)
    {
        var scope = new List<string> { audience.Text };
        if (target.CampusId > 0) scope.Add(campus.Text);
        if (target.ClassId > 0) scope.Add(cls.Text);
        if (target.StudentId > 0) scope.Add(student.Text);
        if (target.StaffId > 0) scope.Add(staff.Text);
        return string.Join(" · ", scope);
    }

    private async Task LoadPreviewAsync()
    {
        CommunicationTarget target = CurrentTarget();
        CommunicationRecipientResolver.Validate(target);
        int version = ++previewReadVersion;
        string capturedText = message.Text;
        CommunicationPreparedMessages.ValidateFormat(capturedText, target.Purpose);
        CommunicationPreview result = await operations.ReadRecipients(target);
        if (IsDisposed || version != previewReadVersion
            || target != CurrentTarget() || capturedText != message.Text)
            return;
        ApplyPreview(target, capturedText, result);
    }

    internal void ApplyPreview(CommunicationTarget target, string capturedText, CommunicationPreview result)
    {
        // Refuse a prepared format if even one eligible recipient lacks a
        // requested field. Do not create a partial or misleading bulk batch.
        foreach (var entry in result.Entries.Where(item => item.Included))
            _ = CommunicationPreparedMessages.Render(capturedText,
                CommunicationPreparedMessages.FromPreview(entry));
        preview = result;
        previewTarget = target;
        previewMessage = capturedText;
        showExcluded.Checked = false;
        BindPreviewRows();
        summary.Text = "Eligible destinations: " + result.Included
            + "   |   Excluded: " + result.Excluded
            + Environment.NewLine + "No consent recorded: " + result.NotRecorded
            + "   |   Declined: " + result.Declined
            + "   |   Invalid mobile: " + result.InvalidNumber
            + Environment.NewLine + "Opted out: " + result.Suppressed
            + "   |   Duplicate phone: " + result.Duplicates;
        ShowSelectedMessage();
        SetStep(1);
        SetStatus("Eligible recipients shown first. No message has been sent.");
    }

    private void BindPreviewRows()
    {
        if (preview is null) return;
        var table = new DataTable();
        table.Columns.Add("RecordId", typeof(int));
        table.Columns.Add("RecordName", typeof(string));
        table.Columns.Add("Kind", typeof(string));
        table.Columns.Add("Mobile", typeof(string));
        table.Columns.Add("Reason", typeof(string));
        foreach (var entry in preview.Entries
            .OrderByDescending(item => item.Included)
            .ThenBy(item => item.RecordName, StringComparer.CurrentCultureIgnoreCase))
        {
            if (!entry.Included && !showExcluded.Checked) continue;
            string maskedNumber = entry.PhoneE164 is { Length: >= 4 } number
                ? "•••• " + number[^4..] : "";
            table.Rows.Add(entry.RecordId, entry.RecordName, entry.Kind, maskedNumber, entry.Reason);
        }
        previewGrid.DataSource = null;
        previewTable?.Dispose();
        previewTable = table;
        previewGrid.DataSource = table;
    }

    private void ShowSelectedMessage()
    {
        if (preview is null || previewMessage is null) { sampleMessage.Text = ""; return; }
        var selected = previewGrid.CurrentRow?.DataBoundItem as DataRowView;
        int id = selected is not null ? Convert.ToInt32(selected["RecordId"]) : 0;
        var entry = selected is null ? preview.Entries.FirstOrDefault(row => row.Included)
            : preview.Entries.FirstOrDefault(row => row.RecordId == id);
        sampleMessage.Text = entry is null ? "No eligible recipient. No chat can be opened."
            : !entry.Included ? entry.RecordName + ": " + entry.Reason
            : "Message preview (" + entry.RecordName + "):" + Environment.NewLine
              + CommunicationPreparedMessages.Render(previewMessage, CommunicationPreparedMessages.FromPreview(entry))
              + Environment.NewLine + "Current school values are checked again when the chat opens. You press Send yourself.";
    }

    private async Task BindLookupsAsync()
    {
        try
        {
            int actor = CurrentUser.UserId;
            var campuses = DatabaseHelper.QueryUserAsync("GetCampuses");
            var classes = DatabaseHelper.QueryUserAsync("GetClasses");
            await Task.WhenAll(campuses, classes);
            using var campusRows = campuses.Result;
            using var classRows = classes.Result;
            if (IsDisposed || actor != CurrentUser.UserId) return;
            campus.Bind(campusRows, "CampusId", "CampusName", "All campuses");
            cls.Bind(classRows, "ClassId", "ClassName", "All classes");
        }
        catch (CommunicationValidationException ex) { if (!IsDisposed) Warn(ex.Message); }
        catch (Exception ex) { if (!IsDisposed) Fail(ex); }
    }
}
