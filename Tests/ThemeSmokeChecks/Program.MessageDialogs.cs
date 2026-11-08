using System.Data;
using System.Reflection;
using FMSC_SMS.Core;

internal static partial class Program
{
    private static IEnumerable<Control> MessageChildren(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var nested in MessageChildren(child)) yield return nested;
        }
    }
    private static void CheckMessageDialogs()
    {
        var prior = AppTheme.CurrentTheme;
        try
        {
            foreach (var theme in new[] { AppThemeType.OceanBlue, AppThemeType.OceanBlueLightweight })
            {
                AppTheme.SetTheme(theme);
                foreach (var buttons in new[] { MessageBoxButtons.YesNo, MessageBoxButtons.OK })
                foreach (var initial in new[] { MessageBoxDefaultButton.Button1, MessageBoxDefaultButton.Button2 })
                {
                    using var dialog = ModernMessageDialog.CreateDialog(null,
                        new MessageDialogContent("Record this payment?", "Check the student and amount before continuing.",
                            new (string, string)[] { ("Student", "Fictional A & B"), ("Amount", "Rs. 5,000") },
                            Notice: "This saves the payment immediately."),
                        "Confirmation fixture", buttons, MessageBoxIcon.Warning, initial);
                    dialog.Show(); Application.DoEvents();
                    var controls = MessageChildren(dialog).ToArray();
                    var panel = controls.OfType<FlowLayoutPanel>().Single(c => c.Name == "MessageButtons");
                    var choices = panel.Controls.OfType<Button>().ToArray();

                    Check(choices.All(b => b.Visible && panel.ClientRectangle.Contains(b.Bounds)),
                        "Every confirmation button is visible and reachable within its group");
                    if (buttons == MessageBoxButtons.YesNo)
                    {
                        var yes = choices.Single(b => b.Text == "Yes"); var no = choices.Single(b => b.Text == "No");
                        var expected = initial == MessageBoxDefaultButton.Button2 ? no : yes;
                        Check(ReferenceEquals(dialog.AcceptButton, expected) && ReferenceEquals(dialog.CancelButton, no),
                            "Explicit Yes/No defaults and cancellation remain unchanged");
                        Check(ReferenceEquals(dialog.ActiveControl, expected) || expected.ContainsFocus,
                            "The selected default button receives initial focus");
                    }
                    var values = controls.OfType<Label>().Where(l => l.Name == "DetailValue").ToArray();
                    Check(values.Length == 2 && values.All(label => label.Visible && label.Width > 0 && label.Height > 0),
                        "Both detail values remain readable without requiring identical column geometry");
                    Check(values[0].Text == "Fictional A & B" && !values[0].UseMnemonic,
                        "Database values display literally, including ampersands");
                    var summary = controls.OfType<Label>().Single(l => l.Name == "MessageSummary");
                    Check(summary.Visible && summary.Height >= AppMetrics.TextHeight(summary.Font),
                        "The decision heading remains readable independent of font weight");
                    typeof(Form).GetMethod("ProcessDialogKey", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(dialog, new object[] { Keys.Enter });
                    Check(dialog.DialogResult == (buttons == MessageBoxButtons.OK ? DialogResult.OK
                        : initial == MessageBoxDefaultButton.Button2 ? DialogResult.No : DialogResult.Yes),
                        "Enter activates the explicit default without changing its decision");
                    dialog.Close();
                }
                foreach (var key in new[] { Keys.Escape, Keys.Y, Keys.N })
                {
                    using var dialog = ModernMessageDialog.CreateDialog(null, new("Continue?"), "Keyboard fixture",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                    dialog.Show(); Application.DoEvents();
                    typeof(Control).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(dialog, new object[] { new KeyEventArgs(key) });
                    Check(dialog.DialogResult == (key == Keys.Y ? DialogResult.Yes : DialogResult.No),
                        "Escape and existing Y/N dialog shortcuts preserve their results");
                    dialog.Close();
                }
                using var rows = new DataTable();
                rows.Columns.Add("Admission No."); rows.Columns.Add("Student"); rows.Columns.Add("Payment (Rs.)", typeof(decimal));
                for (int i = 0; i < 25; i++) rows.Rows.Add((1000 + i).ToString(), "Fictional student " + i, 1000.25m + i);
                using (var dialog = ModernMessageDialog.CreateDialog(null,
                    new("Review family payment", string.Join("\n", Enumerable.Repeat("Fictional review details; no school data.", 60)),
                        new (string, string)[] { ("Long folder", string.Join("/", Enumerable.Repeat("fictional-folder", 12))) },
                        rows, "Check all rows before choosing Yes."),
                    "Long message fixture", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1))
                {
                    dialog.ClientSize = new Size(600, 400);
                    dialog.Show(); Application.DoEvents();
                    var controls = MessageChildren(dialog).ToArray();
                    var viewport = controls.OfType<Panel>().Single(c => c.Name == "MessageViewport");
                    var yes = controls.OfType<Button>().Single(b => b.Text == "Yes");
                    var grid = controls.OfType<DataGridView>().Single(g => g.Name == "MessageRecords");
                    Check(viewport.AutoScroll && viewport.VerticalScroll.Visible,
                        "Long message details scroll without hiding the decision buttons");
                    Check(yes.Visible && dialog.ClientRectangle.Contains(dialog.PointToClient(yes.PointToScreen(Point.Empty))),
                        "Confirmation remains accessible in a small window");
                    var notice = controls.OfType<Label>().Single(l => l.Name == "MessageNotice");
                    var explanation = controls.OfType<Label>().Single(l => l.Name == "MessageExplanation");
                    Check(notice.Top < explanation.Top, "Important consequences appear before long explanations or record lists");
                    Check(grid.ReadOnly && !grid.AllowUserToAddRows && !grid.AllowUserToDeleteRows && grid.Rows.Count == 25,
                        "All fictional database rows remain in a read-only table");
                    Check(ReferenceEquals(grid.DataSource, rows) && (decimal)grid.Rows[0].Cells[2].Value == 1000.25m,
                        "Record presentation preserves exact source values");
                    Check(grid.Columns[2].DefaultCellStyle.Alignment == DataGridViewContentAlignment.MiddleRight,
                        "Amounts align to the right for comparison");
                    Check(grid.Columns[2].DefaultCellStyle.Format == "#,##0.##",
                        "Presentation retains decimal precision without changing stored amounts");
                    dialog.Close();
                }
                Check(rows.Rows.Count == 25 && (decimal)rows.Rows[0][2] == 1000.25m,
                    "Closing a message does not edit or dispose its caller-owned records");
                var plain = MessageDialogContent.FromText("Review this record?\r\n\r\nTime: 11:30\r\nFictional A & B");
                Check(plain.Summary == "Review this record?" && plain.Explanation == "Time: 11:30\nFictional A & B",
                    "Plain messages retain dates, row text and punctuation without guessing a table structure");
                var destructive = typeof(ModernMessageDialog).GetMethod("LooksDestructive", BindingFlags.Static | BindingFlags.NonPublic)!;
                Check((bool)destructive.Invoke(null, new object?[] { "Unsaved changes", "Leave without saving?" })!,
                    "Simplified discard wording retains the conservative default policy");
            }
        }
        finally { AppTheme.SetTheme(prior); }
    }
}
