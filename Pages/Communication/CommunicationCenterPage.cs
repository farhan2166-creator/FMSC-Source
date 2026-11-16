using System.Data;
using FMSC_SMS.Core;

namespace FMSC_SMS.Pages.Communication;

/// <summary>
/// One audience, message and reviewed confirmation flow for manual WhatsApp,
/// approved Meta templates and SMS. Nothing sends merely by opening this page.
/// </summary>
public sealed partial class CommunicationCenterPage : V2Page, IBusyPage
{
    private readonly ModernComboBox audience = HumanUi.Combo();
    private readonly LookupBox campus = new(), cls = new();
    private readonly StudentPicker student = new();
    private readonly StaffPicker staff = new();
    private readonly ModernComboBox deliveryMethod = HumanUi.Combo();
    private CommunicationMetaTransport.Selection? metaSelection;
    private bool IsManual => deliveryMethod.SelectedIndex == 0;
    private bool IsMeta => deliveryMethod.SelectedIndex == 1;
    private string ComposeChannel => deliveryMethod.SelectedIndex == 2 ? "Sms" : "WhatsApp";
    private string ApiProvider => IsMeta ? "MetaCloud" : "Twilio";
    private readonly ModernComboBox templateChoice = HumanUi.Combo();
    private readonly ModernComboBox languageChoice = HumanUi.Combo();
    private bool languageChanged;
    private string CurrentLanguage => languageChoice.SelectedIndex == 1 ? "ur" : "en";
    private IReadOnlyList<CommunicationPreparedMessages.ComposeTemplate> composeTemplates = CommunicationPreparedMessages.BuiltInChoices;
    private readonly ModernTextBox message = HumanUi.TextBox(true, 1600);
    private readonly CheckBox showExcluded = new()
    {
        AutoSize = true, Dock = DockStyle.Top, ForeColor = AppTheme.Text
    };
    private readonly ModernTextBox sampleMessage = HumanUi.TextBox(true, 2400);
    // Live draft view is deliberately not a recipient-specific send preview.
    // Actual personalized text and current eligibility still require Review.
    private readonly ModernTextBox draftPreview = HumanUi.TextBox(true, 1600);
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
    private readonly DataGridView unfinishedGrid = HumanUi.Grid(false);
    private readonly Panel steps = new() { Dock = DockStyle.Fill };
    private readonly List<HumanCard> cards = new();
    private readonly Panel campusField, classField, studentField, staffField;
    private readonly ModernButton next = PageButton("communicationReviewMessage", "Review Message", 165, "Primary");
    private readonly ModernButton confirm = PageButton("communicationConfirmMessage", "Open in WhatsApp", 230, "Primary");
    private readonly ModernButton schedule = PageButton("communicationScheduleMessage", "Schedule…", 145);
    private readonly ModernButton historyButton, unfinishedButton, newMessageButton, templatesButton, configureButton;
    // Compose and review share one workspace. Nothing is sent until the reviewed
    // snapshot is confirmed, just as in the former full-screen review step.
    private readonly TableLayoutPanel composerLayout = new()
    {
        Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty
    };
    private CommunicationPreview? preview;
    private CommunicationTarget? previewTarget;
    private string? previewMessage;
    private DataTable? previewTable;
    private bool manualModeAvailable;
    private Guid manualRequestId = Guid.Empty;
    private int step;
    private int previewReadVersion;
    private const int HistoryStep = 2, UnfinishedStep = 3, AdministrationStep = 4;
    private sealed record AudienceOption(CommunicationAudience Scope, string Label, string Permission = "Students.View")
    {
        public override string ToString() => Label;
    }
    private static readonly AudienceOption[] AudienceOptions =
    {
        new(CommunicationAudience.Student, "One student"), new(CommunicationAudience.Family, "Family / siblings"),
        new(CommunicationAudience.Class, "Class"), new(CommunicationAudience.Campus, "Campus"),
        new(CommunicationAudience.School, "Entire school"), new(CommunicationAudience.OutstandingFees, "Outstanding fees"),
        new(CommunicationAudience.FeeStudent, "One student's fees"), new(CommunicationAudience.FeeFamily, "Family fee balance"),
        new(CommunicationAudience.Staff, "One staff member", "Staff.View")
    };
    internal CommunicationAudience SelectedAudience
    {
        get => (audience.SelectedItem as AudienceOption)?.Scope ?? CommunicationAudience.Class;
        set => audience.SelectedItem = audience.Items.OfType<AudienceOption>().FirstOrDefault(option => option.Scope == value);
    }

