using System.Reflection;
using FMSC_SMS.Core;

internal static partial class Program
{
    private sealed class StatusMessageFixture : V2Page
    {
        internal readonly Panel Body = new() { Dock = DockStyle.Fill };
        internal Label Status => StatusLabel;
        internal StatusMessageFixture(bool actions, bool external) : base("Status fixture", "")
        {
            if (actions)
            {
                AddAction("Save", (_, _) => { });
                AddAction("Refresh", (_, _) => { });
            }
            UseWrappingToolbar();
            if (external)
            {
                var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
                header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
                header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
                header.Controls.Add(new Label { Text = "Record", Dock = DockStyle.Fill }, 0, 0);
                var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
                root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
                root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                root.Controls.Add(header, 0, 0);
                root.Controls.Add(Body, 0, 1);
                SetContent(root);
                SetStatusHeaderHost(header);
            }
            else SetContent(Body);
        }
        internal void ShowMessage(string text) => SetStatus(text, PageStatusKind.Warning);
    }

    private static void CheckCompactStatusLayout()
    {
        foreach (var host in new[] { (Actions: true, External: false), (Actions: false, External: false), (Actions: false, External: true) })
        foreach (int width in new[] { 780, 1280, 1920 })
        foreach (float fontSize in new[] { 9f, 12f, 16f })
        {
            using var window = new Form { ClientSize = new Size(width, 720), StartPosition = FormStartPosition.Manual };
            using var page = new StatusMessageFixture(host.Actions, host.External) { Dock = DockStyle.Fill };
            using var font = new Font("Segoe UI", fontSize);
            page.Status.Font = font;
            window.Controls.Add(page);
            window.Show();
            page.ShowMessage("Outcome unknown. Check History before retrying.\nSelected: 40\nProcessed: 30\nPromoted: 20\nHeld back: 10\nNot processed: 10\nKeep the saved records.");
            Application.DoEvents();
            int bodyHeight = page.Body.ClientSize.Height;
            Check(page.Status.Visible && page.Status.Height >= AppMetrics.TextHeight(page.Status.Font),
                "Long status remains legible in each supported host without a fixed two-line limit");
            Check(page.Body.Visible && page.Body.ClientSize.Height > 0,
                "Long status leaves the main workspace accessible without prescribing a height");
            Check(page.Status.AutoEllipsis && !page.Status.AutoSize, "Bounded status shows ellipsis instead of expanding");
            string longer = "Saved. Reload before editing. " + string.Join(" ", Enumerable.Repeat("Full fictional recovery details.", 100));
            page.ShowMessage(longer);
            Application.DoEvents();
            Check(page.Body.ClientSize.Height == bodyHeight, "More status detail cannot push the workspace farther down");
            Check(page.Status.Text == longer && page.Status.AccessibleDescription == longer,
                "Full message is retained for accessibility without truncating recovery details");
            var tip = (ToolTip)typeof(V2Page).GetField("_statusToolTip", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
            Check(tip.GetToolTip(page.Status) == longer, "Full status wording remains available on hover");
            page.Hide();
            page.ShowMessage(longer + " Further retained details.");
            page.Show();
            Application.DoEvents();
            Check(page.Status.Height > 0 && page.Body.ClientSize.Height == bodyHeight,
                "Updating a hidden page preserves workspace space when reopened");
            page.ShowMessage("");
            Application.DoEvents();
            Check(page.Body.ClientSize.Height >= bodyHeight, "Clearing feedback restores available workspace");
            window.Close();
        }
    }
}
