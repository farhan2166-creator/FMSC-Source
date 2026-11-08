using FMSC_SMS.Core;
using FMSC_SMS.Pages.Finance.Collection;
using FMSC_SMS.Pages.Communication;
using FMSC_SMS.Pages.Finance.Billing;
using FMSC_SMS.Pages.Reports;
using FMSC_SMS.Pages.Reports.Finance;
using FMSC_SMS.Pages.Reports.Students;
using FMSC_SMS.Pages.Setup;
using FMSC_SMS.Pages.Students.Admissions;
using FMSC_SMS.Pages.Students.Certificates;
using FMSC_SMS.Pages.Students.Profile;
namespace FMSC_SMS.Pages.Dashboard;
public sealed class WelcomePage : UserControl
{
    private enum Kpi
    {
        TotalStudents,
        ActiveStudents,
        TotalClasses,
        ActiveStaff,
        NewAdmissions,
    }
    private readonly Action<Func<Control>> navigate;
    /* A quick action opens a page of its own module, so the shell is told which one instead of navigating with the dashboard's own module and title. */
    private readonly Action<Func<Control>, string, string>? navigateInModule;
    private readonly Dictionary<Kpi, (Label Value, Label Caption)> kpiLabels = new();
    private readonly Func<string, CancellationToken, Task<DataTable>> query;
    private readonly Func<CancellationToken, Task<DataTable>> checkReadiness;
    private readonly LatestReadScope reads = new();
    private TableLayoutPanel? root;
    private HumanCard? quickActionsCard;
    private RoundedPanel? setupReadinessBar;
    private Label? setupStatus;
    private RowStyle? setupReadinessRow;
    private bool setupReadinessShown;
    private bool updatingLayout;
    private readonly List<Control> dashboardPageSurfaces = new();
    private static int GreetingTitleRowHeight =>
        Math.Max(32, AppMetrics.TextHeight(AppTheme.PageTitleFont) + 6);
    private static int GreetingSubtitleRowHeight =>
        Math.Max(22, AppMetrics.TextHeight(AppTheme.NormalFont) + 4);
    private static int GreetingHeaderHeight => GreetingTitleRowHeight + GreetingSubtitleRowHeight + 8;
    private static Font KpiValueFont => AppTheme.SharedFont("Segoe UI", 22F * AppScale.BodyFontScale, FontStyle.Bold);
    private static int KpiTitleRowHeight => Math.Max(22, AppMetrics.TextHeight(AppTheme.NormalFont) + 4);
    private static int KpiValueRowHeight => Math.Max(32, AppMetrics.TextHeight(KpiValueFont) + 4);
    private static int KpiCaptionRowHeight => Math.Max(22, AppMetrics.TextHeight(AppTheme.NormalFont) + 4);
    private static int KpiGridRowHeight => AppScale.S(32) + KpiTitleRowHeight + KpiValueRowHeight + KpiCaptionRowHeight;
    private static int QuickActionHeight => Math.Max(AppMetrics.LargeButtonHeight, AppScale.S(58));
    public WelcomePage(
        Action<Func<Control>> navigate,
        Action<Func<Control>, string, string>? navigateInModule = null)
        : this(navigate, navigateInModule,
            (procedure, token) => DatabaseHelper.QueryUserAsync(procedure, token),
            FMSC_SMS.Services.SetupReadiness.CheckAsync)
    {
    }

