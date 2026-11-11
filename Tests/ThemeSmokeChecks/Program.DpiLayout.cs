using FMSC_SMS.Core;

internal static partial class Program
{
    private static void CheckDpiResponsiveLayouts()
    {
        Check(Application.HighDpiMode == HighDpiMode.PerMonitorV2, "Native control fixtures use the required per-monitor awareness");
        var saved = (AppScale.Dpi, AppScale.RawFactor);
        try
        {
            CheckDpiGeometryAndMinimum();
            CheckDialogScrollHost();
            // Synthetic client areas and logical scale factors exercise the
            // shared layout helpers without mutating private framework fields.
            // Physical multi-monitor DPI acceptance remains a separate test.
            foreach (int dpi in new[] { 96, 120, 144, 192 })
            foreach (Size resolution in new[] { new Size(1280, 720), new Size(1366, 768), new Size(1920, 1080), new Size(2560, 1440), new Size(3840, 2160) })
            {
                Size available = new(resolution.Width - DpiLayout.Pixels(16, dpi), resolution.Height - DpiLayout.Pixels(72, dpi));
                AppScale.Recompute(available, dpi);
                using var owner = new Form { AutoScaleMode = AutoScaleMode.None, StartPosition = FormStartPosition.Manual, ClientSize = available };
                var root = new TableLayoutPanel { ColumnCount = 1, RowCount = 3, Padding = new Padding(AppMetrics.PagePadding) };
                root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                root.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.FieldCardHeight(2)));
                root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty };
                fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
                fields.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); fields.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
                var name = HumanUi.TextBox(); name.Text = "Synthetic student"; name.IsInvalid = true;
                var notes = HumanUi.TextBox(); notes.Text = "Unsubmitted layout fixture";
                var choice = HumanUi.Combo(); choice.Items.Add("Synthetic class"); choice.SelectedIndex = 0;
                var date = HumanUi.Date(); date.Value = new DateTime(2026, 1, 15); date.Checked = true;
                fields.Controls.Add(HumanUi.Field("Student name", name), 0, 0);
                fields.Controls.Add(HumanUi.Field("Class", choice), 1, 0);
                fields.Controls.Add(HumanUi.Field("Date", date), 0, 1);
                fields.Controls.Add(HumanUi.Field("Notes", notes), 1, 1);
                var grid = HumanUi.Grid(); grid.Dock = DockStyle.Fill; grid.Columns.Add("Student", "Student name"); grid.Rows.Add("Synthetic student");
                var card = HumanUi.Card("Student information", "Synthetic layout fixture"); card.Content.Controls.Add(fields);
                var tabs = new TabControl(); tabs.TabPages.Add("Students"); tabs.TabPages.Add("Fee details");
                tabs.TabPages[0].Controls.Add(grid);
                var tabHost = TabStrip.CreateHost(tabs);
                var save = new ModernButton { Text = "Save", Width = AppScale.Scale(130) };
                var cancel = new ModernButton { Text = "Close", Width = AppScale.Scale(130) };
                var actions = HumanUi.ButtonRow(save, cancel); actions.Dock = DockStyle.Fill;
                root.Controls.Add(card, 0, 0); root.Controls.Add(tabHost, 0, 1); root.Controls.Add(actions, 0, 2);
                Size contentMinimum = new(AppScale.Scale(900), AppScale.Scale(660));
                root.Size = contentMinimum;
                using var viewport = new ResponsiveViewport(root, contentMinimum); owner.Controls.Add(viewport);
                owner.Show(); Application.DoEvents();
                UiTheme.ApplyTree(root);
                HumanUi.ApplyGridStyle(grid, AppTheme.Primary);
                root.PerformLayout(); viewport.PerformLayout(); Application.DoEvents();
                Check(name.Text == "Synthetic student" && name.IsInvalid && notes.Text == "Unsubmitted layout fixture" &&
                    choice.SelectedIndex == 0 && date.Value.Date == new DateTime(2026, 1, 15), "DPI/layout reflow preserves values and validation state");
                Check(name.Height >= name.Controls.OfType<TextBox>().Single().PreferredHeight, "Text input has room for its native font");
                Check(name.ClientRectangle.Contains(name.Controls.OfType<TextBox>().Single().Bounds), "Text input inner bounds do not clip");
                Check(choice.Height >= choice.Controls.OfType<ComboBox>().Single().PreferredHeight, "Combo input has room for its native font");
                Check(grid.ColumnHeadersHeight >= AppMetrics.TextHeight(grid.ColumnHeadersDefaultCellStyle.Font!), "Grid headers fit the current font");
                Check(grid.RowTemplate.Height >= AppMetrics.TextHeight(grid.DefaultCellStyle.Font!), "Grid rows fit the current font");
                var headings = card.Header.Controls.OfType<Label>().ToArray();
                Check(headings.All(label => card.Header.ClientRectangle.Contains(label.Bounds)) &&
                    !headings[0].Bounds.IntersectsWith(headings[1].Bounds), "Card title and guidance fit without overlap");
                Check(Math.Abs(tabs.Font.Size - UiDpi.ForControl(tabs, AppTheme.ButtonFont).Size) < 0.02f,
                    "Tab appearance refresh preserves monitor-relative fonts");
                var tabButtons = Descendants(tabHost).OfType<ModernButton>().ToArray();
                Check(tabButtons.Length == 2 && tabButtons.All(button => button.Height >= AppMetrics.TextHeight(button.Font) &&
                    button.Width >= AppMetrics.TextWidth(button.Text, button.Font, 0)),
                    "Tab navigation captions remain readable and clickable at synthetic DPI " + dpi +
                    ", viewport " + available + ": " + string.Join("; ", tabButtons.Select(button =>
                        button.Text + " size=" + button.Size + " required=" +
                        AppMetrics.TextWidth(button.Text, button.Font, 0) + " font=" + button.Font.Size)));
                Check(choice.Width <= choice.MaximumSize.Width && date.Width <= date.MaximumSize.Width,
                    "Fixed-purpose fields retain their font-based width cap on large clients");
                Check(root.Width >= contentMinimum.Width && root.Height >= contentMinimum.Height, "A small viewport preserves readable content instead of shrinking it");
                Check(root.Width >= viewport.ClientSize.Width && root.Height >= viewport.ClientSize.Height, "Large viewports expand the content and grid");
                viewport.ScrollControlIntoView(cancel); Application.DoEvents();
                Rectangle visibleButton = viewport.RectangleToClient(cancel.RectangleToScreen(cancel.ClientRectangle));
                Check(viewport.ClientRectangle.Contains(visibleButton), "The footer action is reachable by scrolling at every synthetic size/DPI");
                // A dialog's monitor may differ from the shell's metric context.
                AppScale.Recompute(new Size(1280, 720), 96);
                HumanUi.ApplyGridStyle(grid, AppTheme.Primary); TabStrip.Repaint(tabs);
                Check(grid.ColumnHeadersHeight >= AppMetrics.TextHeight(grid.ColumnHeadersDefaultCellStyle.Font!) + DpiLayout.Pixels(14, grid.DeviceDpi),
                    "Grid headers retain measured control-DPI headroom when shell scaling changes");
                Check(grid.RowTemplate.Height >= AppMetrics.TextHeight(grid.DefaultCellStyle.Font!) + DpiLayout.Pixels(12, grid.DeviceDpi),
                    "Grid rows retain measured control-DPI headroom when shell scaling changes");
                Check(tabButtons.All(button => button.Height >= AppMetrics.TextHeight(button.Font)),
                    "Tab navigation remains readable when the dialog and shell DPI differ");
                AppScale.Recompute(available, dpi);
                owner.Hide();
            }
        }
        finally { AppScale.Recompute(new Size((int)(1280 * saved.RawFactor * DpiLayout.Factor(saved.Dpi)), (int)(720 * saved.RawFactor * DpiLayout.Factor(saved.Dpi))), saved.Dpi); }
    }

    private static IEnumerable<Control> Descendants(Control root) => root.Controls.Cast<Control>()
        .SelectMany(child => new[] { child }.Concat(Descendants(child)));

    private static void CheckDialogScrollHost()
    {
        AppScale.Recompute(new Size(1280, 720), UiDpi.SystemDpi);
        using var form = new ApplicationWindowForm { ClientSize = new Size(900, 650), MinimumSize = new Size(900, 650) };
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var draft = HumanUi.TextBox(); draft.Text = "Keep the dialog draft"; draft.IsInvalid = true;
        var close = new ModernButton { Text = "Close", Width = 130 };
        root.Controls.Add(draft, 0, 0); root.Controls.Add(HumanUi.ButtonRow(close), 0, 1);
        form.Controls.Add(root); form.Show(); Application.DoEvents();
        Check(root.Parent is ResponsiveViewport, "The shared window integrates a scroll host for non-scrollable dialog content");
        var viewport = (ResponsiveViewport)root.Parent!;
        // The initial viewport can expand beyond the retained content minimum.
        Size required = root.Size; // User-visible content footprint, not private scroll-host internals.
        form.FitToWorkingArea(new Rectangle(0, 0, 640, 400), form.DeviceDpi); Application.DoEvents();
        viewport.ScrollControlIntoView(close); Application.DoEvents();
        Check(viewport.ClientRectangle.Contains(viewport.RectangleToClient(close.RectangleToScreen(close.ClientRectangle))),
            "A fitted dialog's bottom action is fully accessible through its installed scroll host");
        Check(root.Width >= required.Width && root.Height >= required.Height && draft.Text == "Keep the dialog draft" && draft.IsInvalid,
            "Working-area fitting retains readable content and the current draft");
        form.Hide();
    }

    private static void CheckDpiGeometryAndMinimum()
    {
        AppScale.Recompute(new Size(1280, 720), 96);
        using var form = new ApplicationWindowForm { ClientSize = new Size(800, 600), MinimumSize = new Size(700, 500) };
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 80)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var text = new ModernTextBox { Text = "Keep this draft", Width = 200, Height = 40, IsInvalid = true };
        root.Controls.Add(text); form.Controls.Add(root);
        form.ApplyDpiGeometry(192, resizeWindow: true);
        Check(text.Height == 80 && root.RowStyles[0].Height == 160, "Shared DPI handling scales fixed control and table geometry once");
        Size twice = form.ClientSize;
        form.ApplyDpiGeometry(192, resizeWindow: true);
        Check(form.ClientSize == twice && text.Height == 80, "Repeated notification at the same DPI does not duplicate scaling");
        form.ApplyDpiGeometry(96, resizeWindow: true);
        Check(text.Height == 40 && text.Text == "Keep this draft" && text.IsInvalid, "Returning to the original DPI retains geometry and draft state");
        Size originalMinimum = form.MinimumSize;
        form.FitToWorkingArea(new Rectangle(0, 0, 640, 400), 96);
        Check(form.MinimumSize.Width < originalMinimum.Width, "Small work areas temporarily bound the window minimum");
        form.FitToWorkingArea(new Rectangle(0, 0, 1920, 1040), 96);
        Check(form.MinimumSize == originalMinimum, "Larger work areas restore the intended minimum rather than retaining the small-monitor clamp");
        using var primed = new ApplicationWindowForm();
        AppScale.Recompute(new Size(2560, 1440), 192);
        primed.CaptureLayoutDpi();
        var scaledInput = new ModernTextBox { Height = AppScale.Scale(40) }; primed.Controls.Add(scaledInput);
        primed.ApplyDpiGeometry(192, resizeWindow: true);
        Check(scaledInput.Height == 80, "Controls built after priming the shell DPI do not receive another initial scaling pass");
        AppScale.Recompute(new Size(1280, 720), 96);
    }
}
