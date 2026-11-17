using Microsoft.Data.SqlClient;
using FMSC_SMS.Core;
using static FMSC_SMS.Core.CertificateUi;
namespace FMSC_SMS.Pages.Staff.Certificates;
public sealed class BankAccountOpeningCertificatePage : V2Page, IUnsavedChanges, IBusyPage, IStaffCertificateWorkspaceEditor
{
    private HumanCard workspaceDetails = null!;
    private Action<int>? workspaceSaved;
    private bool inWorkspace;
    HumanCard IStaffCertificateWorkspaceEditor.DetailsCard => workspaceDetails;
    Label IStaffCertificateWorkspaceEditor.Feedback => StatusLabel;
    int IStaffCertificateWorkspaceEditor.SelectedRecordId => selectedBankAccountOpeningCertificateId;
    Action<int>? IStaffCertificateWorkspaceEditor.Saved { set => workspaceSaved = value; }

    void IStaffCertificateWorkspaceEditor.ConfigureWorkspaceDetails(DataRow selectedStaff, bool editing)
    {
        inWorkspace = true;
        StaffCertificateWorkspaceEditorUi.LockStaff(staff, selectedStaff);
        clearButton.Visible = false;
        createCertificateButton.Visible = !editing;
        updateCertificateButton.Visible = editing;
        // Pending saves must remain recoverable from the compact editor,
        // not only from the original page's hidden history action row.
        checkOutcome.Parent?.Controls.Remove(checkOutcome);
        checkOutcome.Dock = DockStyle.Bottom;
        workspaceDetails.Content.Controls.Add(checkOutcome);
        checkOutcome.BringToFront();
        checkOutcome.Visible = recoveryBlocked;
    }

    void IStaffCertificateWorkspaceEditor.LoadWorkspaceSelection(DataRow selectedStaff, int recordId)
    {
        staff.SetStaff(selectedStaff);
        StaffChanged();
        ReadCertificateRecovery();
        if (recoveryBlocked)
        {
            checkOutcome.Visible = true;
            return;
        }
        if (recordId > 0)
        {
            if (CertificateCollectionFlow.Find(certificateResults, "BankAccountOpeningCertificateId", recordId) is null)
                throw new InvalidOperationException("The selected bank certificate is no longer available. Refresh the workspace.");
            SelectCertificateById(recordId);
        }
        else
        {
            ClearCertificateSelection();
            ClearCertificateFields();
            Grid.ClearSelection();
            try { Grid.CurrentCell = null; } catch (InvalidOperationException) { }
            ApplyWorkflowState();
        }
    }

    bool IStaffCertificateWorkspaceEditor.CanPrintFromWorkspace => printButton.Enabled && !IsBusy && !recoveryBlocked;
    bool IStaffCertificateWorkspaceEditor.CanCollectFromWorkspace => copyFlow.Collect.Enabled && !IsBusy && !recoveryBlocked;
    bool IStaffCertificateWorkspaceEditor.CanIssueAgainFromWorkspace => copyFlow.IssueAgain.Enabled && !IsBusy && !recoveryBlocked;
    void IStaffCertificateWorkspaceEditor.PrintFromWorkspace() => printButton.PerformClick();
    void IStaffCertificateWorkspaceEditor.CollectFromWorkspace() => copyFlow.CollectFromWorkspace();
    void IStaffCertificateWorkspaceEditor.IssueAgainFromWorkspace() => copyFlow.IssueAgainFromWorkspace();

