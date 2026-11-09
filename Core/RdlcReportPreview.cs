namespace FMSC_SMS.Core;

/// <summary>
/// Provides one consistent, user-friendly preview window for every RDLC report.
/// Reports open fitted to the page; users can still resize or maximize the window.
/// </summary>
internal static class RdlcReportPreview
{
    public static void Show(
        IWin32Window? owner,
        string title,
        string reportPath,
        string dataSourceName,
        DataTable data,
        bool fitPageWidth = false,
        string? reportDefinition = null,
        bool enableExternalImages = false,
        bool allowMultiplePages = true)
    {
        title = ReportDocumentNaming.PreviewTitle(title, reportPath, data);
        string suggestedExportName = ReportDocumentNaming.DocumentExportFileName(title, reportPath, data);
        using var printData=RecordTextPolicy.PrintCopy(data);
        Rectangle workingArea = owner is Control ownerControl
            ? Screen.FromControl(ownerControl).WorkingArea
            : Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1366, 768);

        int width = Math.Min(AppScale.Scale(1180), workingArea.Width * 9 / 10);
        int height = Math.Min(AppScale.Scale(820), workingArea.Height * 9 / 10);

        using var form = new ApplicationWindowForm {
            Text = title,
            StartPosition = owner is null
                ? FormStartPosition.CenterScreen
                : FormStartPosition.CenterParent,
            Size = new Size(width, height),
            MinimumSize = new Size(
                Math.Min(AppScale.Scale(850), width),
                Math.Min(AppScale.Scale(600), height)),
            FormBorderStyle = FormBorderStyle.Sizable,
            MaximizeBox = true,
            MinimizeBox = true,
            WindowState = FormWindowState.Maximized,
            KeyPreview = true
        };

        using var viewer = new Microsoft.Reporting.WinForms.ReportViewer {
            Dock = DockStyle.Fill,
            ProcessingMode = Microsoft.Reporting.WinForms.ProcessingMode.Local,
            ShowToolBar = false
        };
        viewer.LocalReport.EnableExternalImages = enableExternalImages;
        viewer.LocalReport.DisplayName = title;
        if (reportDefinition is null) viewer.LocalReport.ReportPath = reportPath;
        else viewer.LocalReport.LoadReportDefinition(new StringReader(reportDefinition));
        viewer.LocalReport.DataSources.Clear();
        viewer.LocalReport.DataSources.Add(
            new Microsoft.Reporting.WinForms.ReportDataSource(dataSourceName, printData));

        using var surface = new Panel { Dock = DockStyle.Fill };
        using var overview = new ReportPageOverview { Visible = false };
        surface.Controls.Add(viewer); surface.Controls.Add(overview);
        Control toolbar = CreateToolbar(form, viewer, fitPageWidth, out var interactions,
            surface, overview, allowMultiplePages, suggestedExportName);
        using var interactionLifetime = interactions;
        form.Controls.Add(surface);
        form.Controls.Add(toolbar);
        using var shortcuts = new ReportShortcutMessageFilter(form);
        form.KeyDown += (_, e) => {
            if (e.KeyCode != Keys.Escape)
                return;
            e.SuppressKeyPress = true;
            form.Close();
        };
        form.Shown += (_, _) => {
            viewer.SetDisplayMode(Microsoft.Reporting.WinForms.DisplayMode.PrintLayout);
            viewer.ZoomMode = fitPageWidth
                ? Microsoft.Reporting.WinForms.ZoomMode.PageWidth
                : Microsoft.Reporting.WinForms.ZoomMode.FullPage;
        };

