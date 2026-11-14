using System.Reflection;
using System.Data;
using FMSC_SMS.Core;
using FMSC_SMS.Pages.Security;
using FMSC_SMS.Pages.Students.Admissions;

internal static partial class Program
{
    private static void CheckRegistrationWorkflow()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var previous = (CurrentUser.UserId, CurrentUser.Username, CurrentUser.RoleName, CurrentUser.MustChangePassword, Grants: CurrentUser.GetPermissions().ToArray());
        try
        {
            CurrentUser.SignIn(1, "Fictional admission operator", "Administrator", false, Array.Empty<string>());
            using var page = new StudentAdmissionPage(false);
            var type = typeof(StudentAdmissionPage);
            void Set(string field, object value) => type.GetField(field, flags)!.SetValue(page, value);
            T Field<T>(string field) => (T)type.GetField(field, flags)!.GetValue(page)!;
            void Update() => type.GetMethod("UpdateStudentWorkspace", flags)!.Invoke(page, null);
            using var host = new Form { ClientSize = new Size(1600, 900) };
            host.Controls.Add(page); host.Show(); Application.DoEvents();
            type.GetMethod("SwitchTab", flags)!.Invoke(page, new object[] { 1 }); Application.DoEvents();
            using var family = new DataTable();
            family.Columns.Add("AdmissionId", typeof(int));
            foreach (string column in new[] { "Student Name", "Father Name", "Class", "Campus", "Status" }) family.Columns.Add(column);
            family.Rows.Add(123, "Fictional Pending Child", "Fictional Parent", "Four", "Test Campus", "Pending");
            family.Rows.Add(456, "Fictional Cancelled Child", "Fictional Parent", "Five", "Test Campus", "Cancelled");
            family.Rows.Add(789, "Fictional Active Child", "Fictional Parent", "Six", "Test Campus", "Active");
            family.Rows.Add(999, "Fictional Waiting Sibling", "Fictional Parent", "Four", "Test Campus", "Pending");
            var siblingGrid = Field<DataGridView>("siblingsGrid"); siblingGrid.AutoGenerateColumns = true; siblingGrid.DataSource = family;
            type.GetMethod("ConfigureSiblingRegistrationRows", flags)!.Invoke(page, null);
            siblingGrid.Columns["AdmissionId"].Visible = false;
            foreach (DataGridViewColumn column in siblingGrid.Columns) column.MinimumWidth = 90;
            Application.DoEvents();
            Set("currentAdmissionId", 123); Set("_workspaceStudentStatus", "Pending"); Update();
            Check(Field<ModernButton>("quickRegistrationButton").Enabled,
                "A permitted operator can start sibling admission for the loaded student");
            // The pending count is a business result, its caption is presentation.
            // One of the two pending fixture children is the loaded child.
            var waitingNumbers = System.Text.RegularExpressions.Regex.Matches(
                Field<Label>("pendingSiblingCount").Text, @"\d+")
                .Cast<System.Text.RegularExpressions.Match>()
                .Select(match => int.Parse(match.Value, System.Globalization.CultureInfo.InvariantCulture))
                .ToArray();
            Check(waitingNumbers.SequenceEqual(new[] { 1 }),
                "Waiting count excludes the loaded child and counts pending siblings");
            Check(Field<ModernButton>("saveButton").Enabled,
                "A permitted operator can save the loaded pending admission without a prerequisite draft");
            Check(!string.IsNullOrWhiteSpace(Convert.ToString(siblingGrid.Rows[0].Cells["RegistrationConfirm"].Value)),
                "Pending children have an available registration-completion action on their own row");
            Check(!string.IsNullOrWhiteSpace(Convert.ToString(siblingGrid.Rows[0].Cells["RegistrationCancel"].Value)),
                "Pending children retain a row cancellation action");
            using var rows = new DataTable(); rows.Columns.Add("Status"); var pending = rows.Rows.Add("Pending"); var cancelled = rows.Rows.Add("Cancelled");
            var picker = Field<StudentPicker>("quickStudent");
            Check(picker.RowFilter!(pending) && picker.RowFilter!(cancelled), "Admission quick search accepts pending and cancelled records");
            // A successful switch from the loaded child to a sibling must also change
            // the quick-search identity without emitting a new user-selection event.
            using var studentDetails = new DataTable();
            studentDetails.Columns.Add("AdmissionId", typeof(int));
            studentDetails.Columns.Add("AdmissionNumber", typeof(string));
            studentDetails.Columns.Add("StudentName", typeof(string));
            studentDetails.Columns.Add("FatherName", typeof(string));
            studentDetails.Columns.Add("Gender", typeof(string));
            var mainChild = studentDetails.Rows.Add(123, "123", "Fictional Pending Child", "Fictional Parent", "Male");
            var siblingChild = studentDetails.Rows.Add(456, "456", "Fictional Cancelled Child", "Fictional Parent", "Female");
            int selectionEvents = 0;
            picker.UserSelectionChanged += (_, _) => selectionEvents++;
            var syncQuickSearch = type.GetMethod("UpdateQuickStudentToLoadedRecord", flags)!;
            syncQuickSearch.Invoke(page, new object[] { mainChild });
            Check(picker.SelectedId == 123 && picker.Text.Contains("Fictional Pending Child"),
                "Admission quick search displays the loaded main student");
            Set("currentAdmissionId", 456);
            syncQuickSearch.Invoke(page, new object[] { siblingChild });
            Check(picker.SelectedId == 456 && picker.Text.Contains("Fictional Cancelled Child") && selectionEvents == 0,
                "Opening a sibling updates quick search without recursively loading a student");
            Set("currentAdmissionId", 123);
            syncQuickSearch.Invoke(page, new object[] { mainChild });
            Check(picker.SelectedId == 123 && selectionEvents == 0,
                "Returning to the main student restores its quick-search identity");
            ((Task)type.GetMethod("RunSiblingRegistrationActionAsync", flags)!.Invoke(page, new object[] { 0, siblingGrid.Columns["RegistrationConfirm"].Index })!).GetAwaiter().GetResult();
            Check(type.GetField("unconfirmedAdmission", flags)!.GetValue(page) is null, "Incomplete pending details refuse confirmation before a write");
            type.GetMethod("SwitchTab", flags)!.Invoke(page, new object[] { 1 });
            Set("_workspaceStudentStatus", "Cancelled"); family.Rows[0]["Status"] = "Cancelled"; Update();
            Check(!string.IsNullOrWhiteSpace(Convert.ToString(siblingGrid.Rows[0].Cells["RegistrationReopen"].Value)),
                "Cancelled registration exposes an actionable row reopening option");
            Check(string.IsNullOrEmpty(Convert.ToString(siblingGrid.Rows[0].Cells["RegistrationConfirm"].Value)) && !Field<ModernButton>("saveButton").Enabled && !Field<ModernButton>("quickRegistrationButton").Enabled, "Cancelled records cannot be confirmed, edited through Save or used to create siblings");
            Set("_workspaceStudentStatus", "Active"); family.Rows[0]["Status"] = "Active"; Update();
            Check(string.IsNullOrEmpty(Convert.ToString(siblingGrid.Rows[0].Cells["RegistrationReopen"].Value)), "Admitted students do not get registration reopening");
            CurrentUser.SignIn(2, "Read only", "Operator", false, new[] { "Students.View" }); Update();
            Check(!Field<ModernButton>("quickRegistrationButton").Enabled && !Field<ModernButton>("quickRegistrationButton").Enabled, "Viewing access does not grant registration writes");
            CurrentUser.SignIn(3, "Registration without family access", "Operator", false, new[] { "Students.Admit", "Students.View" }); Update();
            Check(!Field<ModernButton>("quickRegistrationButton").Enabled, "Admit Sibling requires family-link write permission before the button is enabled");
            Set("_workspaceStudentStatus", "Pending"); family.Rows[0]["Status"] = "Pending";
            CurrentUser.SignIn(4, "Administrator without fee collection", "Operator", false, new[] { "Students.Administer" }); Update();
            Check(string.IsNullOrEmpty(Convert.ToString(siblingGrid.Rows[0].Cells["RegistrationCancel"].Value)),
                "Cancel Registration is not offered without SQL-required Fees.Collect");
            CurrentUser.SignIn(5, "Cancellation operator", "Operator", false, new[] { "Students.Administer", "Fees.Collect" }); Update();
            Check(Convert.ToString(siblingGrid.Rows[0].Cells["RegistrationCancel"].Value) == "Cancel",
                "Cancel Registration is available with its mandatory Students.Administer and Fees.Collect grants");
            Set("_workspaceStudentStatus", "Cancelled"); family.Rows[0]["Status"] = "Cancelled";
            CurrentUser.SignIn(6, "Reopening without student view", "Operator", false, new[] { "Students.Admit", "Students.Administer", "Fees.View" }); Update();
            Check(string.IsNullOrEmpty(Convert.ToString(siblingGrid.Rows[0].Cells["RegistrationReopen"].Value)),
                "Reopening also requires SQL-required Students.View");
            CurrentUser.SignIn(7, "Authorized reopening", "Operator", false, new[] { "Students.View", "Students.Admit", "Students.Administer", "Fees.View" }); Update();
            Check(Convert.ToString(siblingGrid.Rows[0].Cells["RegistrationReopen"].Value) == "Reopen",
                "Reopening is offered with all four SQL-required grants");
            CurrentUser.SignIn(1, "Fictional admission operator", "Administrator", false, Array.Empty<string>());
            Set("_workspaceStudentStatus", "Pending"); family.Rows[0]["Status"] = "Pending"; Update();
            typeof(V2Page).GetMethod("SetStatus", flags, null, new[] { typeof(string), typeof(bool) }, null)!.Invoke(page, new object[] { "Pending registration: complete details, then Confirm & Save.", false });
            Application.DoEvents();
            var status = (Label)typeof(V2Page).GetField("StatusLabel", flags)!.GetValue(page)!;
            var layout = (TableLayoutPanel)status.Parent!;
            Check(layout.ColumnCount == 1 && layout.GetRow(status) == 1, "Admission status has its own full-width row below actions");
            Check(status.Height <= AppMetrics.TextHeight(status.Font) + 8, "Status row stays compact");
            Directory.CreateDirectory("artifacts/registration-workflow-ui");
            foreach (int width in new[] { 1600, 1050 })
            {
                host.ClientSize = new Size(width, 900); Application.DoEvents();
                Check(status.Width >= layout.ClientSize.Width - layout.Padding.Horizontal - 2, "Status retains full width after resize");
                using var picture = new Bitmap(host.Width, host.Height); host.DrawToBitmap(picture, new Rectangle(Point.Empty, picture.Size));
                picture.Save($"artifacts/registration-workflow-ui/admission-{width}.png");
            }
            using var child = new QuickRegistrationFormPage((_, _, _) => { }, 0, false);
            var childType = typeof(QuickRegistrationFormPage);
            T ChildField<T>(string key) => (T)childType.GetField(key, flags)!.GetValue(child)!;
            using var years = new DataTable(); years.Columns.Add("AcademicYearId", typeof(int)); years.Columns.Add("YearName"); years.Rows.Add(77, "2026-2027");
            ChildField<LookupBox>("year").Bind(years, "AcademicYearId", "YearName");
            childType.GetField("defaultAcademicYear", flags)!.SetValue(child, 77);
            var numberFlags = BindingFlags.Static | BindingFlags.NonPublic;
            typeof(AdmissionNumbers).GetField("_numbers", numberFlags)!.SetValue(null, new Dictionary<int, string> { [123] = "QR-123" });
            typeof(AdmissionNumbers).GetField("_loadedAt", numberFlags)!.SetValue(null, DateTime.UtcNow);
            child.ConfigureSiblingEntry(123, "Fictional Parent", "03001234567", "Shared address", "Shared city");
            Check(ChildField<ModernTextBox>("parent").ReadOnly && ChildField<ModernTextBox>("parent").Text == "Fictional Parent", "Sibling father name is inherited and locked");
            Check(ChildField<ModernTextBox>("contact").ReadOnly && ChildField<ModernTextBox>("address").ReadOnly && ChildField<ModernTextBox>("siblingBirth").Text == "Shared city", "Phone, address and birthplace are inherited and locked");
            Check(!ChildField<LookupBox>("year").Enabled && ChildField<LookupBox>("year").SelectedId == 77, "Sibling year is locked to the current year");
            using var childHost = new Form { ClientSize = new Size(1050, 760) }; childHost.Controls.Add(child); childHost.Show(); Application.DoEvents();
            child.FocusSiblingName();
            Check(ChildField<ModernTextBox>("name").ContainsFocus, "Sibling entry starts with student name focused");
            Check(!ChildField<ModernTextBox>("contact").Visible && !ChildField<ModernTextBox>("address").Visible && ChildField<ModernTextBox>("contact").Text == "03001234567" && ChildField<ModernTextBox>("address").Text == "Shared address", "Hidden inherited contact fields retain their saved values");
            var siblingFields = (TableLayoutPanel)ChildField<ModernTextBox>("name").Parent!.Parent!;
            Check(siblingFields.GetRow(ChildField<ModernDatePicker>("joining").Parent!) == 0 && siblingFields.GetRow(ChildField<LookupBox>("year").Parent!) == 0 && siblingFields.GetRow(ChildField<ModernTextBox>("name").Parent!) == 1 && siblingFields.GetRow(ChildField<PlacementPicker>("placement").Parent!) == 1 && siblingFields.RowCount == 3, "Sibling fields use three rows with dates first and student placement second");
            Check(!ChildField<ModernTextBox>("siblingBirth").Visible && siblingFields.GetColumn(ChildField<ModernComboBox>("siblingGender").Parent!) == 0 && siblingFields.GetRow(ChildField<ModernComboBox>("siblingGender").Parent!) == 2 && siblingFields.GetColumn(ChildField<ModernTextBox>("parent").Parent!) == 1, "Gender replaces father on the left; father replaces hidden birthplace on the right");
            Check(ChildField<ModernComboBox>("siblingGender").SelectedIndex == -1 && ChildField<ModernComboBox>("siblingGender").Items.Count == 3 && ChildField<ModernComboBox>("siblingGender").Enabled, "Sibling gender requires an explicit selection before the first save");
            Check(ChildField<ModernDatePicker>("joining").Width == ChildField<ModernTextBox>("name").Width && ChildField<LookupBox>("year").Width == ChildField<ModernTextBox>("name").Width && ChildField<PlacementPicker>("placement").Width == ChildField<ModernTextBox>("name").Width, "Sibling lookup widths match text fields");
            using var childImage = new Bitmap(childHost.Width, childHost.Height); childHost.DrawToBitmap(childImage, new Rectangle(Point.Empty, childImage.Size)); childImage.Save("artifacts/registration-workflow-ui/admit-sibling.png");
            using var reopen = new RegistrationReopenDialog(123, "Fictional Child", false); reopen.Show(); Application.DoEvents();
            Check(typeof(RegistrationReopenDialog).GetField("placement", flags)!.GetValue(reopen) is PlacementPicker, "Reopening uses Add Placement's shared picker");
            Check(reopen.ClientSize.Height <= 380, "Reopening dialog is compact");
            using var reopenImage = new Bitmap(reopen.Width, reopen.Height); reopen.DrawToBitmap(reopenImage, new Rectangle(Point.Empty, reopenImage.Size)); reopenImage.Save("artifacts/registration-workflow-ui/reopen.png");
            using var cancel = new CloseAdmissionDialog(123, "Fictional Child", true, (_, _) => Task.FromResult(new DataTable())); cancel.Show(); Application.DoEvents();
            Check(cancel.ClientSize.Height <= AppScale.Scale(400), "Cancellation dialog is compact");
            using var cancellationPreview = new DataTable();
            cancellationPreview.Columns.Add("Snapshot");
            cancellationPreview.Columns.Add("ChargeCount", typeof(int));
            cancellationPreview.Columns.Add("Outstanding", typeof(decimal));
            cancellationPreview.Columns.Add("BlockedCount", typeof(int));
            cancellationPreview.Rows.Add("fictional-review", 1, 500m, 0);
            using var permissionDialog = new CloseAdmissionDialog(123, "Fictional Child", true,
                (_, _) => Task.FromResult(cancellationPreview));
            var cancelType = typeof(CloseAdmissionDialog);
            void PreviewCancellation() =>
                ((Task)cancelType.GetMethod("ReviewAsync", flags)!.Invoke(permissionDialog, null)!).GetAwaiter().GetResult();
            var applyCancellation = (ModernButton)cancelType.GetField("apply", flags)!.GetValue(permissionDialog)!;
            var cancellationImpact = (ModernTextBox)cancelType.GetField("impact", flags)!.GetValue(permissionDialog)!;
            CurrentUser.SignIn(8, "Collection without adjustment", "Operator", false, new[] { "Students.Administer", "Fees.Collect" });
            PreviewCancellation();
            Check(!applyCancellation.Enabled && cancellationImpact.Text.Contains("Fees.Adjust"),
                "Cancellation of saved charges requires Fees.Adjust, as the SQL charge reversal does");
            cancellationPreview.Rows[0]["ChargeCount"] = 0;
            PreviewCancellation();
            Check(applyCancellation.Enabled, "Cancellation without charges needs Fees.Collect but not Fees.Adjust");
            cancellationPreview.Rows[0]["ChargeCount"] = 1;
            CurrentUser.SignIn(9, "Collection and adjustment", "Operator", false, new[] { "Students.Administer", "Fees.Collect", "Fees.Adjust" });
            PreviewCancellation();
            Check(applyCancellation.Enabled, "Cancellation with charges is enabled for operators with every required SQL grant");
            CurrentUser.SignIn(1, "Fictional admission operator", "Administrator", false, Array.Empty<string>());
            using var cancelImage = new Bitmap(cancel.Width, cancel.Height); cancel.DrawToBitmap(cancelImage, new Rectangle(Point.Empty, cancelImage.Size)); cancelImage.Save("artifacts/registration-workflow-ui/cancel.png");
        }
        finally { AdmissionNumbers.Invalidate(); CurrentUser.SignIn(previous.UserId, previous.Username, previous.RoleName, previous.MustChangePassword, previous.Grants); }
    }
}
