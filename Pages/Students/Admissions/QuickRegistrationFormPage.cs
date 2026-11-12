using FMSC_SMS.Core;
using Microsoft.Data.SqlClient;
namespace FMSC_SMS.Pages.Students.Admissions;

// Shared child editor hosted by FamilyAdmissionWorkspace. The standalone
// Quick Registration screen is retired; its protected writes/recovery remain.
internal sealed partial class QuickRegistrationFormPage : V2Page, IUnsavedChanges
{
    private readonly Action<Func<Control>, string, string> navigate;
    private readonly Func<int, Task<(DataTable Details, DataTable Fees, string Snapshot)>> loadRegistration;
    private bool applyingRegistration, needsRefresh;
    private bool recoveryBlocked;
    private QuickRegistrationRecoveryJournal.Pending? pendingSave;
    private int registrationLoadVersion;
    private readonly StudentPicker family = new();
    private readonly ModernTextBox parent = HumanUi.TextBox(false, 500);
    private readonly ModernTextBox contact = HumanUi.TextBox(false, 30);
    private readonly ModernTextBox address = HumanUi.TextBox(false, 500);
    private readonly ModernTextBox name = HumanUi.TextBox(false, 500);
    private readonly LookupBox year = new();
    private readonly PlacementPicker placement = new();
    private readonly ModernDatePicker joining = HumanUi.Date();
    private readonly CheckBox monthlyConcessionsFromAdmission=new(){Text="Apply monthly concessions from the admission month",Checked=true,AutoSize=true};
    private readonly ModernButton addFee = new() { Text="+ Add Fee", ThemeRole="Secondary", AutoSize=true, Height=AppMetrics.ButtonHeight };
    private readonly ContextMenuStrip feeMenu = new();
    private readonly DataGridView fees = new() { Dock=DockStyle.Fill, AllowUserToAddRows=false, AllowUserToDeleteRows=false, AutoGenerateColumns=false, RowHeadersVisible=false };
    private readonly Label total=new(){Dock=DockStyle.Fill,AutoSize=true,Font=AppTheme.ButtonFont,TextAlign=ContentAlignment.MiddleRight,Padding=new Padding(8),Text="Bill Total: Rs. 0"};
    private DataTable placements = new(), rates = new();
    private int savedStudent, familyAnchor, loadVersion, familyLoadVersion, defaultAcademicYear;
    private int savedFamilyMemberId, savedFamilyMemberCount;
    internal int SavedRegistrationId=>savedStudent;
    internal bool HasUnresolvedSave=>recoveryBlocked;
    internal void ShowRegistrationContextWarning(string message)=>Warn(message);
    internal bool IsBusy=>busy||loadingFees||loadingFamily;
    private string savedStatus="";
    private string savedSnapshot="",expectedQuote="";
    // Owner policy (FR-06): date/year alone on a blank new registration do not prompt.
    // Student details, placement and existing-record edits retain normal discard warnings.
    public bool HasUnsavedChanges=>recoveryBlocked||HasPendingChanges||(savedStudent==0&&(name.Text.Trim().Length>0||parent.Text.Trim().Length>0||contact.Text.Trim().Length>0||address.Text.Trim().Length>0||placement.SelectedId>0));
    public string UnsavedChangesMessage=>recoveryBlocked?"A registration save still needs verification. Its recovery record will be retained when you leave. Check Save Outcome when you return; do not register this child again until its outcome is clear.":"Discard the unsaved registration changes?";
    private string familyReference="";
    private string[] savedBasics=Array.Empty<string>();
    private bool busy, loadingFamily, loadingFees, feesLoaded;
    private readonly FormValidator validator;
    private readonly ModernButton save, next, bill, collect, reload, newFamily, checkSave;
    private readonly ModernButton linkFamily, unlinkFamily;
    private readonly HumanCard registrationCard;
    private readonly TableLayoutPanel registrationContent;
    private readonly TableLayoutPanel root;
    private readonly Control familyField;
    internal QuickRegistrationFormPage(Action<Func<Control>, string, string> navigate, int preselectRegistrationId=0,bool initialize=true)
        : this(navigate, preselectRegistrationId, async id => {
            var result = await QuickRegistrationWriter.LoadAsync(id);
            return (result.Details, result.Fees, result.Snapshot);
        },initialize) { }

    internal QuickRegistrationFormPage(Action<Func<Control>, string, string> navigate, int preselectRegistrationId,
        Func<int, Task<(DataTable Details, DataTable Fees, string Snapshot)>> loadRegistration)
        :this(navigate,preselectRegistrationId,loadRegistration,true){ }

