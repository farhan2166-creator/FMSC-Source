using FMSC_SMS.Core;
using FMSC_SMS.Pages.Administration;

internal static partial class Program
{
    private static void CheckSingleInstanceActivation()
    {
        using var owner = new Form { Text = "Synthetic FMSC window", ShowInTaskbar = false };
        owner.Show(); Application.DoEvents();
        owner.WindowState = FormWindowState.Minimized; Application.DoEvents();
        _ = SingleInstanceStartup.TryActivateWindow(owner.Handle); Application.DoEvents();
        Check(owner.WindowState != FormWindowState.Minimized, "Opening FMSC again restores its existing minimized window");
        using var modal = new Form { Text = "Synthetic active dialog", ShowInTaskbar = false };
        modal.Shown += (_, _) => modal.BeginInvoke((Action)(() => {
            IntPtr target = SingleInstanceStartup.ActivationWindow(owner.Handle);
            Check(target == modal.Handle,
                "Repeated launch targets the active modal dialog; selected owner=" + (target == owner.Handle)
                + "; native owner enabled=" + InstanceNative.IsWindowEnabled(owner.Handle)
                + "; popup owned by main=" + (InstanceNative.GetWindow(modal.Handle, 4) == owner.Handle)
                + "; enabled popup is dialog=" + (InstanceNative.GetWindow(owner.Handle, 6) == modal.Handle));
            modal.Close();
        }));
        modal.ShowDialog(owner);
        Check(SingleInstanceStartup.ActivationWindow(owner.Handle) == owner.Handle,
            "After closing a dialog, activation returns to the existing main window");
        owner.Close();
        CheckRestoreRestartRequest();
    }

    private static class InstanceNative
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        internal static extern bool IsWindowEnabled(IntPtr window);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern IntPtr GetWindow(IntPtr window, uint command);
    }

    private static void CheckRestoreRestartRequest()
    {
        bool previous = FMSC_SMS.Program.ConsumeRestartRequest();
        try
        {
            using var shell = new Form { ShowInTaskbar = false };
            bool refuse = true;
            int warnings = 0;
            shell.FormClosing += (_, e) => e.Cancel = refuse;
            shell.Show(); Application.DoEvents();
            BackupRestorePage.RestartApplication(shell, () => warnings++);
            Check(!shell.IsDisposed && warnings == 1 && !FMSC_SMS.Program.ConsumeRestartRequest(),
                "A refused restore shutdown schedules no replacement and leaves the current session intact");
            refuse = false;
            BackupRestorePage.RestartApplication(shell, () => warnings++);
            Check(shell.IsDisposed && FMSC_SMS.Program.ConsumeRestartRequest(),
                "An accepted restore shutdown requests relaunch through the startup lifetime");
            Check(!FMSC_SMS.Program.ConsumeRestartRequest(), "A restore restart request is consumed once");
        }
        finally
        {
            FMSC_SMS.Program.ConsumeRestartRequest();
            if (previous) FMSC_SMS.Program.RequestRestart();
        }
    }
}