    internal WelcomePage(Action<Func<Control>> navigate,
        Action<Func<Control>, string, string>? navigateInModule,
        Func<string, CancellationToken, Task<DataTable>> query,
        Func<CancellationToken, Task<DataTable>> checkReadiness)
    {
        this.navigate = navigate;
        this.navigateInModule = navigateInModule;
        this.query = query;
        this.checkReadiness = checkReadiness;
        AutoScaleMode = AutoScaleMode.None;
        Dock = DockStyle.Fill;
        BackColor = AppTheme.PageBackground;
        Padding = new Padding(
            UiDesignSystem.Spacing.Xl,
            AppMetrics.PageTopPadding,
            UiDesignSystem.Spacing.Xl,
            UiDesignSystem.Spacing.Xl);
        AutoScroll = true;
        Build();
        AppTheme.ThemeChanged += OnDashboardThemeChanged;
        Disposed += (_, _) => {
            AppTheme.ThemeChanged -= OnDashboardThemeChanged;
            reads.Dispose();
        };
        Defer(() => _ = LoadDashboardDataAsync());
    }
    private T DashboardPageSurface<T>(T control) where T : Control
    {
        control.BackColor = AppTheme.PageBackground;
        dashboardPageSurfaces.Add(control);
        return control;
    }
    private void OnDashboardThemeChanged(object? sender, EventArgs e)
    {
        if (IsDisposed)
            return;
        BackColor = AppTheme.PageBackground;
        foreach (Control surface in dashboardPageSurfaces)
        {
            if (!surface.IsDisposed)
                surface.BackColor = AppTheme.PageBackground;
        }
        Invalidate(true);
    }
    private void Build()
    {
        root = DashboardPageSurface(new TableLayoutPanel {
            Dock = DockStyle.Top, ColumnCount = 1, RowCount = 5,
            Padding = Padding.Empty, Margin = Padding.Empty
        });
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, GreetingHeaderHeight));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, KpiGridRowHeight));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute));
        setupReadinessRow = new RowStyle(SizeType.Absolute);
        root.RowStyles.Add(setupReadinessRow);
        // Keep actions compact on large monitors; extra room belongs to whitespace.
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(BuildDashboardHeader(), 0, 0);
        root.Controls.Add(BuildKpiGrid(), 0, 1);
        quickActionsCard = BuildQuickActionsCard();
        quickActionsCard.Margin = new Padding(0, UiDesignSystem.Spacing.Md, 0, 0);
        root.Controls.Add(quickActionsCard, 0, 2);
        if (CurrentUser.HasPermission("System.Setup") || CurrentUser.HasPermission("Calendar.Manage") ||
            CurrentUser.HasPermission("Fees.Configuration.Manage"))
        {
            setupReadinessShown = true;
            setupReadinessBar = BuildSetupReadinessBar();
            root.Controls.Add(setupReadinessBar, 0, 3);
        }
        Controls.Add(root);
        UpdateDashboardLayout();
    }
    private Control BuildDashboardHeader()
    {
        var layout = DashboardPageSurface(new TableLayoutPanel {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0, 0, 0, 8),
            Padding = new Padding(2, 0, 2, 0)
        });
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, GreetingTitleRowHeight));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label {
            Text = $"Welcome, {CurrentUser.Username}",
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Font = AppTheme.PageTitleFont,
            ForeColor = AppTheme.HeadingText,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        }, 0, 0);
        layout.Controls.Add(new Label {
            Text = "School totals and admissions this month. Choose a quick action to begin.",
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Font = AppTheme.NormalFont,
            ForeColor = AppTheme.MutedText,
            TextAlign = ContentAlignment.TopLeft,
            AutoEllipsis = true
        }, 0, 1);
        return layout;
    }
    private Control BuildKpiGrid()
    {
        var grid = DashboardPageSurface(new TableLayoutPanel {
            Dock = DockStyle.Fill,
            ColumnCount = 5,
            RowCount = 1,
            Padding = new Padding(0, 4, 0, 4),
            Margin = new Padding(0)
        });
        for (int i = 0; i < 5; i++)
            grid.ColumnStyles.Add(
                new ColumnStyle(SizeType.Percent, 20F));
        PlaceKpi(grid, Kpi.TotalStudents, 0, "Total Students");
        PlaceKpi(grid, Kpi.ActiveStudents, 1, "Active Students");
        PlaceKpi(grid, Kpi.TotalClasses, 2, "Total Classes");
        PlaceKpi(grid, Kpi.ActiveStaff, 3, "Active Staff");
        PlaceKpi(grid, Kpi.NewAdmissions, 4, "New Admissions");
        return grid;
    }
    private async Task LoadAcademicYearAsync(LatestReadScope.Request request)
    {
        try
        {
            var years = await query("GetAcademicYears", request.Token);
            if (!CanUpdate(request)) return;
            UpdateAcademicYear(DashboardData.AcademicYearLabel(years));
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!CanUpdate(request)) return;
            AppExceptionHandler.LogNonFatal(ex, "Dashboard academic year");
            UpdateAcademicYear("Academic Year — Unavailable");
        }
    }

    private void UpdateAcademicYear(string text)
    {
        if (FindForm() is MainDashboardForm dashboard)
            dashboard.UpdateTopAcademicYearLabel(this, text);
    }

    private bool CanUpdate(LatestReadScope.Request request) =>
        !IsDisposed && !Disposing && reads.IsCurrent(request);

    private void Navigate(Func<Control> factory, string module, string title)
    {
        if (navigateInModule is not null) navigateInModule(factory, module, title);
        else navigate(factory);
    }
    private void PlaceKpi(TableLayoutPanel grid, Kpi kpi, int column, string title)
    {
        var (card, value, caption) = BuildKpiCard(title);
        card.Margin = new Padding(0, 0, column == 4 ? 0 : UiDesignSystem.Spacing.Sm, 0);
        kpiLabels[kpi] = (value, caption);
        grid.Controls.Add(card, column, 0);
    }

    private (RoundedPanel Card, Label Value, Label Caption) BuildKpiCard(string title)
    {
        var card = new RoundedPanel {
            Dock = DockStyle.Fill, BackColor = AppTheme.Surface, BorderColor = AppTheme.Border,
            ShowBorder = true, CornerRadius = AppScale.S(4), Padding = new Padding(AppScale.S(12))
        };
        var layout = new TableLayoutPanel {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3,
            Margin = Padding.Empty, Padding = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, KpiTitleRowHeight));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, KpiValueRowHeight));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Label TextLine(string text, Font font, Color color) => new() {
            Text = text, Dock = DockStyle.Fill, Margin = Padding.Empty, Font = font,
            ForeColor = color, TextAlign = ContentAlignment.MiddleCenter, AutoEllipsis = true
        };
        var value = TextLine("—", KpiValueFont, AppTheme.Primary);
        var caption = TextLine("Loading...", AppTheme.NormalFont, AppTheme.MutedText);
        layout.Controls.Add(TextLine(title, AppTheme.BodyBoldFont, AppTheme.Text), 0, 0);
        layout.Controls.Add(value, 0, 1);
        layout.Controls.Add(caption, 0, 2);
        card.Controls.Add(layout);
        return (card, value, caption);
    }

    private RoundedPanel BuildSetupReadinessBar()
    {
        var bar = new RoundedPanel {
            Dock = DockStyle.Fill, BackColor = AppTheme.SurfaceSecondary, BorderColor = AppTheme.Border,
            ShowBorder = true, CornerRadius = AppScale.S(4), Padding = new Padding(UiDesignSystem.Spacing.Sm),
            Margin = new Padding(0, UiDesignSystem.Spacing.Md, 0, 0)
        };
        var layout = new TableLayoutPanel {
            Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, AppScale.S(32)));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, AppScale.S(170)));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label {
            Text = "\uE946", Font = AppTheme.IconFont(18F * AppScale.BodyFontScale),
            ForeColor = AppTheme.MutedText, Dock = DockStyle.Fill, Margin = Padding.Empty,
            TextAlign = ContentAlignment.MiddleCenter, AccessibleName = "School setup status"
        }, 0, 0);
        setupStatus = new Label {
            Text = "Checking school setup…", Dock = DockStyle.Fill, Font = AppTheme.NormalFont,
            ForeColor = AppTheme.Text, TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true, Margin = new Padding(UiDesignSystem.Spacing.Sm, 0, UiDesignSystem.Spacing.Sm, 0)
        };
        layout.Controls.Add(setupStatus, 1, 0);
        var check = new ModernButton {
            Text = "Check Readiness", ThemeRole = "Secondary", Dock = DockStyle.Fill,
            Margin = Padding.Empty, CornerRadius = AppScale.S(4)
        };
        check.Click += (_, _) => Navigate(() => new SetupReadinessPage(Navigate), "School Setup", PageTitles.SetupReadiness);
        layout.Controls.Add(check, 2, 0);
        bar.Controls.Add(layout);
        return bar;
    }
    private HumanCard BuildQuickActionsCard()
    {
        var card = HumanUi.Card(
            "Quick Actions",
            "Open a page to start your work.");
        card.CornerRadius = AppScale.S(4);
        var table = BuildQuickActionsGrid();
        card.Content.Controls.Add(table);
        PopulateQuickActions(table);
        return card;
    }
    private TableLayoutPanel BuildQuickActionsGrid()
    {
        var table = new TableLayoutPanel {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Padding = new Padding(2),
            Margin = new Padding(0)
        };
        // Configure equal column widths
        for (int i = 0; i < 3; i++)
        {
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F));
        }
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        return table;
    }
    private void PopulateQuickActions(TableLayoutPanel table)
    {
        var oldControls = table.Controls.Cast<Control>().ToArray();
        table.Controls.Clear();
        foreach (var oldControl in oldControls)
            oldControl.Dispose();
        var actions = new (string Text, string Module, Func<Control> Factory, Func<bool> Allowed)[]
        {
        (PageTitles.Admissions, "Students", () => AdmissionsWorkspace.Create(Navigate), () => DirectAdmissionAccess.CanCreate(CurrentUser.HasPermission)),
        ("Fee Collection", "Finance", () => new FeeCollectionPage(), () => FeeCollectionAccess.CanUse(CurrentUser.HasPermission)),
        (PageTitles.StudentFeeAccount, "Finance", () => StudentAccountStatementPage.CreateAccount(), () => StudentAccountStatementPage.CanOpenAccount),
        (PageTitles.FeePosting, "Finance", () => FeePostingWorkspace.Create(Navigate), () => CurrentUser.HasPermission("Fees.Collect")),
        ("Student Profile", "Students", () => new StudentProfilePage(Navigate), () => CurrentUser.HasPermission("Students.View")),
        ("Student Reports", "Students", () => new StudentReportsPage(), () => CurrentUser.HasPermission("Students.View") && CurrentUser.HasPermission("Reports.View")),
        (PageTitles.StudentCertificates, "Certificates", () => StudentCertificatesPage.CreateDashboardView(Navigate), () => CertificateWorkspaceAccess.CanOpenHub(CurrentUser.HasPermission)),
        ("Reports", "Reports", () => new ReportsHomePage(Navigate), () => CurrentUser.HasPermission("Reports.View")),
        ("Communication Center", "Communication", () => new CommunicationCenterPage(),
            () => CurrentUser.HasPermission("Communication.View"))
        };
        var visibleActions = actions
            .Where(action => action.Allowed())
            .ToArray();
        int rows = Math.Max(1, (int)Math.Ceiling(visibleActions.Length / 3d));
        table.RowCount = rows;
        table.RowStyles.Clear();
        for (int row = 0; row < rows; row++)
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 100F / rows));
        if (visibleActions.Length == 0)
        {
            var guidance = new Label {
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
                Text = "Use the sidebar to open the pages available to your account.",
                Font = AppTheme.NormalFont, ForeColor = AppTheme.MutedText
            };
            table.Controls.Add(guidance, 0, 0);
            table.SetColumnSpan(guidance, 3);
        }
        for (int i = 0; i < visibleActions.Length; i++)
        {
            int row = i / 3;
            int col = i % 3;
            var action = visibleActions[i];
            var button = CreateQuickActionButton(action.Text,
                                              action.Module,
                                              action.Factory,
                                              action.Text);
            button.Dock = DockStyle.Fill;
            button.Size = new Size(0, button.Height);
            button.MinimumSize = new Size(0, QuickActionHeight);
            table.Controls.Add(button, col, row);
        }
    }
    private ModernButton CreateQuickActionButton(string text, string module, Func<Control> factory, string? title = null)
    {
        string glyph = text switch {
            PageTitles.Admissions => "\uE8A5",
            "Fee Collection" => "\uE8C7",
            "Student Profile" => "\uE77B",
            PageTitles.FamilyAdmission => "\uE8FA",
            "Student Reports" => "\uE9D5",
            PageTitles.StudentCertificates => "\uE8D7",
            "Reports" => "\uE9D9",
            "Communication Center" => "\uE8F2",
            _ => "\uE80F"
        };
        var button = new DashboardActionButton(glyph) {
            Text = text, Height = QuickActionHeight, Margin = new Padding(4),
            ThemeRole = "Secondary", CornerRadius = AppScale.S(4), Padding = new Padding(AppScale.S(12), 0, AppScale.S(12), 0)
        };
        button.Click += (_, _) => Navigate(factory, module, title ?? text);
        return button;
    }

    private sealed class DashboardActionButton(string glyph) : ModernButton
    {
        protected override Rectangle PaintLeadingContent(Graphics graphics, Rectangle bounds, Color textColor)
        {
            int size = AppScale.S(26);
            var icon = new Rectangle(bounds.Left, bounds.Top, size, bounds.Height);
            TextRenderer.DrawText(graphics, glyph, AppTheme.IconFont(18F * AppScale.BodyFontScale), icon,
                textColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            int space = size + UiDesignSystem.Spacing.Sm;
            return new Rectangle(bounds.Left + space, bounds.Top, Math.Max(0, bounds.Width - space), bounds.Height);
        }
    }
    private async Task LoadDashboardDataAsync()
    {
        if (IsDisposed || Disposing) return;
        var request = reads.Begin();
        UpdateAcademicYear("Academic Year — Loading...");
        // Each section paints as soon as its own read finishes. Setup/backup history
        // and academic-year failures must not hold up or erase successfully loaded totals.
        await Task.WhenAll(LoadSummaryAsync(request), LoadAcademicYearAsync(request),
            UpdateSetupReadinessAsync(request));
    }

    private async Task LoadSummaryAsync(LatestReadScope.Request request)
    {
        if (!CurrentUser.HasPermission("Reports.View"))
        {
            ShowUnavailableState("Report access required");
            return;
        }
        try
        {
            var summary = await query("GetDashboardSummary", request.Token);
            if (!CanUpdate(request)) return;
            if (summary.Rows.Count == 0)
            {
                ShowUnavailableState("No dashboard data available");
                return;
            }
            RenderSummary(summary.Rows[0]);
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!CanUpdate(request)) return;
            AppExceptionHandler.LogNonFatal(ex, "Dashboard summary");
            ShowLoadError(ex);
        }
    }

    private void RenderSummary(DataRow row)
    {
        // These COUNT columns are part of the verified DACPAC contract. A real
        // zero is valid; missing/malformed data is an error, never a synthetic zero.
        var (totalStudents, activeStudents, totalClasses, activeStaff, newAdmissions) = DashboardData.ReadCounts(row);
        SetKpi(Kpi.TotalStudents, totalStudents.ToString("N0"), "All registered students");
        SetKpi(Kpi.ActiveStudents, activeStudents.ToString("N0"), DashboardData.ActiveStudentCaption(totalStudents, activeStudents));
        SetKpi(Kpi.TotalClasses, totalClasses.ToString("N0"), "Configured classes");
        SetKpi(Kpi.ActiveStaff, activeStaff.ToString("N0"), "Active staff records");
        SetKpi(Kpi.NewAdmissions, newAdmissions.ToString("N0"), $"Since {new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1):dd MMM}");
    }

    private async Task UpdateSetupReadinessAsync(LatestReadScope.Request request)
    {
        if (setupReadinessBar is null || setupStatus is null) return;
        try
        {
            var issues = await checkReadiness(request.Token);
            if (!CanUpdate(request)) return;
            int count = issues.Rows.Count;
            setupStatus.Text = count == 1 ? "School setup: 1 item needs attention" : $"School setup: {count:N0} items need attention";
            SetSetupReadinessVisible(count > 0);
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!CanUpdate(request)) return;
            AppExceptionHandler.LogNonFatal(ex, "Dashboard setup readiness");
            setupStatus.Text = "School setup status unavailable. Check Readiness to retry.";
            SetSetupReadinessVisible(true);
        }
    }

    private void SetSetupReadinessVisible(bool visible)
    {
        if (setupReadinessBar is null) return;
        setupReadinessShown = visible;
        setupReadinessBar.Visible = visible;
        UpdateDashboardLayout();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateDashboardLayout();
    }

    private void UpdateDashboardLayout()
    {
        if (updatingLayout || root is null || quickActionsCard is null || setupReadinessRow is null) return;
        updatingLayout = true;
        try
        {
            var actions = quickActionsCard.Content.Controls.OfType<TableLayoutPanel>().Single();
            int quickHeight = quickActionsCard.Header.Height + quickActionsCard.Padding.Vertical +
                quickActionsCard.Content.Padding.Vertical + quickActionsCard.Margin.Vertical +
                actions.RowCount * (QuickActionHeight + 8) + actions.Padding.Vertical;
            root.RowStyles[2].Height = quickHeight;
            int setupHeight = setupReadinessBar is not null && setupReadinessShown
                ? setupReadinessBar.Padding.Vertical + setupReadinessBar.Margin.Vertical +
                    Math.Max(AppMetrics.ButtonHeight, AppMetrics.TextHeight(AppTheme.NormalFont) + AppScale.S(8))
                : 0;
            setupReadinessRow.Height = setupHeight;
            int minimumHeight = GreetingHeaderHeight + KpiGridRowHeight + quickHeight + setupHeight;
            root.Height = Math.Max(minimumHeight, ClientSize.Height - Padding.Vertical);
        }
        finally { updatingLayout = false; }
    }
    private void SetKpi(
        Kpi kpi,
        string value,
        string caption)
    {
        if (!kpiLabels.TryGetValue(
                kpi,
                out var labels))
        {
            return;
        }
        labels.Value.Text = value;
        labels.Caption.Text = caption;
    }
    private void ShowUnavailableState(string message)
    {
        foreach (Kpi kpi in Enum.GetValues<Kpi>()) SetKpi(kpi, "—", message);
    }

    private void ShowLoadError(Exception ex)
    {
        bool procedureMissing = ex is Microsoft.Data.SqlClient.SqlException sql && sql.Number is 2812 or 208;
        ShowUnavailableState(procedureMissing ? "Dashboard procedure unavailable" : "Unavailable — reopen Dashboard");
    }
    private void Defer(Action action)
    {
        if (IsDisposed)
            return;
        if (IsHandleCreated)
        {
            BeginInvoke(new MethodInvoker(() => {
                if (!IsDisposed)
                    action();
            }));
            return;
        }
        EventHandler? handler = null;
        handler = (_, _) => {
            HandleCreated -= handler;
            if (IsDisposed)
                return;
            BeginInvoke(new MethodInvoker(() => {
                if (!IsDisposed)
                    action();
            }));
        };
        HandleCreated += handler;
    }
}
