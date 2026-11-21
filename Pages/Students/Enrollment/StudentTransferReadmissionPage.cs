using FMSC_SMS.Core;
namespace FMSC_SMS.Pages.Students.Enrollment;
public sealed class StudentTransferReadmissionPage : V2Page
{
    private const int OperationButtonHorizontalMargin = 2;
        /* Each dialog fixes its operation while reusing lifecycle validation and persistence. */
    private ModernButton? executeButton;
    private readonly StudentPicker student = new();
    private readonly LookupBox enrollment = new();
    private bool suppressDestinationEvents;
    private DataTable enrollmentsRaw = new();
    private string selectedStudentStatus = string.Empty;
    private bool selectedStudentOfficial;
    private bool selectedStudentHeld;
    private DataTable destinationClasses = new();
    private DataTable destinationSections = new();
    private DataTable destinationCampuses = new();
    private readonly ModernTextBox currentCampusDisplay =
        HumanUi.TextBox(false, 100);
    private readonly ModernTextBox currentClassDisplay =
        HumanUi.TextBox(false, 100);
    private readonly ModernTextBox currentSectionDisplay =
        HumanUi.TextBox(false, 100);
    private int currentEnrollmentCampusId;
    private int currentEnrollmentClassId;
    private int currentEnrollmentSectionId;
    private int currentEnrollmentClassSectionId;
    private readonly LookupBox campus = new();
    private readonly LookupBox year = new();
    private DateTime? readmissionYearStart;
    private readonly LookupBox cls = new();
    private readonly LookupBox classSection = new();
    private readonly ModernComboBox readmissionNumberChoice = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ModernTextBox currentAdmissionNumber = HumanUi.TextBox(false, 40);
    private readonly Panel readmissionNumberField;
    private readonly Panel currentAdmissionNumberField;
    private readonly ModernTextBox probableAdmissionNumber = HumanUi.TextBox(false, 40);
    private readonly Panel probableAdmissionNumberField;
    private int numberPreviewVersion;
    private readonly CheckBox reAdmitTransport = new ModernCheckBox {
        Text = "Uses Transport",
        AutoSize = true,
        Checked = false,
        Margin = new Padding(0, 6, 0, 0)
    };
    private readonly ModernButton opTransferSection =
        new() {
            Text = "Transfer Section",
            Image = UiHelper.CreateGlyphIcon("⇄", AppTheme.Primary, 17),
            ImageAlign = ContentAlignment.MiddleLeft,
            TextImageRelation = TextImageRelation.ImageBeforeText,
            Dock = DockStyle.Fill,
            ThemeRole = "TabInactive",
            Margin = new Padding(OperationButtonHorizontalMargin, 0, OperationButtonHorizontalMargin, 0)
        };
    private readonly ModernButton opTransferClass =
        new() {
            Text = "Transfer Class",
            Image = UiHelper.CreateGlyphIcon("↑", AppTheme.Primary, 17),
            ImageAlign = ContentAlignment.MiddleLeft,
            TextImageRelation = TextImageRelation.ImageBeforeText,
            Dock = DockStyle.Fill,
            ThemeRole = "TabInactive",
            Margin = new Padding(OperationButtonHorizontalMargin, 0, OperationButtonHorizontalMargin, 0)
        };
    private readonly ModernButton opTransferCampus =
        new() {
            Text = "Transfer Campus",
            Image = UiHelper.CreateGlyphIcon("⌂", AppTheme.Primary, 17),
            ImageAlign = ContentAlignment.MiddleLeft,
            TextImageRelation = TextImageRelation.ImageBeforeText,
            Dock = DockStyle.Fill,
            ThemeRole = "TabInactive",
            Margin = new Padding(OperationButtonHorizontalMargin, 0, OperationButtonHorizontalMargin, 0)
        };
    private readonly ModernButton opReAdmit =
        new() {
            Text = "Re-Admit",
            Image = UiHelper.CreateGlyphIcon("↻", AppTheme.Primary, 17),
            ImageAlign = ContentAlignment.MiddleLeft,
            TextImageRelation = TextImageRelation.ImageBeforeText,
            Dock = DockStyle.Fill,
            ThemeRole = "TabInactive",
            Margin = new Padding(OperationButtonHorizontalMargin, 0, OperationButtonHorizontalMargin, 0)
        };
    private readonly TableLayoutPanel operationBar =
        new() {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1
        };
    private string selectedOperation = "Transfer Section";
    private bool isExecuting;
    private readonly string? fixedOperation;
    public bool IsExecuting => isExecuting;
    public event EventHandler? OperationCompleted;
    private readonly Label placementSummary = new() {
        Dock = DockStyle.Fill, AutoSize = true, ForeColor = AppTheme.MutedText,
        Padding = new Padding(0, 4, 0, 8)
    };
    private readonly ModernDatePicker date = HumanUi.Date();
    private readonly ModernTextBox remarks =
        HumanUi.TextBox(false, 500);
    private readonly Panel operationField;
    private readonly Panel currentCampusField;
    private readonly Panel currentClassField;
    private readonly Panel currentSectionField;
    private readonly Panel campusField;
    private readonly Panel yearField;
    private readonly Panel clsField;
    private readonly Panel classSectionField;
    private readonly Panel dateField;
    private readonly Panel remarksField;
    private readonly Panel reAdmitTransportField;
    private readonly TableLayoutPanel actionFieldsTable =
        new() {
            Dock = DockStyle.Fill,
            ColumnCount = 3
        };
    private readonly TableLayoutPanel root =
        new() {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3
        };
    private static int ActionRowHeight =>
        AppMetrics.FieldRowHeight;
    private static int ActionCardChrome =>
        AppMetrics.CardHeaderHeight() +
        AppMetrics.CardContentVerticalPadding;
    private IEnumerable<(string Name, ModernButton Button)> OperationButtons
    {
        get
        {
            yield return ("Transfer Section", opTransferSection);
            yield return ("Transfer Class", opTransferClass);
            yield return ("Transfer Campus", opTransferCampus);
            yield return ("Re-Admit", opReAdmit);

        }
    }
    public StudentTransferReadmissionPage(Action<Func<Control>, string, string>? navigate = null,
        int initialAdmissionId = 0, string? initialStudentDisplay = null, string? initialOperation = null)
        : base(
            PageTitles.StudentTransfers,
            "Transfer students between classes or campuses, or readmit returning students.")
    {
        fixedOperation = initialAdmissionId > 0 ? initialOperation : null;
        if (fixedOperation is not null) selectedOperation = fixedOperation;
        readmissionNumberChoice.Items.AddRange(new object[] {
            "Keep current admission number", "Allot new admission number automatically"
        });
        readmissionNumberChoice.SelectedIndex = 0;
        currentAdmissionNumber.ReadOnly = true;
        currentAdmissionNumber.TabStop = false;
        currentAdmissionNumber.BackColor = AppTheme.SurfaceSecondary;
        UiAccessibility.Describe(readmissionNumberChoice, "Readmission admission number",
            "Keep the current admission number or assign a new one. The student record and registration number stay the same.");
        currentCampusDisplay.ReadOnly = true;
        currentCampusDisplay.BackColor = AppTheme.SurfaceSecondary;
        currentClassDisplay.ReadOnly = true;
        currentClassDisplay.BackColor = AppTheme.SurfaceSecondary;
        currentSectionDisplay.ReadOnly = true;
        currentSectionDisplay.BackColor = AppTheme.SurfaceSecondary;
        currentCampusDisplay.TabStop = false;
        currentClassDisplay.TabStop = false;
        currentSectionDisplay.TabStop = false;
        for (int i = 0; i < 4; i++)
            operationBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));
        int col = 0;
        foreach (var (name, button) in OperationButtons)
        {
            button.TabIndex = col;
            operationBar.Controls.Add(button, col++, 0);
            button.Click += (_, _) => SelectOperation(name);
        }
        /* each operation has its own permission behind its button, so they are gated individually rather than by the page. */
        GateAction(opTransferSection, "Enrollment.Manage");
        GateAction(opTransferClass, "Enrollment.Manage");
        GateAction(opTransferCampus, "Enrollment.Manage");
        GateAction(opReAdmit, "Students.Administer");
        /* Execute runs a transfer, promotion or re-admission - each of which rewrites enrollments - so it is guarded against a double submit. */
        executeButton =
            AddBusyAction(ExecuteActionLabel(selectedOperation), ExecuteAsync, "Success");
        var clear =
            AddAction(fixedOperation is null ? "Clear" : "Cancel", (_, _) => {
                if (fixedOperation is not null) { FindForm()?.Close(); return; }
                ClearForm(initialAdmissionId <= 0);
                if (initialAdmissionId > 0) LoadEnrollments();
            }, "Secondary");
        var feeHistory=AddBusyAction("Fee Change History",async () => {
            try { await FMSC_SMS.Pages.Reports.Finance.StudentFeeChangeHistory.ShowAsync(this,() => student.SelectedId); }
            catch(Exception ex) { if(!IsDisposed) Fail(ex); }
        },"Secondary");
        void UpdateFeeHistory() => feeHistory.Enabled=student.SelectedId>0
            && CurrentUser.HasPermission("Fees.View") && CurrentUser.HasPermission("Fees.Statement.View");
        student.SelectionCleared += (_, _) => UpdateFeeHistory();
        student.UserSelectionChanged += (_, _) => UpdateFeeHistory();
        UpdateFeeHistory();
        /* Execute runs whichever operation is currently selected, so it is offered when the user holds either permission. */
        GateActionAny(executeButton, "Enrollment.Manage", "Students.Administer");
        RelocateFromActions(executeButton, clear);
        Defer(() => {
            BindCampuses();
            BindYears();
            LoadClasses();
            UpdateVisibility();
            if (initialAdmissionId > 0)
            {
                student.SetStudent(initialAdmissionId, initialStudentDisplay ?? AdmissionNumbers.ForId(initialAdmissionId));
                student.Enabled = false;
                LoadEnrollments();
                if (OperationButtons.Any(x => x.Name == initialOperation && x.Button.Enabled))
                    SelectOperation(initialOperation!);
            }
        });
        student.PlaceholderText = StudentPicker.SearchPlaceholder;
        StudentSearchCache.Warm();
        student.SearchFailed += ex => Fail(ex);
        student.MatchesChanged += (_, _) => {
            if (student.Text.Trim().Length == 0 || student.SelectedId > 0)
                return;
            SetStatus(
                student.MatchCount == 0
                    ? "No student matches what is typed."
                    : student.MatchesCapped
                        ? "Many students match - keep typing to narrow the list."
                        : $"{student.MatchCount} student(s) found. Click one, or use Up/Down and Enter.");
        };
        /* The operator edited away from the student whose enrollments and placement are on screen (or emptied the box). */
        student.SelectionCleared += (_, _) => {
            ClearForm(false);
        };
        student.UserSelectionChanged += (_, _) => {
            if (student.SelectedId <= 0)
                return;
            ResetOperationFields();
            LoadEnrollments();
        };
        enrollment.SelectedIndexChanged += (_, _) => {
            LoadCurrentPlacement();
            placementSummary.Text = $"{student.Text}\nCurrent placement: {currentCampusDisplay.Text} / {currentClassDisplay.Text} / {currentSectionDisplay.Text}";
            UpdateAvailableOperations();
        };
        campus.SelectedIndexChanged += (_, _) => {
            if (suppressDestinationEvents)
                return;
            LoadClasses();
            LoadClassSections();
        };
        cls.SelectedIndexChanged += (_, _) => {
            if (suppressDestinationEvents)
                return;
            LoadClassSections();
        };
        operationField =
            HumanUi.Field("Operation", operationBar, true);
        operationField.Dock =
            DockStyle.Top;
        operationField.Height =
            ActionRowHeight;
        currentCampusField =
            HumanUi.Field("Current Campus", currentCampusDisplay);
        currentClassField =
            HumanUi.Field("Current Class", currentClassDisplay);
        currentSectionField =
            HumanUi.Field("Current Section", currentSectionDisplay);
        campusField =
            HumanUi.Field("New Campus", campus, true);
        yearField =
            HumanUi.Field("New Academic Year", year, true);
        clsField =
            HumanUi.Field("New Class", cls, true);
        classSectionField =
            HumanUi.Field("New Class & Section", classSection, true);
        currentAdmissionNumberField = HumanUi.Field("Current Admission Number", currentAdmissionNumber);
        readmissionNumberField = HumanUi.Field("Admission Number on Readmission", readmissionNumberChoice, true);
        probableAdmissionNumber.ReadOnly = true;
        probableAdmissionNumber.TabStop = false;
        probableAdmissionNumber.BackColor = AppTheme.SurfaceSecondary;
        probableAdmissionNumberField = HumanUi.Field("Probable New Admission Number", probableAdmissionNumber);
        UiAccessibility.Describe(probableAdmissionNumber, "Probable new admission number",
            "Preview only. Number assigned on save; another admission may change it.");
        readmissionNumberChoice.SelectedIndexChanged += async (_, _) => {
            UpdateVisibility();
            await RefreshAdmissionNumberPreviewAsync();
        };
        dateField =
            HumanUi.Field("Effective Date", date, true);
        remarksField =
            HumanUi.Field("Remarks", remarks);
        reAdmitTransportField =
            HumanUi.Field("Transport", reAdmitTransport);
        for (int i = 0; i < 3; i++)
        {
            actionFieldsTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        }
        var studentCard = HumanUi.Card(
            "Student",
            "Type an Admission Number or name. Students who have left are included (needed for Re-Admit).");
        studentCard.Content.Controls.Add(BuildStudentContext());
        var actionCard = HumanUi.Card(
            fixedOperation ?? "Lifecycle Action",
            fixedOperation is null
                ? "Only the actions valid for the selected placement are enabled, with just the fields each one needs."
                : fixedOperation == "Re-Admit" ? "Choose the returning student's placement and admission number."
                : "Choose the destination and effective date.");
        actionCard.Content.Controls.Add(actionFieldsTable);
        actionCard.Content.Controls.Add(operationField);
        var actionButtonRow =
            HumanUi.ButtonRow(executeButton, clear);
        actionButtonRow.Dock =
            DockStyle.Bottom;
        actionCard.Content.Controls.Add(actionButtonRow);
        operationField.TabIndex = 0;
        actionFieldsTable.TabIndex = 1;
        actionButtonRow.TabIndex = 2;
        var history = HumanUi.Card("Academic Placement History", "Academic history remains attached to the same placement for transfers.");
        history.Content.Controls.Add(Grid);
        root.RowStyles.Add(
            new RowStyle(SizeType.Absolute, AppMetrics.FieldCardHeight(1)));
        root.RowStyles.Add(
            new RowStyle(
                SizeType.Absolute,
                ActionCardChrome +
                ActionRowHeight +
                5 * ActionRowHeight +
                AppMetrics.ButtonRowHeight));
        root.RowStyles.Add(
            new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(studentCard, 0, 0);
        root.Controls.Add(actionCard, 0, 1);
        root.Controls.Add(history, 0, 2);
        studentCard.TabIndex = 0;
        actionCard.TabIndex = 1;
        history.TabIndex = 2;
        SetContent(
            root,
            "Transfers stay in their recorded year; readmission follows a recorded departure. Review before confirming.");
        if (fixedOperation is not null)
        {
            PrepareForEmbedding();
            root.RowStyles[0].SizeType = SizeType.AutoSize;
            root.RowStyles[1].SizeType = SizeType.Percent;
            root.RowStyles[1].Height = 100;
            root.RowStyles[2].SizeType = SizeType.Absolute;
            root.RowStyles[2].Height = 0;
            history.Visible = false;
            studentCard.Visible = false;
            placementSummary.MaximumSize = new Size(AppScale.Scale(660), 0);
            root.Controls.Add(placementSummary, 0, 0);
            root.SetCellPosition(studentCard, new TableLayoutPanelCellPosition(0, 2));
            operationField.Visible = false;
            actionCard.Content.Controls.Remove(operationField);
            studentCard.Content.Controls.Add(operationField);
            actionCard.Content.Controls.Remove(actionButtonRow);
            var footer = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 1, RowCount = 2 };
            StatusLabel.Dock = DockStyle.Fill;
            StatusLabel.AutoSize = true;
            StatusLabel.MaximumSize = new Size(AppScale.Scale(660), 0);
            footer.Controls.Add(StatusLabel, 0, 0);
            actionButtonRow.Dock = DockStyle.Fill;
            footer.Controls.Add(actionButtonRow, 0, 1);
            actionCard.Content.Controls.Add(footer);
            actionFieldsTable.AutoScroll = true;
            actionFieldsTable.ColumnCount = 2;
            actionFieldsTable.ColumnStyles.Clear();
            actionFieldsTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            actionFieldsTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        }
        ClearForm();
    }
    private Control BuildStudentContext()
    {
        var t = new TableLayoutPanel {
            Dock = DockStyle.Fill,
            ColumnCount = 2
        };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        t.Controls.Add(
            HumanUi.Field("Student", student, true), 0, 0);
        t.Controls.Add(
            HumanUi.Field("Current Academic Placement", enrollment, true), 1, 0);
        foreach (Control field in t.Controls)
            field.TabIndex = t.GetColumn(field);
        return t;
    }
    private void RebuildActionGrid(IReadOnlyList<Control> fields)
    {
        actionFieldsTable.SuspendLayout();
        actionFieldsTable.Controls.Clear();
        actionFieldsTable.RowStyles.Clear();
        actionFieldsTable.RowCount = 0;
        int fieldIndex = 0;
        while (fieldIndex < fields.Count)
        {
            int row =
                actionFieldsTable.RowCount++;
            actionFieldsTable.RowStyles.Add(
                new RowStyle(SizeType.Absolute, ActionRowHeight));
            var rowFields =
                fields
                    .Skip(fieldIndex)
                    .Take(actionFieldsTable.ColumnCount)
                    .ToArray();
            fieldIndex += rowFields.Length;
            for (int column = 0;
                 column < rowFields.Length;
                 column++)
            {
                // Reused fields retain their previous indexes when the operation changes.
                // Follow the current visual order across each row, then down.
                rowFields[column].TabIndex = row * actionFieldsTable.ColumnCount + column;
                actionFieldsTable.SetColumnSpan(rowFields[column], 1);
                actionFieldsTable.SetRowSpan(rowFields[column], 1);
                actionFieldsTable.Controls.Add(rowFields[column], column, row);
                if (column == rowFields.Length - 1 &&
                    rowFields.Length < actionFieldsTable.ColumnCount)
                {
                    actionFieldsTable.SetColumnSpan(rowFields[column], actionFieldsTable.ColumnCount - column);
                }
            }
        }
        actionFieldsTable.ResumeLayout(true);
        if (fixedOperation is null) root.RowStyles[1].Height =
            ActionCardChrome +
            ActionRowHeight +
            actionFieldsTable.RowCount *
            ActionRowHeight +
            AppMetrics.ButtonRowHeight;
    }
    private void BindCampuses()
    {
        try
        {
            campus.Bind(
                DatabaseHelper.QueryUser("GetCampuses"), "CampusId", "CampusName");
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }
    private void BindYears()
    {
        try
        {
            var allYears = DatabaseHelper.QueryUser("GetAcademicYears");
            var currentYears = allYears.Clone();
            var today = DateTime.Today;
            foreach (DataRow candidate in allYears.Rows)
            {
                if (candidate.Field<bool>("IsCurrent")
                    && candidate.Field<DateTime>("StartDate").Date <= today
                    && candidate.Field<DateTime>("EndDate").Date >= today)
                    currentYears.ImportRow(candidate);
            }
            // Only current operational returns belong in Re-Admit.
            // Earlier academic years are recorded through historical placement correction.
            readmissionYearStart = currentYears.Rows.Count > 0
                ? currentYears.AsEnumerable().Min(row => row.Field<DateTime>("StartDate").Date)
                : null;
            year.Bind(currentYears, "AcademicYearId", "YearName");
            if (selectedOperation == "Re-Admit") SetReadmissionDateBounds();
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }
    private void LoadEnrollments(string? completionMessage = null)
    {
        currentAdmissionNumber.Text = student.SelectedId > 0 ? AdmissionNumbers.ForId(student.SelectedId) : string.Empty;
        enrollment.ClearLookup();
        enrollmentsRaw = new DataTable();
        selectedStudentStatus = string.Empty;
        selectedStudentOfficial = false;
        selectedStudentHeld = false;
        if (student.SelectedId <= 0)
            return;
        try
        {
            var raw =
                DatabaseHelper.QueryUser(
                    "GetStudentEnrollmentsForLifecycle",
                    DatabaseHelper.Int("@AdmissionId", student.SelectedId));
            enrollmentsRaw = raw;
            if (raw.Rows.Count > 0)
            {
                selectedStudentStatus = Convert.ToString(raw.Rows[0]["StudentStatus"]) ?? string.Empty;
                selectedStudentOfficial = raw.Rows[0]["HasOfficialAdmissionNumber"] is bool official && official;
                selectedStudentHeld = raw.Rows[0]["IsOnHold"] is bool held && held;
            }
            var dt = new DataTable();
            dt.Columns.Add("EnrollmentId", typeof(int));
            dt.Columns.Add("Display", typeof(string));
            foreach (DataRow row in raw.Rows)
            {
                dt.Rows.Add(
                    Convert.ToInt32(row["EnrollmentId"]), FormatEnrollmentDisplay(row));
            }
            enrollment.Bind(dt, "EnrollmentId", "Display");
            if (raw.Rows.Count > 0)
            {
                var preferred = fixedOperation is not null && fixedOperation != "Re-Admit"
                    ? raw.AsEnumerable().FirstOrDefault(row => Convert.ToString(row["Status"]) == "Active")
                    : raw.Rows[0];
                enrollment.SelectedValue = Convert.ToInt32((preferred ?? raw.Rows[0])["EnrollmentId"]);
            }
            Bind(raw);
            UpdateAvailableOperations();
            if (completionMessage is not null) Info(completionMessage);
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }
    private bool CanReadmitSelected() => AdmissionWorkflowActions.CanReadmit(
        selectedStudentStatus, selectedStudentOfficial,
        enrollmentsRaw.AsEnumerable().Any(row =>
            string.Equals(Convert.ToString(row["Status"]),"Active",StringComparison.OrdinalIgnoreCase)),
        selectedStudentHeld);

    private static string FormatEnrollmentDisplay(DataRow row)
    {
        string year =
            Convert.ToString(
                row["YearName"]) ??
            string.Empty;
        string className =
            Convert.ToString(
                row["ClassName"]) ??
            string.Empty;
        string section =
            Convert.ToString(
                row["SectionName"]) ??
            string.Empty;
        string status =
            Convert.ToString(
                row["Status"]) ??
            string.Empty;
        string enrolledOn =
            row["EnrollmentDate"] is DateTime enrollmentDate
                ? enrollmentDate.ToString("dd-MMM-yyyy")
                : string.Empty;
        return
            $"{year} — {className} {section} " +
            $"({status}, since {enrolledOn})";
    }
    private void UpdateAvailableOperations()
    {
        string? status =
            SelectedEnrollmentStatus();
        HashSet<string> enabled;
        if (student.SelectedId <= 0)
        {
            enabled = new HashSet<string>
            {
                "Transfer Section",
                "Transfer Class",
                "Transfer Campus",
                "Re-Admit"
            };
        }
        else if (status == "Active")
        {
            enabled = new HashSet<string>
            {
                "Transfer Section",
                "Transfer Class",
                "Transfer Campus"
            };
        }
        else
        {
            enabled = new HashSet<string>
            {
                "Re-Admit"
            };
        }
        if (!CurrentUser.HasPermission("Enrollment.Manage"))
            enabled.RemoveWhere(operation => operation.StartsWith("Transfer", StringComparison.Ordinal));
        if (!CurrentUser.HasPermission("Students.Administer"))
            enabled.Remove("Re-Admit");
        if (!CanReadmitSelected())
            enabled.Remove("Re-Admit");

        foreach (var (name, button)
                 in OperationButtons)
        {
            button.Enabled =
                enabled.Contains(name) && (fixedOperation is null || name == fixedOperation);
        }
        if (fixedOperation is not null)
        {
            SelectOperation(fixedOperation);
            if (executeButton is not null) executeButton.Enabled = enabled.Contains(fixedOperation);
            if (!enabled.Contains(fixedOperation))
                SetStatus(fixedOperation == "Re-Admit"
                    ? "Readmission requires an officially admitted former student with no active enrollment or hold."
                    : "Transfers require an active placement.");
            return;
        }
        if (executeButton is not null) executeButton.Enabled = enabled.Count > 0;
        SelectOperation(enabled.Contains(selectedOperation)
                ? selectedOperation
                : enabled.FirstOrDefault() ?? selectedOperation);
    }
    /* The primary action's caption names the operation it will run. */
    private static string ExecuteActionLabel(string operation)
    {
        return string.IsNullOrWhiteSpace(operation)
            ? "Execute"
            : operation == "Re-Admit" ? "Readmit Student" : operation;
    }
    private void SelectOperation(string name)
    {
        string previousOperation = selectedOperation;
        selectedOperation = name;
        foreach (var (candidateName, button)
                 in OperationButtons)
        {
            TabStrip.ApplyButtonTheme(button, candidateName == name);
        }
        if (executeButton is not null)
        {
            executeButton.Text = ExecuteActionLabel(name);
            /* The row's buttons were sized from their build-time captions, so the caption has to be re-fitted here or the longer operation names are clipped. */
            AppMetrics.FitButtonWidth(executeButton, minWidth: 122);
        }
        UpdateVisibility();
        /* The destination lists are derived FROM the operation: LoadDestinationLists excludes the student's current campus / class / section for every operation except Re-Admit (1243-1244). */
        if (!string.Equals(previousOperation, name, StringComparison.Ordinal))
        {
            if(name!="Re-Admit"&&enrollment.SelectedId>0)LoadCurrentPlacement();
            else LoadDestinationLists();
        }
    }
    private string? SelectedEnrollmentStatus()
    {
        if (enrollment.SelectedId <= 0 ||
            !enrollmentsRaw.Columns.Contains(
                "EnrollmentId") ||
            !enrollmentsRaw.Columns.Contains("Status"))
        {
            return null;
        }
        foreach (DataRow row in enrollmentsRaw.Rows)
        {
            if (Convert.ToInt32(
                    row["EnrollmentId"]) ==
                enrollment.SelectedId)
            {
                return Convert.ToString(row["Status"]);
            }
        }
        return null;
    }
    private void LoadCurrentPlacement()
    {
        currentEnrollmentCampusId = 0;
        currentEnrollmentClassId = 0;
        currentEnrollmentSectionId = 0;
        currentEnrollmentClassSectionId = 0;
        if (enrollment.SelectedId <= 0)
        {
            currentCampusDisplay.Text =
                string.Empty;
            currentClassDisplay.Text =
                string.Empty;
            currentSectionDisplay.Text =
                string.Empty;
            return;
        }
        try
        {
            DataRow? row = enrollmentsRaw?.AsEnumerable()
                .FirstOrDefault(r => Convert.ToInt32(r["EnrollmentId"]) == enrollment.SelectedId);
            if (row is not null)
            {
                if (fixedOperation != "Re-Admit" && selectedOperation != "Re-Admit"
                    && row.Table.Columns.Contains("PlacementYearEnd") && row["PlacementYearEnd"] is DateTime end
                    && row["EnrollmentDate"] is DateTime start)
                {
                    date.MinDate=start.Date;
                    date.MaxDate=end.Date<DateTime.Today?end.Date:DateTime.Today;
                    date.Value=date.MaxDate;
                    date.ShowCheckBox=end.Date<DateTime.Today;
                    date.Checked=!date.ShowCheckBox;
                }
                currentCampusDisplay.Text =
                    Convert.ToString(row["CampusName"]);
                currentClassDisplay.Text =
                    Convert.ToString(row["ClassName"]);
                currentSectionDisplay.Text =
                    Convert.ToString(row["SectionName"]);
                currentEnrollmentCampusId =
                    Convert.ToInt32(row["CampusId"]);
                currentEnrollmentClassId =
                    Convert.ToInt32(row["ClassId"]);
                currentEnrollmentSectionId =
                    Convert.ToInt32(row["SectionId"]);
                currentEnrollmentClassSectionId =
                    Convert.ToInt32(row["ClassSectionId"]);
                LoadDestinationLists();
            }
            else
            {
                currentCampusDisplay.Text =
                    string.Empty;
                currentClassDisplay.Text =
                    string.Empty;
                currentSectionDisplay.Text =
                    string.Empty;
            }
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }
    private static bool IsCurrentPlacement(DataRow? row) => row is not null
        && row.Table.Columns.Contains("PlacementYearStart") && row.Table.Columns.Contains("PlacementYearEnd")
        && row["PlacementYearStart"] is DateTime start && row["PlacementYearEnd"] is DateTime end
        && DateTime.Today >= start.Date && DateTime.Today <= end.Date;
    private bool RequiresActivePlacementChoices => selectedOperation == "Re-Admit" || IsCurrentPlacement(
        enrollmentsRaw.AsEnumerable().FirstOrDefault(row => Convert.ToInt32(row["EnrollmentId"]) == enrollment.SelectedId));

    private void LoadDestinationLists()
    {
        destinationCampuses = new DataTable();
        destinationClasses = new DataTable();
        destinationSections = new DataTable();
        if (currentEnrollmentCampusId <= 0 ||
            currentEnrollmentClassId <= 0)
        {
            return;
        }
        try
        {
            bool excludeCurrent =
                selectedOperation != "Re-Admit";
            var campuses =
                DatabaseHelper.QueryUser("GetCampuses");
            if (excludeCurrent &&
                campuses.Columns.Contains("CampusId"))
            {
                var rows =
                    campuses.AsEnumerable()
                        .Where(row =>
                            Convert.ToInt32(
                                row["CampusId"]) !=
                            currentEnrollmentCampusId);
                destinationCampuses =
                    rows.Any()
                        ? rows.CopyToDataTable()
                        : campuses.Clone();
            }
            else
            {
                destinationCampuses =
                    campuses;
            }
            var classes =
                DatabaseHelper.QueryUser("GetClasses");
            if (selectedOperation == "Transfer Class")
            {
                using var rawAssignments = DatabaseHelper.QueryUser("GetClassSections",
                    DatabaseHelper.Int("@CampusId",currentEnrollmentCampusId),DatabaseHelper.Int("@ClassId",0),DatabaseHelper.Int("@SectionId",0));
                using var assignments = RequiresActivePlacementChoices ? ActiveClassSectionPolicy.OnlyActive(rawAssignments) : rawAssignments.Copy();
                using var assigned = ActiveClassSectionPolicy.ClassesWithAssignments(classes,assignments);
                destinationClasses = assigned.Clone();
                foreach (var row in assigned.AsEnumerable().Where(row=>Convert.ToInt32(row["ClassId"])!=currentEnrollmentClassId))
                    destinationClasses.ImportRow(row);
            }
            else if (excludeCurrent &&
                classes.Columns.Contains("ClassId"))
            {
                var rows =
                    classes.AsEnumerable()
                        .Where(row =>
                            Convert.ToInt32(
                                row["ClassId"]) !=
                            currentEnrollmentClassId);
                destinationClasses =
                    rows.Any()
                        ? rows.CopyToDataTable()
                        : classes.Clone();
            }
            else
            {
                destinationClasses = classes.Clone(); // Choose the destination campus first.
            }
            using var rawSections =
                DatabaseHelper.QueryUser(
                    "GetClassSections",
                    DatabaseHelper.Int(
                        "@CampusId",
                        currentEnrollmentCampusId),
                    DatabaseHelper.Int(
                        "@ClassId",
                        currentEnrollmentClassId),
                    DatabaseHelper.Int("@SectionId", 0));
            using var sections = RequiresActivePlacementChoices ? ActiveClassSectionPolicy.OnlyActive(rawSections) : rawSections.Copy();
            if (excludeCurrent &&
                sections.Columns.Contains("SectionId"))
            {
                var rows =
                    sections.AsEnumerable()
                        .Where(row =>
                            Convert.ToInt32(
                                row["SectionId"]) !=
                            currentEnrollmentSectionId);
                destinationSections =
                    rows.Any()
                        ? rows.CopyToDataTable()
                        : sections.Clone();
            }
            else
            {
                destinationSections =
                    sections.Copy();
            }
            if (selectedOperation == "Re-Admit") destinationSections = sections.Clone();
            BindDestinationLookups();
            suppressDestinationEvents = true;
            try
            {
                BindYears();
            }
            finally
            {
                suppressDestinationEvents = false;
            }
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }
    private void BindDestinationLookups()
    {
        suppressDestinationEvents = true;
        try
        {
            campus.Bind(destinationCampuses, "CampusId", "CampusName");
            cls.Bind(destinationClasses, "ClassId", "ClassName");
            var dt = new DataTable();
            dt.Columns.Add("ClassSectionId", typeof(int));
            dt.Columns.Add("SectionId", typeof(int));
            dt.Columns.Add("Display", typeof(string));
            foreach (DataRow row in destinationSections.Rows)
            {
                dt.Rows.Add(
                    Convert.ToInt32(
                        row["ClassSectionId"]),
                    Convert.ToInt32(
                        row["SectionId"]),
                    Convert.ToString(row["SectionName"]));
            }
            classSection.Bind(dt, "ClassSectionId", "Display");
        }
        finally
        {
            suppressDestinationEvents = false;
        }
    }
    private void LoadClasses()
    {
        try
        {
            int selected = cls.SelectedId;
            cls.ClearLookup();
            if (campus.SelectedId <= 0) return;
            using var catalog = DatabaseHelper.QueryUser("GetClasses");
            using var raw = DatabaseHelper.QueryUser("GetClassSections",DatabaseHelper.Int("@CampusId",campus.SelectedId),
                DatabaseHelper.Int("@ClassId",0),DatabaseHelper.Int("@SectionId",0));
            using var assignments = RequiresActivePlacementChoices ? ActiveClassSectionPolicy.OnlyActive(raw) : raw.Copy();
            using var assigned = ActiveClassSectionPolicy.ClassesWithAssignments(catalog,assignments);
            suppressDestinationEvents = true;
            try {
                cls.Bind(assigned,"ClassId","ClassName");
                if (assigned.AsEnumerable().Any(row=>Convert.ToInt32(row["ClassId"])==selected)) cls.SelectedValue=selected;
            } finally { suppressDestinationEvents=false; }
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    private void LoadClassSections()
    {
        classSection.ClearLookup();
        if (campus.SelectedId <= 0 ||
            cls.SelectedId <= 0)
        {
            return;
        }
        try
        {
            var raw =
                DatabaseHelper.QueryUser(
                    "GetClassSections",
                    DatabaseHelper.Int(
                        "@CampusId",
                        campus.SelectedId),
                    DatabaseHelper.Int(
                        "@ClassId",
                        cls.SelectedId),
                    DatabaseHelper.Int("@SectionId", 0));
            var dt = new DataTable();
            dt.Columns.Add("ClassSectionId", typeof(int));
            dt.Columns.Add("SectionId", typeof(int));
            dt.Columns.Add("Display", typeof(string));
            using var available = RequiresActivePlacementChoices ? ActiveClassSectionPolicy.OnlyActive(raw) : raw.Copy();
            foreach (DataRow row in available.Rows)
            {
                dt.Rows.Add(
                    Convert.ToInt32(
                        row["ClassSectionId"]),
                    Convert.ToInt32(
                        row["SectionId"]),
                    $"{row["ClassName"]} — " +
                    $"{row["SectionName"]}");
            }
            classSection.Bind(dt, "ClassSectionId", "Display");
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }
    private (int ClassSectionId, string? Note)
        ResolveDestinationClassSection(int campusId, int classId, int currentSectionId, string destinationLabel, bool requireActive)
    {
        var exact =
            DatabaseHelper.QueryUser(
                "GetClassSections",
                DatabaseHelper.Int(
                    "@CampusId",
                    campusId),
                DatabaseHelper.Int(
                    "@ClassId",
                    classId),
                DatabaseHelper.Int("@SectionId", currentSectionId));
        if (requireActive) exact = ActiveClassSectionPolicy.OnlyActive(exact);
        if (exact.Rows.Count > 0)
        {
            return (
                Convert.ToInt32(exact.Rows[0]["ClassSectionId"]), null);
        }
        var anySection =
            DatabaseHelper.QueryUser(
                "GetClassSections",
                DatabaseHelper.Int(
                    "@CampusId",
                    campusId),
                DatabaseHelper.Int(
                    "@ClassId",
                    classId),
                DatabaseHelper.Int("@SectionId", 0));
        if (requireActive) anySection = ActiveClassSectionPolicy.OnlyActive(anySection);
        if (anySection.Rows.Count == 0)
            return (0, null);
        string sectionName =
            Convert.ToString(
                anySection.Rows[0]["SectionName"]) ??
            string.Empty;
        return (
            Convert.ToInt32(
                anySection.Rows[0]["ClassSectionId"]),
            $"Note: the student's current section " +
            $"wasn't available at {destinationLabel}, " +
            $"so they were placed in section " +
            $"{sectionName} instead.");
    }
    private void SetReadmissionDateBounds()
    {
        var today = DateTime.Today;
        var start = readmissionYearStart ?? DateTimePicker.MinimumDateTime;
        bool changed = date.ShowCheckBox || date.MinDate != start || date.MaxDate != today;
        date.ShowCheckBox = false;
        date.Checked = true;
        date.MaxDate = today;
        date.MinDate = start;
        if (changed || date.Value.Date < start || date.Value.Date > today)
            date.Value = today;
    }
    private void UpdateVisibility()
    {
        bool transferSection =
            selectedOperation == "Transfer Section";
        bool transferClass =
            selectedOperation == "Transfer Class";
        bool transferCampus =
            selectedOperation == "Transfer Campus";
        bool reAdmit =
            selectedOperation == "Re-Admit";
        if (reAdmit) SetReadmissionDateBounds();
        UiHelper.SetFieldCaption(dateField, "Effective Date");
        UiHelper.SetFieldCaption(remarksField, "Remarks", false);
        var fields =
            new List<Control>
            {
                currentCampusField,
                currentClassField,
                currentSectionField
            };
        if (fixedOperation is not null) fields.Clear();
        if (transferSection)
        {
            UiHelper.SetFieldCaption(classSectionField, "New Section");
            fields.Add(classSectionField);
        }
        else if (transferClass)
        {
            fields.Add(clsField);
        }
        else if (transferCampus)
        {
            fields.Add(campusField);
        }
        else if (reAdmit)
        {
            UiHelper.SetFieldCaption(classSectionField, "New Class & Section");
            fields.Add(campusField);
            UiHelper.SetFieldCaption(yearField, "Current Academic Year", true);
            fields.Add(yearField);
            fields.Add(clsField);
            fields.Add(classSectionField);
            fields.Add(reAdmitTransportField);
            fields.Add(currentAdmissionNumberField);
            fields.Add(readmissionNumberField);
            if (readmissionNumberChoice.SelectedIndex == 1) fields.Add(probableAdmissionNumberField);
        }

        fields.Add(dateField);
        fields.Add(remarksField);
        RebuildActionGrid(fields);
        SetStatus(
            selectedOperation +
            " workflow ready.");
    }
    private async Task ExecuteAsync()
    {
        if (isExecuting)
            return;
        if(selectedOperation!="Re-Admit"&&date.ShowCheckBox&&!date.Checked)
        {Warn("Select the actual historical transfer date within the recorded academic year.");return;}
        if (selectedOperation == "Re-Admit" && !CanReadmitSelected())
        {
            Warn("Readmission is for officially admitted former students who left or became inactive. Cancelled pending registrations use Reopen Registration; holds use Lift Hold / Resume.");
            return;
        }
        if (selectedOperation == "Re-Admit" && year.SelectedId <= 0)
        {
            Warn("No current academic year is available. Set the current year in Academic Years; use historical placement correction for past years.");
            return;
        }
        if (!ValidateRequiredInputs())
            return;
        if (student.SelectedId <= 0)
        {
            Warn(UiText.Prompts.SelectStudent);
            return;
        }
        if (selectedOperation != "Re-Admit" && enrollment.SelectedId <= 0)
        {
            Warn("Select the current active placement first.");
            return;
        }
        string operation = selectedOperation;
        int admissionId = student.SelectedId;
        int enrollmentId = enrollment.SelectedId;
        int destinationCampusId = campus.SelectedId;
        int destinationYearId = year.SelectedId;
        int destinationClassId = cls.SelectedId;
        int destinationSectionId = classSection.SelectedId;
        string destinationClassName = cls.Text;
        string destinationCampusName = campus.Text;
        string destinationSectionName = classSection.Text;
        string destinationYearName = year.Text;
        int currentCampusId = currentEnrollmentCampusId;
        int currentClassId = currentEnrollmentClassId;
        int currentSectionId = currentEnrollmentSectionId;
        DateTime transferDate = date.Value;
        DataRow? selectedEnrollment = enrollmentsRaw.AsEnumerable().FirstOrDefault(row =>
            row["EnrollmentId"] != DBNull.Value && Convert.ToInt32(row["EnrollmentId"]) == enrollmentId);
        if (operation != "Re-Admit" && selectedEnrollment is not null &&
            selectedEnrollment["EnrollmentDate"] != DBNull.Value && transferDate.Date < Convert.ToDateTime(selectedEnrollment["EnrollmentDate"]).Date)
        {
            Warn("Transfer date cannot be before the placement date.");
            return;
        }
        bool currentTransferPlacement=IsCurrentPlacement(selectedEnrollment);
        bool canSyncTransferFees=currentTransferPlacement && selectedEnrollment!["BillingReady"] is bool ready && ready;
        if(operation!="Re-Admit" && !currentTransferPlacement && string.IsNullOrWhiteSpace(remarks.Text))
        { Warn("Enter a reason for recording this historical transfer. Its date must be within the recorded academic year; existing fees will be preserved."); return; }
        string remarksText = remarks.Text.Trim();
        bool usesTransport = reAdmitTransport.Checked;
        bool allotNewAdmissionNumber = readmissionNumberChoice.SelectedIndex == 1;
        // Transfer-Fee Synchronization: for Transfer Class and Transfer Campus only.
        int? preResolvedDestinationClassSectionId = null;
        string? preResolvedNote = null;
        var feeDecision = TransferFeeSyncDialog.FeeDecision.Keep;
        DateTime? feeEffectiveMonth = null;
        bool replaceScheduledFees = false;
        string paidReductionAction = "Credit";
        DateTime? readmissionMonthlyStart=null;
        bool? chargeYearlyFees=null;
        string? reviewedReadmissionRates=null;
        if(operation=="Re-Admit")
        {
            if(destinationCampusId<=0 || destinationYearId<=0 || destinationSectionId<=0) {Warn("Select campus, academic year, and new class/section."); return;}
            try {
                isExecuting=true;
                // Re-admission is a new placement: C# owns its active-section selection.
                await ActiveClassSectionPolicy.EnsureActiveAsync(destinationSectionId);
                var data=await Task.Run(()=> {
                    using var connection=DatabaseHelper.GetConnection();
                    DatabaseHelper.OpenConnection(connection);
                    using var command=new Microsoft.Data.SqlClient.SqlCommand("""
                    EXEC dbo.RequirePermission @UserId,N'Students.Administer';
                    SELECT EndDate FROM dbo.AcademicYears WHERE AcademicYearId=@YearId;
                    SELECT F.FeeTypeId,F.FeeTypeName,C.Frequency,C.Amount FROM dbo.FeeConfigurations C
                    JOIN dbo.FeeTypes F ON F.FeeTypeId=C.FeeTypeId
                    JOIN dbo.CampusClassSections P ON P.CampusId=C.CampusId AND P.ClassId=C.ClassId
                    WHERE P.ClassSectionId=@PlacementId AND (@Transport=1 OR F.IsTransportLinked=0)
                    ORDER BY F.DisplayOrder,F.FeeTypeName;
                    SELECT ISNULL(SUM(CASE WHEN Status<>N'Cancelled' THEN dbo.GetFeeChargeBalance(BillingChargeId) ELSE 0 END),0) Debt,
                      ISNULL(SUM(CASE WHEN Status<>N'Cancelled' AND BillingMonth=DATEFROMPARTS(YEAR(@Date),MONTH(@Date),1) THEN NetAmount ELSE 0 END),0) MonthCharges
                    FROM dbo.BillingCharges WHERE AdmissionId=@AdmissionId;
                    """,connection);
                    command.Parameters.AddRange(new[]{DatabaseHelper.Int("@UserId",CurrentUser.UserId),DatabaseHelper.Int("@YearId",destinationYearId),
                    DatabaseHelper.Int("@PlacementId",destinationSectionId),DatabaseHelper.Bit("@Transport",usesTransport),
                    DatabaseHelper.Int("@AdmissionId",admissionId),DatabaseHelper.Date("@Date",transferDate)});
                    using var adapter=new Microsoft.Data.SqlClient.SqlDataAdapter(command);
                    var result=new DataSet(); adapter.Fill(result);return result;
                });
                if(data.Tables[0].Rows.Count==0) {Warn("Select a valid academic year.");return;}
                using var decision=new ReadmissionFeeDecisionDialog(transferDate,Convert.ToDateTime(data.Tables[0].Rows[0]["EndDate"]),
                    Convert.ToDecimal(data.Tables[2].Rows[0]["Debt"]),Convert.ToDecimal(data.Tables[2].Rows[0]["MonthCharges"]),data.Tables[1],
                    allotNewAdmissionNumber
                        ? $"Admission number: allot a new number automatically (current: {currentAdmissionNumber.Text}; probable new number: {probableAdmissionNumber.Text}). The final number is assigned when saved."
                        : $"Admission number: retain {currentAdmissionNumber.Text}.");
                if(decision.ShowDialog(FindForm())!=DialogResult.OK) return;
                readmissionMonthlyStart=decision.MonthlyStartMonth; chargeYearlyFees=decision.ChargeYearlyFees;
                reviewedReadmissionRates=System.Text.Json.JsonSerializer.Serialize(data.Tables[1].AsEnumerable().Select(row=>new {
                    FeeTypeId=Convert.ToInt32(row["FeeTypeId"]),Amount=Convert.ToDecimal(row["Amount"]),Frequency=Convert.ToString(row["Frequency"])}));
            } catch(Exception ex) {Fail(ex);return;}
            finally {isExecuting=false;}
        }
        if (operation == "Transfer Class" || operation == "Transfer Campus")
        {
            if (operation == "Transfer Class" && destinationClassId <= 0)
            {
                Warn("Select the new class.");
                return;
            }
            if (operation == "Transfer Campus" && destinationCampusId <= 0)
            {
                Warn("Select the new campus.");
                return;
            }
            /* non-zero is not enough. Said here as well as in SelectOperation because this is the boundary the write goes through. */
            if (operation == "Transfer Campus" && destinationCampusId == currentCampusId)
            {
                Warn(
                    "The selected destination campus is the student's current campus. Choose a " +
                    "different campus - use Re-Admit if they are returning to the same one.");
                return;
            }
            // Owner decision: a same-campus/same-class section change is always
            // Transfer Section. SQL independently enforces this for direct callers.
            if (operation == "Transfer Class" && destinationClassId == currentClassId)
            {
                Warn(
                    "The selected destination class is the student's current class. Choose a " +
                    "different class.");
                return;
            }
            if ((operation == "Transfer Class" && currentCampusId <= 0) ||
                (operation == "Transfer Campus" && currentClassId <= 0))
            {
                Alert("The student's current placement could not be determined. Reselect the placement and try again.");
                return;
            }
            isExecuting = true;
            SetStatus("Checking fee configuration for the destination...");
            try
            {
                (int resolvedId, string? note) = operation == "Transfer Class"
                    ? await Task.Run(() => ResolveDestinationClassSection(
                        currentCampusId, destinationClassId, currentSectionId, "the current campus",currentTransferPlacement))
                    : await Task.Run(() => ResolveDestinationClassSection(
                        destinationCampusId, currentClassId, currentSectionId, "the selected campus",currentTransferPlacement));
                if (resolvedId <= 0)
                {
                    Warn(operation == "Transfer Class"
                        ? $"{destinationClassName} is not offered at the current campus. Please contact the administrator to add it there first."
                        : $"{destinationCampusName} does not offer the student's current class. Please contact the administrator to add it there first.");
                    return;
                }
                preResolvedDestinationClassSectionId = resolvedId;
                preResolvedNote = note;
                int feeCampusId = operation == "Transfer Class" ? currentCampusId : destinationCampusId;
                int feeClassId = operation == "Transfer Class" ? destinationClassId : currentClassId;
                string fromLabel = $"{currentCampusDisplay.Text} — {currentClassDisplay.Text}";
                string toLabel = operation == "Transfer Class"
                    ? $"{currentCampusDisplay.Text} — {destinationClassName}"
                    : $"{destinationCampusName} — {currentClassDisplay.Text}";
                if (canSyncTransferFees)
                {
                    using var dialog = new TransferFeeSyncDialog(
                        student.Text,
                        fromLabel,
                        toLabel,
                        transferDate,
                        enrollmentId,
                        feeCampusId,
                        feeClassId);
                    if (dialog.HasFeeDifference)
                    {
                        if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
                        {
                            // Cancel: nothing happens - no transfer, no fee change.
                            return;
                        }
                        feeDecision = dialog.Decision;
                        feeEffectiveMonth = dialog.EffectiveMonth;
                        replaceScheduledFees = dialog.ReplaceScheduledFees;
                        paidReductionAction = dialog.PaidReductionAction;
                    }
                }
            }
            catch (Exception ex)
            {
                Fail(ex);
                return;
            }
            finally
            {
                isExecuting = false;
                SetStatus(string.Empty);
            }
        }
        isExecuting = true;
        // Capture the submitted destination before awaiting the operation; the controls
        // may change while it runs. Do not infer a final admission number or section.
        string completionMessage = operation switch {
            "Transfer Section" => $"Section changed to {destinationSectionName}.",
            "Transfer Class" => $"Class changed to {destinationClassName}.",
            "Transfer Campus" => $"Campus changed to {destinationCampusName}.",
            "Re-Admit" => $"Readmitted: {destinationCampusName} · {destinationSectionName} · {destinationYearName}.",
            _ => "Student operation completed."
        };
        if (operation is "Transfer Class" or "Transfer Campus")
        {
            string feeChoice = feeDecision switch {
                TransferFeeSyncDialog.FeeDecision.Keep => "Keep current fees",
                TransferFeeSyncDialog.FeeDecision.NextMonth => "New fees from next month",
                TransferFeeSyncDialog.FeeDecision.CurrentMonth => "New fees follow the School Profile cutoff; issued bills receive the difference",
                TransferFeeSyncDialog.FeeDecision.CustomMonth when feeEffectiveMonth is DateTime month =>
                    $"New fees from {month:MMM yyyy}",
                _ => ""
            };
            if (feeChoice.Length > 0)
                completionMessage += " " + feeChoice + ".";
            if (replaceScheduledFees)
                completionMessage += " Replace unbilled scheduled rates and waivers from that month.";
        }
        else if (operation == "Re-Admit")
        {
            if (readmissionMonthlyStart is DateTime month)
                completionMessage += $" Monthly fees from {month:MMM yyyy}.";
            if (chargeYearlyFees is bool charge)
                completionMessage += charge
                    ? " Yearly fees charged."
                    : " Yearly fees waived for this placement.";
        }
        SetStatus("Processing student lifecycle operation...");
        try
        {
            string? note = await Task.Run(() => {
                string? resultNote = preResolvedNote;
                switch (operation)
                {
                    case "Transfer Section":
                        if (destinationSectionId <= 0)
                            throw new InvalidOperationException("Select the new section.");
                        // Backdated historical transfers may retain inactive old sections.
                        if (currentTransferPlacement) ActiveClassSectionPolicy.EnsureActive(destinationSectionId);
                        DatabaseHelper.Execute(
                            "TransferEnrollmentClassSection",
                            DatabaseHelper.Int("@UserId", CurrentUser.UserId),
                            DatabaseHelper.Int("@EnrollmentId", enrollmentId),
                            DatabaseHelper.Int("@NewClassSectionId", destinationSectionId),
                            DatabaseHelper.Date("@TransferDate", transferDate),
                            DatabaseHelper.P("@Remarks", remarksText));
                        break;
                    case "Transfer Class":
                        {
                            if (currentTransferPlacement) ActiveClassSectionPolicy.EnsureActive(preResolvedDestinationClassSectionId!.Value);
                            (string? feePolicy, string? feeSyncReason) = ResolveFeeSyncArgs(feeDecision, remarksText);
                            DatabaseHelper.Execute(
                                "TransferEnrollmentClass",
                                DatabaseHelper.Int("@UserId", CurrentUser.UserId),
                                DatabaseHelper.Int("@EnrollmentId", enrollmentId),
                                DatabaseHelper.Int("@NewClassSectionId", preResolvedDestinationClassSectionId!.Value),
                                DatabaseHelper.Date("@TransferDate", transferDate),
                                DatabaseHelper.P("@Remarks", remarksText),
                                DatabaseHelper.P("@FeePolicy", feePolicy),
                                DatabaseHelper.P("@FeeSyncReason", feeSyncReason),
                                DatabaseHelper.P("@FeeEffectiveMonth", feeEffectiveMonth),
                                DatabaseHelper.Bit("@ReplaceScheduledFees", replaceScheduledFees),
                                DatabaseHelper.P("@PaidReductionAction", paidReductionAction));
                            break;
                        }
                    case "Transfer Campus":
                        {
                            if (currentTransferPlacement) ActiveClassSectionPolicy.EnsureActive(preResolvedDestinationClassSectionId!.Value);
                            (string? feePolicy, string? feeSyncReason) = ResolveFeeSyncArgs(feeDecision, remarksText);
                            DatabaseHelper.Execute(
                                "TransferEnrollmentCampus",
                                DatabaseHelper.Int("@UserId", CurrentUser.UserId),
                                DatabaseHelper.Int("@EnrollmentId", enrollmentId),
                                DatabaseHelper.Int("@NewClassSectionId", preResolvedDestinationClassSectionId!.Value),
                                DatabaseHelper.Date("@TransferDate", transferDate),
                                DatabaseHelper.P("@Remarks", remarksText),
                                DatabaseHelper.P("@FeePolicy", feePolicy),
                                DatabaseHelper.P("@FeeSyncReason", feeSyncReason),
                                DatabaseHelper.P("@FeeEffectiveMonth", feeEffectiveMonth),
                                DatabaseHelper.Bit("@ReplaceScheduledFees", replaceScheduledFees),
                                DatabaseHelper.P("@PaidReductionAction", paidReductionAction));
                            break;
                        }
                    case "Re-Admit":
                        if (destinationCampusId <= 0 || destinationYearId <= 0 || destinationSectionId <= 0)
                            throw new InvalidOperationException("Select campus, academic year, and new class/section.");
                        ActiveClassSectionPolicy.EnsureActive(destinationSectionId);
                        DatabaseHelper.Execute(
                            "ReAdmitStudentWithNumberChoice",
                            DatabaseHelper.Int("@UserId", CurrentUser.UserId),
                            DatabaseHelper.Int("@AdmissionId", admissionId),
                            DatabaseHelper.Int("@CampusId", destinationCampusId),
                            DatabaseHelper.Int("@AcademicYearId", destinationYearId),
                            DatabaseHelper.Int("@ClassSectionId", destinationSectionId),
                            DatabaseHelper.Date("@EnrollmentDate", transferDate),
                            DatabaseHelper.P("@Reason", remarksText),
                            DatabaseHelper.Bit("@UsesTransport", usesTransport),
                            DatabaseHelper.P("@MonthlyStartMonth",readmissionMonthlyStart),
                            DatabaseHelper.P("@ChargeYearlyFees",chargeYearlyFees),
                            DatabaseHelper.P("@ReviewedRates",reviewedReadmissionRates),
                            DatabaseHelper.Bit("@AllotNewAdmissionNumber", allotNewAdmissionNumber));
                        break;
                }
                return resultNote;
            });
            AdmissionNumbers.Invalidate();
            StudentSearchCache.Invalidate();
            LoadEnrollments(completionMessage +
                (string.IsNullOrEmpty(note) ? string.Empty : "\n\n" + note));
            if (fixedOperation is not null) OperationCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (InvalidOperationException ex)
        {
            Warn(ex.Message);
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
        finally
        {
            isExecuting = false;
        }
    }
    /// <summary>"Keep existing fees" maps to a NULL policy, which the database interprets as "skip fee sync entirely".</summary>
    private static (string? Policy, string? Reason) ResolveFeeSyncArgs(TransferFeeSyncDialog.FeeDecision decision, string remarksText)
    {
        if (decision == TransferFeeSyncDialog.FeeDecision.Keep)
            return (null, null);
        string policy = decision switch {
            TransferFeeSyncDialog.FeeDecision.CurrentMonth => "SCHOOL_CUTOFF",
            TransferFeeSyncDialog.FeeDecision.CustomMonth => "CUSTOM_MONTH",
            _ => "NEXT_MONTH"
        };
        string reason = string.IsNullOrWhiteSpace(remarksText)
            ? "Fee synchronization after class/campus transfer."
            : remarksText;
        return (policy, reason);
    }
    private void ResetOperationFields()
    {
        enrollment.ClearLookup();
        enrollmentsRaw =
            new DataTable();
        currentCampusDisplay.Text =
            string.Empty;
        currentClassDisplay.Text =
            string.Empty;
        currentSectionDisplay.Text =
            string.Empty;
        currentEnrollmentCampusId = 0;
        currentEnrollmentClassId = 0;
        currentEnrollmentSectionId = 0;
        currentEnrollmentClassSectionId = 0;
        suppressDestinationEvents = true;
        try
        {
            campus.SelectedIndex = -1;
            year.SelectedIndex = -1;
            classSection.ClearLookup();
        }
        finally
        {
            suppressDestinationEvents = false;
        }
        date.Value =
            DateTime.Today;
        remarks.Clear();
        reAdmitTransport.Checked = false;
        numberPreviewVersion++;
        probableAdmissionNumber.Clear();
        readmissionNumberChoice.SelectedIndex = 0;
        currentAdmissionNumber.Clear();
    }
    private async Task RefreshAdmissionNumberPreviewAsync()
    {
        int version = ++numberPreviewVersion;
        if (selectedOperation != "Re-Admit" || readmissionNumberChoice.SelectedIndex != 1)
        {
            probableAdmissionNumber.Clear();
            return;
        }
        probableAdmissionNumber.Text = "Checking…";
        try
        {
            // A read-only preview uses the same counter as ReserveAdmissionNumber without reserving it.
            var result = await Task.Run(() => DatabaseHelper.QuerySql("""
                EXEC dbo.RequirePermission @UserId, N'Students.Administer';
                SELECT CONVERT(varchar(40), LastNumber + 1) AS AdmissionNumber
                FROM dbo.AdmissionNumberCounter WHERE CounterId = 1;
                """, DatabaseHelper.Int("@UserId", CurrentUser.UserId)));
            if (IsDisposed || version != numberPreviewVersion) return;
            probableAdmissionNumber.Text = result.Rows.Count > 0 ? AdmissionNumbers.Read(result.Rows[0]) : "Unavailable";
        }
        catch (Exception ex)
        {
            if (IsDisposed || version != numberPreviewVersion) return;
            probableAdmissionNumber.Text = "Unavailable";
            Fail(ex);
        }
    }

    private void ClearForm(bool clearStudentBox = true)
    {
        /* Item 14 - Clear is the page's empty state, and the history grid is part of what is on screen. */
        Grid.DataSource = null;
        if (clearStudentBox)
            student.Clear();
        ResetOperationFields();
        UpdateAvailableOperations();
    }
}

