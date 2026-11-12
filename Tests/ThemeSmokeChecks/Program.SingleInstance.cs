using FMSC_SMS.Core;

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
        modal.Shown += (_, _) => {
            Check(!owner.Enabled && SingleInstanceStartup.ActivationWindow(owner.Handle) == modal.Handle,
                "Repeated launch targets the active modal dialog instead of its disabled owner");
            modal.Close();
        };
        modal.ShowDialog(owner);
        Check(SingleInstanceStartup.ActivationWindow(owner.Handle) == owner.Handle,
            "After closing a dialog, activation returns to the existing main window");
        owner.Close();
    }
}
