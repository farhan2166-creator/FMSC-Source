using System.Data;
using FMSC_SMS.Core;

namespace FMSC_SMS.Pages.Communication;

/// <summary>
/// Read-only Communication Center workflow under construction.
/// It remains out of navigation until the SQL send gate, provider and
/// end-to-end acceptance are complete. Confirm & Send is fail-closed.
/// </summary>
public sealed class CommunicationCenterPage : V2Page, IBusyPage
{
    private readonly ModernComboBox audience = HumanUi.Combo();
    private readonly LookupBox campus = new(), cls = new();
    private readonly StudentPicker student = new();
    private readonly StaffPicker staff = new();
    private readonly ModernComboBox channel = HumanUi.Combo();
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
    private readonly Panel steps = new() { Dock = DockStyle.Fill };
    private readonly List<HumanCard> cards = new();
    private readonly Panel campusField, classField, studentField, staffField;
    private readonly ModernButton previous, next, confirm;
    private CommunicationPreview? preview;
    private CommunicationTarget? previewTarget;
    private string? previewMessage;
    private DataTable? previewTable;
    private int step;
    private int previewReadVersion;

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

        campusField = HumanUi.Field("Campus", campus);
        classField = HumanUi.Field("Class", cls);
        studentField = HumanUi.Field("Student / family reference", student);
        staffField = HumanUi.Field("Staff member", staff);

        cards.Add(TargetCard());
        cards.Add(ComposeCard());
        cards.Add(PreviewCard());
        cards.Add(ConfirmCard());
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
        confirm = AddAction("Confirm & Send", (_, _) =>
            Warn("Sending is disabled until secure provider acceptance passes."), "Primary");
        // Absolutely no send path exists at this stage.
        confirm.Enabled = false;
        SetContent(root);

        audience.SelectedIndexChanged += (_, _) => { ChangeAudienceFields(); InvalidatePreview(); };
        campus.SelectedIndexChanged += (_, _) => InvalidatePreview();
        cls.SelectedIndexChanged += (_, _) => InvalidatePreview();
        student.UserSelectionChanged += (_, _) => InvalidatePreview();
        student.SelectionCleared += (_, _) => InvalidatePreview();
        staff.UserSelectionChanged += (_, _) => InvalidatePreview();
        staff.SelectionCleared += (_, _) => InvalidatePreview();
        channel.SelectedIndexChanged += (_, _) => InvalidatePreview();
        message.TextChanged += (_, _) => InvalidatePreview();

        ChangeAudienceFields();
        SetStep(0);
        if (initialize) Defer(BindLookupsAsync);
        Disposed += (_, _) => previewTable?.Dispose();
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
            Dock = DockStyle.Top, Height = AppScale.Scale(265),
            ColumnCount = 1, RowCount = 2, Padding = new Padding(12)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(65)));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(HumanUi.Field("Channel", channel, true), 0, 0);
        layout.Controls.Add(HumanUi.Field("Message", message, true), 0, 1);
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
        var card = HumanUi.Card("4. Confirm & Send",
            "Final confirmation remains unavailable until provider delivery is implemented and accepted.");
        card.Content.Controls.Add(confirmation);
        return card;
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
        previewReadVersion++;
    }

    private CommunicationTarget CurrentTarget() => new(
        (CommunicationAudience)Math.Max(audience.SelectedIndex, 0),
        campus.SelectedId, cls.SelectedId, student.SelectedId, staff.SelectedId,
        channel.SelectedIndex == 1 ? "WhatsApp" : "Sms");

    private void SetStep(int number)
    {
        step = Math.Clamp(number, 0, 3);
        string[] stages = { "1 / 4   Select Recipients", "2 / 4   Compose Message",
            "3 / 4   Preview", "4 / 4   Confirm & Send" };
        heading.Text = stages[step];
        for (int i = 0; i < cards.Count; i++) cards[i].Visible = i == step;
        cards[step].BringToFront();
        previous.Enabled = step > 0;
        next.Visible = step < 3;
        confirm.Visible = step == 3;
        confirm.Enabled = false;
        if (step == 3)
            SetStatus("Sending remains disabled until the complete release acceptance is passed.",
                PageStatusKind.Warning);
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
                SetStep(1);
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
                confirmation.Text = "Channel: " + previewTarget.Channel
                    + Environment.NewLine + "Selected school records: " + preview.Entries.Count
                    + Environment.NewLine + "Unique eligible destinations: " + preview.Included
                    + Environment.NewLine + "Excluded: " + preview.Excluded
                    + Environment.NewLine + Environment.NewLine + "Message:"
                    + Environment.NewLine + (previewMessage ?? "")
                    + Environment.NewLine + Environment.NewLine
                    + "Sending is currently DISABLED. No message has been queued or delivered.";
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
