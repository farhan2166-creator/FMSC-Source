namespace FMSC_SMS.Core
{
    public static partial class UiHelper
    {
        public static void ApplyGridPresentation(DataGridView grid)
        {
            if (grid == null)
                return;
            ApplyGridRowMetrics(grid);
            if (grid is not KeyboardDataGridView) grid.StandardTab = true;
            grid.CellFormatting -= GridCellFormatting;
            grid.CellFormatting += GridCellFormatting;
            grid.DataBindingComplete -= GridDataBindingComplete;
            grid.DataBindingComplete += GridDataBindingComplete;
            grid.Paint -= GridPaintEmptyState;
            grid.Paint += GridPaintEmptyState;
            grid.ShowCellToolTips = true;
            grid.MouseMove -= GridMouseMove;
            grid.MouseMove += GridMouseMove;
            grid.CellMouseEnter -= GridCellMouseEnter;
            grid.CellMouseEnter += GridCellMouseEnter;
            grid.CellMouseLeave -= GridCellMouseLeave;
            grid.CellMouseLeave += GridCellMouseLeave;
            grid.CellPainting -= GridCellPainting;
            grid.CellPainting += GridCellPainting;
            grid.CellToolTipTextNeeded -= GridCellToolTipTextNeeded;
            grid.CellToolTipTextNeeded += GridCellToolTipTextNeeded;
            /* Column widths are kept in step with the panel by FitColumnsToWidth. */
            grid.RowsAdded -= GridRowsAdded;
            grid.RowsAdded += GridRowsAdded;
            grid.SizeChanged -= GridSizeChanged;
            grid.SizeChanged += GridSizeChanged;
            grid.HandleCreated -= GridSizeChanged;
            grid.HandleCreated += GridSizeChanged;
            grid.ColumnStateChanged -= GridColumnStateChanged;
            grid.ColumnStateChanged += GridColumnStateChanged;
            grid.ColumnAdded -= GridColumnsChanged;
            grid.ColumnAdded += GridColumnsChanged;
            grid.ColumnRemoved -= GridColumnsChanged;
            grid.ColumnRemoved += GridColumnsChanged;
            grid.Disposed -= GridFitDisposed;
            grid.Disposed += GridFitDisposed;
            ApplyGridColumnPresentation(grid);
            ScheduleColumnFit(grid);
        }
        /// <summary>Apply shared minimum row spacing; allow wrapped content to grow.</summary>
        private static void ApplyGridRowMetrics(DataGridView grid, int startIndex = 0, int? rowCount = null)
        {
            int height = Math.Max(UiDpi.Metric(grid, AppMetrics.GridRowHeight),
                AppMetrics.TextHeight(UiDpi.ForControl(grid, AppTheme.NormalFont)) + UiDpi.Metric(grid, AppScale.Scale(12)));
            grid.RowTemplate.MinimumHeight = height;
            grid.RowTemplate.Height = height;
            int endIndex = Math.Min(grid.Rows.Count, startIndex + (rowCount ?? grid.Rows.Count));
            bool fixedHeight = grid.AutoSizeRowsMode == DataGridViewAutoSizeRowsMode.None;
            for (int index = Math.Max(0, startIndex); index < endIndex; index++)
            {
                // Avoid unsharing rows that already have the standard dimensions.
                DataGridViewRow shared = grid.Rows.SharedRow(index);
                if (shared.MinimumHeight == height && (!fixedHeight || shared.Height == height))
                    continue;
                DataGridViewRow row = grid.Rows[index];
                row.MinimumHeight = height;
                if (fixedHeight) row.Height = height;
            }
        }
        /* One small object per grid, held weakly so it goes away with the grid. */
        private sealed class GridLook
        {
            public Color Accent;
            public bool HasAccent;
            public int HoverRow = -1;
            public bool CompactMoneyHeaders;
        }
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DataGridView, GridLook> GridLooks = new();
        private sealed record GridValueAlignment(DataGridViewContentAlignment Value);
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DataGridViewColumn, GridValueAlignment> GridValueAlignments = new();
        internal static void SetGridColumnAlignment(DataGridViewColumn column, DataGridViewContentAlignment alignment)
        {
            GridValueAlignments.Remove(column);
            GridValueAlignments.Add(column,new GridValueAlignment(alignment));
            column.DefaultCellStyle.Alignment=alignment;
            column.HeaderCell.Style.Alignment=alignment;
        }
        /// <summary>
        /// Use the same centered heading/value axis in read-only dialog tables as
        /// in form grids. Longer descriptions stay left aligned; these are only
        /// presentation changes and never affect the underlying cell values.
        /// </summary>
        internal static void AlignDialogGrid(DataGridView grid)
        {
            foreach(DataGridViewColumn column in grid.Columns)
            {
                if(!column.Visible)continue;
                string name=string.IsNullOrEmpty(column.DataPropertyName)
                    ?column.Name:column.DataPropertyName;
                bool centered=IsCenteredGridValue(column)||IsStatusColumn(column)||
                    name is "ReceiptNumber" or "TransactionType" or "PaymentMethod" or
                        "Adjusted" or "NetPaid" or "Original" or "Net" or
                        "ReceiptTotal" or "BalanceThen";
                if(centered)
                    SetGridColumnAlignment(column,DataGridViewContentAlignment.MiddleCenter);
                else if(name is "Student" or "StudentName" or "ChargeType" or "Description" or "Details")
                    SetGridColumnAlignment(column,DataGridViewContentAlignment.MiddleLeft);
            }
        }
        internal static void UseCompactMoneyHeaders(DataGridView grid)
        {
            GridLooks.GetOrCreateValue(grid).CompactMoneyHeaders = true;
            ApplyGridColumnPresentation(grid);
        }
        private static Color GridAccent(DataGridView grid) =>
            GridLooks.TryGetValue(grid, out GridLook? look) && look.HasAccent
                ? look.Accent
                : AppTheme.Primary;
        internal static Color SharedGridAccent(DataGridView grid) => GridAccent(grid);
        /// <summary>Use a tinted header, bold text and an accent underline.</summary>
        internal static void ApplyGridHeaderStyle(DataGridView grid, Color accent)
        {
            GridLook look = GridLooks.GetOrCreateValue(grid);
            look.Accent = accent;
            look.HasAccent = true;
            Color back = AppTheme.Tint(accent, 0.90f);
            var header = grid.ColumnHeadersDefaultCellStyle;
            header.BackColor = back;
            header.ForeColor = AppTheme.HeadingText;
            header.SelectionBackColor = back;
            header.SelectionForeColor = AppTheme.HeadingText;
            header.Font = UiDpi.ForControl(grid, AppTheme.ButtonFont);
            grid.ColumnHeadersHeight = Math.Max(UiDpi.Metric(grid, AppMetrics.GridHeaderHeight),
                AppMetrics.TextHeight(header.Font) + UiDpi.Metric(grid, AppScale.Scale(14)));
            grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        }
        private static void GridCellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
        {
            if (sender is not DataGridView grid ||
                e.Graphics is null ||
                e.ColumnIndex < 0 ||
                e.CellBounds.Width <= 0 ||
                e.CellBounds.Height <= 0)
            {
                return;
            }
            if (e.RowIndex == -1)
            {
                var column=grid.Columns[e.ColumnIndex];
                ApplyGridValueAlignment(column);
                if(column.SortMode!=DataGridViewColumnSortMode.NotSortable &&
                    column.HeaderCell.InheritedStyle.Alignment is DataGridViewContentAlignment.MiddleCenter or DataGridViewContentAlignment.MiddleRight)
                    PaintAlignedSortableHeader(grid,column,e);
                else e.Paint(e.ClipBounds, e.PaintParts);
                using (var line = new SolidBrush(GridAccent(grid)))
                {
                    e.Graphics.FillRectangle(
                        line,
                        e.CellBounds.Left,
                        e.CellBounds.Bottom - 2,
                        e.CellBounds.Width,
                        2);
                }
                e.Handled = true;
                return;
            }
            if (e.RowIndex < 0 ||
                e.ColumnIndex >= grid.Columns.Count ||
                grid.Columns[e.ColumnIndex].Tag as string != BadgeColumnTag ||
                e.FormattedValue is not string text ||
                text.Length == 0)
            {
                return;
            }
            PaintBadgeCell(e, text);
        }
        private static void PaintAlignedSortableHeader(DataGridView grid,DataGridViewColumn column,DataGridViewCellPaintingEventArgs e)
        {
            // Native sortable headers subtract an arrow slot from the text bounds,
            // shifting their text relative to the values even when styles match.
            e.Paint(e.ClipBounds,e.PaintParts & ~(DataGridViewPaintParts.ContentForeground|DataGridViewPaintParts.ContentBackground));
            var style=column.HeaderCell.InheritedStyle;
            var bounds=e.CellBounds;
            var padding=style.Padding;
            var textBounds=new Rectangle(bounds.Left+padding.Left,bounds.Top+padding.Top,
                Math.Max(1,bounds.Width-padding.Horizontal-1),Math.Max(1,bounds.Height-padding.Vertical-1));
            var flags=TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix|TextFormatFlags.PreserveGraphicsClipping;
            flags|=style.Alignment==DataGridViewContentAlignment.MiddleRight?TextFormatFlags.Right:TextFormatFlags.HorizontalCenter;
            if(grid.RightToLeft==RightToLeft.Yes)flags|=TextFormatFlags.RightToLeft;
            flags|=style.WrapMode==DataGridViewTriState.True?TextFormatFlags.WordBreak:TextFormatFlags.SingleLine;
            string text=Convert.ToString(e.FormattedValue)??string.Empty;
            var font=style.Font??grid.Font;
            int height=Math.Min(textBounds.Height,TextRenderer.MeasureText(e.Graphics!,text,font,new Size(textBounds.Width,int.MaxValue),flags).Height);
            textBounds.Y+=(textBounds.Height-height)/2;textBounds.Height=height;
            TextRenderer.DrawText(e.Graphics!,text,font,textBounds,style.ForeColor,flags);
            if(column.HeaderCell.SortGlyphDirection is SortOrder.Ascending or SortOrder.Descending)
            {
                int width=Math.Max(4,UiDpi.Metric(grid,6)),half=width/2;
                int x=grid.RightToLeft==RightToLeft.Yes?bounds.Left+UiDpi.Metric(grid,2):bounds.Right-UiDpi.Metric(grid,2)-width;
                int y=bounds.Top+bounds.Height/2;
                bool ascending=column.HeaderCell.SortGlyphDirection==SortOrder.Ascending;
                Point[] triangle={new(x,y+(ascending?half:-half)),new(x+width,y+(ascending?half:-half)),new(x+half,y+(ascending?-half:half))};
                using var brush=new SolidBrush(style.ForeColor);
                e.Graphics!.FillPolygon(brush,triangle);
            }
        }
        private const string BadgeColumnTag = "fmsc:badge";
        /// <summary>Category columns that read better as a small coloured label than as plain text.</summary>
        private static bool IsBadgeColumn(DataGridViewColumn column)
        {
            string name = column.Name ?? string.Empty;
            return name.EndsWith("Frequency", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("ProgramType", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("CampusType", StringComparison.OrdinalIgnoreCase);
        }
        private static Color BadgeColor(string text)
        {
            /* Use a stable character sum for colors; string.GetHashCode varies between runs. */
            int sum = 0;
            foreach (char c in text.Trim().ToLowerInvariant())
                sum = unchecked(sum * 31 + c);
            int slot = (sum & 0x7fffffff) % 3;
            return slot switch {
                0 => AppTheme.Primary,
                1 => AppTheme.Success,
                _ => AppTheme.Warning
            };
        }
        private static void PaintBadgeCell(DataGridViewCellPaintingEventArgs e, string text)
        {
            var style = e.CellStyle;
            var g = e.Graphics!;
            // Background, selection and the row border are painted as usual; only the text is replaced.
            e.Paint(e.ClipBounds, e.PaintParts & ~DataGridViewPaintParts.ContentForeground);
            Font font = style?.Font ?? AppTheme.NormalFont;
            Size textSize = TextRenderer.MeasureText(g, text, font, Size.Empty, TextFormatFlags.NoPadding);
            int padLeft = style?.Padding.Left ?? 8;
            int maxWidth = Math.Max(20, e.CellBounds.Width - padLeft - 8);
            int height = Math.Min(24, Math.Max(16, e.CellBounds.Height - 14));
            int width = Math.Max(height, Math.Min(maxWidth, textSize.Width + 20));
            int x = e.CellBounds.Left + padLeft;
            if (style is not null &&
                (style.Alignment == DataGridViewContentAlignment.MiddleCenter ||
                 style.Alignment == DataGridViewContentAlignment.TopCenter ||
                 style.Alignment == DataGridViewContentAlignment.BottomCenter))
            {
                x = e.CellBounds.Left + (e.CellBounds.Width - width) / 2;
            }
            var pill = new Rectangle(
                x,
                e.CellBounds.Top + (e.CellBounds.Height - height) / 2,
                width,
                height);
            Color accent = BadgeColor(text);
            var oldMode = g.SmoothingMode;
            using var fill = new SolidBrush(AppTheme.Tint(accent, 0.86f));
            if (AppTheme.UseSimpleRendering)
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
                g.FillRectangle(fill, pill);
            }
            else
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var path = new System.Drawing.Drawing2D.GraphicsPath();
                int d = height;
                path.AddArc(pill.Left, pill.Top, d, d, 90, 180);
                path.AddArc(pill.Right - d, pill.Top, d, d, 270, 180);
                path.CloseFigure();
                g.FillPath(fill, path);
            }
            g.SmoothingMode = oldMode;
            TextRenderer.DrawText(
                g,
                text,
                font,
                pill,
                AppTheme.StatusText(accent),
                TextFormatFlags.HorizontalCenter |
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPadding);
            e.Handled = true;
        }
        private static void GridMouseMove(object? sender, MouseEventArgs e)
        {
            if (sender is not DataGridView grid)
                return;
            var hit = grid.HitTest(e.X, e.Y);
            if (hit.Type == DataGridViewHitTestType.Cell &&
                grid.Columns[hit.ColumnIndex] is DataGridViewLinkColumn)
                return;
            grid.Cursor = Cursors.Default;
        }
        private static void GridCellToolTipTextNeeded(object? sender, DataGridViewCellToolTipTextNeededEventArgs e)
        {
            if (sender is not DataGridView grid ||
                e.RowIndex < 0 ||
                e.RowIndex >= grid.Rows.Count ||
                e.ColumnIndex < 0 ||
                e.ColumnIndex >= grid.Columns.Count)
            {
                return;
            }
            var column = grid.Columns[e.ColumnIndex];
            if (string.Equals(column.Name, "__OrderUp", StringComparison.Ordinal))
            {
                e.ToolTipText = "Move this row up.";
                return;
            }
            if (string.Equals(column.Name, "__OrderDown", StringComparison.Ordinal))
            {
                e.ToolTipText = "Move this row down.";
                return;
            }
            if (column is DataGridViewButtonColumn ||
                column is DataGridViewCheckBoxColumn ||
                column is DataGridViewImageColumn)
            {
                return;
            }
            try
            {
                var cell =
                    grid.Rows[e.RowIndex].Cells[e.ColumnIndex];
                string text =
                    cell.FormattedValue?.ToString() ?? string.Empty;
                if (text.Length == 0)
                    return;
                var font =
                    cell.InheritedStyle?.Font ??
                    grid.DefaultCellStyle.Font ??
                    AppTheme.NormalFont;
                int textWidth =
                    UiTextWidthCache.Measure(text, font);
                int availableWidth =
                    column.Width - 20;
                if (textWidth > availableWidth)
                    e.ToolTipText = text;
            }
            catch (Exception nonFatalException22)
            {
                FMSC_SMS.Core.AppExceptionHandler.LogNonFatal(nonFatalException22, "UiHelper.Grid: GridCellToolTipTextNeeded fallback");
            }
        }
        private static void GridDataBindingComplete(object? sender, DataGridViewBindingCompleteEventArgs e)
        {
            if (sender is not DataGridView grid ||
                grid.IsDisposed ||
                grid.Disposing)
            {
                return;
            }
            try
            {
                if (GridLooks.TryGetValue(grid, out GridLook? look))
                    look.HoverRow = -1;
                /* A bind is the answer to "waiting / loading / failed": from here on the grid shows its rows, or its ordinary "No records found." message. */
                GridStates.Remove(grid);
                ApplyGridRowMetrics(grid);
                ApplyGridColumnPresentation(grid);
                if (grid.AutoSizeColumnsMode == DataGridViewAutoSizeColumnsMode.None)
                    ScheduleColumnFit(grid);
                grid.Invalidate();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException fallbackException3)
            {
                AppExceptionHandler.LogNonFatal(fallbackException3, "UiHelper.Grid: InvalidOperationException recovery");
            }
            catch (NullReferenceException fallbackException4)
            {
                AppExceptionHandler.LogNonFatal(fallbackException4, "UiHelper.Grid: NullReferenceException recovery");
            }
        }
        private const string DefaultGridEmptyMessage =
            "No records found.";
        public static void SetGridEmptyMessage(DataGridView grid, string message)
        {
            if (grid != null)
                grid.Tag = message;
        }
        /* What a grid says while it has never been bound (no columns yet), e.g. "Run the report to view results." Once the page binds anything. */
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DataGridView, string> GridPlaceholders = new();
        public static void SetGridPlaceholderMessage(DataGridView grid, string message)
        {
            if (grid == null)
                return;
            GridPlaceholders.Remove(grid);
            GridPlaceholders.Add(grid, message);
            grid.Invalidate();
        }
        /// <summary>Distinguish initial, loading, empty and failed grid states.</summary>
        public enum GridDisplayState
        {
            /// <summary>Nothing has been asked for yet (no student selected, no search run).</summary>
            Waiting,
            /// <summary>The rows are being fetched.</summary>
            Loading,
            /// <summary>The fetch failed; the rows shown (none) are not the answer.</summary>
            Failed
        }
        private sealed class GridStateInfo
        {
            public GridDisplayState State;
            public string Message = string.Empty;
        }
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DataGridView, GridStateInfo> GridStates = new();
        public static void SetGridState(DataGridView grid, GridDisplayState state, string? message = null)
        {
            if (grid == null || grid.IsDisposed)
                return;
            GridStates.Remove(grid);
            GridStates.Add(grid, new GridStateInfo {
                State = state,
                Message = !string.IsNullOrWhiteSpace(message)
                    ? message!
                    : state switch {
                        GridDisplayState.Loading => "Loading…",
                        GridDisplayState.Failed => "Could not load the records. Try again.",
                        _ => "Nothing to show yet."
                    }
            });
            grid.Invalidate();
        }
        public static void ClearGridState(DataGridView grid)
        {
            if (grid == null)
                return;
            GridStates.Remove(grid);
            grid.Invalidate();
        }
        private static void PaintGridStateMessage(DataGridView grid, Graphics g, GridStateInfo info)
        {
            int headerHeight =
                grid.ColumnHeadersVisible &&
                grid.Columns.Count > 0
                    ? grid.ColumnHeadersHeight
                    : 0;
            var area = new Rectangle(0, headerHeight, grid.ClientSize.Width, grid.ClientSize.Height - headerHeight);
            if (area.Height <= 0 || area.Width <= 0)
                return;
            Color color = info.State == GridDisplayState.Failed
                ? AppTheme.StatusText(AppTheme.Danger)
                : AppTheme.MutedText;
            var textArea = new Rectangle(
                area.Left + 24,
                area.Top + Math.Max(12, area.Height / 3),
                Math.Max(1, area.Width - 48),
                Math.Min(70, Math.Max(36, area.Height / 4)));
            TextRenderer.DrawText(
                g,
                info.Message,
                AppTheme.NormalFont,
                textArea,
                color,
                TextFormatFlags.HorizontalCenter |
                TextFormatFlags.Top |
                TextFormatFlags.WordBreak);
        }
        private static void GridPaintEmptyState(object? sender, PaintEventArgs e)
        {
            if (sender is not DataGridView grid ||
                grid.IsDisposed ||
                grid.RowCount > 0)
            {
                return;
            }
            if (GridStates.TryGetValue(grid, out GridStateInfo? stateInfo))
            {
                PaintGridStateMessage(grid, e.Graphics, stateInfo);
                return;
            }
            /* It now shows the placeholder its page gave it (SetGridPlaceholderMessage) and stays blank only when the page gave none. */
            string? placeholder = null;
            if (grid.Columns.Count == 0 &&
                !GridPlaceholders.TryGetValue(grid, out placeholder))
            {
                return;
            }
            int headerHeight =
                grid.ColumnHeadersVisible &&
                grid.Columns.Count > 0
                    ? grid.ColumnHeadersHeight
                    : 0;
            var area = new Rectangle(0, headerHeight, grid.ClientSize.Width, grid.ClientSize.Height - headerHeight);
            if (area.Height <= 0 || area.Width <= 0)
                return;
            string message =
                placeholder ??
                grid.Tag as string ??
                DefaultGridEmptyMessage;
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int iconSize = Math.Min(52, Math.Max(36, area.Height / 5));
            int centerX = area.Left + area.Width / 2;
            int iconTop = area.Top + Math.Max(12, area.Height / 2 - iconSize - 18);
            using (var iconBrush = new SolidBrush(AppTheme.PrimaryLight))
                g.FillEllipse(iconBrush, centerX - iconSize / 2, iconTop, iconSize, iconSize);
            using (var iconPen = new Pen(AppTheme.Primary, 2f))
            {
                g.DrawEllipse(iconPen, centerX - iconSize / 2 + 1, iconTop + 1, iconSize - 2, iconSize - 2);
                g.DrawLine(iconPen, centerX - 8, iconTop + iconSize / 2, centerX - 2, iconTop + iconSize / 2 + 6);
                g.DrawLine(iconPen, centerX - 2, iconTop + iconSize / 2 + 6, centerX + 10, iconTop + iconSize / 2 - 7);
            }
            var textArea = new Rectangle(
                area.Left + 24,
                iconTop + iconSize + 12,
                Math.Max(1, area.Width - 48),
                Math.Min(70, Math.Max(36, area.Height / 4)));
            TextRenderer.DrawText(
                g,
                message,
                AppTheme.NormalFont,
                textArea,
                AppTheme.MutedText,
                TextFormatFlags.HorizontalCenter |
                TextFormatFlags.Top |
                TextFormatFlags.WordBreak);
        }
        /* Students are identified by Admission Number; the Registration Number is only a display value for reporting. */
        private static void ApplyGridColumnPresentation(DataGridView grid)
        {
            WireAdmissionNumberSorting(grid);
            bool feeGrid = grid.Columns.Cast<DataGridViewColumn>().Any(column =>
                (column.DataPropertyName.Length > 0 ? column.DataPropertyName : column.Name)
                    is "FeeTypeName" or "FeeType" or "FeeTypeId");
            foreach (DataGridViewColumn column in grid.Columns)
            {
                string name = column.Name ?? string.Empty;
                if (IsRawDatabaseColumn(name))
                {
                    column.Visible = false;
                    continue;
                }
                column.HeaderText =
                    HumanizeColumnHeader(column.HeaderText, feeGrid);
                if (GridLooks.TryGetValue(grid,out var look) && look.CompactMoneyHeaders)
                    column.HeaderText = column.HeaderText.Replace(" (Rs.)",string.Empty);
                if (name == "AdmissionNumber")
                    column.SortMode = DataGridViewColumnSortMode.Programmatic;
                if (string.IsNullOrEmpty(column.DefaultCellStyle.Format) &&
                    (column.ValueType == typeof(DateTime) ||
                    column.ValueType == typeof(DateTime?) ||
                    name.EndsWith("Date", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith("At", StringComparison.OrdinalIgnoreCase)))
                {
                    bool hasTime =
                        name.EndsWith("At", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Time", StringComparison.OrdinalIgnoreCase);
                    column.DefaultCellStyle.Format =
                        hasTime
                            ? "dd-MMM-yyyy hh:mm tt"
                            : "dd-MMM-yyyy";
                }
                /* Apply the default money format only when the page has not supplied one. */
                if (string.IsNullOrEmpty(column.DefaultCellStyle.Format) &&
                    (column.ValueType == typeof(decimal) || column.ValueType == typeof(decimal?)) &&
                    IsMoneyColumnName(name))
                {
                    column.DefaultCellStyle.Format = "N0";
                }
                if (IsStatusColumn(column))
                {
                    column.DefaultCellStyle.Alignment =
                        DataGridViewContentAlignment.MiddleCenter;
                    column.DefaultCellStyle.Font =
                        UiDpi.ForControl(grid, AppTheme.ButtonFont);
                }
                else if (column.Tag is null &&
                         column is DataGridViewTextBoxColumn &&
                         (grid.ReadOnly || column.ReadOnly) &&
                         IsBadgeColumn(column))
                {
                    column.Tag = BadgeColumnTag;
                }
                /* The heading follows its column, so a right-aligned amount is not headed by left-aligned text. */
                ApplyGridValueAlignment(column);
                ApplyContentAwareMinimumWidth(grid, column);
            }
        }
        private static bool IsMoneyColumnName(string name) =>
            name.Contains("Amount", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Balance", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Fee", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Paid", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Due", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Discount", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Fine", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Total", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Charge", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Outstanding", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Refund", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Collection", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Received", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Correction", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Credit", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Settled", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Payable", StringComparison.OrdinalIgnoreCase);
        /// <summary>True when the grid and at least one visible column allow editing.</summary>
        private static bool IsEditableGrid(DataGridView grid)
        {
            if (grid.ReadOnly)
                return false;
            foreach (DataGridViewColumn column in grid.Columns)
                if (column.Visible && !column.ReadOnly)
                    return true;
            return false;
        }
        internal static bool IsRawDatabaseColumn(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            return
                name.StartsWith("__", StringComparison.Ordinal) ||
                name.Equals("AdmissionNumberBase", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("AdmissionNumberSuffix", StringComparison.OrdinalIgnoreCase) ||
                System.Text.RegularExpressions.Regex.IsMatch(name, @"AdmissionId[12]?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ||
                name.EndsWith("Id", StringComparison.Ordinal) ||
                name.EndsWith("Code", StringComparison.Ordinal) ||
                name.EndsWith("Guid", StringComparison.Ordinal) ||
                name.Equals("CreatedAt", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("UpdatedAt", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("ModifiedAt", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("RowVersion", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("ContentVersion", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Timestamp", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("ConcurrencyToken", StringComparison.OrdinalIgnoreCase);
        }
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DataGridView, object> AdmissionSortWired = new();
        private static void WireAdmissionNumberSorting(DataGridView grid)
        {
            if (AdmissionSortWired.TryGetValue(grid, out _)) return;
            AdmissionSortWired.Add(grid, new object());
            grid.ColumnHeaderMouseClick += (_, e) => {
                if (e.ColumnIndex < 0) return;
                var column = grid.Columns[e.ColumnIndex];
                if (column.Name != "AdmissionNumber" || column.SortMode == DataGridViewColumnSortMode.NotSortable) return;
                DataTable? table = grid.DataSource switch {
                    DataTable source => source,
                    DataView source => source.Table,
                    _ => null
                };
                if (table is null || !table.Columns.Contains("AdmissionNumber")) return;
                AdmissionNumbers.AddSortColumns(table);
                bool descending = column.HeaderCell.SortGlyphDirection == SortOrder.Ascending;
                string direction = descending ? "DESC" : "ASC";
                DataView view = grid.DataSource is DataView bound ? bound : table.DefaultView;
                view.Sort = $"__AdmissionNumberBase {direction}, __AdmissionNumberSuffix {direction}";
                column.HeaderCell.SortGlyphDirection = descending ? SortOrder.Descending : SortOrder.Ascending;
            };
        }
        private static string HumanizeColumnHeader(string headerText, bool feeGrid)
        {
            if (string.IsNullOrEmpty(headerText))
                return headerText;
            // Bound fee lists use the same captions as explicitly defined columns.
            // Match database headings and their humanized form; keep contextual captions such as
            // Pay Now, Posted Amount and transfer comparison descriptions intact.
            if (feeGrid)
            {
                headerText = headerText switch {
                    "FeeTypeName" or "Fee Type Name" or "FeeType" => UiText.Fees.FeeType,
                    "Amount" or "StandardAmount" or "Standard Amount" or "GrossAmount" or "Gross Amount" or "ExpectedAmount" or "Expected Amount" => UiText.Fees.Amount,
                    "Discount" or "DiscountAmount" or "Discount Amount" or "ExpectedDiscountAmount" or "Expected Discount Amount" => UiText.Fees.Discount,
                    "NetAmount" or "Net Amount" or "ExpectedNetAmount" or "Expected Net Amount" => UiText.Fees.NetAmount,
                    "CurrentAmount" or "Current Amount" or "OldAmount" or "Old Amount" => UiText.Fees.CurrentAmount,
                    "NewAmount" or "New Amount" => UiText.Fees.NewAmount,
                    _ => headerText
                };
            }
            string header = headerText.Replace("_", " ");
            header = System.Text.RegularExpressions.Regex.Replace(header, "(?<=[a-z])(?=[A-Z])", " ");
            return header.Trim();
        }
        private static bool IsCenteredGridValue(DataGridViewColumn column)
        {
            if (column is DataGridViewCheckBoxColumn or DataGridViewButtonColumn or DataGridViewImageColumn || IsBadgeColumn(column))
                return true;
            Type? type = Nullable.GetUnderlyingType(column.ValueType ?? typeof(object)) ?? column.ValueType;
            if (type == typeof(DateTime) || type == typeof(DateOnly)) return true;
            if (type is not null && Type.GetTypeCode(type) is TypeCode.Byte or TypeCode.SByte or
                TypeCode.Int16 or TypeCode.UInt16 or TypeCode.Int32 or TypeCode.UInt32 or
                TypeCode.Int64 or TypeCode.UInt64 or TypeCode.Single or TypeCode.Double or TypeCode.Decimal)
                return true;
            string name = column.DataPropertyName.Length > 0 ? column.DataPropertyName : column.Name;
            return name.Equals("AdmissionNumber", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("AdmissionNo", StringComparison.OrdinalIgnoreCase) ||
                column.HeaderText.Equals("Adm. No.", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Date", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("Date", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("BillingMonth", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Period", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("PostedThrough", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("LastBilledMonth", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("EffectiveFrom", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("EffectiveTo", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("AssignmentEffectiveFrom", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Amount", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Balance", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Marks", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Percentage", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Current", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("New", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Discount", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Net", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Due", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("PayNow", StringComparison.OrdinalIgnoreCase);
        }
        private static void ApplyGridValueAlignment(DataGridViewColumn column)
        {
            string header = column.HeaderText.Trim();
            if (column.Name.Equals("AdmissionNumber", StringComparison.OrdinalIgnoreCase) ||
                column.DataPropertyName.Equals("AdmissionNumber", StringComparison.OrdinalIgnoreCase) ||
                header.Equals("Admission Number", StringComparison.OrdinalIgnoreCase) ||
                header.Equals("Admission No.", StringComparison.OrdinalIgnoreCase) ||
                header.Equals("Admission No", StringComparison.OrdinalIgnoreCase))
                column.HeaderText = "Adm. No.";
            if(GridValueAlignments.TryGetValue(column,out var requested))
            {
                column.DefaultCellStyle.Alignment=requested.Value;
                column.HeaderCell.Style.Alignment=requested.Value;
                return;
            }
            // Infer a default only when the page has not chosen an alignment.
            // Presentation, resizing and formatting must use the same decision.
            if (column.DefaultCellStyle.Alignment == DataGridViewContentAlignment.NotSet && IsCenteredGridValue(column))
                column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            if (column.DefaultCellStyle.Alignment != DataGridViewContentAlignment.NotSet)
                column.HeaderCell.Style.Alignment = column.DefaultCellStyle.Alignment;
        }
        private static void ApplyContentAwareMinimumWidth(DataGridView grid, DataGridViewColumn column)
        {
            if (grid == null ||
                column == null ||
                grid.IsDisposed ||
                grid.Disposing)
            {
                return;
            }
            try
            {
                if (column.DataGridView != grid)
                    return;
                if (column.Index < 0 ||
                    column.Index >= grid.Columns.Count)
                {
                    return;
                }
                if (!column.Visible)
                    return;
                /* Use the effective auto-size mode, including inherited settings. */
                if (column.InheritedAutoSizeMode !=
                    DataGridViewAutoSizeColumnMode.None)
                {
                    return;
                }
                column.Width =
                    MeasureNaturalColumnWidth(grid, column) is int measured && measured > 0
                        ? measured
                        : column.Width;
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException fallbackException5)
            {
                AppExceptionHandler.LogNonFatal(fallbackException5, "UiHelper.Grid: InvalidOperationException recovery");
            }
            catch (NullReferenceException fallbackException6)
            {
                AppExceptionHandler.LogNonFatal(fallbackException6, "UiHelper.Grid: NullReferenceException recovery");
            }
        }
        /// <summary>Measured content width, bounded by scaled 80/600-pixel layout limits.</summary>
        private static int MeasureNaturalColumnWidth(
            DataGridView grid,
            DataGridViewColumn column)
        {
            try
            {
                var headerFont =
                    column.HeaderCell.InheritedStyle.Font ??
                    AppTheme.ButtonFont;
                var cellFont =
                    column.DefaultCellStyle?.Font ??
                    grid.DefaultCellStyle?.Font ??
                    AppTheme.NormalFont;
                int headerWidth =
                    UiTextWidthCache.Measure(string.IsNullOrWhiteSpace(column.HeaderText)
                            ? " "
                            : column.HeaderText,
                        headerFont) + Math.Max(UiDpi.Metric(grid, AppScale.Scale(24)), column.HeaderCell.InheritedStyle.Padding.Horizontal + UiDpi.Metric(grid, AppScale.Scale(8)));
                int contentWidth = 0;
                int sampleCount =
                    Math.Min(grid.Rows.Count, 40);
                for (int i = 0; i < sampleCount; i++)
                {
                    DataGridViewRow row;
                    try
                    {
                        row = grid.Rows[i];
                    }
                    catch (Exception nonFatalException23)
                    {
                        FMSC_SMS.Core.AppExceptionHandler.LogNonFatal(nonFatalException23, "UiHelper.Grid: MeasureNaturalColumnWidth fallback");
                        continue;
                    }
                    if (row == null ||
                        row.IsNewRow ||
                        column.Index >= row.Cells.Count)
                    {
                        continue;
                    }
                    try
                    {
                        object? value =
                            row.Cells[column.Index].FormattedValue;
                        if (value == null)
                            continue;
                        string text =
                            Convert.ToString(value) ??
                            string.Empty;
                        if (string.IsNullOrWhiteSpace(text))
                            continue;
                        int width =
                            UiTextWidthCache.Measure(
                                text,
                                cellFont);
                        contentWidth =
                            Math.Max(contentWidth, width);
                    }
                    catch (Exception nonFatalException24)
                    {
                        FMSC_SMS.Core.AppExceptionHandler.LogNonFatal(nonFatalException24, "UiHelper.Grid: MeasureNaturalColumnWidth fallback");
                    }
                }
                int MinimumColumnWidth = UiDpi.Metric(grid, AppScale.Scale(80));
                /* The ceiling is the one MasterPage's own measurement uses. */
                int MaximumColumnWidth = UiDpi.Metric(grid, AppScale.Scale(600));
                int desiredWidth =
                    Math.Max(
                        MinimumColumnWidth,
                        Math.Max(headerWidth, contentWidth + Math.Max(UiDpi.Metric(grid, AppScale.Scale(24)), (grid.DefaultCellStyle?.Padding.Horizontal ?? 0) + UiDpi.Metric(grid, AppScale.Scale(8)))));
                desiredWidth =
                    Math.Min(desiredWidth, MaximumColumnWidth);
                return desiredWidth;
            }
            catch (ObjectDisposedException)
            {
                return 0;
            }
            catch (InvalidOperationException fallbackException7)
            {
                AppExceptionHandler.LogNonFatal(fallbackException7, "UiHelper.Grid: InvalidOperationException recovery");
                return 0;
            }
            catch (NullReferenceException fallbackException8)
            {
                AppExceptionHandler.LogNonFatal(fallbackException8, "UiHelper.Grid: NullReferenceException recovery");
                return 0;
            }
        }
        /// <summary>Identify action columns such as arrows, delete, checkboxes and buttons.</summary>
        private static bool IsWidthPinnedColumn(DataGridViewColumn column)
        {
            string name = column.Name ?? string.Empty;
            if (name.Equals("__OrderUp", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("__OrderDown", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("__Delete", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return column is DataGridViewCheckBoxColumn ||
                   column is DataGridViewButtonColumn ||
                   column is DataGridViewLinkColumn ||
                   column is DataGridViewImageColumn;
        }
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DataGridView, object> EqualWidthGrids = new();
        internal static void UseEqualColumnWidths(DataGridView grid)
        {
            EqualWidthGrids.GetValue(grid, _ => new object());
            FitColumnsToWidth(grid);
        }
        /// <summary>Sizes visible columns using the grid's shared sizing policy.</summary>
        public static void FitColumnsToWidth(DataGridView grid)
        {
            if (grid == null ||
                grid.IsDisposed ||
                grid.Disposing ||
                grid.Columns.Count == 0)
            {
                return;
            }
            if (grid.AutoSizeColumnsMode !=
                DataGridViewAutoSizeColumnsMode.None)
            {
                return;
            }
            // A queued fit can run while a view change replaces the columns.
            // Check automatic sizing first and keep this pass on a stable list.
            var columns=grid.Columns.Cast<DataGridViewColumn>().ToArray();
            foreach (DataGridViewColumn column in columns)
                if(column.DataGridView==grid)ApplyGridValueAlignment(column);
            if (EqualWidthGrids.TryGetValue(grid, out _))
            {
                var visible = columns.Where(column => column.DataGridView == grid && column.Visible).ToArray();
                if (visible.Length == 0) return;
                int availableWidth = Math.Max(0, grid.DisplayRectangle.Width - 2);
                int minimum = visible.Max(column => column.MinimumWidth);
                int width = Math.Max(minimum, availableWidth / visible.Length);
                int remainder = width == minimum && availableWidth < minimum * visible.Length ? 0 : availableWidth % visible.Length;
                for (int index = 0; index < visible.Length; index++)
                {
                    visible[index].AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
                    visible[index].Width = width + (index < remainder ? 1 : 0);
                }
                return;
            }
            var flexible =
                new List<DataGridViewColumn>();
            int pinnedWidth = 0;
            foreach (DataGridViewColumn column in columns)
            {
                if (column.DataGridView!=grid || !column.Visible)
                    continue;
                if (IsWidthPinnedColumn(column))
                {
                    column.AutoSizeMode =
                        DataGridViewAutoSizeColumnMode.None;
                    column.Width =
                        Math.Max(column.Width, column.MinimumWidth);
                    pinnedWidth += column.Width;
                    continue;
                }
                flexible.Add(column);
            }
            if (flexible.Count == 0)
                return;
            /* DisplayRectangle is the area the cells actually get: it already excludes a row header and whichever scrollbars are showing. */
            int available =
                grid.DisplayRectangle.Width - 2;
            if (available <= 0)
                return;
            int natural = 0;
            foreach (DataGridViewColumn column in flexible)
            {
                column.AutoSizeMode =
                    DataGridViewAutoSizeColumnMode.None;
                int width =
                    MeasureNaturalColumnWidth(grid, column);
                if (width > 0)
                    column.Width = width;
                column.Width =
                    Math.Max(column.Width, column.MinimumWidth);
                natural += column.Width;
            }
            int leftover =
                available - pinnedWidth - natural;
            if (leftover > 0)
            {
                int each =
                    leftover / flexible.Count;
                int remainder =
                    leftover % flexible.Count;
                for (int i = 0; i < flexible.Count; i++)
                {
                    flexible[i].Width +=
                        each + (i < remainder ? 1 : 0);
                }
            }
            /* Pin widths after distributing space so auto-sizing cannot undo the result. */
            foreach (DataGridViewColumn column in flexible)
            {
                column.AutoSizeMode =
                    DataGridViewAutoSizeColumnMode.None;
            }
        }
        /// <summary>Coalesce row-addition and resize events before measuring columns.</summary>
        private static readonly HashSet<DataGridView> ColumnFitPending = new();
        private static void ScheduleColumnFit(DataGridView grid)
        {
            if (grid == null ||
                grid.IsDisposed ||
                grid.Disposing ||
                !grid.IsHandleCreated ||
                grid.AutoSizeColumnsMode != DataGridViewAutoSizeColumnsMode.None)
            {
                return;
            }
            if (!ColumnFitPending.Add(grid))
                return;
            try
            {
                grid.BeginInvoke(new MethodInvoker(() => {
                    ColumnFitPending.Remove(grid);
                    if (!grid.IsDisposed && !grid.Disposing)
                        FitColumnsToWidth(grid);
                }));
            }
            catch (InvalidOperationException fallbackException9)
            {
                AppExceptionHandler.LogNonFatal(fallbackException9, "UiHelper.Grid: InvalidOperationException recovery");
                /* No handle to post to yet - the next layout event asks again. */
                ColumnFitPending.Remove(grid);
            }
        }
        private static void GridRowsAdded(
            object? sender,
            DataGridViewRowsAddedEventArgs e)
        {
            if (sender is DataGridView grid)
            {
                GridStates.Remove(grid);
                ApplyGridRowMetrics(grid, e.RowIndex, e.RowCount);
                if (grid.DataSource is null)
                    ScheduleColumnFit(grid);
            }
        }
        private static void GridSizeChanged(
            object? sender,
            EventArgs e)
        {
            if (sender is DataGridView grid)
                ScheduleColumnFit(grid);
        }
        private static void GridFitDisposed(
            object? sender,
            EventArgs e)
        {
            if (sender is DataGridView grid)
                ColumnFitPending.Remove(grid);
        }
        private static void GridColumnStateChanged(object? sender, DataGridViewColumnStateChangedEventArgs e)
        {
            if (sender is DataGridView grid && (e.StateChanged & DataGridViewElementStates.Visible) != 0)
                ScheduleColumnFit(grid);
        }
        private static void GridColumnsChanged(object? sender, DataGridViewColumnEventArgs e)
        {
            if (sender is DataGridView grid)
                ScheduleColumnFit(grid);
        }
        private static bool IsStatusColumn(DataGridViewColumn column)
        {
            string name = column.Name ?? string.Empty;
            string header = column.HeaderText ?? string.Empty;
            return name.Contains("Status", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("State", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("Result", StringComparison.OrdinalIgnoreCase) ||
                   header.Contains("Status", StringComparison.OrdinalIgnoreCase);
        }
        private static void GridCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
        {
            if (sender is not DataGridView grid ||
                e.RowIndex < 0 ||
                e.ColumnIndex < 0 ||
                e.ColumnIndex >= grid.Columns.Count)
            {
                return;
            }
            var column =
                grid.Columns[e.ColumnIndex];
            if(column.DataPropertyName=="ContentVersion"&&e.Value is byte[] version)
            {
                e.Value=Convert.ToHexString(version);e.FormattingApplied=true;return;
            }
            ApplyGridValueAlignment(column);
            if(e.CellStyle is not null && column.DefaultCellStyle.Alignment!=DataGridViewContentAlignment.NotSet)
                e.CellStyle.Alignment=column.DefaultCellStyle.Alignment;
            else if (e.CellStyle is not null && (IsCenteredGridValue(column) || e.Value is DateTime ||
                e.Value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal))
            {
                e.CellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
                column.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
            }
            /* GetRowState reads the state without turning a shared row into an individual one, which Rows[i].Selected would. */
            bool selected =
                (grid.Rows.GetRowState(e.RowIndex) &
                 DataGridViewElementStates.Selected) != 0;
            if(e.CellStyle is not null)
            {
                e.CellStyle.SelectionBackColor=AppTheme.SelectionBackground;
                e.CellStyle.SelectionForeColor=AppTheme.SelectionText;
            }
            if (!selected &&
                GridLooks.TryGetValue(grid, out GridLook? look) &&
                look.HoverRow == e.RowIndex &&
                e.CellStyle is not null)
            {
                e.CellStyle.BackColor = AppTheme.Tint(GridAccent(grid), 0.94f);
            }
            /* Keep editable cells white and calculated cells on the secondary surface. */
            if (!selected &&
                e.CellStyle is not null &&
                !(GridLooks.TryGetValue(grid, out GridLook? hoverLook) && hoverLook.HoverRow == e.RowIndex) &&
                IsEditableGrid(grid))
            {
                bool editable = !column.ReadOnly;
                e.CellStyle.BackColor = editable
                    ? AppTheme.Surface
                    : AppTheme.SurfaceSecondary;
            }
            if (IsStatusColumn(column) &&
                e.Value is not null &&
                e.Value != DBNull.Value)
            {
                if (!selected)
                {
                    ApplyStatusCellStyle(e, Convert.ToString(e.Value)?.Trim() ?? string.Empty);
                }
                return;
            }
            if (e.Value is DateTime date)
            {
                bool hasTime =
                    (column.Name ?? string.Empty).EndsWith("At", StringComparison.OrdinalIgnoreCase) ||
                    (column.Name ?? string.Empty).Contains("Time", StringComparison.OrdinalIgnoreCase);
                e.Value = hasTime
                    ? date.ToString("dd-MMM-yyyy hh:mm tt")
                    : date.ToString("dd-MMM-yyyy");
                e.FormattingApplied = true;
            }
        }
        private static void ApplyStatusCellStyle(DataGridViewCellFormattingEventArgs e, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;
            string normalized =
                value.Trim().ToLowerInvariant();
            Color? foreground = null;
            Color? background = null;
            if (normalized.Contains("cancel") ||
                normalized.Contains("reject") ||
                normalized.Contains("fail") ||
                normalized.Contains("overdue") ||
                normalized.Contains("error"))
            {
                foreground = AppTheme.Danger;
                background = AppTheme.Tint(AppTheme.Danger, 0.90f);
            }
            else if (normalized.Contains("pending") ||
                     normalized.Contains("not collected") ||
                     normalized.Contains("warning") ||
                     normalized.Contains("due") ||
                     normalized.Equals("left", StringComparison.Ordinal))
            {
                foreground = AppTheme.Warning;
                background = AppTheme.Tint(AppTheme.Warning, 0.90f);
            }
            else if (normalized.Contains("active") ||
                     normalized.Contains("collected") ||
                     normalized.Contains("approved") ||
                     normalized.Contains("paid") ||
                     normalized.Contains("completed") ||
                     normalized.Contains("success"))
            {
                foreground = AppTheme.Success;
                background = AppTheme.Tint(AppTheme.Success, 0.90f);
            }
            if (foreground.HasValue)
                e.CellStyle!.ForeColor = foreground.Value; // DataGridView always supplies a non-null CellStyle on CellFormatting.
            if (background.HasValue)
                e.CellStyle!.BackColor = background.Value; // DataGridView always supplies a non-null CellStyle on CellFormatting.
            e.FormattingApplied = true;
        }
        private static void SetHoverRow(DataGridView grid, int row)
        {
            GridLook look = GridLooks.GetOrCreateValue(grid);
            if (look.HoverRow == row)
                return;
            int previous = look.HoverRow;
            look.HoverRow = row;
            if (previous >= 0 && previous < grid.RowCount)
                grid.InvalidateRow(previous);
            if (row >= 0 && row < grid.RowCount)
                grid.InvalidateRow(row);
        }
        private static void GridCellMouseEnter(object? sender, DataGridViewCellEventArgs e)
        {
            if (sender is DataGridView grid && e.RowIndex >= 0)
                SetHoverRow(grid, e.RowIndex);
        }
        private static void GridCellMouseLeave(object? sender, DataGridViewCellEventArgs e)
        {
            if (sender is not DataGridView grid)
                return;
            grid.Cursor = Cursors.Default;
            SetHoverRow(grid, -1);
        }
    }
}
