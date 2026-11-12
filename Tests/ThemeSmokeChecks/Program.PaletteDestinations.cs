using System.Reflection;
using FMSC_SMS;
using FMSC_SMS.Core;
using FMSC_SMS.Pages.Security;

internal static partial class Program
{
    private static void CheckPaletteDestinations()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var previous = (CurrentUser.UserId, CurrentUser.Username, CurrentUser.RoleName,
            CurrentUser.MustChangePassword, CurrentUser.GetPermissions().ToArray());
        CurrentUser.SignIn(901, "Fixture", "Operator", false, new[] { "Fixture.Open" });
        try
        {
            using var shell = new MainDashboardForm((_, _) => { });
            var type = shell.GetType();
            type.GetField("_sidebarCompact", flags)!.SetValue(shell, true);
            using var hidden = new FlowLayoutPanel { Visible = false };
            int opens = 0;
            Func<Control> factory = () => { opens++; return new Panel(); };
            var add = type.GetMethod("AddMainNav", flags)!;
            add.Invoke(shell, new object?[] { hidden, "dashboard.png", "Synthetic destination", Color.Blue, factory, "Fixture", "Synthetic", "Fixture.Open", null, null });
            add.Invoke(shell, new object?[] { hidden, "dashboard.png", "Forbidden destination", Color.Blue, factory, "Fixture", "Forbidden", "Fixture.Denied", null, null });
            Check(hidden.Controls.Count == 1 && !hidden.Controls[0].Visible && hidden.Controls[0].Text.Length == 0,
                "Fixture recreates a permitted destination hidden in a compact sidebar");
            var commands = (List<NavigationCommand>)type.GetField("navigationCommands", flags)!.GetValue(shell)!;
            Check(commands.All(c => c.Caption != "Forbidden destination"), "Palette registration excludes destinations without permission");
            bool dynamicAccess = true;
            add.Invoke(shell, new object?[] { hidden, "dashboard.png", "Conditional destination", Color.Blue, factory, "Fixture", "Conditional", null, null, (Func<bool>)(() => dynamicAccess) });
            var conditional = commands.Single(command => command.Caption == "Conditional destination");
            dynamicAccess = false;
            conditional.Execute();
            Check(opens == 0, "A provider-neutral permission predicate is rechecked before navigation");
            using var palette = new CommandPaletteDialog(commands, "synthetic destination");
            typeof(CommandPaletteDialog).GetMethod("Accept", flags)!.Invoke(palette, null);
            Check(palette.SelectedCommand?.Caption == "Synthetic destination", "Search finds a permitted destination even when its sidebar button is hidden and captionless");
            palette.SelectedCommand!.Execute();
            Check(opens == 1 && (string)type.GetField("_currentTitle", flags)!.GetValue(shell)! == "Synthetic",
                "Selected palette destination navigates without invisible Button.PerformClick");
            CurrentUser.SignIn(902, "Fixture without access", "Operator", false, Array.Empty<string>());
            palette.SelectedCommand.Execute(); Check(opens == 1, "A destination rechecks permission before dispatch");
        }
        finally { CurrentUser.SignIn(previous.UserId, previous.Username, previous.RoleName, previous.MustChangePassword, previous.Item5); }
    }
}
