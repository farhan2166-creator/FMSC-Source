using System.Reflection;
using FMSC_SMS;
using FMSC_SMS.Core;
using FMSC_SMS.Core.Updates;

internal static partial class Program
{
    private static void CheckUpdateDialog()
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        ModernButton Find(Control root, string text) => Descendants(root).OfType<ModernButton>().Single(b => b.Text == text);
        foreach (bool enrolled in new[] { false, true })
        {
            foreach (string action in new[] { "Close" })
            {
                using var parent = new Form(); parent.Show();
                using var dialog = new UpdateConnectionForm(() => (enrolled, enrolled ? "ssh-rsa fictional-public-key" : ""));
                dialog.Show(parent); Application.DoEvents();
                var area = Screen.FromControl(parent).WorkingArea;
                void CheckScreenCenter() => Check(
                    Math.Abs(dialog.Left + dialog.Width / 2 - (area.Left + area.Width / 2)) <= 2 &&
                    Math.Abs(dialog.Top + dialog.Height / 2 - (area.Top + area.Height / 2)) <= 2,
                    "Update window is centered on the owner's screen, including after access details change");
                CheckScreenCenter();
                Check(Find(dialog, "Check for Updates").Enabled == enrolled, "Checking requires a prepared computer key");
                var panel = (Panel)typeof(UpdateConnectionForm).GetField("_authorizationPanel", fields)!.GetValue(dialog)!;
                Check(!panel.Visible, "Authorization details start collapsed and expand only on request");
                var accessButton = Find(dialog, enrolled ? "Computer Key Ready" : "Authorize This Computer");
                var access = accessButton.Parent!;
                Check(Math.Abs(accessButton.PointToScreen(new Point(accessButton.Width / 2, 0)).X -
                    dialog.PointToScreen(new Point(dialog.ClientSize.Width / 2, 0)).X) <= 2,
                    "Single computer-access button is centered");
                Check(!Descendants(dialog).OfType<ModernButton>().Any(button => button.Text == "Remind Me Later") &&
                    Descendants(dialog).OfType<ModernButton>().Count(button => button.Text == "Close") == 1,
                    "Only one Close button is shown; no duplicate reminder/dismissal action");
                Check(!Descendants(dialog).OfType<Label>().Any(label => label.Text is
                    "Computer authorized" or "Computer key ready" or "Authorization required"),
                    "Computer authorization is explained by its button, not by a separate status label");
                Check(enrolled ? accessButton.Text == "Computer Key Ready" : accessButton.Text == "Authorize This Computer",
                    "A local key is never presented as confirmed authorization");
                var checkButton = Find(dialog, "Check for Updates");
                var center = checkButton.PointToScreen(new Point(checkButton.Width / 2, 0)).X;
                Check(Math.Abs(center - dialog.PointToScreen(new Point(dialog.ClientSize.Width / 2, 0)).X) <= 2,
                    "Check for Updates action is centered in the window");
                Check(Descendants(dialog).OfType<Label>().Where(label =>
                    label.Text is "Installed version" or "Latest published version" or "Not checked" or "Check for Updates")
                    .All(label => label.TextAlign == ContentAlignment.MiddleCenter),
                    "Heading and version labels use centered text");
                accessButton.PerformClick(); Application.DoEvents(); Check(panel.Visible, "Access button expands details"); CheckScreenCenter();
                accessButton.PerformClick(); Application.DoEvents(); Check(!panel.Visible, "Access button collapses details"); CheckScreenCenter();
                if (enrolled)
                {
                    typeof(UpdateConnectionForm).GetMethod("MarkComputerAuthorized", fields)!.Invoke(dialog, null);
                    Check(accessButton.Text == "Computer Authorized",
                        "An authenticated repository session changes the single button to Computer Authorized");
                }
                var buttons = Descendants(dialog).OfType<ModernButton>().Where(b => b.Visible).ToArray();
                foreach (var button in buttons)
                    Check(button.Right <= button.Parent!.ClientSize.Width && button.Bottom <= button.Parent.ClientSize.Height,
                        "Update controls fit their action rows: " + button.Text);
                if (action == "Close")
                {
                    string output = Path.Combine(Environment.CurrentDirectory, "artifacts", "update-dialog-review");
                    Directory.CreateDirectory(output);
                    using var bitmap = new Bitmap(dialog.Width, dialog.Height);
                    dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, dialog.Size));
                    bitmap.Save(Path.Combine(output, enrolled ? "authorized.png" : "enrollment.png"));
                }
                Find(dialog, action).PerformClick(); Application.DoEvents();
                Check(dialog.IsDisposed && !dialog.Visible, action + " actually closes a modeless update window");
            }
        }
        var prepared = new PreparedUpdate("1.2.0", "unused-stage", "unused-metadata", UpdateDownloadChoice.Full, false);
        foreach (string phase in new[] { "Prepared", "Migrating", "Activating", "Verified", "Completed", "Aborted" })
            Check(UpdateStartupGuard.RequiresRecovery(System.Text.Encoding.UTF8.GetBytes(
                "{\"Format\":\"fmsc-recovery/v1\",\"Phase\":\"" + phase + "\"}")) == (phase != "Completed" && phase != "Aborted"),
                "Direct startup must not loop after verified completion or abort: " + phase);
        foreach (string invalid in new[] { "{}", "[]", "{\"Format\":\"fmsc-recovery/v1\",\"Phase\":\"Unknown\"}",
            "{\"Format\":\"other\",\"Phase\":\"Completed\"}", "{\"Format\":\"fmsc-recovery/v1\",\"Phase\":100}" })
        {
            bool refused = false;
            try { UpdateStartupGuard.RequiresRecovery(System.Text.Encoding.UTF8.GetBytes(invalid)); }
            catch (InvalidDataException) { refused = true; }
            Check(refused, "Malformed recovery state cannot enable direct startup");
        }
        using (var parent = new Form())
        using (var dialog = new UpdateConnectionForm(() => (true, "ssh-rsa fixture")))
        {
            parent.Show(); dialog.Show(parent); Application.DoEvents();
            var downloadAction = Find(dialog, "Download Update");
            var installAction = Find(dialog, "Install Update");
            var setDownloadVisible = typeof(UpdateConnectionForm).GetMethod(
                "SetDownloadActionVisible", fields)!;
            Check(!downloadAction.Visible && !downloadAction.Enabled && !installAction.Visible,
                "Before a check resolves, neither download nor installation can be offered");
            setDownloadVisible.Invoke(dialog, new object[] { true });
            Application.DoEvents();
            Check(downloadAction.Visible && downloadAction.Enabled && !installAction.Visible,
                "Download is available only after a final no-cache decision");
            setDownloadVisible.Invoke(dialog, new object[] { false });
            Application.DoEvents();
            Check(!downloadAction.Visible && !downloadAction.Enabled,
                "Checking a previously downloaded release hides the action rather than flashing a disabled button");
            var transfer = new UpdateDownloadProgress(UpdateDownloadPhase.Downloading, 10 * 1048576, 20 * 1048576,
                2 * 1048576, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), 2, 3, 1, 0, "Downloading part 2 of 3");
            dialog.ApplyDownloadProgress(transfer); Application.DoEvents();
            var bar = (ProgressBar)typeof(UpdateConnectionForm).GetField("_progress", fields)!.GetValue(dialog)!;
            var stats = (Label)typeof(UpdateConnectionForm).GetField("_transferStats", fields)!.GetValue(dialog)!;
            Check(bar.Visible && bar.Style == ProgressBarStyle.Continuous && bar.Value == 50,
                "Download progress is a real determinate bar");
            Check(stats.Text.Contains("10.00 MB / 20.00 MB") && stats.Text.Contains("10.00 MB remaining") &&
                stats.Text.Contains("2.00 MB/s") && stats.Text.Contains("00:05"), "Download amount, remaining bytes, speed, elapsed and ETA are visible");
            Check(stats.GetPreferredSize(new Size(stats.Width, int.MaxValue)).Height <= stats.Height,
                "Download statistics fit without clipping");
            Find(dialog, "Check for Updates").Enabled = false;
            // Let the native progress control finish its short visual transition
            // before saving the review image (the Value contract is checked above).
            var paintUntil = Environment.TickCount64 + 650;
            while (Environment.TickCount64 < paintUntil) { Application.DoEvents(); Thread.Sleep(10); }
            string output = Path.Combine(Environment.CurrentDirectory, "artifacts", "update-dialog-review");
            Directory.CreateDirectory(output);
            using (var bitmap = new Bitmap(dialog.Width, dialog.Height))
            { dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, dialog.Size)); bitmap.Save(Path.Combine(output, "downloading.png")); }
            dialog.ApplyDownloadProgress(transfer with { Phase = UpdateDownloadPhase.Retrying, ReceivedBytes = 8 * 1048576,
                BytesPerSecond = 0, TimeRemaining = null, Attempt = 3, Message = "Connection lost. Retrying in 4s (attempt 3/5)…" });
            Check(bar.Value == 40 && stats.Text.Contains("Speed: —") && stats.Text.Contains("Time left: —"),
                "Reconnect state shows only committed progress and no stale speed/ETA");
            var status = (Label)typeof(UpdateConnectionForm).GetField("_status", fields)!.GetValue(dialog)!;
            dialog.ApplyDownloadProgress(transfer with { Phase = UpdateDownloadPhase.Verifying, ReceivedBytes = 20 * 1048576,
                TimeRemaining = null, Message = "Checking downloaded files." });
            Check(bar.Value == 100 && stats.Text.StartsWith("Download: 100%") &&
                status.Text.StartsWith("Verifying") && !Find(dialog, "Install Update").Enabled,
                "100 percent describes download only; verification cannot enable installation");
            dialog.ApplyDownloadProgress(transfer with { Phase = UpdateDownloadPhase.Preparing,
                Message = "Preparing application files." });
            Check(status.Text.StartsWith("Verifying"), "Staging remains part of verification, never installation");
            dialog.ApplyDownloadProgress(transfer with { Phase = UpdateDownloadPhase.Ready, ReceivedBytes = 20 * 1048576,
                TimeRemaining = null, Message = "Download verified and prepared." });
            Check(bar.Value == 100 && status.Text.StartsWith("Ready to Install") && !Find(dialog, "Install Update").Enabled,
                "A complete progress bar alone cannot bypass the validated installer handoff");
            Find(dialog, "Close").PerformClick(); Application.DoEvents();
            Check(dialog.IsDisposed, "A progress window remains dismissible");
        }
        using (var parent = new Form())
        using (var dialog = new UpdateConnectionForm(() => (true, "ssh-rsa fixture")))
        {
            parent.Show(); dialog.Show(parent); Application.DoEvents();
            typeof(UpdateConnectionForm).GetField("_prepared", fields)!.SetValue(dialog, prepared);
            var install = Find(dialog, "Install Update"); install.Visible = install.Enabled = true;
            int requests = 0;
            dialog.InstallRequested += _ => { requests++; install.PerformClick(); return false; };
            install.PerformClick(); Application.DoEvents();
            Check(requests == 1 && install.Enabled && !dialog.IsDisposed, "Cancelled install permits retry and suppresses reentrant clicks");
            Find(dialog, "Close").PerformClick(); Application.DoEvents();
            Check(requests == 1, "Closing the update window never repeats the install request");
        }
        int launched = 0, confirmations = 0;
        using (var cancelled = new MainDashboardForm(null, _ => launched++, () => { confirmations++; return false; }, _ => { }))
        {
            _ = cancelled.Handle; // No Shown/Activated events: never start dashboard SQL/background work.
            Check(!cancelled.RequestUpdateInstallation(prepared) && launched == 0, "No confirmation leaves FMSC open and launches nothing");
            cancelled.Close();
            Check(launched == 0, "A later ordinary close cannot launch a previously declined update");
        }
        using (var blocked = new MainDashboardForm(null, _ => launched++, () => true, _ => { }))
        {
            _ = blocked.Handle;
            FormClosingEventHandler cancel = (_, e) => e.Cancel = true;
            blocked.FormClosing += cancel;
            Check(!blocked.RequestUpdateInstallation(prepared) && launched == 0 && !blocked.IsDisposed,
                "Cancelled dashboard closure cannot start an installer");
            blocked.FormClosing -= cancel;
            blocked.Close();
            Check(launched == 0, "Cancelled closure clears the pending installation");
        }
        bool closed = false;
        MainDashboardForm? accepted = null;
        using (accepted = new MainDashboardForm(null, _ => {
            Check(closed, "Installer launches only after the dashboard FormClosed notification");
            launched++;
            Check(!accepted!.RequestUpdateInstallation(prepared), "A closing dashboard rejects duplicate installation requests");
        }, () => { confirmations++; return true; }, _ => { }))
        {
            _ = accepted.Handle;
            accepted.FormClosed += (_, _) => closed = true;
            using var ownedUpdate = new UpdateConnectionForm(() => (true, "ssh-rsa fixture"));
            ownedUpdate.Show(accepted); Application.DoEvents();
            typeof(UpdateConnectionForm).GetField("_prepared", fields)!.SetValue(ownedUpdate, prepared);
            var install = Find(ownedUpdate, "Install Update"); install.Visible = install.Enabled = true;
            ownedUpdate.InstallRequested += accepted.RequestUpdateInstallation;
            install.PerformClick(); Application.DoEvents();
            Check(ownedUpdate.IsDisposed, "Accepted dashboard closure disposes its owned update window without another install request");
            Check(accepted.IsDisposed && launched == 1 && confirmations == 2, "One accepted update closes FMSC and launches exactly one installer");
        }
    }
}
