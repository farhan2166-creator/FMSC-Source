using FMSC_SMS.Core;
namespace FMSC_SMS.Pages.Staff;
// BD-37: reject future hire dates and leaving dates before hire; keep Add/Edit Job entry points distinct.
public sealed class StaffEmploymentPage : MasterPage
{
    // Employment history is an active feature, separate from staff assignment
    // scheduling intentionally removed in the 2026-10-03 database cleanup.
    public event Action? EmploymentChanged;
    public Func<bool>? SaveGuard { get; set; }
    private FormValidator? _employmentValidator;
    protected override bool ReserveEditorValidationSpace => true;
    protected override string NewRecordHeaderTitle => "Add Employment";
    protected override string NewRecordHeaderSubtitle => "Leave Leaving Date unchecked for an active job. Enter it for a past job.";
    protected override string EntryTabCaption => "Job Details";
    protected override string RecordsTabCaption => "Employment History";
    protected override bool CanSaveRecord()
    {
        if (_staffProvider is not null && _staffProvider() <= 0)
        {
            Warn("Choose or save a staff member first.");
            return false;
        }
        if (SaveGuard?.Invoke() == false) return false;
        if (_employmentValidator is null)
        {
            _employmentValidator = CreateValidator();
            BindEditorValidationMessages(_employmentValidator);
        }
        _employmentValidator.Begin();
        _employmentValidator.RequiredFields(this);
        if (TryGetEditorControl("HireDate", out var hireControl) && hireControl is ModernDatePicker hire &&
            TryGetEditorControl("LeavingDate", out var leaveControl) && leaveControl is ModernDatePicker leave)
        {
            _employmentValidator.Rule(hire, () => hire.Value.Date <= DateTime.Today,
                "Hire date cannot be in the future.");
            _employmentValidator.Rule(leave, () => !leave.Checked || leave.Value.Date >= hire.Value.Date,
                "Leaving date cannot be earlier than hire date.");
        }
        return _employmentValidator.Finish();
    }
    protected override void AfterSave(int id, Dictionary<string, object?> values)
    {
        base.AfterSave(id, values);
        EmploymentChanged?.Invoke();
    }
    private readonly Func<int>? _staffProvider;
    private DataTable? _selectionSnapshot;
    private bool _chooseInitialView = true;
    private int? _initialEmploymentId;
    internal void EditOnLoad(int employmentId) => _initialEmploymentId = employmentId;
    public StaffEmploymentPage() : base("Staff Employment", "Manage staff jobs and employment history.", "GetStaffEmployment", "SaveStaffEmployment", new[] { new FieldSpec("StaffId", "Staff", FieldKind.ComboInt, Required: true, LookupProcedure: "GetStaff", LookupIdColumn: "StaffId", LookupTextColumn: "StaffName"), new FieldSpec("Designation", "Designation", Required: true), new FieldSpec("Department", "Department"), new FieldSpec("EmploymentType", "Employment Type"), new FieldSpec("HireDate", "Hire Date", FieldKind.Date, Required: true), new FieldSpec("LeavingDate", "Leaving Date", FieldKind.Date), new FieldSpec("Remarks", "Remarks") }, v => new[] { DatabaseHelper.P("@UserId", CurrentUser.UserId), DatabaseHelper.Int("@StaffEmploymentId", Convert.ToInt32(v["Id"] ?? 0)), DatabaseHelper.Int("@StaffId", Convert.ToInt32(v["StaffId"] ?? 0)), DatabaseHelper.P("@Designation", v["Designation"]), DatabaseHelper.P("@Department", v["Department"]), DatabaseHelper.P("@EmploymentType", v["EmploymentType"]), DatabaseHelper.Date("@HireDate", (DateTime)v["HireDate"]!), DatabaseHelper.P("@LeavingDate", v["LeavingDate"]), DatabaseHelper.P("@Remarks", v["Remarks"]) }, savePermission: "Staff.Manage") { }
    // Constructor that binds the page to a selected StaffId provider so the records shown reflect the currently selected staff member in a parent page (e.g. StaffManagementPage).
    public StaffEmploymentPage(Func<int> staffIdProvider)
        : this(staffIdProvider, false) { }
    internal StaffEmploymentPage(Func<int> staffIdProvider, bool editorOnly)
        : base(
            "Employment",
            "Add a job or select a saved job to update its details.",
            "GetStaffEmployment",
            "SaveStaffEmployment",
            new[] {
                new FieldSpec("StaffId", "Staff", FieldKind.ComboInt, Required: true, LookupProcedure: "GetStaff", LookupIdColumn: "StaffId", LookupTextColumn: "StaffName"),
                new FieldSpec("Designation", "Designation", Required: true),
                new FieldSpec("Department", "Department"),
                new FieldSpec("EmploymentType", "Employment Type", FieldKind.ComboString, Options: new[] { "Permanent", "Contract", "Visiting" }),
                new FieldSpec("HireDate", "Hire Date", FieldKind.Date, Required: true),
                new FieldSpec("LeavingDate", "Leaving Date", FieldKind.Date),
                new FieldSpec("Remarks", "Remarks", Multiline: true, MaxLength: 500)
            },
            v => new[] {
                DatabaseHelper.P("@UserId", CurrentUser.UserId),
                DatabaseHelper.Int("@StaffEmploymentId", Convert.ToInt32(v["Id"] ?? 0)),
                DatabaseHelper.Int("@StaffId", Convert.ToInt32(v["StaffId"] ?? 0)),
                DatabaseHelper.P("@Designation", v["Designation"]),
                DatabaseHelper.P("@Department", v["Department"]),
                DatabaseHelper.P("@EmploymentType", v["EmploymentType"]),
                DatabaseHelper.Date("@HireDate", (DateTime)v["HireDate"]!),
                DatabaseHelper.P("@LeavingDate", v["LeavingDate"]),
                DatabaseHelper.P("@Remarks", v["Remarks"]) },
            // supply getParams which will be evaluated on reload; this closes over the provided delegate so the current staff id is used.
            getParams: () => new[] {
                DatabaseHelper.P("@UserId", CurrentUser.UserId),
                DatabaseHelper.Int("@StaffId", staffIdProvider())
            },
            // That override is gone. dbo.SaveStaffEmployment synchronizes Staff.Status in its own transaction, and dbo.EndStaffEmployment ends the newest ACTIVE record for the staff member.
            savePermission: "Staff.Manage",
            separateEntryAndRecords: true,
            editorOnly: editorOnly,
            guidance: "Select a job to edit it. Set a Leaving Date when it ends. Each staff member can have one active job.")
    {
        _staffProvider = staffIdProvider;
        HideContextEditorField("StaffId");
        if (TryGetEditorControl("StaffId", out Control? control) &&
            control is LookupBox staffLookup)
        {
            staffLookup.Enabled = false;
            staffLookup.AccessibleDescription =
                "Uses the staff member selected before opening Employment.";
            staffLookup.LookupBound += (_, _) => ApplySelectedStaff();
            // Clear/New actions in MasterPage reset every editor control. Restore
            // this parent-owned value immediately so the locked field never
            // becomes blank or forces a second staff selection.
            staffLookup.SelectedIndexChanged += (_, _) => ApplySelectedStaff();
        }
    }
    // Notify the page that the selected staff may have changed and the grid should refresh to reflect the current selection.
    public void RefreshForSelectedStaff(DataTable? records = null)
    {
        // If no staff provider was supplied, do nothing.
        if (_staffProvider is null)
            return;
        /* Reload re-queries the grid for the newly selected staff member, but the editor is not part of that reload. */
        _selectionSnapshot = records?.Copy();
        ResetEditorForContextChange();
        ApplySelectedStaff();
        // Reload will evaluate the getParams delegate that references the provider and update the grid accordingly.
        Reload();
    }
    protected override DataTable QueryCurrentTable()
    {
        if (_staffProvider is not null && _staffProvider() <= 0) return new DataTable();
        // Only the selection-triggered reload consumes this shared result.
        // Refreshes, exports and reloads after writes still query fresh records.
        DataTable? snapshot = Interlocked.Exchange(ref _selectionSnapshot, null);
        var records = snapshot ?? base.QueryCurrentTable();
        if (records.Columns.Contains("Status") && records.Columns.Contains("HireDate"))
        {
            var ordered = records.AsEnumerable()
                .OrderBy(row => Convert.ToString(row["Status"]) == "Active" ? 0 : 1)
                .ThenByDescending(row => row["HireDate"] == DBNull.Value ? DateTime.MinValue : Convert.ToDateTime(row["HireDate"]));
            if (records.Rows.Count > 0) records = ordered.CopyToDataTable();
        }
        return records;
    }
    protected override void RecordsLoaded(DataTable table)
    {
        base.RecordsLoaded(table);
        if (!_chooseInitialView || _staffProvider is null) return;
        _chooseInitialView = false;
        if (_initialEmploymentId is int id)
        {
            if (id > 0 && !EditRecordById(id)) Warn("This employment record is no longer available. Refresh the staff page.");
            ShowRecordsTab(false);
            return;
        }
        // Start with existing jobs when present; an empty history starts at Add
        // Employment. Later refreshes must not switch tabs while the operator works.
        ShowRecordsTab(table.Rows.Count > 0);
    }
    private void ApplySelectedStaff()
    {
        if (_staffProvider is null ||
            !TryGetEditorControl("StaffId", out Control? control) ||
            control is not LookupBox staffLookup)
        {
            return;
        }
        int staffId = _staffProvider();
        int expectedId = staffId > 0 ? staffId : 0;
        if (staffLookup.SelectedId != expectedId)
            staffLookup.SelectedValue = expectedId;
        CaptureContextBaseline();
    }
}
