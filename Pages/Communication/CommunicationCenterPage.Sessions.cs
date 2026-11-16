using System.Data;
using FMSC_SMS.Core;

namespace FMSC_SMS.Pages.Communication;

public sealed partial class CommunicationCenterPage
{
    private readonly ModernComboBox queueView = HumanUi.Combo();
    private readonly ModernButton eventSuggestionsButton =
        PageButton("communicationEventSuggestions","Event Suggestions",168);

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
            ("InProgress", "Needs attention", 115, 90)
        })
        {
            if (unfinished && field is ("OperatorName" or "StateText")) continue;
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = field, DataPropertyName = field, HeaderText = caption,
                MinimumWidth = AppScale.Scale(width), FillWeight = weight, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                SortMode = DataGridViewColumnSortMode.NotSortable
            });
        }
        grid.Columns["CreatedUtc"]!.DefaultCellStyle.Format = "dd MMM yyyy  HH:mm";
        if (unfinished)
        {
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "QueueStatus", DataPropertyName = "QueueStatus",
                HeaderText = "Queue status", MinimumWidth = AppScale.Scale(145),
                FillWeight = 110, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
            });
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "DueLocal", DataPropertyName = "DueLocal",
                HeaderText = "Scheduled for", MinimumWidth = AppScale.Scale(135),
                FillWeight = 95, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
            });
            grid.Columns["DueLocal"]!.DefaultCellStyle.Format = "dd MMM yyyy HH:mm";
        }
        var search = HumanUi.TextBox(false, 100);
        search.Name = unfinished ? "communicationUnfinishedSearch" : "communicationHistorySearch";
        search.AccessibleName = search.PlaceholderText = unfinished ? "Search queued batches" : "Search history";
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
        search.TextChanged += (_, _) => FilterSessions(grid, search.Text, unfinished);
        grid.DataSourceChanged += (_, _) => FilterSessions(grid, search.Text, unfinished);
        var toolbar = new TableLayoutPanel
        {
            Dock = DockStyle.Top, Height = AppMetrics.ButtonHeight + AppScale.Scale(10),
            ColumnCount = unfinished ? 4 : 2, RowCount = 1, Margin = Padding.Empty
        };
        if (unfinished)
        {
            queueView.Name = "communicationQueueView";
            queueView.Items.AddRange(new object[] { "Needs Attention", "Upcoming", "All Active" });
            queueView.SelectedIndex = 2;
            queueView.Dock = DockStyle.Fill;
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, AppScale.Scale(180)));
            toolbar.Controls.Add(queueView, 0, 0);
            queueView.SelectedIndexChanged += (_, _) => FilterSessions(grid, search.Text, true);
        }
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, AppScale.Scale(166)));
        toolbar.Controls.Add(search, unfinished ? 1 : 0, 0);
        toolbar.Controls.Add(open, unfinished ? 2 : 1, 0);
        if(unfinished)
        {
            eventSuggestionsButton.Visible=CurrentUser.HasPermission("Communication.Configure")
                && CurrentUser.HasPermission("Fees.View");
            eventSuggestionsButton.Dock=DockStyle.Fill;
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,AppScale.Scale(190)));
            toolbar.Controls.Add(eventSuggestionsButton,3,0);
            eventSuggestionsButton.Click+=(_,_)=>OpenAutomationInbox();
        }
        card.Content.Controls.Add(grid);
        card.Content.Controls.Add(toolbar);
        return card;
    }

    private Task NavigateSessionsAsync(bool unfinished) =>
        RunBusyAsync(unfinished ? "Reading queued batches." : "Reading communication history.",
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
            SetStatus("");
        }
        catch (Exception ex) { if (!IsDisposed) Fail(ex); }
    }

    private void BindSessions(DataTable table, bool unfinished)
    {
        table.Columns.Add("MethodText", typeof(string));
        table.Columns.Add("AudienceText", typeof(string));
        table.Columns.Add("StateText", typeof(string));
        if (unfinished)
        {
            if (!table.Columns.Contains("QueueStatus"))
                table.Columns.Add(new DataColumn("QueueStatus", typeof(string))
                    { DefaultValue = "All Active" });
            table.Columns.Add("DueLocal", typeof(DateTime));
        }
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
                if (table.Columns.Contains("DueUtc") && row["DueUtc"] != DBNull.Value)
                    row["DueLocal"] = DateTime.SpecifyKind(
                        Convert.ToDateTime(row["DueUtc"]), DateTimeKind.Utc).ToLocalTime();
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
        var statuses = table.AsEnumerable().Select(row => row.Field<string>("QueueStatus")).ToArray();
        queueView.SelectedIndex = statuses.Contains("Needs Attention", StringComparer.Ordinal)
            ? 0 : statuses.Contains("Upcoming", StringComparer.Ordinal) ? 1 : 2;
        unfinishedButton.Text = table.Rows.Count == 0 ? "Queue" : $"Queue ({table.Rows.Count:N0})";
        AppMetrics.FitButtonWidth(unfinishedButton, minWidth: AppScale.Scale(140));
    }

    private void FilterSessions(DataGridView grid, string search, bool unfinished)
    {
        if (grid.DataSource is not DataTable table) return;
        string query = search.Trim();
        grid.Tag = table.Rows.Count > 0 && query.Length > 0 ? "No sessions match your search."
            : unfinished ? "No unfinished batches require attention." : "No message sessions yet.";
        string statusClause = unfinished && queueView.SelectedIndex is 0 or 1
            ? "QueueStatus = '" + (queueView.SelectedIndex == 0 ? "Needs Attention" : "Upcoming") + "'"
            : "";
        if (query.Length == 0) table.DefaultView.RowFilter = statusClause;
        else
        {
            if (!table.Columns.Contains("SearchMatch")) table.Columns.Add("SearchMatch", typeof(bool));
            string[] columns = { "RecipientText", "AudienceText", "MethodText", "OperatorName",
                "StateText", "CreatedUtc", "QueueStatus", "DueLocal" };
            foreach (DataRow row in table.Rows)
                row["SearchMatch"] = columns.Where(table.Columns.Contains).Any(name =>
                    Convert.ToString(row[name])?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true);
            table.DefaultView.RowFilter = statusClause.Length == 0 ? "SearchMatch = true"
                : "SearchMatch = true AND " + statusClause;
        }
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

    private static bool AvailableIn(LookupBox lookup, int id) => id == 0
        || lookup.Items.OfType<DataRowView>().Any(row => Convert.ToInt32(row[lookup.ValueMember]) == id);
}
