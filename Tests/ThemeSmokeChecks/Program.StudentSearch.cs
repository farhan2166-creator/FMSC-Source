using System.Data;
using System.Reflection;
using FMSC_SMS.Core;
using FMSC_SMS.Pages.Security;
using FMSC_SMS.Pages.Students.Dialogs;
using Microsoft.Data.SqlClient;

internal static partial class Program
{
    private static void CheckStudentSearchModes()
    {
        CheckStudentPickerMatching();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var previous = (CurrentUser.UserId, CurrentUser.Username, CurrentUser.RoleName,
            CurrentUser.MustChangePassword, CurrentUser.GetPermissions().ToArray());
        DataTable Lookup(string id, string name)
        {
            var table = new DataTable(); table.Columns.Add(id, typeof(int)); table.Columns.Add(name);
            table.Rows.Add(11, "Fixture first"); table.Rows.Add(12, "Fixture second"); return table;
        }
        DataTable Reference(string procedure) => procedure switch {
            "GetCampuses" => Lookup("CampusId", "CampusName"),
            "GetAcademicYears" => Lookup("AcademicYearId", "YearName"),
            "GetClasses" => Lookup("ClassId", "ClassName"),
            _ => throw new InvalidOperationException("Unexpected fixture procedure: " + procedure)
        };
        DataTable Students()
        {
            var table = new DataTable();
            foreach (string name in new[] { "AdmissionId", "CurrentCampusId", "AcademicYearId", "ClassId" })
                table.Columns.Add(name, typeof(int));
            foreach (string name in new[] { "AdmissionNumber", "StudentName", "FatherName", "Status" }) table.Columns.Add(name);
            table.Rows.Add(41, 11, 11, 11, "91001", "Synthetic Ava", "Synthetic Guardian", "Active");
            table.Rows.Add(42, 12, 12, 12, "91002", "Synthetic Ben", "Synthetic Guardian", "Left");
            return table;
        }
        try
        {
            CurrentUser.SignIn(940, "Search fixture", "Administrator", false, Array.Empty<string>());
            var calls = new List<SqlParameter[]>();
            TaskCompletionSource<DataTable>? pending = null;
            Task<DataTable> Read(string procedure, SqlParameter[] parameters)
            {
                if (procedure != "GetStudents") return Task.FromResult(Reference(procedure));
                calls.Add(parameters);
                return pending?.Task ?? Task.FromResult(Students());
            }
            using var dialog = new StudentSearchDialog(null, false, Read);
            T Field<T>(string name) => (T)typeof(StudentSearchDialog).GetField(name, flags)!.GetValue(dialog)!;
            Task Search() => (Task)typeof(StudentSearchDialog).GetMethod("SearchAsync", flags)!.Invoke(dialog, null)!;
            void Finish(Task task) { PumpUntil(() => task.IsCompleted, "Student search read settles"); task.GetAwaiter().GetResult(); }
            void Empty(string reason) => Check(Field<DataGridView>("resultsGrid").Rows.Count == 0 &&
                !Field<ModernButton>("openButton").Enabled && dialog.SelectedAdmissionId == 0 && dialog.NewAdmissionNumber is null, reason);
            int Value(string name) => Convert.ToInt32(calls.Last().Single(p => p.ParameterName == name).Value);
            dialog.Show(); Application.DoEvents();
            var text = Field<ModernTextBox>("searchBox");
            var campus = Field<LookupBox>("searchCampus");
            var year = Field<LookupBox>("searchYear");
            var cls = Field<LookupBox>("searchClass");
            var status = Field<ModernComboBox>("searchStatus");
            var grid = Field<DataGridView>("resultsGrid");
            campus.SelectedValue = 11; year.SelectedValue = 11; cls.SelectedValue = 11; status.SelectedIndex = 1;
            Finish(Search());
            Check(grid.Rows.Count == 1 && Field<ModernButton>("openButton").Enabled, "Combined filters select a fictional active student");
            int before = calls.Count;
            text.Text = "9100";
            Check(campus.SelectedId == 0 && year.SelectedId == 0 && cls.SelectedId == 0 && status.SelectedIndex == 0,
                "Typing an admission number clears every placement and status filter");
            Empty("Typing clears old results and their open action immediately");
            Check(calls.Count == before, "Changing criteria does not execute an implicit query");
            Finish(Search());
            Check(grid.Rows.Count == 2 && Value("@CampusId") == 0 && Value("@AcademicYearId") == 0 &&
                Value("@ClassId") == 0 && Convert.ToBoolean(calls.Last().Single(p => p.ParameterName == "@IncludeFormerStudents").Value),
                "Admission prefix search still finds both active and former records independently");
            foreach (string filter in new[] { "searchCampus", "searchYear", "searchClass", "searchStatus" })
            {
                text.Text = "9100"; Finish(Search());
                Field<ModernComboBox>(filter).SelectedIndex = 1;
                Check(text.Text.Length == 0 && Field<ModernComboBox>(filter).SelectedIndex == 1,
                    "Each filter clears admission text while retaining the newly chosen filter");
                Empty("Each changed filter clears old rows and disables Open Student");
                Finish(Search());
                Check(grid.Rows.Count == 1, "Searching after a filter change uses that filter instead of the previous admission number");
            }
            text.Text = "9100"; campus.SelectedValue = 11; year.SelectedValue = 11; cls.SelectedValue = 11; status.SelectedIndex = 1;
            Check(text.Text.Length == 0 && campus.SelectedId == 11 && year.SelectedId == 11 && cls.SelectedId == 11,
                "Successive filter changes preserve other filters");
            text.Text = "Synthetic Ava"; Finish(Search());
            Check(grid.Rows.Count == 1 && campus.SelectedId == 11 && year.SelectedId == 11 && cls.SelectedId == 11,
                "Name search keeps its existing combined-filter capability");
            text.Text = "99999"; Finish(Search());
            Check(Field<ModernButton>("openButton").Text == "New Admission" && Field<ModernButton>("openButton").Enabled,
                "An authorized missing number retains the existing New Admission shortcut");
            campus.SelectedValue = 11; Empty("Switching to filters removes the old missing-number shortcut");
            Check(Field<ModernButton>("openButton").Text == "Open Student", "Changed criteria reset the action caption");
            text.Text = "9100";
            pending = new TaskCompletionSource<DataTable>(); var stale = Search();
            campus.SelectedValue = 11;
            pending.SetResult(Students()); Finish(stale); pending = null;
            Empty("A delayed admission response cannot restore results after switching to filters");
            Check(Field<Label>("status").Text.Contains("Choose Search"), "A discarded reply preserves the next-step guidance");
            Finish(Search()); Check(grid.Rows.Count == 1, "A fresh filter search works after the discarded response");
            pending = new TaskCompletionSource<DataTable>(); stale = Search();
            text.Text = "9100"; text.Clear(); text.Text = "9100";
            pending.SetResult(Students()); Finish(stale); pending = null;
            Empty("A delayed filtered reply cannot restore rows after switching to admission search");
            pending = new TaskCompletionSource<DataTable>(); stale = Search();
            text.Text = "91002"; text.Text = "9100";
            pending.SetResult(Students()); Finish(stale); pending = null;
            Empty("Changing away and back still invalidates an earlier response");
            Finish(Search()); Field<ModernButton>("resetButton").PerformClick();
            Empty("Reset clears the current results and action");
            Check(text.Text.Length == 0 && campus.SelectedId == 0 && year.SelectedId == 0 && cls.SelectedId == 0 && status.SelectedIndex == 0,
                "Reset clears both independent modes without recursive events");
            before = calls.Count; Finish(Search()); Check(calls.Count == before, "Blank criteria do not query every student");
            status.SelectedIndex = 1; Finish(Search());
            Check(grid.Rows.Count == 1 && Value("@CampusId") == 0, "Active Only is a usable filter on its own");
            dialog.Close();

            var lookupReplies = new Dictionary<string, TaskCompletionSource<DataTable>>();
            using var loading = new StudentSearchDialog(null, false, (procedure, _) => {
                var reply = new TaskCompletionSource<DataTable>(); lookupReplies.Add(procedure, reply); return reply.Task;
            });
            loading.Show();
            var loadingText = (ModernTextBox)typeof(StudentSearchDialog).GetField("searchBox", flags)!.GetValue(loading)!;
            loadingText.Text = "9100";
            foreach (var (procedure, reply) in lookupReplies) reply.SetResult(Reference(procedure));
            PumpUntil(() => ((LookupBox)typeof(StudentSearchDialog).GetField("searchClass", flags)!.GetValue(loading)!).Items.Count == 3,
                "Delayed lookup binding completes");
            Check(loadingText.Text == "9100", "Initial lookup binding cannot erase an admission number typed while loading");
            loading.Close();
        }
        finally { CurrentUser.SignIn(previous.UserId, previous.Username, previous.RoleName, previous.MustChangePassword, previous.Item5); }
    }

