using FMSC_SMS.Core;
using Microsoft.Data.SqlClient;
using static FMSC_SMS.Core.CertificateUi;
namespace FMSC_SMS.Pages.Staff.Certificates;
public sealed class RegularLecturerCertificatePage : V2Page, IUnsavedChanges
{
    internal override bool FocusEntryField() => CertificateUi.FocusEntry(staff, Grid, certificateDate);
    private readonly StaffPicker staff = new();
    private readonly Func<SqlParameter[], DataTable> createCertificate;
    private readonly Func<int, DataTable> readCertificates;
    private readonly CertificateCollectionFlow copyFlow = new("Lecturer");
    private string? _baseline;
    private string Snapshot() => string.Join("\u001f", roleTitle.Text, subjectTaught.Text, certificateDate.Value.Date.ToString("yyyy-MM-dd"));
    // Short certificate details can be retyped without a discard warning.
    public bool HasUnsavedChanges => false;
    public string UnsavedChangesMessage => "This staff certificate has unsaved changes. Leaving will discard them.";
    private bool ConfirmDiscard() => ModernMessageDialog.ConfirmDiscardIfDirty(FindForm(), () => HasUnsavedChanges, UnsavedChangesMessage);
    private void CaptureBaseline() => _baseline = Snapshot();

    private readonly ModernTextBox roleTitle =
        HumanUi.TextBox(false, 100);
    private readonly ModernTextBox subjectTaught =
        HumanUi.TextBox(false, 150);
    private readonly ModernDatePicker certificateDate =
        HumanUi.Date();
    private DataTable certificateResults = new();
    private string? savedEditorSnapshot;
    private string EditorSnapshot() => string.Join("\u001f", roleTitle.Text, subjectTaught.Text, certificateDate.Value.Date.ToString("yyyy-MM-dd"));
    private bool HasCertificateEdits => selectedRegularLecturerCertificateId > 0 && savedEditorSnapshot is not null && savedEditorSnapshot != EditorSnapshot();
    private bool suppressStaffChanged;
    private int selectedRegularLecturerCertificateId;
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
    public RegularLecturerCertificatePage() : this(true) { }
    internal RegularLecturerCertificatePage(bool initialize, Func<SqlParameter[], DataTable>? createCertificate = null,
        Func<int, DataTable>? readCertificates = null)
        : base("Regular Lecturer/Teacher Certificate", "Record and print Regular Lecturer/Teacher certificates for staff.")
    {
        this.createCertificate = createCertificate ?? (args => DatabaseHelper.QueryUser("CreateRegularLecturerCertificate", args));
        this.readCertificates = readCertificates ?? (id => DatabaseHelper.QueryUser("GetRegularLecturerCertificateRecords", DatabaseHelper.Int("@StaffId", id)));
        roleTitle.PlaceholderText =
            "e.g. Teacher, Lecturer";
        roleTitle.MaxLength = 30;
        subjectTaught.PlaceholderText =
            "From staff specialization";
        subjectTaught.MaxLength = 100;
        UiAccessibility.Describe(subjectTaught, "Subject taught",
            "Filled from the selected staff member's Specialization. Review the wording before creating the certificate.");
        copyFlow.Wire(this,()=>selectedRegularLecturerCertificateId,id=>{LoadCertificates(staff.SelectedId);SelectSavedCertificate(id);},()=>HasCertificateEdits);
        createCertificateButton.Click +=
            (_, _) => CreateCertificate();
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
        staff.SelectionChangeGuard = _ => ConfirmDiscard();
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
        if (initialize) Defer(
            () => {
                /* The ORDER here was the defect. Resetting first and binding second leaves the list populated with nothing selected, which is what opening this page means. */
                ResetToStart();
                BindStaff();
            });
        var details =
            HumanUi.Card(
                UiText.Certificates.Details,
                "Select staff; confirm designation and subject to create. Names come from the staff record; select a saved row to edit other details.");
        var fields =
            new TableLayoutPanel {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 2,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
        fields.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 50));
        fields.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 50));
        for (int i = 0; i < 2; i++)
        {
            fields.RowStyles.Add(
                new RowStyle(SizeType.Absolute, AppMetrics.FieldRowHeight));
        }
        fields.Controls.Add(
            CertificateUi.Field("Staff", staff, true), 0, 0);
        fields.Controls.Add(
            CertificateUi.Field(UiText.Certificates.Date, certificateDate, true), 1, 0);
        fields.Controls.Add(
            CertificateUi.Field("Designation (Teacher/Lecturer)", roleTitle, true), 0, 1);
        fields.Controls.Add(
            CertificateUi.Field("Subject Taught", subjectTaught, true), 1, 1);
        details.Content.Controls.Add(fields);
        var certButtonRow =
            CertificateButtonRow.Create(createCertificateButton, updateCertificateButton, clearButton);
        certButtonRow.Dock =
            DockStyle.Bottom;
        details.Content.Controls.Add(certButtonRow);
        var records =
            HumanUi.Card(
                "Regular Lecturer/Teacher Certificates",
                "A staff member may have more than one of these. Select a row to edit or print it.");
        var certificateActions =
            CertificateButtonRow.Create(printButton, copyFlow.Collect, copyFlow.IssueAgain, refreshButton);
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
            new RowStyle(SizeType.Absolute, AppMetrics.FieldCardHeight(2)
                + AppMetrics.ButtonRowHeight));
        root.RowStyles.Add(
            new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(details, 0, 0);
        root.Controls.Add(records, 0, 1);
        SetContent(
            root,
            UiText.Certificates.StaffGuidance);
        roleTitle.TextChanged += (_, _) => ApplyWorkflowState();
        subjectTaught.TextChanged += (_, _) => ApplyWorkflowState();
        certificateDate.ValueChanged += (_, _) => ApplyWorkflowState();
        ApplyWorkflowState();
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
            ClearCertificateFields();
            ApplyWorkflowState();
            CertificateUi.FocusHistory(Grid, () => SelectSavedCertificate(0), certificateDate);
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }
    private void LoadCertificates(int staffId)
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
                return;
            }
            DataTable table =
                readCertificates(staffId);
            certificateResults = CertificateRecordOrder.NewestFirst(table, "RegularLecturerCertificateId");
            Bind(certificateResults);
            FormatCertificateGrid();
            Grid.ClearSelection();
            try
            {
                Grid.CurrentCell = null;
            }
            catch (Exception nonFatalException45)
            {
                FMSC_SMS.Core.AppExceptionHandler.LogNonFatal(nonFatalException45, "RegularLecturerCertificatePage: LoadCertificates fallback");
            }
            ApplyWorkflowState();
        }
        catch (Exception ex)
        {
            Fail(ex);
            /* Finding 11 - the load failed, so the grid must not keep showing the previous staff member's certificates, and the buttons must be recomputed for the empty list. */
            certificateResults =
                new DataTable();
            Bind(certificateResults);
            FormatCertificateGrid();
            ApplyWorkflowState();
        }
    }
    private void SelectSavedCertificate(int id)
    {
        CaptureBaseline();
        CertificateRecordOrder.Select(Grid, "RegularLecturerCertificateId", id,
            index => Grid_CellMouseClick(Grid, new DataGridViewCellMouseEventArgs(0,index,0,0,new MouseEventArgs(MouseButtons.Left,1,0,0,0))));
    }
    private void FormatCertificateGrid()
    {
        CertificateRecordOrder.ArrangeColumns(Grid, "RegularLecturerCertificateId");
        /* The click handlers below read the row from certificateResults by the clicked row INDEX, which only matches the grid while it is in the table's own order. */
        LockGridOrder();
        SetGridWidth("RegularLecturerCertificateId", 110);
        if (Grid.Columns.Contains("RegularLecturerCertificateId"))
        {
            Grid.Columns["RegularLecturerCertificateId"]!.HeaderText =
                "Certificate No.";
        }
        SetGridWidth("StaffId", 70);
        SetGridWidth("StaffName", 170);
        SetGridWidth("FatherName", 150);
        SetGridWidth("RoleTitle", 110);
        SetGridWidth("SubjectTaught", 130);
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
        if (e.Button != MouseButtons.Left ||
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
        int regularLecturerCertificateId =
            GetInt(row, "RegularLecturerCertificateId");
        if (regularLecturerCertificateId <= 0)
            return false;
        selectedRegularLecturerCertificateId =
            regularLecturerCertificateId;
        roleTitle.Text =
            GetString(row, "RoleTitle");
        subjectTaught.Text =
            GetString(row, "SubjectTaught");
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
            selectedRegularLecturerCertificateId > 0;
        staff.Enabled = true;
        roleTitle.Enabled = hasStaff;
        subjectTaught.Enabled = hasStaff;
        certificateDate.Enabled = hasStaff;
        createCertificateButton.Enabled =
            hasStaff &&
            !hasSelection;
        updateCertificateButton.Enabled =
            hasStaff &&
            hasSelection && HasCertificateEdits;
        clearButton.Enabled = true;
        refreshButton.Enabled =
            hasStaff;
        printButton.Enabled =
            hasSelection && !HasCertificateEdits && CurrentUser.HasPermission("Certificates.Print");
        copyFlow.Available=true;
        if(copyFlow.Apply(certificateResults,"RegularLecturerCertificateId",selectedRegularLecturerCertificateId,updateCertificateButton,printButton,HasCertificateEdits,roleTitle,subjectTaught,certificateDate))
        { SetStatus(copyFlow.Status); return; }
        if (!hasStaff)
        {
            SetStatus("Select a staff member.");
            return;
        }
        if (hasSelection)
        {
            SetStatus(
                "Editing certificate. Use Save Changes to save, or New to clear the form.");
            return;
        }
        SetStatus(
            "Confirm designation, subject and date to create. Select a saved certificate to edit or print.");
    }
    private void CreateCertificate()
    {
        if (!ValidateRequiredInputs())
            return;
        /* Finding 12 - Info writes the status line, but the reload below calls Bind ("N record(s)") and ApplyWorkflowState after it. */
        string? successMessage =
            null;
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
            using var saved = createCertificate(new[] {
                DatabaseHelper.P(
                    "@UserId",
                    CurrentUser.UserId),
                DatabaseHelper.Int(
                    "@StaffId",
                    staffId),
                DatabaseHelper.P(
                    "@RoleTitle",
                    roleTitle.Text.Trim()),
                DatabaseHelper.P(
                    "@SubjectTaught",
                    subjectTaught.Text.Trim()),
                DatabaseHelper.Date("@CertificateDate", certificateDate.Value.Date) });
            int savedId = ReadSavedCertificateId(saved, "RegularLecturerCertificateId");
            ClearCertificateFields();
            ClearCertificateSelection();
            LoadCertificates(staffId);
            CertificateRecordOrder.RequireSavedCopy(certificateResults, "RegularLecturerCertificateId", savedId);
            SelectSavedCertificate(savedId);
            successMessage = "Certificate created successfully.";
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
            }
        }
    }
    private void UpdateCertificate()
    {
        int savedCertificateId = selectedRegularLecturerCertificateId;
        // Finding 12 - see CreateCertificate: the confirmation is set last.
        string? successMessage =
            null;
        try
        {
            if (selectedRegularLecturerCertificateId <= 0)
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
                "UpdateRegularLecturerCertificate",
                DatabaseHelper.P(
                    "@UserId",
                    CurrentUser.UserId),
                DatabaseHelper.Int(
                    "@RegularLecturerCertificateId",
                    selectedRegularLecturerCertificateId),
                DatabaseHelper.P(
                    "@RoleTitle",
                    roleTitle.Text.Trim()),
                DatabaseHelper.P(
                    "@SubjectTaught",
                    subjectTaught.Text.Trim()),
                DatabaseHelper.Date("@CertificateDate", certificateDate.Value.Date),CertificateCollectionFlow.VersionParameter(certificateResults,"RegularLecturerCertificateId",selectedRegularLecturerCertificateId));
            successMessage =
                "Certificate updated successfully.";
            ClearCertificateFields();
            ClearCertificateSelection();
            LoadCertificates(staffId);
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
            }
        }
    }
    private bool ValidateCertificateFields()
    {
        if (string.IsNullOrWhiteSpace(roleTitle.Text))
        {
            Warn("Enter the staff member's designation (Teacher/Lecturer).");
            return false;
        }
        if (string.IsNullOrWhiteSpace(subjectTaught.Text))
        {
            Warn("Enter the subject taught.");
            return false;
        }
        if (subjectTaught.Text.Trim().Length > subjectTaught.MaxLength)
        {
            Warn("Keep the subject wording within 100 characters before saving the certificate.");
            subjectTaught.Focus();
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
            if (selectedRegularLecturerCertificateId <= 0)
            {
                Warn("Select a certificate from the list first.");
                return;
            }
            PrintCertificate(selectedRegularLecturerCertificateId);
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }
    private void PrintCertificate(int regularLecturerCertificateId)
    {
        DataTable data =
            DatabaseHelper.QueryUser(
                "GetRegularLecturerCertificatePrintData",
                DatabaseHelper.P(
                    "@UserId",
                    CurrentUser.UserId),
                DatabaseHelper.Int("@RegularLecturerCertificateId", regularLecturerCertificateId));
        if (data.Rows.Count == 0)
        {
            Warn(UiText.Certificates.PrintDataMissing);
            return;
        }
        string reportPath =
            Path.Combine(AppContext.BaseDirectory, "Reports", "RegularLecturerCertificate.rdlc");
        if (!File.Exists(reportPath))
        {
            throw new FileNotFoundException("Regular Lecturer/Teacher Certificate RDLC report was not found.", reportPath);
        }
        ShowReport(FindForm(), "Regular Lecturer/Teacher Certificate", reportPath, "RegularLecturerCertificateData", data);
    }
    private void RefreshCertificates()
    {
        if (!ConfirmDiscard()) return;
        int staffId =
            staff.SelectedId;
        if (staffId <= 0)
            return;
        /* Refresh reloads the list, and LoadCertificates always clears the row selection, but nothing cleared the editor. */
        int previouslySelected =
            selectedRegularLecturerCertificateId;
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
            if (GetInt(row, "RegularLecturerCertificateId") != certificateId)
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
        roleTitle.Clear();
        subjectTaught.Clear();
        certificateDate.Value =
            DateTime.Today;
        ApplyStaffDefaults();
        CaptureBaseline();
    }
    private void ApplyStaffDefaults()
    {
        int staffId = staff.SelectedId;
        if (staffId <= 0) return;
        DataRow? employment = staff.SelectedRow;
        // The result list is newest first. Never reuse a previous staff member's certificate.
        var saved = certificateResults.AsEnumerable()
            .Where(row => GetInt(row, "StaffId") == staffId).ToArray();
        string designation = employment is null ? "" : GetString(employment, "Designation");
        roleTitle.Text = string.IsNullOrWhiteSpace(designation)
            ? saved.Select(row => GetString(row, "RoleTitle")).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? ""
            : designation;
        string specialization = employment is null ? "" : GetString(employment, "Specialization");
        subjectTaught.Text = string.IsNullOrWhiteSpace(specialization)
            ? saved.Select(row => GetString(row, "SubjectTaught")).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? ""
            : specialization;
    }
    private void ClearCertificateSelection()
    {
        selectedRegularLecturerCertificateId = 0;
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
