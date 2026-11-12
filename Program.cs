using FMSC_SMS.Core;
namespace FMSC_SMS;
internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
#if FMSC_LEVEL2
        if (args.SequenceEqual(new[]{"--verify-level2-guards"}))
        {
            Environment.ExitCode = Level2Target.VerifyRejections();
            return;
        }
        // Fail before any UI or operational file/engine access. Normal startup,
        // first-run checks, authentication and operator forms remain unchanged.
        Level2Target.ValidateSchema();
        if (args.Length != 0) throw new InvalidOperationException("Installation commands are disabled in Level 2.");
#else
        // A normal binary must reject the test-only probe before normal startup
        // can access its operational database. This also detects a wrong build.
        if (args.SequenceEqual(new[]{"--verify-level2-guards"}))
        {
            Environment.ExitCode = 2;
            return;
        }
#endif
        if (args.Length > 0 && args[0] == "--verify-installation")
        {
            Environment.ExitCode = DatabaseInstallationVerification.Run(args);
            return;
        }
        // A retired installer command must fail headlessly rather than open
        // the normal login window or re-create optional communication services.
        if (args.Length > 0 && args[0] == "--install-communication")
        {
            Environment.ExitCode = DatabaseInstallationVerification.Failed;
            return;
        }
        // The enrolled Windows Task Scheduler identity runs this command with
        // its protected cloud credentials and local SQL permissions. It never
        // opens the login window or exposes student data to the command line.
#if !FMSC_LEVEL2
        if (args.SequenceEqual(new[] { "--cloud-backup-scheduled" }))
        {
            AppExceptionHandler.Register();
            Environment.ExitCode = Services.Backup.CloudBackupCoordinator
                .RunScheduledAsync().GetAwaiter().GetResult();
            return;
        }
#endif
        ApplicationConfiguration.Initialize();
        // Headless installation verification and scheduled backups above remain independent.
        // Keep ownership on this STA thread until every interactive window has closed.
        using var instance = SingleInstanceStartup.Enter();
        if (instance is null) return;
#if !FMSC_LEVEL2
        if (!FMSC_SMS.Core.Updates.UpdateStartupGuard.AllowStartup()) return;
#endif
        AppExceptionHandler.Register();
        MainDashboardForm.PrimeInitialScale();
        using var login = new LoginForm(checkConnectionOnStartup: true);
        var loginResult = login.ShowDialog();
        if (login.PendingUpdate is { } prepared)
        {
            try { FMSC_SMS.Core.Updates.UpdateInstallerHandoff.Launch(prepared); }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or
                InvalidOperationException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
            {
                ModernMessageDialog.ShowWarning(null,
                    "The installer could not start. Reopen FMSC and choose Check for Updates to try again. " + error.Message,
                    "FMSC Update");
            }
            return;
        }
        if (loginResult != DialogResult.OK)
            return;
        if (CurrentUser.MustChangePassword)
        {
            using var change = new ChangePasswordForm();
            if (change.ShowDialog() != DialogResult.OK)
            {
                ModernMessageDialog.ShowInfo(
                    null,
                    "Your account requires a password change before you can continue. " +
                    "FMSC will now close - please sign in again to set a new password.",
                    "Password Change Required");
                return;
            }
        }
        Application.Run(new MainDashboardForm());
    }
}
