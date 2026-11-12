using System.Data;
using System.Reflection;
using FMSC_SMS.Core;
using FMSC_SMS.Pages.Security;
using FMSC_SMS.Pages.Students.Certificates;
using Microsoft.Data.SqlClient;

internal static partial class Program
{
    private static void CheckCertificateSubmissionGuard()
    {
        var previous = (CurrentUser.UserId, CurrentUser.Username, CurrentUser.RoleName,
            CurrentUser.MustChangePassword, CurrentUser.GetPermissions().ToArray());
        CurrentUser.SignIn(901, "Fixture", "Administrator", false, Array.Empty<string>());
        try
        {
            foreach (string kind in new[] { "Sports", "Provisional", "NoObjection" })
            {
                int writes = 0, reads = 0;
                SqlParameter[]? submitted = null;
                var reply = new TaskCompletionSource<int>();
                Task<int> Write(SqlParameter[] parameters) { writes++; submitted = parameters; return reply.Task; }
                string key = kind == "Sports" ? "SportsCertificateId" : kind == "Provisional" ? "ProvisionalCertificateId" : "NoObjectionCertificateId";
                DataTable Read(int id)
                {
                    reads++; Check(id == 41, "Reload keeps the submitted student");
                    var rows = new DataTable();
                    rows.Columns.Add(key, typeof(int)); rows.Columns.Add("CertificateDate", typeof(DateTime));
                    rows.Columns.Add("OtherSubjects"); rows.Columns.Add("SubjectIds");
                    rows.Rows.Add(1, DateTime.Today, "English", "");
                    rows.Rows.Add(2, DateTime.Today.AddDays(-1), "English", "");
                    return rows;
                }
                using Control page = kind switch
                {
                    "Sports" => new SportsCertificatePage(false, Write, Read),
                    "Provisional" => new ProvisionalCertificatePage(false, Write, Read, _ => new DataTable()),
                    _ => new NoObjectionCertificatePage(false, Write, Read)
                };
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var type = page.GetType();
                page.BindingContext = new BindingContext();
                T Field<T>(string name) => (T)type.GetField(name, flags)!.GetValue(page)!;
                object? Call(string name) => type.GetMethod(name, flags)!.Invoke(page, null);
                var student = Field<StudentPicker>("student");
                var studentRows = new DataTable();
                studentRows.Columns.Add("AdmissionId", typeof(int));
                studentRows.Columns.Add("StudentName");
                studentRows.Columns.Add("DateOfBirth", typeof(DateTime));
                studentRows.Rows.Add(41, "Synthetic student", new DateTime(2010, 1, 1));
                student.SetSource(studentRows);
                student.SetStudent(41, "Synthetic student");
                Field<ModernDatePicker>("certificateDate").Value = DateTime.Today.AddDays(-1);
                if (kind == "Sports")
                {
                    Field<ModernTextBox>("sportName").Text = "Cricket";
                    Field<ModernTextBox>("performance").Text = "Team captain";
                }
                if (kind == "Provisional")
                {
                    Field<ModernComboBox>("examType").SelectedIndex = 0;
                    foreach (var entry in new[] { ("examYear", DateTime.Today.Year.ToString()),
                        ("boardName", "Fixture Board"), ("rollNumber", "22"),
                        ("marksObtained", "70"), ("marksOutOf", "100"), ("otherSubjects", "English") })
                        Field<ModernTextBox>(entry.Item1).Text = entry.Item2;
                }
                Call("ApplyWorkflowState");
                Check(Field<ModernButton>("createCertificateButton").Enabled, kind + " valid fixture is ready");
                var pending = (Task)Call("RunCreateCertificateAsync")!;
                Check(writes == 1 && !pending.IsCompleted && ((IBusyPage)page).IsBusy && !page.Enabled,
                    kind + " locks all inputs while the actual write is delayed");
                if (kind == "Sports") Field<ModernTextBox>("performance").Text = "Forced late event";
                else if (kind == "Provisional") Field<ModernTextBox>("boardName").Text = "Forced late event";
                else Field<ModernDatePicker>("certificateDate").Value = DateTime.Today;
                Check(!student.Enabled && !Field<ModernButton>("createCertificateButton").Enabled,
                    kind + " field events cannot re-enable submission or student switching");
                Check(((Task)Call("RunCreateCertificateAsync")!).IsCompleted && writes == 1,
                    kind + " second submission is refused without a second write");
                foreach (string action in new[] { "ClearForm", "RefreshCertificates",  "UpdateCertificate" }) Call(action);
                Check(student.SelectedId == 41 && reads == 0 && ((IBusyPage)page).IsBusy,
                    kind + " direct actions cannot clear, reload, update or release the active operation");
                string original = kind == "Sports" ? "Team captain" : kind == "Provisional" ? "Fixture Board" : "";
                if (original.Length > 0)
                    Check(Convert.ToString(submitted!.Single(p => p.ParameterName == (kind == "Sports" ? "@Performance" : "@BoardName")).Value) == original,
                        kind + " submitted parameters retain the captured values");
                reply.SetResult(2);
                PumpUntil(() => pending.IsCompleted, kind + " delayed submission settles");
                pending.GetAwaiter().GetResult();
                Check(writes == 1 && reads == 1 && page.Enabled && !((IBusyPage)page).IsBusy,
                    kind + " restores the page only after the original write and refresh finish");
                var status = (Label)typeof(V2Page).GetField("StatusLabel", flags)!.GetValue(page)!;
                Check(status.Text.Contains("created successfully"), kind + " completion keeps its explicit save result after controls are restored");
                string selectedField = kind == "Sports" ? "selectedSportsCertificateId" : kind == "Provisional" ? "selectedProvisionalCertificateId" : "selectedNoObjectionCertificateId";
                Check(Field<int>(selectedField) == 2, kind + " selects the confirmed backdated save instead of the first date-sorted row");
            }
            CheckTeacherCertificateSavedIdentity();
        }
        finally { CurrentUser.SignIn(previous.UserId, previous.Username, previous.RoleName, previous.MustChangePassword, previous.Item5); }
    }

