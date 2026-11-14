using System.Data;
using FMSC_SMS.Core;

namespace FMSC_SMS.Pages.Communication;

/// <summary>
/// One audience, message and reviewed confirmation flow for manual WhatsApp,
/// approved Meta templates and SMS. Nothing sends merely by opening this page.
/// </summary>
public sealed class CommunicationCenterPage : V2Page, IBusyPage
{
    private readonly ModernComboBox audience = HumanUi.Combo();
    private readonly LookupBox campus = new(), cls = new();
    private readonly StudentPicker student = new();
    private readonly StaffPicker staff = new();
    private readonly ModernComboBox deliveryMethod = HumanUi.Combo();
    private readonly ModernTextBox historySearch = HumanUi.TextBox(false,100);
    private CommunicationMetaTransport.Selection? metaSelection;
    private bool IsManual => deliveryMethod.SelectedIndex == 0;
    private bool IsMeta => deliveryMethod.SelectedIndex == 1;
    private string ComposeChannel => deliveryMethod.SelectedIndex == 2 ? "Sms" : "WhatsApp";
    private string ApiProvider => IsMeta ? "MetaCloud" : "Twilio";
    private readonly ModernComboBox templateChoice = HumanUi.Combo();
    private readonly List<TemplateChoice> composeTemplates = new();

    private sealed record TemplateChoice(int Id,string Name,string Channel,string Purpose,string Body)
    {
        public override string ToString() => Name;
    }
    private readonly ModernTextBox message = HumanUi.TextBox(true, 1600);
    private readonly CheckBox showExcluded = new()
    {
        AutoSize = true, Dock = DockStyle.Top, ForeColor = AppTheme.Text
    };
    private readonly ModernTextBox sampleMessage = HumanUi.TextBox(true, 2400);
    private readonly Label summary = new()
    {
        Dock = DockStyle.Top, AutoSize = true, ForeColor = AppTheme.Text,
        Padding = new Padding(0, 0, 0, AppScale.Scale(6))
    };
    private readonly Label targetIssue = InlineMessage();
    private readonly Label messageIssue = InlineMessage();
    private readonly Label reviewNote = InlineMessage();
    private Label previewCaption = null!;

