using System.Data;
using System.Reflection;
using FMSC_SMS.Core;
using FMSC_SMS.Pages.Security;

internal static partial class Program
{
    private static void CheckDashboard()
    {
        var previous = (CurrentUser.UserId, CurrentUser.Username, CurrentUser.RoleName,
            CurrentUser.MustChangePassword, Permissions: CurrentUser.GetPermissions().ToArray());
        var theme = AppTheme.CurrentTheme;
        var type = typeof(ApplicationWindowForm).Assembly.GetType("FMSC_SMS.Pages.Dashboard.WelcomePage", true)!;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Control[] descendants(Control parent) => parent.Controls.Cast<Control>()
            .SelectMany(child => new[] { child }.Concat(descendants(child))).ToArray();
        DataTable Summary(int value)
        {
            var table = new DataTable();
            foreach (string name in new[] { "TotalStudents", "ActiveStudents", "TotalClasses", "ActiveStaff", "NewAdmissionsThisMonth" })
                table.Columns.Add(name, typeof(int));
            table.Rows.Add(value, value, value, value, value); return table;
        }
        DataTable Years()
        {
            var table = new DataTable(); table.Columns.Add("YearName", typeof(string)); table.Columns.Add("IsCurrent", typeof(bool));
            table.Rows.Add("Fixture year", true); return table;
        }
        try
        {
            CurrentUser.SignIn(7777, "Dashboard fixture", "Operator", false, new[] {
                "Reports.View", "Students.Admit", "Students.View", "Fees.Collect", "System.Setup", "Calendar.Manage", "Fees.Configuration.Manage" });
            var queries = new List<(string Procedure, CancellationToken Token, TaskCompletionSource<DataTable> Result)>();
            var readiness = new List<(CancellationToken Token, TaskCompletionSource<DataTable> Result)>();
            Func<string, CancellationToken, Task<DataTable>> query = (procedure, token) => {
                var result = new TaskCompletionSource<DataTable>(TaskCreationOptions.RunContinuationsAsynchronously);
                queries.Add((procedure, token, result)); return result.Task;
            };
            Func<CancellationToken, Task<DataTable>> ready = token => {
                var result = new TaskCompletionSource<DataTable>(TaskCreationOptions.RunContinuationsAsynchronously);
                readiness.Add((token, result)); return result.Task;
            };
            (string Module, string Title) route = default;
            Action<Func<Control>> fallback = _ => throw new Exception("Expected module-aware navigation");
            Action<Func<Control>, string, string> navigate = (_, module, title) => route = (module, title);
            using var window = new Form { ClientSize = new Size(1080, 620), AutoScaleMode = AutoScaleMode.None };
            using var page = (UserControl)Activator.CreateInstance(type, flags, null, new object[] { fallback, navigate, query, ready }, null)!;
            window.Controls.Add(page); window.Show();
            PumpUntil(() => queries.Count == 2 && readiness.Count == 1, "All three dashboard sections start independently");
            queries.Single(read => read.Procedure == "GetDashboardSummary").Result.SetResult(Summary(0));
            PumpUntil(() => descendants(page).OfType<Label>().Count(label => label.Text == "0") >= 5,
                "Real zero totals paint while academic year and readiness are still pending");
            Check(!page.HorizontalScroll.Visible, "Dashboard remains readable without horizontal scrolling");
            var actions = descendants(page).OfType<TableLayoutPanel>().First(panel =>
                panel.Controls.OfType<ModernButton>().Any(button => button.Text == "Student Profile"));
            var availableActions = actions.Controls.OfType<ModernButton>()
                .Select(button => button.Text).ToHashSet(StringComparer.Ordinal);
            Check(new[] { PageTitles.Admissions, "Fee Collection", "Student Profile",
                    "Quick Registration", "Student Reports", "Reports" }.All(availableActions.Contains),
                "Core daily actions remain available regardless of their visual order or additional shortcuts");
            foreach (var button in actions.Controls.OfType<ModernButton>())
                Check(button.Height >= button.MinimumSize.Height && button.Bottom <= actions.ClientSize.Height,
                    "Quick actions remain within the available workspace");
            window.ClientSize = new Size(1080, 360); Application.DoEvents();
            Check(page.VerticalScroll.Visible && !page.HorizontalScroll.Visible, "Very small windows retain vertical access to every section");
            window.ClientSize = new Size(1080, 620); Application.DoEvents();
            var header = page.Controls[0].Controls[0];
            foreach (var label in header.Controls.OfType<Label>())
                Check(label.Height >= AppMetrics.TextHeight(label.Font), "Dashboard greeting text has its full measured height");
            foreach (var value in descendants(page).OfType<Label>().Where(label => label.Text == "0"))
                Check(value.Height >= AppMetrics.TextHeight(value.Font), "Dashboard values are not clipped by default label margins");
            queries.Single(read => read.Procedure == "GetAcademicYears").Result.SetException(new InvalidOperationException("Fixture year unavailable"));
            readiness[0].Result.SetResult(new DataTable());
            var setup = (Control)type.GetField("setupReadinessBar", flags)!.GetValue(page)!;
            PumpUntil(() => !setup.Visible, "A completed setup check releases its dashboard space");
            Check(descendants(page).OfType<Label>().Count(label => label.Text == "0") >= 5,
                "An academic-year failure cannot erase valid totals");
            var profile = descendants(page).OfType<ModernButton>().Single(button => button.Text == "Student Profile");
            profile.PerformClick(); Check(route == ("Students", "Student Profile"), "Profile quick actions preserve the sidebar route");
            AppTheme.SetTheme(AppThemeType.Emerald); Application.DoEvents();
            Check(descendants(page).OfType<Label>().Where(label => label.Text == "0").All(label => label.ForeColor == AppTheme.Primary),
                "Compact dashboard figures follow the current theme");
            using (var bitmap = new Bitmap(window.Width, window.Height))
                window.DrawToBitmap(bitmap, window.ClientRectangle);
            Check(queries.Count == 2 && readiness.Count == 1, "Theme changes and quick-action routing do not reload dashboard data");

            var reload = (Task)type.GetMethod("LoadDashboardDataAsync", flags)!.Invoke(page, null)!;
            Check(queries.Count == 4 && readiness.Count == 2 && queries[0].Token.IsCancellationRequested,
                "A new dashboard read cancels its predecessor");
            using var issues = new DataTable(); issues.Columns.Add("Issue");
            issues.Rows.Add("Profile"); issues.Rows.Add("Year"); issues.Rows.Add("Fee rates");
            readiness[1].Result.SetResult(issues);
            var setupStatus = (Label)type.GetField("setupStatus", flags)!.GetValue(page)!;
            PumpUntil(() => setup.Visible && setupStatus.Text.Contains("3 items need attention"), "Setup uses one counted status bar when attention is needed");
            descendants(setup).OfType<ModernButton>().Single().PerformClick();
            Check(route == ("School Setup", PageTitles.SetupReadiness), "The status bar opens the existing readiness workflow");
            var labels = descendants(page).OfType<Label>().Select(label => (Label: label, label.Text)).ToArray();
            page.Dispose();
            Check(queries.Skip(2).All(read => read.Token.IsCancellationRequested) && readiness[1].Token.IsCancellationRequested,
                "Leaving the dashboard cancels all pending reads");
            queries[2].Result.SetResult(Summary(99)); queries[3].Result.SetResult(Years());
            PumpUntil(() => reload.IsCompleted, "Late fake reads finish safely after dashboard disposal");
            Check(labels.All(item => item.Label.Text == item.Text), "Late completions cannot repaint the disposed page");
            Check(profile.IsDisposed, "Dashboard shortcuts dispose with the page regardless of icon implementation");

            CurrentUser.SignIn(7778, "Restricted fixture", "Operator", false, new[] { "Staff.Manage" });
            int summaryReads = 0, readinessReads = 0;
            query = (procedure, _) => { if (procedure == "GetDashboardSummary") summaryReads++; return Task.FromResult(Years()); };
            ready = _ => { readinessReads++; return Task.FromResult(new DataTable()); };
            using var restricted = (UserControl)Activator.CreateInstance(type, flags, null, new object[] { fallback, navigate, query, ready }, null)!;
            window.Controls.Add(restricted);
            PumpUntil(() => descendants(restricted).OfType<Label>().Count(label => label.Text == "Report access required") >= 5,
                "A restricted operator sees a clear totals-access state");
            Check(summaryReads == 0 && readinessReads == 0, "Restricted dashboards do not issue unauthorized summary/setup reads");
        }
        finally
        {
            CurrentUser.SignIn(previous.UserId, previous.Username, previous.RoleName, previous.MustChangePassword, previous.Permissions);
            AppTheme.SetTheme(theme);
        }
    }
}
