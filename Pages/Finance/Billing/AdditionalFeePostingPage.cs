using FMSC_SMS.Core;
using Microsoft.Data.SqlClient;
using FMSC_SMS.Pages.Reports;
namespace FMSC_SMS.Pages.Finance.Billing;
public sealed class AdditionalFeePostingPage : V2Page, IBusyPage
{
    private bool posting;
    public bool IsBusy => posting;
    public string BusyMessage => "Additional fees are being posted. Wait for the result before leaving.";
    private readonly LookupBox year = new(), campus = new(), klass = new(), section = new(), fee = new();
    private readonly ReportClassSectionFilters locationFilters;
    private readonly ModernDatePicker date = HumanUi.Date();
    private readonly ModernNumericTextBox amount = new() { Minimum = 0, Maximum = 100000000, WholeRupeesOnly = true, DecimalPlaces = 0, Dock = DockStyle.Fill };
    private readonly ModernTextBox reason = HumanUi.TextBox(false, 500);
    private readonly DataGridView students = HumanUi.Grid(true);
    private readonly HashSet<int> selected = new();
    private DataTable? candidates;
    private readonly AdditionalFeeRecovery recovery;
    private bool loading;
    private readonly LatestReadScope candidateReads=new();
    private (int Year,int Campus,int Class,int Section,DateTime ChargeDate)? loadedScope;
    private readonly Func<int,int,int,int,Task<DataTable>> loadCandidates;
    private readonly ModernButton selectAll,post,checkSave;
    private (int Year,int Campus,int Class,int Section,DateTime ChargeDate) CurrentScope=>(year.SelectedId,campus.SelectedId,klass.SelectedId,section.SelectedId,date.Value.Date);
    private bool CurrentCandidates=>candidates is not null&&loadedScope==CurrentScope;
    private void UpdateSelectionActions()
    {
        selectAll.Enabled=!posting&&CurrentCandidates&&candidates!.Rows.Count>0;
        post.Enabled=!posting&&!recovery.Blocked&&CurrentCandidates&&selected.Count>0&&CurrentUser.HasPermission("Fees.Collect");
        checkSave.Visible=recovery.Blocked;checkSave.Enabled=!posting;
    }
    public AdditionalFeePostingPage() : this(null) { }
    internal AdditionalFeePostingPage(Func<int,int,int,int,Task<DataTable>>? loadCandidates,bool initialize=true)
        : this(loadCandidates,initialize,new AdditionalFeeRecovery()) { }
    internal AdditionalFeePostingPage(Func<int,int,int,int,Task<DataTable>>? loadCandidates,bool initialize,AdditionalFeeRecovery recovery) : base("Post Additional Fee",
        "Choose a fee and amount, load students, then select who to charge.")
    {
        locationFilters = new ReportClassSectionFilters(campus, klass, section);
        this.recovery=recovery;
        this.loadCandidates=loadCandidates ?? ((y,c,k,s)=>DatabaseHelper.QueryUserAsync("GetAdditionalFeeCandidates",
            DatabaseHelper.Int("@AcademicYearId",y),DatabaseHelper.Int("@CampusId",c),DatabaseHelper.Int("@ClassId",k),
            DatabaseHelper.Int("@SectionId",s),DatabaseHelper.Date("@ChargeDate",date.Value.Date)));
        Disposed+=(_,_)=>candidateReads.Dispose();
        var load = AddBusyAction("Load Students", LoadAsync, "Secondary");
        selectAll = AddAction(UiText.Actions.SelectAll, (_, _) => SelectAll(), "Secondary");
        post = AddAction("Post Selected", async(_,_)=>await PostAsync(), "Success");
        checkSave = AddAction("Check Save Outcome", async(_,_)=>await CheckSaveAsync(), "Secondary");
        FinanceHelp.Attach(this,post,FinanceHelp.Topic.PostFees);
        FinanceHelp.Attach(this,amount,FinanceHelp.Topic.FeeRate);
        FinanceHelp.Attach(this,date,"Choose the date for the additional fee. Saving adds a fee due for each selected student; it does not collect payment.","Select three students and enter Rs. 100 per student: each student is charged Rs. 100, for Rs. 300 in total.");
        GateAction(post,"Fees.Collect");
        RelocateFromActions(load, selectAll, post);
        students.ReadOnly = false;
        students.AutoGenerateColumns = false;
        students.AllowUserToAddRows = false;
        students.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Selected", HeaderText = "Post", Width = AppScale.Scale(65) });
        /* Admission Number first - it is what identifies a student; the registration number is a reporting label and follows it. */
        foreach (var name in new[] { "AdmissionNumber", "StudentName", "FatherName", "CampusName", "ClassName", "SectionName" })
            students.Columns.Add(new DataGridViewTextBoxColumn {
                Name = name, HeaderText = name switch {
                    "AdmissionNumber" => "Admission Number",
                    "RegistrationNumber" => "Registration",
                    "StudentName" => "Student",
                    "FatherName" => "Father",
                    "CampusName" => "Campus",
                    "ClassName" => "Class",
                    _ => "Section"
                }, DataPropertyName = name, ReadOnly = true
            });
        students.CurrentCellDirtyStateChanged += (_, _) => {
            if (students.IsCurrentCellDirty) students.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        students.CellValueChanged += (_, e) => {
            if (loading || e.RowIndex < 0 || e.ColumnIndex != 0 || candidates is null) return;
            if (students.Rows[e.RowIndex].DataBoundItem is not DataRowView bound) return;
            int id = Convert.ToInt32(bound["EnrollmentId"]);
            if (Convert.ToBoolean(students.Rows[e.RowIndex].Cells[0].Value)) selected.Add(id);
            else selected.Remove(id);
            UpdateSelectionActions();
        };
        var fields = new TableLayoutPanel { Dock = DockStyle.Top, Height = AppMetrics.FieldRowHeight * 2, ColumnCount = 4, RowCount = 2 };
        for (int i = 0; i < 4; i++) fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        for (int i = 0; i < 2; i++) fields.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.FieldRowHeight));
        fields.Controls.Add(HumanUi.Field("Academic year", year, true), 0, 0);
        fields.Controls.Add(HumanUi.Field("Campus", campus), 1, 0);
        fields.Controls.Add(HumanUi.Field("Class", klass), 2, 0);
        fields.Controls.Add(HumanUi.Field("Section", section), 3, 0);
        fields.Controls.Add(HumanUi.Field("Charge date", date, true, fillHeight: false), 0, 1);
        fields.Controls.Add(HumanUi.Field("Amount per student", amount, true, fillHeight: false), 1, 1);
        fields.Controls.Add(HumanUi.Field("Fee type", fee, true), 2, 1);
        fields.Controls.Add(HumanUi.Field("Remarks", reason, false, fillHeight: false), 3, 1);
        /* The three actions were removed from the action bar above and never put anywhere else, so the page had no buttons at all. */
        foreach (Control field in fields.Controls)
            foreach (Label label in field.Controls.OfType<Label>()) label.Font = AppTheme.NormalFont;
        var root = ReportUi.WorkflowLayout(fields, students, "Posting Scope",
            "Choose a fee and amount, then load students. Remarks are optional.",
            "Students to Charge", "Select the students to charge, then Post Selected.", load, selectAll, post);
        SetContent(root);
        FormFlow.OrderTableFields(root);
        FormFlow.EnterMovesToNext(this);
        year.SelectedIndexChanged += (_, _) => ClearCandidates();
        campus.SelectedIndexChanged += (_, _) => ClearCandidates();
        klass.SelectedIndexChanged += (_, _) => ClearCandidates();
        section.SelectedIndexChanged += (_, _) => ClearCandidates();
        date.ValueChanged += (_, _) => ClearCandidates();
        year.Enabled = false;
        UpdateSelectionActions();
        if(initialize)Defer(BindAsync);
        SetStatus("Choose the fee and amount, load students, then select and post. Remarks are optional.");
    }
    private async Task BindAsync()
    {
        try
        {
            recovery.Load();
            if(recovery.Blocked)SetStatus("An earlier additional-fee posting needs checking. Use Check Save Outcome before posting again.",PageStatusKind.Warning);
            Task<DataTable> yearsTask = DatabaseHelper.QueryUserAsync("GetAcademicYears");
            Task<DataTable> campusesTask = DatabaseHelper.QueryUserAsync("GetCampuses");
            Task<DataTable> classesTask = DatabaseHelper.QueryUserAsync("GetClasses");
            Task<DataTable> sectionsTask = ReportUi.SectionFilterListAsync();
            Task<DataTable> assignmentsTask = ReportClassSectionLookup.QueryAsync(activeOnly: true);
            Task<DataTable> feeTypesTask = DatabaseHelper.QueryUserAsync(
                "GetFeeTypes");
            await Task.WhenAll(yearsTask, campusesTask, classesTask, sectionsTask, assignmentsTask, feeTypesTask);
            if(IsDisposed)return;
            DataTable years = await yearsTask;
            HumanUi.BindCurrentAcademicYear(year, years);
            campus.Bind(await campusesTask, "CampusId", "CampusName", "All campuses");
            locationFilters.Bind(await classesTask, await sectionsTask, await assignmentsTask,
                "All classes", "All sections");
            DataTable feeTypes = await feeTypesTask;
            feeTypes.DefaultView.RowFilter = "DefaultFrequency = 'As Needed' AND FeeTypeCode NOT IN ('ADMISSION','TUITION','TRANSPORT')";
            fee.Bind(feeTypes.DefaultView.ToTable(), "FeeTypeId", "FeeTypeName");
        }
        catch (Exception ex) { if(!IsDisposed)Fail(ex); }
        finally { if(!IsDisposed)UpdateSelectionActions(); }
    }
    private void ClearCandidates()
    {
        candidateReads.Cancel();loadedScope=null;
        SetStatus("Filters changed. Load students, then select those to charge.");
        candidates = null;
        students.DataSource = null;
        selected.Clear();
        UpdateSelectionActions();
    }
    private void SelectAll()
    {
        if (posting||!CurrentCandidates) return;
        loading = true;
        foreach (DataGridViewRow row in students.Rows)
        {
            if (row.DataBoundItem is not DataRowView bound) continue;
            row.Cells[0].Value = true;
            selected.Add(Convert.ToInt32(bound["EnrollmentId"]));
        }
        loading = false;
        UpdateSelectionActions();
        SetStatus($"{selected.Count} students selected. Untick any exceptions before posting.");
    }
    private async Task LoadAsync()
    {
        if(posting)return;
        ClearCandidates();
        if (year.SelectedId <= 0)
        {
            Warn("Set the current academic year in Academic Year setup first.");
            return;
        }
        var scope=CurrentScope;
        var request=candidateReads.Begin();
        try
        {
            var data=await loadCandidates(scope.Year,scope.Campus,scope.Class,scope.Section);
            if(IsDisposed||!candidateReads.IsCurrent(request)||scope!=CurrentScope)return;
            candidates=data;loadedScope=scope;
            loading=true;
            try
            {
                students.DataSource=candidates;
                foreach(DataGridViewRow row in students.Rows)row.Cells[0].Value=false;
            }
            finally{loading=false;}
            UpdateSelectionActions();
            SetStatus($"{candidates.Rows.Count} students eligible on {scope.ChargeDate:dd MMM yyyy}. Students whose billing has not started are excluded. Tick those to charge.");
        }
        catch(Exception ex)
        {
            if(!IsDisposed&&candidateReads.IsCurrent(request)&&scope==CurrentScope){ClearCandidates();Fail(ex);}
        }
    }
    private async Task PostAsync()
    {
        if (posting || recovery.Blocked) return;
        if(!CurrentCandidates){SetStatus("Filters changed or students have not been loaded. Load students again before posting.",PageStatusKind.Warning);return;}
        if (!ValidateRequiredInputs())
            return;
        students.EndEdit();
        if (candidates is null || selected.Count == 0 || fee.SelectedId <= 0 ||
            amount.Value <= 0 || !WholeRupees.IsWhole(amount.Value))
        {
            Warn("Select students and a fee, then enter a positive whole-rupee amount.");
            return;
        }
        var ids=selected.OrderBy(id=>id).ToArray();
        var chosen=candidates!.AsEnumerable().Where(r=>ids.Contains(Convert.ToInt32(r["EnrollmentId"]))).ToArray();
        if(chosen.Length!=ids.Length){ClearCandidates();SetStatus("The selected students no longer match this list. Load students again.",PageStatusKind.Warning);return;}
        var scope=CurrentScope;
        string scopeText=$"{year.Text} · {campus.Text} · {klass.Text} · {section.Text}";
        string studentText=string.Join("\n",chosen.Take(6).Select(r=>$"{r["AdmissionNumber"]} · {r["StudentName"]}"));
        if(chosen.Length>6)studentText+=$"\n…and {chosen.Length-6} more selected students.";
        int count = ids.Length;
        if (!Confirm($"Post {fee.Text} to {count} students?\n{scopeText}\n{studentText}\nCharge date: {date.Value:dd-MMM-yyyy}\nAmount each: Rs. {amount.Value:N0}\nTotal: Rs. {amount.Value * count:N0}",
            "Post Additional Fee")) return;
        if(!CurrentCandidates||scope!=CurrentScope)return;
        string postedFeeName = fee.Text;
        decimal postedAmount = amount.Value;
        DateTime postedDate = date.Value;
        posting = true;UpdateSelectionActions();
        try
        {
            using var busy = BeginBusy(BusyMessage, this);
            var args=AdditionalFeePostingWriter.Parameters(fee.SelectedId,date.Value,amount.Value,0m,
                string.IsNullOrWhiteSpace(reason.Text)?$"Additional fee: {fee.Text}":reason.Text.Trim(),ids);
            await recovery.SaveAsync(args,$"{postedFeeName}: {count} student(s), Rs. {postedAmount:N0} each, {postedDate:dd MMM yyyy}");
            Info($"{postedFeeName} posted to {count} student(s): Rs. {postedAmount:0.##} each, dated {postedDate:dd MMM yyyy}.");
            ClearCandidates();
        }
        catch (Exception ex) { if(!IsDisposed){Fail(ex);if(recovery.Blocked)SetStatus("The save needs checking. Use Check Save Outcome before posting again.",PageStatusKind.Warning);} }
        finally { posting = false;if(!IsDisposed)UpdateSelectionActions(); }
    }
    private async Task CheckSaveAsync()
    {
        if(posting)return;
        posting=true;UpdateSelectionActions();
        try
        {
            using var busy=BeginBusy("Checking the earlier posting…",this);
            int? count=await recovery.CheckAsync();
            if(IsDisposed)return;
            ClearCandidates();
            Info(count>0?$"The earlier posting was saved for {count} student(s). No additional charges were posted.":
                "No saved posting was found. Load students and review the details before posting again.");
        }
        catch(Exception ex){if(!IsDisposed)Fail(ex);}
        finally{posting=false;if(!IsDisposed)UpdateSelectionActions();}
    }
}
