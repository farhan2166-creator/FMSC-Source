using FMSC_SMS.Core;
using Microsoft.Data.SqlClient;
using static FMSC_SMS.Core.CertificateUi;
namespace FMSC_SMS.Pages.Students.Certificates;
public sealed class NoObjectionCertificatePage : V2Page, IUnsavedChanges, IBusyPage, IStudentCertificateWorkspaceEditor
{
    internal override bool FocusEntryField() => CertificateUi.FocusEntry(student, Grid, certificateDate);
    // Short certificate details can be retyped without a discard warning.
    public bool HasUnsavedChanges => false;
    public string UnsavedChangesMessage => "Discard the unsaved certificate changes?";
    public bool IsBusy => creatingCertificate;
    public string BusyMessage => "Please wait for the certificate to finish saving.";
    private bool creatingCertificate;
    private readonly Func<SqlParameter[], Task<int>> writeCertificate;
    private readonly Func<int, DataTable> readCertificates;
    private HumanCard workspaceDetails = null!;
    private Action<DataRowView?>? workspaceSaved;
    Action<DataRowView?>? IStudentCertificateWorkspaceEditor.Saved { set => workspaceSaved = value; }
    HumanCard IStudentCertificateWorkspaceEditor.DetailsCard => workspaceDetails;
    Label IStudentCertificateWorkspaceEditor.Feedback => StatusLabel;
    void IStudentCertificateWorkspaceEditor.ConfigureWorkspaceDetails(DataRow selectedStudent, bool editing)
    {
        CertificateWorkspaceDetailsDialog.LockStudent(student, selectedStudent);
        createCertificateButton.Visible = !editing;
        updateCertificateButton.Visible = editing;
        clearButton.Visible = false;
        printButton.Visible = false;
        copyFlow.Collect.Visible = false;
        copyFlow.IssueAgain.Visible = false;
    }
    private readonly StudentPicker student = new();
    private readonly CertificateCollectionFlow copyFlow = new("NOC");
    private readonly ModernDatePicker certificateDate =
        HumanUi.Date();
    private DataTable studentResults => student.Source;
    private DataTable certificateResults = new();
    private string? savedEditorSnapshot;
    private string EditorSnapshot() => string.Join("\u001f", certificateDate.Value.Date.ToString("yyyy-MM-dd"));
    private bool HasCertificateEdits => selectedNoObjectionCertificateId > 0 && savedEditorSnapshot is not null && savedEditorSnapshot != EditorSnapshot();
    /* Finding 13 - a search debounce tick that lands while a modal (print preview, confirmation) is open runs inside that modal's message loop and re-binds the picker underneath it. */
    private bool suppressStudentChanged;
    private int selectedNoObjectionCertificateId;
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
    public NoObjectionCertificatePage() : this(true) { }
    internal NoObjectionCertificatePage(bool initialize, Func<SqlParameter[], Task<int>>? writeCertificate = null,
        Func<int, DataTable>? readCertificates = null)
        : base("No Objection Certificate", "Record and print No Objection Certificates (NOC) for students.")
    {
        this.writeCertificate = writeCertificate ?? (args => ReadSavedCertificateIdAsync("CreateNoObjectionCertificate", "NoObjectionCertificateId", args));
        this.readCertificates = readCertificates ?? (id => DatabaseHelper.QueryUser("GetNoObjectionCertificateRecords", DatabaseHelper.Int("@AdmissionId", id)));
        /* dbo.CreateNoObjectionCertificate has no duplicate guard, and the certificate table has no unique constraint on (AdmissionId, ...). */
        createCertificateButton.Click += async (_, _) => await RunCreateCertificateAsync();
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
        copyFlow.Wire(this,()=>selectedNoObjectionCertificateId,id=>{LoadCertificates(student.SelectedId);SelectSavedCertificate(id);},()=>HasCertificateEdits);
        TrackStudentSearchStatus(student);
        /* The shared student finder (StudentPicker): the list narrows on every keystroke and nothing is chosen until the operator clicks a row, presses Enter on a highlighted row, or types an… */
        if (initialize) StudentSearchCache.Warm();
        student.SelectionChangeGuard = _ => !creatingCertificate;
        student.UserSelectionChanged +=
            (_, _) => {
                if (suppressStudentChanged)
                    return;
                StudentChanged();
            };
        student.SelectionCleared +=
            (_, _) => {
                if (suppressStudentChanged)
                    return;
                StudentChanged();
            };
        Grid.CellMouseClick +=
            Grid_CellMouseClick;
        if (initialize) Defer(
            () => {
                ResetToStart();
            });
        var details = workspaceDetails =
            HumanUi.Card(
                UiText.Certificates.Details,
                "Select student and date to create the NOC. Names come from the student record; select a saved row to edit its date.");
        var fields =
            new TableLayoutPanel {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Tag=CertificateUi.FieldGridTag,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
        fields.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 75));
        fields.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 25));
        for (int i = 0; i < 1; i++)
        {
            fields.RowStyles.Add(
                new RowStyle(SizeType.Absolute, AppMetrics.FieldRowHeight));
        }
        fields.Controls.Add(
            CertificateUi.Field("Student", student, true), 0, 0);
        fields.Controls.Add(
            CertificateUi.Field(UiText.Certificates.Date, certificateDate, true), 1, 0);
        details.Content.Controls.Add(fields);
        var certButtonRow =
            CertificateButtonRow.Create(createCertificateButton, updateCertificateButton, clearButton);
        certButtonRow.Dock =
            DockStyle.Bottom;
        details.Content.Controls.Add(certButtonRow);
        var records =
            HumanUi.Card("No Objection Certificates", "A student may have more than one NOC. Select a row to edit or print it.");
        var certificateActions =
            CertificateButtonRow.Create(printButton, copyFlow.Collect, copyFlow.IssueAgain, refreshButton);
        certificateActions.Dock =
            DockStyle.Bottom;
        records.Content.Controls.Add(certificateActions);
        records.Content.Controls.Add(Grid);
        CertificateUi.WireNavigation(this, student, Grid, clearButton, fields, certButtonRow, certificateActions,
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
            new RowStyle(SizeType.Absolute, AppMetrics.FieldCardHeight(1)
                + AppMetrics.ButtonRowHeight));
        root.RowStyles.Add(
            new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(details, 0, 0);
        root.Controls.Add(records, 0, 1);
        SetContent(
            root,
            UiText.Certificates.StudentGuidance);
        certificateDate.ValueChanged += (_, _) => ApplyWorkflowState();
        ApplyWorkflowState();
    }
    private void StudentChanged()
    {
        if (suppressStudentChanged)
            return;
        try
        {
            ClearCertificateSelection();
            ClearCertificateFields();
            int admissionId =
                student.SelectedId;
            if (admissionId <= 0)
            {
                certificateResults =
                    new DataTable();
                Bind(certificateResults);
                FormatCertificateGrid();
                ApplyWorkflowState();
                return;
            }
            LoadCertificates(admissionId);
            CertificateUi.FocusHistory(Grid, () => SelectSavedCertificate(0), certificateDate);
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }
    private void LoadCertificates(int admissionId)
    {
        try
        {
            ClearCertificateSelection();
            certificateResults =
                new DataTable();
            if (admissionId <= 0)
            {
                Bind(certificateResults);
                FormatCertificateGrid();
                ApplyWorkflowState();
                return;
            }
            DataTable table = readCertificates(admissionId);
            certificateResults = CertificateRecordOrder.NewestFirst(table, "NoObjectionCertificateId");
            Bind(certificateResults);
            FormatCertificateGrid();
            Grid.ClearSelection();
            try
            {
                Grid.CurrentCell = null;
            }
            catch (Exception nonFatalException63)
            {
                FMSC_SMS.Core.AppExceptionHandler.LogNonFatal(nonFatalException63, "NoObjectionCertificatePage: LoadCertificates fallback");
            }
            ApplyWorkflowState();
        }
        catch (Exception ex)
        {
            Fail(ex);
            /* Finding 11 - the load failed, so the grid must not keep showing the previous student's certificates, and the buttons must be recomputed for the empty list. */
            certificateResults =
                new DataTable();
            Bind(certificateResults);
            FormatCertificateGrid();
            ApplyWorkflowState();
        }
    }
    private void SelectSavedCertificate(int id)
    {

        CertificateRecordOrder.Select(Grid, "NoObjectionCertificateId", id,
            index => Grid_CellMouseClick(Grid, new DataGridViewCellMouseEventArgs(0,index,0,0,new MouseEventArgs(MouseButtons.Left,1,0,0,0))));
    }
    private void FormatCertificateGrid()
    {
        CertificateRecordOrder.ArrangeColumns(Grid, "NoObjectionCertificateId");
        /* The click handlers below read the row from certificateResults by the clicked row INDEX, which only matches the grid while it is in the table's own order. */
        LockGridOrder();
        SetGridWidth("NoObjectionCertificateId", 110);
        if (Grid.Columns.Contains("NoObjectionCertificateId"))
        {
            Grid.Columns["NoObjectionCertificateId"]!.HeaderText =
                "Certificate No.";
        }
        SetGridWidth("AdmissionNumber", 80);
        SetGridWidth("StudentName", 170);
        SetGridWidth("FatherName", 150);
        SetGridWidth("CertificateDate", 120);
        SetGridWidth("CreatedAt", 145);
        SetGridWidth("UpdatedAt", 145);
        HideGridColumn("AdmissionId");
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
        if (e.Button != MouseButtons.Left ||
            e.RowIndex < 0 ||
            e.RowIndex >= certificateResults.Rows.Count)
        {
            return;
        }
        try
        {
            DataGridViewRow gridRow =
                Grid.Rows[e.RowIndex];
            if (gridRow.IsNewRow)
                return;
            Grid.ClearSelection();
            gridRow.Selected = true;
            DataRow row =
                certificateResults.Rows[
                    e.RowIndex];
            int noObjectionCertificateId =
                GetInt(row, "NoObjectionCertificateId");
            if (noObjectionCertificateId <= 0)
            {
                ClearCertificateSelection();
                SetStatus("The selected NOC contains invalid data.", true);
                return;
            }
            if (!ConfirmCertificateSelection(FindForm(), Grid, "NoObjectionCertificateId",
                selectedNoObjectionCertificateId, noObjectionCertificateId, false)) return;
            selectedNoObjectionCertificateId =
                noObjectionCertificateId;
            DateTime? certDate =
                GetDate(row, "CertificateDate");
            certificateDate.Value =
                certDate ?? DateTime.Today;
            savedEditorSnapshot = EditorSnapshot();
            ApplyWorkflowState();
        }
        catch (Exception ex)
        {
            ClearCertificateSelection();
            SetStatus($"Unable to read the selected NOC: {DbMessage.UserMessage(ex)}", true);
        }
    }
    private void ApplyWorkflowState() => ApplyWorkflowStateAndStatus(true);
    private void ApplyWorkflowStateAndStatus(bool showStatus)
    {
        student.Enabled = Grid.Enabled = !creatingCertificate;
        if (creatingCertificate)
        {
            createCertificateButton.Enabled = updateCertificateButton.Enabled =
                clearButton.Enabled = refreshButton.Enabled = printButton.Enabled = false;
            return;
        }
        bool hasStudent =
            student.SelectedId > 0;
        bool hasSelection =
            selectedNoObjectionCertificateId > 0;
        student.Enabled = true;
        certificateDate.Enabled = hasStudent;
        createCertificateButton.Enabled =
            hasStudent &&
            !hasSelection;
        updateCertificateButton.Enabled =
            hasStudent &&
            hasSelection && HasCertificateEdits;
        clearButton.Enabled = true;
        refreshButton.Enabled =
            hasStudent;
        printButton.Enabled =
            hasSelection && !HasCertificateEdits && CurrentUser.HasPermission("Certificates.Print");
        copyFlow.Available=!creatingCertificate;
        if(copyFlow.Apply(certificateResults,"NoObjectionCertificateId",selectedNoObjectionCertificateId,updateCertificateButton,printButton,HasCertificateEdits,certificateDate))
        { SetStatus(copyFlow.Status); return; }
        if (!showStatus) return;
        if (!hasStudent)
        {
            SetStatus(UiText.Prompts.SearchForStudent);
            return;
        }
        if (hasSelection)
        {
            SetStatus(
                "Editing NOC. Use Save Changes to save, or New to clear the form.");
            return;
        }
        SetStatus("Confirm the date to create the NOC. Select a saved NOC to edit or print.");
    }
    private async Task RunCreateCertificateAsync()
    {
        if (creatingCertificate || IsDisposed || !createCertificateButton.Enabled) return;
        creatingCertificate = true;
        ApplyWorkflowState();
        try
        {
            await CreateCertificate();
        }
        finally
        {
            creatingCertificate = false;
            if (!IsDisposed) ApplyWorkflowStateAndStatus(false);
        }
    }
    private async Task CreateCertificate()
    {
        if (!ValidateRequiredInputs())
            return;
        /* Finding 12 - Info writes the status line, but the reload below calls Bind ("N record(s)") and ApplyWorkflowState after it. */
        string? successMessage =
            null;
        try
        {
            if (student.SelectedId <= 0)
            {
                Warn(UiText.Prompts.SelectStudent);
                return;
            }
            if (!ValidateCertificateFields())
                return;
            int admissionId =
                student.SelectedId;
            DateTime certDate =
                certificateDate.Value.Date;
            createCertificateButton.Enabled = false;
            // awaited so the caller's disabled button covers the whole write. @UserId is passed explicitly and equals CurrentUser.UserId, so ExecuteUserAsync's automatic injection supplies the same value.
            using var hold = BeginBusy(BusyMessage, this);
            int savedId = await writeCertificate(new[] {
                DatabaseHelper.P(
                    "@UserId",
                    CurrentUser.UserId),
                DatabaseHelper.Int(
                    "@AdmissionId",
                    admissionId),
                DatabaseHelper.Date("@CertificateDate", certDate) });

            /* refresh only when the student this certificate was created for is still the selected one. The success message above stays: the write did happen. */
            if (IsDisposed || student.SelectedId != admissionId)
                return;
            ClearCertificateFields();
            ClearCertificateSelection();
            LoadCertificates(admissionId);
            CertificateRecordOrder.RequireSavedCopy(certificateResults, "NoObjectionCertificateId", savedId);
            SelectSavedCertificate(savedId);
            successMessage =
                "NOC created successfully.";
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
                Info(successMessage);
                workspaceSaved?.Invoke(Grid.CurrentRow?.DataBoundItem as DataRowView);
            }
        }
    }
    private void UpdateCertificate()
    {
        if (creatingCertificate) return;
        int savedCertificateId = selectedNoObjectionCertificateId;
        // Finding 12 - see CreateCertificate: the confirmation is set last.
        string? successMessage =
            null;
        try
        {
            if (selectedNoObjectionCertificateId <= 0)
            {
                Warn("Select an NOC from the list first.");
                return;
            }
            if (!ValidateCertificateFields())
                return;
            int admissionId =
                student.SelectedId;
            DateTime certDate =
                certificateDate.Value.Date;
            updateCertificateButton.Enabled = false;
            DatabaseHelper.Execute(
                "UpdateNoObjectionCertificate",
                DatabaseHelper.P(
                    "@UserId",
                    CurrentUser.UserId),
                DatabaseHelper.Int(
                    "@NoObjectionCertificateId",
                    selectedNoObjectionCertificateId),
                DatabaseHelper.Date("@CertificateDate", certDate),CertificateCollectionFlow.VersionParameter(certificateResults,"NoObjectionCertificateId",selectedNoObjectionCertificateId));
            successMessage =
                "NOC updated successfully.";
            /* same rule as the create path: the student this certificate was updated for must still be the selected one, or the reload would show the previous student's certificates under the… */
            if (IsDisposed || student.SelectedId != admissionId)
                return;
            ClearCertificateFields();
            ClearCertificateSelection();
            LoadCertificates(admissionId);
            SelectSavedCertificate(savedCertificateId);
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
                Info(successMessage);
                workspaceSaved?.Invoke(Grid.CurrentRow?.DataBoundItem as DataRowView);
            }
        }
    }
    private bool ValidateCertificateFields()
    {
        if (certificateDate.Value.Date > DateTime.Today)
        {
            Warn(UiText.Certificates.FutureDateRefused);
            return false;
        }
        return true;
    }
    private void PrintSelected()
    {
        if (creatingCertificate) return;
        if (!printButton.Enabled) return;
        // Finding 13 - the preview is modal; park the search while it is open.
        try
        {
            if (selectedNoObjectionCertificateId <= 0)
            {
                Warn("Select an NOC from the list first.");
                return;
            }
            PrintCertificate(selectedNoObjectionCertificateId);
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
        finally
        {
        }
    }
    private void PrintCertificate(int noObjectionCertificateId)
    {
        DataTable data =
            DatabaseHelper.QueryUser(
                "GetNoObjectionCertificatePrintData",
                DatabaseHelper.Int("@NoObjectionCertificateId", noObjectionCertificateId));
        if (data.Rows.Count == 0)
        {
            Warn(UiText.Certificates.PrintDataMissing);
            return;
        }
        string reportPath =
            Path.Combine(AppContext.BaseDirectory, "Reports", "NoObjectionCertificate.rdlc");
        if (!File.Exists(reportPath))
        {
            throw new FileNotFoundException("No Objection Certificate RDLC report was not found.", reportPath);
        }
        ShowReport(FindForm(), "No Objection Certificate", reportPath, "NoObjectionCertificateData", data);
    }
    private void RefreshCertificates()
    {
        if (creatingCertificate) return;
        if (student.SelectedId <= 0) return;
        int selectedId = selectedNoObjectionCertificateId;
        LoadCertificates(student.SelectedId);
        if (selectedId > 0) SelectSavedCertificate(selectedId);
    }
    private void ClearCertificateFields()
    {
        certificateDate.Value =
            DateTime.Today;
    }
    private void ClearCertificateSelection()
    {
        selectedNoObjectionCertificateId = 0;
    }
    private void ResetToStart()
    {
        suppressStudentChanged = true;
        try
        {
            student.Clear();
        }
        finally
        {
            suppressStudentChanged = false;
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
        if (creatingCertificate) return;
        suppressStudentChanged = true;
        try
        {
            student.Clear();
            certificateResults =
                new DataTable();
            ClearCertificateFields();
            ClearCertificateSelection();
            Bind(certificateResults);
            FormatCertificateGrid();
        }
        finally
        {
            suppressStudentChanged = false;
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

    /// <summary>Opt-in workspace bridge: use the same selection and saved-copy logic as this editor.</summary>
    void IStudentCertificateWorkspaceEditor.LoadWorkspaceSelection(DataRow selectedStudent, int recordId, int issueId)
    {
        if (IsDisposed || selectedStudent.Table is null ||
            !selectedStudent.Table.Columns.Contains("AdmissionId") ||
            CertificateUi.GetInt(selectedStudent, "AdmissionId") <= 0)
            throw new ArgumentException("A valid selected student is required.", nameof(selectedStudent));
        student.SetStudent(selectedStudent);
        StudentChanged();
        if (recordId > 0)
        {
            if (!certificateResults.AsEnumerable().Any(row =>
                CertificateUi.GetInt(row, "NoObjectionCertificateId") == recordId))
                throw new InvalidOperationException("The selected saved certificate is no longer available. Refresh the workspace.");
            SelectSavedCertificate(recordId);
        }
        else
        {
            // New preparation stays on the selected student; the ordinary New/Clear
            // action on the old form still keeps its original behavior.
            ClearCertificateSelection(); ClearCertificateFields(); ApplyWorkflowState();
        }
    }


    // Workspace action surface: same legacy handlers, same validation/permissions,
    // no duplicate certificate persistence, rendering or reissue implementation.
    bool IStudentCertificateWorkspaceEditor.CanPrintFromWorkspace => printButton.Enabled;
    bool IStudentCertificateWorkspaceEditor.CanCollectFromWorkspace => copyFlow.Collect.Enabled;
    bool IStudentCertificateWorkspaceEditor.CanIssueAgainFromWorkspace => copyFlow.IssueAgain.Enabled;

    void IStudentCertificateWorkspaceEditor.PrintFromWorkspace()
    {
        if (!printButton.Enabled) throw new InvalidOperationException("This saved copy cannot be printed in its current state.");
        PrintSelected();
    }

    void IStudentCertificateWorkspaceEditor.CollectFromWorkspace()
    {
        if (!copyFlow.Collect.Enabled) throw new InvalidOperationException("This saved copy is not currently eligible for collection.");
        copyFlow.CollectFromWorkspace();
    }

    Task IStudentCertificateWorkspaceEditor.IssueAgainFromWorkspaceAsync()
    {
        if (!copyFlow.IssueAgain.Enabled) throw new InvalidOperationException("Only the eligible latest collected copy may be issued again.");
        copyFlow.IssueAgainFromWorkspace(); return Task.CompletedTask;
    }

}
