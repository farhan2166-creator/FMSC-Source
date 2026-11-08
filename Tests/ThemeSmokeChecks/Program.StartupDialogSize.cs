using FMSC_SMS.Core;
using FMSC_SMS.Core.Updates;
using FMSC_SMS.Pages.Security;

internal static partial class Program
{
    private static void CheckStartupDialogSize()
    {
        // Construction-only smoke test: never open a database connection or
        // start an installer just to inspect the four startup window sizes.
        Size reference = AppMetrics.StartupDialogClientSize;
        Check(reference.Width >= AppScale.Scale(520) && reference.Height >= AppScale.Scale(340),
            "Startup windows retain usable space without fixing an exact design size");

        using var login = new LoginForm((_, _) => (false, "fixture", null));
        using var connection = new DatabaseConnectionForm(startup: true);
        using var update = new UpdateConnectionForm(() => (true, "ssh-rsa fixture"));
        using var unapprovedUpdate = new UpdateConnectionForm(() => (false, ""));

        void Usable(Form dialog, string task) =>
            Check(dialog.ClientSize.Width >= AppScale.Scale(520) &&
                  dialog.ClientSize.Height >= AppScale.Scale(340),
                task + " is usable without assuming all startup windows share one size");
        Usable(login, "Login");
        Usable(connection, "Startup connection");
        Usable(update, "Authorized update");
        Usable(unapprovedUpdate, "Computer enrollment");

        // The installer PowerShell UI intentionally shares this baseline for
        // both first-time installation and prepared updates (core.ps1).
    }
}