    internal sealed record Operations(
        Func<CommunicationTarget, Task<CommunicationPreview>> ReadRecipients,
        Func<Task<bool>> ReadManualMode,
        Func<CommunicationTarget, CommunicationPreview, string, Guid, Task<CommunicationConfirmation>> ConfirmBatch,
        Action<Guid, bool> OpenHandoff,
        Func<bool> ConfirmStart,
        Func<bool, Task<DataTable>>? ReadSessions = null,
        Func<CommunicationSessionRow, CommunicationHistoryDraft?>? ViewHistory = null);
    private readonly Operations operations;


    public bool IsBusy => BusyOperationRunning;
    public string BusyMessage => "School records are being checked for current recipient eligibility.";

    public CommunicationCenterPage() : this(true) { }

    // SQL-free construction supports synthetic Windows UI tests.
    internal CommunicationCenterPage(bool initialize) : this(initialize, null) { }
    internal CommunicationCenterPage(bool initialize, Operations? operations)
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
        draftPreview.Name = "communicationDraftPreview";
        draftPreview.ReadOnly = true;
        draftPreview.TabStop = false;
        draftPreview.ScrollBars = ScrollBars.Vertical;
        draftPreview.Text = "";
        sampleMessage.ReadOnly = true;
        sampleMessage.ScrollBars = ScrollBars.Vertical;
        sampleMessage.Dock = DockStyle.Fill;
        // SelectionChanged can fire before CurrentRow moves to the clicked cell.
        previewGrid.CurrentCellChanged += (_, _) => ShowSelectedMessage();
        audience.Items.AddRange(AudienceOptions.Where(option => CurrentUser.HasPermission(option.Permission)
            && ((option.Scope is not (CommunicationAudience.OutstandingFees or CommunicationAudience.FeeStudent or CommunicationAudience.FeeFamily)) || CurrentUser.HasPermission("Fees.View"))).ToArray());
        SelectedAudience = CurrentUser.HasPermission("Students.View") ? CommunicationAudience.Class : CommunicationAudience.Staff;
        languageChoice.Name = "communicationComposeLanguage";
        languageChoice.Items.AddRange(new object[] { "English", "اردو / Urdu" });
        languageChoice.SelectedIndex = 0;
        templateChoice.Items.Add("Write your own message");
        templateChoice.SelectedIndex = 0;

        campusField = HumanUi.Field("Campus", campus);
        classField = HumanUi.Field("Class", cls);
        studentField = HumanUi.Field("Student", student);
        staffField = HumanUi.Field("Staff member", staff);

