using System.Reflection;
using FMSC_SMS.Core;
using FMSC_SMS.Core.Updates;
using FMSC_SMS.Pages.Security;

internal static partial class Program
{
    private static void CheckLoginConnection()
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        void Capture(Form form, string name)
        {
            string output = Path.Combine(Environment.CurrentDirectory, "artifacts", "startup-update-review");
            Directory.CreateDirectory(output);
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            bitmap.Save(Path.Combine(output, name + ".png"));
        }
        // Visible alone is insufficient: an opaque sibling can cover a control.
        bool Exposed(Control control)
        {
            if (!control.Visible || control.Width <= 0 || control.Height <= 0) return false;
            Point center = control.PointToScreen(new Point(control.Width / 2, control.Height / 2));
            for (Control? child = control; child?.Parent is { } parent; child = parent)
                if (parent.GetChildAtPoint(parent.PointToClient(center), GetChildAtPointSkip.Invisible) != child) return false;
            return true;
        }

        foreach (var (found, title) in new[] { ("1", "Database update required"), ("3", "Application update required") })
        {
            using var release = new ManualResetEventSlim();
            int attempts = 0, firstRuns = 0;
            var failure = new DatabaseCompatibilityException(new(DatabaseCompatibilityIssue.SchemaVersion, "2", found));
            using var form = new LoginForm((_, _) => (false, "unused", null), true,
                _ => {
                    Interlocked.Increment(ref attempts);
                    if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Fixture was not released");
                    return (attempts > 1, failure.Message, attempts > 1 ? null : failure);
                }, () => { firstRuns++; return true; }, (owner, content) =>
                    new StartupUpdateController(owner, content, schema =>
                        new UpdateConnectionForm(() => (true, "ssh-rsa fixture"), installedSchema: schema)));
            form.Show();
            Application.DoEvents();
            var view = Descendants(form).OfType<DatabaseConnectionView>().Single();
            var reconnect = (ModernButton)view.AcceptAction;
            var status = (Label)typeof(DatabaseConnectionView).GetField("_status", fields)!.GetValue(view)!;
            var message = (Label)typeof(DatabaseConnectionView).GetField("_message", fields)!.GetValue(view)!;
            Check(view.Busy && Exposed(status) && Exposed(message), "Connection progress is exposed above the hidden login shell");
            Check(!reconnect.Enabled && firstRuns == 0, "Checking disables reconnect and cannot start first-run setup");
            release.Set();
            PumpUntil(() => !view.Busy, "Compatibility check finishes");
            bool older = found == "1";
            {
                var automaticUpdater = Descendants(form).OfType<UpdateConnectionForm>().Single();
                Check(!automaticUpdater.TopLevel && automaticUpdater.TopLevelControl == form && !view.Visible,
                    "Either schema version mismatch opens the existing updater without an extra check click");
                Check((int)typeof(UpdateConnectionForm).GetField("_installedSchema", fields)!.GetValue(automaticUpdater)! == int.Parse(found),
                    "The updater receives the actual database schema in both mismatch directions");
                var updaterStatus = (Label)typeof(UpdateConnectionForm).GetField("_status", fields)!.GetValue(automaticUpdater)!;
                Check(updaterStatus.Text.Contains("If none is available") && updaterStatus.Text.Contains("administrator"),
                    "Both updater mismatch screens explain what to do if no compatible release is published");
                Descendants(automaticUpdater).OfType<ModernButton>().Single(b => b.Text == "Close").PerformClick();
                Application.DoEvents();
            }
            Check(status.Text == title && Exposed(status) && Exposed(message), "Compatibility guidance remains exposed after startup layout");
            Check(message.Text.Contains($"version: {found}") && message.Text.Contains("application: 2"), "Main guidance identifies found and required versions");
            var updates = Descendants(view).OfType<ModernButton>().Single(b =>
                b.Text == (older ? "Open Updates" : "Update Application"));
            Check(Exposed(updates) && !reconnect.Visible,
                "Both database/app version mismatches offer the existing updater, not connection restart");
            Check(ReferenceEquals(form.AcceptButton, updates),
                "Enter follows the appropriate updater recovery action");
            if (older)
                Check(message.Text.Contains("internet") && message.Text.Contains("Install Update") &&
                    message.Text.Contains("back up") && message.Text.Contains("If no newer release is published"),
                    "Startup explains the published-release check, the fallback and the backup/migration path");
            else
                Check(message.Text.Contains("Update Application") && message.Text.Contains("Do not downgrade") &&
                    message.Text.Contains("compatible release") && message.Text.Contains("If none is published"),
                    "A newer database explains the published-release fallback without suggesting a database downgrade");
            Capture(form, older ? "database-update-required" : "application-update-required");
            Check(!((Control)typeof(DatabaseConnectionView).GetField("_restart", fields)!.GetValue(view)!).Enabled &&
                !((Control)typeof(DatabaseConnectionView).GetField("_repair", fields)!.GetValue(view)!).Visible,
                "Version failures cannot suggest engine restart or repair");
            Check(firstRuns == 0 && !Descendants(form).OfType<ModernButton>().Single(b => b.Text == "Sign In").Visible,
                "An incompatible database cannot proceed to setup or sign-in");
            var details = Descendants(view).OfType<LinkLabel>().Single();
            typeof(LinkLabel).GetMethod("OnLinkClicked", fields)!.Invoke(details,
                new object[] { new LinkLabelLinkClickedEventArgs(details.Links[0]) });
            Application.DoEvents();
            Check(Exposed(status) && Exposed(updates), "Expanded details preserve the visible update action");
            {
                Size connectionSize = form.ClientSize;
                updates.PerformClick(); Application.DoEvents();
                using var embedded = Descendants(form).OfType<UpdateConnectionForm>().Single();
                Check(!embedded.TopLevel && embedded.TopLevelControl == form && !view.Visible,
                    "The existing updater replaces connection content inside the same window");
                Check(Exposed(Descendants(embedded).OfType<ModernButton>().Single(b => b.Text == "Check for Updates")),
                    "The embedded updater's check action is exposed");
                Check((int)typeof(UpdateConnectionForm).GetField("_installedSchema", fields)!.GetValue(embedded)! == int.Parse(found),
                    "The updater retains the observed database version without querying SQL");
                Capture(form, older ? "embedded-database-updater" : "embedded-application-updater");
                Descendants(embedded).OfType<ModernButton>().Single(b => b.Text == "Computer access").PerformClick();
                Application.DoEvents();
                Check(embedded.Location == Point.Empty && embedded.Size == embedded.Parent!.ClientSize,
                    "Authorization expansion keeps the embedded updater filling its host");
                Descendants(embedded).OfType<ModernButton>().Single(b => b.Text == "Close").PerformClick();
                Application.DoEvents();
                Check(view.Visible && Exposed(updates) && form.ClientSize == connectionSize && form.PendingUpdate is null,
                    $"Closing the updater restores connection guidance, size and keyboard action without installing (visible={view.Visible}, size={form.ClientSize}, expected={connectionSize})");
            }
            // Simulate an administrator having completed recovery externally.
            var connected = view.ConnectAsync(false);
            PumpUntil(() => view.IsDisposed, "Successful reconnect switches to sign-in");
            connected.GetAwaiter().GetResult();
            Application.DoEvents();
            var signIn = Descendants(form).OfType<ModernButton>().Single(b => b.Text == "Sign In");
            Check(firstRuns == 1 && form.Text == "FMSC - Sign In" && Exposed(signIn), "Same window exposes sign-in after readiness succeeds");
            Check(ReferenceEquals(form.AcceptButton, signIn) && form.CancelButton is null, "Sign-in restores keyboard actions after connection view disposal");
            form.Close();
        }
        CheckStartupUpdateHandoff();
        foreach (Exception connectionFailure in new Exception[] {
            new DatabaseRecoveryException("EngineMissing", "SQL connection software is missing"),
            new DatabaseRecoveryException("EngineUnavailable", "LocalDB could not start"),
            new DatabaseCompatibilityException(new(DatabaseCompatibilityIssue.DatabaseIdentity, "2")),
            new DatabaseCompatibilityException(new(DatabaseCompatibilityIssue.SchemaStructure, "2", "2")) })
        {
            using var blocked = new LoginForm((_, _) => throw new Exception("Connection failure must not sign in"), true,
                _ => (false, connectionFailure.Message, connectionFailure),
                () => throw new Exception("Connection failure must not run first-run setup"),
                (owner, content) => new StartupUpdateController(owner, content,
                    _ => throw new Exception("Connection failure must not transition to updates")));
            blocked.Show(); Application.DoEvents();
            var connection = Descendants(blocked).OfType<DatabaseConnectionView>().Single();
            PumpUntil(() => !connection.Busy, "Connection recovery fixture finishes");
            Check(connection.Visible && !Descendants(blocked).OfType<UpdateConnectionForm>().Any(),
                "SQL/LocalDB, database identity and structure failures retain the connection recovery screen");
            blocked.Close();
        }
        using var offline = new UpdateConnectionForm(() => (true, "fixture"), installedSchema: 1, networkAvailable: () => false);
        offline.Show(); Application.DoEvents();
        var checkUpdates = Descendants(offline).OfType<ModernButton>().Single(b => b.Text == "Check for Updates");
        checkUpdates.PerformClick(); Application.DoEvents();
        var offlineMessage = (Label)typeof(UpdateConnectionForm).GetField("_status", fields)!.GetValue(offline)!;
        Check(offlineMessage.Text.Contains("offline") && offlineMessage.Text.Contains("Connect to the internet") && checkUpdates.Enabled,
            "An offline computer gets internet/retry guidance and can check again without closing startup");
        Check(typeof(UpdateConnectionForm).GetField("_prepared", fields)!.GetValue(offline) is null &&
            !Descendants(offline).OfType<ModernButton>().Single(b => b.Text == "Install Update").Visible,
            "An offline check cannot prepare or install an update");
        offline.Close();
    }

    private static void CheckStartupUpdateHandoff()
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        bool approve = false, reject = false;
        int validations = 0, confirmations = 0, errors = 0;
        var failure = new DatabaseCompatibilityException(new(DatabaseCompatibilityIssue.SchemaVersion, "2", "1"));
        using var form = new LoginForm((_, _) => throw new Exception("Startup updates must not sign in"), true,
            _ => (false, failure.Message, failure), () => throw new Exception("Incompatible startup must not run setup"),
            (owner, content) => new StartupUpdateController(owner, content,
                schema => new UpdateConnectionForm(() => (true, "fixture"), schema),
                _ => { validations++; if (reject) throw new InvalidDataException("fixture rejected"); },
                () => { confirmations++; return approve; }, _ => errors++));
        form.Show(); Application.DoEvents();
        var view = Descendants(form).OfType<DatabaseConnectionView>().Single();
        PumpUntil(() => !view.Busy, "Older database detected for handoff fixture");
        var updater = Descendants(form).OfType<UpdateConnectionForm>().Single();
        var prepared = new PreparedUpdate("9.0.0", "unused-stage", "unused-manifest", UpdateDownloadChoice.Full, false);
        typeof(UpdateConnectionForm).GetField("_prepared", fields)!.SetValue(updater, prepared);
        var install = Descendants(updater).OfType<ModernButton>().Single(b => b.Text == "Install Update");
        install.Visible = install.Enabled = true;
        install.PerformClick();
        Check(form.PendingUpdate is null && updater.Visible && validations == 1 && confirmations == 1,
            "Declining installation leaves the embedded updater open and no handoff scheduled");
        reject = true;
        install.PerformClick();
        Check(form.PendingUpdate is null && errors == 1 && confirmations == 1 && install.Enabled,
            "Rejected update validation reports an error and allows retry without closing startup");
        reject = false; approve = true;
        install.PerformClick(); Application.DoEvents();
        Check(ReferenceEquals(form.PendingUpdate, prepared) && form.DialogResult == DialogResult.Cancel && !form.Visible,
            "An approved installation ends startup with a prepared handoff, never a successful login");
        Check(validations == 3 && confirmations == 2 && updater.IsDisposed,
            "Each request validates the package and only the accepted request closes the updater");
    }
}
