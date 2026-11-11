using System.Reflection;
using FMSC_SMS;
using FMSC_SMS.Core;
using FMSC_SMS.Pages.Security;
using DashboardNavButton = FMSC_SMS.MainDashboardForm.DashboardNavButton;

internal static partial class Program
{
    private static void CheckSidebarGroups()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var previous = (CurrentUser.UserId, CurrentUser.Username, CurrentUser.RoleName,
            CurrentUser.MustChangePassword, CurrentUser.GetPermissions().ToArray());
        try
        {
            CurrentUser.SignIn(990, "Navigation fixture", "Administrator", false, Array.Empty<string>());
            using var shell = new FMSC_SMS.MainDashboardForm((_, _) => { });
            var type = shell.GetType();
            void VerifyMenus()
            {
                // Inspect actual user-visible navigation, not the sidebar's
                // private field, first-three ordering, or an exact six-item menu.
                var headers = DescendantControls(shell).OfType<DashboardNavButton>()
                    .Where(button => button.IsModule).ToArray();
                var moduleNames = headers.Select(button => button.Text).ToArray();
                Check(new[] { "Daily Work", "Fees", "Students" }
                    .All(moduleNames.Contains), "Common work, fees and student navigation remain accessible");
                string[] Links(string title)
                {
                    var module = headers.Single(button => button.Text == title);
                    return DescendantControls(module.Parent!).OfType<DashboardNavButton>()
                        .Where(button => button.IsChild).Select(button => button.Text).ToArray();
                }
                string[] shortcuts = headers.SelectMany(header =>
                    DescendantControls(header.Parent!).OfType<DashboardNavButton>()
                        .Where(button => button.IsChild).Select(button => button.Text)).ToArray();
                Check(new[] { PageTitles.Admissions, "Student Profile", PageTitles.StudentRecordChecks,
                    "Fee Collection", PageTitles.OutstandingBills, PageTitles.FeePosting,
                    PageTitles.FeeRateSetup, PageTitles.StudentCertificates,
                    PageTitles.AnnualPromotion, PageTitles.PlacementHistory }
                    .All(shortcuts.Contains), "Authorized school work stays navigable without fixing menu grouping or order");
                Check(Links("Daily Work").Distinct(StringComparer.Ordinal).Count() == Links("Daily Work").Length,
                    "Common shortcuts do not repeat the same destination");
                var commands = (List<NavigationCommand>)type.GetField("navigationCommands", flags)!.GetValue(shell)!;
                Check(commands.Select(c => c.Caption).Distinct().Count() == commands.Count,
                    "Daily aliases do not duplicate command-palette destinations");
                Check(commands.Count(c => c.Caption == "Fee Collection") == 1 &&
                    commands.Count(c => c.Caption == PageTitles.Admissions) == 1,
                    "Common shortcuts share one route command");
                var targets = (Dictionary<Keys, DashboardNavButton>)type.GetField("navigationShortcutTargets", flags)!.GetValue(shell)!;
                foreach (var expected in AppHotKeys.DashboardNavigation.Where(binding =>
                    binding.Caption is "Fees" or "Students"))
                    Check(targets.TryGetValue(expected.Keys, out var target)
                        && target.Text == expected.Caption,
                        "Documented navigation shortcut reaches " + expected.Caption);
            }
            VerifyMenus();
            type.GetMethod("RebuildSidebar", flags)!.Invoke(shell, null);
            VerifyMenus();
            Check(!shell.IsHandleCreated, "Sidebar checks never start application database loaders");
            CurrentUser.SignIn(991, "Restricted fixture", "Operator", false, Array.Empty<string>());
            using var restricted = new FMSC_SMS.MainDashboardForm((_, _) => { });
            var restrictedCommands = (List<NavigationCommand>)type.GetField("navigationCommands", flags)!.GetValue(restricted)!;
            var restrictedSidebar = DescendantControls(restricted).OfType<DashboardNavButton>()
                .Where(button => button.IsChild).Select(button => button.Text).ToArray();
            var protectedDestinations = new[] { "Fee Collection", PageTitles.FeePosting, PageTitles.Admissions };
            Check(!restrictedCommands.Any(command => protectedDestinations.Contains(command.Caption))
                && !restrictedSidebar.Any(protectedDestinations.Contains),
                "Neither the visible menus nor the command palette expose unauthorized destinations");
        }
        finally { CurrentUser.SignIn(previous.UserId, previous.Username, previous.RoleName, previous.MustChangePassword, previous.Item5); }
    }
}