    private static void CheckTeacherCertificateSavedIdentity()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        const string key = "RegularLecturerCertificateId";
        foreach (int returnedId in new[] { 2, 1 })
        {
            var rows = new DataTable();
            rows.Columns.Add(key, typeof(int)); rows.Columns.Add("CertificateDate", typeof(DateTime));
            rows.Columns.Add("RoleTitle"); rows.Columns.Add("SubjectTaught");
            rows.Rows.Add(1, DateTime.Today, "Teacher", "Chemistry");
            rows.Rows.Add(2, DateTime.Today.AddDays(-1), "Lecturer", "Physics");
            using var page = new FMSC_SMS.Pages.Staff.Certificates.RegularLecturerCertificatePage(false,
                _ => { var saved = new DataTable(); saved.Columns.Add(key, typeof(int)); saved.Rows.Add(returnedId); return saved; },
                _ => rows.Copy());
            page.BindingContext = new BindingContext();
            var type = page.GetType();
            T Field<T>(string name) => (T)type.GetField(name, flags)!.GetValue(page)!;
            type.GetField("suppressStaffChanged", flags)!.SetValue(page, true);
            Field<StaffPicker>("staff").SetStaff(41, "Fictional teacher");
            type.GetField("suppressStaffChanged", flags)!.SetValue(page, false);
            Field<ModernTextBox>("roleTitle").Text = "Lecturer";
            Field<ModernTextBox>("subjectTaught").Text = "Physics";
            Field<ModernDatePicker>("certificateDate").Value = DateTime.Today.AddDays(-1);
            type.GetMethod("CreateCertificate", flags)!.Invoke(page, null);
            Check(Field<int>("selectedRegularLecturerCertificateId") == returnedId,
                "Teacher creation selects the exact returned identity for backdated and deduplicated saves");
        }
        bool refused = false;
        try { CertificateRecordOrder.RequireSavedCopy(new DataTable(), key, 2); }
        catch (InvalidOperationException ex) { refused = DbMessage.UserMessage(ex).Contains("was saved") && DbMessage.UserMessage(ex).Contains("do not create it again"); }
        Check(refused, "Missing saved-copy history cannot silently substitute a different certificate or invite duplicate creation");
    }
}
