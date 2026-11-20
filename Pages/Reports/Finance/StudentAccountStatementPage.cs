using FMSC_SMS.Core;
namespace FMSC_SMS.Pages.Reports.Finance;
// BD-20: current dues/credit and filtered period totals are distinct; no running ledger or net debt is invented.
// Receipts access must not grant statement SQL/export; retain each reader and correction writer's permissions.
public sealed partial class StudentAccountStatementPage : V2Page, IBusyPage
{
    protected override string ReportExportFileScope()
    {
        var selected = GetSelectedAdmissionIds();
        string identity = selected.Count switch
        {
            0 => "",
            1 => studentNames.GetValueOrDefault(selected[0], studentSearch.Text),
            _ => selected.Count + " students"
        };
        // StudentPicker may show an extended description; retain a compact identity.
        if (identity.Length > 60) identity = identity[..60].TrimEnd();
        var parts = new List<string>();
        if (identity.Length > 0) parts.Add(identity);
        if (from.Checked || to.Checked)
            parts.Add(from.Checked && to.Checked
                ? from.Value.ToString("yyyy-MM-dd") + " to " + to.Value.ToString("yyyy-MM-dd")
                : from.Checked ? "from " + from.Value.ToString("yyyy-MM-dd") : "to " + to.Value.ToString("yyyy-MM-dd"));
        return string.Join(" - ", parts);
    }
    private readonly StudentPicker studentSearch = new();
    private readonly ModernDatePicker from =
        new ModernDatePicker {
            Format = DateTimePickerFormat.Custom,
            CustomFormat = "dd-MM-yyyy",
            Value = DateTime.Today.AddMonths(-1),
            ShowCheckBox = true,
            Checked = false,
            Dock = DockStyle.Fill
        };
    private readonly ModernDatePicker to =
        new ModernDatePicker {
            Format = DateTimePickerFormat.Custom,
            CustomFormat = "dd-MM-yyyy",
            Value = DateTime.Today,
            ShowCheckBox = true,
            Checked = false,
            Dock = DockStyle.Fill
        };
    private readonly Label openingBalance =
        SummaryValue(AppTheme.Success);
    private readonly Label chargesTotal =
        SummaryValue(AppTheme.Text);
    private readonly Label paymentsTotal =
        SummaryValue(AppTheme.Success);
    private readonly Label closingBalance =
        SummaryValue(AppTheme.Danger);
    private Action? arrangeStatement;
    private readonly Label statementContext = new() {
        Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft,
        Font = AppTheme.ButtonFont, ForeColor = AppTheme.Text,
        Text = "Select a student to view their account."
    };
    private ModernButton? annualReceiptsButton;
    private int studentSearchVersion;
    private readonly Dictionary<int,string> studentNames=new();
    private FormValidator? statementDateValidator;
    private readonly ModernButton exportButton;
    private ModernButton? generateStatementButton;
    private ModernButton? printAmountsDueButton;
    /* The filter values that produced the statement currently in the grid. Empty until a statement has actually been loaded for this page. */
    private string _appliedFilterSignature = string.Empty;
    private readonly HashSet<int> manuallySelectedAdmissionIds =
        new();
    private readonly Dictionary<int, HashSet<int>> familySelections =
        new();
    private readonly LatestReadScope statementReads=new();
    private readonly Func<int,DateTime?,DateTime?,Task<(DataTable Transactions,decimal OpeningBalance)>> readStatement;
    public bool IsBusy=>BusyOperationRunning||printingDueReport||accountActionBusy;
    public string BusyMessage=>"A financial statement or report is being prepared. Wait for the result before leaving.";
    public StudentAccountStatementPage() : this(null) { }
    internal StudentAccountStatementPage(Func<int,DateTime?,DateTime?,Task<(DataTable Transactions,decimal OpeningBalance)>>? readStatement)
        : base(PageTitles.StudentFeeAccount,
            "See what a student was charged, what was paid and what remains due.")
    {
        Disposed+=(_,_)=>{
            accountReads.Dispose();
            foreach(var control in new Control[]{accountReceipts,accountPayments,accountTransactions,accountCredits,accountSummary})
                if(!control.IsDisposed)control.Dispose();
        };
        this.readStatement=readStatement??LoadStatementDataAsync;
        FinanceHelp.Attach(this,openingBalance,"The amount already due before the selected statement period. It is carried into this statement, not newly charged.","Rs. 500 was unpaid before October. The October statement starts with Rs. 500 opening dues.");
        FinanceHelp.Attach(this,closingBalance,"The amount still due after the statement's charges, payments and adjustments. This report does not change the student's account.","Opening dues Rs. 500 plus fees Rs. 400 minus payment Rs. 300 leaves Rs. 600 due.");
        Disposed+=(_,_)=>statementReads.Dispose();
        var export =
            AddExportAction("Student_Fee_Statement");
        exportButton = export;
        RelocateFromActions(export);
        WorkspaceState.Remember(studentSearch, "FeeStatement:StudentSearch");
        WorkspaceState.Remember(from, "FeeStatement:From");
        WorkspaceState.Remember(to, "FeeStatement:To");
        studentSearch.PlaceholderText =
            StudentPicker.SearchPlaceholder;
        studentSearch.SearchFailed += ex => Fail(ex);
        studentSearch.UserSelectionChanged += async (_, _) => {
            SelectSingleStudent();
            await LoadStatement();
        };
        studentSearch.SelectionCleared += (_, _) => ClearSingleStudent();
        studentSearch.KeyDown += async (_, e) => {
            if(e.KeyCode!=Keys.Enter||e.Handled)return;
            e.SuppressKeyPress=true;e.Handled=true;
            if(studentSearch.SelectedId>0){SelectSingleStudent();await LoadStatement();}
        };
        studentSearch.TextChanged +=
            (_, _) => {
                /* The search text is one of the criteria an in-flight search was run for, so changing it invalidates that run. */
                studentSearchVersion++;
                OnFiltersChanged();
            };
        from.ValueChanged +=
            (_, _) => OnFiltersChanged();
        to.ValueChanged +=
            (_, _) => OnFiltersChanged();
        // Optional dates can change from unchecked to checked without changing Value.
        // Both transitions invalidate the statement/export snapshot.
        from.CheckedChanged += (_, _) => OnFiltersChanged();
        to.CheckedChanged += (_, _) => OnFiltersChanged();
        Grid.DataBindingComplete += (_, _) => { ConfigureStatementGrid(); FormatStatementAmounts(); };
        BuildStatementLayout();
        ClearForm();
        InitializeAccount();
    }
    private void BuildStatementLayout()
    {
        var filters = HumanUi.Card("Student Account", "Leave dates unchecked for the complete account.");
        int headerHeight=filters.Header.Height;
        var filterGrid = new TableLayoutPanel {
            Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = Padding.Empty
        };
        foreach (float width in new[] { 25f, 25f, 25f, 25f })
            filterGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, width));
        int fieldHeight = Math.Max(AppMetrics.FieldRowHeight,
            AppMetrics.TextHeight(AppTheme.FieldCaptionFont) + AppMetrics.InputControlHeight
            + AppMetrics.SpacingSm + Math.Max(studentSearch.Margin.Vertical, from.Margin.Vertical));
        filterGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        filterGrid.Controls.Add(ReportUi.FormField("Student", studentSearch), 0, 0);
        filterGrid.SetColumnSpan(studentSearch.Parent!, 2);
        filterGrid.Controls.Add(ReportUi.FormField("From (optional)", from), 2, 0);
        filterGrid.Controls.Add(ReportUi.FormField("To (optional)", to), 3, 0);
        generateStatementButton = CreateFilterButton("View Account", AppTheme.Primary);
        generateStatementButton.Click += async (_, _) => await LoadStatement();
        var clear = CreateFilterButton("Reset Filters", AppTheme.MutedText);
        clear.MinimumSize=new Size(AppScale.Scale(90),AppMetrics.ButtonHeight);
        clear.Click += (_, _) => { ClearForm(); };
        SetToolTip(clear, "Clear this statement's selection, dates and displayed results.");
        var actions = new FlowLayoutPanel {
            Dock = DockStyle.Fill, WrapContents = true, Margin = Padding.Empty, Padding = Padding.Empty
        };
        foreach (var button in new[] { generateStatementButton, clear })
        {
            AppMetrics.FitButtonWidth(button, minWidth: button==clear?AppScale.Scale(90):110);
            actions.Controls.Add(button);
        }
        var modeBar = new FlowLayoutPanel {
            Dock = DockStyle.Fill, WrapContents = true, Margin = Padding.Empty, Padding = Padding.Empty
        };
        printAmountsDueButton=CreateFilterButton("Outstanding Bills…",AppTheme.MutedText);
        printAmountsDueButton.Click+=async(_,_)=>await PrintDueReport();
        SetToolTip(printAmountsDueButton,"Open current unpaid bills for this student. Statement dates do not limit these bills.");
        annualReceiptsButton=CreateFilterButton("Annual Receipts…",AppTheme.MutedText);
        annualReceiptsButton.Click+=async(_,_)=>await OpenAnnualReceipts();
        SetToolTip(annualReceiptsButton,"Choose an academic year and print receipts for the selected student.");
        foreach(var button in new[]{printAmountsDueButton,annualReceiptsButton})
        {
            AppMetrics.FitButtonWidth(button,minWidth:110);
            button.Margin=new Padding(4,0,4,0);modeBar.Controls.Add(button);
        }
        filters.Header.Controls.Add(modeBar);
        filters.Header.Controls.SetChildIndex(modeBar,filters.Header.Controls.Count-1);
        var filterBody = new TableLayoutPanel {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty
        };
        filterBody.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        filterBody.RowStyles.Add(new RowStyle(SizeType.Absolute, fieldHeight));
        var actionsRow = new RowStyle(SizeType.Absolute, AppMetrics.ButtonRowHeight);
        filterBody.RowStyles.Add(actionsRow);
        filterBody.Controls.Add(filterGrid, 0, 0);
        filterBody.Controls.Add(actions,0,1);
        filters.Content.Controls.Add(filterBody);

        var statement = HumanUi.Card("Account Activity", "Review fees, payments, receipts and advance credit.", accessory: exportButton);
        statement.Header.Controls.SetChildIndex(exportButton, statement.Header.Controls.Count - 1);
        statement.Content.Controls.Add(BuildAccountViews());
        var summary = BuildSummary();
        var root = new TableLayoutPanel {
            ColumnCount = 1, RowCount = 5, Margin = Padding.Empty, Padding = Padding.Empty
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var filterRow = new RowStyle(SizeType.Absolute);
        root.RowStyles.Add(filterRow);
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, SummaryCardRowHeight()));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.TextHeight(AppTheme.NormalFont) * 2 + 8));
        root.Controls.Add(filters, 0, 0);
        root.Controls.Add(statementContext, 0, 1);
        statementContext.Visible=false;
        root.Controls.Add(summary, 0, 2);
        root.Controls.Add(statement, 0, 3);
        root.Controls.Add(StatusLabel, 0, 4);
        var viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
        viewport.Controls.Add(root);
        int actionWidthNeeded=actions.Controls.Cast<Control>().Sum(control=>control.Width+control.Margin.Horizontal);
        filterGrid.ColumnCount=5;
        filterGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,0));
        bool arranging = false;
        arrangeStatement = () => {
            if (arranging) return;
            arranging = true;
            try
            {
                root.SuspendLayout();
                int width = Math.Max(AppScale.Scale(720), viewport.ClientSize.Width - SystemInformation.VerticalScrollBarWidth);
                int actionWidth = width - filters.Padding.Horizontal - filters.Content.Padding.Horizontal;
                bool inlineActions=actionWidth>=AppScale.Scale(880);
                int relatedWidth=modeBar.Controls.Cast<Control>().Sum(control=>control.Width+control.Margin.Horizontal);
                bool stackReports=actionWidth<relatedWidth+AppScale.Scale(250);
                filters.Header.Height=headerHeight+(stackReports?AppMetrics.ButtonRowHeight:0);
                modeBar.Dock=stackReports?DockStyle.Bottom:DockStyle.Right;
                modeBar.Width=relatedWidth;modeBar.Height=stackReports?AppMetrics.ButtonRowHeight:headerHeight;
                filterGrid.ColumnStyles[4].Width=inlineActions?actionWidthNeeded:0;
                if(inlineActions)
                {
                    if(actions.Parent!=filterGrid)filterGrid.Controls.Add(actions,4,0);
                    actions.Padding=new Padding(0,Math.Max(16,AppMetrics.TextHeight(AppTheme.FieldCaptionFont))-3,0,0);
                }
                else
                {
                    if(actions.Parent!=filterBody)filterBody.Controls.Add(actions,0,1);
                    actions.Padding=Padding.Empty;
                }
                int NeededHeight(FlowLayoutPanel bar)
                {
                    int rows=1,usedWidth=0;
                    foreach(Control control in bar.Controls)
                    {
                        int required=control.Width+control.Margin.Horizontal;
                        if(usedWidth>0&&usedWidth+required>actionWidth){rows++;usedWidth=0;}
                        usedWidth+=required;
                    }
                    return AppMetrics.ButtonRowHeight*rows;
                }
                actionsRow.Height=inlineActions?0:NeededHeight(actions);
                filterRow.Height = filters.Padding.Vertical + filters.Header.Height + filters.Content.Padding.Vertical
                    + fieldHeight + actionsRow.Height;
                int fixedHeight = root.RowStyles.Cast<RowStyle>().Where(r => r.SizeType == SizeType.Absolute).Sum(r => (int)r.Height);
                int statementHeight = statement.Padding.Vertical + statement.Header.Height + statement.Content.Padding.Vertical
                    + Grid.ColumnHeadersHeight + AppMetrics.GridRowHeight * 5 + SystemInformation.HorizontalScrollBarHeight;
                statementHeight+=AppScale.Scale(90);
                root.Size = new Size(width,
                    Math.Max(viewport.ClientSize.Height, fixedHeight + statementHeight));
                root.ResumeLayout(true);
            }
            finally { arranging = false; }
        };
        viewport.SizeChanged += (_, _) => arrangeStatement();
        SetContent(viewport);
        arrangeStatement();
    }
    private void SelectSingleStudent()
    {
        statementReads.Cancel();
        Grid.DataSource = null;
        openingBalance.Text = chargesTotal.Text = paymentsTotal.Text = closingBalance.Text = "—";
        _appliedFilterSignature = string.Empty;
        SetReportActionsEnabled(false);
        manuallySelectedAdmissionIds.Clear();
        familySelections.Clear();
        studentNames.Clear();
        int id = studentSearch.SelectedId;
        if (id > 0)
        {
            manuallySelectedAdmissionIds.Add(id);
            studentNames[id]=studentSearch.Text;
        }
        statementContext.Text = id > 0 ? studentSearch.Text : "Select a student to view their account.";
        OnFiltersChanged();
    }
    private void ClearSingleStudent()
    {
        SelectSingleStudent();
        SetStatus("Select a student to view their charges, payments and balance.");
    }
    internal Func<Task<DataTable>> ReadAnnualYears=()=>DatabaseHelper.QueryUserAsync("GetAcademicYears");
    internal Func<Form?,int,int,Func<bool>,Action<string>,Task> PreviewAnnualReceipts=AnnualReceiptPreview.ShowAsync;
    // Intentional: Annual Receipts always uses the main student selected in studentSearch,
    // even when Include Siblings is active. Family selection changes account/bill scope,
    // not the student whose annual receipts are previewed or printed.
    private async Task OpenAnnualReceipts()
    {
        if(IsBusy||IsDisposed||!CurrentUser.HasPermission("Fees.Statement.View")||studentSearch.SelectedId<=0)return;
        int admission=studentSearch.SelectedId,user=CurrentUser.UserId;
        bool Current()=>!IsDisposed&&studentSearch.SelectedId==admission&&CurrentUser.UserId==user&&CurrentUser.HasPermission("Fees.Statement.View");
        accountActionBusy=true;UpdateSelectionActions();
        try
        {
            using var years=await ReadAnnualYears();
            if(!Current())return;
            using var dialog=new AnnualReceiptYearDialog(studentSearch.Text,years);
            if(dialog.ShowDialog(FindForm())!=DialogResult.OK||!Current())return;
            using var busy=BeginBusy("Preparing annual receipts…",this);
            await PreviewAnnualReceipts(FindForm(),admission,dialog.AcademicYearId,Current,Warn);
        }
        catch(Exception ex){if(!IsDisposed)Fail(ex);}
        finally{accountActionBusy=false;if(!IsDisposed)UpdateSelectionActions();}
    }
    private static ApplicationWindowForm CreateRelatedReportDialog(string title,Control page)
    {
        var dialog=new ApplicationWindowForm { Text=title,StartPosition=FormStartPosition.CenterParent,
            ClientSize=new Size(AppScale.Scale(1180),AppScale.Scale(760)),
            MinimumSize=new Size(AppScale.Scale(820),AppScale.Scale(560)),ShowInTaskbar=false,MinimizeBox=false };
        page.Dock=DockStyle.Fill;dialog.Controls.Add(page);ModalPageCloseGuard.Attach(dialog,page);
        return dialog;
    }
    private async Task PreloadAdmissionIdAsync(int admissionId)
    {
        try
        {
            int version=++studentSearchVersion;
            var raw=await DatabaseHelper.QueryUserAsync("GetStudents",
                DatabaseHelper.Int("@CampusId",0),DatabaseHelper.Int("@AcademicYearId",0),
                DatabaseHelper.P("@Search",AdmissionNumbers.ForId(admissionId)),
                DatabaseHelper.Bit("@IncludeFormerStudents",true));
            if(IsDisposed||version!=studentSearchVersion)return;
            var selected=raw.AsEnumerable().FirstOrDefault(row=>Convert.ToInt32(row["AdmissionId"])==admissionId);
            if(selected is null){ClearSingleStudent();SetStatus("This student could not be found. Choose a student before viewing their account.",PageStatusKind.Warning);return;}
            studentSearch.SetStudent(admissionId,HumanUi.StudentDisplay(admissionId,
                Convert.ToString(selected["StudentName"]),Convert.ToString(selected["FatherName"])));
            SelectSingleStudent();
            await LoadStatement();
        }
        catch (Exception ex) { if (!IsDisposed) Fail(ex); }
    }
    private static ModernButton CreateFilterButton(string text, Color color)
    {
        var button =
            new ModernButton {
                Text = text,
                Width = AppScale.Scale(145),
                MinimumSize = new Size(145, AppMetrics.ButtonHeight),
                Height = AppMetrics.ButtonHeight,
                Margin = new Padding(4, 3, 4, 3),
                Font = AppTheme.ButtonFont
            };
        if (color == AppTheme.Success)
            button.ThemeRole = "Success";
        else if (color == AppTheme.Warning)
            button.ThemeRole = "Warning";
        else if (color == AppTheme.Danger)
            button.ThemeRole = "Danger";
        else if (color == AppTheme.MutedText)
            button.ThemeRole = "Secondary";
        else
            button.ThemeRole = "Primary";
        return button;
    }
    /* Include student identity, dates and sibling expansion in the statement signature. */
    private string FilterSignature()
    {
        var admissionIds =
            GetSelectedAdmissionIds();
        admissionIds.Sort();
        return
            $"{studentSearch.Text.Trim()}|{string.Join(",", admissionIds)}|" +
            $"{from.Checked}:{from.Value.Date:yyyy-MM-dd}|{to.Checked}:{to.Value.Date:yyyy-MM-dd}";
    }
    private void SetReportActionsEnabled(bool enabled)
    {
        exportButton.Enabled = enabled;
    }
    /* report the displayed statement as stale the moment the selection or dates no longer match the values that produced it. */
    private void OnFiltersChanged()
    {
        InvalidateAccount();
        if(BusyOperationRunning)statementReads.Cancel();
        UpdateSelectionActions();
        if (string.IsNullOrEmpty(_appliedFilterSignature))
        {
            // An account summary is not proof of a newly generated statement.
            SetReportActionsEnabled(false);
            return;
        }
        bool current = string.Equals(_appliedFilterSignature, FilterSignature(), StringComparison.Ordinal);
        SetReportActionsEnabled(current);
        if (current)
            SetStatus("Statement matches the current filters and student selection.");
        else
            SetStatus("Filters or student changed. Use View Account again before exporting.", true);
    }
    private void SelectFamilyForStudent(int admissionId)
    {
        try
        {
            var family =
                DatabaseHelper.QueryUser(
                    "GetStudentFamily",
                    DatabaseHelper.Int(
                        "@UserId",
                        CurrentUser.UserId),
                    DatabaseHelper.Int("@AdmissionId", admissionId));
            if (family.Rows.Count == 0)
            {
                UncheckFamily(admissionId);
                Warn("No family members were found for this student.");
                return;
            }
            var familyIds =
                new HashSet<int>();
            foreach (DataRow row
                     in family.Rows)
            {
                if (!family.Columns.Contains("AdmissionId"))
                {
                    continue;
                }
                if (row["AdmissionId"] ==
                    DBNull.Value)
                {
                    continue;
                }
                int memberId =
                    Convert.ToInt32(row["AdmissionId"]);
                if (memberId > 0)
                {
                    familyIds.Add(memberId);
                }
            }
            if (familyIds.Count == 0)
            {
                UncheckFamily(admissionId);
                Warn("No family members were found.");
                return;
            }
            familySelections[
                admissionId] =
                familyIds;
            manuallySelectedAdmissionIds.Remove(admissionId);
            foreach(DataRow member in family.Rows)
                if(member["AdmissionId"]!=DBNull.Value)
                    studentNames[Convert.ToInt32(member["AdmissionId"])]=Convert.ToString(member["StudentName"])??"";
            SetStatus($"{familyIds.Count} family member(s) selected." + SelectionCountText());
        }
        catch (Exception ex)
        {
            familySelections.Remove(admissionId);
            UncheckFamily(admissionId);
            Fail(ex);
        }
    }
    private void UncheckFamily(int admissionId) => familySelections.Remove(admissionId);
    private string SelectionCountText() => $" {GetSelectedAdmissionIds().Count:N0} student(s) included.";
    private void UpdateSelectionActions()
    {
        // Keep the family toggle caption in sync with its actual scope, including
        // when opening a sibling directly or clearing/changing the main student.
        if (accountFamily is not null)
            accountFamily.Text = familySelections.Count > 0 ? "Student Only" : "Include Siblings";
        bool hasStudents = GetSelectedAdmissionIds().Count > 0;
        if (generateStatementButton is not null) generateStatementButton.Enabled = hasStudents&&!BusyOperationRunning;
        if (printAmountsDueButton is not null) printAmountsDueButton.Enabled = hasStudents && !IsBusy && CurrentUser.HasPermission("Fees.View");
        if (annualReceiptsButton is not null) annualReceiptsButton.Enabled = studentSearch.SelectedId > 0
            && !IsBusy && CurrentUser.HasPermission("Fees.Statement.View");
    }
    private List<int> GetSelectedAdmissionIds()
    {
        var result =
            new HashSet<int>();
        foreach (int id
                 in manuallySelectedAdmissionIds)
        {
            if (id > 0)
                result.Add(id);
        }
        foreach (HashSet<int> family
                 in familySelections.Values)
        {
            foreach (int id
                     in family)
            {
                if (id > 0)
                    result.Add(id);
            }
        }
        return result.ToList();
    }
    private static Label SummaryValue(Color color)
    {
        return new Label {
            Dock = DockStyle.Fill,
            Font = AppTheme.HeadingFont,
            ForeColor = color,
            TextAlign =
                ContentAlignment.MiddleCenter
        };
    }
    private Control BuildSummary()
    {
        var table =
            new TableLayoutPanel {
                Dock = DockStyle.Fill,
                ColumnCount = 4,
                RowCount = 1,
                Padding =
                    new Padding(0, 6, 0, 6),
                Margin =
                    new Padding(0)
            };
        for (int i = 0; i < 4; i++)
        {
            table.ColumnStyles.Add(
                new ColumnStyle(SizeType.Percent, 25f));
        }
        table.Controls.Add(
            SummaryCard("Opening Balance", openingBalance), 0, 0);
        table.Controls.Add(
            SummaryCard("Fees Charged in Period", chargesTotal), 1, 0);
        table.Controls.Add(
            SummaryCard("Payments in Period", paymentsTotal), 2, 0);
        table.Controls.Add(
            SummaryCard("Balance Due at Period End", closingBalance), 3, 0);
        return table;
    }
    private static Control SummaryCard(string title, Label value)
    {
        var card =
            new RoundedPanel {
                Dock = DockStyle.Fill,
                BackColor =
                    AppTheme.Surface,
                BorderColor =
                    AppTheme.Border,
                ShowBorder = true,
                CornerRadius =
                    AppTheme.CardRadius,
                Padding =
                    new Padding(8),
                Margin =
                    new Padding(0, 0, 8, 0)
            };
        var root =
            new TableLayoutPanel {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Padding =
                    new Padding(0),
                Margin =
                    new Padding(0)
            };
        root.RowStyles.Add(
            new RowStyle(
                SizeType.Absolute,
                Math.Max(
                    22,
                    AppMetrics.TextHeight(AppTheme.ButtonFont) + 4)));
        root.RowStyles.Add(
            new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(
            new Label {
                Text = title,
                Dock = DockStyle.Fill,
                Font =
                    AppTheme.ButtonFont,
                ForeColor =
                    AppTheme.MutedText,
                TextAlign =
                    ContentAlignment.MiddleCenter
            },
            0,
            0);
        root.Controls.Add(value, 0, 1);
        card.Controls.Add(root);
        return card;
    }
    private static int SummaryCardRowHeight()
    {
        return
            12 +
            16 +
            Math.Max(
                22,
                AppMetrics.TextHeight(
                    AppTheme.ButtonFont) + 4) +
            Math.Max(
                30,
                AppMetrics.TextHeight(
                    AppTheme.HeadingFont) + 12) +
            12;
    }
    // It now lives in dbo.GetFeeStatementTransactions -- a verbatim lift of the same query -- for consistency with the rest of the app.
    private static async Task<(DataTable Transactions, decimal OpeningBalance)> LoadStatementDataAsync(
        int admissionId, DateTime? fromDate, DateTime? toDate)
    {
        var data = await DatabaseHelper.QueryUserSetAsync(
            "GetFeeStatementTransactions",
            DatabaseHelper.Int("@AdmissionId", admissionId),
            DatabaseHelper.Date("@FromDate", fromDate),
            DatabaseHelper.Date("@ToDate", toDate),
            DatabaseHelper.Bit("@IncludeOpeningBalance", true));
        if (data.Tables.Count != 2 || data.Tables[1].Rows.Count != 1 ||
            data.Tables[1].Rows[0].IsNull("OpeningBalance"))
            throw new InvalidOperationException("The statement opening balance could not be verified.");
        return (data.Tables[0], Convert.ToDecimal(data.Tables[1].Rows[0]["OpeningBalance"]));
    }
    private async Task LoadStatement()
    {
        InvalidateAccount();
        try{await RunBusyAsync("Generating statement…",async()=>{
            if(!CurrentUser.HasPermission("Reports.View"))
            {
                Grid.DataSource=null;_appliedFilterSignature=string.Empty;SetReportActionsEnabled(false);
                openingBalance.Text=chargesTotal.Text=paymentsTotal.Text=closingBalance.Text="—";
                await RefreshAccountAsync();return;
            }
            await LoadStatementCoreAsync();
            if(!string.IsNullOrEmpty(_appliedFilterSignature))await RefreshAccountAsync();
        },this);}
        finally{if(!IsDisposed){UpdateSelectionActions();UpdateAccountActions();}}
    }
    private async Task LoadStatementCoreAsync()
    {
        var request=statementReads.Begin();
        // Every regeneration attempt invalidates the old financial snapshot,
        // including a failed attempt under exactly the same filters.
        _appliedFilterSignature=string.Empty;
        SetReportActionsEnabled(false);
        SetStatus("Generating statement. The previous snapshot is not current and cannot be exported.");
        try
        {
            if (!ReportUi.ValidDateRange(
                    statementDateValidator ??= CreateValidator(),
                    from,
                    to))
            {
                return;
            }
            var admissionIds =
                GetSelectedAdmissionIds();
            if (admissionIds.Count == 0)
            {
                Warn("Select at least one student first.");
                return;
            }
            var combined =
                CreateStatementTable();
            var chronological=new DataTable();
            var names=new Dictionary<int,string>();
            decimal combinedOpening = 0m;
            decimal combinedCharges = 0m;
            decimal combinedPayments = 0m;
            DateTime? fromDate=from.Checked?from.Value.Date:null,toDate=to.Checked?to.Value.Date:null;
            string signature=FilterSignature();
            var visibleNames=new Dictionary<int,string>(studentNames);
            await SequentialReadBatch.ReadAsync(admissionIds,async id=>{
                var statement=await readStatement(id,fromDate,toDate).ConfigureAwait(false);
                request.Token.ThrowIfCancellationRequested();
                string name=visibleNames.GetValueOrDefault(id,"");
                if(string.IsNullOrWhiteSpace(name)&&statement.Transactions.Columns.Contains("StudentName")&&statement.Transactions.Rows.Count>0)
                    name=Convert.ToString(statement.Transactions.Rows[0]["StudentName"])??"";
                if(string.IsNullOrWhiteSpace(name))
                {
                    var details=await DatabaseHelper.QueryUserAsync("GetStudentDetails",DatabaseHelper.Int("@AdmissionId",id)).ConfigureAwait(false);
                    if(details.Rows.Count==0)throw new InvalidOperationException("A selected statement student could not be verified. Select the student again.");
                    name=Convert.ToString(details.Rows[0]["StudentName"])??"";
                }
                return (statement.Transactions,statement.OpeningBalance,Name:name);
            },(id,statement)=>{
                AppendStudentRows(combined,statement.Transactions,statement.Name);
                AppendAccountTransactions(chronological,statement.Transactions,id,statement.Name);
                names[id]=statement.Name;
                combinedCharges+=SumDistinctCharges(statement.Transactions);
                combinedPayments+=SumPayments(statement.Transactions);
                combinedOpening+=statement.OpeningBalance;
            },request.Token);
            if(IsDisposed||!statementReads.IsCurrent(request)||signature!=FilterSignature())return;
            if (combined.Columns.Contains("StatementStudent") &&
                combined.Columns.Contains("TransactionDate"))
            {
                combined = await Task.Run(()=>{
                    combined.DefaultView.Sort=string.Join(", ",new[]{"BillingMonth","TransactionDate","StatementStudent","FeeDisplayOrder","TransactionId"}.Where(combined.Columns.Contains).Select(column=>column+" ASC"));
                    return combined.DefaultView.ToTable();
                },request.Token);
                if(IsDisposed||!statementReads.IsCurrent(request)||signature!=FilterSignature())return;
            }
            Bind(combined);
            accountStudentNames=names;
            BindAccountTransactions(chronological);
            UiHelper.SetGridEmptyMessage(Grid, "No account entries for the selected students and dates.");
            ConfigureStatementGrid();
            FormatStatementAmounts();
            openingBalance.Text =
                FormatAmount(combinedOpening);
            chargesTotal.Text =
                FormatAmount(combinedCharges);
            paymentsTotal.Text =
                FormatAmount(combinedPayments);
            closingBalance.Text =
                FormatAmount(
                    combinedOpening +
                    combinedCharges -
                    combinedPayments);
            SetStatus($"{admissionIds.Count} student(s) included in statement.");
            /* The statement now on screen was built from exactly these students and dates, so Export is enabled with it. */
            _appliedFilterSignature =
                FilterSignature();
            SetReportActionsEnabled(true);
            UpdateAccountActions();
        }
        catch(OperationCanceledException) when(!statementReads.IsCurrent(request)) { }
        catch (Exception ex)
        {
            if(IsDisposed||!statementReads.IsCurrent(request))return;
            Fail(ex);
            SetStatus("Statement could not be refreshed. The previous snapshot is not current; generate again before exporting. "+DbMessage.UserMessage(ex),PageStatusKind.Warning);
        }
    }
    private static DataTable CreateStatementTable()
    {
        return new DataTable();
    }
    private static void AppendStudentRows(DataTable combined, DataTable source, string studentName)
    {
        if (source.Rows.Count == 0)
            return;
        if (combined.Columns.Count == 0)
        {
            foreach (DataColumn column
                     in source.Columns)
            {
                combined.Columns.Add(column.ColumnName, column.DataType);
            }
            if (!combined.Columns.Contains("StatementStudent"))
            {
                combined.Columns.Add("StatementStudent", typeof(string));
            }
        }
        foreach (DataRow sourceRow
                 in source.Rows)
        {
            if (source.Columns.Contains("TransactionType") &&
                string.Equals(Convert.ToString(sourceRow["TransactionType"]), "PAYMENT", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var target =
                combined.NewRow();
            foreach (DataColumn column
                     in source.Columns)
            {
                if (!combined.Columns.Contains(column.ColumnName))
                {
                    continue;
                }
                target[column.ColumnName] =
                    sourceRow[column.ColumnName];
            }
            target["StatementStudent"] =
                studentName;
            combined.Rows.Add(target);
        }
    }
    private static decimal SumPayments(DataTable table)
    {
        if (!table.Columns.Contains("PaidAmount"))
        {
            return 0m;
        }
        return table.AsEnumerable()
            .Where(
                row =>
                    !table.Columns.Contains(
                        "TransactionType") ||
                    string.Equals(
                        Convert.ToString(row["TransactionType"]), "PAYMENT", StringComparison.OrdinalIgnoreCase))
            .Sum(
                row =>
                    row["PaidAmount"] ==
                    DBNull.Value
                        ? 0m
                        : Convert.ToDecimal(row["PaidAmount"]));
    }
    private static decimal SumDistinctCharges(DataTable table)
    {
        if (!table.Columns.Contains("DebitAmount"))
        {
            return 0m;
        }
        IEnumerable<DataRow> chargeRows =
            table.AsEnumerable();
        if (table.Columns.Contains("TransactionType"))
        {
            chargeRows =
                chargeRows.Where(
                    row =>
                        string.Equals(
                            Convert.ToString(row["TransactionType"]), "CHARGE", StringComparison.OrdinalIgnoreCase));
        }
        if (!table.Columns.Contains("TransactionId"))
        {
            return chargeRows.Sum(
                row =>
                    row["DebitAmount"] ==
                    DBNull.Value
                        ? 0m
                        : Convert.ToDecimal(row["DebitAmount"]));
        }
        return chargeRows
            .GroupBy(
                row =>
                    Convert.ToInt64(row["TransactionId"]))
            .Sum(
                group =>
                    group.First()["DebitAmount"] ==
                    DBNull.Value
                        ? 0m
                        : Convert.ToDecimal(group.First()["DebitAmount"]));
    }
    private static string FormatAmount(decimal value)
    {
        return value.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
    }
    private void ConfigureStatementGrid()
    {
        Grid.CellFormatting-=FormatAccountMonth;Grid.CellFormatting+=FormatAccountMonth;
        Grid.AutoGenerateColumns = true;
        Grid.AllowUserToAddRows = false;
        Grid.AllowUserToDeleteRows = false;
        Grid.ReadOnly = true;
        Grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        Grid.MultiSelect = false;
        Grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        string[] hiddenColumns =
        {
            "StudentName",
            "TransactionId",
            "AdmissionId",
            "TransactionType",
            "EnrollmentId",
            "CreditAmount",
            "Remarks",
            "PaymentMethod",
            "ReceiptNumber"
        };
        foreach (string name in hiddenColumns)
        {
            if (Grid.Columns.Contains(name))
                Grid.Columns[name].Visible = false;
        }
        foreach(DataGridViewColumn column in Grid.Columns)column.Visible=false;
        SetStatementColumn("StatementStudent", "Student", 170, GetSelectedAdmissionIds().Count>1);
        SetStatementColumn("TransactionDate", "Date", 88, true);
        SetStatementColumn("FeeTypeName", UiText.Fees.FeeType, 135, true);
        SetStatementColumn("BillingMonth", "Month", 90, true);
        SetStatementColumn("DebitAmount", "Charge", 78, true);
        SetStatementColumn("PaidAmount", "Paid", 70, true);
        SetStatementColumn("Balance", "Due", 70, true);
        if (Grid.Columns.Contains("TransactionDate"))
            Grid.Columns["TransactionDate"].DefaultCellStyle.Format = "dd-MM-yyyy";
        if (Grid.Columns.Contains("BillingMonth"))
            Grid.Columns["BillingMonth"].DefaultCellStyle.Format = "MMM yyyy";
        if (Grid.Columns.Contains("DebitAmount"))
            Grid.Columns["DebitAmount"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        if (Grid.Columns.Contains("PaidAmount"))
            Grid.Columns["PaidAmount"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        if (Grid.Columns.Contains("Balance"))
            Grid.Columns["Balance"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        int index = 0;
        foreach (string name in new[]
                 {
                     "StatementStudent",
                     "TransactionDate",
                     "FeeTypeName",
                     "BillingMonth",
                     "DebitAmount",
                     "PaidAmount",
                     "Balance"
                 })
        {
            if (Grid.Columns.Contains(name) && Grid.Columns[name].Visible)
                Grid.Columns[name].DisplayIndex = index++;
        }
    }
    private void SetStatementColumn(string name, string header, int minimumWidth, bool visible = true)
    {
        if (!Grid.Columns.Contains(name))
            return;
        var column = Grid.Columns[name];
        column.Visible = visible;
        column.HeaderText = header;
        column.MinimumWidth = minimumWidth;
    }
    private void FormatStatementAmounts()
    {
        string[] amountColumns =
        {
            "DebitAmount",
            "CreditAmount",
            "PaidAmount",
            "Balance",
            "Amount",
            "OpeningBalance",
            "ClosingBalance"
        };
        foreach (string columnName
                 in amountColumns)
        {
            if (!Grid.Columns.Contains(columnName))
            {
                continue;
            }
            Grid.Columns[columnName]
                .DefaultCellStyle
                .Format = "N0";
        }
    }
    /* One print at a time, and the page must still be alive when the institution name comes back. */
    private bool printingDueReport;
    private async Task PrintDueReport(bool byFamily=false)
    {
        if(IsBusy || IsDisposed || !CurrentUser.HasPermission("Fees.View"))return;
        var ids=GetSelectedAdmissionIds();
        if(ids.Count==0){Warn("Select at least one student first.");return;}
        string signature=FilterSignature();
        printingDueReport=true;UpdateSelectionActions();
        try
        {
            using var busy=BeginBusy("Preparing outstanding bills…",this);
            await OutstandingBillsPreview.ShowAsync(FindForm(),ids,()=>!IsDisposed && signature==FilterSignature(),Warn,byFamily:byFamily);
        }
        catch(Exception ex){if(!IsDisposed)Fail(ex);}
        finally{printingDueReport=false;if(!IsDisposed)UpdateSelectionActions();}
    }
    private void ClearForm()
    {
        statementDateValidator?.ClearAll();
        InvalidateAccount();
        if (accountViews is not null) accountViews.SelectedIndex = 0;
        statementReads.Cancel();
        studentSearchVersion++;
        studentSearch.Clear();
        manuallySelectedAdmissionIds.Clear();
        familySelections.Clear();
        studentNames.Clear();
        Grid.DataSource = null;
        try
        {
            Grid.Rows.Clear();
        }
        catch (Exception nonFatalException34)
        {
            FMSC_SMS.Core.AppExceptionHandler.LogNonFatal(nonFatalException34, "StudentAccountStatementPage: ClearForm fallback");
        }
        from.Value =
            DateTime.Today.AddMonths(-1);
        to.Value =
            DateTime.Today;
        from.Checked = to.Checked = false;
        openingBalance.Text =
            "—";
        chargesTotal.Text =
            "—";
        paymentsTotal.Text =
            "—";
        closingBalance.Text =
            "—";
        /* The statement was cleared, so there is nothing current to export until View Statement runs again. */
        _appliedFilterSignature =
            string.Empty;
        SetReportActionsEnabled(false);
        UpdateSelectionActions();
        statementContext.Text = "Select a student to view their account.";
        UiHelper.SetGridPlaceholderMessage(Grid, "Select a student to view their account.");
        UiHelper.SetGridEmptyMessage(Grid, "Select a student to view their account.");
        SetStatus("Select a student above. The complete account is shown unless you choose dates.");
    }
}
