using System.Diagnostics;
using System.Reflection;
using FMSC_SMS.Core;
using FMSC_SMS.Pages.Administration;
using FMSC_SMS.Pages.Security;
using FMSC_SMS.Services.Backup;

internal static partial class Program
{
    private static void CheckCloudConnectionCancellation()
    {
        if (CloudBackupOAuth.IsConnected(CloudProvider.GoogleDrive))
            throw new Exception("Cloud sign-in regression requires a disposable Windows profile without a real account.");
        const BindingFlags methods = BindingFlags.Instance | BindingFlags.NonPublic;
        int cancelled = 0, callbacks = 0;
        Func<CloudProvider,CancellationToken,Task> noBrowser = async (_, token) => {
            using var callback = new CloudOAuthLoopback();
            callbacks++;
            try { await callback.ReceiveCodeAsync("fictional-ui-state", token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { cancelled++; throw; }
        };
        void Exercise(Form dialog, Button cancel, Button connect, Func<Task> start, Func<string> status)
        {
            dialog.Show(); Application.DoEvents();
            foreach (bool closeWindow in new[] { false, true })
            {
                Task work = start();
                Check(!work.IsCompleted && cancel.Enabled && !connect.Enabled,
                    "Browser waiting keeps cancellation available and prevents duplicate sign-in attempts");
                bool heartbeat = false;
                dialog.BeginInvoke(new Action(() => heartbeat = true));
                Application.DoEvents();
                Check(heartbeat, "The Windows message pump remains responsive while no browser callback arrives");
                if (closeWindow) dialog.Close(); else cancel.PerformClick();
                var wait = Stopwatch.StartNew();
                while (!work.IsCompleted && wait.Elapsed < TimeSpan.FromSeconds(5))
                { Application.DoEvents(); Thread.Sleep(10); }
                Check(work.IsCompleted, "Cancelling abandoned browser sign-in completes promptly");
                work.GetAwaiter().GetResult();
                Check(!dialog.IsDisposed && dialog.Visible && connect.Enabled && cancel.Enabled,
                    "Cancelled sign-in restores controls and permits a fresh retry");
                Check(status().Contains("cancelled",StringComparison.OrdinalIgnoreCase),
                    "Cancelled sign-in is clearly reported without claiming a successful connection");
            }
            dialog.Close();
        }
        using (var settings = new CloudBackupSettingsDialog())
        {
            settings.ConnectAccountAsync = noBrowser;
            var connect = Field<ModernButton>(settings,"driveConnect");
            Exercise(settings, Field<ModernButton>(settings,"cancel"), connect,
                () => (Task)typeof(CloudBackupSettingsDialog).GetMethod("ToggleProviderAsync",methods)!
                    .Invoke(settings,new object[]{CloudProvider.GoogleDrive,connect})!,
                () => Field<Label>(settings,"connectionStatus").Text);
        }
        using (var recovery = new RestoreExistingSchoolForm())
        {
            Field<ComboBox>(recovery,"source").SelectedIndex = 1;
            recovery.ConnectAccountAsync = noBrowser;
            Exercise(recovery, Field<Button>(recovery,"close"), Field<Button>(recovery,"connect"),
                () => (Task)typeof(RestoreExistingSchoolForm).GetMethod("ConnectCloudAsync",methods)!.Invoke(recovery,null)!,
                () => Field<Label>(recovery,"status").Text);
        }
        Check(callbacks == 4 && cancelled == 4 && !CloudBackupOAuth.IsConnected(CloudProvider.GoogleDrive),
            "Cancel and window close release each pending sign-in without saving cloud credentials");
    }
}