    internal QuickRegistrationFormPage(Action<Func<Control>,string,string> navigate,int preselectRegistrationId,
        Func<int,Task<(DataTable Details,DataTable Fees,string Snapshot)>> loadRegistration,bool initialize)
        : base("Quick Registration", "Save basic details, print a bill or add siblings. Review and confirm from Pending Registrations.")
    {
        this.navigate=navigate;
        this.loadRegistration=loadRegistration;
        Disposed += (_, _) => ++registrationLoadVersion;
        validator=CreateValidator();
        // These operations own their enabled state, including a stale post-save
        // screen that must remain disabled until a deliberate reload succeeds.
        save=AddAction("Save Registration", async (_, _) => await SaveAsync(), "Success");
        reload=AddAction("Reload Registration", async (_, _) => { await OpenRegistrationAsync(savedStudent); }, "Secondary");
        checkSave=AddAction("Check Save Outcome",async(_,_)=>await CheckSaveOutcomeAsync(),"Secondary");
        next=AddAction("Add Another Child", (_,_)=>StartNextDraft(false), "Secondary");
        bill=AddBusyAction("Print Bill", PrintBillAsync, "Secondary");
        collect=AddAction("Collect Payment",(_,_)=>{if(FeeCollectionAccess.CanUse(CurrentUser.HasPermission))navigate(()=>new FMSC_SMS.Pages.Finance.Collection.FeeCollectionPage(savedStudent),"Daily Work","Fee Collection");},"Primary");
        newFamily=AddAction("New Family",(_,_)=>StartNextDraft(true),"Secondary");
        GateAction(save,"Students.Admit"); GateAction(collect,"Fees.Collect");
        collect.Visible = FeeCollectionAccess.CanUse(CurrentUser.HasPermission);
        GateAction(next,"Students.Administer");
        linkFamily=AddAction("Link Existing Parent",(_,_)=>ToggleFamilySearch(),"Secondary");
        unlinkFamily=AddAction("Unlink Parent",async(_,_)=>await UnlinkFamilyAsync(),"Secondary");
        GateAction(linkFamily,"Students.Administer");GateAction(unlinkFamily,"Students.Administer");
        SetToolTip(next,"Register another child of the same parent. Parent details carry forward; pending registrations can form a family before admission confirmation.");
        SetToolTip(newFamily,"Start a child with different parent details. Unsaved changes require confirmation before clearing.");
        SetToolTip(linkFamily,"Choose another child of this parent, including a pending QR registration. You can link before or after saving.");
        SetToolTip(unlinkFamily,"Remove the family link while retaining this student's details, fees and payments.");
        SetToolTip(bill,"Print outstanding charges. One child produces a Student Bill; several children produce a Family Bill.");
        PhoneNumberInput.Attach(contact,()=>validator);
        registrationCard=HumanUi.Card("Registration Details", "Enter a child, or use Link Existing Parent.");
        registrationContent=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2};
        registrationContent.RowStyles.Add(new RowStyle(SizeType.Absolute,0));
        registrationContent.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        familyField=HumanUi.Field("Find another child of this parent (pending QR or admitted student)",family);familyField.Visible=false;familyField.TabIndex=0;
        registrationContent.Controls.Add(familyField,0,0);
        var fields=Fields(("Student name",name,true,1),("Joining date",joining,true,1),("Academic year",year,true,1),("Campus / Class / Section",placement,true,1),("Parent name",parent,true,1),("Primary phone (optional)",contact,false,1),("Address",address,false,2));
        fields.TabIndex=1;registrationContent.Controls.Add(fields,0,1);registrationCard.Content.Controls.Add(registrationContent);
        WholeNumberInput.WireGrid(fees);
        fees.Columns.Add(new DataGridViewCheckBoxColumn { Name="Include",DataPropertyName="Include",HeaderText="Include",Width=65 });
        fees.Columns.Add(new DataGridViewTextBoxColumn { Name="Fee",DataPropertyName="FeeTypeName",HeaderText=UiText.Fees.FeeType,ReadOnly=true,AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill });
        fees.Columns.Add(new DataGridViewTextBoxColumn { Name="Frequency",DataPropertyName="Frequency",HeaderText=UiText.Fees.Frequency,ReadOnly=true,Width=100 });
        fees.Columns.Add(new DataGridViewTextBoxColumn { Name="Amount",DataPropertyName="Amount",HeaderText=UiText.Fees.Amount,ReadOnly=true,Width=110 });
        fees.Columns.Add(new DataGridViewTextBoxColumn { Name="Discount",DataPropertyName="Discount",HeaderText=UiText.Fees.Discount,ReadOnly=!CurrentUser.HasPermission("Fees.Adjust"),Width=125 });
        fees.Columns.Add(new DataGridViewTextBoxColumn { Name="NetAmount",DataPropertyName="NetAmount",HeaderText="Plan Net",ReadOnly=true,Width=145 });
        fees.Columns.Add(CreateCrossActionColumn("Remove",useColumnTextForButtonValue:false));
        fees.CellContentClick+=async(_,e)=>{if(e.RowIndex>=0&&e.ColumnIndex>=0&&fees.Columns[e.ColumnIndex].Name=="Remove"&&fees.Rows[e.RowIndex].DataBoundItem is DataRowView row)await RemoveFeeAsync(row.Row);};
        addFee.Click+=async(_,_)=>await ShowAddFeeAsync();
        Disposed+=(_,_)=>feeMenu.Dispose();
        var concessionColumn = new DataGridViewComboBoxColumn { Name="ConcessionLabel", DataPropertyName="ConcessionLabel",
            HeaderText="Concession", Width=120, FlatStyle=FlatStyle.Flat };
        FeeConcessionLabels.Configure(concessionColumn);
        fees.Columns.Add(concessionColumn);
        fees.Columns["Amount"].DefaultCellStyle.Format=fees.Columns["Discount"].DefaultCellStyle.Format=fees.Columns["NetAmount"].DefaultCellStyle.Format="N0";
        fees.CellValidating+=(_,e)=>{
            if(e.RowIndex<0||fees.Columns[e.ColumnIndex].Name!="Discount")return;
            bool valid=fees.Rows[e.RowIndex].DataBoundItem is DataRowView row&&InputParse.TryWholeAmount(Convert.ToString(e.FormattedValue),out decimal discount)&&discount>=0&&discount<=Convert.ToDecimal(row["Amount"]);
            e.Cancel=!valid;if(!valid)SetStatus("Enter a whole-rupee discount between zero and its fee amount.");
        };
        fees.DataError+=(_,e)=>{e.ThrowException=false;SetStatus("Enter a valid discount amount.");};
        fees.CurrentCellDirtyStateChanged+=(_,_)=>{if(fees.IsCurrentCellDirty&&fees.CurrentCell is DataGridViewCheckBoxCell or DataGridViewComboBoxCell)fees.CommitEdit(DataGridViewDataErrorContexts.Commit);};
        fees.CellValueChanged+=(_,e)=>{
            if(e.RowIndex>=0&&e.ColumnIndex>=0&&fees.Columns[e.ColumnIndex].Name=="ConcessionLabel"&&
                fees.Rows[e.RowIndex].DataBoundItem is DataRowView entry&&Convert.ToString(entry["ConcessionLabel"]) is "Free" or "Waived")
                entry["Discount"]=entry["Amount"];
            if(e.ColumnIndex>=0&&fees.Columns[e.ColumnIndex].Name!="Remove")UpdateTotal();
            if(e.ColumnIndex>=0&&fees.Columns[e.ColumnIndex].Name=="ConcessionLabel")UpdateActions();};
        fees.CellEndEdit+=(_,_)=>{if(fees.CurrentRow?.DataBoundItem is DataRowView row)row.EndEdit();UpdateTotal();};
        foreach(var input in new[]{name,parent,contact,address})input.TextChanged+=(_,_)=>PendingEditChanged();
        var feeCard=HumanUi.Card("Fee Plan", "Choose whether monthly concessions begin in the admission month or the following month. Print Bill or open Fee Collection for the actual posted dues.");
        var feeContent=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=3};feeContent.RowStyles.Add(new RowStyle(SizeType.AutoSize));feeContent.RowStyles.Add(new RowStyle(SizeType.Percent,100));feeContent.RowStyles.Add(new RowStyle(SizeType.AutoSize));feeContent.Controls.Add(monthlyConcessionsFromAdmission,0,0);feeContent.Controls.Add(fees,0,1);var feeFooter=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,WrapContents=true};feeFooter.Controls.Add(addFee);feeFooter.Controls.Add(total);feeContent.Controls.Add(feeFooter,0,2);feeCard.Content.Controls.Add(feeContent);
        root=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=1,RowCount=2 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute,AppMetrics.FieldCardHeight(4)));
        root.RowStyles.Add(new RowStyle(SizeType.Percent,100));root.Controls.Add(registrationCard,0,0);root.Controls.Add(feeCard,0,1);SetContent(root);
        family.UserSelectionChanged+=async(_,_)=>await LoadFamilyAsync();
        family.RowFilter=row=>savedStudent==0||Convert.ToInt32(row["AdmissionId"])!=savedStudent;
        family.SelectionCleared+=(_,_)=>{if(applyingRegistration)return;++familyLoadVersion;if(savedStudent==0){familyAnchor=0;familyReference="";}loadingFamily=false;UpdateActions();};
        placement.SelectedIndexChanged+=async(_,_)=>{if(!applyingRegistration)await LoadFeesAsync();};
        if(initialize)Load+=async(_,_)=>{await LoadLookupsAsync();if(recoveryBlocked)await CheckSaveOutcomeAsync(afterSave:true);else if(preselectRegistrationId>0)await LoadRegistrationAsync(preselectRegistrationId);};UpdateActions();
        SetStatus("Review student details, placement and opening fees, then Save Registration.");
        if(initialize)ReadRecoveryRecord();UpdateActions();
    }
    private static TableLayoutPanel Fields(params (string Label,Control Input,bool Required,int Span)[] fields)
    {
        int rows=(fields.Sum(field=>field.Span)+1)/2;
        var grid=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=rows};
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));
        for(int row=0;row<rows;row++)grid.RowStyles.Add(new RowStyle(SizeType.Absolute,AppMetrics.FieldRowHeight));
        int cell=0;
        for(int i=0;i<fields.Length;i++){
            var field=HumanUi.Field(fields[i].Label,fields[i].Input,fields[i].Required);field.TabIndex=i;
            grid.Controls.Add(field,cell%2,cell/2);grid.SetColumnSpan(field,fields[i].Span);cell+=fields[i].Span;
        }
        return grid;
    }
    private void ShowFamilyPicker(bool visible){
        familyField.Visible=visible;
        registrationContent.RowStyles[0].Height=visible?AppMetrics.FieldRowHeight:0;
        root.RowStyles[0].Height=AppMetrics.FieldCardHeight(workspaceHosted?2:4)+(visible?AppMetrics.FieldRowHeight:0);
        linkFamily.Text=visible?"Hide Parent Search":"Link Existing Parent";
    }
    private void ToggleFamilySearch()
    {
        if(IsBusy||recoveryBlocked||needsRefresh||HasPendingChanges||!CurrentUser.HasPermission("Students.Administer"))return;
        ShowFamilyPicker(!familyField.Visible);if(familyField.Visible)family.Focus();
    }
    private async Task LoadLookupsAsync()
    {
        try {
            var years=await DatabaseHelper.QueryUserAsync("GetAcademicYears");
            placements=await DatabaseHelper.QueryUserAsync("GetClassSections",DatabaseHelper.Int("@CampusId",0),DatabaseHelper.Int("@ClassId",0),DatabaseHelper.Int("@SectionId",0));
            year.Bind(years,"AcademicYearId","YearName");placement.Bind(placements);
            var current=years.AsEnumerable().FirstOrDefault(r=>r["StartDate"] is DateTime start && r["EndDate"] is DateTime end && joining.Value.Date>=start.Date && joining.Value.Date<=end.Date);
            defaultAcademicYear=current is null?0:Convert.ToInt32(current["AcademicYearId"]);
            year.SelectedValue=defaultAcademicYear;
        }catch(Exception ex){Fail(ex);}
    }
    private async Task LoadFamilyAsync()
    {
        if(busy)return;
        int version=++familyLoadVersion;
        if(family.SelectedId<=0){if(savedStudent==0)familyAnchor=0;loadingFamily=false;UpdateActions();return;}
        loadingFamily=true;UpdateActions();
        int selected=family.SelectedId;
        try {
            var data=await DatabaseHelper.QueryUserAsync("GetStudentDetails",DatabaseHelper.Int("@AdmissionId",selected));
            if(IsDisposed||version!=familyLoadVersion||selected!=family.SelectedId)return;
            if(data.Rows.Count==0){Warn("That student was not found. Choose another child of the same parent.");return;}
            if(savedStudent>0){await LinkSavedFamilyAsync(selected,data.Rows[0]);return;}
            var r=data.Rows[0];familyAnchor=selected;parent.Text=Convert.ToString(r["FatherName"]);contact.Text=Convert.ToString(r["ContactNumber"]);address.Text=Convert.ToString(r["Address"]);
            familyReference=Convert.ToString(r.IsNull("AdmissionNumber")?r["QuickReference"]:r["AdmissionNumber"])??"";
            ShowFamilyPicker(false);name.Focus();
        }catch(Exception ex){if(version==familyLoadVersion&&selected==family.SelectedId){if(savedStudent==0)familyAnchor=0;Fail(ex);}}
        finally{if(!IsDisposed&&version==familyLoadVersion){loadingFamily=false;UpdateActions();}}
    }
    private void ClearFamilySelection()
    {
        bool previous=applyingRegistration;applyingRegistration=true;
        try{family.Clear();ShowFamilyPicker(false);}
        finally{applyingRegistration=previous;}
    }
    private async Task LinkSavedFamilyAsync(int selected, DataRow candidate)
    {
        try
        {
            if(HasPendingChanges){Warn("Save changes before linking an existing parent.");return;}
            if(selected==savedStudent){Warn("Choose another child of the same parent, not this student.");return;}
            if(needsRefresh||!CurrentUser.HasPermission("Students.Administer")||savedStatus is not ("Pending" or "Active"))return;
            string target=$"{candidate["StudentName"]} · Parent: {candidate["FatherName"]}";
            if(!Confirm($"Link {name.Text.Trim()} (parent: {parent.Text.Trim()}) to the family of {target}?\n\nVerify this is the same parent. Existing student details, fees and payments are retained.","Link Existing Parent"))return;
            busy=true;UpdateActions();
            await QuickRegistrationWriter.ChangeFamilyAsync(savedStudent,savedSnapshot,selected);
            await RefreshFamilyChangeAsync("Parent linked. The student now shares that child's family.");
        }
        catch(Exception ex){FamilyChangeFailed(ex);}
        finally{busy=false;loadingFamily=false;if(!IsDisposed){ClearFamilySelection();UpdateActions();}}
    }
    private async Task UnlinkFamilyAsync()
    {
        if(IsBusy||recoveryBlocked||needsRefresh||HasPendingChanges||!CurrentUser.HasPermission("Students.Administer"))return;
        if(savedStudent==0)
        {
            ++familyLoadVersion;familyAnchor=0;familyReference="";ClearFamilySelection();UpdateActions();
            SetStatus("Parent link removed. Entered details are retained and can be edited.");return;
        }
        if(savedFamilyMemberId<=0||savedStatus is not ("Pending" or "Active"))return;
        if(!Confirm($"Remove {name.Text.Trim()} from the linked family?\n\nThe student, parent/contact details, fees and payments are retained. You can link another parent later.","Unlink Parent"))return;
        busy=true;UpdateActions();
        try{
            await QuickRegistrationWriter.ChangeFamilyAsync(savedStudent,savedSnapshot,null);
            await RefreshFamilyChangeAsync("Parent link removed. The student and financial history are retained.");
        }catch(Exception ex){FamilyChangeFailed(ex);}
        finally{busy=false;if(!IsDisposed)UpdateActions();}
    }
    private void FamilyChangeFailed(Exception ex)
    {
        needsRefresh=true;StudentSearchCache.Invalidate();
        AppExceptionHandler.LogNonFatal(ex,"Quick Registration parent link update");
        if(!IsDisposed)SetStatus("Could not confirm the parent-link update. Use Reload Registration to check the current family before continuing. "+DbMessage.UserMessage(ex),PageStatusKind.Warning);
    }
    private async Task RefreshFamilyChangeAsync(string message)
    {
        needsRefresh=true;savedSnapshot="";StudentSearchCache.Invalidate();
        var result=await LoadRegistrationAsync(savedStudent,afterSave:true);
        if(IsDisposed||result==RegistrationLoadOutcome.Superseded)return;
        SetStatus(result==RegistrationLoadOutcome.Loaded?message:
            "The parent link was updated in the database, but the screen could not refresh. Use Reload Registration before continuing.",
            result==RegistrationLoadOutcome.Loaded?PageStatusKind.Success:PageStatusKind.Warning);
    }
    private async Task LoadFeesAsync()
    {
        int version=++loadVersion;feesLoaded=false;rates=new();fees.DataSource=null;UpdateTotal();
        loadingFees=placement.SelectedId>0;UpdateActions();
        if(!loadingFees)return;
        int selectedPlacement=placement.SelectedId;
        try{
            var data=await Task.Run(()=>DatabaseHelper.QuerySql("""
                EXEC dbo.RequirePermission @UserId,N'Students.Admit';
                """ + QuickRegistrationWriter.FeeQuery,DatabaseHelper.Int("@UserId",CurrentUser.UserId),DatabaseHelper.Int("@Placement",selectedPlacement)));
            if(IsDisposed||version!=loadVersion)return;
            var missing=data.AsEnumerable().Where(r=>r.IsNull("Amount")).ToArray();
            string missingNames=string.Join(", ",missing.Select(r=>Convert.ToString(r["FeeTypeName"])));
            foreach(var row in missing)data.Rows.Remove(row);
            expectedQuote=QuickRegistrationWriter.Quote(data);SetRates(data);feesLoaded=missing.Length==0;
            if(!feesLoaded)SetStatus("Configure the required class fees before saving: "+missingNames+".");
            UpdateTotal();
        }catch(Exception ex){if(version==loadVersion)Fail(ex);}
        finally{if(version==loadVersion){loadingFees=false;UpdateActions();}}
    }
    private async Task ShowAddFeeAsync()
    {
        if(!addFee.Enabled||IsBusy||recoveryBlocked||needsRefresh)return;
        try
        {
            int version=registrationLoadVersion,feeVersion=loadVersion,selectedPlacement=placement.SelectedId,user=CurrentUser.UserId;
            var options=savedStudent==0?rates.Copy():await Task.Run(()=>DatabaseHelper.QuerySql("EXEC dbo.RequirePermission @UserId,N'Students.View';\n"+QuickRegistrationWriter.FeeQuery,DatabaseHelper.Int("@UserId",user),DatabaseHelper.Int("@Placement",selectedPlacement)));
            if(IsDisposed||IsBusy||recoveryBlocked||needsRefresh||version!=registrationLoadVersion||feeVersion!=loadVersion||user!=CurrentUser.UserId)return;
            feeMenu.Items.Clear();
            var included=rates.AsEnumerable().Where(r=>Convert.ToBoolean(r["Include"])).Select(r=>Convert.ToInt32(r["FeeTypeId"])).ToHashSet();
            string quote=QuickRegistrationWriter.Quote(options);
            foreach(DataRow option in options.Rows)
            {
                int feeId=Convert.ToInt32(option["FeeTypeId"]);
                if(included.Contains(feeId)||option.IsNull("Amount"))continue;
                var item=feeMenu.Items.Add($"{option["FeeTypeName"]} · Rs. {option["Amount"]:N0} · {option["Frequency"]}");
                item.Click+=async(_,_)=>{
                    if(!addFee.Enabled||IsBusy||version!=registrationLoadVersion||feeVersion!=loadVersion||user!=CurrentUser.UserId)return;
                    if(savedStudent==0){var row=rates.AsEnumerable().Single(r=>Convert.ToInt32(r["FeeTypeId"])==feeId);row["Include"]=true;UpdateTotal();}
                    else await ChangeSavedFeeAsync(feeId,null,quote);
                };
            }
            if(feeMenu.Items.Count==0){var item=feeMenu.Items.Add("No additional configured fees. Configure this class in Fee Setup.");item.Enabled=false;}
            feeMenu.Show(addFee,new Point(0,addFee.Height));
        }
        catch(Exception ex){if(!IsDisposed)Fail(ex);}
    }
    private async Task RemoveFeeAsync(DataRow row)
    {
        if(IsBusy||recoveryBlocked||needsRefresh||savedStudent>0&&savedStatus!="Pending"||!CurrentUser.HasPermission("Fees.Adjust")||Convert.ToBoolean(row["HasPayments"]))return;
        if(!fees.EndEdit())return;
        if(savedStudent==0){row["Include"]=false;UpdateTotal();return;}
        if(HasPendingChanges){SetStatus("Save changes before removing a saved fee.",PageStatusKind.Warning);return;}
        if(!Confirm($"Remove {row["FeeTypeName"]} (Rs. {Convert.ToDecimal(row["Amount"])-Convert.ToDecimal(row["Discount"]):N0}) from this registration? Unpaid charges will be cancelled; history will be retained.","Remove Fee"))return;
        await ChangeSavedFeeAsync(Convert.ToInt32(row["FeeTypeId"]),Convert.ToInt32(row["StudentFeeAssignmentId"]),null);
    }
    private async Task ChangeSavedFeeAsync(int feeId,int? assignment,string? quote)
    {
        if(IsBusy||HasPendingChanges||recoveryBlocked||needsRefresh)return;
        busy=true;UpdateActions();
        try
        {
            await QuickRegistrationWriter.ChangeFeeAsync(savedStudent,savedSnapshot,placement.SelectedId,feeId,assignment,quote);
            needsRefresh=true;
            if(await LoadRegistrationAsync(savedStudent,afterSave:true)==RegistrationLoadOutcome.Loaded)SetStatus(assignment.HasValue?"Fee removed. Unpaid charges cancelled; history retained.":"Fee added to the opening bill.",PageStatusKind.Success);
        }
        catch(Exception ex){needsRefresh=true;Fail(ex);}
        finally{busy=false;if(!IsDisposed)UpdateActions();}
    }
    private void SetRates(DataTable data)
    {
        var prepared = QuickRegistrationView.PrepareFees(data);
        rates = prepared; fees.DataSource = prepared;
    }
    // Business boundary: Save Registration stores the quick/pending registration;
    // confirmation of official admission is a separate review step. Printing an
    // outstanding bill and collecting money are also separate actions. An uncertain
    // save outcome must be reconciled before another write (recoveryBlocked).
    // See Audit/OWNER_DECISIONS_AND_INVARIANTS.md.
    private bool ReadyToSave=>!recoveryBlocked&&!needsRefresh&&!busy&&!loadingFamily&&!loadingFees&&feesLoaded&&(savedStudent==0||(savedStatus=="Pending"&&savedSnapshot.Length>0))&&(family.SelectedId<=0||family.SelectedId==familyAnchor)
        &&(savedStudent==0&&familyAnchor==0||CurrentUser.HasPermission("Students.Administer"));
    private string[] BasicValues()=>new[]{name.Text.Trim(),parent.Text.Trim(),contact.Text.Trim(),address.Text.Trim()};
    private bool HasPendingChanges=>savedStudent>0&&savedStatus=="Pending"&&savedBasics.Length==4&&(!savedBasics.SequenceEqual(BasicValues())||rates.AsEnumerable().Any(r=>Convert.ToDecimal(r["Discount"])!=Convert.ToDecimal(r["OriginalDiscount"])));
    private void PendingEditChanged(){if(applyingRegistration)return;UpdateActions();if(!busy&&!recoveryBlocked&&HasPendingChanges)SetStatus("Save changes before printing, collecting payment or changing the parent link.");}
    private void UpdateActions(){save.Enabled=ReadyToSave&&QuickRegistrationAccess.CanUse(CurrentUser.HasPermission);save.Text=savedStudent>0?"Save Changes":"Save Registration";next.Enabled=!IsBusy&&!recoveryBlocked&&!needsRefresh&&savedStudent>0&&CurrentUser.HasPermission("Students.Administer");newFamily.Enabled=!IsBusy&&!recoveryBlocked&&!needsRefresh;bill.Enabled=!IsBusy&&!recoveryBlocked&&!needsRefresh&&!HasPendingChanges&&savedStudent>0&&savedStatus!="Cancelled"&&CurrentUser.HasPermission("Fees.View");collect.Enabled=!IsBusy&&!recoveryBlocked&&!needsRefresh&&!HasPendingChanges&&savedStudent>0&&savedStatus!="Cancelled"&&FeeCollectionAccess.CanUse(CurrentUser.HasPermission);
        checkSave.Visible=recoveryBlocked;checkSave.Enabled=recoveryBlocked&&!IsBusy;
        reload.Visible=savedStudent>0 && (!workspaceHosted || needsRefresh);reload.Enabled=!IsBusy&&!recoveryBlocked&&savedStudent>0;
        bool editing=!busy&&!loadingFamily&&!recoveryBlocked&&!needsRefresh&&(savedStudent==0||savedStatus=="Pending"&&CurrentUser.HasPermission("Students.Administer"));year.Enabled=placement.Enabled=joining.Enabled=editing&&savedStudent==0;
        bool canLink=!IsBusy&&!recoveryBlocked&&!needsRefresh&&!HasPendingChanges&&(savedStudent==0||savedStatus is "Pending" or "Active")&&CurrentUser.HasPermission("Students.Administer");
        family.Enabled=linkFamily.Enabled=canLink;
        unlinkFamily.Enabled=canLink&&(savedStudent==0?familyAnchor>0:savedFamilyMemberId>0);
        name.ReadOnly=contact.ReadOnly=address.ReadOnly=!editing;
        if (siblingEntry) { contact.ReadOnly = address.ReadOnly = true; year.Enabled = false; siblingGender.Enabled = editing && savedStudent == 0; }
        parent.ReadOnly=!editing||familyAnchor>0||loadingFamily;
        registrationCard.SetHeaderText(siblingEntry ? "Admit Sibling" : workspaceHosted?"Child Details":"Registration Details",recoveryBlocked?"Save recovery pending · Use Check Save Outcome before continuing.":siblingEntry ? "Shared details are inherited and locked. Enter the child's name, gender, placement and fees." : workspaceHosted?"Parent details are shared above. Review this child's placement and fees.":savedStudent>0
            ? $"{familyReference} · "+(savedFamilyMemberCount>1?$"Family: {savedFamilyMemberCount} children · Link or unlink a parent above.":"One child · You can link an existing parent above.")
            : familyAnchor>0?$"Parent linked · {familyReference} · Use Unlink Parent to change it.":"Enter a child, or use Link Existing Parent above.");
        addFee.Enabled=editing&&feesLoaded&&(savedStudent==0||!HasPendingChanges&&CurrentUser.HasPermission("Fees.Adjust"));
        monthlyConcessionsFromAdmission.Visible=savedStudent==0;
        monthlyConcessionsFromAdmission.Enabled=editing&&savedStudent==0&&CurrentUser.HasPermission("Fees.Adjust");
        fees.ReadOnly=!editing;
        // Switching grid ReadOnly can reset individual column/cell flags.
        // Restore the edit policy after every busy or saved-state transition.
        foreach(DataGridViewColumn column in fees.Columns)column.ReadOnly=column.Name switch{
            "Discount"=>!editing||!CurrentUser.HasPermission("Fees.Adjust"),"ConcessionLabel"=>!editing||savedStudent>0||!CurrentUser.HasPermission("Fees.Adjust"),"Include"=>!editing||savedStudent>0,"Remove"=>false,_=>true};
        foreach(DataGridViewRow row in fees.Rows)if(row.DataBoundItem is DataRowView data){
            row.Cells["Include"].ReadOnly=!editing||savedStudent>0||(Convert.ToBoolean(data.Row["IsMandatory"])&&!CurrentUser.HasPermission("Fees.Adjust"));
            bool removable=editing&&!Convert.ToBoolean(data.Row["HasPayments"])&&CurrentUser.HasPermission("Fees.Adjust");
            if(!Equals(row.Cells["Remove"].Value,removable?"×":""))row.Cells["Remove"].Value=removable?"×":"";
            row.Cells["Remove"].ToolTipText=Convert.ToBoolean(data.Row["HasPayments"])?"This fee has a payment. Use Fee Corrections.":!CurrentUser.HasPermission("Fees.Adjust")?"Fee adjustment permission is required.":"Remove this fee from the opening bill.";
            row.Cells["ConcessionLabel"].ReadOnly = !editing || savedStudent > 0 ||
                !CurrentUser.HasPermission("Fees.Adjust") || Convert.ToString(data.Row["Frequency"]) != "Monthly";
            bool paid=Convert.ToBoolean(data.Row["HasPayments"]);
            row.Cells["Discount"].ReadOnly=!editing||paid||!CurrentUser.HasPermission("Fees.Adjust")||(savedStudent>0&&Convert.ToString(data.Row["ConcessionLabel"])!="None")||Convert.ToString(data.Row["Frequency"])!="Monthly"&&savedStudent>0||Convert.ToString(data.Row["ConcessionLabel"]) is "Free" or "Waived";
            row.Cells["Discount"].ToolTipText=paid?"This fee has a payment. Use Fee Corrections to adjust it.":savedStudent>0?"Future monthly discounts use Fee Discount in Admission / Review. Existing bills are adjusted in Fee Collection.":"Use the selected start for monthly concessions.";
        }
        if(recoveryOnly)
        {
            foreach(var button in new[]{save,next,bill,collect,newFamily,linkFamily,unlinkFamily,reload})button.Visible=false;
            registrationContent.Enabled=fees.Enabled=monthlyConcessionsFromAdmission.Enabled=addFee.Enabled=false;
        }
        WorkspaceChanged?.Invoke(this,EventArgs.Empty);
    }
    private void UpdateTotal()
    {
        if(applyingRegistration)return;
        if(!rates.Columns.Contains("Include")){total.Text="Select the fee plan.";return;}
        var selected=rates.AsEnumerable().Where(r=>Convert.ToBoolean(r["Include"])).ToArray();
        decimal monthly=selected.Where(r=>Convert.ToString(r["Frequency"])=="Monthly")
            .Sum(r=>Convert.ToDecimal(r["Amount"])-Convert.ToDecimal(r["Discount"]));
        decimal other=selected.Where(r=>Convert.ToString(r["Frequency"])!="Monthly")
            .Sum(r=>Convert.ToDecimal(r["Amount"])-Convert.ToDecimal(r["Discount"]));
        total.Text=workspaceHosted?$"Monthly: Rs. {monthly:N0} · Yearly / once: Rs. {other:N0}":
            $"Monthly plan: Rs. {monthly:N0} · Yearly / one-time fees: Rs. {other:N0}";
        PendingEditChanged();
    }
    private void StartNextDraft(bool startNewFamily)
    {
        if(IsBusy||recoveryBlocked||needsRefresh||!fees.EndEdit())return;
        if(!startNewFamily&&!CurrentUser.HasPermission("Students.Administer"))return;
        if(HasUnsavedChanges&&!Confirm("Discard the unsaved registration changes and "+(startNewFamily?"start a new family?":"enter the next child?"),UiText.Dialogs.UnsavedChanges))return;
        if(startNewFamily){familyAnchor=0;familyReference="";ClearFamilySelection();parent.Clear();contact.Clear();address.Clear();}
        ClearChild();
        if(startNewFamily)SetStatus("New family. Enter the student and parent details, choose placement, then Save Registration.");
    }
    private void ClearChild()
    {
        if(busy||recoveryBlocked)return;
        // Start a fresh child; only the family link and shared details carry over.
        // Invalidate any previous fee load before clearing the placement.
        ++registrationLoadVersion;++familyLoadVersion;++loadVersion;loadingFamily=false;needsRefresh=false;savedStudent=0;savedFamilyMemberId=savedFamilyMemberCount=0;savedStatus="";savedSnapshot="";expectedQuote="";loadingFees=false;feesLoaded=false;
        name.Clear();monthlyConcessionsFromAdmission.Checked=true;joining.Value=DateTime.Today;year.SelectedValue=defaultAcademicYear;
        placement.SelectedValue=0;rates=new();fees.DataSource=null;
        validator.Begin();UpdateTotal();UpdateActions();name.Focus();
        SetStatus("Enter the next child and choose their placement. Family details are retained.");
    }
    private async Task SaveAsync()
    {
        if(!ReadyToSave)return;
        var v=validator;v.Begin();v.Required(name);v.Required(parent);v.Required(year);v.Required(placement);
        if (siblingEntry && savedStudent == 0) v.Required(siblingGender);
        v.Check(contact,PakistanPhoneNumber.IsValidOptional(contact.Text),PakistanPhoneNumber.ValidationMessage);
        if(!v.Finish())return;
        if(!fees.EndEdit()){Warn("Correct the discount amount before saving.");return;}var row=placements.AsEnumerable().First(r=>Convert.ToInt32(r["ClassSectionId"])==placement.SelectedId);
        var selected=rates.AsEnumerable().Where(r=>Convert.ToBoolean(r["Include"])).ToArray();
        if(selected.Any(r=>!WholeRupees.IsWhole(Convert.ToDecimal(r["Discount"]))||!WholeRupees.IsWhole(Convert.ToDecimal(r["Amount"]))||Convert.ToDecimal(r["Discount"])<0||Convert.ToDecimal(r["Discount"])>Convert.ToDecimal(r["Amount"]))) {Warn("Enter a whole-rupee discount between zero and its fee amount.");return;}
        if(savedStudent>0){await SavePendingAsync();return;}
        busy=true;UpdateActions();using var hold=BeginBusy("Saving registration…",family,parent,contact,address,name,year,placement,joining,fees);
        try{
            // Only new registrations need active section selection; saved history is retained.
            await ActiveClassSectionPolicy.EnsureActiveAsync(placement.SelectedId);
            // Re-read the family parent before saving. The copied contact/address
            // belong to this child; a linked parent cannot be renamed here.
            if(familyAnchor>0){
                var anchor=await DatabaseHelper.QueryUserAsync("GetStudentDetails",DatabaseHelper.Int("@AdmissionId",familyAnchor));
                if(anchor.Rows.Count==0){Warn("The linked family record could not be found. Choose the family again.");return;}
                string parentName=Convert.ToString(anchor.Rows[0]["FatherName"])?.Trim()??"";
                if(parent.Text.Trim()!=parentName){parent.Text=parentName;Warn("The linked parent name has changed. Review it and save again. Correct parent details in Admission / Review.");return;}
            }
            var args=new List<SqlParameter>{DatabaseHelper.Int("@UserId",CurrentUser.UserId),DatabaseHelper.Bit("@QuickRegistration",true),DatabaseHelper.Bit("@UseSelectedFeeTypes",true),DatabaseHelper.P("@FamilyStudentId",familyAnchor>0?familyAnchor:null),
                DatabaseHelper.Date("@AdmissionDate",joining.Value.Date),DatabaseHelper.Int("@AdmissionCampusId",Convert.ToInt32(row["CampusId"])),DatabaseHelper.Int("@AcademicYearId",year.SelectedId),DatabaseHelper.Int("@ClassSectionId",placement.SelectedId),
                DatabaseHelper.P("@StudentName",name.Text.Trim()),DatabaseHelper.P("@FatherName",parent.Text.Trim()),DatabaseHelper.P("@ContactNumber",contact.Text.Trim()),DatabaseHelper.P("@Address",address.Text.Trim()),DatabaseHelper.P("@Disability","No"),DatabaseHelper.Bit("@UsesTransport",selected.Any(r=>Convert.ToBoolean(r["IsTransportLinked"]))),
                new SqlParameter("@SelectedFeeTypeIds",SqlDbType.Structured){TypeName="dbo.FeeTypeIdList",Value=Tvp.FeeTypeIds(selected.Select(r=>Convert.ToInt32(r["FeeTypeId"])))},
                new SqlParameter("@AdmissionDiscounts",SqlDbType.Structured){TypeName="dbo.AdmissionFeeDiscountType",Value=Tvp.AdmissionFeeDiscounts(selected.Where(r=>Convert.ToDecimal(r["Discount"])>0).Select(r=>(Convert.ToInt32(r["FeeTypeId"]),Convert.ToDecimal(r["Discount"]))))}};
            args.Add(DatabaseHelper.P("@FeeConcessionsJson",System.Text.Json.JsonSerializer.Serialize(
                selected.Where(r=>Convert.ToString(r["ConcessionLabel"])!="None")
                    .Select(r=>new { FeeTypeId=Convert.ToInt32(r["FeeTypeId"]),ConcessionLabel=Convert.ToString(r["ConcessionLabel"]) }))));
            args.Add(new SqlParameter("@ExcludedFeeTypeIds",SqlDbType.Structured){TypeName="dbo.FeeTypeIdList",Value=Tvp.FeeTypeIds(rates.AsEnumerable().Where(r=>Convert.ToBoolean(r["IsMandatory"])&&!Convert.ToBoolean(r["Include"])).Select(r=>Convert.ToInt32(r["FeeTypeId"])))});
            foreach(string key in new[]{"DateOfBirth","PlaceOfBirth","Gender","BFormNumber","Religion","BloodGroup","AlternateContactNumber","PreviousSchool","FatherOccupation"})args.Add(DatabaseHelper.P("@"+key,siblingEntry ? key == "PlaceOfBirth" ? DatabaseHelper.OptionalText(siblingBirth.Text) : key == "Gender" ? siblingGender.Text : null : null));
            if (siblingEntry) args.Add(DatabaseHelper.Bit("@RequireCurrentAcademicYear", true));
            if(selected.Any(r=>Convert.ToString(r["Frequency"])=="Monthly"&&Convert.ToDecimal(r["Discount"])>0))
                args.Add(DatabaseHelper.Date("@MonthlyDiscountFrom",new DateTime(joining.Value.Year,joining.Value.Month,1).AddMonths(monthlyConcessionsFromAdmission.Checked?0:1)));
            await SubmitNewRegistrationAsync(args.ToArray());
        }catch(Exception ex){ReadRecoveryRecord();if(recoveryBlocked)ShowSaveRecovery();else Fail(ex);}finally{busy=false;UpdateActions();}
    }
    private void ReadRecoveryRecord()
    {
        try{pendingSave=QuickRegistrationRecoveryJournal.ReadPending();recoveryBlocked=pendingSave is not null;
            if(recoveryBlocked)ShowSaveRecovery();}
        catch(Exception ex){recoveryBlocked=true;AppExceptionHandler.LogNonFatal(ex,"Reading registration recovery");
            SetStatus("A previous registration save needs review. Use Check Save Outcome. "+DbMessage.UserMessage(ex),PageStatusKind.Warning);}
    }
    private void ShowSaveRecovery()=>SetStatus("Registration save not yet confirmed"+(pendingSave is null?"":": "+pendingSave.Summary)+
        ". Your current entries are retained. Use Check Save Outcome before saving or starting another registration.",PageStatusKind.Warning);
    private bool ClearVerifiedRecovery()
    {
        if(pendingSave is null)return false;
        try{if(QuickRegistrationRecoveryJournal.ReadPending() is not null)QuickRegistrationRecoveryJournal.ClearVerified(pendingSave.Id);pendingSave=null;recoveryBlocked=false;return true;}
        catch(Exception ex){AppExceptionHandler.LogNonFatal(ex,"Clearing verified registration recovery");
            SetStatus("The save outcome was checked, but its recovery record could not be cleared. Use Check Save Outcome again before continuing. "+DbMessage.UserMessage(ex),PageStatusKind.Warning);return false;}
    }
    private async Task SubmitNewRegistrationAsync(SqlParameter[] parameters)
    {
        int actor=CurrentUser.UserId;
        string scope=await QuickRegistrationWriter.GetScopeAsync();
        if(IsDisposed||CurrentUser.UserId!=actor)return;
        using var journal=QuickRegistrationRecoveryJournal.Begin(actor,scope,
            QuickRegistrationWriter.PayloadHash(actor,placement.SelectedId,expectedQuote,parameters),
            $"{name.Text.Trim()} · Parent: {parent.Text.Trim()} · {joining.Value:dd MMM yyyy}");
        pendingSave=journal.Intent;recoveryBlocked=true;UpdateActions();
        if(workspaceHosted)workspaceSaveRequest=journal.Intent.Id;
        SavedQuickRegistration? saved=null;
        try{saved=await QuickRegistrationWriter.SaveNewAsync(journal.Intent,placement.SelectedId,expectedQuote,parameters);}
        catch(QuickRegistrationOutcomeUnknownException ex){AppExceptionHandler.LogNonFatal(ex,"Unconfirmed Quick Registration save");}
        catch(Exception ex){journal.Dispose();if(ClearVerifiedRecovery())Fail(ex);return;}
        finally{journal.Dispose();}
        if(IsDisposed)return; // Keep the durable intent for recovery on the next open.
        if(saved is not null)await ApplyVerifiedSaveAsync(saved);
        else{ShowSaveRecovery();await CheckSaveOutcomeAsync(afterSave:true);}
    }
    private async Task ApplyVerifiedSaveAsync(SavedQuickRegistration saved)
    {
        savedStudent=saved.Id;savedStatus="Pending";savedSnapshot="";needsRefresh=true;
        await CompleteCommittedSaveAsync(saved.Id,saved.Reference,isNew:true);
        if(IsDisposed)return;
        if(needsRefresh){SetStatus($"Saved registration {saved.Reference} was confirmed, but its screen could not refresh. The recovery record is retained. Use Check Save Outcome to reload it.",PageStatusKind.Warning);return;}
        ClearVerifiedRecovery();
    }
    private async Task CheckSaveOutcomeAsync(bool afterSave=false)
    {
        if(IsBusy&&!afterSave)return;
        if(workspaceHosted)
        {
            try
            {
                var pending=QuickRegistrationRecoveryJournal.ReadPending()??pendingSave;
                if(pending is not null && pending.Id!=workspaceSaveRequest)
                {
                    if(WorkspaceRecoveryRequested is not null)await WorkspaceRecoveryRequested();
                    else Warn("Use Check Previous Save in the family workspace. This child's identity has been retained.");
                    return;
                }
            }
            catch(Exception ex){Fail(ex);return;}
        }
        bool wasBusy=busy;busy=true;UpdateActions();int version=registrationLoadVersion;
        try
        {
            // Another window may have cleared a verified intent. Retain the
            // in-memory request identity and check SQL before releasing this draft.
            pendingSave=QuickRegistrationRecoveryJournal.ReadPending()??pendingSave;
            if(pendingSave is null){recoveryBlocked=false;SetStatus("The previous recovery record has been resolved. Review the current registration before continuing.");return;}
            recoveryBlocked=true;
            using var progress=BeginLoading("Checking the registration save…");
            var saved=await QuickRegistrationWriter.CheckSaveAsync(pendingSave);
            if(IsDisposed||version!=registrationLoadVersion)return;
            if(saved is null)
            {
                if(savedStudent>0){SetStatus("A previously confirmed student's recovery marker was not found. Keep this record for administrator review.",PageStatusKind.Warning);return;}
                if(ClearVerifiedRecovery())SetStatus("The database confirmed this request did not save a student. Review or re-enter the details, then Save Registration when ready.",PageStatusKind.Warning);
            }
            else
            {
                await ApplyVerifiedSaveAsync(saved);
                if(!IsDisposed&&!recoveryBlocked&&!needsRefresh)SetStatus($"Saved registration {saved.Reference} verified. The current student record is loaded.",PageStatusKind.Success);
            }
        }
        catch(Exception ex){recoveryBlocked=true;AppExceptionHandler.LogNonFatal(ex,"Checking registration save outcome");
            if(!IsDisposed)SetStatus("The save outcome is still unknown. Keep the recovery record and use Check Save Outcome after the connection or access is restored. "+DbMessage.UserMessage(ex),PageStatusKind.Warning);}
        finally{busy=wasBusy;if(!IsDisposed)UpdateActions();}
    }
    private sealed record EditorState(int Student, string Status, string Snapshot, string Quote, int Anchor, string Reference,
        string[] Basics, bool FeesLoaded, bool NeedsRefresh, DataTable Rates, string Name, string Parent, string Contact,
        string Address, DateTime Joining, int Year, int Placement, int FamilyId, string FamilyText, bool FamilyShown, string Total, int MemberId, int MemberCount, bool ConcessionsFromAdmission);

    private EditorState CaptureEditor() => new(savedStudent,savedStatus,savedSnapshot,expectedQuote,familyAnchor,familyReference,
        savedBasics,feesLoaded,needsRefresh,rates,name.Text,parent.Text,contact.Text,address.Text,joining.Value,
        year.SelectedId,placement.SelectedId,family.SelectedId,family.Text,registrationContent.RowStyles[0].Height>0,total.Text,savedFamilyMemberId,savedFamilyMemberCount,monthlyConcessionsFromAdmission.Checked);

    private void RestoreEditor(EditorState state)
    {
        savedStudent=state.Student;savedStatus=state.Status;savedSnapshot=state.Snapshot;expectedQuote=state.Quote;
        familyAnchor=state.Anchor;familyReference=state.Reference;savedBasics=state.Basics;feesLoaded=state.FeesLoaded;
        needsRefresh=state.NeedsRefresh;rates=state.Rates;
        savedFamilyMemberId=state.MemberId;savedFamilyMemberCount=state.MemberCount;
        name.Text=state.Name;parent.Text=state.Parent;contact.Text=state.Contact;address.Text=state.Address;
        joining.Value=state.Joining;year.SelectedValue=state.Year;placement.SelectedValue=state.Placement;
        family.SetStudent(state.FamilyId,state.FamilyText);ShowFamilyPicker(state.FamilyShown);fees.DataSource=rates;
        total.Text=state.Total;monthlyConcessionsFromAdmission.Checked=state.ConcessionsFromAdmission;
    }

    private async Task<RegistrationLoadOutcome> LoadRegistrationAsync(int id, bool afterSave=false)
    {
        if(busy&&!afterSave)return RegistrationLoadOutcome.Busy;
        if(id<=0)return RegistrationLoadOutcome.NotFound;
        bool wasBusy=busy;busy=true;
        int version=++registrationLoadVersion;
        ++familyLoadVersion;++loadVersion;loadingFamily=loadingFees=false;
        UpdateActions();
        using var progress=BeginLoading("Refreshing registration…");
        EditorState? previous=null;
        try
        {
            var loaded=await loadRegistration(id);
            if(IsDisposed||version!=registrationLoadVersion)return RegistrationLoadOutcome.Superseded;
            var view=QuickRegistrationView.Read(id,loaded.Details,loaded.Fees,loaded.Snapshot);
            if(view is null)
            {
                if(id==savedStudent)needsRefresh=true;
                SetStatus("The registration was not found. The current form has been retained.",PageStatusKind.Warning);
                return RegistrationLoadOutcome.NotFound;
            }
            if(view.Joining<joining.MinDate||view.Joining>joining.MaxDate)
                throw new InvalidDataException("The registration date cannot be displayed.");
            if(view.Year>0&&(year.DataSource is not DataTable years||!years.AsEnumerable().Any(row=>Convert.ToInt32(row["AcademicYearId"])==view.Year)))
                throw new InvalidDataException("Reload the academic-year list before opening this registration.");
            if(view.Placement>0&&!placements.AsEnumerable().Any(row=>Convert.ToInt32(row["ClassSectionId"])==view.Placement))
                throw new InvalidDataException("Reload the placement list before opening this registration.");
            previous=CaptureEditor();
            applyingRegistration=true;SuspendLayout();
            try
            {
                parent.Text=view.Parent;contact.Text=view.Contact;address.Text=view.Address;name.Text=view.Student;
                joining.Value=view.Joining;year.SelectedValue=view.Year;placement.SelectedValue=view.Placement;
                rates=view.Fees;fees.DataSource=rates;
                savedStudent=view.Id;savedStatus=view.Status;savedSnapshot=view.Snapshot;familyAnchor=view.Id;
                savedFamilyMemberId=view.FamilyMemberId;savedFamilyMemberCount=view.FamilyMemberCount;
                familyReference=view.FamilyReference;savedBasics=BasicValues();feesLoaded=true;needsRefresh=false;
                family.Clear();ShowFamilyPicker(false);
            }
            finally { applyingRegistration=false;ResumeLayout(true); }
            UpdateTotal();
            SetStatus(view.Status=="Pending"?$"{view.Reference} · Pending. Save edits here; review and confirm from Pending Registrations."
                : view.Status=="Active"?$"Admitted · Admission {view.AdmissionNumber} · Registration {view.RegistrationNumber}"
                : $"Registration status: {view.Status}. History is retained.");
            return RegistrationLoadOutcome.Loaded;
        }
        catch(Exception ex)
        {
            if(IsDisposed||version!=registrationLoadVersion)return RegistrationLoadOutcome.Superseded;
            if(previous is not null)
            {
                applyingRegistration=true;SuspendLayout();
                try { RestoreEditor(previous); }
                catch(Exception rollbackError)
                {
                    needsRefresh=true;
                    AppExceptionHandler.LogNonFatal(rollbackError,"Restoring the registration editor after a failed refresh");
                }
                finally { applyingRegistration=false;ResumeLayout(false); }
            }
            if(id==savedStudent)needsRefresh=true;
            AppExceptionHandler.LogNonFatal(ex,"Quick Registration refresh");
            SetStatus("Could not refresh the registration. The current form has been retained. Reopen the record before continuing. "+DbMessage.UserMessage(ex),PageStatusKind.Error);
            return RegistrationLoadOutcome.Failed;
        }
        finally { busy=wasBusy;if(!IsDisposed)UpdateActions(); }
    }

    private async Task CompleteCommittedSaveAsync(int id, string reference, bool isNew)
    {
        if(id<=0)throw new InvalidDataException("The saved registration did not return a valid student ID.");
        savedStudent=id;savedStatus="Pending";savedSnapshot="";needsRefresh=true;
        if(isNew)familyAnchor=familyAnchor>0?familyAnchor:id;
        if(familyAnchor==id)familyReference=reference;
        savedBasics=BasicValues();
        foreach(DataRow rate in rates.Rows)rate["OriginalDiscount"]=rate["Discount"];
        StudentSearchCache.Invalidate();
        var result=await LoadRegistrationAsync(id,afterSave:true);
        if(IsDisposed||result==RegistrationLoadOutcome.Superseded)return;
        if(result==RegistrationLoadOutcome.Loaded)
            SetStatus(isNew?$"Saved {reference}. Print Bill, collect payment or Add Another Child. Confirm later from Pending Registrations."
                : "Changes saved. Review / Confirm from Pending Registrations.",PageStatusKind.Success);
        else
            SetStatus($"{reference} saved. Do not save again. Use Reload Registration before editing, printing or payment.",PageStatusKind.Warning);
    }

    private async Task SavePendingAsync(){
        busy=true;UpdateActions();using var hold=BeginBusy("Saving changes…",name,parent,contact,address,fees);
        try{
            var current=await DatabaseHelper.QueryUserAsync("GetStudentDetails",DatabaseHelper.Int("@AdmissionId",savedStudent));
            if(current.Rows.Count==0||Convert.ToString(current.Rows[0]["Status"])!="Pending"){Warn("This registration is no longer pending. Refresh the list before editing.");return;}
            var row=current.Rows[0];
            // Preserve the current stored parent even if another workspace has
            // corrected it since this registration was opened.
            var args=new List<SqlParameter>{DatabaseHelper.Int("@AdmissionId",savedStudent),DatabaseHelper.P("@StudentName",name.Text.Trim()),DatabaseHelper.P("@FatherName",row["FatherName"]),DatabaseHelper.P("@ContactNumber",contact.Text.Trim()),DatabaseHelper.P("@Address",address.Text.Trim())};
            foreach(string key in new[]{"DateOfBirth","Gender","BFormNumber","Religion","BloodGroup","AlternateContactNumber","PreviousSchool","PlaceOfBirth","FatherOccupation","Disability","BiseEnrolmentNumber","CasteTribe"})args.Add(DatabaseHelper.P("@"+key,row[key]));
            args.Add(new SqlParameter("@Discounts",SqlDbType.Structured){TypeName="dbo.StudentFeeDiscountType",Value=Tvp.StudentFeeDiscounts(rates.AsEnumerable().Where(r=>Convert.ToDecimal(r["Discount"])!=Convert.ToDecimal(r["OriginalDiscount"])).Select(r=>(Convert.ToInt32(r["StudentFeeAssignmentId"]),Convert.ToDecimal(r["Discount"]))))});
            await QuickRegistrationWriter.SavePendingAsync(savedStudent,savedSnapshot,args.ToArray());
            await CompleteCommittedSaveAsync(savedStudent,familyReference,isNew:false);
        }catch(Exception ex){Fail(ex);}finally{busy=false;UpdateActions();}
    }
    internal async Task<bool> OpenRegistrationAsync(int id)
    {
        if(IsBusy||recoveryBlocked)return false;
        if(!fees.EndEdit())return false;
        if(HasUnsavedChanges&&!Confirm("Discard the unsaved registration changes and reload this record?",UiText.Dialogs.UnsavedChanges))return false;
        return await LoadRegistrationAsync(id)==RegistrationLoadOutcome.Loaded;
    }
    private async Task PrintBillAsync()
    {
        if(IsBusy||recoveryBlocked||needsRefresh||savedStudent<=0||!CurrentUser.HasPermission("Fees.View")||!fees.EndEdit()||HasPendingChanges)return;
        busy=true;UpdateActions();
        try{
            int id=savedStudent, version=registrationLoadVersion;
            await FeeBillPreview.ShowAsync(FindForm(),id,isCurrent:()=>!IsDisposed && savedStudent==id && registrationLoadVersion==version && !needsRefresh && !recoveryBlocked && !HasPendingChanges);
        }catch(Exception ex){Fail(ex);}
        finally{busy=false;if(!IsDisposed)UpdateActions();}
    }
}