        if (owner is null)
            form.ShowDialog();
        else
            form.ShowDialog(owner);
    }

    internal static void BindToolbarAction(ApplicationWindowForm form, ModernButton button, Keys keys, string hint, Action execute)
    {
        button.Click += (_, _) => execute();
        form.ShortcutController.RegisterShortcut(button, keys, hint, execute);
    }

    private static Control CreateToolbar(
        ApplicationWindowForm form,
        Microsoft.Reporting.WinForms.ReportViewer viewer,
        bool fitPageWidth,
        out ReportPreviewInteractionFilter interactions,
        Control surface, ReportPageOverview overview, bool allowMultiplePages, string suggestedExportName)
    {
        var toolbar = new FlowLayoutPanel {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            Padding = new Padding(
                AppScale.Scale(12),
                AppScale.Scale(9),
                AppScale.Scale(12),
                AppScale.Scale(9)),
            BackColor = AppTheme.Surface
        };

        ModernButton Button(string text, int width, string role = "Secondary") =>
            new() {
                Text = text,
                Width = AppScale.Scale(width),
                Height = AppMetrics.ButtonHeight,
                Margin = new Padding(0, 0, AppScale.Scale(8), 0),
                ThemeRole = role,
                TakesFocus = true
            };

        var print = Button("Print", 88, "Primary");
        var printCurrent = Button("Print this page", 136);
        var pageSetup = Button("Page setup", 108);
        var exportPdf = Button("Export PDF", 112);
        var previous = Button("Previous", 94);
        var next = Button("Next", 78);
        var close = Button("Close", 76);

        var page = new Label {
            AutoSize = false,
            Width = AppScale.Scale(78),
            Height = AppMetrics.ButtonHeight,
            Margin = new Padding(0, 0, AppScale.Scale(8), 0),
            Font = AppTheme.NormalFont,
            ForeColor = AppTheme.Text,
            TextAlign = ContentAlignment.MiddleCenter,
            Text = "Page 1"
        };
        var pageNumber = new NumericUpDown {
            Minimum = 1, Maximum = 1, Value = 1, Width = AppScale.Scale(66),
            Font = AppTheme.NormalFont, AccessibleName = "Go to page", TextAlign = HorizontalAlignment.Center,
            Margin = new Padding(0, AppScale.Scale(4), AppScale.Scale(4), 0)
        };
        foreach(var editor in pageNumber.Controls.OfType<TextBox>())WholeNumberInput.Attach(editor);
        var go = Button("Go", 52);
        var zoom = new ModernComboBox {
            Width = AppScale.Scale(132),
            Height = AppMetrics.InputControlHeight,
            Margin = new Padding(0, 0, AppScale.Scale(8), 0)
        };
        zoom.Items.AddRange(new object[] {
            "Fit page",
            "Page width",
            "25%",
            "50%",
            "75%",
            "100%",
            "125%",
            "150%",
            "200%",
            "300%",
            "400%"
        });
        zoom.SelectedIndex = fitPageWidth ? 1 : 0;

        var actions = new FlowLayoutPanel {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        actions.Controls.Add(print);
        actions.Controls.Add(printCurrent);
        actions.Controls.Add(pageSetup);
        actions.Controls.Add(exportPdf);

        var navigation = new FlowLayoutPanel {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        navigation.Controls.Add(previous);
        navigation.Controls.Add(pageNumber);
        navigation.Controls.Add(page);
        navigation.Controls.Add(go);
        navigation.Controls.Add(next);
        navigation.Controls.Add(zoom);
        navigation.Controls.Add(close);

        toolbar.Controls.Add(actions);
        toolbar.Controls.Add(navigation);
        ModernComboBox? pageView = null;
        if (allowMultiplePages)
        {
            pageView = new ModernComboBox {
                Width = AppScale.Scale(170), Height = AppMetrics.InputControlHeight,
                Margin = new Padding(0, 0, AppScale.Scale(8), 0), AccessibleName = "Page view"
            };
            pageView.Items.AddRange(new object[] { "Single page", "Multiple pages" });
            pageView.SelectedIndex = ReportLayoutStore.GetPreference("ReportPageView") == "Multiple" ? 1 : 0;
            pageView.SelectionChangeCommitted += (_, _) => Execute("change page view", () => {
                ApplyPageView();
                ReportLayoutStore.SetPreference("ReportPageView", pageView.SelectedIndex == 1 ? "Multiple" : "Single");
            });
            toolbar.Controls.Add(pageView);
        }
        var search = new ReportPreviewSearchBar(form, () => {
            byte[] pdf = viewer.LocalReport.Render("PDF",RdlcReportPrinter.PageDeviceInfo(viewer.GetPageSettings()));
            return Task.Run(() => ReportPageSearchIndex.FromPdf(pdf));
        }, NavigateTo);
        toolbar.Controls.Add(search);
        void ApplyPageView()
        {
            bool multiple = pageView?.SelectedIndex == 1;
            if (multiple && !overview.HasPages)
            {
                form.UseWaitCursor = true;
                try { overview.LoadPages(viewer); }
                catch { if (pageView is not null) pageView.SelectedIndex = 0; overview.Visible = false; viewer.Visible = true; throw; }
                finally { form.UseWaitCursor = false; }
            }
            overview.SetZoom(viewer.ZoomMode, viewer.ZoomPercent);
            overview.Visible = multiple; viewer.Visible = !multiple;
            if (multiple) { overview.BringToFront(); overview.SelectPage(viewer.CurrentPage); }
        }
        overview.PageSelected += selected => { if (viewer.CurrentPage != selected) viewer.CurrentPage = selected; RefreshPageState(); };

        void RefreshPageState()
        {
            int totalPages;
            try
            {
                totalPages = Math.Max(1, viewer.GetTotalPages());
            }
            catch (Exception nonFatalException18)
            {
                FMSC_SMS.Core.AppExceptionHandler.LogNonFatal(nonFatalException18, "RdlcReportPreview: CreateToolbar fallback");
                totalPages = 1;
            }
            int currentPage = Math.Clamp(viewer.CurrentPage, 1, totalPages);
            pageNumber.Maximum = totalPages;
            pageNumber.Value = currentPage;
            page.Text = $"of {totalPages}";
            printCurrent.Text = $"Print page {currentPage}";
            previous.Enabled = pageNumber.Enabled && currentPage > 1;
            next.Enabled = pageNumber.Enabled && currentPage < totalPages;
        }

        void Execute(string action, Action execute)
        {
            try { execute(); }
            catch (Exception ex)
            {
                AppExceptionHandler.LogNonFatal(ex, $"Report {action}");
                ModernMessageDialog.Show(form, $"Unable to {action} the report.\n\n{ex.GetBaseException().Message}",
                    form.Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        BindToolbarAction(form, print, Keys.Control | Keys.P, "Ctrl+P", () => Execute("print", () =>
            RdlcReportPrinter.Print(form, viewer)));
        printCurrent.Click += (_, _) => Execute("print this page", () => RdlcReportPrinter.Print(form, viewer, viewer.CurrentPage));
        void NavigateTo(int number)
        {
            if (!pageNumber.Enabled) return;
            Execute("go to page", () => {
                viewer.CurrentPage = Math.Clamp(number, 1, Math.Max(1, viewer.GetTotalPages()));
                if (overview.Visible) overview.SelectPage(viewer.CurrentPage);
                RefreshPageState();
            });
        }
        go.Click += (_, _) => NavigateTo((int)pageNumber.Value);
        pageNumber.KeyDown += (_, e) => {
            if (e.KeyCode != Keys.Enter) return;
            NavigateTo((int)pageNumber.Value); e.SuppressKeyPress = true;
        };
        form.ShortcutController.RegisterShortcut(pageNumber, Keys.Control | Keys.G, "Ctrl+G", () => {
            if (!pageNumber.Enabled) return;
            pageNumber.Focus(); pageNumber.Select(0, pageNumber.Text.Length);
        });
        form.ShortcutController.RegisterShortcut(go, Keys.Control | Keys.Home, "Ctrl+Home", () => NavigateTo(1));
        form.ShortcutController.RegisterShortcut(go, Keys.Control | Keys.End, "Ctrl+End", () => NavigateTo(Math.Max(1, viewer.GetTotalPages())));
        BindToolbarAction(form, pageSetup, Keys.Alt | Keys.P, "Alt+P", () => Execute("set up", () => { viewer.PageSetupDialog(); overview.ClearPages(); search.ResetIndex(); ApplyPageView(); }));
        BindToolbarAction(form, exportPdf, Keys.Alt | Keys.E, "Alt+E", () => {
            Execute("export", () => {
                using var dialog = new SaveFileDialog {
                    Filter = "PDF document (*.pdf)|*.pdf", DefaultExt = "pdf", AddExtension = true,
                    FileName = suggestedExportName,
                    Title = "Export report"
                };
                if (dialog.ShowDialog(form) != DialogResult.OK) return;
                byte[] bytes = viewer.LocalReport.Render("PDF",RdlcReportPrinter.PageDeviceInfo(viewer.GetPageSettings()));
                AtomicExportFile.Publish(dialog.FileName, temporary => File.WriteAllBytes(temporary, bytes), PdfTableExporter.ValidatePdf);
            });
        });
        BindToolbarAction(form, previous, Keys.Control | Keys.Left, "Ctrl+Left", () => {
            viewer.CurrentPage = Math.Max(1, viewer.CurrentPage - 1);
            if (overview.Visible) overview.SelectPage(viewer.CurrentPage);
            RefreshPageState();
        });
        BindToolbarAction(form, next, Keys.Control | Keys.Right, "Ctrl+Right", () => {
            int totalPages = Math.Max(1, viewer.GetTotalPages());
            viewer.CurrentPage = Math.Min(totalPages, viewer.CurrentPage + 1);
            if (overview.Visible) overview.SelectPage(viewer.CurrentPage);
            RefreshPageState();
        });
        void SelectZoom(string value)
        {
            if (!zoom.Enabled) return;
            if (!zoom.Items.Contains(value)) zoom.Items.Add(value);
            zoom.SelectedItem = value;
            if (value == "Fit page") viewer.ZoomMode = Microsoft.Reporting.WinForms.ZoomMode.FullPage;
            else if (value == "Page width") viewer.ZoomMode = Microsoft.Reporting.WinForms.ZoomMode.PageWidth;
            else {
                viewer.ZoomMode = Microsoft.Reporting.WinForms.ZoomMode.Percent;
                viewer.ZoomPercent = int.Parse(value.TrimEnd('%'));
            }
            overview.SetZoom(viewer.ZoomMode, viewer.ZoomPercent);
        }
        void StepZoom(int direction)
        {
            if (!zoom.Enabled) return;
            int current = overview.Visible ? overview.EffectiveZoomPercent : viewer.ZoomPercent;
            int percent = Math.Clamp(current + direction * 25, 25, 400);
            SelectZoom($"{percent}%");
        }
        zoom.SelectionChangeCommitted += (_, _) => SelectZoom(zoom.SelectedItem!.ToString()!);
        BindToolbarAction(form, close, Keys.Escape, "Esc", () => form.Close());
        form.ShortcutController.RegisterShortcut(zoom, Keys.Control | Keys.D0, "Ctrl+0", () => SelectZoom("Fit page"));
        form.ShortcutController.RegisterShortcut(zoom, Keys.Control | Keys.D1, "Ctrl+1", () => SelectZoom("Page width"));
        form.ShortcutController.RegisterShortcut(zoom, Keys.Control | Keys.D2, "Ctrl+2", () => SelectZoom("100%"));
        form.ShortcutController.RegisterShortcut(zoom, Keys.Control | Keys.Oemplus, "Ctrl++", () => StepZoom(1));
        form.ShortcutController.RegisterShortcut(zoom, Keys.Control | Keys.Add, "Ctrl++", () => StepZoom(1));
        form.ShortcutController.RegisterShortcut(zoom, Keys.Control | Keys.OemMinus, "Ctrl+-", () => StepZoom(-1));
        form.ShortcutController.RegisterShortcut(zoom, Keys.Control | Keys.Subtract, "Ctrl+-", () => StepZoom(-1));
        var canvasMenu = new ContextMenuStrip();
        canvasMenu.Items.Add("Zoom in (Ctrl++)", null, (_, _) => StepZoom(1));
        canvasMenu.Items.Add("Zoom out (Ctrl+-)", null, (_, _) => StepZoom(-1));
        canvasMenu.Items.Add(new ToolStripSeparator());
        canvasMenu.Items.Add("Fit page (Ctrl+0)", null, (_, _) => SelectZoom("Fit page"));
        canvasMenu.Items.Add("Page width (Ctrl+1)", null, (_, _) => SelectZoom("Page width"));
        canvasMenu.Items.Add("100% (Ctrl+2)", null, (_, _) => SelectZoom("100%"));
        canvasMenu.Items.Add(new ToolStripSeparator());
        canvasMenu.Items.Add("Go to page (Ctrl+G)", null, (_, _) => { pageNumber.Focus(); pageNumber.Select(0, pageNumber.Text.Length); });
        canvasMenu.Items.Add("First page (Ctrl+Home)", null, (_, _) => NavigateTo(1));
        canvasMenu.Items.Add("Last page (Ctrl+End)", null, (_, _) => NavigateTo(Math.Max(1, viewer.GetTotalPages())));
        var printPageMenu = canvasMenu.Items.Add("Print this page", null, (_, _) => {
            if (printCurrent.Enabled) printCurrent.PerformClick();
        });
        canvasMenu.Opening += (_, _) => { printPageMenu.Text = printCurrent.Text; printPageMenu.Enabled = printCurrent.Enabled; };
        if (pageView is not null)
        {
            canvasMenu.Items.Add(new ToolStripSeparator());
            var single = new ToolStripMenuItem("Single page");
            var multiple = new ToolStripMenuItem("Multiple pages");
            void ChooseView(int index)
            {
                if (!pageView.Enabled) return;
                pageView.SelectedIndex = index;
                Execute("change page view", () => {
                    ApplyPageView();
                    ReportLayoutStore.SetPreference("ReportPageView", pageView.SelectedIndex == 1 ? "Multiple" : "Single");
                });
            }
            single.Click += (_, _) => ChooseView(0); multiple.Click += (_, _) => ChooseView(1);
            canvasMenu.Items.Add(single); canvasMenu.Items.Add(multiple);
            canvasMenu.Opening += (_, _) => {
                single.Checked = pageView.SelectedIndex == 0; multiple.Checked = pageView.SelectedIndex == 1;
                single.Enabled = multiple.Enabled = pageView.Enabled;
            };
        }
        toolbar.Controls.Add(new Label {
            AutoSize = true, Text = "Ctrl+wheel: zoom · Drag: scroll · Right-click: view options",
            ForeColor = AppTheme.Text, Font = AppTheme.NormalFont,
            Margin = new Padding(0, AppScale.Scale(8), 0, 0)
        });
        interactions = new ReportPreviewInteractionFilter(form, surface, direction => Execute("zoom", () => StepZoom(direction)), canvasMenu);
        form.ShortcutController.RefreshHints();
        void SetReady(bool ready)
        {
            search.Enabled = ready;
            print.Enabled = printCurrent.Enabled = pageSetup.Enabled = exportPdf.Enabled = zoom.Enabled = pageNumber.Enabled = go.Enabled = ready;
            printPageMenu.Enabled = ready;
            if (pageView is not null) pageView.Enabled = ready;
            if (!ready) { previous.Enabled = next.Enabled = false; page.Text = "Loading..."; }
        }
        SetReady(false);
        viewer.RenderingBegin += (_, _) => SetReady(false);
        viewer.PageNavigation += (_, _) => {
            if (!form.IsDisposed && form.IsHandleCreated)
                form.BeginInvoke(RefreshPageState);
        };
        form.Shown += (_, _) => RefreshPageState();
        viewer.RenderingComplete += (_, e) => {
            // Navigation or closing can cancel an obsolete render without failing the report.
            if (e.Exception is OperationCanceledException) return;
            if (!form.IsDisposed && form.IsHandleCreated)
                form.BeginInvoke((Action)(() => {
                    if (form.IsDisposed || viewer.IsDisposed) return;
                    SetReady(e.Exception is null);
                    if (e.Exception is not null)
                    {
                        page.Text = "Preview failed";
                        AppExceptionHandler.LogNonFatal(e.Exception, "Report preview rendering");
                        ModernMessageDialog.Show(form, $"Unable to preview the report.\n\n{e.Exception.GetBaseException().Message}",
                            form.Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                    // Rendering print layout can replace the internal page control.
                    // Reapply the selected zoom after it exists, including the first render.
                    if (zoom.SelectedIndex == 0 && viewer.ZoomMode != Microsoft.Reporting.WinForms.ZoomMode.FullPage)
                        viewer.ZoomMode = Microsoft.Reporting.WinForms.ZoomMode.FullPage;
                    else if (zoom.SelectedIndex == 1 && viewer.ZoomMode != Microsoft.Reporting.WinForms.ZoomMode.PageWidth)
                        viewer.ZoomMode = Microsoft.Reporting.WinForms.ZoomMode.PageWidth;
                    Execute("show page view", ApplyPageView);
                    RefreshPageState();
                    printPageMenu.Text = printCurrent.Text;
                }));
        };

        return toolbar;
    }
}

