using FMSC_SMS.Core;
using FMSC_SMS.Pages.Students.Admissions;
using FMSC_SMS.Pages.Students.Enrollment;
using FMSC_SMS.Pages.Students.Maintenance;
namespace FMSC_SMS.Pages.Students.Profile;
/// <summary>A read-only aggregator: pick a student once and see identity, current placement, outstanding balance, family and lifecycle history in one place, with jump-links into the existing pages that actually edit each area.</summary>
public sealed class StudentProfilePage : V2Page
{
    private readonly Action<Func<Control>, string, string>? navigate;
    private readonly StudentPicker student = new();
    private readonly List<ModernButton> studentShortcuts=new();
    private readonly Dictionary<ModernButton, (Func<bool> Allowed, bool RequireStudent)> shortcutAccess = new();
    private void UpdateStudentShortcuts()
    {
        foreach (var (button, access) in shortcutAccess)
        {
            bool allowed = access.Allowed();
            button.Enabled = allowed && (!access.RequireStudent || student.SelectedId > 0);
            string hint = !allowed ? "This action is not enabled for your account."
                : access.RequireStudent && student.SelectedId <= 0 ? "Select a student in this profile first."
                : button.Text == "Manage Photo" ? "Choose or update this student's photo."
                : button.Text == "Open " + PageTitles.AnnualFeeReceipts ? "Open annual receipts for this student."
                : CurrentUser.HasPermission("Students.Administer") ? "Open this student's record."
                : "Open the record for viewing. Editing requires student administration access.";
            SetToolTip(button, hint);
        }
    }
    private readonly LatestReadScope profileReads = new();
    private readonly ModernButton feeHistoryButton = new() { Text="Fee Change History", Enabled=false };
    private bool loadingFeeHistory;
    private readonly PictureBox photo = new() {
        Dock = DockStyle.Fill,
        SizeMode = PictureBoxSizeMode.Zoom,
        BackColor = AppTheme.SurfaceSecondary,
        BorderStyle = BorderStyle.FixedSingle
    };
    private readonly Label nameLabel = new() {
        Dock = DockStyle.Top,
        Height = AppScale.Scale(26),
        Font = AppTheme.HeadingFont,
        ForeColor = AppTheme.HeadingText,
        TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true
    };
    private readonly Label fatherLabel = new() {
        Dock = DockStyle.Top,
        Height = AppScale.Scale(20),
        Font = AppTheme.NormalFont,
        ForeColor = AppTheme.MutedText,
        TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true
    };
    private readonly Label admissionDateLabel = FactLabel();
    private readonly Label admissionCampusLabel = FactLabel();
    private readonly Label dateOfBirthLabel = FactLabel();
    private readonly Label genderLabel = FactLabel();
    private static Label FactLabel() => new() {
        Dock = DockStyle.Fill,
        Height = AppScale.Scale(23),
        Font = AppTheme.NormalFont,
        ForeColor = AppTheme.MutedText,
        TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true,
        Margin = Padding.Empty
    };
    private readonly Label placementLabel = new() {
        Dock = DockStyle.Top,
        Height = AppScale.Scale(20),
        Font = AppTheme.NormalFont,
        ForeColor = AppTheme.MutedText,
        TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true
    };
    private readonly Label registrationLabel = new() {
        Dock = DockStyle.Top,
        Height = AppScale.Scale(20),
        Font = AppTheme.NormalFont,
        ForeColor = AppTheme.MutedText,
        TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true
    };
    private readonly Label contactLabel = new() {
        Dock = DockStyle.Top,
        Height = AppScale.Scale(20),
        Font = AppTheme.NormalFont,
        ForeColor = AppTheme.MutedText,
        TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true
    };
    private readonly Label balanceLabel = new() {
        Dock = DockStyle.Top,
        Height = AppScale.Scale(24),
        Font = AppTheme.FontSmallBold,
        ForeColor = AppTheme.MutedText,
        TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true
    };
    private readonly DataGridView contextGrid = HumanUi.Grid();
    private readonly DataGridView familyGrid = HumanUi.Grid();
    private readonly DataGridView timelineGrid = HumanUi.Grid();
    private readonly Label familyEmptyLabel = new() {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        Font = AppTheme.NormalFont,
        ForeColor = AppTheme.MutedText,
        Text = "This student does not currently belong to a family.",
        Visible = false
    };
    public StudentProfilePage(Action<Func<Control>, string, string>? navigate = null) : this(navigate,true) { }
    internal static StudentProfilePage ForStudent(int admissionId,Action<Func<Control>,string,string>? navigate=null)
    {
        var page=new StudentProfilePage(navigate);
        int actor=CurrentUser.UserId;string endpoint=DatabaseHelper.DatabaseEndpoint;
        // Explicit search targets load after workspace restoration and win over remembered selections.
        page.Defer(async()=>{
            if(actor!=CurrentUser.UserId||endpoint!=DatabaseHelper.DatabaseEndpoint||!CurrentUser.HasPermission("Students.View"))return;
            var row=await StudentSearchCache.FindByAdmissionIdAsync(admissionId);
            if(page.IsDisposed||actor!=CurrentUser.UserId||endpoint!=DatabaseHelper.DatabaseEndpoint||!CurrentUser.HasPermission("Students.View"))return;
            if(row is null){page.Warn("This student is no longer available.");return;}
            page.student.SetStudent(row);page.UpdateStudentShortcuts();page.UpdateFeeHistoryAvailability();
            await page.LoadProfileAsync();
        });
        return page;
    }
    internal StudentProfilePage(Action<Func<Control>, string, string>? navigate,bool initialize)
        : base(
            "Student Profile",
            "")
    {
        UiHelper.DisposePictureImageOnDispose(photo);
        StudentPhotoViewForm.EnableZoom(photo, () => nameLabel.Text);
        Disposed += (_, _) => profileReads.Dispose();
        this.navigate = navigate;
        student.UserSelectionChanged += async (_, _) => await LoadProfileAsync();
        student.UserSelectionChanged += (_, _) => {UpdateFeeHistoryAvailability();UpdateStudentShortcuts();};
        /* Editing the box away from the student on screen drops that student's profile, so nothing is shown under a name that is no longer in the box. */
        student.SelectionCleared += (_, _) => {
            ClearProfile();
            UpdateFeeHistoryAvailability();
            UpdateStudentShortcuts();
            SetStatus("Select a student to view their profile.");
        };
        student.SearchFailed += ex => Fail(ex);
        if(initialize)StudentSearchCache.Warm();
        UiHelper.SetGridPlaceholderMessage(contextGrid, UiText.EmptyGrid.SelectStudent);
        UiHelper.SetGridPlaceholderMessage(familyGrid, UiText.EmptyGrid.SelectStudent);
        UiHelper.SetGridPlaceholderMessage(timelineGrid, UiText.EmptyGrid.SelectStudent);
        // A loaded profile is an overview, not three preselected records.
        // Keep row highlights only when the operator deliberately selects a grid row.
        foreach (var grid in new[] { familyGrid, contextGrid, timelineGrid })
            grid.DataBindingComplete += (_, _) => { if (!grid.IsDisposed) grid.ClearSelection(); };
        PlaceStudentSearchInHeader();
        var overview = BuildHeaderCard();
        var sections = BuildSectionsGrid(overview);
        var footer = BuildQuickActionsFooter();
        var root = new TableLayoutPanel {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute,
            Math.Max(AppScale.Scale(94), 2 * (AppTheme.CompactButtonHeight + UiDesignSystem.Spacing.Sm)
                + UiDesignSystem.Spacing.Md)));
        root.Controls.Add(sections, 0, 0);
        root.Controls.Add(footer, 0, 1);
        SetContent(root);
        ClearProfile();
    }
    private void PlaceStudentSearchInHeader()
    {
        // Reuse the page heading's free horizontal area instead of reserving
        // an entire card/row just for student selection.
        // Keep the same accessible identity as the former Student field,
        // including the stable Level 2 automation target on its inner textbox.
        student.AccessibleName = "Student";
        student.ApplyAccessibleInfo("Student", "Search by admission number, student or father name.");
        var search = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        var inputRow = new TableLayoutPanel {
            Dock = DockStyle.Right,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, AppScale.Scale(108)));
        inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        inputRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var label = new Label {
            Text = "Find student",
            Font = AppTheme.NormalFont,
            ForeColor = AppTheme.MutedText,
            TextAlign = ContentAlignment.MiddleRight,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, UiDesignSystem.Spacing.Sm, 0)
        };
        inputRow.Controls.Add(label, 0, 0);
        inputRow.Controls.Add(student, 1, 0);
        search.Controls.Add(inputRow);
        search.Resize += (_, _) => {
            int width = Math.Min(AppScale.Scale(590), search.ClientSize.Width);
            inputRow.Width = width;
            inputRow.ColumnStyles[0].Width = width >= AppScale.Scale(380) ? AppScale.Scale(108) : 0;
        };
        SetInlineHeaderContent(search, AppMetrics.InputControlHeight + AppScale.Scale(2));
    }
    private static int OverviewRowHeight(params Label[] labels) =>
        labels.Max(label => Math.Max(label.Height,
            AppMetrics.TextHeight(label.Font) + AppScale.Scale(2)));
    private int[] OverviewRowHeights() => new[] {
        OverviewRowHeight(nameLabel),
        OverviewRowHeight(fatherLabel),
        OverviewRowHeight(placementLabel, genderLabel),
        OverviewRowHeight(dateOfBirthLabel, admissionDateLabel),
        OverviewRowHeight(admissionCampusLabel, contactLabel),
        OverviewRowHeight(registrationLabel, balanceLabel)
    };
    private int OverviewDetailsHeight() => OverviewRowHeights().Sum();

    private HumanCard BuildHeaderCard()
    {
        var card = HumanUi.Card("Student Overview");
        var layout = new TableLayoutPanel {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, AppScale.Scale(96)));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var photoHost = new Panel {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, UiDesignSystem.Spacing.Md, 0)
        };
        photoHost.Controls.Add(photo);

        // Two facts columns fit the half-width card; full-width name/father
        // rows remain readable while preserving all admission and fee facts.
        var details = new TableLayoutPanel {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 6,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 51));
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 49));
        var rows = OverviewRowHeights();
        foreach (int height in rows)
            details.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        foreach (var fact in new[] { nameLabel, fatherLabel, placementLabel,
                     genderLabel, dateOfBirthLabel, admissionDateLabel,
                     admissionCampusLabel, contactLabel, registrationLabel, balanceLabel })
        {
            fact.Dock = DockStyle.Fill;
            fact.Margin = Padding.Empty;
        }
        details.Controls.Add(nameLabel, 0, 0);
        details.SetColumnSpan(nameLabel, 2);
        details.Controls.Add(fatherLabel, 0, 1);
        details.SetColumnSpan(fatherLabel, 2);
        details.Controls.Add(placementLabel, 0, 2);
        details.Controls.Add(genderLabel, 1, 2);
        details.Controls.Add(dateOfBirthLabel, 0, 3);
        details.Controls.Add(admissionDateLabel, 1, 3);
        details.Controls.Add(admissionCampusLabel, 0, 4);
        details.Controls.Add(contactLabel, 1, 4);
        details.Controls.Add(registrationLabel, 0, 5);
        details.Controls.Add(balanceLabel, 1, 5);

        layout.Controls.Add(photoHost, 0, 0);
        layout.Controls.Add(details, 1, 0);
        card.Content.Controls.Add(layout);
        return card;
    }
    private Control BuildSectionsGrid(HumanCard overview)
    {
        var grid = new TableLayoutPanel {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        var familyCard = HumanUi.Card("Family / Siblings", "Other children linked to this family.");
        familyCard.Margin = new Padding(AppScale.Scale(4), AppScale.Scale(4), 0, AppScale.Scale(4));
        var familyHost = new Panel { Dock = DockStyle.Fill };
        familyHost.Controls.Add(familyGrid);
        familyHost.Controls.Add(familyEmptyLabel);
        familyEmptyLabel.BringToFront();
        familyCard.Content.Controls.Add(familyHost);
        var contextCard = HumanUi.Card("Academic Placement", "Academic year, campus, class and status.");
        contextCard.Margin = new Padding(0, AppScale.Scale(4), AppScale.Scale(4), AppScale.Scale(4));
        contextCard.Content.Controls.Add(contextGrid);
        var timelineCard = HumanUi.Card("Student History", "Enrollment, certificates and status changes.");
        timelineCard.Margin = new Padding(0, AppScale.Scale(4), 0, 0);
        timelineCard.Content.Controls.Add(timelineGrid);
        overview.Margin = new Padding(0, AppScale.Scale(4), AppScale.Scale(4), AppScale.Scale(4));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute,
            Math.Max(AppScale.Scale(190),
                overview.Margin.Vertical + overview.Padding.Vertical +
                overview.Header.Height + overview.Content.Padding.Vertical +
                OverviewDetailsHeight() + AppScale.Scale(4))));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute,
            Math.Max(AppScale.Scale(170),
                contextCard.Margin.Vertical + contextCard.Padding.Vertical +
                contextCard.Header.Height + contextCard.Content.Padding.Vertical +
                AppMetrics.GridHeaderHeight + 2 * AppMetrics.GridRowHeight)));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        grid.Controls.Add(overview, 0, 0);
        grid.Controls.Add(contextCard, 0, 1);
        grid.Controls.Add(familyCard, 1, 0);
        grid.SetRowSpan(familyCard, 2);
        grid.Controls.Add(timelineCard, 0, 2);
        grid.SetColumnSpan(timelineCard, 2);
        return grid;
    }
    /* The six jump buttons were one long strip that scrolled sideways on a narrow window. */
    private Control BuildQuickActionsFooter()
    {
        var table = new TableLayoutPanel {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Margin = new Padding(0, UiDesignSystem.Spacing.Sm, 0, 0),
            Padding = new Padding(0)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, AppScale.Scale(150)));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        FlowLayoutPanel Row() => new FlowLayoutPanel {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        Label GroupLabel(string text) => new Label {
            Text = text,
            Dock = DockStyle.Fill,
            Font = UiDesignSystem.Typography.Caption,
            ForeColor = AppTheme.MutedText,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            UseMnemonic = false
        };
        var recordRow = Row();
        recordRow.Controls.Add(
            JumpButton("Open Admissions", id => new StudentAdmissionPage(navigate, preselectAdmissionId: id), title: PageTitles.Admissions,requireStudent:false));
        recordRow.Controls.Add(
            JumpButton("Open Family / Siblings", id => new StudentFamilyPage(id), ownedWindow: true, permissions: new[] { "Students.Family" }));
        recordRow.Controls.Add(
            JumpButton("Manage Photo", id => new StudentAdmissionPage(navigate, preselectAdmissionId: id, focusPhoto: true), title: PageTitles.Admissions, permissions: new[] { "Students.Administer" }));
        var lifecycleRow = Row();
        lifecycleRow.Controls.Add(
            JumpButton("Open " + PageTitles.StudentRecordChecks, id => new StudentRecordChecksPage(id)));
        lifecycleRow.Controls.Add(
            JumpButton(
                "Open " + PageTitles.AnnualFeeReceipts,
                id => new FMSC_SMS.Pages.Reports.Finance.AnnualFeeReceiptsPage(id),
                module: "Finance", permissions: new[] { "Fees.Statement.View" }));
        lifecycleRow.Controls.Add(feeHistoryButton);
        feeHistoryButton.Height=AppTheme.CompactButtonHeight;
        feeHistoryButton.Margin=new Padding(0,0,UiDesignSystem.Spacing.Sm,0);
        AppMetrics.FitButtonWidth(feeHistoryButton,minWidth:150);
        feeHistoryButton.SetOutlineColors(AppTheme.InputBorder,AppTheme.Primary,AppTheme.Surface);
        feeHistoryButton.Click += async (_, _) => {
            if (loadingFeeHistory) return;
            loadingFeeHistory=true;
            UpdateFeeHistoryAvailability();
            try { await FMSC_SMS.Pages.Reports.Finance.StudentFeeChangeHistory.ShowAsync(this,() => student.SelectedId); }
            catch(Exception ex) { if(!IsDisposed) Fail(ex); }
            finally { loadingFeeHistory=false; if(!IsDisposed) UpdateFeeHistoryAvailability(); }
        };
        table.Controls.Add(GroupLabel("Student record"), 0, 0);
        table.Controls.Add(recordRow, 1, 0);
        table.Controls.Add(GroupLabel("History & fees"), 0, 1);
        table.Controls.Add(lifecycleRow, 1, 1);
        return table;
    }
    private void UpdateFeeHistoryAvailability() => feeHistoryButton.Enabled=!loadingFeeHistory && student.SelectedId>0
        && CurrentUser.HasPermission("Fees.View") && CurrentUser.HasPermission("Fees.Statement.View");
    private ModernButton JumpButton(string text, Func<int, Control> factory, string module = "Students", string? title = null,bool requireStudent=true, bool ownedWindow=false, params string[] permissions)
    {
        var button = new ModernButton {
            Text = text,
            Height = AppTheme.CompactButtonHeight,
            Margin = new Padding(
                0,
                0,
                UiDesignSystem.Spacing.Sm,
                0),
            Tag = UiDesignSystem.Actions.Secondary
        };
        AppMetrics.FitButtonWidth(button, minWidth: 150);
        button.SetOutlineColors(AppTheme.InputBorder, AppTheme.Primary, AppTheme.Surface);
        if (permissions.Length == 0) permissions = new[] { "Students.View" };
        bool Allowed() => permissions.All(CurrentUser.HasPermission);
        shortcutAccess[button] = (Allowed, requireStudent);
        if (requireStudent) studentShortcuts.Add(button);
        UpdateStudentShortcuts();
        button.Click += async (_, _) => {
            if (!Allowed()) { SetStatus("This action is not enabled for your account.", PageStatusKind.Warning); return; }
            int admissionId = student.SelectedId;
            if(requireStudent&&admissionId<=0){SetStatus("Select a student in Student Profile before opening this action.",PageStatusKind.Warning);student.Focus();return;}
            Func<Control> openPage = () => factory(admissionId);
            if (navigate is not null && !ownedWindow)
            {
                navigate(openPage, module, title ?? text.Replace("Open ", string.Empty));
                return;
            }
            try
            {
                using var dialog = CreateShortcutDialog(text, openPage);
                dialog.ShowDialog(FindForm());
                if (ownedWindow && !IsDisposed && student.SelectedId == admissionId)
                    await LoadProfileAsync();
            }
            catch (Exception ex) { if (!IsDisposed) Fail(ex); }
        };
        return button;
    }
    private static ApplicationWindowForm CreateShortcutDialog(string text, Func<Control> openPage)
    {
        var dialog = new ApplicationWindowForm {
                Text = text,
                StartPosition = FormStartPosition.CenterParent,
                Width = AppScale.Scale(1200),
                Height = AppScale.Scale(760),
                MinimumSize = new Size(
                    AppScale.Scale(980),
                    AppScale.Scale(620)),
                BackColor = AppTheme.PageBackground,
                ShowInTaskbar = false
            };
        try
        {
            var control = openPage();
            control.Dock = DockStyle.Fill;
            dialog.Controls.Add(control);
            ModalPageCloseGuard.Attach(dialog, control);
            return dialog;
        }
        catch { dialog.Dispose(); throw; }
    }
    private async Task LoadProfileAsync()
    {
        if (IsDisposed) return;
        if (student.SelectedId <= 0)
        {
            Warn(UiText.Prompts.SelectStudent);
            return;
        }
        int admissionId = student.SelectedId;
        ClearProfile();
        var request = profileReads.Begin();
        bool Current() => !IsDisposed && profileReads.IsCurrent(request) && student.SelectedId == admissionId;
        try
        {
            SetStatus("Loading student profile...");
            var contextTask = DatabaseHelper.QueryUserAsync("GetStudentDetails", request.Token,
                DatabaseHelper.Int("@AdmissionId", admissionId));
            var photoTask = DatabaseHelper.QueryUserAsync("GetStudentPhoto", request.Token,
                DatabaseHelper.Int("@AdmissionId", admissionId));
            var familyTask = DatabaseHelper.QueryUserAsync("GetStudentFamily", request.Token,
                DatabaseHelper.Int("@AdmissionId", admissionId));
            var timelineTask = DatabaseHelper.QueryUserAsync("GetStudentLifecycleTimeline", request.Token,
                DatabaseHelper.Int("@AdmissionId", admissionId));
            var balanceTask = ReadBalanceAsync(admissionId, request.Token);
            // Show identity/context as soon as those reads finish; a slower
            // balance calculation must not hold back the rest of the profile.
            await Task.WhenAll(contextTask, photoTask, familyTask, timelineTask);
            if (!Current()) return;
            RenderIdentity(await contextTask, admissionId);
            RenderPhoto(await photoTask);
            BindContextGrid(await contextTask);
            BindMiniGrid(timelineGrid, await timelineTask);
            ConfigureTimelineGrid();
            RenderFamily(await familyTask, admissionId);
            var balance = await balanceTask;
            if (!Current()) return;
            RenderBalance(balance);
            if (Current()) SetStatus("Profile loaded.");
        }
        catch (Exception) when (request.Token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (Current()) { profileReads.Cancel(); Fail(ex); }
        }
    }
    private void RenderIdentity(DataTable students, int admissionId)
    {
        DataRow? row = students
            .AsEnumerable()
            .FirstOrDefault(r =>
                r["AdmissionId"] != DBNull.Value &&
                Convert.ToInt32(r["AdmissionId"]) == admissionId);
        if (row is null)
        {
            nameLabel.Text = "Student not found.";
            fatherLabel.Text = string.Empty;
            admissionDateLabel.Text = string.Empty;
            dateOfBirthLabel.Text = string.Empty;
            genderLabel.Text = string.Empty;
            admissionCampusLabel.Text = string.Empty;
            placementLabel.Text = string.Empty;
            registrationLabel.Text = string.Empty;
            contactLabel.Text = string.Empty;
            return;
        }
        string name = CellText(row, "StudentName");
        string className = CellText(row, "ClassName");
        string sectionName = CellText(row, "SectionName");
        string status = CellText(row, "Status");
        nameLabel.Text = $"{AdmissionNumbers.ForId(admissionId)} - {name}";
        RenderIdentityFacts(row);
        string placement =
            string.IsNullOrWhiteSpace(className)
                ? "Not currently enrolled"
                : string.IsNullOrWhiteSpace(sectionName)
                    ? className
                    : $"{className} - {sectionName}";
        placementLabel.Text =
            string.IsNullOrWhiteSpace(status)
                ? placement
                : $"{placement} · {status}";
    }
    // Fact rendering is deliberately independent of the admission-number
    // lookup so SQL-free UI contracts can verify it with synthetic records.
    private void RenderIdentityFacts(DataRow row)
    {
        string father = CellText(row, "FatherName");
        string registration = CellText(row, "RegistrationNumber");
        string contact = CellText(row, "ContactNumber");
        fatherLabel.Text = string.IsNullOrWhiteSpace(father) ? "" : $"Father: {father}";
        admissionDateLabel.Text = "Admitted: " + FactDate(row, "AdmissionDate");
        dateOfBirthLabel.Text = "Born: " + FactDate(row, "DateOfBirth");
        genderLabel.Text = "Gender: " + FactText(row, "Gender");
        admissionCampusLabel.Text = "Admission campus: " + FactText(row, "AdmissionCampusName");
        registrationLabel.Text = string.IsNullOrWhiteSpace(registration) ? "" : $"Registration No.: {registration}";
        contactLabel.Text = string.IsNullOrWhiteSpace(contact) ? "" : $"Contact: {contact}";
    }
    private void RenderPhoto(DataTable photoTable)
    {
        StudentPhotoViewForm.SetPreviewImage(photo, null);
        if (photoTable.Rows.Count == 0 ||
            !photoTable.Columns.Contains("PhotoData"))
        {
            return;
        }
        try
        {
            var bytes =
                (byte[])photoTable.Rows[0]["PhotoData"];
            using var stream =
                new MemoryStream(bytes);
            using var loaded =
                Image.FromStream(stream);
            StudentPhotoViewForm.SetPreviewImage(photo, new Bitmap(loaded));
        }
        catch (Exception nonFatalException76)
        {
            FMSC_SMS.Core.AppExceptionHandler.LogNonFatal(nonFatalException76, "StudentProfilePage: RenderPhoto fallback");
            // A corrupt or unreadable photo shouldn't block the rest of the profile.
        }
    }
    private void RenderFamily(DataTable familyLookup, int admissionId)
    {
        if (familyLookup.Rows.Count == 0 ||
            !familyLookup.Columns.Contains("FamilyId"))
        {
            familyEmptyLabel.Text = "This student does not currently belong to a family.";
            familyGrid.DataSource = null;
            familyGrid.Visible = false;
            familyEmptyLabel.Visible = true;
            return;
        }
        // The family procedure returns every member, including the selected child.
        var members = familyLookup.Clone();
        foreach (DataRow member in familyLookup.Rows)
            if (member["AdmissionId"] != DBNull.Value && Convert.ToInt32(member["AdmissionId"]) != admissionId)
                members.ImportRow(member);
        if (IsDisposed ||
            student.SelectedId != admissionId)
        {
            return;
        }
        if (members.Rows.Count == 0)
        {
            familyEmptyLabel.Text = "No other admitted children in this family.";
            familyGrid.DataSource = null;
            familyGrid.Visible = false;
            familyEmptyLabel.Visible = true;
            return;
        }
        familyEmptyLabel.Visible = false;
        familyGrid.Visible = true;
        string[] visible = { "AdmissionNumber", "StudentName", "ClassSection", "CampusName", "Status" };
        if (!members.Columns.Contains("ClassSection"))
        {
            members.Columns.Add("ClassSection", typeof(string));
            foreach (DataRow member in members.Rows)
                member["ClassSection"] = PlacementText(member);
        }
        BindMiniGrid(familyGrid, members);
        int order = 0;
        foreach (string name in visible)
            if (familyGrid.Columns[name] is { } column)
            {
                column.DisplayIndex = order++;
                column.HeaderText = name switch {
                    "AdmissionNumber" => "Adm. No.", "StudentName" => "Student",
                    "ClassSection" => "Class", "CampusName" => "Campus", _ => "Status"
                };
            }
        foreach (DataGridViewColumn column in familyGrid.Columns)
            column.Visible = visible.Contains(column.DataPropertyName);
        familyGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        foreach (DataGridViewColumn column in familyGrid.Columns)
        {
            column.MinimumWidth = AppScale.Scale(55);
            column.FillWeight = column.DataPropertyName switch
            {
                "AdmissionNumber" => 15,
                "StudentName" => 25,
                "ClassSection" => 20,
                "CampusName" => 22,
                "Status" => 18,
                _ => 15
            };
        }
        familyGrid.ClearSelection();
    }
    private static async Task<(bool Available, decimal Amount, Exception? Error)> ReadBalanceAsync(int admissionId, CancellationToken token)
    {
        // Everyone with Students.View may see the profile balance. Keep the
        // dedicated procedure; do not broaden permission on financial reports.
        try
        {
            object? value = await DatabaseHelper.ScalarUserAsync(
                "GetStudentProfileOutstandingBalance", token,
                DatabaseHelper.Int("@AdmissionId", admissionId)).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return (true, value is null or DBNull ? 0m : Convert.ToDecimal(value), null);
        }
        catch (Exception) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 2812 or 208)
        {
            return (false, 0m, null);
        }
        catch (Exception ex) { return (false, 0m, ex); }
    }
    private void RenderBalance((bool Available, decimal Amount, Exception? Error) result)
    {
        if (result.Error is not null) { Fail(result.Error); return; }
        balanceLabel.Text = result.Available
            ? $"Outstanding Balance: Rs. {result.Amount:N0}"
            : "Outstanding Balance: unavailable. Contact the administrator.";
        balanceLabel.ForeColor = !result.Available ? AppTheme.MutedText
            : result.Amount > 0 ? AppTheme.Danger : AppTheme.Success;
    }
    /* The identity card above already shows name, father, registration number and contact. */
    private static readonly string[] ContextColumns =
    {
        "AcademicYearName", "YearName", "CurrentCampusName", "ClassSection",
        "EnrollmentStatus", "EnrollmentDate", "TransportText"
    };
    private void BindContextGrid(DataTable table)
    {
        var display = table.Copy();
        display.Columns.Add("ClassSection", typeof(string));
        display.Columns.Add("TransportText", typeof(string));
        foreach (DataRow row in display.Rows)
        {
            row["ClassSection"] = PlacementText(row);
            row["TransportText"] = display.Columns.Contains("UsesTransport") && row["UsesTransport"] != DBNull.Value
                && Convert.ToBoolean(row["UsesTransport"]) ? "Yes" : "No";
        }
        BindMiniGrid(contextGrid, display);
        int order = 0;
        foreach (string name in ContextColumns)
        {
            DataGridViewColumn? column = contextGrid.Columns[name];
            if (column is not null)
                column.DisplayIndex = order++;
        }
        // Some detail versions expose both names for the same academic year.
        // Show one year column, with a fallback for older records.
        bool yearNamePresent = display.Columns.Contains("YearName") &&
            display.AsEnumerable().Any(row => !string.IsNullOrWhiteSpace(CellText(row, "YearName")));
        bool legacyYearPresent = display.Columns.Contains("AcademicYearName");
        foreach (DataGridViewColumn column in contextGrid.Columns)
        {
            bool requested = Array.IndexOf(ContextColumns, column.DataPropertyName) >= 0;
            column.Visible = requested &&
                (column.DataPropertyName != "YearName" || yearNamePresent || !legacyYearPresent) &&
                (column.DataPropertyName != "AcademicYearName" || !yearNamePresent);
        }
        SetHeader("CurrentCampusName", "Campus");
        SetHeader("EnrollmentStatus", "Status");
        SetHeader("TransportText", "Transport");
        SetHeader("ClassSection", "Class");
        SetHeader("YearName", "Academic Year");
        SetHeader("AcademicYearName", "Academic Year");
        contextGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        foreach (DataGridViewColumn column in contextGrid.Columns)
        {
            column.MinimumWidth = AppScale.Scale(55);
            column.FillWeight = column.DataPropertyName switch
            {
                "AcademicYearName" or "YearName" => 23,
                "CurrentCampusName" => 24,
                "ClassSection" => 20,
                "EnrollmentStatus" => 16,
                "EnrollmentDate" => 21,
                "TransportText" => 12,
                _ => 15
            };
        }
        contextGrid.ClearSelection();
    }
    private static string PlacementText(DataRow row)
    {
        string className = CellText(row, "ClassName");
        string section = CellText(row, "SectionName");
        return string.IsNullOrWhiteSpace(section) ? className
            : string.IsNullOrWhiteSpace(className) ? section : $"{className} - {section}";
    }
    private void SetHeader(string column, string text)
    {
        DataGridViewColumn? found = contextGrid.Columns[column];
        if (found is not null)
            found.HeaderText = text;
    }
    private static void BindMiniGrid(DataGridView grid, DataTable table)
    {
        grid.DataSource = null;
        grid.DataSource = table;
        grid.AutoSizeColumnsMode =
            DataGridViewAutoSizeColumnsMode.None;
    }
    private void ConfigureTimelineGrid()
    {
        timelineGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        timelineGrid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
        timelineGrid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        timelineGrid.DefaultCellStyle.Alignment = DataGridViewContentAlignment.TopLeft;
        timelineGrid.ScrollBars = ScrollBars.Both;
        foreach (DataGridViewColumn column in timelineGrid.Columns)
        {
            // Keep dates and event labels compact, leaving room for the full description.
            column.MinimumWidth = AppScale.Scale(40);
            column.FillWeight = column.DataPropertyName switch
            {
                "EventDate" => 18,
                "EventType" => 25,
                "Description" => 57,
                _ => 30
            };
        }
        timelineGrid.ClearSelection();
    }
    private static string FactText(DataRow row, string name)
    {
        var text = CellText(row, name);
        return string.IsNullOrWhiteSpace(text) ? "—" : text;
    }
    private static string FactDate(DataRow row, string name)
    {
        if (!row.Table.Columns.Contains(name) || row[name] is DBNull) return "—";
        return row[name] is DateTime date ? date.ToString("dd-MMM-yyyy") : FactText(row, name);
    }
    private static string CellText(
        DataRow row,
        string columnName) =>
        row.Table.Columns.Contains(columnName) &&
        row[columnName] != DBNull.Value
            ? Convert.ToString(
                row[columnName]) ?? string.Empty
            : string.Empty;
    private void ClearProfile()
    {
        UpdateStudentShortcuts();
        profileReads.Cancel();
        nameLabel.Text =
            "Select a student to see their full profile.";
        fatherLabel.Text = string.Empty;
        admissionDateLabel.Text = string.Empty;
        admissionCampusLabel.Text = string.Empty;
        dateOfBirthLabel.Text = string.Empty;
        genderLabel.Text = string.Empty;
        placementLabel.Text = string.Empty;
        registrationLabel.Text = string.Empty;
        contactLabel.Text = string.Empty;
        balanceLabel.Text = string.Empty;
        StudentPhotoViewForm.SetPreviewImage(photo, null);
        contextGrid.DataSource = null;
        familyGrid.DataSource = null;
        familyGrid.Visible = true;
        familyEmptyLabel.Visible = false;
        timelineGrid.DataSource = null;
        SetStatus("Select a student to see their full profile.");
    }
}

