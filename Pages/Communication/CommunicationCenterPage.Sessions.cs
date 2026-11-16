using System.Data;
using FMSC_SMS.Core;

namespace FMSC_SMS.Pages.Communication;

public sealed partial class CommunicationCenterPage
{
    private readonly ModernComboBox queueView = HumanUi.Combo();
    private ModernButton? masterPauseButton;
    private bool sendingPaused;

    private HumanCard SessionsCard(DataGridView grid, bool unfinished)
    {
        var card = HumanUi.Card(unfinished ? "Queue" : "History");
        card.Header.Visible = false;
        grid.Name = unfinished ? "communicationUnfinishedGrid" : "communicationHistoryGrid";
        grid.AutoGenerateColumns = false;
        foreach (var (field, caption, width, weight) in new[]
        {
            ("CreatedUtc", "Created (UTC)", 135, 100),
            ("MethodText", "Method", 130, 100),
            ("RecipientText", "Recipient", 210, 210),
            ("AudienceText", "Audience", 105, 100),
            ("OperatorName", "Started by", 90, 80),
            ("StateText", "Outcome", 215, 220),
            ("InProgress", "Remaining", 100, 90),
            ("NeedsAttention", "Attention", 95, 80)
        })
        {
            if (unfinished && field == "OperatorName") continue;
            if (!unfinished && field == "NeedsAttention") continue;
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = field, DataPropertyName = field, HeaderText = caption,
                MinimumWidth = AppScale.Scale(width), FillWeight = weight, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                SortMode = DataGridViewColumnSortMode.NotSortable
            });
        }
        grid.Columns["CreatedUtc"]!.DefaultCellStyle.Format = "dd MMM yyyy  HH:mm";
        var search = HumanUi.TextBox(false, 100);
        search.Name = unfinished ? "communicationUnfinishedSearch" : "communicationHistorySearch";
        search.AccessibleName = search.PlaceholderText = unfinished ? "Search queued messages" : "Search history";
        search.Dock = DockStyle.Fill;
        var open = PageButton(unfinished ? "communicationResumeSession" : "communicationViewHistory",
            unfinished ? "Open Batch" : "View Message", 155, unfinished ? "Primary" : "Secondary");
        open.Dock = DockStyle.Fill;
        open.Enabled = false;
        grid.CurrentCellChanged += (_, _) => open.Enabled = grid.CurrentRow?.DataBoundItem is DataRowView;
        open.Click += async (_, _) => await OpenSessionAsync(grid.CurrentRow, unfinished);
        grid.CellDoubleClick += async (_, e) =>
        {
            if (e.RowIndex >= 0) await OpenSessionAsync(grid.Rows[e.RowIndex], unfinished);
        };
        grid.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter || grid.CurrentRow is null) return;
            e.Handled = e.SuppressKeyPress = true;
            await OpenSessionAsync(grid.CurrentRow, unfinished);
        };
        if (unfinished)
        {
            queueView.Name = "communicationQueueView";
            queueView.Items.AddRange(new object[] { "Needs Attention", "Upcoming", "All Active" });
            queueView.SelectedIndex = 2;
            queueView.AccessibleName = "Queue view";
            queueView.SelectedIndexChanged += (_, _) =>
                FilterSessions(grid, search.Text, true, queueView.Text);
        }
        search.TextChanged += (_, _) => FilterSessions(grid, search.Text, unfinished, queueView.Text);
        grid.DataSourceChanged += (_, _) => FilterSessions(grid, search.Text, unfinished, queueView.Text);
        bool showPause = unfinished && CurrentUser.HasPermission("Communication.Configure");
        bool showExport = !unfinished && CurrentUser.HasPermission("Communication.Configure");
        var toolbar = new TableLayoutPanel
        {
            Dock = DockStyle.Top, Height = AppMetrics.ButtonHeight + AppScale.Scale(10),
            ColumnCount = showPause ? 4 : (unfinished || showExport) ? 3 : 2, RowCount = 1, Margin = Padding.Empty
        };
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        if (unfinished) toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, AppScale.Scale(165)));
        if (showPause) toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, AppScale.Scale(185)));
        if (showExport) toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, AppScale.Scale(145)));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, AppScale.Scale(166)));
        toolbar.Controls.Add(search, 0, 0);
        if (unfinished) toolbar.Controls.Add(queueView, 1, 0);
        if (showExport)
        {
            var export = PageButton("communicationExportHistory", "Export…", 135);
            export.Dock = DockStyle.Fill;
            export.Click += async (_, _) => await ExportHistoryAsync(grid);
            toolbar.Controls.Add(export, 1, 0);
        }
        if (showPause)
        {
            masterPauseButton = PageButton("communicationPauseAll", "Pause All Sending", 175);
            masterPauseButton.Dock = DockStyle.Fill;
            masterPauseButton.Enabled = false;
            toolbar.Controls.Add(masterPauseButton, 2, 0);
            masterPauseButton.Click += async (_, _) => await ToggleMasterPauseAsync();
        }
        toolbar.Controls.Add(open, toolbar.ColumnCount - 1, 0);
        card.Content.Controls.Add(grid);
        card.Content.Controls.Add(toolbar);
        return card;
    }

    private Task NavigateSessionsAsync(bool unfinished) =>
        RunBusyAsync(unfinished ? "Reading unfinished sessions." : "Reading communication history.",
            () => ShowSessionsAsync(unfinished), this);

    private async Task ShowSessionsAsync(bool unfinished)
    {
        int actor = CurrentUser.UserId;
        SetStep(unfinished ? UnfinishedStep : HistoryStep);
        try
        {
            var read = operations.ReadSessions;
            DataTable table = await (read is not null ? read(unfinished) : unfinished
                ? CommunicationManagementStore.ReadMyUnfinishedBatchesAsync()
                : CommunicationManagementStore.ReadHistoryAsync());
            if (IsDisposed || actor != CurrentUser.UserId)
            {
                table.Dispose();
                return;
            }
            BindSessions(table, unfinished);
            if (unfinished && masterPauseButton is not null && operations.ReadSessions is null)
                await RefreshSendControlAsync();
            SetStatus("");
        }
        catch (Exception ex) { if (!IsDisposed) Fail(ex); }
    }

    private void BindSessions(DataTable table, bool unfinished)
    {
        table.Columns.Add("MethodText", typeof(string));
        table.Columns.Add("AudienceText", typeof(string));
        table.Columns.Add("StateText", typeof(string));
        foreach (DataRow row in table.Rows)
        {
            row["MethodText"] = Convert.ToString(row["Method"]) switch
            {
                "WhatsApp Business (manual)" => "WhatsApp · manual",
                "MetaCloud" => "WhatsApp · Meta API",
                "Twilio" => "SMS · Twilio",
                _ => Convert.ToString(row["Method"])
            };
            string? code = Convert.ToString(row["Audience"]);
            row["AudienceText"] = code switch
            {
                nameof(CommunicationAudience.FeeStudent) => "Student fees",
                nameof(CommunicationAudience.FeeFamily) => "Family fees",
                _ => Enum.TryParse(code, out CommunicationAudience scope)
                    ? AudienceOptions.FirstOrDefault(option => option.Scope == scope)?.Label ?? code : code
            };
            if (unfinished)
            {
                row["StateText"] = Convert.ToString(row["ScheduleState"]) == "Paused"
                    ? "Needs review"
                    : row.Table.Columns.Contains("ScheduledUtc") && !row.IsNull("ScheduledUtc")
                        ? "Scheduled " + Convert.ToDateTime(row["ScheduledUtc"]).ToLocalTime().ToString("dd MMM HH:mm")
                        : "Ready";
                continue;
            }
            row["StateText"] = Convert.ToString(row["Method"]) == "WhatsApp Business (manual)"
                ? $"{row["ManualReported"]} reported sent · {row["ManualOpened"]} opened · {row["ManualSkipped"]} skipped / excluded"
                : $"{row["Accepted"]} accepted · {row["Delivered"]} delivered · {row["Failed"]} failed · {row["Unknown"]} unknown";
        }
        var grid = unfinished ? unfinishedGrid : historyGrid;
        var previous = grid.DataSource as DataTable;
        grid.DataSource = table;
        previous?.Dispose();
        if (!unfinished) return;
        bool attention = table.Columns.Contains("NeedsAttention") &&
            table.AsEnumerable().Any(row => Convert.ToInt64(row["NeedsAttention"]) > 0);
        bool upcoming = table.Columns.Contains("Upcoming") &&
            table.AsEnumerable().Any(row => Convert.ToInt64(row["Upcoming"]) > 0);
        queueView.SelectedItem = attention ? "Needs Attention" : upcoming ? "Upcoming" : "All Active";
        unfinishedButton.Text = table.Rows.Count == 0 ? "Queue" : $"Queue ({table.Rows.Count:N0})";
        AppMetrics.FitButtonWidth(unfinishedButton, minWidth: AppScale.Scale(140));
    }

    private static void FilterSessions(DataGridView grid, string search, bool unfinished, string view)
    {
        if (grid.DataSource is not DataTable table) return;
        string query = search.Trim();
        grid.Tag = query.Length > 0 ? "No messages match your search."
            : unfinished ? "No queued messages in this view." : "No message history yet.";
        var filters = new List<string>();
        if (unfinished && table.Columns.Contains("NeedsAttention"))
        {
            if (view == "Needs Attention") filters.Add("NeedsAttention > 0");
            else if (view == "Upcoming") filters.Add("Upcoming > 0 AND NeedsAttention = 0");
        }
        if (query.Length > 0)
        {
            if (!table.Columns.Contains("SearchMatch")) table.Columns.Add("SearchMatch", typeof(bool));
            string[] columns = { "RecipientText", "AudienceText", "MethodText", "OperatorName", "StateText", "CreatedUtc" };
            foreach (DataRow row in table.Rows)
                row["SearchMatch"] = columns.Where(table.Columns.Contains).Any(name =>
                    Convert.ToString(row[name])?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true);
            filters.Add("SearchMatch = true");
        }
        table.DefaultView.RowFilter = string.Join(" AND ", filters);
        grid.Invalidate();
    }

    private async Task OpenSessionAsync(DataGridViewRow? selected, bool unfinished)
    {
        if (IsBusy || selected?.DataBoundItem is not DataRowView row) return;
        var session = new CommunicationSessionRow((Guid)row["BatchId"],
            Convert.ToString(row["Method"]) ?? "", Convert.ToString(row["StateText"]) ?? "",
            Convert.ToString(row["AudienceText"]) ?? "");
        if (!unfinished)
        {
            if (!CurrentUser.HasPermission("Communication.View")) return;
            CommunicationHistoryDraft? draft;
            if (operations.ViewHistory is not null) draft = operations.ViewHistory(session);
            else
            {
                using var dialog = new CommunicationHistoryDialog(session);
                draft = dialog.ShowDialog(FindForm()) == DialogResult.OK ? dialog.Draft : null;
            }
            if (!IsDisposed && draft is not null)
                await RunBusyAsync("Preparing a new message from History.", () => CopyHistoryDraftAsync(draft), this);
            return;
        }
        if (!CurrentUser.HasPermission("Communication.Send")) return;
        try
        {
            if (session.Method == "WhatsApp Business (manual)") operations.OpenHandoff(session.BatchId, false);
            else
            {
                using var dialog = new CommunicationApiDispatchDialog(session.BatchId);
                dialog.ShowDialog(FindForm());
            }
            if (!IsDisposed) await NavigateSessionsAsync(unfinished: true);
        }
        catch (Exception ex) { if (!IsDisposed) Fail(ex); }
    }

    private async Task CopyHistoryDraftAsync(CommunicationHistoryDraft draft)
    {
        if (!CommunicationAccess.CanCompose(CurrentUser.HasPermission)) return;
        if (message.Text.Length > 0 && !Confirm("Replace your current draft with a new draft from this message?",
                "Replace Draft", false)) return;
        int actor = CurrentUser.UserId;
        var target = draft.Target;
        if ((target.CampusId != 0 && campus.Items.Count == 0) || (target.ClassId != 0 && cls.Items.Count == 0))
        {
            await BindLookupsAsync();
            if (IsDisposed || actor != CurrentUser.UserId) return;
        }
        if (!AvailableIn(campus, target.CampusId) || !AvailableIn(cls, target.ClassId))
        {
            Warn("The original campus or class is unavailable. Choose an audience in New Message.");
            return;
        }
        InvalidatePreview();
        methodChanged = true;
        deliveryMethod.SelectedIndex = draft.Provider switch { "MetaCloud" => 1, "Twilio" => 2, _ => 0 };
        SelectedAudience = target.Audience;
        if (campus.Items.Count > 0) campus.SelectedValue = target.CampusId;
        if (cls.Items.Count > 0) cls.SelectedValue = target.ClassId;
        student.SetStudent(target.StudentId, draft.StudentName);
        staff.SetStaff(target.StaffId, draft.StaffName);
        metaSelection = draft.MetaTemplate;
        message.Text = IsMeta && metaSelection is null ? "" : draft.Body;
        SetStep(0);
    }

    private async Task ExportHistoryAsync(DataGridView grid)
    {
        if (IsBusy || !CurrentUser.HasPermission("Communication.Configure")
            || grid.DataSource is not DataTable history) return;
        using var report = new DataTable("Communication History");
        var fields = new (string Source, string Heading)[]
        {
            ("CreatedUtc", "Created (UTC)"), ("RecipientText", "Recipient"),
            ("AudienceText", "Audience"), ("MethodText", "Method"),
            ("OperatorName", "Started by"), ("StateText", "Outcome")
        };
        foreach (var field in fields)
            report.Columns.Add(field.Heading, field.Source == "CreatedUtc" ? typeof(DateTime) : typeof(string));
        foreach (DataRowView row in history.DefaultView)
        {
            var values = fields.Select(field => row.Row.Table.Columns.Contains(field.Source)
                ? row[field.Source] : DBNull.Value).ToArray();
            report.Rows.Add(values);
        }
        if (report.Rows.Count == 0) { Warn("No filtered history to export."); return; }
        using var save = new SaveFileDialog
        {
            Title = "Export Communication History",
            Filter = "PDF report (*.pdf)|*.pdf|CSV data (*.csv)|*.csv",
            FileName = "FMSC_Communication_History_" + DateTime.Today.ToString("yyyyMMdd"),
            AddExtension = true,
            OverwritePrompt = true,
            RestoreDirectory = true
        };
        if (save.ShowDialog(FindForm()) != DialogResult.OK) return;
        string format = save.FilterIndex == 1 ? "PDF" : "CSV";
        try
        {
            await CommunicationManagementStore.RecordHistoryExportAsync(report.Rows.Count, format);
            if (IsDisposed) return;
            string destination = save.FileName;
            if (format == "PDF")
                await Task.Run(() => PdfTableExporter.Export(destination, "Communication History",
                    "Filtered records · recipient numbers masked", report, AppTheme.Primary));
            else
                await Task.Run(() => CsvFileExporter.Write(report, destination));
            if (!IsDisposed) SetStatus("Filtered communication history exported.");
        }
        catch (Exception ex) { if (!IsDisposed) Fail(ex); }
    }

    private async Task RefreshSendControlAsync()
    {
        if (masterPauseButton is null || !CurrentUser.HasPermission("Communication.Configure")) return;
        using var settings = await CommunicationManagementStore.ReadSendControlAsync();
        if (IsDisposed || settings.Rows.Count != 1) return;
        sendingPaused = Convert.ToBoolean(settings.Rows[0]["Paused"]);
        masterPauseButton.Text = sendingPaused ? "Resume Sending" : "Pause All Sending";
        masterPauseButton.ThemeRole = sendingPaused ? "Primary" : "Secondary";
        masterPauseButton.Enabled = true;
    }

    private async Task ToggleMasterPauseAsync()
    {
        if (IsBusy || masterPauseButton is null || !CurrentUser.HasPermission("Communication.Configure")) return;
        bool nextPaused = !sendingPaused;
        string question = nextPaused
            ? "Pause new SMS and WhatsApp API submissions across this school? Existing in-flight requests cannot be recalled."
            : "Resume API sending for this school? Pending messages still require their normal reviews and authorization.";
        if (!Confirm(question, nextPaused ? "Pause Sending" : "Resume Sending", false)) return;
        try
        {
            masterPauseButton.Enabled = false;
            await CommunicationManagementStore.SetSendPausedAsync(nextPaused);
            await RefreshSendControlAsync();
        }
        catch (Exception ex) { if (!IsDisposed) Fail(ex); }
        finally
        {
            if (!IsDisposed && masterPauseButton is not null) masterPauseButton.Enabled = true;
        }
    }

    private static bool AvailableIn(LookupBox lookup, int id) => id == 0
        || lookup.Items.OfType<DataRowView>().Any(row => Convert.ToInt32(row[lookup.ValueMember]) == id);
}