    private bool compactReview;
    private bool methodChanged;
    private readonly DataGridView previewGrid = HumanUi.Grid(false);
    private readonly DataGridView historyGrid = HumanUi.Grid(false);
    private readonly DataGridView settingsGrid = HumanUi.Grid(false);
    private readonly DataGridView templateGrid = HumanUi.Grid(false);
    private readonly Panel steps = new() { Dock = DockStyle.Fill };
    private readonly List<HumanCard> cards = new();
    private readonly Panel campusField, classField, studentField, staffField;
    private TableLayoutPanel targetFieldsLayout = null!;
    private readonly ModernButton next = PageButton("communicationReviewMessage", "Review Message", 165, "Primary");
    private readonly ModernButton confirm = PageButton("communicationConfirmMessage", "Open in WhatsApp", 230, "Primary");
    private readonly ModernButton historyButton, settingsButton, newMessageButton;
    // Compose and review share one workspace. Nothing is sent until the reviewed
    // snapshot is confirmed, just as in the former full-screen review step.
    private readonly TableLayoutPanel composerLayout = new()
    {
        Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty
    };
    private readonly CheckBox onlyUnfinished = new()
    {
        Text = "Only my unfinished", Dock = DockStyle.Top,
        Height = AppScale.Scale(35), ForeColor = AppTheme.Text,
        Padding = new Padding(AppScale.Scale(12), 0, 0, 0)
    };
    private bool showingUnfinished;
    private bool updatingHistoryFilter;
    // Built by SettingsCard during the page constructor; cannot be readonly.
    private ModernButton enableFreeWhatsAppButton = null!;
    private ModernButton configureProviderButton = null!, verifyProviderButton = null!, pauseApiButton = null!;
    private ModernButton blockNumberButton = null!, editTemplateButton = null!;
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
        Action<Guid, bool> OpenHandoff,
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
        : base("Communication Center", "")
    {
        this.operations = operations ?? new Operations(CommunicationRecipientResolver.ReadAsync,
            CommunicationManualHandoffStore.IsEnabledAsync,
            (target, preview, body, key) => CommunicationBatchStore.ConfirmPreviewAsync(target, preview, body, key, manualWhatsApp: true),
            (batch, openFirstChat) => {
                using var dialog = new CommunicationManualHandoffDialog(batch, openFirstChat: openFirstChat);
                dialog.ShowDialog(FindForm());
            },
            () => Confirm("Open the reviewed WhatsApp chats one at a time? "
                + "You will press Send in WhatsApp yourself. You can finish the remaining chats later.",
                "Start WhatsApp Chats", false));
        deliveryMethod.Name="communicationDeliveryMethod";
        deliveryMethod.Items.AddRange(new object[]{"WhatsApp Business · manual","WhatsApp · Meta API","SMS · Twilio"});
        deliveryMethod.SelectedIndex=0;
        sampleMessage.ReadOnly = true;
        sampleMessage.ScrollBars = ScrollBars.Vertical;
        sampleMessage.Dock = DockStyle.Fill;
        // SelectionChanged can fire before CurrentRow moves to the clicked cell.
        previewGrid.CurrentCellChanged += (_, _) => ShowSelectedMessage();
        audience.Items.AddRange(new object[]
        {
            "Entire school", "Campus", "Class", "Outstanding fees",
            "One student", "Family / siblings", "One staff member"
        });
        audience.SelectedIndex = (int)CommunicationAudience.Class;
        templateChoice.Items.Add("Write your own message");
        templateChoice.SelectedIndex = 0;
        foreach (var builtIn in CommunicationPreparedMessages.BuiltIns.Select((item, index) => (item, index)))
            composeTemplates.Add(new TemplateChoice(-builtIn.index - 1, builtIn.item.Name,
                "WhatsApp", builtIn.item.Purpose, builtIn.item.Body));

        campusField = HumanUi.Field("Campus", campus);
        classField = HumanUi.Field("Class", cls);
        studentField = HumanUi.Field("Student", student);
        staffField = HumanUi.Field("Staff member", staff);

        cards.Add(TargetCard());
        cards.Add(PreviewCard());
        cards.Add(HistoryCard());
        cards.Add(SettingsCard());
        foreach (var card in cards) card.Dock = DockStyle.Fill;
        composerLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        composerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        composerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 0));
        composerLayout.Controls.Add(cards[0], 0, 0);
        composerLayout.Controls.Add(cards[1], 1, 0);
        steps.Controls.Add(composerLayout);
        steps.Controls.Add(cards[2]);
        steps.Controls.Add(cards[3]);
        next.Click += async (_, _) =>
        {
            if (ValidateDraft())
                await RunBusyAsync(BusyMessage, ContinueAsync, this);
        };
        confirm.Click += async (_, _) => await RunBusyAsync(
            "Confirming the reviewed message…", StartReviewedMessageAsync, this);
        newMessageButton = AddAction("New Message", (_, _) =>
        {
            if (IsBusy || step <= 1) return;
            bool returningFromSettings = step == 3;
            if (returningFromSettings) InvalidatePreview();
            SetStep(preview is null ? 0 : 1);
            if (returningFromSettings && initialize && !methodChanged)
                Defer(SelectReadyMethodAsync);
        }, "TabActive");
        historyButton = AddAction("History", async (_, _) =>
        {
            if (step == 2) return;
            await RunBusyAsync("Reading communication history.", () => CurrentUser.HasPermission("Communication.View")
                ? LoadHistoryAsync() : LoadUnfinishedAsync(), this);
        }, "TabInactive");
        settingsButton = AddAction("Settings", async (_, _) =>
        {
            if (step != 3) await RunBusyAsync("Reading communication settings.", LoadSettingsAsync, this);
        }, "TabInactive");
        newMessageButton.Name = "communicationNewMessage";
        historyButton.Name = "communicationHistory";
        settingsButton.Name = "communicationSettings";
        GateAction(settingsButton, "Communication.Configure");
        SetContent(steps);

        deliveryMethod.SelectionChangeCommitted += (_, _) => methodChanged = true;
        deliveryMethod.SelectedIndexChanged += (_, _) =>
        {
            if (metaSelection is not null || IsMeta) message.Clear();
            metaSelection = null;
            message.ReadOnly = IsMeta;
            RefreshTemplateChoices();
            InvalidatePreview();
        };
        audience.SelectedIndexChanged += (_, _) => {
            if(IsMeta){metaSelection=null;message.Clear();}
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
        templateChoice.SelectedIndexChanged += async (_, _) =>
        {
            if(IsMeta && templateChoice.SelectedIndex==1)
            {
                await RunBusyAsync("Reading approved Meta templates…",ChooseMetaTemplateAsync,this);
                return;
            }
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
                if (initialTarget is null) Defer(SelectReadyMethodAsync);
            }
            else if (CurrentUser.HasPermission("Communication.Configure")) Defer(LoadSettingsAsync);
            else if (CurrentUser.HasPermission("Communication.View")) Defer(LoadHistoryAsync);
        }
        if (!compose) SetStep(CurrentUser.HasPermission("Communication.Configure") ? 3 : 2);
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
        var card = HumanUi.Card("New Message");
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        targetFieldsLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 7,
            Padding = new Padding(AppScale.Scale(8)), Margin = Padding.Empty
        };
        var fields = targetFieldsLayout;
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        for (int row = 0; row <= 2; row++)
            fields.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.FieldRowHeight));
        fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.FieldRowHeight));
        fields.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fields.Controls.Add(HumanUi.Field("Send to", audience, true), 0, 0);
        fields.Controls.Add(HumanUi.Field("Send using", deliveryMethod, true), 1, 0);
        fields.Controls.Add(campusField, 0, 1);
        fields.Controls.Add(classField, 1, 1);
        fields.Controls.Add(studentField, 0, 2);
        fields.Controls.Add(staffField, 1, 2);
        fields.Controls.Add(targetIssue, 0, 3);
        fields.SetColumnSpan(targetIssue, 2);
        fields.Controls.Add(HumanUi.Field("Quick message (optional)", templateChoice), 0, 4);
        var editor = HumanUi.Field("Your message", message, true);
        fields.Controls.Add(editor, 0, 5);
        fields.SetColumnSpan(editor, 2);
        fields.Controls.Add(messageIssue, 0, 6);
        fields.SetColumnSpan(messageIssue, 2);
        fields.SizeChanged += (_, _) =>
        {
            int width = Math.Max(1, fields.ClientSize.Width - fields.Padding.Horizontal);
            targetIssue.MaximumSize = messageIssue.MaximumSize = new Size(width, 0);
        };
        scroll.Controls.Add(fields);
        card.Content.Controls.Add(scroll);
        card.Content.Controls.Add(ActionFooter(next));
        return card;
    }

    private HumanCard PreviewCard()
    {
        var card = HumanUi.Card("Review Message");
        previewGrid.AutoGenerateColumns = false;
        foreach (var (field, name, size) in new[]
        {
            ("RecordName", "School record", 105),
            ("Mobile", "Number ending", 85),
            ("Reason", "Eligibility", 105)
        })
            previewGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = field, DataPropertyName = field, HeaderText = name,
                MinimumWidth = size, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                SortMode = DataGridViewColumnSortMode.NotSortable
            });
        showExcluded.CheckedChanged += (_, _) => BindPreviewRows();
        previewCaption = HumanUi.GroupHeading("Message preview");
        previewCaption.Dock = DockStyle.Top;
        previewCaption.Height = HumanUi.GroupHeadingHeight;
        var previewMessagePane = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        previewMessagePane.Controls.Add(sampleMessage);
        previewMessagePane.Controls.Add(previewCaption);
        var details = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Margin = Padding.Empty
        };
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        details.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        details.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        details.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        details.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        details.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        details.Controls.Add(summary, 0, 0);
        details.Controls.Add(showExcluded, 0, 1);
        details.Controls.Add(previewGrid, 0, 2);
        details.Controls.Add(previewMessagePane, 0, 3);
        details.Controls.Add(reviewNote, 0, 4);
        details.SizeChanged += (_, _) =>
        {
            var max = new Size(Math.Max(1, details.ClientSize.Width - AppScale.Scale(8)), 0);
            summary.MaximumSize = reviewNote.MaximumSize = max;
        };
        previewGrid.VisibleChanged += (_, _) => details.RowStyles[2].Height = previewGrid.Visible ? 55 : 0;
        card.Content.Controls.Add(details);
        card.Content.Controls.Add(ActionFooter(confirm));
        return card;
    }

    private static Label InlineMessage() => new()
    {
        Dock = DockStyle.Top, AutoSize = true, UseMnemonic = false,
        ForeColor = AppTheme.Danger, Margin = Padding.Empty
    };

    private static ModernButton PageButton(string name, string text, int width, string role = "Secondary") => new()
    {
        Name = name, Text = text, ThemeRole = role,
        Width = AppScale.Scale(width), Height = AppMetrics.ButtonHeight
    };

    private static FlowLayoutPanel ActionFooter(ModernButton button)
    {
        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = AppMetrics.ButtonHeight + AppScale.Scale(10),
            FlowDirection = FlowDirection.RightToLeft, WrapContents = false
        };
        footer.Controls.Add(button);
        return footer;
    }


    private HumanCard HistoryCard()
    {
        var card = HumanUi.Card("History", "WhatsApp sent counts are reported by the operator.");
        historyGrid.AutoGenerateColumns = false;
        foreach (var (field, caption, width) in new[]
        {
            ("CreatedUtc", "Created (UTC)", 155),
            ("MethodText", "Method", 155),
            ("Audience", "Audience", 100),
            ("OperatorName", "Started by", 100),
            ("StateText", "Outcome", 235),
            ("InProgress", "Pending / unresolved", 115)

        })
            historyGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = field, DataPropertyName = field, HeaderText = caption,
                MinimumWidth = width, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                SortMode = DataGridViewColumnSortMode.NotSortable
            });
        var resume = PageButton("communicationOpenSession", "Open Selected Session", 225);
        resume.Enabled = false;
        historyGrid.CurrentCellChanged += (_, _) =>
            resume.Enabled = CurrentUser.HasPermission("Communication.Send")
                && historyGrid.CurrentRow?.DataBoundItem is DataRowView;
        resume.Click += async (_, _) => await RunBusyAsync("Opening the saved communication session…", ResumeManualFromHistoryAsync, this);
        var footer = ActionFooter(resume);
        onlyUnfinished.Visible = CurrentUser.HasPermission("Communication.Send")
            && CurrentUser.HasPermission("Communication.View");
        onlyUnfinished.CheckedChanged += async (_, _) =>
        {
            if (IsDisposed || updatingHistoryFilter || IsBusy || step != 2) return;
            await RunBusyAsync("Reading communication history…", async () =>
            {
                if (onlyUnfinished.Checked) await LoadUnfinishedAsync();
                else await LoadHistoryAsync();
            }, this);
        };
        historySearch.Name = "communicationHistorySearch";
        var searchField = HumanUi.Field("Search history", historySearch);
        searchField.Height = AppMetrics.FieldRowHeight;
        searchField.Dock = DockStyle.Top;
        historySearch.TextChanged+=(_,_)=>FilterHistory();
        card.Content.Controls.Add(historyGrid);
        card.Content.Controls.Add(onlyUnfinished);
        card.Content.Controls.Add(searchField);
        card.Content.Controls.Add(footer);
        return card;
    }

    private HumanCard SettingsCard()
    {
        var card = HumanUi.Card("Settings");
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
            Dock = DockStyle.Top, ColumnCount = 1, RowCount = 6,
            Padding = new Padding(AppScale.Scale(8)), Margin = Padding.Empty
        };
        tables.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        tables.RowStyles.Add(new RowStyle(SizeType.Absolute, HumanUi.GroupHeadingHeight));
        tables.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.GridHeaderHeight + 2 * AppMetrics.GridRowHeight + 4));
        tables.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        tables.RowStyles.Add(new RowStyle(SizeType.Absolute, HumanUi.GroupHeadingHeight + AppScale.Scale(8)));
        tables.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(224)));
        tables.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        tables.Controls.Add(HumanUi.GroupHeading("Sending methods"), 0, 0);
        tables.Controls.Add(settingsGrid, 0, 1);

        ModernButton SettingsAction(string name, string text, int width, Func<Task> action)
        {
            var button = PageButton(name, text, width);
            button.Click += async (_, _) => await RunBusyAsync("Updating communication settings.", action, this);
            return button;
        }
        enableFreeWhatsAppButton = SettingsAction("communicationManualWhatsApp", "Use Free WhatsApp", 205,
            () => SelectedChannel?["ProviderCode"] as string == "WhatsAppBusinessManual"
                && SelectedChannel.Row.Field<bool>("Enabled") ? PauseChannelAsync("WhatsApp") : EnableFreeWhatsAppAsync());
        configureProviderButton = SettingsAction("communicationConfigureProvider", "Configure API", 215, ConfigureProviderAsync);
        verifyProviderButton = SettingsAction("communicationVerifyProvider", "Verify & Enable API", 245, VerifyAndEnableApiAsync);
        pauseApiButton = SettingsAction("communicationPauseProvider", "Pause API", 190, () =>
            SelectedChannel is { } row ? PauseChannelAsync(row.Row.Field<string>("Channel")!) : Task.CompletedTask);
        blockNumberButton = SettingsAction("communicationBlockNumber", "Block a Number", 205, RecordOptOutAsync);
        var modeActions = SettingsActions(enableFreeWhatsAppButton, configureProviderButton, verifyProviderButton,
            pauseApiButton, blockNumberButton);
        tables.Controls.Add(modeActions, 0, 2);

        tables.Controls.Add(HumanUi.GroupHeading("Prepared messages"), 0, 3);
        tables.Controls.Add(templateGrid, 0, 4);
        var addTemplate = SettingsAction("communicationAddTemplate", "New Template", 150, () => EditTemplateAsync(false));
        addTemplate.Enabled = CurrentUser.HasPermission("Communication.Configure");
        editTemplateButton = SettingsAction("communicationEditTemplate", "Edit Template", 200, () => EditTemplateAsync(true));
        tables.Controls.Add(SettingsActions(addTemplate, editTemplateButton), 0, 5);

        settingsGrid.CurrentCellChanged += (_, _) => UpdateChannelActions();
        templateGrid.CurrentCellChanged += (_, _) => UpdateTemplateAction();
        settingsGrid.CellFormatting += (_, e) =>
        {
            if (settingsGrid.Columns[e.ColumnIndex].Name != "Channel" || e.Value is not string channel) return;
            e.Value = ChannelLabel(channel);
            e.FormattingApplied = true;
        };
        templateGrid.CellFormatting += (_, e) =>
        {
            if (templateGrid.Columns[e.ColumnIndex].Name == "Active" && e.Value is bool active)
            {
                e.Value = active ? "Yes" : "No";
                e.FormattingApplied = true;
            }
            else if (templateGrid.Columns[e.ColumnIndex].Name == "Purpose" && e.Value is string purpose)
            {
                e.Value = CommunicationTemplateDialog.PurposeLabel(purpose);
                e.FormattingApplied = true;
            }
            else if (templateGrid.Columns[e.ColumnIndex].Name == "Channel" && e.Value is string channel)
            {
                e.Value = ChannelLabel(channel);
                e.FormattingApplied = true;
            }
        };
        UpdateChannelActions();
        UpdateTemplateAction();
        tables.AutoSize = true;
        tables.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        var scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        scroller.Controls.Add(tables);
        card.Content.Controls.Add(scroller);
        return card;
    }

    private static FlowLayoutPanel SettingsActions(params ModernButton[] buttons)
    {
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true, Margin = Padding.Empty
        };
        actions.Controls.AddRange(buttons);
        return actions;
    }

    private DataRowView? SelectedChannel => settingsGrid.CurrentRow?.DataBoundItem as DataRowView;
    private readonly HashSet<string> configuredApiChannels = new();

    private static string ChannelLabel(string channel) => channel == "Sms" ? "SMS" : channel;

    private void UpdateChannelActions()
    {
        string? channel = SelectedChannel?.Row.Field<string>("Channel");
        string? provider = SelectedChannel?.Row.Field<string>("ProviderCode");
        bool canConfigure = CurrentUser.HasPermission("Communication.Configure");
        bool whatsapp = channel == "WhatsApp";
        bool sms = channel == "Sms";
        bool enabled = SelectedChannel?.Row.Field<bool>("Enabled") == true;
        bool manualProvider = provider == "WhatsAppBusinessManual";
        bool apiEnabled = enabled && !manualProvider;
        string api = whatsapp ? "Meta" : "Twilio";
        enableFreeWhatsAppButton.Visible = whatsapp;
        enableFreeWhatsAppButton.Enabled = canConfigure && whatsapp;
        enableFreeWhatsAppButton.Text = !manualProvider ? "Use Free WhatsApp"
            : enabled ? "Pause WhatsApp" : "Resume WhatsApp";
        configureProviderButton.Text = $"Configure {api}";
        verifyProviderButton.Text = $"Verify & Enable {api}";
        pauseApiButton.Text = $"Pause {api}";
        blockNumberButton.Text = channel is null ? "Block a Number" : $"Block {ChannelLabel(channel)} Number";
        configureProviderButton.Enabled = blockNumberButton.Enabled = canConfigure && (whatsapp || sms);
        verifyProviderButton.Enabled = canConfigure && !enabled && channel is not null
            && provider == (whatsapp ? "MetaCloud" : "Twilio") && configuredApiChannels.Contains(channel);
        pauseApiButton.Visible = apiEnabled;
        pauseApiButton.Enabled = canConfigure && apiEnabled;
    }

    private void UpdateTemplateAction()
    {
        var selected = templateGrid.CurrentRow?.DataBoundItem as DataRowView;
        editTemplateButton.Enabled = CurrentUser.HasPermission("Communication.Configure") && selected is not null;
        editTemplateButton.Text = selected?.Row.Field<int>("TemplateId") < 0 ? "Customize Template" : "Edit Template";
        SetToolTip(editTemplateButton, selected?.Row.Field<string>("Name") ?? "");
    }

    private async Task RecordOptOutAsync()
    {
        RequirePermission("Communication.Configure");
        if (SelectedChannel?.Row.Field<string>("Channel") is not { } channel) return;
        string channelLabel = ChannelLabel(channel);
        using var dialog = new ApplicationWindowForm
        {
            Text = "Record " + channelLabel + " Opt-Out", StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(AppScale.Scale(510), AppScale.Scale(175)),
            FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false
        };
        var phone = HumanUi.TextBox(false, 100);
        var save = new ModernButton { Text = "Block " + channelLabel + " Messages", DialogResult = DialogResult.OK, ThemeRole = "Primary" };
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
        await CommunicationManagementStore.RecordChannelOptOutAsync(channel,number);
        if (!IsDisposed) SetStatus(channelLabel + " opt-out recorded for the phone number.");
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
        string? chosenChannel = SelectedChannel?.Row.Field<string>("Channel");
        if (chosenChannel is not ("Sms" or "WhatsApp")) return;
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
        if(IsMeta)
        {
            templateChoice.Items.Clear();
            templateChoice.Items.Add("Choose an approved Meta template");
            templateChoice.Items.Add("Browse approved templates…");
            templateChoice.SelectedIndex=0;
            return;
        }
        foreach (TemplateChoice item in composeTemplates)
            if ((item.Channel == ComposeChannel || item.Id < 0) && item.Purpose == purpose)
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
                row["StateText"] = manual
                    ? $"{row["ManualReported"]} reported sent · {row["ManualOpened"]} opened · {row["ManualSkipped"]} skipped / excluded (unverified)"
                    : $"{row["Accepted"]} accepted · {row["Delivered"]} delivered · {row["Failed"]} failed · {row["Unknown"]} unknown";
            }
            ShowHistory(table, unfinished: false);
            SetStatus("");
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
        SetStatus("");
    }

    private void FilterHistory()
    {
        if(historyGrid.DataSource is not DataTable table)return;
        // Filtering typed values avoids DataView expression escaping and wildcard surprises.
        string query=historySearch.Text.Trim();
        table.DefaultView.RowFilter="";
        historyGrid.Tag = table.Rows.Count > 0 && query.Length > 0 ? "No sessions match your search."
            : showingUnfinished ? "You have no unfinished message sessions." : "No message sessions yet.";
        historyGrid.Invalidate();
        if(query.Length==0)return;
        if(!table.Columns.Contains("SearchMatch"))table.Columns.Add("SearchMatch",typeof(bool));
        foreach(DataRow row in table.Rows)row["SearchMatch"]=new[]{"Audience","Method","MethodText","OperatorName","StateText","Channel"}
            .Where(table.Columns.Contains).Any(name=>Convert.ToString(row[name])?.Contains(query,StringComparison.CurrentCultureIgnoreCase)==true);
        table.DefaultView.RowFilter="SearchMatch = true";
    }

    private void ShowHistory(DataTable table, bool unfinished)
    {
        showingUnfinished = unfinished;
        updatingHistoryFilter = true;
        try { onlyUnfinished.Checked = unfinished; }
        finally { updatingHistoryFilter = false; }
        var old = historyGrid.DataSource as DataTable;
        if(!table.Columns.Contains("MethodText"))table.Columns.Add("MethodText",typeof(string));
        foreach(DataRow row in table.Rows)row["MethodText"]=Convert.ToString(row["Method"]) switch {
            "WhatsApp Business (manual)"=>"WhatsApp · manual", "MetaCloud"=>"WhatsApp · Meta API",
            "Twilio"=>Convert.ToString(row["Channel"])+" · Twilio", _=>Convert.ToString(row["Method"])
        };
        foreach (string column in new[] { "OperatorName", "StateText" })
            historyGrid.Columns[column].Visible = !unfinished;
        historyGrid.DataSource = table;
        old?.Dispose();
        cards[2].SetHeaderText("History",
            unfinished ? "Only your unfinished communication sessions are shown."
                       : "WhatsApp sent counts are reported by the operator.");
        FilterHistory();
        SetStep(2);
    }

    private async Task LoadSettingsAsync()
    {
        try
        {
            int actor = CurrentUser.UserId;
            int version = ++managementReadVersion;
            string? selectedChannel = SelectedChannel?.Row.Field<string>("Channel");
            var channelTask = CommunicationManagementStore.ReadSettingsAsync();
            var templateTask = CommunicationManagementStore.ReadTemplatesAsync();
            await Task.WhenAll(channelTask, templateTask);
            DataTable channels = channelTask.Result, templates = templateTask.Result;
            if (IsDisposed || actor != CurrentUser.UserId || version != managementReadVersion)
            {
                channels.Dispose(); templates.Dispose(); return;
            }
            PrepareSettingsForDisplay(channels);
            // Email has no active adapter; retain stored profiles without offering it as ready.
            foreach (DataRow unused in channels.Select("Channel = 'Email'"))
                unused.Delete();
            channels.AcceptChanges();
            configuredApiChannels.Clear();
            foreach (string channel in channels.AsEnumerable().Select(row => row.Field<string>("Channel")!))
            {
                try
                {
                    var local = CommunicationProviderCredentialStore.ReadMetadata(channel);
                    if (local.ProviderCode == (channel == "WhatsApp" ? "MetaCloud" : "Twilio"))
                        configuredApiChannels.Add(channel);
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException
                    or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException)
                { /* Configure remains available when the protected profile must be replaced. */ }
            }
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
            settingsGrid.CurrentCell = settingsGrid.Rows.Cast<DataGridViewRow>().FirstOrDefault(row =>
                (row.DataBoundItem as DataRowView)?.Row.Field<string>("Channel") == (selectedChannel ?? "WhatsApp"))
                ?.Cells[0] ?? settingsGrid.CurrentCell;
            UpdateChannelActions();
            UpdateTemplateAction();
            oldChannels?.Dispose(); oldTemplates?.Dispose();
            SetStep(3);
            SetStatus("");
        }
        catch (CommunicationValidationException ex) { if (!IsDisposed) Warn(ex.Message); }
        catch (Exception ex) { if (!IsDisposed) Fail(ex); }
    }

    // UI-only presentation: SQL remains the authority for actual mode and permissions.
    // Keep raw settings untouched while displaying plain-language statuses.
    private static void PrepareSettingsForDisplay(DataTable channels)
    {
        if (!channels.Columns.Contains("SendingMethod"))
            channels.Columns.Add("SendingMethod", typeof(string));
        if (!channels.Columns.Contains("StatusText"))
            channels.Columns.Add("StatusText", typeof(string));

        foreach (DataRow row in channels.Rows)
        {
            string name = Convert.ToString(row["Channel"]) ?? "";
            string provider = Convert.ToString(row["ProviderCode"]) ?? "";
            bool enabled = Convert.ToBoolean(row["Enabled"]);
            row["SendingMethod"] = (name, provider) switch
            {
                ("WhatsApp", "WhatsAppBusinessManual") => "WhatsApp Business — manual",
                ("WhatsApp", "MetaCloud") => "WhatsApp · Meta API",
                ("WhatsApp", "Twilio") => "Twilio WhatsApp — future",
                ("Sms", "Twilio") => "SMS · Twilio",
                ("Email", "Smtp") => "School email — future",
                ("Email", "EmailClientManual") => "Email app — future",
                _ => "Not configured"
            };
            row["StatusText"] = name == "Email" ? "Not available yet"
                : enabled && name == "WhatsApp" && provider == "WhatsAppBusinessManual"
                    ? "Ready for manual chats"
                    : enabled && ((name == "Sms" && provider == "Twilio") || (name == "WhatsApp" && provider == "MetaCloud"))
                        ? "Enabled · local profile required" : enabled ? "Unsupported method" : "Paused / unavailable";
        }
    }

    private async Task PauseChannelAsync(string name)
    {
        if (!Confirm("Pause " + ChannelLabel(name) + " school messaging? No queued message will be dispatched while paused.",
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
        SetFieldCaption(campusField, campus, "Campus", target is CommunicationAudience.Campus or CommunicationAudience.Class);
        SetFieldCaption(classField, cls, "Class", target == CommunicationAudience.Class);
        SetFieldCaption(studentField, student, target == CommunicationAudience.Family ? "Student in the family" : "Student",
            target is CommunicationAudience.Student or CommunicationAudience.Family);
        SetFieldCaption(staffField, staff, "Staff member", showStaff);
        UpdateLookupPrompt(campus, target == CommunicationAudience.OutstandingFees ? "All campuses" : "Select a campus");
        UpdateLookupPrompt(cls, target == CommunicationAudience.OutstandingFees ? "All classes" : "Select a class");
        targetFieldsLayout.RowStyles[1].Height = showCampus || showClass ? AppMetrics.FieldRowHeight : 0;
        targetFieldsLayout.RowStyles[2].Height = showStudent || showStaff ? AppMetrics.FieldRowHeight : 0;
        int minimumHeight = (int)targetFieldsLayout.RowStyles.Cast<RowStyle>()
            .Where(row => row.SizeType == SizeType.Absolute).Sum(row => row.Height);
        targetFieldsLayout.MinimumSize = new Size(0, minimumHeight + AppScale.Scale(180));
        targetFieldsLayout.ResumeLayout(true);
    }

    private static void SetFieldCaption(Panel field, Control control, string name, bool required)
    {
        field.Controls.OfType<Label>().Single().Text = required ? name + " *" : name;
        control.AccessibleName = name;
        control.AccessibleDescription = required ? "Required" : "";
    }

    private static void UpdateLookupPrompt(LookupBox lookup, string prompt)
    {
        if (lookup.DataSource is DataTable table && table.Rows.Count > 0)
            table.Rows[0][lookup.DisplayMember] = prompt;
    }

    private async Task SelectReadyMethodAsync()
    {
        if (!CurrentUser.HasPermission("Communication.Send") || methodChanged) return;
        int actor = CurrentUser.UserId;
        var readiness = await Task.WhenAll(
            operations.ReadManualMode(),
            CommunicationApiBatchStore.IsReadyAsync("WhatsApp", "MetaCloud"),
            CommunicationApiBatchStore.IsReadyAsync("Sms", "Twilio"));
        if (IsDisposed || actor != CurrentUser.UserId || methodChanged || step != 0) return;
        // Choosing Meta replaces the text with an approved template. Preserve a draft already being written.
        int ready = Array.IndexOf(readiness, true);
        if (readiness.Count(value => value) == 1 && (ready != 1 || message.Text.Length == 0))
            deliveryMethod.SelectedIndex = ready;
    }

    private bool ValidateDraft()
    {
        if (IsBusy || step != 0) return false;
        var target = CurrentTarget();
        (Control? Field, string Error) issue = target.Audience switch
        {
            CommunicationAudience.Campus or CommunicationAudience.Class when target.CampusId <= 0
                => (campus, "Select a campus."),
            CommunicationAudience.Class when target.ClassId <= 0 => (cls, "Select a class."),
            CommunicationAudience.Student or CommunicationAudience.Family when target.StudentId <= 0
                => (student, "Select a student."),
            CommunicationAudience.Staff when target.StaffId <= 0 => (staff, "Select a staff member."),
            _ => (null, "")
        };
        targetIssue.Text = issue.Error;
        messageIssue.Text = IsMeta && metaSelection is null ? "Choose an approved Meta template."
            : string.IsNullOrWhiteSpace(message.Text) ? "Write or choose a prepared message." : "";
        if (issue.Field is not null)
        {
            FormValidator.FocusInput(issue.Field);
            return false;
        }
        if (messageIssue.Text.Length == 0) return true;
        FormValidator.FocusInput(IsMeta ? templateChoice : message);
        return false;
    }

    private void InvalidatePreview()
    {
        targetIssue.Text = messageIssue.Text = "";
        preview = null;
        previewTarget = null;
        previewMessage = null;
        manualModeAvailable = false;
        manualRequestId = Guid.Empty;
        previewReadVersion++;
        // Editing the target or text returns to editable composition without a
        // Back button; stale preview and confirmation are immediately invalid.
        if (step == 1) SetStep(0);
    }

    private void ClearReviewedDraft()
    {
        InvalidatePreview();
        metaSelection = null;
        message.Clear();
        templateChoice.SelectedIndex = 0;
        audience.SelectedIndex = CurrentUser.HasPermission("Students.View")
            ? (int)CommunicationAudience.Class : (int)CommunicationAudience.Staff;
        if (campus.Items.Count > 0) campus.SelectedIndex = 0;
        if (cls.Items.Count > 0) cls.SelectedIndex = 0;
        student.SetStudent(0, "");
        staff.SetStaff(0, "");
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
        step = Math.Clamp(number, 0, cards.Count - 1);
        bool composing = step <= 1;
        // Review appears next to the editable draft rather than replacing it
        // with another full-screen wizard page.
        composerLayout.SuspendLayout();
        composerLayout.ColumnStyles[0].Width = step == 1 ? 48 : 100;
        composerLayout.ColumnStyles[1].Width = step == 1 ? 52 : 0;
        cards[0].Visible = composing;
        cards[1].Visible = step == 1;
        composerLayout.ResumeLayout(true);
        composerLayout.Visible = composing;
        cards[2].Visible = step == 2;
        cards[3].Visible = step == 3;
        (composing ? (Control)composerLayout : cards[step]).BringToFront();

        bool compose = CommunicationAccess.CanCompose(CurrentUser.HasPermission);
        next.Visible = compose && step == 0;
        confirm.Visible = compose && step == 1;
        confirm.Text = IsManual ? (preview?.Included == 1 ? "Open in WhatsApp" : "Start WhatsApp Chats") : "Confirm & Send";
        confirm.Enabled = step == 1 && manualModeAvailable && preview is { Included: > 0 }
            && CurrentUser.HasPermission("Communication.Send");
        historyButton.Visible = CurrentUser.HasPermission("Communication.View") || compose;
        settingsButton.Visible = CurrentUser.HasPermission("Communication.Configure");
        newMessageButton.Visible = compose;
        SetNavigationTab(newMessageButton, composing);
        SetNavigationTab(historyButton, step == 2);
        SetNavigationTab(settingsButton, step == 3);
    }

    private static void SetNavigationTab(ModernButton tab, bool active)
    {
        string role = active ? "TabActive" : "TabInactive";
        tab.Tag = role;
        tab.ThemeRole = role;
    }

    private async Task ChooseMetaTemplateAsync()
    {
        try
        {
            int actor=CurrentUser.UserId;
            var templates=await CommunicationApiBatchStore.ReadMetaTemplatesAsync();
            if(IsDisposed || !IsMeta || actor!=CurrentUser.UserId)return;
            using var dialog=new CommunicationMetaTemplateDialog(templates,CurrentTarget().Purpose);
            if(dialog.ShowDialog(FindForm())==DialogResult.OK && dialog.Selection is not null)
            {
                metaSelection=dialog.Selection;message.Text=metaSelection.Format();
            }
            templateChoice.SelectedIndex=0;
        }
        catch(CommunicationValidationException ex){if(!IsDisposed)Warn(ex.Message);}
        catch(Exception){if(!IsDisposed)Warn("Meta templates could not be read. Check the protected profile and account permissions in Settings.");}
    }

    private async Task VerifyAndEnableApiAsync()
    {
        RequirePermission("Communication.Configure");
        if(settingsGrid.CurrentRow?.DataBoundItem is not DataRowView selected)return;
        string channel=Convert.ToString(selected["Channel"])!;
        if (!Confirm("Verify the saved API account and enable " + ChannelLabel(channel)
            + "? Provider charges may apply when you later confirm a send.", "Enable API", false)) return;
        try
        {
            await CommunicationManagementStore.VerifyAndEnableAsync(channel);
            if(!IsDisposed)await LoadSettingsAsync();
        }
        catch(CommunicationValidationException ex){if(!IsDisposed)Warn(ex.Message);}
        catch(Exception){if(!IsDisposed)Warn("The provider could not be verified. Check the saved profile, internet connection and provider permissions; sending was not enabled by this action.");}
    }

    private async Task StartReviewedMessageAsync()
    {
        if(IsManual){await StartManualHandoffAsync();return;}
        try
        {
            var target=previewTarget;var reviewed=preview;string? body=previewMessage;
            if(!manualModeAvailable || target is null || reviewed is null || target!=CurrentTarget()
                || body!=message.Text || manualRequestId==Guid.Empty)
                throw new CommunicationValidationException("The review changed or the selected method is unavailable. Review recipients again.");
            if(!Confirm($"Send {reviewed.Included} reviewed message(s) using {deliveryMethod.Text}? Provider charges may apply. You can stop the remaining messages while sending.","Confirm Send",false))return;
            int actor=CurrentUser.UserId;Guid request=manualRequestId;
            var saved=await CommunicationApiBatchStore.ConfirmAsync(target,reviewed,body,request,ApiProvider,metaSelection);
            if(IsDisposed || actor!=CurrentUser.UserId)return;
            if(request!=manualRequestId || target!=CurrentTarget() || body!=message.Text)
                throw new CommunicationValidationException("The batch was saved, but the current review changed. Open it in History; nothing was sent.");
            using(var dialog=new CommunicationApiDispatchDialog(saved.BatchId,startConfirmed:!saved.AlreadyConfirmed))dialog.ShowDialog(FindForm());
            if (!IsDisposed)
            {
                ClearReviewedDraft();
                if (CurrentUser.HasPermission("Communication.View")) await LoadHistoryAsync();
                else await LoadUnfinishedAsync();
            }
        }
        catch(CommunicationValidationException ex){if(!IsDisposed)Warn(ex.Message);}
        catch(Exception ex){if(!IsDisposed)Fail(ex);}
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
            bool single = reviewed.Included == 1;
            bool individual = IsIndividualTarget(target);
            if ((!single || !individual) && !operations.ConfirmStart()) return;
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
            // The operator has reviewed and confirmed this batch. Open the first
            // chat now rather than requiring another redundant click. No message
            // is sent by FMSC; every WhatsApp send remains operator-controlled.
            // A previously confirmed batch is never auto-reopened on retry.
            operations.OpenHandoff(saved.BatchId, !saved.AlreadyConfirmed);
            if (!IsDisposed)
            {
                ClearReviewedDraft();
                if (CurrentUser.HasPermission("Communication.View")) await LoadHistoryAsync();
                else await LoadUnfinishedAsync();
            }
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
        if (historyGrid.CurrentRow?.DataBoundItem is not DataRowView row)
        {
            Warn("Choose a communication batch to open.");
            return;
        }
        try
        {
            var batchId = (Guid)row["BatchId"];
            if(Convert.ToString(row["Method"]) == "WhatsApp Business (manual)")
            {using var handoff = new CommunicationManualHandoffDialog(batchId);handoff.ShowDialog(FindForm());}
            else
            {using var api = new CommunicationApiDispatchDialog(batchId);api.ShowDialog(FindForm());}
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
            if (step != 0) return;
            CommunicationTarget target = CurrentTarget();
            CommunicationRecipientResolver.Validate(target);
            RequirePermission(target.Audience == CommunicationAudience.Staff
                ? "Staff.View" : "Students.View");
            if (target.Audience == CommunicationAudience.OutstandingFees)
                RequirePermission("Fees.View");

            await LoadPreviewAsync(target);
        }
        catch (CommunicationValidationException ex) { if (!IsDisposed) messageIssue.Text = ex.Message; }
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

    private static bool IsIndividualTarget(CommunicationTarget target) =>
        target.Audience is CommunicationAudience.Student or CommunicationAudience.Staff
        || (target.Audience == CommunicationAudience.OutstandingFees && target.StudentId > 0);

    private async Task LoadPreviewAsync(CommunicationTarget target)
    {
        int version = ++previewReadVersion;
        if(IsMeta && (metaSelection is null || metaSelection.Format()!=message.Text))
            throw new CommunicationValidationException("Choose an approved Meta template before reviewing.");
        string capturedText = message.Text;
        CommunicationPreparedMessages.ValidateFormat(capturedText, target.Purpose);
        CommunicationPreview result = await operations.ReadRecipients(target);
        if (IsDisposed || version != previewReadVersion
            || target != CurrentTarget() || capturedText != message.Text)
            return;
        bool enabled = result.Included > 0 && (IsManual ? await operations.ReadManualMode()
            : await CommunicationApiBatchStore.IsReadyAsync(target.Channel,ApiProvider));
        if (IsDisposed || version != previewReadVersion
            || target != CurrentTarget() || capturedText != message.Text)
            return;
        manualModeAvailable = enabled;
        if (enabled && manualRequestId == Guid.Empty) manualRequestId = Guid.NewGuid();
        ApplyPreview(target, capturedText, result);
    }

    internal void ApplyPreview(CommunicationTarget target, string capturedText, CommunicationPreview result)
    {
        // A bulk review cannot proceed with a format that fails for any eligible recipient.
        var eligible = result.Entries.Where(entry => entry.Included).ToArray();
        foreach (var entry in eligible)
            _ = CommunicationPreparedMessages.Render(capturedText,
                CommunicationPreparedMessages.FromPreview(entry));

        preview = result;
        previewTarget = target;
        previewMessage = capturedText;
        int excluded = result.Entries.Count - eligible.Length;
        compactReview = IsIndividualTarget(target) && eligible.Length == 1 && excluded == 0;
        string scope = "To: " + AudienceDescription(target);
        string counts = $"{eligible.Length:N0} eligible " + (eligible.Length == 1 ? "recipient" : "recipients");
        if (excluded > 0) counts += $" · {excluded:N0} excluded";
        if (compactReview && eligible[0].PhoneE164 is { Length: >= 4 } number)
            counts += " · Number ending in " + number[^4..];
        var reasons = result.Entries.Where(entry => !entry.Included).GroupBy(entry => entry.Reason)
            .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.CurrentCulture)
            .Select(group => $"{group.Count():N0} {group.Key.ToLowerInvariant()}");
        summary.Text = string.Join(Environment.NewLine, new[] { counts, scope, string.Join(" · ", reasons) }
            .Where(line => line.Length > 0));

        showExcluded.Checked = false;
        showExcluded.Visible = excluded > 0;
        showExcluded.Text = $"Show excluded records ({excluded:N0})";
        reviewNote.ForeColor = eligible.Length > 0 && manualModeAvailable ? AppTheme.MutedText : AppTheme.Danger;
        reviewNote.Text = eligible.Length == 0 ? "No eligible recipients for this message."
            : !manualModeAvailable ? "This sending method is paused or needs setup in Settings."
            : IsManual ? "Press Send in WhatsApp for each chat." : "";
        BindPreviewRows();
        SetStep(1);
    }

    private void BindPreviewRows()
    {
        if (preview is null) return;
        var table = new DataTable();
        table.Columns.Add("Entry", typeof(CommunicationPreviewEntry));
        table.Columns.Add("RecordName", typeof(string));
        table.Columns.Add("Mobile", typeof(string));
        table.Columns.Add("Reason", typeof(string));
        foreach (var entry in preview.Entries.Where(row => row.Included || showExcluded.Checked)
            .OrderByDescending(row => row.Included)
            .ThenBy(row => row.RecordName, StringComparer.CurrentCultureIgnoreCase))
        {
            string lastFour = entry.PhoneE164 is { Length: >= 4 } number ? number[^4..] : "";
            table.Rows.Add(entry, entry.RecordName, lastFour, entry.Reason);
        }
        previewGrid.DataSource = null;
        previewTable?.Dispose();
        previewTable = table;
        previewGrid.DataSource = table;
        previewGrid.Visible = !compactReview && table.Rows.Count > 0;
        ShowSelectedMessage();
    }

    private void ShowSelectedMessage()
    {
        if (preview is null || previewMessage is null) { sampleMessage.Text = ""; return; }
        var selected = previewGrid.CurrentRow?.DataBoundItem as DataRowView;
        var entry = selected?["Entry"] as CommunicationPreviewEntry;
        if (entry?.Included != true) entry = preview.Entries.FirstOrDefault(row => row.Included);
        bool personalized = CommunicationPreparedMessages.HasFields(previewMessage);
        previewCaption.Text = personalized && entry is not null ? "Message for " + entry.RecordName
            : personalized ? "Message format" : "Message preview";
        sampleMessage.Text = entry is null ? previewMessage
            : CommunicationPreparedMessages.Render(previewMessage, CommunicationPreparedMessages.FromPreview(entry));
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
            bool optional = CurrentTarget().Audience == CommunicationAudience.OutstandingFees;
            campus.Bind(campusRows, "CampusId", "CampusName", optional ? "All campuses" : "Select a campus");
            cls.Bind(classRows, "ClassId", "ClassName", optional ? "All classes" : "Select a class");
        }
        catch (CommunicationValidationException ex) { if (!IsDisposed) Warn(ex.Message); }
        catch (Exception ex) { if (!IsDisposed) Fail(ex); }
    }
}