    internal override bool FocusEntryField() => CertificateUi.FocusEntry(staff, Grid, certificateDate);
    private readonly StaffPicker staff = new();
    private readonly CertificateCollectionFlow copyFlow = new("Bank");
    public bool IsBusy=>BusyOperationRunning;
    public string BusyMessage=>"The bank certificate is being created. Wait for the result and history reload before leaving.";
    private readonly Func<string,bool>? discardConfirmation;
    private bool recoveryBlocked;
    private MutationIntent? pendingCertificate;
    private readonly ModernButton checkOutcome=new(){Text="Check Save Outcome",Width=AppScale.Scale(170)};
    private string? _baseline;
    private string Snapshot() => string.Join("\u001f", bankName.Text, branchName.Text, designation.Text, monthlyIncome.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), purpose.Text, certificateDate.Value.Date.ToString("yyyy-MM-dd"));
    // Short certificate details can be retyped; unresolved saves still require recovery checks.
    public bool HasUnsavedChanges => recoveryBlocked;
    public string UnsavedChangesMessage => recoveryBlocked?"This certificate creation needs outcome checking. Its recovery record is retained when you leave. Do not create it again before checking.":"This staff certificate has unsaved changes. Leaving will discard them.";
    private bool ConfirmDiscard()=>!HasUnsavedChanges||
        (discardConfirmation?.Invoke(UnsavedChangesMessage)??ModernMessageDialog.ConfirmDiscardIfDirty(FindForm(),()=>HasUnsavedChanges,UnsavedChangesMessage));
    private void CaptureBaseline() => _baseline = Snapshot();

    private readonly ModernTextBox bankName =
        HumanUi.TextBox(false, 100);
    private readonly ModernTextBox branchName =
        HumanUi.TextBox(false, 150);
    private readonly ModernTextBox designation =
        HumanUi.TextBox(false, 100);
    private readonly ModernNumericTextBox monthlyIncome =
        HumanUi.Number(10000000);
    private readonly ModernTextBox purpose =
        HumanUi.TextBox(false, 200);
    private readonly ModernDatePicker certificateDate =
        HumanUi.Date();
    private DataTable certificateResults = new();
    private string? savedEditorSnapshot;
    private string EditorSnapshot() => string.Join("\u001f", bankName.Text, branchName.Text, designation.Text, monthlyIncome.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), purpose.Text, certificateDate.Value.Date.ToString("yyyy-MM-dd"));
    private bool HasCertificateEdits => selectedBankAccountOpeningCertificateId > 0 && savedEditorSnapshot is not null && savedEditorSnapshot != EditorSnapshot();
    private bool suppressStaffChanged;
    private int selectedBankAccountOpeningCertificateId;
    private readonly ModernButton createCertificateButton = new() {
        SavesChanges = true, Text = UiText.Certificates.Create,
        Width = AppScale.Scale(170)
    };
    private readonly ModernButton updateCertificateButton = new() {
        Text = "Save Changes",
        Width = AppScale.Scale(170)
    };
    private readonly ModernButton clearButton = new() {
        Text = UiText.Certificates.New,
        Width = AppScale.Scale(120)
    };
    private readonly ModernButton printButton = new() {
        Text = UiText.Actions.Print,
        Width = AppScale.Scale(120)
    };
    private readonly ModernButton refreshButton = new() {
        Text = UiText.Actions.Refresh,
        Width = AppScale.Scale(120)
    };
    public BankAccountOpeningCertificatePage():this(true){ }
    internal BankAccountOpeningCertificatePage(bool initialize,Func<string,bool>? discardConfirmation=null)
        : base("Bank Account Opening Certificate", "Record and print bank account opening request letters for staff.")
    {
        this.discardConfirmation=discardConfirmation;
        bankName.PlaceholderText =
            "e.g. Meezan Bank, Allied Bank";
        bankName.MaxLength = 100;
        branchName.PlaceholderText =
            "e.g. Ring Road Peshawar";
        branchName.MaxLength = 150;
        designation.PlaceholderText =
            "e.g. Computer Operator, Account Assistant";
        designation.MaxLength = 100;
        purpose.PlaceholderText =
            "e.g. for his/her salary purpose";
        purpose.MaxLength = 200;
        monthlyIncome.WholeRupeesOnly = true;
        monthlyIncome.DecimalPlaces = 0;
        monthlyIncome.ThousandsSeparator = true;
        checkOutcome.Click+=async(_,_)=>await CheckCertificateOutcomeAsync();
        copyFlow.Wire(this,()=>selectedBankAccountOpeningCertificateId,id=>{LoadCertificates(staff.SelectedId);SelectSavedCertificate(id);},()=>HasCertificateEdits);
        createCertificateButton.Click+=async(_,_)=>
        {
            if(IsBusy||!createCertificateButton.Enabled)return;
            await CreateCertificate();
        };
        updateCertificateButton.Click +=
            (_, _) => UpdateCertificate();
        clearButton.Click +=
            (_, _) => ClearForm();
        printButton.Click +=
            (_, _) => PrintSelected();
        /* printing calls a Get*PrintData procedure, which requires Certificates.Print, while this page is reached with Certificates.Create. Without this gate a create-only user was offered a Print button the database would refuse. */
        GateAction(printButton, "Certificates.Print");
        refreshButton.Click +=
            (_, _) => RefreshCertificates();
        RelocateFromActions(createCertificateButton, clearButton);
        staff.SelectionChangeGuard = _ => !IsBusy&&!recoveryBlocked&&ConfirmDiscard();
        staff.SelectionCleared += (_, _) => { if (!suppressStaffChanged) StaffChanged(); };
        staff.SearchFailed += ex => Fail(ex);
        staff.UserSelectionChanged +=
            (_, _) => {
                if (suppressStaffChanged)
                    return;
                StaffChanged();
            };
        Grid.CellMouseClick +=
            Grid_CellMouseClick;
        if(initialize)Defer(
            () => {
                /* The ORDER here was the defect. Resetting first and binding second leaves the list populated with nothing selected, which is what opening this page means. */
                ResetToStart();
                BindStaff();
                ReadCertificateRecovery();
            });
        var details = workspaceDetails =
            HumanUi.Card(
                UiText.Certificates.Details,
                "Select staff; enter bank details and designation to create. Names and CNIC come from the staff record; select a saved row to edit other details.");
        var fields =
            new TableLayoutPanel {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 4,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
        fields.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 50));
        fields.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 50));
        for (int i = 0; i < 4; i++)
        {
            fields.RowStyles.Add(
                new RowStyle(SizeType.Absolute, AppMetrics.FieldRowHeight));
        }
        fields.Controls.Add(
            CertificateUi.Field("Staff", staff, true), 0, 0);
        fields.Controls.Add(
            CertificateUi.Field(UiText.Certificates.Date, certificateDate, true), 1, 0);
        fields.Controls.Add(
            CertificateUi.Field("Bank Name", bankName, true), 0, 1);
        fields.Controls.Add(
            CertificateUi.Field("Branch Name", branchName, true), 1, 1);
        fields.Controls.Add(
            CertificateUi.Field("Designation", designation, true), 0, 2);
        fields.Controls.Add(
            CertificateUi.Field("Monthly Income (optional)", monthlyIncome, false), 1, 2);
        fields.Controls.Add(
            CertificateUi.Field("Purpose", purpose, true), 0, 3);
        details.Content.Controls.Add(fields);
        var certButtonRow =
            CertificateButtonRow.Create(createCertificateButton, updateCertificateButton, clearButton);
        certButtonRow.Dock =
            DockStyle.Bottom;
        details.Content.Controls.Add(certButtonRow);
        var records =
            HumanUi.Card(
                "Bank Account Opening Certificates",
                "A staff member may have more than one of these. Select a row to edit or print it.");
        var certificateActions =
            CertificateButtonRow.Create(printButton, copyFlow.Collect, copyFlow.IssueAgain, refreshButton, checkOutcome);
        certificateActions.Dock =
            DockStyle.Bottom;
        records.Content.Controls.Add(certificateActions);
        records.Content.Controls.Add(Grid);
        CertificateUi.WireNavigation(this, staff, Grid, clearButton, fields, certButtonRow, certificateActions,
            index => Grid_CellMouseClick(Grid, new DataGridViewCellMouseEventArgs(0,index,0,0,new MouseEventArgs(MouseButtons.Left,1,0,0,0))));
        Grid.BringToFront();
        var root =
            new TableLayoutPanel {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
        root.RowStyles.Add(
            new RowStyle(SizeType.Absolute, AppMetrics.FieldCardHeight(4)
                + AppMetrics.ButtonRowHeight));
        root.RowStyles.Add(
            new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(details, 0, 0);
        root.Controls.Add(records, 0, 1);
        SetContent(
            root,
            UiText.Certificates.StaffGuidance);
        bankName.TextChanged += (_, _) => ApplyWorkflowState();
        branchName.TextChanged += (_, _) => ApplyWorkflowState();
        designation.TextChanged += (_, _) => ApplyWorkflowState();
        purpose.TextChanged += (_, _) => ApplyWorkflowState();
        certificateDate.ValueChanged += (_, _) => ApplyWorkflowState();
        monthlyIncome.ValueChanged += (_, _) => ApplyWorkflowState();
        if(initialize)ReadCertificateRecovery();else ApplyWorkflowState();
    }
    private void BindStaff()
    {
        try
        {
            suppressStaffChanged = true;
            DataTable dt =
                DatabaseHelper.QueryUser(
                    "GetStaff",
                    DatabaseHelper.Int(
                        "@UserId",
                        CurrentUser.UserId));
            /* @StaffId is a filter whose default (NULL) means "everyone"; the earlier explicit 0 matched no staff row, so this picker came back empty. */
            staff.SetSource(dt);
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
        finally
        {
            suppressStaffChanged = false;
        }
    }
    private void StaffChanged()
    {
        if (suppressStaffChanged)
            return;
        try
        {
            ClearCertificateSelection();
            ClearCertificateFields();
            int staffId =
                staff.SelectedId;
            if (staffId <= 0)
            {
                certificateResults =
                    new DataTable();
                Bind(certificateResults);
                FormatCertificateGrid();
                ApplyWorkflowState();
                return;
            }
            LoadCertificates(staffId);
            CertificateUi.FocusHistory(Grid, () => SelectSavedCertificate(0), certificateDate);
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }
    private bool LoadCertificates(int staffId,bool reportError=true)
    {
        try
        {
            ClearCertificateSelection();
            certificateResults =
                new DataTable();
            if (staffId <= 0)
            {
                Bind(certificateResults);
                FormatCertificateGrid();
                ApplyWorkflowState();
                return true;
            }
            DataTable table =
                DatabaseHelper.QueryUser(
                    "GetBankAccountOpeningCertificateRecords",
                    DatabaseHelper.Int(
                        "@UserId",
                        CurrentUser.UserId),
                    DatabaseHelper.Int("@StaffId", staffId));
            certificateResults = CertificateRecordOrder.NewestFirst(table, "BankAccountOpeningCertificateId");
            Bind(certificateResults);
            FormatCertificateGrid();
            Grid.ClearSelection();
            try
            {
                Grid.CurrentCell = null;
            }
            catch (Exception nonFatalException43)
            {
                FMSC_SMS.Core.AppExceptionHandler.LogNonFatal(nonFatalException43, "BankAccountOpeningCertificatePage: LoadCertificates fallback");
            }
            ApplyWorkflowState();
            return true;
        }
        catch (Exception ex)
        {
            if(reportError)Fail(ex);else AppExceptionHandler.LogNonFatal(ex,"Bank certificate history after confirmed save");
            /* Finding 11 - the load failed, so the grid must not keep showing the previous staff member's certificates, and the buttons must be recomputed for the empty list. */
            certificateResults =
                new DataTable();
            Bind(certificateResults);
            FormatCertificateGrid();
            ApplyWorkflowState();
            return false;
        }
    }
    private void SelectSavedCertificate(int id)
    {
        CaptureBaseline();
        SelectCertificateById(id);
    }
    private void FormatCertificateGrid()
    {
        CertificateRecordOrder.ArrangeColumns(Grid, "BankAccountOpeningCertificateId");
        /* The click handlers below read the row from certificateResults by the clicked row INDEX, which only matches the grid while it is in the table's own order. */
        LockGridOrder();
        SetGridWidth("BankAccountOpeningCertificateId", 110);
        if (Grid.Columns.Contains("BankAccountOpeningCertificateId"))
        {
            Grid.Columns["BankAccountOpeningCertificateId"]!.HeaderText =
                "Certificate No.";
        }
        SetGridWidth("StaffId", 70);
        SetGridWidth("StaffName", 170);
        SetGridWidth("FatherName", 150);
        SetGridWidth("Designation", 140);
        SetGridWidth("BankName", 130);
        SetGridWidth("BranchName", 150);
        SetGridWidth("MonthlyIncome", 110);
        SetGridWidth("Purpose", 170);
        SetGridWidth("CertificateDate", 120);
        SetGridWidth("CreatedAt", 145);
        SetGridWidth("UpdatedAt", 145);
        HideGridColumn("StaffId");
        CertificateCollectionFlow.FormatGrid(Grid);
        CertificateRecordOrder.FitColumns(Grid);
    }
    private void HideGridColumn(string columnName)
    {
        if (!Grid.Columns.Contains(columnName))
        {
            return;
        }
        Grid.Columns[columnName].Visible =
            false;
    }
    private void Grid_CellMouseClick(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (IsBusy||recoveryBlocked||e.Button != MouseButtons.Left ||
            e.RowIndex < 0 ||
            e.RowIndex >= certificateResults.Rows.Count)
        {
            return;
        }
        if (!ConfirmDiscard()) return;
        try
        {
            DataGridViewRow gridRow =
                Grid.Rows[e.RowIndex];
            if (gridRow.IsNewRow)
                return;
            Grid.ClearSelection();
            gridRow.Selected = true;
            /* The field population below moved into ApplyCertificateRow so that Refresh can put the same record back into the editor after reloading the list. */
            if (!ApplyCertificateRow(
                    certificateResults.Rows[
                        e.RowIndex]))
            {
                ClearCertificateSelection();
                SetStatus("The selected certificate contains invalid data.", true);
            }
        }
        catch (Exception ex)
        {
            ClearCertificateSelection();
            SetStatus($"Unable to read the selected certificate: {DbMessage.UserMessage(ex)}", true);
        }
    }
    /// <summary>Loads one certificate row into the editor and sets the selection to it.</summary>
    private bool ApplyCertificateRow(DataRow row)
    {
        int bankAccountOpeningCertificateId =
            GetInt(row, "BankAccountOpeningCertificateId");
        if (bankAccountOpeningCertificateId <= 0)
            return false;
        selectedBankAccountOpeningCertificateId =
            bankAccountOpeningCertificateId;
        bankName.Text =
            GetString(row, "BankName");
        branchName.Text =
            GetString(row, "BranchName");
        designation.Text =
            GetString(row, "Designation");
        purpose.Text =
            GetString(row, "Purpose");
        monthlyIncome.Value =
            GetDecimal(row, "MonthlyIncome");
        DateTime? certDate =
            GetDate(row, "CertificateDate");
        certificateDate.Value =
            certDate ?? DateTime.Today;
        savedEditorSnapshot = EditorSnapshot();
        ApplyWorkflowState();
        CaptureBaseline();
        return true;
    }
    private void ApplyWorkflowState()
    {
        bool hasStaff =
            staff.SelectedId > 0;
        bool hasSelection =
            selectedBankAccountOpeningCertificateId > 0;
        staff.Enabled = !recoveryBlocked;
        Grid.Enabled = !recoveryBlocked;
        checkOutcome.Enabled=recoveryBlocked&&!IsBusy;
        if (inWorkspace) checkOutcome.Visible = recoveryBlocked;
        bankName.Enabled = hasStaff&&!recoveryBlocked;
        branchName.Enabled = hasStaff&&!recoveryBlocked;
        designation.Enabled = hasStaff&&!recoveryBlocked;
        monthlyIncome.Enabled = hasStaff&&!recoveryBlocked;
        purpose.Enabled = hasStaff&&!recoveryBlocked;
        certificateDate.Enabled = hasStaff&&!recoveryBlocked;
        createCertificateButton.Enabled =
            !recoveryBlocked&&hasStaff &&
            !hasSelection;
        updateCertificateButton.Enabled =
            !recoveryBlocked&&hasStaff &&
            hasSelection && HasCertificateEdits;
        clearButton.Enabled = !recoveryBlocked;
        refreshButton.Enabled =
            hasStaff&&!recoveryBlocked;
        printButton.Enabled =
            hasSelection && !HasCertificateEdits && CurrentUser.HasPermission("Certificates.Print");
        copyFlow.Available=!IsBusy&&!recoveryBlocked;
        if(copyFlow.Apply(certificateResults,"BankAccountOpeningCertificateId",selectedBankAccountOpeningCertificateId,updateCertificateButton,printButton,HasCertificateEdits,bankName,branchName,designation,monthlyIncome,purpose,certificateDate))
        {SetStatus(copyFlow.Status);return;}
        if(recoveryBlocked)
        {
            SetStatus((pendingCertificate?.Summary??"An earlier certificate creation")+" · Outcome needs checking. Use Check Save Outcome; do not create it again.",PageStatusKind.Warning);
            return;
        }
        if (!hasStaff)
        {
            SetStatus("Select a staff member.");
            return;
        }
        if (hasSelection)
        {
            SetStatus(
                "Editing certificate. Use Update Certificate to save, or New to clear the form.");
            return;
        }
        SetStatus(
            "Enter bank, branch, designation and purpose to create. Select a saved certificate to edit or print.");
    }
    private async Task CreateCertificate()
    {
        if(recoveryBlocked){Warn("Check the earlier save outcome before creating another certificate.");return;}
        try{await RunBusyAsync(BusyMessage,CreateCertificateCoreAsync,this);}
        finally{if(!IsDisposed)checkOutcome.Enabled=recoveryBlocked;}
    }
    private async Task CreateCertificateCoreAsync()
    {
        if (!ValidateRequiredInputs())
            return;
        /* Finding 12 - Info writes the status line, but the reload below calls Bind ("N record(s)") and ApplyWorkflowState after it. */
        string? successMessage =
            null;
        bool historyRefreshed=true;
        int confirmedRecord = 0;
        try
        {
            if (staff.SelectedId <= 0)
            {
                Warn("Select a staff member first.");
                return;
            }
            if (!ValidateCertificateFields())
                return;
            int staffId =
                staff.SelectedId;
            createCertificateButton.Enabled = false;
            var saved=await SubmitCertificateAsync(
                $"Bank letter · Staff {staffId} · {bankName.Text.Trim()} · {certificateDate.Value:dd MMM yyyy}",
                DatabaseHelper.Int("@StaffId",staffId),
                DatabaseHelper.P("@Designation",designation.Text.Trim()),
                DatabaseHelper.P("@BankName",bankName.Text.Trim()),
                DatabaseHelper.P("@BranchName",branchName.Text.Trim()),
                DatabaseHelper.P("@MonthlyIncome",monthlyIncome.Value),
                DatabaseHelper.P("@Purpose",purpose.Text.Trim()),
                DatabaseHelper.Date("@CertificateDate",certificateDate.Value.Date));
            if(saved is null)return;
            successMessage =
                $"Certificate {saved.RecordId} created successfully.";
            confirmedRecord = saved.RecordId;
            /* (D-H2) - reload only while the staff member this certificate was created for is still the selected one, or the list would be shown under a different member's name and… */
            if (IsDisposed ||
                staff.SelectedId != staffId)
            {
                return;
            }
            ClearCertificateFields();
            ClearCertificateSelection();
            historyRefreshed=LoadCertificates(staffId,reportError:false)&&certificateResults.Rows.Count>0;
            historyRefreshed=historyRefreshed&&certificateResults.AsEnumerable().Any(r=>GetInt(r,"BankAccountOpeningCertificateId")==saved.RecordId);
            if(historyRefreshed)SelectSavedCertificate(saved.RecordId);
        }
        catch (Exception ex)
        {
            ReadCertificateRecovery();
            if(recoveryBlocked)Warn("The certificate may already be created. Use Check Save Outcome before creating another. "+DbMessage.UserMessage(ex));else Fail(ex);
        }
        finally
        {
            if(!IsDisposed)ApplyWorkflowState();
            if (successMessage is not null)
            {
                if(historyRefreshed) { Info(successMessage); if (confirmedRecord > 0) workspaceSaved?.Invoke(confirmedRecord); }
                else Warn(successMessage+" History could not be refreshed or verified. Reload before acting further; do not repeat the save because this read failed.");
            }
        }
    }
    private void ReadCertificateRecovery()
    {
        bool wasBlocked=recoveryBlocked;
        try
        {
            pendingCertificate??=MutationRecoveryJournal.ReadPending(MutationKind.BankCertificateCreate);
            recoveryBlocked=pendingCertificate is not null;
            if(wasBlocked&&!recoveryBlocked){ClearCertificateSelection();ClearCertificateFields();Grid.ClearSelection();Grid.CurrentCell=null;}
        }
        catch(Exception ex)
        {
            recoveryBlocked=true;ApplyWorkflowState();
            Warn("Retain the certificate recovery record for review. Do not create the certificate again. "+DbMessage.UserMessage(ex));return;
        }
        ApplyWorkflowState();
    }
    private async Task<MutationResult?> SubmitCertificateAsync(string summary,params SqlParameter[] args)
    {
        int user=CurrentUser.UserId;string scope=await RecoverableMutationWriter.GetScopeAsync();
        if(IsDisposed||CurrentUser.UserId!=user)throw new InvalidOperationException("The certificate page or signed-in operator changed before submission.");
        MutationWriteResult outcome;MutationIntent intent;
        using(var journal=MutationRecoveryJournal.Begin(MutationKind.BankCertificateCreate,user,scope,RecoverableMutationWriter.PayloadHash(MutationKind.BankCertificateCreate,user,args),summary))
        {
            intent=journal.Intent;pendingCertificate=intent;recoveryBlocked=true;ApplyWorkflowState();
            outcome=await RecoverableMutationWriter.SaveAsync(intent,args);
        }
        if(outcome.Outcome==MutationOutcome.Unconfirmed){Warn("The certificate creation outcome is unknown. Use Check Save Outcome; do not create it again.");return null;}
        MutationRecoveryJournal.ClearVerified(intent);pendingCertificate=null;recoveryBlocked=false;
        if(outcome.Outcome==MutationOutcome.Rejected)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(outcome.Error!).Throw();
        LookupDataCache.OperationCompleted("CreateBankAccountOpeningCertificate");
        return outcome.Result!;
    }
    private async Task CheckCertificateOutcomeAsync()
    {
        if(IsBusy||IsDisposed)return;ReadCertificateRecovery();if(pendingCertificate is null)return;
        try
        {
            await RunBusyAsync("Checking the earlier certificate creation outcome…",async()=>
            {
                MutationResult? verified=null;bool checkedOutcome=false;
                try
                {
                    var intent=pendingCertificate!;var saved=await RecoverableMutationWriter.CheckAsync(intent);verified=saved;checkedOutcome=true;
                    MutationRecoveryJournal.ClearVerified(intent);pendingCertificate=null;recoveryBlocked=false;
                    if(IsDisposed)return;
                    ClearCertificateSelection();ClearCertificateFields();Grid.ClearSelection();Grid.CurrentCell=null;
                    if(saved is null){ApplyWorkflowState();Warn("The earlier certificate was not created. Reload staff history and deliberately create it only if still needed.");return;}
                    LookupDataCache.OperationCompleted("CreateBankAccountOpeningCertificate");
                    suppressStaffChanged=true;
                    try{if(staff.SelectedId!=saved.SubjectId)staff.SetStaff(saved.SubjectId,"Recovered staff member");}
                    finally{suppressStaffChanged=false;}
                    bool loaded=LoadCertificates(saved.SubjectId,reportError:false)&&certificateResults.AsEnumerable().Any(r=>GetInt(r,"BankAccountOpeningCertificateId")==saved.RecordId);
                    if(loaded){var row=certificateResults.AsEnumerable().First(r=>GetInt(r,"BankAccountOpeningCertificateId")==saved.RecordId);staff.SetStaff(saved.SubjectId,GetString(row,"StaffName"));SelectSavedCertificate(saved.RecordId);Info($"Earlier certificate {saved.RecordId} was created. The exact saved certificate is selected.");}
                    else Warn($"Earlier certificate {saved.RecordId} was created. History could not be refreshed or verified. Reload history; do not create it again.");
                }
                catch(Exception ex)
                {
                    if(!IsDisposed)Warn((checkedOutcome?(verified is null?"The earlier certificate was not created, but recovery cleanup failed. Reopen this page before creating it. ":"The earlier certificate is recorded, but the screen or recovery file could not be refreshed. Do not create it again. Reopen this page or reload history. "):"The certificate outcome is still unresolved. Keep recovery and do not create it again. ")+DbMessage.UserMessage(ex));
                }
            },this);
        }
        finally{if(!IsDisposed)checkOutcome.Enabled=recoveryBlocked;}
    }
    private void UpdateCertificate()
    {
        if(IsBusy||recoveryBlocked)return;
        int savedCertificateId = selectedBankAccountOpeningCertificateId;
        // Finding 12 - see CreateCertificate: the confirmation is set last.
        string? successMessage =
            null;
        bool historyRefreshed=true;
        try
        {
            if (selectedBankAccountOpeningCertificateId <= 0)
            {
                Warn("Select a certificate from the list first.");
                return;
            }
            if (!ValidateCertificateFields())
                return;
            int staffId =
                staff.SelectedId;
            updateCertificateButton.Enabled = false;
            DatabaseHelper.Execute(
                "UpdateBankAccountOpeningCertificate",
                DatabaseHelper.P(
                    "@UserId",
                    CurrentUser.UserId),
                DatabaseHelper.Int(
                    "@BankAccountOpeningCertificateId",
                    selectedBankAccountOpeningCertificateId),
                DatabaseHelper.P(
                    "@Designation",
                    designation.Text.Trim()),
                DatabaseHelper.P(
                    "@BankName",
                    bankName.Text.Trim()),
                DatabaseHelper.P(
                    "@BranchName",
                    branchName.Text.Trim()),
                DatabaseHelper.P(
                    "@MonthlyIncome",
                    monthlyIncome.Value),
                DatabaseHelper.P(
                    "@Purpose",
                    purpose.Text.Trim()),
                DatabaseHelper.Date("@CertificateDate", certificateDate.Value.Date),CertificateCollectionFlow.VersionParameter(certificateResults,"BankAccountOpeningCertificateId",selectedBankAccountOpeningCertificateId));
            successMessage =
                "Certificate updated successfully.";
            ClearCertificateFields();
            ClearCertificateSelection();
            historyRefreshed=LoadCertificates(staffId,reportError:false)&&certificateResults.Rows.Count>0;
            if(historyRefreshed)SelectSavedCertificate(savedCertificateId);
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
        finally
        {
            ApplyWorkflowState();
            if (successMessage is not null)
            {
                if(historyRefreshed) { Info(successMessage); workspaceSaved?.Invoke(savedCertificateId); }
                else Warn(successMessage+" History could not be refreshed or verified. Reload before acting further; do not repeat the save because this read failed.");
            }
        }
    }
    private bool ValidateCertificateFields()
    {
        if (string.IsNullOrWhiteSpace(bankName.Text))
        {
            Warn("Enter the bank name.");
            return false;
        }
        if (string.IsNullOrWhiteSpace(branchName.Text))
        {
            Warn("Enter the branch name.");
            return false;
        }
        if (string.IsNullOrWhiteSpace(designation.Text))
        {
            Warn("Enter the staff member's designation.");
            return false;
        }
        if (string.IsNullOrWhiteSpace(purpose.Text))
        {
            Warn("Enter the purpose of opening the account.");
            return false;
        }
        if (certificateDate.Value.Date > DateTime.Today)
        {
            Warn(UiText.Certificates.FutureDateRefused);
            return false;
        }
        return true;
    }
    private void PrintSelected()
    {
        if (!printButton.Enabled) return;
        try
        {
            if (selectedBankAccountOpeningCertificateId <= 0)
            {
                Warn("Select a certificate from the list first.");
                return;
            }
            PrintCertificate(selectedBankAccountOpeningCertificateId);
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }
    private void PrintCertificate(int bankAccountOpeningCertificateId)
    {
        DataTable data =
            DatabaseHelper.QueryUser(
                "GetBankAccountOpeningCertificatePrintData",
                DatabaseHelper.P(
                    "@UserId",
                    CurrentUser.UserId),
                DatabaseHelper.Int("@BankAccountOpeningCertificateId", bankAccountOpeningCertificateId));
        if (data.Rows.Count == 0)
        {
            Warn(UiText.Certificates.PrintDataMissing);
            return;
        }
        string reportPath =
            Path.Combine(AppContext.BaseDirectory, "Reports", "BankAccountOpeningCertificate.rdlc");
        if (!File.Exists(reportPath))
        {
            throw new FileNotFoundException("Bank Account Opening Certificate RDLC report was not found.", reportPath);
        }
        ShowReport(FindForm(), "Bank Account Opening Certificate", reportPath, "BankAccountOpeningCertificateData", data);
    }
    private void RefreshCertificates()
    {
        if(IsBusy||recoveryBlocked)return;
        if (!ConfirmDiscard()) return;
        int staffId =
            staff.SelectedId;
        if (staffId <= 0)
            return;
        /* Refresh reloads the list, and LoadCertificates always clears the row selection, but nothing cleared the editor. */
        int previouslySelected =
            selectedBankAccountOpeningCertificateId;
        LoadCertificates(staffId);
        if (previouslySelected <= 0)
        {
            ClearCertificateFields();
            ApplyWorkflowState();
            return;
        }
        SelectCertificateById(previouslySelected);
    }
    /// <summary>re-selects a certificate by id after the list is reloaded, so the editor and the workflow state still describe the row the operator was working on.</summary>
    private void SelectCertificateById(int certificateId)
    {
        for (int index = 0;
             index < certificateResults.Rows.Count;
             index++)
        {
            DataRow row =
                certificateResults.Rows[index];
            if (GetInt(row, "BankAccountOpeningCertificateId") != certificateId)
            {
                continue;
            }
            if (index < Grid.Rows.Count)
            {
                Grid.ClearSelection();
                Grid.Rows[index].Selected = true;
            }
            ApplyCertificateRow(row);
            return;
        }
        ClearCertificateSelection();
        ClearCertificateFields();
        ApplyWorkflowState();
    }
    private void ClearCertificateFields()
    {
        bankName.Clear();
        branchName.Clear();
        designation.Text = staff.SelectedRow is DataRow employment
            ? GetString(employment, "Designation") : string.Empty;
        monthlyIncome.Value = 0;
        purpose.Text =
            "for his/her salary purpose";
        certificateDate.Value =
            DateTime.Today;
        CaptureBaseline();
    }
    private void ClearCertificateSelection()
    {
        selectedBankAccountOpeningCertificateId = 0;
    }
    private void ResetToStart()
    {
        suppressStaffChanged = true;
        try
        {
            staff.Clear();
        }
        finally
        {
            suppressStaffChanged = false;
        }
        ClearCertificateFields();
        ClearCertificateSelection();
        certificateResults =
            new DataTable();
        Bind(certificateResults);
        FormatCertificateGrid();
        ApplyWorkflowState();
    }
    private void ClearForm()
    {
        if(IsBusy||recoveryBlocked)return;
        if (!ConfirmDiscard()) return;
        suppressStaffChanged = true;
        try
        {
            staff.Clear();
            certificateResults =
                new DataTable();
            ClearCertificateFields();
            ClearCertificateSelection();
            Bind(certificateResults);
            FormatCertificateGrid();
        }
        finally
        {
            suppressStaffChanged = false;
        }
        ApplyWorkflowState();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Grid.CellMouseClick -=
                Grid_CellMouseClick;
        }
        base.Dispose(disposing);
    }
}
