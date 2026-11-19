using FMSC_SMS.Core;
using FMSC_SMS.Pages.Reports;

namespace FMSC_SMS.Pages.Finance.Collection;

public sealed class OutstandingBillsPage : V2Page,IBusyPage
{
    private readonly LookupBox campus=new(),year=new(),cls=new(),section=new();
    private readonly ModernComboBox comparison=HumanUi.Combo(),paper=HumanUi.Combo(),billGrouping=HumanUi.Combo(),orderBy=HumanUi.Combo();
    private readonly ModernNumericTextBox amount=HumanUi.Number(),through=HumanUi.Number();
    private readonly Label selectedSummary=new(){Dock=DockStyle.Fill,AutoSize=true,ForeColor=AppTheme.MutedText,Padding=new Padding(4,6,0,4)};
    private readonly ModernButton preview;
    private readonly BindingSource selectionSource=new();
    private readonly ReportClassSectionFilters locationFilters;
    private readonly Func<OutstandingBillScope,Task<DataTable>> readBalances;
    private DataTable? roster;
    private OutstandingBillScope? loadedScope;
    private bool changingSelection;
    public bool IsBusy=>BusyOperationRunning;
    public string BusyMessage=>"Outstanding bills are being prepared. Wait for the preview before leaving.";
    public OutstandingBillsPage():this(true,null){ }
    internal OutstandingBillsPage(bool initialize,Func<OutstandingBillScope,Task<DataTable>>? readBalances)
        :base(PageTitles.OutstandingBills,"Choose students by placement and balance, then preview their bills in the standard format.")
    {
        this.readBalances=readBalances??ReadBalancesAsync;
        comparison.Items.AddRange(new object[]{"Any outstanding","At least (≥)","More than (>)","At most (≤)","Less than (<)","Exactly (=)","Between (inclusive)"});
        comparison.SelectedIndex=0;
        paper.Items.AddRange(new object[]{"Thermal","A4 sheets"});paper.SelectedIndex=0;
        billGrouping.Items.AddRange(new object[]{"Individual","By family"});billGrouping.SelectedIndex=0;
        orderBy.Items.AddRange(new object[]{"Default","Admission number","Student name","Class / section","Outstanding: highest first","Outstanding: lowest first"});orderBy.SelectedIndex=0;
        foreach(var number in new[]{amount,through}){number.DecimalPlaces=0;number.WholeRupeesOnly=true;}
        var load=AddBusyAction("Load Students",LoadStudentsAsync,"Primary");
        preview=AddBusyAction("Preview Selected Bills",PreviewAsync,"Secondary");preview.Enabled=false;
        FinanceHelp.Attach(this,preview,"Prepare bills for the selected students using their posted unpaid fees. This does not receive payment.","A bill showing Rs. 1,000 due is a request for payment; the student still owes it until money or advance is applied.");
        FinanceHelp.Attach(this,comparison,"Choose which students to include by the size of their outstanding balance. This only filters the list.","At least Rs. 1,000 includes a student owing Rs. 1,000 or Rs. 1,500, but excludes one owing Rs. 500.");
        FinanceHelp.Attach(this,orderBy,"Reorder the loaded students without changing their selection. Family grouping keeps siblings together; each family's first child in the chosen order determines its position.","Highest first puts the child with the largest outstanding balance first, followed by their siblings when grouped by family. Bill preview follows the displayed family order.");
        preview.EnabledChanged+=(_,_)=>{
            if(preview.Enabled && (Selected().Count==0 || loadedScope!=Scope() || !CurrentUser.HasPermission("Fees.View")))preview.Enabled=false;
        };
        var all=AddAction("Select All",(_,_)=>SetSelection(true),"Secondary");
        var none=AddAction("Clear Selection",(_,_)=>SetSelection(false),"Secondary");
        FinanceHelp.Attach(this,all,"Check every student in the loaded bulk list.","Load a class, then Select All to include every matching student.");
        FinanceHelp.Attach(this,none,"Uncheck every student in the loaded bulk list.","Clear Selection leaves no students selected for bill preview.");
        RelocateFromActions(load,preview,all,none);
        Grid.AutoGenerateColumns=false;Grid.ReadOnly=false;Grid.AllowUserToAddRows=false;
        Grid.Columns.Add(new DataGridViewCheckBoxColumn{Name="Include",DataPropertyName="Include",HeaderText="Print",Width=65});
        foreach(var (field,label,width) in new[]{("AdmissionNumber","Adm. / QR No.",115),("StudentName","Student",170),
            ("FatherName","Father",170),("CampusName","Campus",120),("ClassSection","Class – Section",140),
            ("AcademicYear","Academic Year",115),("Outstanding","Outstanding (Rs.)",140)})
            Grid.Columns.Add(new DataGridViewTextBoxColumn{Name=field,DataPropertyName=field,HeaderText=label,ReadOnly=true,MinimumWidth=width,AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill,SortMode=DataGridViewColumnSortMode.NotSortable});
        Grid.Columns["Outstanding"].DefaultCellStyle.Format="N0";
        Grid.Columns["Outstanding"].DefaultCellStyle.Alignment=DataGridViewContentAlignment.MiddleRight;
        Grid.DataSource=selectionSource;
        Grid.CurrentCellDirtyStateChanged+=(_,_)=>{if(Grid.IsCurrentCellDirty)Grid.CommitEdit(DataGridViewDataErrorContexts.Commit);};
        Grid.CellValueChanged+=(_,_)=>{if(!changingSelection)UpdateSummary();};
        locationFilters=new ReportClassSectionFilters(campus,cls,section);
        foreach(var lookup in new[]{campus,year,cls,section})lookup.SelectedIndexChanged+=(_,_)=>FiltersChanged();
        comparison.SelectedIndexChanged+=(_,_)=>{UpdateAmountFields();FiltersChanged();};
        amount.TextChanged+=(_,_)=>FiltersChanged();through.TextChanged+=(_,_)=>FiltersChanged();
        orderBy.SelectedIndexChanged+=(_,_)=>ApplyOrder();
        billGrouping.SelectedIndexChanged+=(_,_)=>ApplyOrder();

        var card=HumanUi.Card("Bulk Bills","Filter and load students, then preview the selected bills.");
        var filters=ReportUi.Bar(5,("Campus",campus),("Academic Year",year),("Class",cls),("Section",section),("Bill grouping",billGrouping),
            ("Student balance",comparison),("Amount (Rs.)",amount),("Through (Rs.)",through),("Paper format",paper),("Order by",orderBy));
        filters.AutoSize=true;filters.AutoSizeMode=AutoSizeMode.GrowAndShrink;filters.Dock=DockStyle.Fill;
        var body=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=4,Margin=Padding.Empty};
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var actions=HumanUi.ButtonRowInOrder(load,all,none,preview);actions.Dock=DockStyle.Fill;actions.Margin=Padding.Empty;
        body.Controls.Add(filters,0,0);body.Controls.Add(actions,0,1);body.Controls.Add(Grid,0,2);body.Controls.Add(selectedSummary,0,3);
        card.Content.Controls.Add(body);
        var viewport=new Panel{Dock=DockStyle.Fill,AutoScroll=true};card.Dock=DockStyle.None;viewport.Controls.Add(card);
        void Arrange()=>card.Size=new Size(Math.Max(AppScale.Scale(760),viewport.ClientSize.Width-SystemInformation.VerticalScrollBarWidth),
            Math.Max(AppScale.Scale(500),viewport.ClientSize.Height));
        viewport.SizeChanged+=(_,_)=>Arrange();SetContent(viewport);Arrange();
        UpdateAmountFields();UpdateSummary();
        if(initialize)Defer(BindFiltersAsync);
        Disposed+=(_,_)=>{selectionSource.Dispose();roster?.Dispose();};
    }

    private OutstandingBillScope Scope()=>new(campus.SelectedId,year.SelectedId,cls.SelectedId,section.SelectedId,
        (BillBalanceComparison)comparison.SelectedIndex,amount.Value,through.Value);
    private void UpdateAmountFields(){amount.Enabled=comparison.SelectedIndex>0;through.Enabled=comparison.SelectedIndex==(int)BillBalanceComparison.Between;}
    private void FiltersChanged(){preview.Enabled=false;if(loadedScope is not null)SetStatus("Filters changed. Load Students to refresh the list.",PageStatusKind.Warning);}
    private void Commit(){Grid.EndEdit();selectionSource.EndEdit();}
    private List<int> Selected()=>selectionSource.List.Cast<DataRowView>().Where(row=>row.Row.Field<bool>("Include"))
        .Select(row=>row.Row.Field<int>("AdmissionId")).ToList();
    private void ApplyOrder()
    {
        if(roster is null||IsBusy)return;
        Commit();
        ApplyRosterOrder();
        UpdateSummary();
    }
    private void ApplyRosterOrder()
    {
        if(roster is null)return;
        if(!roster.Columns.Contains("__BillOrder"))roster.Columns.Add("__BillOrder",typeof(int));
        var rows=OutstandingBillSelection.Order(roster,(BillSortOrder)orderBy.SelectedIndex,billGrouping.SelectedIndex==1);
        changingSelection=true;selectionSource.RaiseListChangedEvents=false;
        try {
            for(int index=0;index<rows.Length;index++)rows[index]["__BillOrder"]=index;
            roster.DefaultView.Sort="__BillOrder ASC";
        }
        finally {selectionSource.RaiseListChangedEvents=true;selectionSource.ResetBindings(false);changingSelection=false;}
    }
    private void UpdateSummary()
    {
        var ids=Selected();decimal total=roster?.AsEnumerable().Where(row=>row.Field<bool>("Include")).Sum(row=>Convert.ToDecimal(row["Outstanding"]))??0m;
        selectedSummary.Text=$"{ids.Count} selected of {roster?.Rows.Count??0} loaded · Selected balance: Rs. {total:N0} · Full posted outstanding — all periods, including future posted months";
        preview.Enabled=ids.Count>0 && loadedScope==Scope() && CurrentUser.HasPermission("Fees.View");
    }
    private void SetSelection(bool value)
    {
        if(roster is null||IsBusy)return;
        Commit();
        // Publish one completed selection after committing the active checkbox.
        changingSelection=true;
        selectionSource.RaiseListChangedEvents=false;
        try
        {
            foreach(DataRow row in roster.Rows){row["Include"]=value;row.EndEdit();}
        }
        finally
        {
            selectionSource.RaiseListChangedEvents=true;
            selectionSource.ResetBindings(false);
            changingSelection=false;
            UpdateSummary();
        }
    }
    private bool Valid(OutstandingBillScope scope)
    {
        if(scope.Comparison!=BillBalanceComparison.Any && scope.Amount<=0){Warn("Enter a balance amount greater than zero.");amount.Focus();return false;}
        if(scope.Comparison==BillBalanceComparison.Between && scope.Through<scope.Amount){Warn("Through amount must be at least the first amount.");through.Focus();return false;}
        return true;
    }
    private async Task LoadStudentsAsync()
    {
        if(IsBusy||!CurrentUser.HasPermission("Fees.View"))return;
        var scope=Scope();if(!Valid(scope))return;
        int user=CurrentUser.UserId;
        try
        {
            await RunBusyAsync("Loading students…",async()=>{
                using var source=await readBalances(scope);
                if(IsDisposed||scope!=Scope()||CurrentUser.UserId!=user||!CurrentUser.HasPermission("Fees.View"))return;
                var next=OutstandingBillSelection.Select(source,scope);selectionSource.DataSource=null;roster?.Dispose();roster=next;loadedScope=scope;
                selectionSource.DataSource=roster.DefaultView;
                ApplyRosterOrder();
                SetStatus($"{roster.Rows.Count} students match. Balances and readiness are reviewed before numbered bills are prepared.");
            },this);
        }
        catch(Exception ex){if(!IsDisposed){loadedScope=null;preview.Enabled=false;Fail(ex);}}
        finally{if(!IsDisposed){ApplyRosterOrder();UpdateSummary();}}
    }
    private async Task PreviewAsync()
    {
        if(IsBusy||!CurrentUser.HasPermission("Fees.View"))return;
        Commit();var scope=Scope();var ids=Selected();bool a4=paper.SelectedIndex==1,byFamily=billGrouping.SelectedIndex==1;int sort=orderBy.SelectedIndex;
        if(ids.Count==0||scope!=loadedScope||!Valid(scope))return;
        try { await RunBusyAsync(BusyMessage,async()=>{
            using var fresh=await readBalances(scope);
            if(IsDisposed||scope!=Scope()||sort!=orderBy.SelectedIndex||byFamily!=(billGrouping.SelectedIndex==1))return;
            var eligible=fresh.AsEnumerable().Where(row=>scope.Matches(Convert.ToDecimal(row["Outstanding"]))).Select(row=>row.Field<int>("AdmissionId")).ToHashSet();
            if(ids.Any(id=>!eligible.Contains(id))){loadedScope=null;preview.Enabled=false;Warn("Some selected students no longer match the filters. Load Students again before printing.");return;}
            await OutstandingBillsPreview.ShowAsync(FindForm(),ids,()=>!IsDisposed&&scope==Scope()&&sort==orderBy.SelectedIndex&&ids.SequenceEqual(Selected())&&byFamily==(billGrouping.SelectedIndex==1),Warn,a4,byFamily);
        },this); }
        catch(Exception ex){if(!IsDisposed)Fail(ex);}
        finally{if(!IsDisposed){ApplyRosterOrder();UpdateSummary();}}
    }
    private static Task<DataTable> ReadBalancesAsync(OutstandingBillScope scope)=>DatabaseHelper.QuerySqlAsync(OutstandingBillSelection.Query,
        DatabaseHelper.Int("@UserId",CurrentUser.UserId),DatabaseHelper.Int("@CampusId",scope.Campus),DatabaseHelper.Int("@AcademicYearId",scope.Year),
        DatabaseHelper.Int("@ClassId",scope.Class),DatabaseHelper.Int("@SectionId",scope.Section));
    private async Task BindFiltersAsync()
    {
        try {
        var tasks=new[]{DatabaseHelper.QueryUserAsync("GetCampuses"),DatabaseHelper.QueryUserAsync("GetAcademicYears"),DatabaseHelper.QueryUserAsync("GetClasses"),ReportUi.SectionFilterListAsync(),ReportClassSectionLookup.QueryAsync(activeOnly:true)};
        await Task.WhenAll(tasks);if(IsDisposed)return;
        campus.Bind(tasks[0].Result,"CampusId","CampusName","All");year.Bind(tasks[1].Result,"AcademicYearId","YearName","All");
        locationFilters.Bind(tasks[2].Result,tasks[3].Result,tasks[4].Result);
        } catch(Exception ex){if(!IsDisposed)Fail(ex);}
    }
}
