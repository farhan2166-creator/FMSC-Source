using FMSC_SMS.Core;
using Microsoft.Data.SqlClient;
using static FMSC_SMS.Core.CertificateUi;
namespace FMSC_SMS.Pages.Students.Certificates;
public sealed class ProvisionalCertificatePage : V2Page, IUnsavedChanges, IBusyPage, IStudentCertificateWorkspaceEditor
{
    internal override bool FocusEntryField() => CertificateUi.FocusEntry(student, Grid, examType);
    public bool HasUnsavedChanges => HasCertificateEdits;
    public string UnsavedChangesMessage => "Discard the unsaved certificate changes?";
    public bool IsBusy => creatingCertificate;
    public string BusyMessage => "Please wait for the certificate to finish saving.";
    private bool creatingCertificate;
    private readonly Func<SqlParameter[], Task<int>> writeCertificate;
    private readonly Func<int, DataTable> readCertificates;
    private readonly Func<int, DataTable> readSubjects;
    private HumanCard workspaceDetails = null!;
    private TableLayoutPanel workspaceBody = null!;
    private Action<DataRowView?>? workspaceSaved;
    Action<DataRowView?>? IStudentCertificateWorkspaceEditor.Saved { set => workspaceSaved = value; }
    HumanCard IStudentCertificateWorkspaceEditor.DetailsCard => workspaceDetails;
    Label IStudentCertificateWorkspaceEditor.Feedback => StatusLabel;
    void IStudentCertificateWorkspaceEditor.ConfigureWorkspaceDetails(DataRow selectedStudent, bool editing)
    {
        CertificateWorkspaceDetailsDialog.LockStudent(student, selectedStudent);
        workspaceBody.AutoScroll = true;
        createCertificateButton.Visible = !editing;
        updateCertificateButton.Visible = editing;
        clearButton.Visible = false;
        printButton.Visible = false;
        copyFlow.Collect.Visible = false;
        copyFlow.IssueAgain.Visible = false;
    }
    private readonly StudentPicker student = new();
    private readonly ModernTextBox dateOfBirthDisplay = HumanUi.TextBox(false,150);
    private readonly CertificateCollectionFlow copyFlow = new("Provisional");
    private readonly ModernComboBox examType =
        HumanUi.Combo();
    private readonly ModernTextBox examYear =
        HumanUi.TextBox(false, 100);
    private readonly ModernTextBox boardName =
        HumanUi.TextBox(false, 200);
    private readonly ModernTextBox rollNumber =
        HumanUi.TextBox(false, 100);
    private readonly ModernTextBox marksObtained =
        HumanUi.TextBox(false, 80);
    private readonly ModernTextBox marksOutOf =
        HumanUi.TextBox(false, 80);
    private readonly ModernComboBox grade =
        HumanUi.Combo();
    private readonly ModernDatePicker certificateDate =
        HumanUi.Date();
    private readonly ModernTextBox otherSubjects =
        HumanUi.TextBox(false, 500);
    private readonly CheckedListBox subjectsList = new() {
        CheckOnClick = true,
        BorderStyle = BorderStyle.FixedSingle
    };
    private readonly ModernButton selectAllSubjects = new() { Text = UiText.Actions.SelectAll, Width = AppScale.Scale(110), ThemeRole = "Secondary" };
    private readonly ModernButton clearAllSubjects = new() { Text = "Clear All", Width = AppScale.Scale(110), ThemeRole = "Secondary" };
    private bool changingSubjectChecks;
    private bool syncingSubjectChecks;
    private readonly TableLayoutPanel subjectChoices = new() {
        Dock = DockStyle.Top, AutoScroll = false, Margin = Padding.Empty, Padding = Padding.Empty
    };
    private Action? fitSubjectArea;
    private readonly FormValidator editorValidator = new();
    private bool validatingEditor;
    private DataTable studentResults => student.Source;
    private DataTable certificateResults = new();
    private string? savedEditorSnapshot;
    private string? newContentSnapshot;
    private string EditorSnapshot() => ContentSnapshot() + "\u001f" + certificateDate.Value.Date.ToString("yyyy-MM-dd");
    private string ContentSnapshot() => string.Join("\u001f", examType.Text, examYear.Text, boardName.Text, rollNumber.Text, marksObtained.Text, marksOutOf.Text, grade.Text, otherSubjects.Text, string.Join("|", subjectsList.CheckedItems.Cast<object>().Select(item => item.ToString())));
    // Owner policy (UFA-04): a new date-only draft is abandonable; saved-record date edits count.
    private bool HasCertificateEdits => student.SelectedId > 0 &&
        (selectedProvisionalCertificateId > 0
            ? savedEditorSnapshot is not null && savedEditorSnapshot != EditorSnapshot()
            : newContentSnapshot is not null && newContentSnapshot != ContentSnapshot());
    private DataTable subjectsTable = new();
    /* Track certificate subjects missing from the current subject catalog. */
    private readonly List<int> savedSubjectIds = new();
    /* Suspend search debounce while a modal dialog is open. */
    private bool suppressStudentChanged;
    private int selectedProvisionalCertificateId;
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
    private static readonly string[] ExamTypeCodes = { "SSC", "HSSC1", "HSSC2" };
    private static readonly string[] ExamTypeLabels = {
        "SSC (Matric)",
        "HSSC Part I",
        "HSSC Part II"
    };
    public ProvisionalCertificatePage() : this(true) { }
    internal ProvisionalCertificatePage(bool initialize, Func<SqlParameter[], Task<int>>? writeCertificate = null,
        Func<int, DataTable>? readCertificates = null, Func<int, DataTable>? readSubjects = null)
        : base("Provisional Certificate", "Record and print provisional (board result) certificates for SSC/HSSC.")
    {
        this.writeCertificate = writeCertificate ?? (args => ReadSavedCertificateIdAsync("CreateProvisionalCertificate", "ProvisionalCertificateId", args));
        this.readCertificates = readCertificates ?? (id => DatabaseHelper.QueryUser("GetProvisionalCertificateRecords", DatabaseHelper.Int("@AdmissionId", id), DatabaseHelper.P("@UserId", CurrentUser.UserId)));
        this.readSubjects = readSubjects ?? (id => DatabaseHelper.QueryUser("GetProvisionalCertificateSubjects", DatabaseHelper.Int("@ProvisionalCertificateId", id)));
        examType.DropDownStyle =
            ComboBoxStyle.DropDownList;
        examType.Items.AddRange(ExamTypeLabels);
        examYear.PlaceholderText =
            "e.g. 2026";
        boardName.PlaceholderText =
            "e.g. B.I.S.E Peshawar";
        boardName.MaxLength = 150;
        rollNumber.PlaceholderText =
            "Board roll number";
        rollNumber.MaxLength = 30;
        marksObtained.PlaceholderText =
            "e.g. 707";
        marksOutOf.PlaceholderText =
            "e.g. 1100";
        grade.DropDownStyle = ComboBoxStyle.DropDownList;
        grade.Items.AddRange(new[] { "", "A+", "A", "B", "C", "D", "E", "F" });
        grade.Enabled = false;
        otherSubjects.PlaceholderText =
            "Any subjects not in the list below, comma-separated (e.g. Mutala e Quran Hakeem)";
        otherSubjects.MaxLength = 300;
        examYear.MaxLength = 4;
        marksObtained.MaxLength = marksOutOf.MaxLength = 6;
        foreach (var input in new[] { examYear, marksObtained, marksOutOf })
            WholeNumberInput.Attach(input);
        WholeNumberInput.Attach(rollNumber,active:()=>IsPeshawarBoard);
        /* Hold the synchronous handler through queued clicks to prevent duplicate saves. */
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
        copyFlow.Wire(this,()=>selectedProvisionalCertificateId,id=>{LoadCertificates(student.SelectedId);SelectSavedCertificate(id);},()=>HasCertificateEdits);
        TrackStudentSearchStatus(student);
        /* Filter as the operator types; load only after a committed student selection. */
        if (initialize) StudentSearchCache.Warm();
        student.SelectionChangeGuard = _ => !creatingCertificate &&
            (!HasCertificateEdits || Confirm("Discard the unsaved certificate changes and choose another student?", UiText.Dialogs.UnsavedChanges));
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
                BindSubjects();
                ResetToStart();
            });
        var details = workspaceDetails =
            HumanUi.Card(
                UiText.Certificates.Details,
                "Choose student and exam type/year; enter board results. Names and birth date come from the student record; select a saved row to edit other details.");
        var fields =
            new TableLayoutPanel {
                Dock = DockStyle.Top,
                Height = 3 * AppMetrics.FieldRowHeight,
                ColumnCount = 6,
                RowCount = 3,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
        for (int i = 0; i < 6; i++)
        {
            fields.ColumnStyles.Add(
                new ColumnStyle(SizeType.Percent, 100f / 6));
        }
        for (int i = 0; i < 3; i++)
        {
            fields.RowStyles.Add(
                new RowStyle(SizeType.Absolute, AppMetrics.FieldRowHeight));
        }
        var studentField =
            CertificateUi.Field("Student", student, true);
        fields.Controls.Add(studentField, 0, 0);
        fields.SetColumnSpan(studentField, 3);
        fields.Controls.Add(
            CertificateUi.Field("Exam Type", examType, true), 4, 0);
        fields.Controls.Add(
            CertificateUi.Field("Exam Year", examYear, true), 5, 0);
        var boardField =
            CertificateUi.Field("Board Name", boardName, true);
        fields.Controls.Add(boardField, 0, 1);
        fields.SetColumnSpan(boardField, 2);
        fields.Controls.Add(
            CertificateUi.Field("Roll Number", rollNumber, true), 2, 1);
        fields.Controls.Add(
            CertificateUi.Field("Marks Obtained", marksObtained, true), 3, 1);
        fields.Controls.Add(
            CertificateUi.Field("Marks Out Of", marksOutOf, true), 4, 1);
        fields.Controls.Add(
            CertificateUi.Field("Grade", grade), 5, 1);
        fields.Controls.Add(
            CertificateUi.Field(UiText.Certificates.Date, certificateDate, true), 0, 2);
        var otherSubjectsField =
            CertificateUi.Field("Other Subjects (not in list below)", otherSubjects);
        fields.Controls.Add(otherSubjectsField, 1, 2);
        fields.SetColumnSpan(otherSubjectsField, 5);
        dateOfBirthDisplay.ReadOnly=true;dateOfBirthDisplay.TabStop=false;
        fields.Controls.Add(CertificateUi.Field("Date of Birth",dateOfBirthDisplay),3,0);
        var subjectsLabel =
            new Label {
                Text =
                    "Subjects Passed",
                Dock = DockStyle.Fill,
                Font = AppTheme.ButtonFont,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = Padding.Empty
            };
        subjectsList.Font = AppTheme.NormalFont;
        subjectsList.Dock =
            DockStyle.Fill;
        subjectsList.IntegralHeight = false;
        subjectsList.Height = 2 * (AppMetrics.TextHeight(AppTheme.NormalFont) + AppMetrics.SpacingSm);
        subjectsList.MultiColumn =
            true;
        subjectsList.ColumnWidth =
            AppScale.Scale(180);
        subjectsList.ItemCheck +=
            SubjectsList_ItemCheck;
        selectAllSubjects.Click += (_, _) => SetAllSubjectChecks(true);
        clearAllSubjects.Click += (_, _) => SetAllSubjectChecks(false);
        var subjectTools = new FlowLayoutPanel {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false, Margin = Padding.Empty, Padding = Padding.Empty
        };
        foreach (var button in new[] { selectAllSubjects, clearAllSubjects })
        {
            button.Height = AppMetrics.ButtonHeight;
            button.Margin = new Padding(AppMetrics.SpacingXs, 0, 0, 0);
            subjectTools.Controls.Add(button);
        }
        var subjectsHeader = new TableLayoutPanel {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty
        };
        subjectsHeader.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        subjectsHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        subjectsHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,
            selectAllSubjects.Width + clearAllSubjects.Width + 2 * AppMetrics.SpacingXs));
        subjectsHeader.Controls.Add(subjectsLabel, 0, 0);
        subjectsHeader.Controls.Add(subjectTools, 1, 0);
        var subjectsPanel =
            new TableLayoutPanel {
                Dock = DockStyle.Top,
                ColumnCount = 1,
                RowCount = 2,
                Height = AppMetrics.ButtonHeight + AppMetrics.SpacingSm + subjectsList.Height,
                Margin = new Padding(0)
            };
        subjectsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        subjectsPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.ButtonHeight + AppMetrics.SpacingSm));
        subjectsPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, subjectsList.Height));
        subjectsPanel.Controls.Add(subjectsHeader, 0, 0);
        subjectsPanel.Controls.Add(subjectChoices, 0, 1);
        var editorBody = workspaceBody = new TableLayoutPanel {
            Dock = DockStyle.Fill, AutoScroll = true,
            ColumnCount = 1, RowCount = 2, Margin = Padding.Empty
        };
        editorBody.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        editorBody.RowStyles.Add(new RowStyle(SizeType.Absolute, fields.Height));
        editorBody.RowStyles.Add(new RowStyle(SizeType.Absolute, subjectsPanel.Height));
        editorBody.Controls.Add(fields, 0, 0);
        editorBody.Controls.Add(subjectsPanel, 0, 1);
        int subjectAreaHeight = subjectsPanel.Height;
        bool fittingSubjects = false;
        Action? fitCertificateCards = null;
        fitSubjectArea = () => {
            if (fittingSubjects || subjectChoices.ClientSize.Width <= 0) return;
            fittingSubjects = true;
            try
            {
            int rowHeight = AppMetrics.TextHeight(AppTheme.NormalFont) + AppMetrics.SpacingSm;
            int longest = subjectsList.Items.Cast<object>()
                .Select(item => TextRenderer.MeasureText(item.ToString(), AppTheme.NormalFont).Width)
                .DefaultIfEmpty(AppScale.Scale(100)).Max() + AppScale.Scale(32);
            int count = Math.Max(1, subjectsList.Items.Count);
            int fittingColumns = Math.Max(1, subjectChoices.ClientSize.Width / longest);
            int rows = count <= fittingColumns ? 1 : 2;
            int columns = (count + rows - 1) / rows;
            editorBody.AutoScroll = false;
            subjectChoices.AutoScroll = false;
            subjectChoices.SuspendLayout();
            var checkboxes = subjectChoices.Controls.Cast<Control>().ToArray();
            subjectChoices.Controls.Clear();
            subjectChoices.AutoScrollPosition = Point.Empty;
            subjectChoices.ColumnCount = columns;
            subjectChoices.RowCount = rows;
            subjectChoices.GrowStyle = TableLayoutPanelGrowStyle.FixedSize;
            subjectChoices.ColumnStyles.Clear();
            subjectChoices.RowStyles.Clear();
            int columnWidth = Math.Max(longest, Math.Max(1, subjectChoices.ClientSize.Width) / columns);
            for (int i = 0; i < columns; i++) subjectChoices.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, columnWidth));
            for (int i = 0; i < rows; i++) subjectChoices.RowStyles.Add(new RowStyle(SizeType.Absolute, rowHeight));
            for (int i = 0; i < checkboxes.Length; i++)
                subjectChoices.Controls.Add(checkboxes[i], i % columns, i / columns);
            subjectChoices.ResumeLayout(true);
            int listHeight = rows * rowHeight + (columnWidth * columns > subjectChoices.ClientSize.Width ? SystemInformation.HorizontalScrollBarHeight : 0);
            subjectChoices.AutoScrollMinSize = new Size(columnWidth * columns, rows * rowHeight);
            subjectChoices.AutoScroll = columnWidth * columns > subjectChoices.ClientSize.Width;
            subjectAreaHeight = AppMetrics.ButtonHeight + AppMetrics.SpacingSm + listHeight;
            subjectChoices.Height = listHeight;
            subjectsPanel.Height = subjectAreaHeight;
            subjectsPanel.RowStyles[1].Height = listHeight;
            editorBody.RowStyles[1].Height = subjectAreaHeight;
            subjectChoices.MinimumSize = new Size(0, listHeight);
            subjectsPanel.MinimumSize = new Size(0, subjectAreaHeight);
            fitCertificateCards?.Invoke();
            editorBody.AutoScroll = editorBody.ClientSize.Height < fields.Height + subjectAreaHeight;
            }
            finally { fittingSubjects = false; }
        };
        subjectChoices.SizeChanged += (_, _) => fitSubjectArea();
        int fieldColumns = 6;
        void ReflowFields()
        {
            int columns = editorBody.ClientSize.Width < AppScale.Scale(1000) ? 3 : 6;
            if (columns == fieldColumns)
            {
                fields.Height = fields.RowCount * AppMetrics.FieldRowHeight;
                editorBody.RowStyles[0].Height = fields.Height;
                return;
            }
            fieldColumns = columns;
            fields.SuspendLayout();
            var fieldControls = fields.Controls.Cast<Control>().ToArray();
            fields.Controls.Clear();
            fields.ColumnStyles.Clear();
            fields.RowStyles.Clear();
            fields.ColumnCount = columns;
            fields.RowCount = columns == 3 ? 5 : 3;
            for (int i = 0; i < columns; i++) fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / columns));
            for (int i = 0; i < fields.RowCount; i++) fields.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.FieldRowHeight));
            var positions = columns == 3
                ? new[] { (0, 0, 2), (0, 1, 1), (1, 1, 1), (0, 2, 2), (2, 2, 1), (0, 3, 1), (1, 3, 1), (2, 3, 1), (2, 1, 1), (0, 4, 3), (2,0,1) }
                : new[] { (0, 0, 3), (4, 0, 1), (5, 0, 1), (0, 1, 2), (2, 1, 1), (3, 1, 1), (4, 1, 1), (5, 1, 1), (0, 2, 1), (1, 2, 5), (3,0,1) };
            for (int i = 0; i < fieldControls.Length; i++)
            {
                fields.Controls.Add(fieldControls[i], positions[i].Item1, positions[i].Item2);
                fields.SetColumnSpan(fieldControls[i], positions[i].Item3);
            }
            fields.Height = fields.RowCount * AppMetrics.FieldRowHeight;
            editorBody.RowStyles[0].Height = fields.Height;
            fields.ResumeLayout(true);
        }
        editorBody.SizeChanged += (_, _) => ReflowFields();
        var certButtonRow =
            CertificateButtonRow.Create(createCertificateButton, updateCertificateButton, clearButton);
        certButtonRow.Dock =
            DockStyle.Bottom;
        details.Content.Controls.Add(editorBody);
        details.Content.Controls.Add(certButtonRow);
        editorBody.BringToFront();
        var records =
            HumanUi.Card(
                "Provisional Certificates",
                "Select an existing certificate to update or reprint it. Use New to start another request.");
        var certificateActions =
            CertificateButtonRow.Create(printButton, copyFlow.Collect, copyFlow.IssueAgain, refreshButton);
        certificateActions.Dock =
            DockStyle.Bottom;
        records.Content.Controls.Add(certificateActions);
        records.Content.Controls.Add(Grid);
        CertificateUi.WireNavigation(this, student, Grid, clearButton, editorBody, certButtonRow, certificateActions,
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
            new RowStyle(SizeType.Percent, 75));
        root.RowStyles.Add(
            new RowStyle(SizeType.Percent, 25));
        root.Controls.Add(details, 0, 0);
        root.Controls.Add(records, 0, 1);
        bool sizingCards = false;
        void FitCards()
        {
            if (sizingCards || root.ClientSize.Height <= 0 || details.Parent != root) return;
            int detailsHeight = details.Header.Height + details.Padding.Vertical + details.Content.Padding.Vertical
                + fields.Height + subjectAreaHeight + certButtonRow.Height + certButtonRow.Margin.Vertical;
            int recordsHeight = records.Header.Height + records.Padding.Vertical + records.Content.Padding.Vertical
                + certificateActions.Height + certificateActions.Margin.Vertical + AppMetrics.GridHeaderHeight;
            int available = root.ClientSize.Height;
            int desired = Math.Min(detailsHeight, Math.Max(1, available - recordsHeight));
            float share = Math.Clamp(100f * desired / available, 1, 99);
            if (Math.Abs(root.RowStyles[0].Height - share) < 0.1f) return;
            sizingCards = true;
            try
            {
                root.RowStyles[0].Height = share;
                root.RowStyles[1].Height = 100 - share;
            }
            finally { sizingCards = false; }
        }
        fitCertificateCards = FitCards;
        root.SizeChanged += (_, _) => FitCards();
        root.Layout += (_, _) => FitCards();
        certButtonRow.SizeChanged += (_, _) => FitCards();
        certificateActions.SizeChanged += (_, _) => FitCards();
        fields.SizeChanged += (_, _) => FitCards();
        subjectsPanel.SizeChanged += (_, _) => FitCards();
        SetContent(
            root,
            UiText.Certificates.StudentGuidance);
        examYear.TextChanged += (_, _) => { CalculateGrade(); ApplyWorkflowState(); };
        boardName.TextChanged += (_, _) => ApplyWorkflowState();
        rollNumber.TextChanged += (_, _) => ApplyWorkflowState();
        marksObtained.TextChanged += (_, _) => { CalculateGrade(); ApplyWorkflowState(); };
        marksOutOf.TextChanged += (_, _) => { CalculateGrade(); ApplyWorkflowState(); };
        grade.TextChanged += (_, _) => ApplyWorkflowState();
        otherSubjects.TextChanged += (_, _) => ApplyWorkflowState();
        certificateDate.ValueChanged += (_, _) => ApplyWorkflowState();
        examType.SelectedIndexChanged += (_, _) => { CalculateGrade(); ApplyWorkflowState(); };
        subjectsList.ItemCheck += (_, e) => {
            if (e.Index < subjectChoices.Controls.Count && subjectChoices.Controls[e.Index] is CheckBox checkbox)
            {
                syncingSubjectChecks = true;
                try { checkbox.Checked = e.NewValue == CheckState.Checked; }
                finally { syncingSubjectChecks = false; }
            }
            if (!changingSubjectChecks && IsHandleCreated) BeginInvoke(new MethodInvoker(ApplyWorkflowState));
        };
        newContentSnapshot = ContentSnapshot();
        ApplyWorkflowState();
    }
    private void BindSubjects()
    {
        try
        {
            subjectsTable =
                DatabaseHelper.QueryUser(
                        "GetSubjects",
                        DatabaseHelper.Int(
                            "@UserId",
                            CurrentUser.UserId))
                    .Copy();
            subjectsTable = SubjectDisplayOrder.Order(subjectsTable);
            try
            {
                subjectsList.Items.Clear();
                savedSubjectIds.Clear();
                foreach (DataRow row in subjectsTable.Rows)
                {
                    subjectsList.Items.Add(
                        GetString(row, "SubjectName"), false);
                }
                FitSubjectColumns();
            }
            finally
            {
            }
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }
    private void SubjectsList_ItemCheck(object? sender, ItemCheckEventArgs e)
    {
    }
    private void FitSubjectColumns()
    {
        subjectChoices.SuspendLayout();
        subjectChoices.GrowStyle = TableLayoutPanelGrowStyle.AddRows;
        foreach (Control control in subjectChoices.Controls.Cast<Control>().ToArray()) control.Dispose();
        subjectChoices.Controls.Clear();
        for (int i = 0; i < subjectsList.Items.Count; i++)
        {
            int index = i;
            var checkbox = new CheckBox {
                Text = subjectsList.Items[i]?.ToString(), Checked = subjectsList.GetItemChecked(i),
                Font = AppTheme.NormalFont, Dock = DockStyle.Fill, Margin = Padding.Empty,
                TextAlign = ContentAlignment.MiddleLeft, CheckAlign = ContentAlignment.MiddleLeft,
                Enabled = subjectsList.Enabled, AutoSize = false
            };
            checkbox.CheckedChanged += (_, _) => {
                if (!syncingSubjectChecks && subjectsList.GetItemChecked(index) != checkbox.Checked)
                    subjectsList.SetItemChecked(index, checkbox.Checked);
            };
            subjectChoices.Controls.Add(checkbox);
        }
        subjectChoices.ResumeLayout(true);
        fitSubjectArea?.Invoke();
    }
    private void SetAllSubjectChecks(bool check)
    {
        if (!subjectsList.Enabled) return;
        changingSubjectChecks = true;
        subjectsList.BeginUpdate();
        try
        {
            for (int i = 0; i < subjectsList.Items.Count; i++)
                subjectsList.SetItemChecked(i, check);
        }
        finally
        {
            subjectsList.EndUpdate();
            changingSubjectChecks = false;
        }
        ApplyWorkflowState();
    }
    private string GetCheckedSubjectIdsCsv()
    {
        var ids =
            new List<string>();
        for (int i = 0; i < subjectsList.Items.Count; i++)
        {
            if (!subjectsList.GetItemChecked(i))
                continue;
            int subjectId;
            if (i >= subjectsTable.Rows.Count)
            {
                /* Finding 3 - an entry appended because this certificate holds a subject that has since been deactivated. */
                int extraIndex =
                    i - subjectsTable.Rows.Count;
                subjectId =
                    extraIndex < savedSubjectIds.Count
                        ? savedSubjectIds[extraIndex]
                        : 0;
            }
            else
            {
                subjectId =
                    GetInt(subjectsTable.Rows[i], "SubjectId");
            }
            if (subjectId > 0)
            {
                ids.Add(subjectId.ToString());
            }
        }
        return string.Join(",", ids);
    }
    /* Finding 3 - drops the "(saved)" entries appended for a selected certificate. */
    private void RemoveSavedSubjectItems()
    {
        savedSubjectIds.Clear();
        // Only trim what THIS method appended: if the subject list failed to bind, subjectsTable is empty and trimming would wipe the whole list.
        if (subjectsTable.Rows.Count == 0)
            return;
        while (subjectsList.Items.Count > subjectsTable.Rows.Count)
        {
            subjectsList.Items.RemoveAt(
                subjectsList.Items.Count - 1);
        }
    }
    private void CheckSubjectsByName(IEnumerable<string> subjectNames)
    {
        var wanted =
            new HashSet<string>(subjectNames, StringComparer.OrdinalIgnoreCase);
        try
        {
            for (int i = 0; i < subjectsList.Items.Count; i++)
            {
                string itemText =
                    subjectsList.Items[i]?.ToString()
                    ?? string.Empty;
                subjectsList.SetItemChecked(i, wanted.Contains(itemText));
            }
        }
        finally
        {
        }
    }
    private void UncheckAllSubjects()
    {
        RemoveSavedSubjectItems();
        try
        {
            for (int i = 0; i < subjectsList.Items.Count; i++)
            {
                subjectsList.SetItemChecked(i, false);
            }
        }
        finally
        {
            FitSubjectColumns();
        }
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
            CertificateUi.FocusHistory(Grid, () => SelectSavedCertificate(0), examType);
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
            certificateResults = CertificateRecordOrder.NewestFirst(table, "ProvisionalCertificateId");
            Bind(certificateResults);
            FormatCertificateGrid();
            Grid.ClearSelection();
            try
            {
                Grid.CurrentCell = null;
            }
            catch (Exception nonFatalException65)
            {
                FMSC_SMS.Core.AppExceptionHandler.LogNonFatal(nonFatalException65, "ProvisionalCertificatePage: LoadCertificates fallback");
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

        CertificateRecordOrder.Select(Grid, "ProvisionalCertificateId", id,
            index => Grid_CellMouseClick(Grid, new DataGridViewCellMouseEventArgs(0,index,0,0,new MouseEventArgs(MouseButtons.Left,1,0,0,0))));
    }
    private void FormatCertificateGrid()
    {
        CertificateRecordOrder.ArrangeColumns(Grid, "ProvisionalCertificateId");
        /* Keep grid order aligned with certificateResults for index-based selection. */
        LockGridOrder();
        SetGridWidth("ProvisionalCertificateId", 60);
        if (Grid.Columns.Contains("ProvisionalCertificateId"))
        {
            Grid.Columns["ProvisionalCertificateId"]!.HeaderText =
                "Certificate No.";
        }
        SetGridWidth("AdmissionNumber", 80);
        SetGridWidth("StudentName", 160);
        SetGridWidth("FatherName", 140);
        SetGridWidth("ExamType", 0);
        SetGridWidth("ExamTypeDisplay", 110);
        SetGridWidth("ExamYear", 80);
        SetGridWidth("BoardName", 140);
        SetGridWidth("RollNumber", 100);
        SetGridWidth("MarksObtained", 90);
        SetGridWidth("MarksOutOf", 90);
        SetGridWidth("Grade", 60);
        SetGridWidth("CertificateDate", 110);
        SetGridWidth("OtherSubjects", 160);
        SetGridWidth("CreatedAt", 145);
        SetGridWidth("UpdatedAt", 145);
        HideGridColumn("AdmissionId");
        HideGridColumn("ExamType");
        foreach (string column in new[] { "CreatedAt", "UpdatedAt", "OtherSubjects" }) HideGridColumn(column);
        foreach (var (column, caption) in new[] {
            ("AdmissionNumber", "Admission No."), ("ExamTypeDisplay", "Exam"), ("ExamYear", "Year"),
            ("BoardName", "Board"), ("RollNumber", "Roll No."), ("MarksObtained", "Marks"), ("MarksOutOf", "Total")
        }) if (Grid.Columns.Contains(column)) Grid.Columns[column].HeaderText = caption;
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
            int provisionalCertificateId =
                GetInt(row, "ProvisionalCertificateId");
            if (provisionalCertificateId <= 0)
            {
                ClearCertificateSelection();
                SetStatus("The selected certificate contains invalid data.", true);
                return;
            }
            if (!ConfirmCertificateSelection(FindForm(), Grid, "ProvisionalCertificateId",
                selectedProvisionalCertificateId, provisionalCertificateId, HasCertificateEdits)) return;
            selectedProvisionalCertificateId =
                provisionalCertificateId;
            string examTypeCode =
                GetString(row, "ExamType");
            int examTypeIndex =
                Array.IndexOf(ExamTypeCodes, examTypeCode);
            examType.SelectedIndex =
                examTypeIndex >= 0
                    ? examTypeIndex
                    : -1;
            examYear.Text =
                GetInt(row, "ExamYear")
                    .ToString();
            boardName.Text =
                GetString(row, "BoardName");
            rollNumber.Text =
                GetString(row, "RollNumber");
            marksObtained.Text =
                GetInt(row, "MarksObtained")
                    .ToString();
            marksOutOf.Text =
                GetInt(row, "MarksOutOf")
                    .ToString();
            string savedGrade = StandardGrade(GetString(row, "Grade"));
            grade.Text = savedGrade;
            DateTime? certDate =
                GetDate(row, "CertificateDate");
            certificateDate.Value =
                certDate ?? DateTime.Today;
            /* Preserve typed text when the database omits certificate subject data. */
            string? selectionHint =
                null;
            if (row.Table.Columns.Contains("OtherSubjects"))
            {
                otherSubjects.Text =
                    GetString(row, "OtherSubjects");
            }
            else
            {
                selectionHint =
                    "Other Subjects could not be loaded for this certificate - check the box before using Update, which saves whatever it holds.";
            }
            string? subjectsHint =
                LoadSubjectsForCertificate(provisionalCertificateId);
            savedEditorSnapshot = EditorSnapshot();
            ApplyWorkflowState();
            if (subjectsHint is not null)
            {
                selectionHint =
                    selectionHint is null
                        ? subjectsHint
                        : selectionHint + " " + subjectsHint;
            }
            if (selectionHint is not null)
            {
                SetStatus(selectionHint, true);
            }
        }
        catch (Exception ex)
        {
            ClearCertificateSelection();
            SetStatus($"Unable to read the selected certificate: {DbMessage.UserMessage(ex)}", true);
        }
    }
    /// <summary>Select stored subjects and report any that cannot be displayed.</summary>
    private string? LoadSubjectsForCertificate(int provisionalCertificateId)
    {
        try
        {
            // Drop any entries appended for the previously selected certificate FIRST, so a failing query below cannot leave them ticked.
            RemoveSavedSubjectItems();
            DataTable table =
                readSubjects(provisionalCertificateId);
            var names =
                new List<string>();
            foreach (DataRow row in table.Rows)
            {
                names.Add(
                    GetString(row, "SubjectName"));
            }
            CheckSubjectsByName(names);
            /* Keep subject snapshots whose names differ from the current catalogue. */
            int appended =
                0;
            int unresolved =
                0;
            foreach (DataRow row in table.Rows)
            {
                string subjectName =
                    GetString(row, "SubjectName");
                if (string.IsNullOrWhiteSpace(subjectName))
                    continue;
                bool onList =
                    false;
                for (int i = 0; i < subjectsTable.Rows.Count && i < subjectsList.Items.Count; i++)
                {
                    if (string.Equals(
                            subjectsList.Items[i]?.ToString(), subjectName, StringComparison.OrdinalIgnoreCase))
                    {
                        onList = true;
                        break;
                    }
                }
                if (onList)
                    continue;
                int subjectId =
                    GetInt(row, "SubjectId");
                if (subjectId <= 0)
                {
                    unresolved++;
                    continue;
                }
                savedSubjectIds.Add(subjectId);
                subjectsList.Items.Add(
                    subjectName + " (saved)", true);
                appended++;
            }
            FitSubjectColumns();
            if (unresolved > 0)
            {
                return $"{unresolved} subject(s) on this certificate are no longer on the subject list and could not be kept - re-enter them under Other Subjects before using Update.";
            }
            return appended > 0
                ? $"{appended} subject(s) on this certificate are saved with an earlier subject name; they are shown ticked and marked (saved) so Update keeps them."
                : null;
        }
        catch (Exception ex)
        {
            Fail(ex);
            return null;
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
            selectedProvisionalCertificateId > 0;
        var birthRow=hasSelection?CertificateCollectionFlow.Find(certificateResults,"ProvisionalCertificateId",selectedProvisionalCertificateId):student.SelectedRow;
        DateTime? recordedBirth=birthRow is null?null:GetDate(birthRow,"DateOfBirth");
        dateOfBirthDisplay.Text=recordedBirth?.ToString("dd MMM yyyy")??(hasStudent?"Not on record":"");
        bool validEditor = hasStudent && recordedBirth.HasValue && CheckEditor(false);
        student.Enabled = true;
        examType.Enabled = hasStudent;
        examYear.Enabled = hasStudent;
        boardName.Enabled = hasStudent;
        rollNumber.Enabled = hasStudent;
        marksObtained.Enabled = hasStudent;
        marksOutOf.Enabled = hasStudent;
        grade.Enabled = hasStudent;
        certificateDate.Enabled = hasStudent;
        otherSubjects.Enabled = hasStudent;
        subjectsList.Enabled = hasStudent;
        subjectChoices.Enabled = hasStudent;
        foreach (Control checkbox in subjectChoices.Controls) checkbox.Enabled = hasStudent;
        selectAllSubjects.Enabled = hasStudent && subjectsList.Items.Count > 0;
        clearAllSubjects.Enabled = hasStudent && subjectsList.Items.Count > 0;
        createCertificateButton.Enabled =
            hasStudent &&
            !hasSelection && validEditor;
        updateCertificateButton.Enabled =
            hasStudent &&
            hasSelection && HasCertificateEdits && validEditor;
        clearButton.Enabled = true;
        refreshButton.Enabled =
            hasStudent;
        printButton.Enabled =
            hasSelection && !HasCertificateEdits && CurrentUser.HasPermission("Certificates.Print");
        copyFlow.Available=!creatingCertificate;
        if(copyFlow.Apply(certificateResults,"ProvisionalCertificateId",selectedProvisionalCertificateId,updateCertificateButton,printButton,HasCertificateEdits,examType,examYear,boardName,rollNumber,marksObtained,marksOutOf,grade,certificateDate,otherSubjects,subjectsList,subjectChoices,selectAllSubjects,clearAllSubjects))
        { SetStatus(copyFlow.Status); return; }
        if (!showStatus) return;
        if(hasStudent&&!hasSelection&&!recordedBirth.HasValue){SetStatus("Date of birth is missing. Complete the student's record, then refresh before creating the certificate.",true);return;}
        if (!hasStudent)
        {
            SetStatus(UiText.Prompts.SearchForStudent);
            return;
        }
        if (hasSelection)
        {
            SetStatus(
                "Editing certificate. Use Save Changes to save, or New to clear the form.");
            return;
        }
        SetStatus(
            "Enter the board result and check passed subjects to create. Select a saved certificate to edit or print.");
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
            if (!ValidateCertificateFields(out int examYearValue, out int marksObtainedValue, out int marksOutOfValue))
            {
                return;
            }
            int admissionId =
                student.SelectedId;
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
                DatabaseHelper.P(
                    "@ExamType",
                    ExamTypeCodes[examType.SelectedIndex]),
                DatabaseHelper.Int(
                    "@ExamYear",
                    examYearValue),
                DatabaseHelper.P(
                    "@BoardName",
                    boardName.Text.Trim()),
                DatabaseHelper.P(
                    "@RollNumber",
                    rollNumber.Text.Trim()),
                DatabaseHelper.Int(
                    "@MarksObtained",
                    marksObtainedValue),
                DatabaseHelper.Int(
                    "@MarksOutOf",
                    marksOutOfValue),
                DatabaseHelper.P("@Grade", string.IsNullOrWhiteSpace(grade.Text)
                        ? null
                        : grade.Text.Trim()),
                DatabaseHelper.P("@OtherSubjects", string.IsNullOrWhiteSpace(otherSubjects.Text)
                        ? null
                        : otherSubjects.Text.Trim()),
                DatabaseHelper.Date(
                    "@CertificateDate",
                    certificateDate.Value.Date),
                DatabaseHelper.P("@SubjectIds", GetCheckedSubjectIdsCsv()) });

            /* Refresh only for the current student; retain success because the write committed. */
            if (IsDisposed || student.SelectedId != admissionId)
                return;
            ClearCertificateFields();
            ClearCertificateSelection();
            LoadCertificates(admissionId);
            CertificateRecordOrder.RequireSavedCopy(certificateResults, "ProvisionalCertificateId", savedId);
            SelectSavedCertificate(savedId);
            successMessage =
                "Certificate created successfully.";
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
        int savedCertificateId = selectedProvisionalCertificateId;
        // Finding 12 - see CreateCertificate: the confirmation is set last.
        string? successMessage =
            null;
        try
        {
            if (selectedProvisionalCertificateId <= 0)
            {
                Warn("Select a certificate from the list first.");
                return;
            }
            if (!ValidateCertificateFields(out int examYearValue, out int marksObtainedValue, out int marksOutOfValue))
            {
                return;
            }
            int admissionId =
                student.SelectedId;
            updateCertificateButton.Enabled = false;
            DatabaseHelper.Execute(
                "UpdateProvisionalCertificate",
                DatabaseHelper.P(
                    "@UserId",
                    CurrentUser.UserId),
                DatabaseHelper.Int(
                    "@ProvisionalCertificateId",
                    selectedProvisionalCertificateId),
                DatabaseHelper.P(
                    "@ExamType",
                    ExamTypeCodes[examType.SelectedIndex]),
                DatabaseHelper.Int(
                    "@ExamYear",
                    examYearValue),
                DatabaseHelper.P(
                    "@BoardName",
                    boardName.Text.Trim()),
                DatabaseHelper.P(
                    "@RollNumber",
                    rollNumber.Text.Trim()),
                DatabaseHelper.Int(
                    "@MarksObtained",
                    marksObtainedValue),
                DatabaseHelper.Int(
                    "@MarksOutOf",
                    marksOutOfValue),
                DatabaseHelper.P("@Grade", string.IsNullOrWhiteSpace(grade.Text)
                        ? null
                        : grade.Text.Trim()),
                DatabaseHelper.P("@OtherSubjects", string.IsNullOrWhiteSpace(otherSubjects.Text)
                        ? null
                        : otherSubjects.Text.Trim()),
                DatabaseHelper.Date(
                    "@CertificateDate",
                    certificateDate.Value.Date),
                DatabaseHelper.P("@SubjectIds", GetCheckedSubjectIdsCsv()),CertificateCollectionFlow.VersionParameter(certificateResults,"ProvisionalCertificateId",selectedProvisionalCertificateId));
            successMessage =
                "Certificate updated successfully.";
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
    private bool ValidateCertificateFields(out int examYearValue, out int marksObtainedValue, out int marksOutOfValue)
    {
        if (!CheckEditor(true))
        {
            examYearValue = marksObtainedValue = marksOutOfValue = 0;
            return false;
        }
        examYearValue = 0;
        marksObtainedValue = 0;
        marksOutOfValue = 0;
        if (examType.SelectedIndex < 0)
        {
            Warn("Select the exam type.");
            return false;
        }
        if (!int.TryParse(
                examYear.Text.Trim(),
                out examYearValue) ||
            examYearValue < 1990 ||
            examYearValue > DateTime.Today.Year + 1)
        {
            Warn("Enter a valid exam year.");
            return false;
        }
        if (string.IsNullOrWhiteSpace(boardName.Text))
        {
            Warn("Enter the board name.");
            return false;
        }
        if (string.IsNullOrWhiteSpace(rollNumber.Text))
        {
            Warn("Enter the roll number.");
            return false;
        }
        if (!int.TryParse(
                marksObtained.Text.Trim(),
                out marksObtainedValue) ||
            marksObtainedValue < 0)
        {
            Warn("Enter valid marks obtained.");
            return false;
        }
        if (!int.TryParse(
                marksOutOf.Text.Trim(),
                out marksOutOfValue) ||
            marksOutOfValue <= 0)
        {
            Warn("Enter a valid marks out of.");
            return false;
        }
        if (marksObtainedValue > marksOutOfValue)
        {
            Warn("Marks obtained cannot exceed marks out of.");
            return false;
        }
        if (certificateDate.Value.Date > DateTime.Today)
        {
            Warn(UiText.Certificates.FutureDateRefused);
            return false;
        }
        bool anySubjectChecked =
            false;
        for (int i = 0; i < subjectsList.Items.Count; i++)
        {
            if (subjectsList.GetItemChecked(i))
            {
                anySubjectChecked = true;
                break;
            }
        }
        if (!anySubjectChecked &&
            string.IsNullOrWhiteSpace(otherSubjects.Text))
        {
            Warn("Check at least one subject, or enter subjects in the Other Subjects field.");
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
            if (selectedProvisionalCertificateId <= 0)
            {
                Warn("Select a certificate from the list first.");
                return;
            }
            PrintCertificate(selectedProvisionalCertificateId);
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
        finally
        {
        }
    }
    private void PrintCertificate(int provisionalCertificateId)
    {
        DataTable data =
            DatabaseHelper.QueryUser(
                "GetProvisionalCertificatePrintData",
                DatabaseHelper.P(
                    "@UserId",
                    CurrentUser.UserId),
                DatabaseHelper.Int("@ProvisionalCertificateId", provisionalCertificateId));
        if (data.Rows.Count == 0)
        {
            Warn(UiText.Certificates.PrintDataMissing);
            return;
        }
        string reportPath =
            Path.Combine(AppContext.BaseDirectory, "Reports", "ProvisionalCertificate.rdlc");
        if (!File.Exists(reportPath))
        {
            throw new FileNotFoundException("Provisional Certificate RDLC report was not found.", reportPath);
        }
        var subjectRows = DatabaseHelper.QuerySql("""
            EXEC dbo.RequirePermission @UserId, N'Certificates.Print';
            SELECT PCS.SubjectName, PC.OtherSubjects
            FROM dbo.ProvisionalCertificates PC
            LEFT JOIN dbo.ProvisionalCertificateSubjects PCS ON PCS.ProvisionalCertificateId = PC.ProvisionalCertificateId
            WHERE PC.ProvisionalCertificateId = @CertificateId
            ORDER BY PCS.DisplayOrder, PCS.ProvisionalCertificateSubjectId;
            """, DatabaseHelper.Int("@UserId", CurrentUser.UserId),
            DatabaseHelper.Int("@CertificateId", provisionalCertificateId));
        var printSubjects = subjectRows.AsEnumerable().Select(row => GetString(row, "SubjectName"))
            .Where(name => !string.IsNullOrWhiteSpace(name)).ToList();
        if (subjectRows.Rows.Count > 0)
            printSubjects.AddRange(GetString(subjectRows.Rows[0], "OtherSubjects").Split(',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        ShowReport(FindForm(), "Provisional Certificate", reportPath, "ProvisionalCertificateData", data, printSubjects);
    }
    private void RefreshCertificates()
    {
        if (creatingCertificate) return;
        if (HasCertificateEdits && !Confirm("Discard the unsaved changes and refresh certificates?", "Refresh Certificates")) return;
        if (student.SelectedId <= 0) return;
        try {CertificateCollectionFlow.RefreshStudent(student);}catch(Exception ex){Fail(ex);return;}
        int selectedId = selectedProvisionalCertificateId;
        LoadCertificates(student.SelectedId);
        if (selectedId > 0) SelectSavedCertificate(selectedId);
    }
    private void ClearCertificateFields()
    {
        examType.SelectedIndex = -1;
        examYear.Clear();
        boardName.Text = "B.I.S.E Peshawar";
        rollNumber.Clear();
        marksObtained.Clear();
        marksOutOf.Clear();
        grade.SelectedIndex = -1;
        otherSubjects.Clear();
        certificateDate.Value =
            DateTime.Today;
        UncheckAllSubjects();
        newContentSnapshot = ContentSnapshot();
    }
    private void ClearCertificateSelection()
    {
        selectedProvisionalCertificateId = 0;
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
        if (HasCertificateEdits && !Confirm("Discard the unsaved changes and choose another student?", "New")) return;
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
            editorValidator.Dispose();
            Grid.CellMouseClick -=
                Grid_CellMouseClick;
            subjectsList.ItemCheck -=
                SubjectsList_ItemCheck;
            subjectsList.Dispose();
        }
        base.Dispose(disposing);
    }
    private bool IsPeshawarBoard => boardName.Text.Contains("Peshawar", StringComparison.OrdinalIgnoreCase);

    internal static string StandardGrade(string value)=>value.Trim().ToUpperInvariant() switch {
        "A1" or "A++"=>"A+", "B++" or "B+"=>"B", "C+"=>"C", "U"=>"F",
        "A+"=>"A+", "A"=>"A", "B"=>"B", "C"=>"C", "D"=>"D", "E"=>"E", "F"=>"F", _=>""
    };

    private void CalculateGrade()
    {
        const System.Globalization.NumberStyles style = System.Globalization.NumberStyles.None;
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        if (!int.TryParse(marksObtained.Text, style, culture, out int obtained) ||
            !int.TryParse(marksOutOf.Text, style, culture, out int total) ||
            total <= 0 || obtained > total || examType.SelectedIndex < 0 ||
            !int.TryParse(examYear.Text, style, culture, out int year))
        {
            grade.SelectedIndex = -1;
            return;
        }

        decimal percentage = obtained * 100m / total;
        // IBCC's revised scale starts with Part I in 2026 and Part II in 2027.
        bool revised = year >= (examType.SelectedIndex == 1 ? 2026 : 2027);
        string calculatedGrade = revised
            ? percentage switch {
                >= 96m => "A++", >= 91m => "A+", >= 86m => "A",
                >= 81m => "B++", >= 76m => "B+", >= 71m => "B",
                >= 61m => "C+", >= 51m => "C", >= 40m => "D", _ => "U"
            }
            : percentage switch {
                >= 80m => "A1", >= 70m => "A", >= 60m => "B",
                >= 50m => "C", >= 40m => "D", >= 33m => "E", _ => "F"
            };
        grade.Text = StandardGrade(calculatedGrade);
    }

    private bool CheckEditor(bool showMissing)
    {
        if (validatingEditor) return false;
        validatingEditor = true;
        try
        {
            editorValidator.Begin();
            bool valid = true;
            void Check(Control control, bool okay, string message)
            {
                valid &= okay;
                if (showMissing || !string.IsNullOrWhiteSpace(control.Text))
                    editorValidator.Check(control, okay, message);
            }
            bool Whole(string text, out int value) => int.TryParse(text,
                System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out value);
            Check(examType, examType.SelectedIndex >= 0, "Choose the examination.");
            bool yearOkay = Whole(examYear.Text, out int year) && year >= 1990 && year <= certificateDate.Value.Year;
            DateTime? birth = student.SelectedRow is DataRow studentRow ? GetDate(studentRow, "DateOfBirth") : null;
            if (birth.HasValue) yearOkay &= year > birth.Value.Year;
            Check(examYear, yearOkay, "Exam year must follow the student's birth year and not exceed the certificate year.");
            Check(boardName, !string.IsNullOrWhiteSpace(boardName.Text), "Enter the board name.");
            string roll = rollNumber.Text.Trim();
            Check(rollNumber, roll.Length > 0 && (!IsPeshawarBoard ||
                (roll.All(char.IsAsciiDigit) && roll.Any(character => character != '0'))), "Enter the numeric roll number exactly as shown on the board slip.");
            bool obtainedOkay = Whole(marksObtained.Text, out int obtained);
            bool totalOkay = Whole(marksOutOf.Text, out int total) && total > 0;
            Check(marksOutOf, totalOkay && (!obtainedOkay || total >= obtained), "Total marks must be greater than zero and cannot be less than marks obtained.");
            Check(marksObtained, obtainedOkay && (!totalOkay || obtained <= total), "Enter whole-number marks, not exceeding the total.");
            string resultGrade = grade.Text.Trim().ToUpperInvariant();
            Check(grade, resultGrade.Length > 0 && grade.Items.Contains(resultGrade), "Enter valid marks to calculate the grade.");
            Check(certificateDate, certificateDate.Value.Date <= DateTime.Today, "Certificate date cannot be in the future.");
            valid &= subjectsList.CheckedItems.Count > 0 || !string.IsNullOrWhiteSpace(otherSubjects.Text);
            return showMissing ? editorValidator.Finish() && valid : valid;
        }
        finally { validatingEditor = false; }
    }

    /// <summary>Opt-in workspace bridge: use the same selection and saved-copy logic as this editor.</summary>
    void IStudentCertificateWorkspaceEditor.LoadWorkspaceSelection(DataRow selectedStudent, int recordId, int issueId)
    {
        if (IsDisposed || selectedStudent.Table is null ||
            !selectedStudent.Table.Columns.Contains("AdmissionId") ||
            CertificateUi.GetInt(selectedStudent, "AdmissionId") <= 0)
            throw new ArgumentException("A valid selected student is required.", nameof(selectedStudent));
        BindSubjects();
        student.SetStudent(selectedStudent);
        StudentChanged();
        if (recordId > 0)
        {
            if (!certificateResults.AsEnumerable().Any(row =>
                CertificateUi.GetInt(row, "ProvisionalCertificateId") == recordId))
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
