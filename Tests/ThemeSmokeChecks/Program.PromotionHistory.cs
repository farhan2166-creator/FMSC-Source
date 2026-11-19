using System.Data;
using System.Reflection;
using FMSC_SMS;
using FMSC_SMS.Core;
using FMSC_SMS.Pages.Security;
using FMSC_SMS.Pages.Students.Enrollment;
using Microsoft.Data.SqlClient;

internal static partial class Program
{
    private static void CheckPromotionHistoryEntry()
    {
        using(var catalog=new DataTable())
        {
            catalog.Columns.Add("ClassId",typeof(int));catalog.Columns.Add("AcademicProgramId",typeof(int));catalog.Columns.Add("DisplayOrder",typeof(int));
            catalog.Rows.Add(4,1,4);catalog.Rows.Add(10,1,10);catalog.Rows.Add(5,1,5);catalog.Rows.Add(20,2,1);
            Check(Convert.ToInt32(PromotionChoices.NextClass(catalog,4)!["ClassId"])==5,"Annual promotion uses the immediate next catalog class in the same program");
            Check(PromotionChoices.NextClass(catalog,10) is null,"The final class cannot jump into another program");
            Check(PromotionChoices.NextClass(catalog,999) is null,"A missing source class cannot choose an arbitrary destination");
        }
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var previous=(CurrentUser.UserId,CurrentUser.Username,CurrentUser.RoleName,CurrentUser.MustChangePassword,CurrentUser.GetPermissions().ToArray());
        try
        {
            foreach (var access in new[] { (Role: "Operator", Permissions: new[] { "Enrollment.Manage" }, Allowed: true),
                (Role: "Accountant", Permissions: new[] { "Students.View" }, Allowed: false),
                (Role: "Administrator", Permissions: Array.Empty<string>(), Allowed: true) })
            {
                CurrentUser.SignIn(910, "Promotion fixture", access.Role, false, access.Permissions);
                using var shell = new MainDashboardForm((_, _) => { });
                var commands = (List<NavigationCommand>)typeof(MainDashboardForm).GetField("navigationCommands", flags)!.GetValue(shell)!;
                foreach(var title in new[]{PageTitles.AnnualPromotion,PageTitles.PlacementTransfers,PageTitles.PlacementCorrections,PageTitles.PlacementHistory})
                    Check(commands.Count(c=>c.Caption==title)==(access.Allowed?1:0),"Placement pages keep enrollment permissions and the Administrator override.");
                Check(commands.All(c=>c.Caption is not ("Student Promotion" or "Bulk Promotion")),"Separate placement routes replace the former combined promotion destination.");
            }
            CurrentUser.SignIn(911,"Promotion history fixture","Operator",false,new[]{"Enrollment.Manage","Reports.View"});
            DataTable Lookup(string key, string text, params (int Id, string Name)[] rows)
            {
                var table = new DataTable(); table.Columns.Add(key, typeof(int)); table.Columns.Add(text, typeof(string));
                foreach (var row in rows) table.Rows.Add(row.Id, row.Name);
                return table;
            }
            var years = Lookup("AcademicYearId", "YearName", (103, "Fixture source year"), (104, "Fixture next year"));
            var classes = Lookup("ClassId", "ClassName", (606, "Fixture Class 6"), (607, "Fixture Class 7"));
            classes.Columns.Add("DisplayOrder",typeof(int));classes.Columns.Add("AcademicProgramId",typeof(int));
            for(int index=0;index<classes.Rows.Count;index++){classes.Rows[index]["DisplayOrder"]=index+6;classes.Rows[index]["AcademicProgramId"]=1;}
            var sections = Lookup("SectionId", "SectionName", (17, "Fixture Section A"), (18, "Fixture Section B"));
            var assignments = new DataTable();
            assignments.Columns.Add("ClassId",typeof(int));assignments.Columns.Add("SectionId",typeof(int));
            assignments.Rows.Add(606,17);assignments.Rows.Add(607,18);
            using var parent=new Form {ClientSize=new Size(1080,650),AutoScaleMode=AutoScaleMode.None};
            parent.Show();
            using var dialog=new ApplicationWindowForm {Text="Promotion History",ShowInTaskbar=false,StartPosition=FormStartPosition.CenterParent,Size=new Size(900,600)};
            var initial=(Year:103,Class:606,Section:17);
            var reads = new List<SqlParameter[]>();
            Func<string, SqlParameter[], DataTable> query = (procedure, parameters) => {
                if (procedure == "GetAcademicYears") return years;
                if (procedure == "GetClasses") return classes;
                if (procedure == "GetSections") return sections;
                if (procedure == "GetClassSections") return assignments;
                Check(procedure == "GetPromotionBatchesFiltered", "History retains its production read-only procedure contract");
                reads.Add(parameters);
                var batches = new DataTable(); batches.Columns.Add("PromotionBatchId", typeof(int));
                batches.Columns.Add("PromotedCount", typeof(int));batches.Columns.Add("HeldBackCount",typeof(int)); batches.Rows.Add(201, 2,1); return batches;
            };
            PromotionHistoryPage History(int year, int cls, int section) => (PromotionHistoryPage)Activator.CreateInstance(
                typeof(PromotionHistoryPage), flags, null, new object[] { year, cls, section, query }, null)!;
            using var history = History(initial.Year, initial.Class, initial.Section); history.Dock=DockStyle.Fill;dialog.Controls.Add(history);
            dialog.Show(parent);
            PumpUntil(() => reads.Count == 1, "The history window loads its carried source filters");
            Check(reads[0].Select(p => p.ParameterName).SequenceEqual(new[] { "@SourceAcademicYearId", "@SourceClassId", "@SourceSectionId" }) &&
                reads[0].Select(p => Convert.ToInt32(p.Value)).SequenceEqual(new[] { 103, 606, 17 }),
                "The first history query uses the selected source IDs");
            var historyType = typeof(PromotionHistoryPage);
            T HistoryField<T>(string name) => (T)historyType.GetField(name, flags)!.GetValue(history)!;
            var export = HistoryField<ModernButton>("exportButton");
            Check(export.Enabled && HistoryField<Label>("summary").Text.Contains("1 batch"), "Loaded history retains summary and export availability");
            Check(HistoryField<Label>("summary").Text.Contains("Repeated class: 1")
                &&((DataGridView)typeof(V2Page).GetField("Grid",flags)!.GetValue(history)!).Columns["HeldBackCount"].HeaderText=="Repeated class",
                "Promotion history gives repeat decisions a clear label in both the grid and summary");
            var sourceClass = HistoryField<LookupBox>("cls");
            var sourceSection = HistoryField<LookupBox>("section");
            sourceClass.SelectedValue = 607;
            Check(sourceSection.SelectedId == 0 && sourceSection.Items.Cast<DataRowView>()
                .Where(row => Convert.ToInt32(row["SectionId"]) > 0)
                .Select(row => Convert.ToInt32(row["SectionId"])).SequenceEqual(new[] { 18 }),
                "Promotion History sections follow the selected source class without stale choices");
            sourceClass.SelectedValue = 606;
            sourceSection.SelectedValue = 17;
            HistoryField<LookupBox>("year").SelectedValue = 104;
            Check(!export.Enabled && reads.Count == 1, "Changing history filters requires a fresh Load before export");
            historyType.GetMethod("LoadData", flags)!.Invoke(history, null);
            Check(export.Enabled && Convert.ToInt32(reads.Last()[0].Value) == 104, "Users can load a different year within the history window");
            historyType.GetMethod("ClearForm", flags)!.Invoke(history, null);
            Check(!export.Enabled && HistoryField<Label>("summary").Text.Length == 0, "Clear removes the displayed history and disables export");
            historyType.GetMethod("LoadData", flags)!.Invoke(history, null);
            Check(reads.Last().All(p => Convert.ToInt32(p.Value) == 0), "Blank history filters use the production procedure's zero-as-all contract");
            dialog.Close();
            Check(history.IsDisposed && !parent.IsDisposed, "Closing earlier promotion history disposes its page without closing its host");

            using var missing = History(999, 606, 17);
            int before = reads.Count; parent.Controls.Add(missing); missing.BringToFront();
            PumpUntil(() => ((LookupBox)historyType.GetField("year", flags)!.GetValue(missing)!).Items.Count > 0,
                "Stale carried filters bind the available lookup choices");
            Check(reads.Count == before && !(bool)((ModernButton)historyType.GetField("exportButton", flags)!.GetValue(missing)!).Enabled,
                "A missing carried filter refuses an accidental unfiltered history load");
            Check(((Label)typeof(V2Page).GetField("StatusLabel", flags)!.GetValue(missing)!).Text.Contains("no longer available"),
                "Unavailable carried filters give a clear recovery instruction");
            parent.Close();
        }
        finally { CurrentUser.SignIn(previous.UserId, previous.Username, previous.RoleName, previous.MustChangePassword, previous.Item5); }
    }
}