    private static void CheckStudentPickerMatching()
    {
        using var source = new DataTable();
        source.Columns.Add("AdmissionId", typeof(int));
        foreach (string field in new[] { "AdmissionNumber", "StudentName", "FatherName", "RegistrationNumber" })
            source.Columns.Add(field);
        source.Rows.Add(41, "9100", "Synthetic Ava", "Muhammad Sajid", "REG-41");
        source.Rows.Add(42, "91002", "Synthetic Ben", "Synthetic Guardian", "REG-42");
        source.Rows.Add(43, "29100", "Fixture 9100", DBNull.Value, DBNull.Value);
        source.Rows.Add(44, "9100a", "Fixture Sara", "Fixture Father", "REG-44");
        using var picker = new StudentPicker();
        int[] Matches(string term) => picker.FindMatches(source, term).Entries.Select(entry => entry.Id).ToArray();

        Check(Matches("  aVA ").SequenceEqual(new[] { 41 }),
            "Student Picker matches a name anywhere regardless of case or surrounding search spaces");
        Check(Matches("aji").SequenceEqual(new[] { 41 }),
            "Student Picker matches an interior substring of a father's name");
        Check(Matches("9100").SequenceEqual(new[] { 41, 44, 42 }) &&
            Matches("09100").SequenceEqual(new[] { 41, 44, 42 }),
            "Student Picker puts the exact admission number first and only matches admission numbers from the start");
        Check(Matches("9100A").SequenceEqual(new[] { 44 }),
            "Student Picker accepts the admission number suffix regardless of case");
        Check(Matches("reg-4").SequenceEqual(new[] { 41, 44, 42 }) && Matches("G-41").Length == 0,
            "Student Picker retains registration number prefix matching");
    }
}
