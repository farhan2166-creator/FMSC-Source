namespace FMSC_SMS.Core;
public abstract class V2Page : UserControl, IWorkspaceStateHost
{
    protected Color Accent;
    private sealed class BufferedDataGridView : KeyboardDataGridView
    {
        public BufferedDataGridView()
        {
            DoubleBuffered = true;
        }
    }
    protected readonly DataGridView Grid = new BufferedDataGridView {
        Dock = DockStyle.Fill,
        AutoGenerateColumns = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        ReadOnly = true,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
        AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
        RowHeadersVisible = false,
        BackgroundColor = AppTheme.Surface,
        BorderStyle = BorderStyle.None,
        CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
        EnableHeadersVisualStyles = false,
        ColumnHeadersHeight = AppMetrics.GridHeaderHeight,
        RowTemplate = { Height = AppMetrics.GridRowHeight }
    };
    protected readonly FlowLayoutPanel Actions = new() {
        Dock = DockStyle.Fill,
        AutoSize = false,
        WrapContents = false,
        FlowDirection = FlowDirection.LeftToRight,
        Padding = new Padding(0),
        Margin = new Padding(0),
        AutoScroll = true
    };
    public string NavigationTitle { get; }
    protected Label ContextLabel = null!;
    protected Label StatusLabel = null!;
    private PageStatusKind _statusKind = PageStatusKind.Info;
    private Action? _fitActionBar;
    /// <summary>Carries the FULL text of the status line.</summary>
    private readonly ToolTip _statusToolTip = new() {
        InitialDelay = 500,
        ReshowDelay = 100,
        AutoPopDelay = 6000,
        ShowAlways = true
    };
    private readonly PageActionShortcutController _shortcutController;
    private DataTable? _reportSource;
    private Panel? _reportPager;
    private Label? _reportPageLabel;
    private ModernButton? _reportPreviousButton;
    private ModernButton? _reportNextButton;
    private int _reportPage;
    private const int ReportPageSize = 500;
    private readonly ReportLoadCoordinator _reportLoads = new();
    /// <summary>True once the filters the displayed report was generated from are no longer the ones on screen.</summary>
    private bool _reportStale;
    /// <summary>True once the last <see cref="BindReportAsync(Func{DataTable})"/> call actually bound a table.</summary>
    private bool _reportLanded;
    /// <summary>Track export actions so stale reports cannot be exported.</summary>
    private readonly List<ModernButton> _exportButtons = new();
    private RoundedPanel? _pageHeader;
    private TableLayoutPanel? _shellRoot;
    private Label? _titleLabel;
    private TableLayoutPanel? _headerTextLayout;
    private TableLayoutPanel? _headerTitleLine;
    private TableLayoutPanel? _externalStatusHeader;
    private Label? _headerGuidanceLabel;
    private string _headerGuidance = string.Empty;
    private string _contextText = string.Empty;
    private bool _headerFitQueued;
    private RoundedPanel? _actionBar;
    private RoundedPanel? _embeddedActionBar;
    private readonly Panel _content = new() {
        Dock = DockStyle.Fill,
        Padding = new Padding(0)
    };
    private readonly List<ModernButton> _relocatedActionButtons = new();
    protected V2Page(string title, string context)
    {
        _shortcutController = new PageActionShortcutController(this, _statusToolTip);
        // Shared metrics and text measurements size these controls; avoid a second WinForms autoscaling pass.
        AutoScaleMode = AutoScaleMode.None;
        NavigationTitle = title;
        _contextText = context;
        Accent = AppTheme.ModuleAccent(title);
        BackColor = AppTheme.PageBackground;
        ForeColor = AppTheme.Text;
        Font = UiDesignSystem.Typography.Body;
        Dock = DockStyle.Fill;
        AutoScroll = true;
        Padding = new Padding(
            UiDesignSystem.Spacing.Xl,
            AppMetrics.PageTopPadding,
            UiDesignSystem.Spacing.Xl,
            UiDesignSystem.Spacing.Xl);
        var root = new TableLayoutPanel {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(0),
            Margin = new Padding(0)
        };
        int titleLabelHeight =
            Math.Max(
                20,
                AppMetrics.TextHeight(AppTheme.PageTitleFont));
        root.RowStyles.Add(
            new RowStyle(
                SizeType.Absolute,
                titleLabelHeight +
                AppMetrics.PageHeaderContentHeightAllowance));
        root.RowStyles.Add(
            new RowStyle(
                SizeType.Absolute,
                0));
        root.RowStyles.Add(
            new RowStyle(
                SizeType.Percent,
                100));
        var header =
            BuildHeader(
                title,
                context,
                titleLabelHeight);
        StatusLabel = new Label {
            Dock = DockStyle.Fill,
            Font = AppTheme.NormalFont,
            ForeColor = AppTheme.MutedText,
            TextAlign = ContentAlignment.MiddleRight,
            AutoEllipsis = true,
            AutoSize = false,
            UseMnemonic = false,
            Margin = Padding.Empty,
            Padding = new Padding(8, 4, 8, 4),
            BackColor = AppTheme.SurfaceSecondary,
            Tag = "ThemeStatus"
        };
        _pageHeader = header;
        _shellRoot = root;
        root.Controls.Add(
            header,
            0,
            0);
        root.Controls.Add(
            _content,
            0,
            2);
        Controls.Add(root);
        TabStop = true;
        StyleGrid();
        AppTheme.ThemeChanged += OnThemeChanged;
        Disposed += (_, _) => {
            AppTheme.ThemeChanged -= OnThemeChanged;
            /* Detach report data on disposal while any uncancellable worker read finishes. */
            _reportSource = null;
            if (!Grid.IsDisposed)
                Grid.DataSource = null;
            _statusToolTip.Dispose();
            foreach (FormValidator validator in _validators)
                validator.Dispose();
            _reportLoads.Dispose();
        };
        // Measure guidance after docking and when the available width changes.
        HandleCreated += (_, _) => ScheduleHeaderFit();
        VisibleChanged += (_, _) => ScheduleHeaderFit();
        SizeChanged += (_, _) => ScheduleHeaderFit();
        Load += (_, _) => ActionButtonStyle.Arrange(Actions);
        Defer(ApplyScale);
    }
    private RoundedPanel BuildHeader(
        string title,
        string context,
        int titleLabelHeight)
    {
        var header = new RoundedPanel {
            Tag = "ThemePageHeader",
            Dock = DockStyle.Fill,
            BackColor = AppTheme.PageBackground,
            BorderColor = AppTheme.PageBackground,
            ShowBorder = false,
            CornerRadius = 0,
            Padding = new Padding(2, 0, 2, 0),
            Margin = new Padding(0, 0, 0, UiDesignSystem.Spacing.Sm)
        };
        var accentBar = new Panel {
            Dock = DockStyle.Left,
            Width = Math.Max(3, AppScale.Scale(4)),
            BackColor = Accent,
            Margin = new Padding(0, 4, UiDesignSystem.Spacing.Md, 4)
        };
        var titlePanel = new TableLayoutPanel {
            Tag = "ThemeHeaderContainer",
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(0),
            Margin = new Padding(0)
        };
        titlePanel.RowStyles.Add(
            new RowStyle(
                SizeType.Absolute,
                titleLabelHeight));
        titlePanel.RowStyles.Add(
            new RowStyle(
                SizeType.Absolute,
                0));
        var titleLine = new TableLayoutPanel {
            Tag = "ThemeHeaderContainer",
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        titleLine.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        titleLine.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        titleLine.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,0));
        var titleLabel = new Label {
            Tag = "ThemeHeaderTitle",
            Text = title,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            Font = AppTheme.PageTitleFont,
            ForeColor = AppTheme.HeadingText,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };
        ContextLabel = new Label {
            Tag = "ThemeHeaderGuidance",
            Text = string.IsNullOrWhiteSpace(context) ? string.Empty : $" ({context})",
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            Font = AppTheme.NormalFont,
            ForeColor = AppTheme.MutedText,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };
        var guidanceLabel = new Label {
            Tag = "ThemeHeaderGuidance",
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Font = AppTheme.NormalFont,
            ForeColor = AppTheme.MutedText,
            TextAlign = ContentAlignment.TopLeft,
            AccessibleName = string.Empty
        };
        guidanceLabel.Paint += (_, e) => {
            if (!string.IsNullOrWhiteSpace(_headerGuidance))
                TextRenderer.DrawText(
                    e.Graphics,
                    _headerGuidance,
                    guidanceLabel.Font,
                    guidanceLabel.ClientRectangle,
                    guidanceLabel.ForeColor,
                    TextFormatFlags.WordBreak | TextFormatFlags.Left |
                    TextFormatFlags.Top | TextFormatFlags.NoPadding);
        };
        guidanceLabel.Resize += (_, _) => ScheduleHeaderFit();
        titleLine.Controls.Add(titleLabel, 0, 0);
        titleLine.Controls.Add(ContextLabel, 1, 0);
        titlePanel.Controls.Add(titleLine, 0, 0);
        titlePanel.Controls.Add(guidanceLabel, 0, 1);
        titlePanel.Resize += (_, _) => ScheduleHeaderFit();
        titleLine.SizeChanged+=(_,_)=>_fitActionBar?.Invoke();
        header.Controls.Add(titlePanel);
        header.Controls.Add(accentBar);
        _titleLabel = titleLabel;
        _headerTextLayout = titlePanel;
        _headerTitleLine=titleLine;
        _headerGuidanceLabel = guidanceLabel;
        _statusToolTip.SetToolTip(ContextLabel, context);
        return header;
    }
    protected void SetHeaderGuidance(string? guidance)
    {
        _headerGuidance = guidance?.Trim() ?? string.Empty;
        if (_headerGuidanceLabel is null)
            return;
        _headerGuidanceLabel.AccessibleName = _headerGuidance;
        _statusToolTip.SetToolTip(_headerGuidanceLabel, _headerGuidance);
        /* Defer header sizing until the initial layout settles. */
        if (IsHandleCreated && Visible &&
            _headerTextLayout is { IsDisposed: false, ClientSize.Width: > 0 })
        {
            FitHeaderGuidance();
        }
        else
        {
            ScheduleHeaderFit();
        }
        _headerGuidanceLabel.Invalidate();
    }
    private void ScheduleHeaderFit()
    {
        if (_headerFitQueued || !IsHandleCreated || IsDisposed || !Visible)
            return;
        _headerFitQueued = true;
        BeginInvoke(new MethodInvoker(() => {
            _headerFitQueued = false;
            if (IsDisposed || !Visible)
                return;
            _shellRoot?.PerformLayout();
            _pageHeader?.PerformLayout();
            _headerTextLayout?.PerformLayout();
            FitHeaderGuidance();
        }));
    }
    private void FitHeaderGuidance()
    {
        if (_shellRoot is null || _headerTextLayout is null ||
            _headerGuidanceLabel is null || _titleLabel is null ||
            _pageHeader is null || !_pageHeader.Visible ||
            _headerTextLayout.ClientSize.Width <= 0)
            return;
        int titleHeight = Math.Max(20, AppMetrics.TextHeight(AppTheme.PageTitleFont));
        if(StatusLabel is not null && StatusLabel.Parent==_headerTitleLine)
            titleHeight=Math.Max(titleHeight,FitHeaderStatus(_headerTitleLine!,2));
        int guidanceHeight = 0;
        if (!string.IsNullOrWhiteSpace(_headerGuidance))
        {
            int width = _headerTextLayout.ClientSize.Width;
            guidanceHeight = TextRenderer.MeasureText(
                _headerGuidance,
                _headerGuidanceLabel.Font,
                new Size(width, int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.Left |
                TextFormatFlags.Top | TextFormatFlags.NoPadding).Height;
            guidanceHeight = Math.Max(
                AppMetrics.TextHeight(AppTheme.NormalFont),
                guidanceHeight);
        }
        if (_headerTextLayout.RowStyles[0].Height != titleHeight)
            _headerTextLayout.RowStyles[0].Height = titleHeight;
        if (_headerTextLayout.RowStyles[1].Height != guidanceHeight)
            _headerTextLayout.RowStyles[1].Height = guidanceHeight;
        int headerHeight = titleHeight + guidanceHeight +
            Math.Max(AppMetrics.PageHeaderContentHeightAllowance,_pageHeader.Margin.Vertical+_pageHeader.Padding.Vertical);
        if (_shellRoot.RowStyles[0].Height != headerHeight)
            _shellRoot.RowStyles[0].Height = headerHeight;
    }
    private void EnsureActionBar()
    {
        if (_actionBar is not null)
            return;
        if (_shellRoot is null || _shellRoot.RowStyles.Count < 3)
            return;
        var bar = new RoundedPanel {
            Dock = DockStyle.Fill,
            Height = AppMetrics.PageActionBarHeight,
            BackColor = AppTheme.Surface,
            BorderColor = AppTheme.Border,
            ShowBorder = true,
            CornerRadius = AppTheme.CardRadius,
            Padding = new Padding(
                AppScale.Scale(10, AppScale.SpacingScale),
                AppScale.Scale(6, AppScale.SpacingScale),
                AppScale.Scale(10, AppScale.SpacingScale),
                AppScale.Scale(6, AppScale.SpacingScale)),
            Margin = new Padding(0, 0, 0, UiDesignSystem.Spacing.Md)
        };
        var layout = new TableLayoutPanel {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(
            SizeType.Absolute,
            AppScale.Scale(AppMetrics.PageActionStatusWidth)));
        Actions.Dock = DockStyle.Fill;
        Actions.Visible = true;
        layout.Controls.Add(Actions, 0, 0);
        layout.Controls.Add(StatusLabel, 1, 0);
        bar.Controls.Add(layout);
        _actionBar = bar;
        _shellRoot.Controls.Add(bar, 0, 1);
        _shellRoot.RowStyles[1].Height = AppMetrics.PageActionBarHeight;
        RefreshActionButtons();
        ConfigureActionBarStatus(false);
    }
    public void PrepareForEmbedding(
        bool keepActions = false)
    {
        if (_pageHeader is not null)
            _pageHeader.Hide();
        if (_shellRoot is not null &&
            _shellRoot.RowStyles.Count >= 3)
        {
            _shellRoot.RowStyles[0].Height = 0;
        }
        if (_embeddedActionBar is not null)
        {
            _embeddedActionBar.Parent?.Controls.Remove(_embeddedActionBar);
            _embeddedActionBar.Dispose();
            _embeddedActionBar = null;
        }
        if(keepActions) {
            EnsureActionBar();
            if(_actionBar is not null && _shellRoot is not null && _actionBar.Parent!=_shellRoot)
                _shellRoot.Controls.Add(_actionBar,0,1);
            _fitActionBar?.Invoke();
        } else {
            if(_actionBar is not null) {
                _shellRoot?.Controls.Remove(_actionBar);
                _actionBar.Visible=false;
                if(_shellRoot is not null)_shellRoot.RowStyles[1].Height=0;
            }
            Actions.Visible=false;
        }
        AutoScroll = false;
        Padding = new Padding(0);
        BackColor = AppTheme.Surface;
    }
    protected void UseToolbar()
    {
        EnsureActionBar();
    }
    public void SetStatusHeaderHost(TableLayoutPanel header)
    {
        _externalStatusHeader=header;
        header.SizeChanged+=(_,_)=>_fitActionBar?.Invoke();
        _fitActionBar?.Invoke();
    }
    private int FitHeaderStatus(TableLayoutPanel header,int column)
    {
        int width=header.ClientSize.Width;
        if(width<=0)return AppMetrics.TextHeight(StatusLabel.Font)+StatusLabel.Padding.Vertical;
        bool hasText=!string.IsNullOrWhiteSpace(StatusLabel.Text);
        int desired=TextRenderer.MeasureText(StatusLabel.Text,StatusLabel.Font).Width+StatusLabel.Padding.Horizontal+8;
        int statusWidth=hasText?Math.Min(desired,Math.Max(1,width/2)):0;
        if(column==1)statusWidth=Math.Max(1,width-header.GetColumnWidths()[0]);
        if(header.ColumnStyles[column].SizeType==SizeType.Absolute)header.ColumnStyles[column].Width=statusWidth;
        StatusLabel.Visible=hasText;
        int height=MeasureStatusHeight(Math.Max(1,statusWidth));
        if(header==_externalStatusHeader && header.Parent is TableLayoutPanel root)
            root.RowStyles[0].Height=Math.Max(AppMetrics.TextHeight(AppTheme.ButtonFont)+8,height);
        return height;
    }
    private int MeasureStatusHeight(int width)
    {
        // Status must not displace the workspace; full wording remains in the tooltip and accessibility text.
        int lineHeight=AppMetrics.TextHeight(StatusLabel.Font);
        int maximum=2*lineHeight+StatusLabel.Padding.Vertical;
        StatusLabel.MaximumSize=new Size(Math.Max(1,width),maximum);
        if(string.IsNullOrWhiteSpace(StatusLabel.Text))return 0;
        int measured=TextRenderer.MeasureText(StatusLabel.Text,StatusLabel.Font,
            new Size(Math.Max(1,width-StatusLabel.Padding.Horizontal),int.MaxValue),
            TextFormatFlags.WordBreak|TextFormatFlags.NoPrefix|TextFormatFlags.TextBoxControl).Height;
        return Math.Min(maximum,Math.Max(lineHeight,measured)+StatusLabel.Padding.Vertical);
    }
    /// <summary>Space reserved for action-bar status before buttons wrap.</summary>
    protected virtual int ActionStatusWidth => 240;
    protected virtual bool StatusBelowActions => false;
    /// <summary>For dense entry pages: allow actions to wrap within the available width.</summary>
    protected void UseWrappingToolbar()
    {
        EnsureActionBar();
        ConfigureActionBarStatus(true);
    }
    private void ConfigureActionBarStatus(bool wrapActions)
    {
        if (_actionBar?.Controls.OfType<TableLayoutPanel>().FirstOrDefault() is not { } layout)return;
        if(_fitActionBar is not null) {_fitActionBar();return;}
        layout.ColumnCount=2;layout.RowCount=1;
        layout.SetCellPosition(StatusLabel,new TableLayoutPanelCellPosition(1,0));
        layout.ColumnStyles.Clear();
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,0));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        layout.RowStyles.Clear();layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        if (StatusBelowActions)
        {
            layout.ColumnCount = 1; layout.RowCount = 2;
            layout.ColumnStyles.Clear(); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.SetCellPosition(StatusLabel, new TableLayoutPanelCellPosition(0, 1));
            layout.RowStyles.Clear();
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.ButtonHeight));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
            StatusLabel.TextAlign = ContentAlignment.MiddleLeft;
        }
        Actions.AutoSize=false;Actions.AutoScroll=false;
        bool fitting=false;
        void Fit()
        {
            // Effective Control.Visible is false for every button in a hidden tab.
            // Do not turn that inherited state into a collapsed action bar.
            if(fitting || !Visible || _actionBar.IsDisposed || _shellRoot is null || _actionBar.Parent!=_shellRoot)return;
            fitting=true;
            try {
                int width=Math.Max(1,_shellRoot.ClientSize.Width-_actionBar.Padding.Horizontal);
                _actionBar.Visible=true;Actions.Visible=true;
                var buttons=Actions.Controls.Cast<Control>().Where(c=>c.Visible).ToArray();
                bool hasActions=buttons.Length>0;
                if (StatusBelowActions)
                {
                    int required = buttons.Sum(c => c.Width + c.Margin.Horizontal) + Actions.Padding.Horizontal;
                    Actions.WrapContents = required > width;
                    int buttonsHeight = hasActions ? Math.Max(AppMetrics.ButtonHeight,
                        Actions.GetPreferredSize(new Size(width, 0)).Height) : 0;
                    bool statusVisible = !string.IsNullOrWhiteSpace(StatusLabel.Text);
                    int messageHeight = statusVisible ? AppMetrics.TextHeight(StatusLabel.Font) + 8 : 0;
                    StatusLabel.Visible = statusVisible;
                    layout.RowStyles[0].Height = buttonsHeight;
                    layout.RowStyles[1].Height = messageHeight;
                    Actions.Visible = hasActions;
                    _actionBar.Visible = hasActions || statusVisible;
                    _shellRoot.RowStyles[1].Height = buttonsHeight + messageHeight + _actionBar.Padding.Vertical + _actionBar.Margin.Vertical;
                    return;
                }
                bool managed=StatusLabel.Parent==layout || StatusLabel.Parent==_headerTitleLine || StatusLabel.Parent==_externalStatusHeader;
                var headerHost=_externalStatusHeader ?? (_pageHeader?.Visible==true?_headerTitleLine:null);
                if(managed && !hasActions && headerHost is not null) {
                    int column=headerHost==_externalStatusHeader?1:2;
                    if(StatusLabel.Parent!=headerHost)headerHost.Controls.Add(StatusLabel,column,0);
                    FitHeaderStatus(headerHost,column);FitHeaderGuidance();
                } else if(managed && hasActions && StatusLabel.Parent!=layout) {
                    layout.Controls.Add(StatusLabel,1,0);
                    if(_headerTitleLine is not null)_headerTitleLine.ColumnStyles[2].Width=0;
                    FitHeaderGuidance();
                }
                bool hasStatus=StatusLabel.Parent==layout && !string.IsNullOrWhiteSpace(StatusLabel.Text);
                int neededWidth=buttons.Sum(c=>c.Width+c.Margin.Horizontal)+Actions.Padding.Horizontal;
                int reserve=hasStatus?Math.Min(AppScale.Scale(ActionStatusWidth),width/3):0;
                int actionWidth=hasActions?Math.Min(neededWidth,Math.Max(1,width-reserve)):0;
                if(!hasStatus && hasActions)actionWidth=width;
                layout.ColumnStyles[0].Width=actionWidth;
                Actions.WrapContents=neededWidth>actionWidth;
                int actionHeight=hasActions?Math.Max(AppMetrics.ButtonHeight,
                    Actions.GetPreferredSize(new Size(Math.Max(1,actionWidth),0)).Height)+Actions.Margin.Vertical:0;
                int statusHeight=0;
                if(StatusLabel.Parent==layout) {
                    StatusLabel.Visible=hasStatus;
                    int statusWidth=Math.Max(1,width-actionWidth);
                    statusHeight=MeasureStatusHeight(statusWidth);
                }
                Actions.Visible=hasActions;
                bool visible=hasActions || hasStatus;_actionBar.Visible=visible;
                int height=visible?Math.Max(AppMetrics.PageActionBarHeight,
                    Math.Max(actionHeight,statusHeight)+_actionBar.Padding.Vertical+_actionBar.Margin.Vertical):0;
                if(_shellRoot.RowStyles[1].Height!=height)_shellRoot.RowStyles[1].Height=height;
            } finally {fitting=false;}
        }
        _fitActionBar=Fit;
        VisibleChanged+=(_,_)=>Fit();
        Actions.Layout+=(_,_)=>Fit();layout.SizeChanged+=(_,_)=>Fit();
        StatusLabel.FontChanged+=(_,_)=>Fit();StatusLabel.TextChanged+=(_,_)=>Fit();
        StatusLabel.ParentChanged+=(_,_)=>{StatusLabel.MaximumSize=Size.Empty;StatusLabel.Visible=true;Fit();};
        Fit();
    }
    public void ApplyScale()
    {
        SuspendLayout();
        bool headerVisible =
            _pageHeader is not null &&
            _pageHeader.Visible;
        if (headerVisible &&
            _shellRoot is not null &&
            _shellRoot.RowStyles.Count > 0 &&
            _titleLabel is not null)
        {
            _titleLabel.Font =
                UiDpi.ForControl(_titleLabel, AppTheme.PageTitleFont);
        }
        if (ContextLabel is not null)
            ContextLabel.Font =
                UiDpi.ForControl(ContextLabel, AppTheme.NormalFont);
        if (_headerGuidanceLabel is not null)
            _headerGuidanceLabel.Font = UiDpi.ForControl(_headerGuidanceLabel, AppTheme.NormalFont);
        ScheduleHeaderFit();
        if (StatusLabel is not null)
            StatusLabel.Font =
                UiDpi.ForControl(StatusLabel, AppTheme.NormalFont);
        RefreshActionButtons();
        if (_embeddedActionBar is not null)
        {
            int scaledBarHeight = AppMetrics.PageActionBarHeight;
            _embeddedActionBar.Height =
                scaledBarHeight;
            if (_shellRoot is not null &&
                _shellRoot.RowStyles.Count > 1)
            {
                _shellRoot.RowStyles[1].Height =
                    scaledBarHeight;
            }
        }
        ResumeLayout(true);
    }
    private void RefreshActionButtons()
    {
        foreach (ModernButton button in Actions.Controls
                     .OfType<ModernButton>()
                     .Concat(_relocatedActionButtons))
        {
            string role =
                button.Tag as string ??
                UiDesignSystem.Actions.Primary;
            button.Font =
                UiDpi.ForControl(button, AppTheme.ButtonFont);
            button.Height =
                AppMetrics.ButtonHeight;
            AppMetrics.FitButtonWidth(
                button,
                minWidth: AppMetrics.ActionButtonMinimumWidth, horizontalPadding: AppScale.Scale(32));
            ApplyActionButtonTheme(
                button,
                role);
        }
    }
    private void ApplyActionButtonTheme(
        ModernButton button,
        string role)
    {
        switch (role)
        {
            case UiDesignSystem.Actions.Secondary:
                button.ThemeRole = "Secondary";
                break;
            case UiDesignSystem.Actions.Danger:
                button.ThemeRole = "Danger";
                break;
            case UiDesignSystem.Actions.Success:
                button.ThemeRole = "Success";
                break;
            case UiDesignSystem.Actions.Warning:
                button.ThemeRole = "Warning";
                break;
            default:
                button.ThemeRole = "Primary";
                break;
        }
        button.Invalidate();
        button.Update();
    }
    protected static Control BuildSplitPanels(
        Control left,
        Control right,
        int minLeftWidth = 320,
        float leftRatio = 0.44f)
    {
        var split = new SplitContainer {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            FixedPanel = FixedPanel.None,
            IsSplitterFixed = false,
            SplitterWidth = 8,
            BorderStyle = BorderStyle.None,
            BackColor = AppTheme.PageBackground,
            Padding = new Padding(0)
        };
        left.Margin =
            new Padding(0, 0, 4, 0);
        right.Margin =
            new Padding(4, 0, 0, 0);
        split.Panel1.Padding =
            new Padding(0, 0, 4, 0);
        split.Panel2.Padding =
            new Padding(4, 0, 0, 0);
        split.Panel1.Controls.Add(left);
        split.Panel2.Controls.Add(right);
        void FitSplit()
        {
            int available = split.ClientSize.Width - split.SplitterWidth - split.Panel2MinSize;
            if (available < split.Panel1MinSize) return;
            int desired = Math.Clamp(Math.Max(AppScale.Scale(minLeftWidth), (int)(split.ClientSize.Width * leftRatio)),
                split.Panel1MinSize, available);
            if (split.SplitterDistance != desired) split.SplitterDistance = desired;
        }
        split.HandleCreated += (_, _) => FitSplit();
        split.SizeChanged += (_, _) => FitSplit();
        return split;
    }
    protected void SetContent(Control control)
    {
        SetHeaderGuidance(null);
        var oldControls = _content.Controls.Cast<Control>().ToArray();
        _content.Controls.Clear();
        foreach (var oldControl in oldControls)
            oldControl.Dispose();
        control.Dock = DockStyle.Fill;
        _content.Controls.Add(control);
        FormFlow.OrderTableFields(control);
    }
    internal virtual bool FocusEntryField() => FormFlow.FocusFirstInput(_content);
    protected void SetContent(Control control, string? guidance)
    {
        SetContent(control);
        SetHeaderGuidance(guidance);
    }
    protected void SetActionIcon(string buttonText, string icon)
    {
        ModernButton? button = Actions.Controls
            .OfType<ModernButton>()
            .Concat(_relocatedActionButtons)
            .FirstOrDefault(b => string.Equals(b.Text?.Trim(), buttonText, StringComparison.OrdinalIgnoreCase));
        if (button is null || string.IsNullOrEmpty(icon))
            return;
        string baseText = button.Text ?? string.Empty;
        if (!baseText.StartsWith(icon, StringComparison.Ordinal))
            button.Text = $"{icon} {baseText}".Trim();
    }
    protected void RelocateFromActions(params Control[] buttons)
    {
        foreach (Control button in buttons)
        {
            Actions.Controls.Remove(button);
            if (button is ModernButton modernButton &&
                !_relocatedActionButtons.Contains(modernButton))
            {
                _relocatedActionButtons.Add(modernButton);
            }
        }
        UpdateActionBarVisibility();
    }
    private void UpdateActionBarVisibility()
    {
        if (_actionBar is null || _shellRoot is null || _embeddedActionBar is not null)
            return;
        _fitActionBar?.Invoke();
    }
    /// <summary>Do not offer an action the database will refuse.</summary>
    protected void GateAction(Control? action, string permission)
    {
        if (action is null || string.IsNullOrWhiteSpace(permission))
            return;
        if (CurrentUser.HasPermission(permission))
            return;
        action.Visible = false;
        action.Enabled = false;
        UpdateActionBarVisibility();
    }
    /// <summary>as <see cref="GateAction(Control?, string)"/>, but the action is offered when the user holds ANY of the listed permissions.</summary>
    protected void GateActionAny(Control? action, params string[] anyOfPermission)
    {
        if (action is null || anyOfPermission is null || anyOfPermission.Length == 0)
            return;
        if (anyOfPermission.Any(permission => CurrentUser.HasPermission(permission)))
            return;
        action.Visible = false;
        action.Enabled = false;
        UpdateActionBarVisibility();
    }
    /// <summary>An action that performs a write, guarded against being run twice.</summary>
    protected ModernButton AddBusyAction(
        string text,
        Func<Task> operation,
        string? role = null,
        string? busyMessage = null)
    {
        ModernButton? button = null;
        string? progress = busyMessage ?? ActionBusyMessage.For(text);
        button = AddAction(
            text,
            async (_, _) => {
                if (button is null || !button.Enabled)
                    return;
                /* Preserve focus before disabling the active action button. */
                Form? owningForm = FindForm();
                bool buttonHadFocus = button.Focused;
                button.Enabled = false;
                Control? focusAfterDisable = owningForm?.ActiveControl;
                /* Preserve disabled states set by page validation or permissions during the operation. */
                bool pageChangedEnabled = false;
                void NoteEnabledChange(object? sender, EventArgs e) =>
                    pageChangedEnabled = true;
                button.EnabledChanged += NoteEnabledChange;
                /* Show progress inline while the action runs. */
                BusyScope? progressScope = progress is null ? null : BeginBusy(progress);
                try
                {
                    await operation();
                }
                finally
                {
                    progressScope?.Dispose();
                    button.EnabledChanged -= NoteEnabledChange;
                    if (!pageChangedEnabled &&
                        !button.IsDisposed)
                    {
                        button.Enabled = true;
                    }
                    if (buttonHadFocus &&
                        !button.IsDisposed &&
                        button.Enabled &&
                        owningForm is { IsDisposed: false } &&
                        !FormFlow.HasInputFocus(this) &&
                        ReferenceEquals(owningForm.ActiveControl, focusAfterDisable))
                    {
                        if (!FocusEntryField()) button.Focus();
                    }
                }
            },
            role);
        return button;
    }
    private bool _busyOperationRunning;
    protected bool BusyOperationRunning=>_busyOperationRunning;
    protected string BusyOperationMessage { get; private set; }="";
    /// <summary>Show status and hold only controls that could interfere or double-submit.</summary>
    protected BusyScope BeginBusy(string message, params Control?[] controlsToDisable) =>
        new BusyScope(
            message,
            controlsToDisable,
            () => StatusLabel.Text ?? string.Empty,
            () => _statusKind,
            (text, kind) => {
                if (!StatusLabel.IsDisposed)
                    SetStatus(text, kind);
            },
            FindForm());
    /// <summary>Show loading progress without disabling controls.</summary>
    protected BusyScope BeginLoading(string message) => BeginBusy(message);
    /// <summary>Run inside a busy scope and reject concurrent submissions.</summary>
    protected async Task<bool> RunBusyAsync(
        string message,
        Func<Task> operation,
        params Control?[] controlsToDisable)
    {
        if (_busyOperationRunning)
            return false;
        _busyOperationRunning = true;
        BusyOperationMessage=message;
        try
        {
            using (BeginBusy(message, controlsToDisable))
                await operation();
        }
        finally
        {
            _busyOperationRunning = false;
            BusyOperationMessage="";
        }
        return true;
    }
    /// <summary>Create inline validation with this page's summary.</summary>
    private FormValidator? _requiredFieldsValidator;
    protected bool ValidateRequiredInputs(Control? root = null)
    {
        _requiredFieldsValidator ??= CreateValidator();
        _requiredFieldsValidator.Begin();
        _requiredFieldsValidator.RequiredFields(root ?? this);
        return _requiredFieldsValidator.Finish();
    }
    protected FormValidator CreateValidator()
    {
        var validator = new FormValidator();
        string? shown = null;
        validator.SummaryChanged = summary => {
            if (StatusLabel is null || StatusLabel.IsDisposed)
                return;
            if (summary is not null)
            {
                shown = summary;
                SetStatus(summary, true);
            }
            else if (shown is not null)
            {
                if (string.Equals(StatusLabel.Text, shown, StringComparison.Ordinal))
                    SetStatus(string.Empty);
                shown = null;
            }
        };
        _validators.Add(validator);
        return validator;
    }
    private readonly List<FormValidator> _validators = new();
    /* Resolve shortcuts through visible action buttons, preserving permissions and enabled states. */
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (AppHotKeys.TrySwitchTab(this, keyData))
            return true;
        if (_shortcutController.TryHandle(keyData))
            return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }
    protected void SetSaveShortcut(ModernButton button) => _shortcutController.SetSaveButton(button);
    internal PageActionShortcutController ShortcutController => _shortcutController;
    internal bool IsGridReport => GetType().Namespace?.StartsWith("FMSC_SMS.Pages.Reports.", StringComparison.Ordinal) == true;
    internal Control ReportShortcutTarget => Grid;
    internal bool CanPreviewReport => !_reportStale && (_reportSource ?? Grid.DataSource as DataTable)?.Rows.Count > 0;
    internal void BeginReportPreview() => _ = PreviewReportAsync();
    private bool _previewingReport;
    protected async Task PreviewReportAsync()
    {
        if (_previewingReport) return;
        if (_reportStale) { Warn("Filters changed. Run the report again before printing."); return; }
        DataTable? source = _reportSource ?? Grid.DataSource as DataTable;
        if (source is null || source.Rows.Count == 0) { Warn("Run the report before printing."); return; }
        _previewingReport = true;
        try
        {
            using var table = ReportExportBuilder.Build(source, Grid.Columns.Cast<DataGridViewColumn>(),
                (ReportColumnCatalog ?? Array.Empty<ReportColumnDef>()).Where(column => column.Kind == ReportColumnKind.Date).Select(column => column.Key));
            if (table.Columns.Count == 0) { Warn("Choose at least one report column before printing."); return; }
            string signature = ReportFilterSignature();
            string title = ReportExportTitle(), subtitle = ReportExportSubtitle();
            string? campusContext=ReportCampusContext;
            var metrics=ReportPrintMetrics();
            int actor=CurrentUser.UserId;
            string printedBy=CurrentUser.Username;
            string institution = await InstitutionProfile.GetNameAsync();
            if (IsDisposed) return;
            if (CurrentUser.UserId!=actor || _reportStale || signature != ReportFilterSignature()) { Warn("Filters changed. Run the report again before printing."); return; }
            if (string.IsNullOrWhiteSpace(institution)) { Warn("Complete School Profile before printing."); return; }
            GridReportPreview.Show(FindForm(), institution, title, subtitle, table, printedBy,summaryMetrics:metrics,campusContext:campusContext);
        }
        catch (Exception ex) { if (!IsDisposed) Fail(ex); }
        finally { _previewingReport = false; }
    }
    private static string InferActionRole(string text) =>
        ActionButtonStyle.Role(text, UiDesignSystem.Actions.Primary);
    protected ModernButton AddAction(
        string text,
        EventHandler click,
        string? role = null)
    {
        string effectiveRole = role ?? InferActionRole(text);
        var button = new ModernButton {
            Text = text,
            Width = AppMetrics.ActionButtonMinimumWidth,
            Height = AppMetrics.ButtonHeight,
            Margin = new Padding(
                AppMetrics.SpacingXs,
                0,
                AppMetrics.SpacingXs,
                0),
            TabStop = true,
            TabIndex = Actions.Controls.Count,
            Tag = effectiveRole
        };
        AppMetrics.FitButtonWidth(
            button,
            minWidth: AppMetrics.ActionButtonMinimumWidth);
        ApplyActionButtonTheme(
            button,
            effectiveRole);
        button.Click += (sender, e) => {
            if (text.StartsWith("Refresh", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("Reload", StringComparison.OrdinalIgnoreCase))
            {
                LookupDataCache.Invalidate();
                StudentSearchCache.Invalidate();
            }
            click(sender, e);
        };
        EnsureActionBar();
        Actions.Controls.Add(button);
        UpdateActionBarVisibility();
        RefreshShortcutHints();
        return button;
    }
    /* Put the shortcut beside the action name after the normal hover delay. */
    private void RefreshShortcutHints()
    {
        try { _shortcutController.RefreshHints(); }
        catch (ObjectDisposedException)
        {
        }
    }
    protected ModernButton AddExportAction(
        string filePrefix = "FMSC_Report")
    {
        var button = AddBusyAction(
            UiText.Actions.Export,
            () => ExportGridAsync(filePrefix),
            "Secondary");
        /* registered so a landing under changed filters can close it. */
        _exportButtons.Add(button);
        return button;
    }
    /* Pages may opt into operator-selected report columns and order. */
    private readonly Dictionary<string, List<string>?> _reportLayoutCache = new(StringComparer.Ordinal);
    private bool _reportLayoutHooked;
    /// <summary>Which saved layout applies. A page with several shapes of report returns one key per shape.</summary>
    protected virtual string ReportLayoutKey => NavigationTitle;
    /// <summary>Every column the operator may show. Null (the default) means the page has no chooser.</summary>
    protected virtual IReadOnlyList<ReportColumnDef>? ReportColumnCatalog => null;
    /// <summary>The columns shown until the operator chooses otherwise. Null means the catalogue's DefaultVisible flags.</summary>
    protected virtual IReadOnlyList<string>? ReportDefaultColumnKeys => null;
    protected virtual IReadOnlyList<string> ReportRequiredColumnKeys => Array.Empty<string>();
    /// <summary>Opt-in report pages refresh their dynamic catalogue after typed rows bind.</summary>
    protected void RefreshReportColumnLayout() => ApplyReportColumnLayout();
    /// <summary>The title printed on the PDF. Defaults to the page's navigation title.</summary>
    protected virtual string ReportExportTitle() => NavigationTitle;
    /// <summary>The line printed under the PDF title. Defaults to the page's header context.</summary>
    protected virtual string ReportExportSubtitle() => _contextText;
    protected virtual string? ReportCampusContext => null;
    private protected virtual IReadOnlyList<ReportPrintMetric>? ReportPrintMetrics() => null;
    /// <summary>True when the chosen columns include data the report has not loaded yet.</summary>
    protected virtual bool ReportLayoutNeedsReload(IReadOnlyList<string> keys) => false;
    /// <summary>Called after the operator picks columns that need data the report has not loaded.</summary>
    protected virtual void ReloadReportForLayout()
    {
    }
    private IReadOnlyList<string> DefaultReportColumns(IReadOnlyList<ReportColumnDef> catalog)
    {
        var known = new HashSet<string>(catalog.Select(c => c.Key), StringComparer.OrdinalIgnoreCase);
        var fromPage = ReportDefaultColumnKeys?
            .Where(known.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (fromPage is { Count: > 0 })
            return fromPage;
        return catalog.Where(c => c.DefaultVisible).Select(c => c.Key).ToList();
    }
    /// <summary>The columns to show now: the operator's saved choice, else the page's defaults - only ones the catalogue still has.</summary>
    protected IReadOnlyList<string> ResolveReportColumns(IReadOnlyList<ReportColumnDef> catalog)
    {
        string layoutKey = ReportLayoutKey;
        if (!_reportLayoutCache.TryGetValue(layoutKey, out var saved))
        {
            saved = ReportLayoutStore.GetColumns(layoutKey)?.ToList();
            _reportLayoutCache[layoutKey] = saved;
        }
        if (saved is not null)
        {
            var known = new HashSet<string>(catalog.Select(c => c.Key), StringComparer.OrdinalIgnoreCase);
            var usable = saved
                .Where(known.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (usable.Count > 0)
                return ReportRequiredColumnKeys.Where(known.Contains).Except(usable,StringComparer.OrdinalIgnoreCase).Concat(usable).ToArray();
        }
        return DefaultReportColumns(catalog);
    }
    /// <summary>The columns the report is showing (or would show) right now, in order. Empty when the page has no chooser.</summary>
    protected IReadOnlyList<string> CurrentReportColumns()
    {
        var catalog = ReportColumnCatalog;
        return catalog is null
            ? Array.Empty<string>()
            : ResolveReportColumns(catalog);
    }
    /// <summary>The "Columns" button. The page relocates it into its own button row like View Report and Export.</summary>
    protected ModernButton AddColumnsAction()
    {
        return AddAction(
            "Columns",
            (_, _) => ShowReportColumnChooser(),
            "Secondary");
    }
    protected void ShowReportColumnChooser()
    {
        var catalog = ReportColumnCatalog;
        if (catalog is null || catalog.Count == 0)
            return;
        using var dialog = new ReportColumnDialog(
            "Choose the columns for this report",
            catalog,
            ResolveReportColumns(catalog),
            DefaultReportColumns(catalog),ReportRequiredColumnKeys);
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
            return;
        SetReportColumns(dialog.ResultKeys);
    }
    /// <summary>Sets (and remembers) the columns for the current layout.</summary>
    protected void SetReportColumns(IReadOnlyList<string>? keys)
    {
        var catalog = ReportColumnCatalog;
        if (catalog is null)
            return;
        string layoutKey = ReportLayoutKey;
        var defaults = DefaultReportColumns(catalog);
        RememberReportColumns(
            layoutKey,
            keys is null || keys.SequenceEqual(defaults, StringComparer.OrdinalIgnoreCase)
                ? null
                : keys);
        if (ReportLayoutNeedsReload(ResolveReportColumns(catalog)))
        {
            ReloadReportForLayout();
            return;
        }
        ApplyReportColumnLayout();
    }
    /// <summary>Store column choices before switching to a saved view, without changing the grid.</summary>
    protected void RememberReportColumns(string layoutKey, IReadOnlyList<string>? keys)
    {
        if (keys is null || keys.Count == 0)
        {
            _reportLayoutCache[layoutKey] = null;
            var reset = ReportLayoutStore.ClearColumns(layoutKey);
            if (!reset.Succeeded) SetStatus("The default columns apply here, but the saved layout could not be reset. " +
                DbMessage.UserMessage(reset.Error!), PageStatusKind.Warning);
            return;
        }
        _reportLayoutCache[layoutKey] = keys.ToList();
        var saved = ReportLayoutStore.SetColumns(layoutKey, keys);
        if (!saved.Succeeded) SetStatus("These columns apply here, but the layout was not saved for next time. " +
            DbMessage.UserMessage(saved.Error!), PageStatusKind.Warning);
    }
    private void HookReportColumnLayout()
    {
        if (_reportLayoutHooked || Grid.IsDisposed)
            return;
        _reportLayoutHooked = true;
        /* The shared grid presentation re-hides raw columns and re-humanises headers on every rebind, and sorting by a header click rebinds. */
        Grid.DataBindingComplete += (_, _) => {
            /* Deferred until every other DataBindingComplete handler has run, whatever order they were attached in, so this is genuinely the last word. */
            if (Grid.IsHandleCreated && !Grid.IsDisposed)
                Grid.BeginInvoke(new Action(ApplyReportColumnLayout));
            else
                ApplyReportColumnLayout();
        };
        Grid.ColumnHeaderMouseClick += (_, e) => {
            if (e.Button == MouseButtons.Right && ReportColumnCatalog is not null)
                ShowReportColumnChooser();
        };
    }
    private void ApplyReportColumnLayout()
    {
        var catalog = ReportColumnCatalog;
        if (catalog is null || Grid.IsDisposed || Grid.Columns.Count == 0)
            return;
        var byKey = catalog.ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);
        var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int order = 0;
        foreach (string key in ResolveReportColumns(catalog))
        {
            if (!Grid.Columns.Contains(key) || !byKey.TryGetValue(key, out var def))
                continue;
            DataGridViewColumn column = Grid.Columns[key]!;
            column.Visible = true;
            column.HeaderText = def.Header;
            column.DisplayIndex = order++;
            shown.Add(column.Name);
            switch (def.Kind)
            {
                case ReportColumnKind.Date:
                    column.DefaultCellStyle.Format = "dd-MMM-yyyy";
                    column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
                    column.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
                    break;
                case ReportColumnKind.Money:
                    column.DefaultCellStyle.Format = "N0";
                    column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
                    column.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
                    break;
                case ReportColumnKind.Number:
                    column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
                    column.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
                    break;
            }
        }
        foreach (DataGridViewColumn column in Grid.Columns)
        {
            if (!shown.Contains(column.Name))
                column.Visible = false;
        }
    }
    protected void TrackStudentSearchStatus(StudentPicker student)
    {
        student.SearchFailed += ex => Fail(ex);
        student.MatchesChanged += (_, _) => {
            if (student.Text.Trim().Length > 0)
                SetStatus(student.MatchStatusText);
        };
    }
    protected void SetStatus(string message)
    {
        SetStatus(message, PageStatusKind.Info);
    }
    protected void SetStatus(string message, bool warning)
    {
        SetStatus(
            message,
            warning ? PageStatusKind.Warning : PageStatusKind.Info);
    }
    protected void SetStatus(
        string message,
        PageStatusKind kind)
    {
        StatusLabel.Text = message;
        StatusLabel.AccessibleDescription = message;
        _statusKind = kind;
        _statusToolTip.SetToolTip(StatusLabel, message);
        ApplyStatusVisual();
    }
    /// <summary>lets a page that serves both "new" and "edit" say which one it is in.</summary>
    protected void SetPageHeaderText(
        string title,
        string? context = null)
    {
        if (!string.IsNullOrWhiteSpace(title) &&
            _titleLabel is not null &&
            !_titleLabel.IsDisposed)
        {
            _titleLabel.Text = title;
        }
        if (context is not null &&
            ContextLabel is not null &&
            !ContextLabel.IsDisposed)
        {
            _contextText = context;
            ContextLabel.Text = string.IsNullOrWhiteSpace(context) ? string.Empty : $" ({context})";
            _statusToolTip.SetToolTip(ContextLabel, context);
        }
    }
    internal void RefreshStatusTheme()
    {
        if (StatusLabel is null || StatusLabel.IsDisposed)
            return;
        ApplyStatusVisual();
    }
    private void ApplyStatusVisual()
    {
        Color? tone = _statusKind switch {
            PageStatusKind.Success => AppTheme.Success,
            PageStatusKind.Warning => AppTheme.Danger,
            PageStatusKind.Error => AppTheme.Danger,
            PageStatusKind.Busy => AppTheme.Primary,
            _ => null
        };
        if (tone is Color color)
        {
            StatusLabel.ForeColor = AppTheme.StatusText(color);
            StatusLabel.BackColor = AppTheme.Tint(
                color,
                _statusKind == PageStatusKind.Busy ? 0.94f : 0.92f);
            return;
        }
        StatusLabel.ForeColor = AppTheme.MutedText;
        StatusLabel.BackColor = AppTheme.SurfaceSecondary;
    }
    /// <summary>Turns header-click sorting off for the current grid columns.</summary>
    protected void LockGridOrder()
    {
        foreach (DataGridViewColumn column in Grid.Columns)
            column.SortMode = DataGridViewColumnSortMode.NotSortable;
    }
    /// <summary>Sets a grid column width when that optional result column exists.</summary>
    protected void SetGridWidth(string columnName, int width)
    {
        if (!Grid.Columns.Contains(columnName))
            return;
        Grid.Columns[columnName].Width = DpiLayout.Pixels(width, Grid.DeviceDpi);
        Grid.Columns[columnName].FillWeight = Math.Max(width, 1);
    }
    protected void Bind(DataTable table)
    {
        // WinForms otherwise infers an image column for rowversion byte arrays.
        // Prepare the hidden metadata column before binding can measure or paint it.
        if(Grid.AutoGenerateColumns&&table.Columns.Contains("ContentVersion")&&table.Columns["ContentVersion"]!.DataType==typeof(byte[]))
        {
            if(Grid.Columns["ContentVersion"] is DataGridViewImageColumn imageColumn)Grid.Columns.Remove(imageColumn);
            if(!Grid.Columns.Contains("ContentVersion"))Grid.Columns.Add(new DataGridViewTextBoxColumn {
                Name="ContentVersion",DataPropertyName="ContentVersion",Visible=false,ReadOnly=true,ValueType=typeof(byte[])
            });
            Grid.Columns["ContentVersion"]!.Visible=false;
        }
        Grid.DataSource = table;
        StyleGrid();
        ClearGridSelection(Grid);
        SetStatus(
            $"{table.Rows.Count:N0} record(s)");
    }
    protected static void ClearGridSelection(DataGridView grid)
    {
        if (grid.IsDisposed)
            return;
        grid.ClearSelection();
        try
        {
            grid.CurrentCell = null;
        }
        catch (InvalidOperationException fallbackException10)
        {
            AppExceptionHandler.LogNonFatal(fallbackException10, "V2Page: InvalidOperationException recovery");
        }
    }
    Dictionary<string, string> IWorkspaceStateHost.CaptureWorkspaceState() =>
        WorkspaceState.Capture(this);
    void IWorkspaceStateHost.RestoreWorkspaceState(IReadOnlyDictionary<string, string> state)
    {
        OnWorkspaceStateRestoring();
        WorkspaceState.Restore(this, state);
    }
    protected virtual void OnWorkspaceStateRestoring() { }
    protected Task BindReportAsync(Func<DataTable> query) =>
        BindReportCoreAsync(token => Task.Run(query, token));

    protected Task BindReportAsync(Func<Task<DataTable>> query) =>
        BindReportCoreAsync(_ => query());

    protected Task BindReportAsync(Func<CancellationToken, Task<DataTable>> query) =>
        BindReportCoreAsync(query);

    private async Task BindReportCoreAsync(Func<CancellationToken, Task<DataTable>> query)
    {
        if (IsDisposed) return;
        ReportLoadCoordinator.Ticket ticket = _reportLoads.Begin();
        string requestedSignature = ReportFilterSignature();
        _reportLanded = false;
        UseWaitCursor = true;
        try
        {
            SetStatus("Generating report...");
            UiHelper.SetGridState(Grid, UiHelper.GridDisplayState.Loading, "Generating report…");
            /* see the synchronous overload above. */
            SetReportPagerEnabled(false);
            _reportStale = true;
            await _reportLoads.EnterAsync(ticket);
            try
            {
                if (!_reportLoads.IsCurrent(ticket) || IsDisposed)
                    return;
                var table = await query(ticket.Token);
                if (!_reportLoads.IsCurrent(ticket) || IsDisposed)
                    return;
                SuspendLayout();
                try
                {
                    _reportSource = table;
                    _reportPage = 0;
                    _reportStale = false;
                    _reportLanded = true;
                    BindReportPage();
                    ApplyLandedFilterState(requestedSignature);
                }
                finally { ResumeLayout(true); }
            }
            finally
            {
                _reportLoads.Exit();
            }
        }
        // A cancelled provider read can also surface a SqlException. Neither
        // form should display a failure for an intentionally superseded request.
        catch (Exception) when (ticket.Token.IsCancellationRequested) { }
        catch (Exception) when (MarkReportGridFailed(ticket.Cancellation))
        {
            /* The filter records failure and returns false; the page handler still receives the exception. */
            throw;
        }
        finally
        {
            if (_reportLoads.IsCurrent(ticket))
                UseWaitCursor = false;
            _reportLoads.Complete(ticket);
        }
    }
    /* Distinguish a failed load from an empty result. */
    private bool MarkReportGridFailed(CancellationTokenSource cts)
    {
        if (!cts.IsCancellationRequested && !IsDisposed)
        {
            // A failed refresh must not leave the previous rows looking like a
            // successful result, or allow them to be restored through the pager.
            _reportSource = null;
            _reportPage = 0;
            _reportLanded = false;
            _reportStale = true;
            Grid.DataSource = null;
            SetReportPagerEnabled(false);
            if (_reportPageLabel is not null) _reportPageLabel.Text = "Report unavailable";
            UiHelper.SetGridState(Grid, UiHelper.GridDisplayState.Failed, "Could not generate the report. Try again.");
        }
        return false;
    }
    private protected virtual ReportPaginator.Page CreateReportPage(DataTable source, int index, int pageSize) =>
        ReportPaginator.Build(source, index, pageSize);
    protected virtual DataTable SelectExportRows(DataTable source, bool dataOnly) => source;
    private protected virtual void WritePdfExport(string path, GridReportPreview.Report report) =>
        GridReportPreview.ExportPdf(path,report);
    internal GridReportPreview.Report BuildPdfExportDocument(DataTable projection,string institution,string printedBy) =>
        GridReportPreview.Build(institution,ReportExportTitle(),ReportExportSubtitle(),projection,printedBy,
            summaryMetrics:ReportPrintMetrics(),campusContext:ReportCampusContext);
    protected virtual int ExportRecordCount(DataTable source) => ReportPrintSummary.DetailCount(source);
    protected virtual string CsvExportLabel => "CSV file (*.csv)";
    private void BindReportPage()
    {
        if (_reportSource is null)
            return;
        ReportPaginator.Page page = CreateReportPage(_reportSource, _reportPage, ReportPageSize);
        _reportPage = page.Index;
        HookReportColumnLayout();
        Grid.DataSource = page.Rows;
        StyleGrid();
        ClearGridSelection(Grid);
        UiHelper.SetGridEmptyMessage(Grid, "No records match the current filters.");
        ApplyReportColumnLayout();
        EnsureReportPager();
        _reportPageLabel!.Text = page.RangeText;
        _reportPreviousButton!.Enabled = page.HasPrevious;
        _reportNextButton!.Enabled = page.HasNext;
        SetStatus(page.StatusText);
        OnReportPageBound();
    }
    /// <summary>Runs after every bind of a report page - the first page AND each Next / Previous step.</summary>
    protected virtual void OnReportPageBound()
    {
    }
    /// <summary>Override with the current filter signature to track report freshness.</summary>
    protected virtual string ReportFilterSignature() => string.Empty;
    /// <summary>True when the rows that just landed answer the filters now on screen.</summary>
    protected bool ReportMatchesCurrentFilters(string requestedSignature) =>
        string.Equals(
            requestedSignature,
            ReportFilterSignature(),
            StringComparison.Ordinal);
    /// <summary>True once the last BindReportAsync call actually bound a table.</summary>
    protected bool ReportLanded => _reportLanded;
    /// <summary>A report that lands under filters the operator has since changed is not current, whatever the page does afterwards.</summary>
    private void ApplyLandedFilterState(string requestedSignature)
    {
        string currentSignature = ReportFilterSignature();
        if (requestedSignature.Length == 0 &&
            currentSignature.Length == 0)
        {
            return;
        }
        if (string.Equals(
                requestedSignature,
                currentSignature,
                StringComparison.Ordinal))
        {
            return;
        }
        MarkReportStale();
        SetReportExportEnabled(false);
        SetStatus(UiText.Reports.FiltersChanged, true);
    }
    private void SetReportExportEnabled(bool enabled)
    {
        foreach (ModernButton button in _exportButtons)
        {
            if (!button.IsDisposed)
                button.Enabled = enabled;
        }
    }
    /// <summary>A report page calls this the moment its filters, scope or report choice stop matching the results on screen.</summary>
    protected void InvalidateReport()
    {
        _reportStale = true;
        _reportLoads.Cancel();
        _reportSource = null;
        _reportPage = 0;
        /* The superseded run cannot land any more, so do not leave the operator behind a wait cursor until it happens to finish. */
        UseWaitCursor = false;
        Grid.DataSource = null;
        StyleGrid();
        SetReportPagerEnabled(false);
        if (_reportPageLabel is not null)
            _reportPageLabel.Text = "No records";
        SetStatus("Filters changed - run the report again to refresh the results.");
    }
    /// <summary>Stops a superseded read while retaining the previous report for
    /// pages that deliberately show old rows behind a filters-changed warning.</summary>
    protected void CancelPendingReport()
    {
        if (!_reportLoads.Cancel()) return;
        UseWaitCursor = false;
        UiHelper.ClearGridState(Grid);
        SetReportPagerEnabled(false);
        SetReportExportEnabled(false);
        SetStatus(UiText.Reports.FiltersChanged, true);
    }
    /// <summary>Mark the retained report stale when filters change; keep its warning visible.</summary>
    protected void MarkReportStale()
    {
        if (_reportSource is null)
            return;
        _reportStale = true;
        SetReportPagerEnabled(false);
    }
    /// <summary>Restore freshness and paging when filters again match the displayed report.</summary>
    protected void RestoreReportStale()
    {
        if (!_reportStale)
            return;
        _reportStale = false;
        if (_reportSource is not null)
            BindReportPage();
    }
    private void StepReportPage(int delta)
    {
        /* Only ever move within a report that is still current. */
        if (_reportStale || _reportSource is null)
            return;
        _reportPage += delta;
        BindReportPage();
    }
    private void SetReportPagerEnabled(bool enabled)
    {
        if (_reportPreviousButton is not null && !_reportPreviousButton.IsDisposed)
            _reportPreviousButton.Enabled = enabled;
        if (_reportNextButton is not null && !_reportNextButton.IsDisposed)
            _reportNextButton.Enabled = enabled;
    }
    private void EnsureReportPager()
    {
        if (_reportPager is not null || Grid.Parent is null)
            return;
        ReportPagerView.Controls pager = ReportPagerView.Create(
            () => StepReportPage(-1), () => StepReportPage(1));
        _reportPager = pager.Panel;
        _reportPageLabel = pager.Label;
        _reportPreviousButton = pager.Previous;
        _reportNextButton = pager.Next;
        Grid.Parent.Controls.Add(_reportPager);
        // Dock the bottom pager before the fill grid so it reserves its own space.
        Grid.BringToFront();
    }
    private async Task ExportGridAsync(
        string filePrefix = "FMSC_Report")
    {
        DataTable? source = _reportSource ?? Grid.DataSource as DataTable;
        /* Disable export while displayed rows belong to different filters. */
        if (_reportStale)
        {
            Warn("These results are no longer current - the filters changed, or the last run did not complete - so exporting them could put one selection's figures in a file labelled with another's. Run the report again, then export.");
            return;
        }
        if (source is null || source.Columns.Count == 0)
        {
            Warn("There is no report data to export yet.");
            return;
        }
        using var dialog = new SaveFileDialog {
            Filter = $"PDF file (*.pdf)|*.pdf|{CsvExportLabel}|*.csv|Excel workbook (*.xlsx)|*.xlsx",
            FilterIndex = 1,
            FileName = ReportDocumentNaming.ReportExportFileName(ReportExportTitle(), ReportExportSubtitle()),
            DefaultExt = "pdf",
            AddExtension = true,
            OverwritePrompt = true
        };
        int exportActor=CurrentUser.UserId;
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
            return;
        if (CurrentUser.UserId!=exportActor || _reportStale || !ReferenceEquals(source,_reportSource ?? Grid.DataSource as DataTable))
        { Warn("Results or user changed. Run the report again before exporting."); return; }
        /* declared outside the try so both the finally and the catch restore the same captured states. */
        var suspendedActions = new List<(Control Control, bool WasEnabled)>();
        try
        {
            string filePath = dialog.FileName;
            bool exportAsCsv = string.Equals(
                Path.GetExtension(filePath),
                ".csv",
                StringComparison.OrdinalIgnoreCase);
            bool exportAsExcel=string.Equals(Path.GetExtension(filePath),".xlsx",StringComparison.OrdinalIgnoreCase);
            bool exportAsPdf=string.Equals(Path.GetExtension(filePath),".pdf",StringComparison.OrdinalIgnoreCase);
            if(!exportAsCsv && !exportAsExcel && !exportAsPdf)
            { Warn("Choose a PDF, CSV or Excel (.xlsx) file type."); return; }
            int rowCount = ExportRecordCount(source);
            if (source.Columns.Count == 0)
            {
                Warn("There is no report data to export yet.");
                return;
            }
            /* The file is the report as it is on screen: the columns the operator has showing, in the order they have them, under the headers they read. */
            using DataTable exportTable = ReportExportBuilder.Build(
                SelectExportRows(source, exportAsCsv || exportAsExcel),
                Grid.Columns.Cast<DataGridViewColumn>(),
                (ReportColumnCatalog ?? Array.Empty<ReportColumnDef>())
                    .Where(column => column.Kind == ReportColumnKind.Date)
                    .Select(column => column.Key),preserveDateTypes:exportAsExcel);
            if (exportTable.Columns.Count == 0)
            {
                Warn("No columns are showing, so there is nothing to export. Choose at least one column first.");
                return;
            }
            GridReportPreview.Report? pdfReport=null;
            if (exportAsPdf)
            {
                int actor=CurrentUser.UserId;
                string printedBy=CurrentUser.Username, signature=ReportFilterSignature();
                string institution=await InstitutionProfile.GetNameAsync();
                if (IsDisposed) return;
                if (CurrentUser.UserId!=actor || _reportStale || signature!=ReportFilterSignature())
                { Warn("Filters or user changed. Run the report again before exporting."); return; }
                if (string.IsNullOrWhiteSpace(institution)) { Warn("Complete School Profile before exporting a PDF."); return; }
                pdfReport=BuildPdfExportDocument(exportTable,institution,printedBy);
            }
            using var pdfData=pdfReport?.Data;
            SetStatus($"Exporting {rowCount:N0} record(s)...");
            UseWaitCursor = true;
            suspendedActions = SuspendActionButtons();
            /* The export is only complete once the file it claims to have written is really on disk and is not empty. */
            bool fileVerified = false;
            try
            {
                await Task.Run(() => {
                    if (exportAsCsv)
                        ReportExportBuilder.WriteCsv(exportTable, filePath);
                    else if(exportAsExcel)
                        XlsxTableExporter.Write(exportTable,filePath);
                    else
                        WritePdfExport(filePath, pdfReport!);
                });
                try
                {
                    var writtenFile = new FileInfo(filePath);
                    fileVerified = writtenFile.Exists && writtenFile.Length > 0;
                }
                catch (IOException fallbackException11)
                {
                    AppExceptionHandler.LogNonFatal(fallbackException11, "V2Page: IOException recovery");
                    fileVerified = false;
                }
                catch (UnauthorizedAccessException fallbackException12)
                {
                    AppExceptionHandler.LogNonFatal(fallbackException12, "V2Page: UnauthorizedAccessException recovery");
                    fileVerified = false;
                }
            }
            finally
            {
                UseWaitCursor = false;
                RestoreActionButtons(suspendedActions);
            }
            if (!fileVerified)
            {
                Warn($"Export failed: no non-empty file was found at {filePath}. Nothing was exported - check the export location and available disk space, then try again.");
                return;
            }
            SetStatus($"Exported {rowCount:N0} record(s).");
        }
        catch (Exception ex)
        {
            UseWaitCursor = false;
            RestoreActionButtons(suspendedActions);
            Fail(ex);
        }
    }
    /// <summary>every action button this page owns, relocated ones included.</summary>
    private IEnumerable<Control> ActionButtons() =>
        Actions.Controls
            .Cast<Control>()
            .Concat(_relocatedActionButtons);
    /// <summary>Hold action buttons and preserve page-specific enabled states on restoration.</summary>
    private List<(Control Control, bool WasEnabled)> SuspendActionButtons()
    {
        var captured = new List<(Control Control, bool WasEnabled)>();
        foreach (Control control in ActionButtons())
        {
            if (control.IsDisposed)
                continue;
            captured.Add((control, control.Enabled));
            control.Enabled = false;
        }
        return captured;
    }
    private static void RestoreActionButtons(
        IEnumerable<(Control Control, bool WasEnabled)> captured)
    {
        foreach (var (control, wasEnabled) in captured)
        {
            if (!control.IsDisposed)
                control.Enabled = wasEnabled;
        }
    }
    private void StyleGrid()
    {
        ReportGridStyler.Apply(Grid, Accent);
    }
    protected void Defer(
        Action action)
    {
        DeferredControlRunner.Run(this, action, Fail);
    }
    protected void Defer(Func<Task> action)
    {
        DeferredControlRunner.Run(this, action, Fail);
    }
    /// <summary>Reports a completed action in the status line - deliberately NOT in a dialog.</summary>
    protected void Info(
        string message)
    {
        SetStatus(message, PageStatusKind.Success);
    }
    /// <summary>Reports a refusal in the status line: the operator's action did not run, and why.</summary>
    protected void Warn(
        string message)
    {
        SetStatus(
            message,
            PageStatusKind.Warning);
        DrawAttentionToStatus(message);
    }
    /* Warn now also pops the full sentence beneath the status line for a few seconds and pulses the line twice. */
    private void DrawAttentionToStatus(string message)
    {
        StatusAttention.Show(this, StatusLabel, _statusToolTip, message, ApplyStatusVisual);
    }
    protected void SetToolTip(Control control, string text)
    {
        ControlToolTips.SetDescription(_statusToolTip, control, text);
    }
    /// <summary>Report an attempted operation that failed; retain the outcome after dismissing the dialog.</summary>
    protected void Alert(
        string message)
    {
        SetStatus(
            message,
            PageStatusKind.Warning);
        ModernMessageDialog.ShowWarning(FindForm(), message);
    }
    protected DataGridViewButtonColumn CreateCrossActionColumn(
        string name = "__Delete",
        int width = 34,
        bool useColumnTextForButtonValue = true)
    {
        var column = new DataGridViewButtonColumn {
            Name = name,
            HeaderText = "",
            Text = "✕",
            UseColumnTextForButtonValue = useColumnTextForButtonValue,
            Width = width,
            MinimumWidth = width,
            FillWeight = width,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            FlatStyle = FlatStyle.Flat,
            Resizable = DataGridViewTriState.False
        };
        column.DefaultCellStyle.Alignment =
            DataGridViewContentAlignment.MiddleCenter;
        column.DefaultCellStyle.Padding = new Padding(0);
        column.DefaultCellStyle.Font =
            AppTheme.SymbolFont(11F, FontStyle.Bold);
        column.DefaultCellStyle.ForeColor = AppTheme.Danger;
        column.DefaultCellStyle.BackColor = AppTheme.Surface;
        column.DefaultCellStyle.SelectionForeColor = AppTheme.SelectionText;
        column.DefaultCellStyle.SelectionBackColor = AppTheme.SelectionBackground;
        return column;
    }
    /// <summary>Null uses the wording-based default; destructive/discard confirmations default to No.</summary>
    protected bool Confirm(
        string message,
        string title = "Confirm",
        bool? defaultYes = null)
    {
        return ModernMessageDialog.Confirm(FindForm(), message, title, defaultYes);
    }
    protected void Fail(
        Exception ex)
    {
        AppExceptionHandler.LogNonFatal(
            ex,
            $"Page: {GetType().FullName}");
        string message =
            DbMessage.UserMessage(ex);
        try
        {
            string logPath =
                Path.Combine(
                    AppContext.BaseDirectory,
                    "FMSC_LastError.log");
            File.WriteAllText(
                logPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}{ex}");
        }
        catch (Exception nonFatalException25)
        {
            FMSC_SMS.Core.AppExceptionHandler.LogNonFatal(nonFatalException25, "V2Page: Fail fallback");
        }
        /* The operator left this page while the work was still in flight. */
        if (IsDisposed)
            return;
        if (DatabaseConnectionForm.IsConnectionProblem(ex))
        {
            SetStatus("The connection was interrupted. Reconnect, then refresh this page before continuing.", PageStatusKind.Error);
            using var connection = new DatabaseConnectionForm(startup: false);
            connection.ShowDialog(FindForm());
            return;
        }
        bool actionable = DbMessage.IsActionable(ex);
        SetStatus(
            message,
            actionable ? PageStatusKind.Warning : PageStatusKind.Error);
        /* Present actionable SQL refusals as warnings. */
        if (actionable)
        {
            ModernMessageDialog.ShowWarning(FindForm(), message);
            return;
        }
        ModernMessageDialog.ShowError(FindForm(), message);
    }
    protected void RequirePermission(
        string permission)
    {
        if (!CurrentUser.HasPermission(
                permission))
        {
            throw new InvalidOperationException(
                "You do not have permission to perform this operation.");
        }
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        BeginInvoke(new Action(() => { if (!IsDisposed) UiTheme.ApplyTree(this); }));
    }
    private void OnThemeChanged(
        object? sender,
        EventArgs e)
    {
        SuspendLayout();
        BackColor =
            AppTheme.PageBackground;
        ForeColor =
            AppTheme.Text;
        Accent =
            AppTheme.ModuleAccent(
                NavigationTitle);
        if (_pageHeader is not null)
        {
            _pageHeader.BackColor = AppTheme.PageBackground;
            _pageHeader.BorderColor = AppTheme.PageBackground;
        }
        if (_titleLabel is not null)
        {
            _titleLabel.Font =
                AppTheme.PageTitleFont;
            _titleLabel.ForeColor = AppTheme.HeadingText;
        }
        if (ContextLabel is not null)
        {
            ContextLabel.Font =
                AppTheme.NormalFont;
            ContextLabel.ForeColor = AppTheme.MutedText;
        }
        if (_headerGuidanceLabel is not null)
            _headerGuidanceLabel.ForeColor = AppTheme.MutedText;
        if (StatusLabel is not null)
        {
            StatusLabel.Font =
                AppTheme.NormalFont;
            /* The status line paints its own background (a tint chosen by message kind), so its text colour must be chosen together with it. */
            ApplyStatusVisual();
        }
        if (_embeddedActionBar is not null)
        {
            _embeddedActionBar.BackColor =
                AppTheme.Surface;
            _embeddedActionBar.BorderColor =
                AppTheme.Border;
        }
        Grid.BackgroundColor =
            AppTheme.Surface;
        StyleGrid();
        ScheduleHeaderFit();
        RefreshActionButtons();
        if (_pageHeader is not null) UiTheme.ApplyTree(_pageHeader);
        Invalidate(true);
        ResumeLayout(true);
    }
}



