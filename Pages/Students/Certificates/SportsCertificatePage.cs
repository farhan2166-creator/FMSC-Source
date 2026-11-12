using FMSC_SMS.Core;
using Microsoft.Data.SqlClient;
using static FMSC_SMS.Core.CertificateUi;
namespace FMSC_SMS.Pages.Students.Certificates;
public sealed class SportsCertificatePage : V2Page, IUnsavedChanges, IBusyPage, IStudentCertificateWorkspaceEditor
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
    private readonly CertificateCollectionFlow copyFlow = new("Sports");
    private readonly ModernTextBox sportName =
        HumanUi.TextBox(false, 200);
    private readonly ModernTextBox performance =
        HumanUi.TextBox(false, 1000);
    private readonly ModernComboBox performanceSuggestions = HumanUi.Combo();
    private static readonly (string Label,string Sentence)[] PerformanceSentences = {
        ("Sportsmanship and discipline", "Demonstrated good sportsmanship, discipline and respect for fellow players."),
        ("Active participation", "Participated actively in school sports with enthusiasm and dedication."),
        ("Teamwork", "Displayed commendable teamwork and contributed positively to team performance."),
        ("Representing the institution", "Represented the institution in sports competitions with commitment and good conduct."),
        ("Leadership and fair play", "Demonstrated leadership, encouraged fellow players and upheld the spirit of fair play."),
        ("Effort and achievement", "Achieved a commendable standard of performance through regular practice and consistent effort.")
    };
    private readonly ModernDatePicker certificateDate =
        HumanUi.Date();
    private DataTable studentResults => student.Source;
    private DataTable certificateResults = new();
    private string? savedEditorSnapshot;
    private string EditorSnapshot() => string.Join("\u001f", sportName.Text, performance.Text, certificateDate.Value.Date.ToString("yyyy-MM-dd"));
    // Owner policy (UFA-04): a new date-only draft is abandonable; saved-record date edits count.
    private bool HasCertificateEdits => student.SelectedId > 0 &&
        (selectedSportsCertificateId > 0
            ? savedEditorSnapshot is not null && savedEditorSnapshot != EditorSnapshot()
            : !string.IsNullOrWhiteSpace(sportName.Text) || !string.IsNullOrWhiteSpace(performance.Text));
    /* Suspend search debounce while a modal dialog is open. */
    private bool suppressStudentChanged;
    private int selectedSportsCertificateId;
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
    public SportsCertificatePage() : this(true) { }
    internal SportsCertificatePage(bool initialize, Func<SqlParameter[], Task<int>>? writeCertificate = null,
        Func<int, DataTable>? readCertificates = null)
        : base("Sports Certificate", "Record and print sports certificates for students.")
    {
        this.writeCertificate = writeCertificate ?? (args => ReadSavedCertificateIdAsync("CreateSportsCertificate", "SportsCertificateId", args));
        this.readCertificates = readCertificates ?? (id => DatabaseHelper.QueryUser("GetSportsCertificateRecords", DatabaseHelper.Int("@AdmissionId", id)));
        sportName.MaxLength = 200;
        performance.MaxLength = 1000;
        ConfigureFieldSuggestions();
        performanceSuggestions.DropDownStyle=ComboBoxStyle.DropDownList;
        performanceSuggestions.Items.Add("Choose a sentence, then edit the Performance field if needed");
        foreach(var suggestion in PerformanceSentences)performanceSuggestions.Items.Add(suggestion.Label);
        performanceSuggestions.SelectedIndex=0;
        performanceSuggestions.SelectedIndexChanged+=(_,_)=>{
            int index=performanceSuggestions.SelectedIndex-1;
            if(index<0||creatingCertificate||!performance.Enabled||!performanceSuggestions.Enabled)return;
            performance.Text=PerformanceSentences[index].Sentence;
        };
        performance.EnabledChanged+=(_,_)=>performanceSuggestions.Enabled=performance.Enabled;
        /* dbo.CreateSportsCertificate has no duplicate guard, and the certificate table has no unique constraint on (AdmissionId, ...). */
        createCertificateButton.Click += async (_, _) => await RunCreateCertificateAsync();
        updateCertificateButton.Click +=
            (_, _) => UpdateCertificate();
        clearButton.Click +=
            (_, _) => ClearForm();
        printButton.Click +=
            (_, _) => PrintSelected();
        /* Gate printing separately with Certificates.Print; creation uses Certificates.Create. */
        GateAction(printButton, "Certificates.Print");
        refreshButton.Click +=
            (_, _) => RefreshCertificates();
        RelocateFromActions(createCertificateButton, clearButton);
        copyFlow.Wire(this,()=>selectedSportsCertificateId,id=>{LoadCertificates(student.SelectedId);SelectSavedCertificate(id);},()=>HasCertificateEdits);
        TrackStudentSearchStatus(student);
        /* Filter as the operator types; load only after a committed student selection. */
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
        if (initialize) Defer(LoadFieldSuggestions);
        var details = workspaceDetails =
            HumanUi.Card(
                UiText.Certificates.Details,
                "Select student; enter sport and performance to create. Select a saved row to edit any field.");
        var fields =
            new TableLayoutPanel {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 3,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
        fields.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 50));
        fields.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 50));
        for (int i = 0; i < 3; i++)
        {
            fields.RowStyles.Add(
                new RowStyle(SizeType.Absolute, AppMetrics.FieldRowHeight));
        }
        fields.Controls.Add(
            CertificateUi.Field("Student", student, true), 0, 0);
        fields.Controls.Add(
            CertificateUi.Field(UiText.Certificates.Date, certificateDate, true), 1, 0);
        fields.Controls.Add(
            CertificateUi.Field("Sport Name", sportName, true), 0, 1);
        fields.Controls.Add(
            CertificateUi.Field("Performance", performance, true), 1, 1);
        var suggestedField=CertificateUi.Field("Suggested Performance Sentence (optional)",performanceSuggestions);
        fields.Controls.Add(suggestedField,0,2);fields.SetColumnSpan(suggestedField,2);
        details.Content.Controls.Add(fields);
        var certButtonRow =
            CertificateButtonRow.Create(createCertificateButton, updateCertificateButton, clearButton);
        certButtonRow.Dock =
            DockStyle.Bottom;
        details.Content.Controls.Add(certButtonRow);
        var records =
            HumanUi.Card("Sports Certificates", "A student may have more than one sports certificate. Select a row to edit or print it.");
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
            new RowStyle(SizeType.Absolute, AppMetrics.FieldCardHeight(3)
                + AppMetrics.ButtonRowHeight));
        root.RowStyles.Add(
            new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(details, 0, 0);
        root.Controls.Add(records, 0, 1);
        CertificateUi.CompactFields(details,fields,root,new[]{40f,20f,20f,20f},new[]{(0,0,1),(1,0,1),(2,0,1),(3,0,1),(0,1,4)},1200);
        SetContent(
            root,
            UiText.Certificates.StudentGuidance);
        sportName.TextChanged += (_, _) => ApplyWorkflowState();
        performance.TextChanged += (_, _) => ApplyWorkflowState();
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
            certificateResults = CertificateRecordOrder.NewestFirst(table, "SportsCertificateId");
            Bind(certificateResults);
            FormatCertificateGrid();
            Grid.ClearSelection();
            try
            {
                Grid.CurrentCell = null;
            }
            catch (Exception nonFatalException69)
            {
                FMSC_SMS.Core.AppExceptionHandler.LogNonFatal(nonFatalException69, "SportsCertificatePage: LoadCertificates fallback");
            }
            ApplyWorkflowState();
        }
        catch (Exception ex)
        {
            Fail(ex);
            /* Clear stale certificates and recompute actions after a failed load. */
            certificateResults =
                new DataTable();
            Bind(certificateResults);
            FormatCertificateGrid();
            ApplyWorkflowState();
        }
    }
    private void SelectSavedCertificate(int id)
    {

        CertificateRecordOrder.Select(Grid, "SportsCertificateId", id,
            index => Grid_CellMouseClick(Grid, new DataGridViewCellMouseEventArgs(0,index,0,0,new MouseEventArgs(MouseButtons.Left,1,0,0,0))));
    }
    private void FormatCertificateGrid()
    {
        CertificateRecordOrder.ArrangeColumns(Grid, "SportsCertificateId");
        /* Keep grid order aligned with certificateResults for index-based selection. */
        LockGridOrder();
        SetGridWidth("SportsCertificateId", 110);
        if (Grid.Columns.Contains("SportsCertificateId"))
        {
            Grid.Columns["SportsCertificateId"]!.HeaderText =
                "Certificate No.";
        }
        SetGridWidth("AdmissionNumber", 80);
        SetGridWidth("StudentName", 150);
        SetGridWidth("FatherName", 130);
        SetGridWidth("SportName", 140);
        SetGridWidth("Performance", 220);
        SetGridWidth("CertificateDate", 110);
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
            int sportsCertificateId =
                GetInt(row, "SportsCertificateId");
            if (sportsCertificateId <= 0)
            {
                ClearCertificateSelection();
                SetStatus("The selected sports certificate contains invalid data.", true);
                return;
            }
            if (!ConfirmCertificateSelection(FindForm(), Grid, "SportsCertificateId",
                selectedSportsCertificateId, sportsCertificateId, false)) return;
            selectedSportsCertificateId =
                sportsCertificateId;
            performanceSuggestions.SelectedIndex=0;
            sportName.Text =
                GetString(row, "SportName");
            performance.Text =
                GetString(row, "Performance");
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
            SetStatus($"Unable to read the selected sports certificate: {DbMessage.UserMessage(ex)}", true);
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
            selectedSportsCertificateId > 0;
        student.Enabled = true;
        certificateDate.Enabled = hasStudent;
        sportName.Enabled = hasStudent;
        performance.Enabled = hasStudent;
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
        if(copyFlow.Apply(certificateResults,"SportsCertificateId",selectedSportsCertificateId,updateCertificateButton,printButton,HasCertificateEdits,certificateDate,sportName,performance,performanceSuggestions))
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
                "Editing sports certificate. Use Save Changes to save, or New to clear the form.");
            return;
        }
        SetStatus("Enter sport and performance to create. Select a saved certificate to edit or print.");
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
            // Await the full write while actions are held; pass the current actor.
            using var hold = BeginBusy(BusyMessage, this);
            int savedId = await writeCertificate(new[] {
                DatabaseHelper.P(
                    "@UserId",
                    CurrentUser.UserId),
                DatabaseHelper.Int(
                    "@AdmissionId",
                    admissionId),
                DatabaseHelper.Date(
                    "@CertificateDate",
                    certDate),
                DatabaseHelper.P(
                    "@SportName",
                    DatabaseHelper.OptionalText(
                        sportName.Text)),
                DatabaseHelper.P(
                    "@Performance",
                    DatabaseHelper.OptionalText(performance.Text)) });

            AddSuggestionIfNew(sportName, sportName.Text);
            AddSuggestionIfNew(performance, performance.Text);
            /* refresh only when the student this certificate was created for is still the selected one. */
            if (IsDisposed || student.SelectedId != admissionId)
                return;
            ClearCertificateFields();
            ClearCertificateSelection();
            LoadCertificates(admissionId);
            CertificateRecordOrder.RequireSavedCopy(certificateResults, "SportsCertificateId", savedId);
            SelectSavedCertificate(savedId);
            successMessage =
                "Sports certificate created successfully.";
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
        int savedCertificateId = selectedSportsCertificateId;
        // Finding 12 - see CreateCertificate: the confirmation is set last.
        string? successMessage =
            null;
        try
        {
            if (selectedSportsCertificateId <= 0)
            {
                Warn("Select a sports certificate from the list first.");
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
                "UpdateSportsCertificate",
                DatabaseHelper.P(
                    "@UserId",
                    CurrentUser.UserId),
                DatabaseHelper.Int(
                    "@SportsCertificateId",
                    selectedSportsCertificateId),
                DatabaseHelper.Date(
                    "@CertificateDate",
                    certDate),
                DatabaseHelper.P(
                    "@SportName",
                    DatabaseHelper.OptionalText(
                        sportName.Text)),
                DatabaseHelper.P(
                    "@Performance",
                    DatabaseHelper.OptionalText(performance.Text)),CertificateCollectionFlow.VersionParameter(certificateResults,"SportsCertificateId",selectedSportsCertificateId));
            successMessage =
                "Sports certificate updated successfully.";
            AddSuggestionIfNew(sportName, sportName.Text);
            AddSuggestionIfNew(performance, performance.Text);
            /* Refresh only if the saved certificate's student is still selected. */
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
        if (string.IsNullOrWhiteSpace(sportName.Text))
        {
            Warn("Enter the Sport Name.");
            return false;
        }
        if (string.IsNullOrWhiteSpace(performance.Text))
        {
            Warn("Enter the Performance.");
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
        if (creatingCertificate) return;
        if (!printButton.Enabled) return;
        // Finding 13 - the preview is modal; park the search while it is open.
        try
        {
            if (selectedSportsCertificateId <= 0)
            {
                Warn("Select a sports certificate from the list first.");
                return;
            }
            PrintCertificate(selectedSportsCertificateId);
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
        finally
        {
        }
    }
    private void PrintCertificate(int sportsCertificateId)
    {
        DataTable data =
            DatabaseHelper.QueryUser(
                "GetSportsCertificatePrintData",
                DatabaseHelper.Int("@SportsCertificateId", sportsCertificateId));
        if (data.Rows.Count == 0)
        {
            Warn(UiText.Certificates.PrintDataMissing);
            return;
        }
        string reportPath =
            Path.Combine(AppContext.BaseDirectory, "Reports", "SportsCertificate.rdlc");
        if (!File.Exists(reportPath))
        {
            throw new FileNotFoundException("Sports Certificate RDLC report was not found.", reportPath);
        }
        ShowReport(FindForm(), "Sports Certificate", reportPath, "SportsCertificateData", data);
    }
    private void ConfigureFieldSuggestions()
    {
        sportName.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        sportName.AutoCompleteSource = AutoCompleteSource.CustomSource;
        sportName.AutoCompleteCustomSource = new AutoCompleteStringCollection();
        performance.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        performance.AutoCompleteSource = AutoCompleteSource.CustomSource;
        performance.AutoCompleteCustomSource = new AutoCompleteStringCollection();
        performance.AutoCompleteCustomSource.AddRange(PerformanceSentences.Select(item=>item.Sentence).ToArray());
    }
    private void LoadFieldSuggestions()
    {
        LoadFieldSuggestions(sportName, "SportName");
        LoadFieldSuggestions(performance, "Performance");
    }
    private void LoadFieldSuggestions(ModernTextBox field, string columnField)
    {
        try
        {
            DataTable table = DatabaseHelper.QueryUser("GetSportsCertificateFieldSuggestions", DatabaseHelper.P("@Field", columnField));
            var source = new AutoCompleteStringCollection();
            if(columnField=="Performance")source.AddRange(PerformanceSentences.Select(item=>item.Sentence).ToArray());
            foreach (DataRow row in table.Rows)
            {
                string? value = Convert.ToString(row["Value"]);
                if (!string.IsNullOrWhiteSpace(value)&&!source.Cast<string>().Any(existing=>string.Equals(existing,value,StringComparison.OrdinalIgnoreCase)))
                    source.Add(value);
            }
            field.AutoCompleteCustomSource = source;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"{nameof(LoadFieldSuggestions)} failed: {ex.Message}");
        }
    }
    private static void AddSuggestionIfNew(ModernTextBox field, string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return;
        if (field.AutoCompleteCustomSource.Cast<string>()
                .Any(existing => string.Equals(existing, value, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }
        field.AutoCompleteCustomSource.Add(value);
    }
    private void RefreshCertificates()
    {
        if (creatingCertificate) return;
        if (student.SelectedId <= 0) return;
        int selectedId = selectedSportsCertificateId;
        LoadCertificates(student.SelectedId);
        if (selectedId > 0) SelectSavedCertificate(selectedId);
    }
    private void ClearCertificateFields()
    {
        performanceSuggestions.SelectedIndex=0;
        sportName.Clear();
        performance.Clear();
        certificateDate.Value =
            DateTime.Today;
    }
    private void ClearCertificateSelection()
    {
        selectedSportsCertificateId = 0;
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
                CertificateUi.GetInt(row, "SportsCertificateId") == recordId))
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
