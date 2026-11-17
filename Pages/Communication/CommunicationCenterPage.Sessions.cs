using System.Data;
using System.Globalization;
using System.Text;
using FMSC_SMS.Core;

namespace FMSC_SMS.Pages.Communication;

public sealed partial class CommunicationCenterPage
{
    internal static string QueueAttentionCaption(int attention, int total)
    {
        if (attention < 0 || total < 0 || attention > total)
            throw new ArgumentOutOfRangeException(nameof(attention));
        return attention > 0 ? $"Queue · {attention:N0} attention"
            : total > 0 ? $"Queue ({total:N0})" : "Queue";
    }

    private readonly CommunicationQueueAlertPolicy queueAlertPolicy = new();
    private NotifyIcon? queueAlertIcon;

    private void ShowQueueAttentionNotification(int attention)
    {
        if(!queueAlertPolicy.ShouldAlert(attention,DateTime.UtcNow) || IsDisposed) return;
        try
        {
            if(queueAlertIcon is null)
            {
                queueAlertIcon=new NotifyIcon
                {
                    Icon=System.Drawing.SystemIcons.Information,
                    Text="FMSC Communication Queue",Visible=true
                };
                queueAlertIcon.BalloonTipClicked+=(_,_)=>
                {
                    if(IsDisposed || !IsHandleCreated)return;
                    BeginInvoke((Action)(() => { if(!IsDisposed) _=NavigateSessionsAsync(true); }));
                };
            }
            // Never expose student, guardian, payment, or message content on
            // the Windows desktop or lock screen.
            queueAlertIcon.BalloonTipTitle="Communication Queue needs attention";
            queueAlertIcon.BalloonTipText=$"{attention:N0} communication item(s) need review. Open FMSC Queue for details.";
            queueAlertIcon.ShowBalloonTip(8000);
        }
        catch(Exception ex)
        {
            // Desktop notifications are optional; Queue remains available.
            AppExceptionHandler.LogNonFatal(ex,"Communication desktop notification unavailable");
        }
    }