        cards.Add(TargetCard());
        cards.Add(PreviewCard());
        cards.Add(SessionsCard(historyGrid, unfinished: false));
        cards.Add(SessionsCard(unfinishedGrid, unfinished: true));
        cards.Add(HumanUi.Card("Communication Administration"));
        foreach (var card in cards) card.Dock = DockStyle.Fill;
        composerLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        composerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        composerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 0));
        composerLayout.Controls.Add(cards[0], 0, 0);
        composerLayout.Controls.Add(cards[1], 1, 0);
        steps.Controls.Add(composerLayout);
        foreach (var card in cards.Skip(HistoryStep)) steps.Controls.Add(card);
        next.Click += async (_, _) =>
        {
            if (ValidateDraft())
                await RunBusyAsync(BusyMessage, ContinueAsync, this);
        };
        confirm.Click += async (_, _) => await RunBusyAsync(
            "Confirming the reviewed message…", StartReviewedMessageAsync, this);
        schedule.Click += async (_, _) => await RunBusyAsync(
            "Scheduling the reviewed message…", ScheduleReviewedMessageAsync, this);
        newMessageButton = PageButton("communicationNewMessage", "New Message", 140, "TabInactive");
        unfinishedButton = PageButton("communicationUnfinished", "Queue", 140, "TabInactive");
        historyButton = PageButton("communicationHistory", "History", 120, "TabInactive");
        templatesButton = PageButton("communicationTemplates", "Templates", 125);
        configureButton = PageButton("communicationConfigureSending", "Settings", 120);
        newMessageButton.Click += (_, _) =>
        {
            if (!IsBusy) SetStep(preview is null ? 0 : 1);
        };
        unfinishedButton.Click += async (_, _) => await NavigateSessionsAsync(unfinished: true);
        historyButton.Click += async (_, _) => await NavigateSessionsAsync(unfinished: false);
        templatesButton.Click += (_, _) => OpenTemplates();
        configureButton.Click += (_, _) => OpenSendingOptions();
        UseToolbar();
        SetContent(BuildWorkspace());
        deliveryMethod.SelectionChangeCommitted += (_, _) => methodChanged = true;
        languageChoice.SelectionChangeCommitted += (_, _) => languageChanged = true;
        languageChoice.SelectedIndexChanged += (_, _) =>
        {
            // Switching languages never silently translates or overwrites
            // existing message text; a template must be chosen explicitly.
            RefreshTemplateChoices();
            InvalidatePreview();
        };
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
            if (campus.Items.Count > 0) campus.SelectedIndex = 0;
            if (cls.Items.Count > 0) cls.SelectedIndex = 0;
            student.SetStudent(0, ""); staff.SetStaff(0, "");
            ChangeAudienceFields(); RefreshTemplateChoices(); InvalidatePreview();
        };
        campus.SelectedIndexChanged += (_, _) => {
            if (cls.Items.Count > 0) cls.SelectedIndex = 0;
            UpdateClassSelection();
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
            if (!IsMeta && templateChoice.SelectedItem is string action
                && action == "Search matching templates…")
            {
                templateChoice.SelectedIndex = 0;
                OpenComposerTemplateBrowser();
                return;
            }
            if (templateChoice.SelectedItem is CommunicationPreparedMessages.ComposeTemplate selected)
                message.Text = selected.Body;
        };
        message.TextChanged += (_, _) =>
        {
            draftPreview.Text = message.Text;
            InvalidatePreview();
        };

        ChangeAudienceFields();
        SetStep(0);
        RefreshTemplateChoices();
        bool compose = CommunicationAccess.CanCompose(CurrentUser.HasPermission);
        bool view = CurrentUser.HasPermission("Communication.View");
        // The everyday entry point is the composer, not a work-in-progress
        // list. Queue remains one click away when there is work to resume.
        int landing = compose ? 0 : view ? HistoryStep : AdministrationStep;
        SetStep(landing);
        if (initialize)
        {
            if (compose)
            {
                if (CurrentUser.HasPermission("Students.View")) Defer(BindLookupsAsync);
                Defer(LoadComposeTemplatesAsync);
                Defer(SelectReadyMethodAsync);
                Defer(SelectSchoolLanguageAsync);
                Defer(RefreshQueueAttentionAsync);
            }
            if (landing is HistoryStep or UnfinishedStep)
                Defer(() => step == landing ? NavigateSessionsAsync(landing == UnfinishedStep) : Task.CompletedTask);
        }
        Disposed += (_, _) =>
        {
            previewTable?.Dispose();
            (historyGrid.DataSource as DataTable)?.Dispose();
            (unfinishedGrid.DataSource as DataTable)?.Dispose();
        };
    }

    private Control BuildWorkspace()
    {
        // All five actions live in one responsive navigation row. A FlowLayoutPanel
        // owns the button positions: the old two-panel bounds calculation could
        // leave the administration actions clipped to a few pixels on the right.
        var navigation = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, WrapContents = true, Margin = Padding.Empty,
            BackColor = AppTheme.Surface, Padding = new Padding(AppScale.Scale(6))
        };
        var navigationButtons = new[]
        {
            newMessageButton, unfinishedButton, historyButton, templatesButton, configureButton
        };
        var buttonMargin = new Padding(0, 0, AppScale.Scale(6), AppScale.Scale(4));
        foreach (var button in navigationButtons)
            button.Margin = buttonMargin;
        navigation.Controls.AddRange(navigationButtons);
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var navigationRow = new RowStyle(SizeType.Absolute,
            AppMetrics.ButtonHeight + navigation.Padding.Vertical);
        layout.RowStyles.Add(navigationRow);
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(navigation, 0, 0);
        layout.Controls.Add(steps, 0, 1);
        bool fitting = false;
        void FitNavigation()
        {
            if (fitting || navigation.IsDisposed) return;
            int width = navigation.ClientSize.Width - navigation.Padding.Horizontal;
            if (width <= 0) return;
            int measuredHeight = WrappedRowsHeight(navigation, width)
                + navigation.Padding.Vertical;
            // During a Windows resize the FlowLayoutPanel can position its
            // wrapped children *after* SizeChanged. Never let its actual
            // bottommost visible button extend beyond the available row.
            int actualHeight = navigation.Controls.Cast<Control>()
                .Where(button => button.Visible)
                .Select(button => button.Bottom + button.Margin.Bottom)
                .DefaultIfEmpty(0).Max() + navigation.Padding.Bottom;
            int height = Math.Max(AppMetrics.ButtonHeight + navigation.Padding.Vertical,
                Math.Max(measuredHeight, actualHeight));
            if (navigationRow.Height == height) return;
            fitting = true;
            try
            {
                navigationRow.Height = height;
                layout.PerformLayout();
            }
            finally { fitting = false; }
        }
        bool fitPending = false;
        void RefitAfterLayout()
        {
            // WinForms raises resize/layout notifications before the flow
            // children have finished wrapping. Re-evaluate once the current
            // layout pass completes, using the final client width.
            if (fitPending || layout.IsDisposed || !layout.IsHandleCreated) return;
            fitPending = true;
            layout.BeginInvoke(new Action(() =>
            {
                fitPending = false;
                if (!layout.IsDisposed) FitNavigation();
            }));
        }
        navigation.SizeChanged += (_, _) => RefitAfterLayout();
        navigation.Layout += (_, _) => RefitAfterLayout();
        SizeChanged += (_, _) => RefitAfterLayout();
        navigation.VisibleChanged += (_, _) => RefitAfterLayout();
        layout.SizeChanged += (_, _) => RefitAfterLayout();
        layout.HandleCreated += (_, _) => FitNavigation();
        foreach (Control button in navigation.Controls)
        {
            button.SizeChanged += (_, _) => FitNavigation();
            button.VisibleChanged += (_, _) => FitNavigation();
        }
        return layout;
    }

    private static int WrappedRowsHeight(FlowLayoutPanel panel, int availableWidth)
    {
        int height = 0, rowHeight = 0, rowWidth = 0;
        foreach (Control button in panel.Controls)
        {
            if (!button.Visible) continue;
            int width = button.Width + button.Margin.Horizontal;
            if (rowWidth > 0 && rowWidth + width > availableWidth)
            {
                height += rowHeight;
                rowHeight = rowWidth = 0;
            }
            rowWidth += width;
            rowHeight = Math.Max(rowHeight, button.Height + button.Margin.Vertical);
        }
        return height + rowHeight;
    }

    private HumanCard TargetCard()
    {
        var card = HumanUi.Card("New Message");
        card.Header.Visible = false;
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var fields = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, RowCount = 7, MaximumSize = new Size(AppScale.Scale(980), 0),
            Padding = new Padding(AppScale.Scale(8), 0, AppScale.Scale(8), AppScale.Scale(8)), Margin = Padding.Empty
        };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int row = 0; row < fields.RowCount; row++) fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var audienceField = HumanUi.Field("Send to", audience, true);
        var recipients = ComposeFieldRow(audienceField, campusField, classField, studentField, staffField);
        var methodField = HumanUi.Field("Send using", deliveryMethod, true);
        var templateField = HumanUi.Field("Template (optional)", templateChoice);
        var languageField = HumanUi.Field("Language", languageChoice);
        var sending = ComposeFieldRow(methodField, languageField, templateField);
        sending.Margin = new Padding(0, AppScale.Scale(6), 0, 0);
        fields.Controls.Add(recipients, 0, 0);
        fields.Controls.Add(targetIssue, 0, 1);
        fields.Controls.Add(sending, 0, 2);
        var editor = HumanUi.Field("Message", message, true);
        editor.Height = AppScale.Scale(200);
        fields.Controls.Add(editor, 0, 3);
        fields.Controls.Add(messageIssue, 0, 4);
        var livePreview = HumanUi.Field("Live draft preview · review for personalized messages", draftPreview);
        livePreview.Height = AppScale.Scale(140);
        draftPreview.Dock = DockStyle.Fill;
        fields.Controls.Add(livePreview, 0, 5);
        foreach (var issue in new[] { targetIssue, messageIssue })
        {
            issue.Visible = false;
            issue.TextChanged += (_, _) => issue.Visible = issue.Text.Length > 0;
        }
        var footer = ActionFooter(next);
        footer.Dock = DockStyle.Fill;
        fields.Controls.Add(footer, 0, 6);
        fields.RowStyles[0] = new RowStyle(SizeType.Absolute, AppMetrics.FieldRowHeight);
        fields.RowStyles[2] = new RowStyle(SizeType.Absolute, AppMetrics.FieldRowHeight + sending.Margin.Vertical);
        fields.RowStyles[3] = new RowStyle(SizeType.Absolute, editor.Height);
        fields.RowStyles[5] = new RowStyle(SizeType.Absolute, livePreview.Height);
        fields.RowStyles[6] = new RowStyle(SizeType.Absolute, footer.Height);
        bool fitting = false;
        void FitFields()
        {
            int width = fields.ClientSize.Width - fields.Padding.Horizontal;
            if (fitting || !fields.Visible || fields.IsDisposed || width <= 0) return;
            fitting = true;
            fields.SuspendLayout();
            try
            {
                int columns = Math.Clamp(width / AppScale.Scale(235), 1, 3);
                int columnWidth = width / columns;
                audienceField.Width = campusField.Width = classField.Width = columnWidth;
                studentField.Width = staffField.Width = Math.Min(width, columnWidth * 2);
                methodField.Width = languageField.Width = templateField.Width = columnWidth;
                fields.RowStyles[0].Height = WrappedRowsHeight(recipients, width);
                fields.RowStyles[2].Height = WrappedRowsHeight(sending, width) + sending.Margin.Vertical;
                targetIssue.MaximumSize = messageIssue.MaximumSize = new Size(width, 0);
            }
            finally
            {
                fields.ResumeLayout(true);
                fitting = false;
            }
        }
        fields.SizeChanged += (_, _) => FitFields();
        fields.VisibleChanged += (_, _) => FitFields();
        recipients.Layout += (_, _) => FitFields();
        scroll.Controls.Add(fields);
        card.Content.Controls.Add(scroll);
        return card;
    }

    private static FlowLayoutPanel ComposeFieldRow(params Panel[] fields)
    {
        var row = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, WrapContents = true, Margin = Padding.Empty, Padding = Padding.Empty
        };
        foreach (var field in fields)
        {
            field.Dock = DockStyle.None;
            field.Height = AppMetrics.FieldRowHeight;
            row.Controls.Add(field);
        }
        return row;
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
        card.Content.Controls.Add(ActionFooter(confirm, schedule));
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

    private static FlowLayoutPanel ActionFooter(params ModernButton[] buttons)
    {
        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = AppMetrics.ButtonHeight + AppScale.Scale(10),
            FlowDirection = FlowDirection.RightToLeft, WrapContents = false
        };
        footer.Controls.AddRange(buttons);
        return footer;
    }


    private void OpenTemplates()
    {
        if (IsBusy || !CurrentUser.HasPermission("Communication.Configure")) return;
        using var dialog = new CommunicationTemplatesDialog();
        dialog.ShowDialog(FindForm());
        if (dialog.Changed && CurrentUser.HasPermission("Communication.Send")) Defer(LoadComposeTemplatesAsync);
    }

    private void OpenSendingOptions()
    {
        if (IsBusy || !CurrentUser.HasPermission("Communication.Configure")) return;
        using var dialog = new CommunicationSendingOptionsDialog();
        dialog.ShowDialog(FindForm());
        if (!dialog.Changed) return;
        InvalidatePreview();
        Defer(SelectReadyMethodAsync);
        if (!languageChanged) Defer(SelectSchoolLanguageAsync);
    }
    private async Task LoadComposeTemplatesAsync()
    {
        if (!CurrentUser.HasPermission("Communication.Send")) return;
        int actor = CurrentUser.UserId;
        var choices = await CommunicationManagementStore.ReadComposeTemplatesAsync();
        if (IsDisposed || actor != CurrentUser.UserId) return;
        composeTemplates = choices;
        RefreshTemplateChoices();
    }

    private void RefreshTemplateChoices()
    {
        if (templateChoice is null) return;
        var target = CurrentTarget();
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
        templateChoice.Items.Add("Search matching templates…");
        foreach (var item in composeTemplates)
            if (item.Matches(target) && item.LanguageCode == CurrentLanguage)
                templateChoice.Items.Add(item);
        templateChoice.SelectedIndex = 0;
    }

    private void OpenComposerTemplateBrowser()
    {
        if (IsBusy || IsMeta || !CurrentUser.HasPermission("Communication.Send")) return;
        int actor = CurrentUser.UserId;
        CommunicationTarget target = CurrentTarget();
        var compatible = composeTemplates.Where(item =>
            item.Matches(target) && item.LanguageCode == CurrentLanguage).ToArray();
        using var dialog = new CommunicationComposerTemplateBrowser(compatible);
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK ||
            dialog.SelectedTemplate is not { } selected) return;
        if (IsDisposed || actor != CurrentUser.UserId || target != CurrentTarget()
            || IsMeta || !CurrentUser.HasPermission("Communication.Send")) return;
        if (message.Text.Length > 0 && message.Text != selected.Body &&
            !Confirm("Replace your current message draft with the selected template?",
                "Replace Draft", false)) return;
        message.Text = selected.Body;
        InvalidatePreview();
    }

    private void ChangeAudienceFields()
    {
        var target = SelectedAudience;
        bool showCampus = target is CommunicationAudience.Campus
            or CommunicationAudience.Class or CommunicationAudience.OutstandingFees;
        bool showClass = target is CommunicationAudience.Class
            or CommunicationAudience.OutstandingFees;
        bool showStudent = target is CommunicationAudience.Student or CommunicationAudience.Family or CommunicationAudience.FeeStudent or CommunicationAudience.FeeFamily or CommunicationAudience.OutstandingFees;
        bool showStaff = target == CommunicationAudience.Staff;
        Control layout = campusField.Parent!;
        layout.SuspendLayout();
        campusField.Visible = showCampus;
        classField.Visible = showClass;
        studentField.Visible = showStudent;
        staffField.Visible = showStaff;
        SetFieldCaption(campusField, campus, "Campus", target is CommunicationAudience.Campus or CommunicationAudience.Class);
        SetFieldCaption(classField, cls, "Class", target == CommunicationAudience.Class);
        SetFieldCaption(studentField, student, target switch
        {
            CommunicationAudience.Family or CommunicationAudience.FeeFamily => "Student in the family",
            CommunicationAudience.OutstandingFees => "Student (optional)",
            _ => "Student"
        },
            target is CommunicationAudience.Student or CommunicationAudience.Family or CommunicationAudience.FeeStudent or CommunicationAudience.FeeFamily);
        SetFieldCaption(staffField, staff, "Staff member", showStaff);
        UpdateLookupPrompt(campus, target == CommunicationAudience.OutstandingFees ? "All campuses" : "Select a campus");
        UpdateClassSelection();
        layout.ResumeLayout(true);
    }

    private void UpdateClassSelection()
    {
        bool mustChooseCampus = SelectedAudience == CommunicationAudience.Class && campus.SelectedId == 0;
        cls.Enabled = !mustChooseCampus;
        UpdateLookupPrompt(cls, SelectedAudience == CommunicationAudience.OutstandingFees ? "All classes"
            : mustChooseCampus ? "Select a campus first" : "Select a class");
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

    private async Task SelectSchoolLanguageAsync()
    {
        if (!CurrentUser.HasPermission("Communication.Send") || languageChanged) return;
        int actor = CurrentUser.UserId;
        using DataTable language = await CommunicationManagementStore.ReadMessageLanguageAsync();
        if (IsDisposed || actor != CurrentUser.UserId || languageChanged
            || step == 1 || language.Rows.Count != 1) return;
        string code = Convert.ToString(language.Rows[0]["DefaultMessageLanguage"]) ?? "en";
        languageChoice.SelectedIndex = code == "ur" ? 1 : 0;
    }

    private async Task SelectReadyMethodAsync()
    {
        if (!CurrentUser.HasPermission("Communication.Send") || methodChanged) return;
        int actor = CurrentUser.UserId;
        var preferredTask = CommunicationQueuePolicyStore.ReadPreferredMethodAsync();
        var readiness = await Task.WhenAll(
            operations.ReadManualMode(),
            CommunicationApiBatchStore.IsReadyAsync("WhatsApp", "MetaCloud"),
            CommunicationApiBatchStore.IsReadyAsync("Sms", "Twilio"));
        using var preferred = await preferredTask;
        if (IsDisposed || actor != CurrentUser.UserId || methodChanged || step == 1) return;
        // A school-wide choice is the initial suggestion, never a forced
        // recipient-specific fallback. Explicit user changes always win.
        int selected = -1;
        if (preferred.Rows.Count == 1)
        {
            string channel = Convert.ToString(preferred.Rows[0]["PreferredChannel"]) ?? "";
            string provider = Convert.ToString(preferred.Rows[0]["PreferredProviderCode"]) ?? "";
            selected = (channel, provider) switch
            {
                ("WhatsApp", "WhatsAppBusinessManual") => 0,
                ("WhatsApp", "MetaCloud") => 1,
                ("Sms", "Twilio") => 2,
                _ => -1
            };
        }
        if (selected >= 0 && readiness[selected] && (selected != 1 || message.Text.Length == 0))
            deliveryMethod.SelectedIndex = selected;
        else if (readiness.Count(value => value) == 1)
        {
            // Selecting the sole available method still requires a final
            // review and charge disclosure. Never replace an edited draft.
            int only = Array.IndexOf(readiness, true);
            if (only != 1 || message.Text.Length == 0)
                deliveryMethod.SelectedIndex = only;
        }
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
        pendingAutomationSuggestion=null;
        InvalidatePreview();
        metaSelection = null;
        message.Clear();
        templateChoice.SelectedIndex = 0;
        SelectedAudience = CurrentUser.HasPermission("Students.View") ? CommunicationAudience.Class : CommunicationAudience.Staff;
        if (campus.Items.Count > 0) campus.SelectedIndex = 0;
        if (cls.Items.Count > 0) cls.SelectedIndex = 0;
        student.SetStudent(0, "");
        staff.SetStaff(0, "");
    }

    private CommunicationTarget CurrentTarget()
    {
        var selected = SelectedAudience;
        bool scoped = selected is CommunicationAudience.Campus or CommunicationAudience.Class or CommunicationAudience.OutstandingFees;
        return new(selected, scoped ? campus.SelectedId : 0,
            selected is CommunicationAudience.Class or CommunicationAudience.OutstandingFees ? cls.SelectedId : 0,
            selected is CommunicationAudience.Student or CommunicationAudience.Family or CommunicationAudience.FeeStudent or CommunicationAudience.FeeFamily or CommunicationAudience.OutstandingFees ? student.SelectedId : 0,
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
        for (int index = HistoryStep; index < cards.Count; index++) cards[index].Visible = step == index;
        (composing ? (Control)composerLayout : cards[step]).BringToFront();

        bool compose = CommunicationAccess.CanCompose(CurrentUser.HasPermission);
        next.Visible = compose && step == 0;
        confirm.Visible = compose && step == 1;
        schedule.Visible = compose && step == 1 && !IsManual;
        schedule.Enabled = schedule.Visible && manualModeAvailable && preview is { Included: > 0 };
        confirm.Text = IsManual ? (preview?.Included == 1 ? "Open in WhatsApp" : "Start WhatsApp Chats") : "Confirm & Send";
        confirm.Enabled = step == 1 && manualModeAvailable && preview is { Included: > 0 }
            && CurrentUser.HasPermission("Communication.Send");
        historyButton.Visible = CurrentUser.HasPermission("Communication.View");
        unfinishedButton.Visible = compose;
        templatesButton.Visible = configureButton.Visible = CurrentUser.HasPermission("Communication.Configure");
        newMessageButton.Visible = compose;
        SetNavigationTab(newMessageButton, composing);
        SetNavigationTab(unfinishedButton, step == UnfinishedStep);
        SetNavigationTab(historyButton, step == HistoryStep);
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
        catch(Exception){if(!IsDisposed)Warn("Meta templates could not be read. Check the protected profile and account permissions in Configure Sending.");}
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
            string costNotice = SmsChargeDisclosure(target, reviewed, body);
            if(!Confirm($"Send {reviewed.Included} reviewed message(s) using {deliveryMethod.Text}? "
                + costNotice + " You can stop the remaining messages while sending.",
                "Confirm Send",false))return;
            int actor=CurrentUser.UserId;Guid request=manualRequestId;
            var saved=await CommunicationApiBatchStore.ConfirmAsync(target,reviewed,body,request,ApiProvider,metaSelection);
            if(IsDisposed || actor!=CurrentUser.UserId)return;
            if(request!=manualRequestId || target!=CurrentTarget() || body!=message.Text)
                throw new CommunicationValidationException("The batch was saved, but the current review changed. Open it in Queue; nothing was sent.");
            await MarkAutomationSuggestionPreparedAsync(target,saved.BatchId);
            using(var dialog=new CommunicationApiDispatchDialog(saved.BatchId,startConfirmed:!saved.AlreadyConfirmed))dialog.ShowDialog(FindForm());
            if (!IsDisposed)
            {
                ClearReviewedDraft();
                await ShowSessionsAsync(unfinished: true);
            }
        }
        catch(CommunicationValidationException ex){if(!IsDisposed)Warn(ex.Message);}
        catch(Exception ex){if(!IsDisposed)Fail(ex);}
    }

    /// <summary>
    /// Explicitly disclose potentially higher Unicode SMS billing for the
    /// actual personalized preview, not just the English draft with fields.
    /// Provider tariffs and segment counts vary; never promise a fixed price.
    /// </summary>
    private static string SmsChargeDisclosure(
        CommunicationTarget target, CommunicationPreview reviewed, string body)
    {
        if (target.Channel != "Sms") return "Provider charges may apply.";
        bool unicode = reviewed.Entries.Where(entry => entry.Included).Any(entry =>
            CommunicationPreparedMessages.Render(body,
                CommunicationPreparedMessages.FromPreview(entry)).Any(ch => ch > 127));
        return unicode
            ? "This SMS includes Unicode characters (for example Urdu names or text). Unicode and long messages may be split into multiple separately billed SMS segments; review provider charges."
            : "Long SMS messages may be split into multiple separately billed segments; review provider charges.";
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
                throw new InvalidOperationException("Signed-in user changed. Open Queue.");
            if (request != manualRequestId || target != CurrentTarget() || body != message.Text)
            {
                Warn("The reviewed batch was saved, but the current message changed. Review that batch in Queue; no chat was opened.");
                return;
            }
            await MarkAutomationSuggestionPreparedAsync(target,saved.BatchId);
            // The operator has reviewed and confirmed this batch. Open the first
            // chat now rather than requiring another redundant click. No message
            // is sent by FMSC; every WhatsApp send remains operator-controlled.
            // A previously confirmed batch is never auto-reopened on retry.
            operations.OpenHandoff(saved.BatchId, !saved.AlreadyConfirmed);
            if (!IsDisposed)
            {
                ClearReviewedDraft();
                await ShowSessionsAsync(unfinished: true);
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
            : !manualModeAvailable ? "This sending method is paused or needs setup under Configure Sending."
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
            UpdateClassSelection();
        }
        catch (CommunicationValidationException ex) { if (!IsDisposed) Warn(ex.Message); }
        catch (Exception ex) { if (!IsDisposed) Fail(ex); }
    }
}
