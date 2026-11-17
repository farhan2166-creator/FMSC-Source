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
                using var dialog = new UpdateConnectionForm(() => (enrolled, enrolled ? "ssh-rsa fictional-public-key" : ""),
                    networkAvailable: () => true, verifyAuthorization: _ => Task.CompletedTask);
                dialog.Show(parent); Application.DoEvents();
                var area = Screen.FromControl(parent).WorkingArea;
                void CheckScreenCenter() => Check(
                    area.Contains(dialog.Bounds) &&
                    HorizontallyCentered(dialog.Bounds, area) &&
                    Math.Abs((dialog.Top + dialog.Bottom) / 2d - (area.Top + area.Bottom) / 2d)
                        <= Math.Max(AppScale.Scale(8), area.Height * 0.05d),
                    "Update window remains visible and approximately centered after access details change");
                CheckScreenCenter();
                Check(!Find(dialog, "Check for Updates").Enabled,
                    "A locally generated key is not proof of GitHub authorization");
                var panel = (Panel)typeof(UpdateConnectionForm).GetField("_authorizationPanel", fields)!.GetValue(dialog)!;
                Check(panel.Visible, "Unverified computers keep their public-key controls visible");
                if (enrolled)
                {
                    // Explicit live verification is what transitions to the existing compact update view.
                    Find(dialog, "Verify access").PerformClick(); Application.DoEvents();
                    Check(Find(dialog, "Check for Updates").Enabled && !panel.Visible,
                        "Only a successfully verified Git connection enables update checking");
                }
                var accessButton = Find(dialog, enrolled ? "Computer Authorized" : "Authorize This Computer");
                Check(HorizontallyCentered(BoundsWithin(accessButton, dialog), dialog.ClientRectangle),
                    "The sole computer-authorization control is centered and not clipped");
                Check(!Descendants(dialog).OfType<ModernButton>().Any(b => b.Text == "Remind Me Later") &&
                    Descendants(dialog).OfType<ModernButton>().Count(b => b.Text == "Close") == 1,
                    "Only one centered Close action is present");
                Check(!Descendants(dialog).OfType<Label>().Any(label => label.Text is
                    "Computer authorized" or "Computer key ready" or "Approval not verified" or "Authorization required"),
                    "Computer access status is explained by its own button, not a duplicate label");
                var checkButton = Find(dialog, "Check for Updates");
                Check(HorizontallyCentered(BoundsWithin(checkButton, dialog), dialog.ClientRectangle),
                    "Check for Updates action is centered and not clipped in the window");
                Check(Descendants(dialog).OfType<Label>().Where(label =>
                    label.Text is "Installed version" or "Latest published version" or "Not checked" or "Check for Updates")
                    .All(label => label.TextAlign == ContentAlignment.MiddleCenter),
                    "Heading and version labels use centered text");
                accessButton.PerformClick(); Application.DoEvents(); CheckScreenCenter();
                Check(panel.Visible, "Pending key details remain visible and authorized details can expand");
                accessButton.PerformClick(); Application.DoEvents(); CheckScreenCenter();
                Check(panel.Visible == !enrolled, "Only approved computers may collapse their key details");
                var viewport = Descendants(dialog).OfType<ResponsiveViewport>().Single();
                Check(!viewport.HorizontalScroll.Visible && !viewport.VerticalScroll.Visible,
                    "Switching computer access details must not leave obsolete viewport scrollbars");
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
        // Reproduce the reported Generate key -> hidden textbox / enabled check failure
        // without touching the real Windows user's protected SSH credential.
        bool keyGenerated = false;
        int deniedChecks = 0;
        using (var parent = new Form())
        using (var dialog = new UpdateConnectionForm(
            () => (keyGenerated, keyGenerated ? "ssh-rsa synthetic-pending-key" : ""),
            networkAvailable: () => true,
            verifyAuthorization: _ => { deniedChecks++; return Task.FromException(new IOException("Approval pending")); },
            generateEnrollment: () => { keyGenerated = true; return "ssh-rsa synthetic-pending-key"; }))
        {
            parent.Show(); dialog.Show(parent); Application.DoEvents();
            var panel = (Panel)typeof(UpdateConnectionForm).GetField("_authorizationPanel", fields)!.GetValue(dialog)!;
            Find(dialog, "Authorize This Computer").PerformClick(); Application.DoEvents();
            Check(panel.Visible, "Computer access cannot hide the enrollment form before a key exists");
            Find(dialog, "Generate key").PerformClick(); Application.DoEvents();
            var key = (TextBox)typeof(UpdateConnectionForm).GetField("_publicKey", fields)!.GetValue(dialog)!;
            Check(key.Text == "ssh-rsa synthetic-pending-key" && panel.Visible &&
                  Find(dialog, "Computer Key Ready").Enabled &&
                  Find(dialog, "Copy public key").Enabled && Find(dialog, "Verify access").Enabled,
                "Generating a key leaves it visible and copyable while approval is pending");
            Check(!Find(dialog, "Check for Updates").Enabled,
                "Generating a local private key never enables an update check");
            Find(dialog, "Verify access").PerformClick(); Application.DoEvents();
            Check(deniedChecks == 1 && panel.Visible && !Find(dialog, "Check for Updates").Enabled,
                "Denied GitHub authorization keeps the public key visible and updates blocked");
            var checkMethod = typeof(UpdateConnectionForm).GetMethod("CheckNowAsync", fields)!;
            ((Task)checkMethod.Invoke(dialog, null)!).GetAwaiter().GetResult();
            Check(deniedChecks == 1 && !Find(dialog, "Check for Updates").Enabled &&
                  Descendants(dialog).OfType<Label>().Any(label => label.Text.Contains("Verify computer access")),
                "Even a direct update check call refuses an unverified local key");
            var viewport = Descendants(dialog).OfType<ResponsiveViewport>().Single();
            Check(!viewport.HorizontalScroll.Visible && !viewport.VerticalScroll.Visible,
                "Generate-key and rejected-authorization states do not introduce scrollbars");
        }
        // Exercise the exact factory used by the dashboard's Check for Updates
        // menu item; merely verifying a key must not leave an extra check click.
        {
            int probes = 0, authorizations = 0;
            using var parent = new Form();
            using var menuCheck = UpdateConnectionForm.ForMenuUpdateCheck(
                authorization: () => (true, "ssh-rsa saved-menu-key"),
                networkAvailable: () => ++probes == 1,
                verifyAuthorization: _ => { authorizations++; return Task.CompletedTask; });
            parent.Show();
            menuCheck.Show(parent);
            var panel = (Panel)typeof(UpdateConnectionForm).GetField("_authorizationPanel", fields)!.GetValue(menuCheck)!;
            Check(!panel.Visible && !Find(menuCheck, "Check for Updates").Visible,
                "Menu update checks start compact, without flashing the saved key or requiring a manual click");
            PumpUntil(() => probes >= 2, "Menu check automatically enters release discovery");
            var status = (Label)typeof(UpdateConnectionForm).GetField("_status", fields)!.GetValue(menuCheck)!;
            Check(authorizations == 1 && probes == 2 &&
                  status.Text.Contains("offline", StringComparison.OrdinalIgnoreCase),
                "The menu command verifies saved access once, then immediately attempts an update check");
            Check(!panel.Visible && Find(menuCheck, "Computer Authorized").Visible &&
                  Find(menuCheck, "Check for Updates").Enabled,
                "Offline update discovery retains compact authorized view and provides retry");
            menuCheck.Close();
        }
        {
            int authorizations = 0;
            using var parent = new Form();
            using var pending = UpdateConnectionForm.ForMenuUpdateCheck(
                authorization: () => (true, "ssh-rsa unapproved-menu-key"),
                networkAvailable: () => true,
                verifyAuthorization: _ => {
                    authorizations++;
                    return Task.FromException(new IOException("Approval pending"));
                });
            parent.Show();
            pending.Show(parent);
            var panel = (Panel)typeof(UpdateConnectionForm).GetField("_authorizationPanel", fields)!.GetValue(pending)!;
            Check(!panel.Visible, "Menu key stays hidden while live verification is pending");
            var status = (Label)typeof(UpdateConnectionForm).GetField("_status", fields)!.GetValue(pending)!;
            PumpUntil(() => status.Text.Contains("not been verified"), "Unapproved menu access shows key recovery");
            Check(authorizations == 1 && panel.Visible &&
                  Find(pending, "Verify access").Enabled &&
                  !Find(pending, "Check for Updates").Enabled,
                "Unapproved menu access reveals key approval controls and never checks for releases");
            pending.Close();
        }
        {
            using var parent = new Form();
            using var enrollment = UpdateConnectionForm.ForMenuUpdateCheck(
                authorization: () => (false, ""),
                networkAvailable: () => true,
                verifyAuthorization: _ => Task.CompletedTask);
            parent.Show();
            enrollment.Show(parent); Application.DoEvents();
            var panel = (Panel)typeof(UpdateConnectionForm).GetField("_authorizationPanel", fields)!.GetValue(enrollment)!;
            Check(panel.Visible && Find(enrollment, "Generate key").Enabled &&
                  !Find(enrollment, "Check for Updates").Enabled,
                "Menu update check preserves enrollment when the computer has no key");
            enrollment.Close();
        }
        // A blocked startup with a saved key must not flash key enrollment.
        // Once live authorization succeeds, discover an update without another click.
        // The second offline probe prevents an actual Git operation in this test.
        {
            int probes = 0, authorizations = 0;
            using var parent = new Form();
            using var startup = new UpdateConnectionForm(
                () => (true, "ssh-rsa saved-startup-key"),
                networkAvailable: () => ++probes == 1,
                verifyAuthorization: _ => { authorizations++; return Task.CompletedTask; },
                autoCheckOnVerifiedAccess: true);
            parent.Show();
            startup.Show(parent);
            var panel = (Panel)typeof(UpdateConnectionForm).GetField("_authorizationPanel", fields)!.GetValue(startup)!;
            Check(!panel.Visible && !Find(startup, "Check for Updates").Visible,
                "Saved startup authorization starts compact, without flashing enrollment or a manual update action");
            PumpUntil(() => probes >= 2, "Startup automatically checks for updates after live authorization");
            var status = (Label)typeof(UpdateConnectionForm).GetField("_status", fields)!.GetValue(startup)!;
            Check(authorizations == 1 && probes == 2 && status.Text.Contains("offline", StringComparison.OrdinalIgnoreCase),
                "Verified startup authorization automatically reaches release discovery without a click");
            Check(!panel.Visible && Find(startup, "Computer Authorized").Visible &&
                  Find(startup, "Check for Updates").Enabled,
                "After an offline update check, authorized startup stays compact and allows manual retry");
            startup.Close();
        }
        {
            int authorizations = 0;
            using var parent = new Form();
            using var startup = new UpdateConnectionForm(
                () => (true, "ssh-rsa still-pending-key"),
                networkAvailable: () => true,
                verifyAuthorization: _ => {
                    authorizations++;
                    return Task.FromException(new IOException("Approval pending"));
                },
                autoCheckOnVerifiedAccess: true);
            parent.Show();
            startup.Show(parent);
            var panel = (Panel)typeof(UpdateConnectionForm).GetField("_authorizationPanel", fields)!.GetValue(startup)!;
            Check(!panel.Visible, "Saved startup key starts hidden during verification");
            var status = (Label)typeof(UpdateConnectionForm).GetField("_status", fields)!.GetValue(startup)!;
            PumpUntil(() => status.Text.Contains("not been verified"), "Denied startup key presents access recovery");
            Check(authorizations == 1 && panel.Visible && Find(startup, "Verify access").Enabled &&
                  !Find(startup, "Check for Updates").Enabled,
                "Denied startup authorization reveals the public key and never checks the repository");
            startup.Close();
        }
        {
            bool generated = false;
            using var parent = new Form();
            using var startup = new UpdateConnectionForm(
                () => (generated, generated ? "ssh-rsa newly-generated-startup-key" : ""),
                networkAvailable: () => true,
                verifyAuthorization: _ => Task.CompletedTask,
                generateEnrollment: () => { generated = true; return "ssh-rsa newly-generated-startup-key"; },
                autoCheckOnVerifiedAccess: true);
            parent.Show();
            startup.Show(parent);
            var panel = (Panel)typeof(UpdateConnectionForm).GetField("_authorizationPanel", fields)!.GetValue(startup)!;
            Find(startup, "Generate key").PerformClick(); Application.DoEvents();
            var newKey=(TextBox)typeof(UpdateConnectionForm).GetField("_publicKey", fields)!.GetValue(startup)!;
            Check(newKey.Text=="ssh-rsa newly-generated-startup-key",
                "New startup enrollment stores and displays the generated public key");
            Check(panel.Visible, "New startup enrollment keeps the public-key panel visible");
            Check(Find(startup, "Copy public key").Enabled,
                "New startup enrollment keeps the public key copyable");
            Check(!Find(startup, "Check for Updates").Enabled,
                "New startup enrollment does not enable update checking without verified approval");
            startup.Close();
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