    private async Task RefreshQueueAttentionAsync()
    {
        if (!CurrentUser.HasPermission("Communication.Send")) return;
        int actor=CurrentUser.UserId;
        try
        {
            using var rows=await (operations.ReadSessions is { } fixture
                ? fixture(true) : CommunicationManagementStore.ReadMyUnfinishedBatchesAsync());
            if (IsDisposed || actor!=CurrentUser.UserId) return;
            int attention=rows.Columns.Contains("QueueStatus")
                ? rows.AsEnumerable().Count(row => string.Equals(
                    Convert.ToString(row["QueueStatus"]), "Needs Attention", StringComparison.Ordinal))
                : 0;
            unfinishedButton.Text=QueueAttentionCaption(attention,rows.Rows.Count);
            AppMetrics.FitButtonWidth(unfinishedButton,minWidth:AppScale.Scale(140));
            ShowQueueAttentionNotification(attention);
        }
        catch(Exception ex)
        {
            // Navigation is always usable even when the optional count fails.
            AppExceptionHandler.LogNonFatal(ex,"Communication Queue summary could not load");
        }
    }

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
                HeaderText = "Scheduled (school time)", MinimumWidth = AppScale.Scale(135),
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
        bool allowExport = !unfinished && CurrentUser.HasPermission("Communication.View")
            && CurrentUser.HasPermission("Communication.Configure");
        var toolbar = new TableLayoutPanel
        {
            Dock = DockStyle.Top, Height = AppMetrics.ButtonHeight + AppScale.Scale(10),
            ColumnCount = unfinished ? 4 : allowExport ? 3 : 2, RowCount = 1, Margin = Padding.Empty
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
        if (allowExport)
        {
            var export = PageButton("communicationExportHistory", "Export…", 140, "Secondary");
            export.Dock = DockStyle.Fill;
            export.Click += async (_, _) => await ExportHistoryAsync();
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, AppScale.Scale(150)));
            toolbar.Controls.Add(export, 2, 0);
        }
        if(unfinished)
        {
            eventSuggestionsButton.Visible=CurrentUser.HasPermission("Communication.Configure")
                && CurrentUser.HasPermission("Fees.View");
            eventSuggestionsButton.Dock=DockStyle.Fill;
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,AppScale.Scale(190)));
            toolbar.Controls.Add(eventSuggestionsButton,3,0);
            var recurring=PageButton("communicationRecurringSchedules","Recurring Schedules…",185);
            recurring.Click+=async(_,_)=>{
                using var schedules=new CommunicationRecurringSchedulesDialog();
                if(schedules.ShowDialog(FindForm())==DialogResult.OK && schedules.Draft is { } draft)
                    await CopyHistoryDraftAsync(draft);
            };
            toolbar.RowCount=2;toolbar.Height=2*(AppMetrics.ButtonHeight+AppScale.Scale(10));
            toolbar.RowStyles.Add(new RowStyle(SizeType.Percent,50));toolbar.RowStyles.Add(new RowStyle(SizeType.Percent,50));
            toolbar.Controls.Add(recurring,0,1);toolbar.SetColumnSpan(recurring,2);

            eventSuggestionsButton.Click+=(_,_)=>OpenAutomationInbox();
        }
        card.Content.Controls.Add(grid);
        card.Content.Controls.Add(toolbar);
        return card;
    }

    /// <summary>
    /// Exports only the currently filtered, permission-scoped summary rows.
    /// Never exports saved full telephone numbers, message bodies or credentials.
    /// </summary>
    private async Task ExportHistoryAsync()
    {
        if (IsBusy || !CurrentUser.HasPermission("Communication.View")
            || !CurrentUser.HasPermission("Communication.Configure")
            || historyGrid.DataSource is not DataTable table) return;
        int actor = CurrentUser.UserId;
        using DataTable exportRows = HistoryExportRows(table.DefaultView);
        string csv = FormatHistoryCsv(table.DefaultView);
        if (exportRows.Rows.Count == 0) { Warn("No matching history entries to export."); return; }
        if (!Confirm($"Export {exportRows.Rows.Count:N0} displayed history entries as a PDF or CSV? "
            + "This contains school record names. Save it in a protected location.",
            "Export Communication History", false)) return;
        using var save = new SaveFileDialog
        {
            Title = "Export communication history",
            Filter = "PDF document (*.pdf)|*.pdf|CSV UTF-8 (*.csv)|*.csv",
            FilterIndex = 1,
            DefaultExt = "pdf",
            AddExtension = true,
            FileName = $"FMSC-Communication-History-{DateTime.Now:yyyyMMdd}.pdf"
        };
        if (save.ShowDialog(FindForm()) != DialogResult.OK || actor != CurrentUser.UserId
            || !CurrentUser.HasPermission("Communication.Configure")
            || !CurrentUser.HasPermission("Communication.View")) return;
        try
        {
            if (save.FilterIndex == 1)
            {
                PdfTableExporter.Export(save.FileName, "FMSC Communication History",
                    "Filtered communication history; delivery and manual reports are distinguished",
                    exportRows, Accent);
            }
            else
            {
                await File.WriteAllTextAsync(save.FileName,
                    csv, new UTF8Encoding(true));
            }
            if (!IsDisposed) SetStatus("Displayed communication history exported.");
        }
        catch (Exception ex) { if (!IsDisposed) Fail(ex); }
    }

    /// <summary>
    /// A strict summary-only projection for printing. No recipient search keys,
    /// contact numbers, message bodies, provider credentials or audit secrets.
    /// </summary>
    private static DataTable HistoryExportRows(DataView rows)
    {
        var export = new DataTable("History");
        foreach (var name in new[] { "Created UTC", "Method", "Recipient",
            "Audience", "Started by", "Outcome" })
            export.Columns.Add(name, typeof(string));
        foreach (DataRowView row in rows)
        {
            string created = row["CreatedUtc"] is DateTime date
                ? date.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC"
                : Convert.ToString(row["CreatedUtc"], CultureInfo.InvariantCulture) ?? "";
            export.Rows.Add(created,
                Convert.ToString(row["MethodText"]) ?? "",
                Convert.ToString(row["RecipientText"]) ?? "",
                Convert.ToString(row["AudienceText"]) ?? "",
                Convert.ToString(row["OperatorName"]) ?? "",
                Convert.ToString(row["StateText"]) ?? "");
        }
        return export;
    }

    // Quoted cells, Unicode output and leading-formula protection are mandatory:
    // school record names come from users and must never execute in a spreadsheet.
    private static string FormatHistoryCsv(DataView rows)
    {
        static string Cell(string value)
        {
            value = value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
            string trimmed = value.TrimStart();
            if (trimmed.Length > 0 && "=+-@".Contains(trimmed[0]))
                value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        var csv = new StringBuilder();
        csv.AppendLine("Created UTC,Method,Recipient,Audience,Started by,Outcome");
        foreach (DataRowView row in rows)
        {
            string created = row["CreatedUtc"] is DateTime date
                ? date.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC"
                : Convert.ToString(row["CreatedUtc"], CultureInfo.InvariantCulture) ?? "";
            csv.AppendLine(string.Join(",", new[]
            {
                created, Convert.ToString(row["MethodText"]) ?? "",
                Convert.ToString(row["RecipientText"]) ?? "",
                Convert.ToString(row["AudienceText"]) ?? "",
                Convert.ToString(row["OperatorName"]) ?? "",
                Convert.ToString(row["StateText"]) ?? ""
            }.Select(Cell)));
        }
        return csv.ToString();
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
                    row["DueLocal"] = table.Columns.Contains("DueSchool")
                        ? row["DueSchool"] : row["DueUtc"];
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
        unfinishedButton.Text = QueueAttentionCaption(
            statuses.Count(status => status == "Needs Attention"), table.Rows.Count);
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
            string[] columns = { "RecipientText", "RecipientSearchText", "AudienceText", "MethodText",
                "OperatorName", "StateText", "CreatedUtc", "QueueStatus", "DueLocal" };
            string[] terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            foreach (DataRow row in table.Rows)
                row["SearchMatch"] = terms.All(term => columns.Where(table.Columns.Contains).Any(name =>
                    Convert.ToString(row[name])?.Contains(term, StringComparison.CurrentCultureIgnoreCase) == true));
            table.DefaultView.RowFilter = statusClause.Length == 0 ? "SearchMatch = true"
                : "SearchMatch = true AND " + statusClause;
        }
        grid.Invalidate();
    }

    private async Task OpenSessionAsync(DataGridViewRow? selected, bool unfinished)
    {
        if (IsBusy || selected?.DataBoundItem is not DataRowView row) return;
        if(unfinished && row.Row.Table.Columns.Contains("SourceRowKind") && Convert.ToString(row["SourceRowKind"])=="Event")
        {
            int actor=CurrentUser.UserId;
            var suggestion=await CommunicationAutomationInboxStore.OfferReviewAsync(Convert.ToInt64(row["PlanCandidateId"]));
            if(actor==CurrentUser.UserId && !IsDisposed && suggestion is not null)UseEventSuggestion(suggestion);
            return;
        }
        if(unfinished && row.Row.Table.Columns.Contains("SourceRowKind") && Convert.ToString(row["SourceRowKind"])=="Recurrence")
        {
            using var schedules=new CommunicationRecurringSchedulesDialog((Guid)row["PlanRuleId"]);
            if(schedules.ShowDialog(FindForm())==DialogResult.OK && schedules.Draft is { } draft)
                await CopyHistoryDraftAsync(draft);
            await ShowSessionsAsync(true);
            return;
        }
        var session = new CommunicationSessionRow((Guid)row["BatchId"],
            Convert.ToString(row["Method"]) ?? "", Convert.ToString(row["StateText"]) ?? "",
            Convert.ToString(row["AudienceText"]) ?? "");
        if (!unfinished)
        {
            if (!CurrentUser.HasPermission("Communication.View")) return;
            CommunicationHistoryDraft? draft;
            string? correctionReason=null;
            if (operations.ViewHistory is not null) draft = operations.ViewHistory(session);
            else
            {
                using var dialog = new CommunicationHistoryDialog(session);
                draft = dialog.ShowDialog(FindForm()) == DialogResult.OK ? dialog.Draft : null;
                correctionReason=dialog.CorrectionReason;
            }
            if (!IsDisposed && draft is not null)
                await RunBusyAsync("Preparing a new message from History.",
                    () => CopyHistoryDraftAsync(draft,
                        correctionReason is null ? null : session.BatchId,correctionReason), this);
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

    private Guid? pendingCorrectionOriginal;
    private string? pendingCorrectionReason;

    private async Task LinkPendingCorrectionAsync(Guid newBatchId)
    {
        if (pendingCorrectionOriginal is not Guid original) return;
        string reason=pendingCorrectionReason ??
            throw new CommunicationValidationException("Correction reason is missing. Restart from History.");
        await CommunicationManagementStore.LinkCorrectionBatchAsync(original,newBatchId,reason);
    }

    private async Task CopyHistoryDraftAsync(CommunicationHistoryDraft draft,
        Guid? correctionSource=null,string? correctionReason=null)
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
        pendingCorrectionOriginal=correctionSource;
        pendingCorrectionReason=correctionReason;
        SetStep(0);
        if(correctionSource is not null)
            SetStatus("Correction draft: edit the message, review current recipients, and confirm. Original History is preserved.");
    }

    private static bool AvailableIn(LookupBox lookup, int id) => id == 0
        || lookup.Items.OfType<DataRowView>().Any(row => Convert.ToInt32(row[lookup.ValueMember]) == id);
}
