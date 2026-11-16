using System.Data;
using FMSC_SMS.Core;

namespace FMSC_SMS.Pages.Communication;

internal sealed class CommunicationHistoryDialog : ApplicationWindowForm
{
    private readonly DataGridView recipients = HumanUi.Grid(false);
    private readonly ModernTextBox preview = HumanUi.TextBox(true, 1600);
    private readonly Label evidence = new()
    {
        Name = "communicationHistoryEvidence",
        Dock = DockStyle.Fill, AutoEllipsis = false,
        ForeColor = AppTheme.MutedText
    };
    private readonly ModernButton checkDelivery = new()
    {
        Name = "communicationCheckProviderDelivery",
        Text = "Check Delivery Status", ThemeRole = "Secondary",
        Enabled = false, Visible = false
    };
    private readonly Func<Guid,Task<DataTable>> readHistory;
    private readonly Func<long,Task<string>> pollAttempt;
    private readonly CommunicationSessionRow session;
    private bool checkingDelivery;
    private readonly ModernButton sendAgain = new()
    {
        Name = "communicationSendAgain", Text = "Send Again…", ThemeRole = "Secondary",
        Enabled = false, Visible = false
    };

    internal CommunicationHistoryDraft? Draft { get; private set; }

    internal CommunicationHistoryDialog(CommunicationSessionRow session,
        Func<Guid, Task<DataTable>>? read = null, Func<long,Task<string>>? poll = null)
    {
        this.session = session;
        readHistory = read ?? CommunicationHistoryStore.ReadAsync;
        pollAttempt = poll ?? (attemptId =>
            CommunicationDispatchStore.ReconcileKnownAttemptAsync(attemptId));
        Text = "Message History";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(AppScale.Scale(760), AppScale.Scale(540));
        MinimumSize = new Size(AppScale.Scale(570), AppScale.Scale(445));
        BackColor = AppTheme.PageBackground;

        var details = new Label
        {
            Dock = DockStyle.Fill, AutoEllipsis = true,
            ForeColor = AppTheme.Text, Font = AppTheme.NormalFont,
            Text = $"{session.Audience}  ·  {session.Outcome}"
        };
        recipients.AutoGenerateColumns = false;
        foreach (var (field, heading) in new[]
        {
            ("RecipientName", "Recipient"), ("RecordNumber", "Admission / staff no."),
            ("LastFour", "Number ending"), ("Outcome", "Status")
        })
            recipients.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = field, DataPropertyName = field, HeaderText = heading,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
            });
        recipients.Columns["RecipientName"]!.FillWeight = 170;
        recipients.Columns["Outcome"]!.FillWeight = 120;
        preview.ReadOnly = true;
        preview.ScrollBars = ScrollBars.Vertical;
        var message = HumanUi.Field("Saved message", preview);
        var close = new ModernButton { Text = "Close", DialogResult = DialogResult.Cancel };
        CancelButton = close;
        sendAgain.Click += (_, _) => { if (Draft is not null) DialogResult = DialogResult.OK; };
        checkDelivery.Click += async (_, _) => await CheckDeliveryAsync();
        var buttons = HumanUi.DialogButtons(checkDelivery, sendAgain, close);
        buttons.Dock = DockStyle.Fill;
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(AppScale.Scale(16))
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(48)));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(68)));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.ButtonHeight + AppScale.Scale(10)));
        layout.Controls.Add(details, 0, 0);
        layout.Controls.Add(recipients, 0, 1);
        layout.Controls.Add(evidence, 0, 2);
        layout.Controls.Add(message, 0, 3);
        layout.Controls.Add(buttons, 0, 4);
        Controls.Add(layout);
        recipients.CurrentCellChanged += (_, _) => ShowMessage();
        Shown += async (_, _) =>
        {
            try
            {
                int actor = CurrentUser.UserId;
                var rows = await readHistory(session.BatchId);
                if (IsDisposed || actor != CurrentUser.UserId)
                {
                    rows.Dispose();
                    return;
                }
                recipients.DataSource = rows;
                DataRow first = rows.Rows[0];
                string prepared = DisplayValue(first, "PreparedByName");
                string approved = DisplayValue(first, "ApprovedByName");
                details.Text = $"{session.Audience} · Prepared by {prepared} ({LocalWhen(first, "PreparedUtc")})"
                    + (approved.Length == 0 ? "" : $" · Approved by {approved} ({LocalWhen(first, "ApprovedUtc")})");
                string takeover = DisplayValue(first, "TakeoverApprovedBy");
                if (takeover.Length > 0)
                    details.Text += $" · Reassigned by {takeover} ({LocalWhen(first, "TakeoverUtc")})";
                if (rows.Rows[0]["ProviderCode"] is "WhatsAppBusinessManual")
                    message.Controls.OfType<Label>().Single().Text = "Saved message format";
                ShowMessage();
                Draft = CommunicationHistoryStore.CreateDraft(rows);
                sendAgain.Visible = sendAgain.Enabled = Draft is not null;
            }
            catch (Exception ex)
            {
                if (!IsDisposed) details.Text = DbMessage.UserMessage(ex, "Could not load the saved message.");
            }
        };
        FormClosing += (_, e) => e.Cancel |= checkingDelivery;
        FormClosed += (_, _) => (recipients.DataSource as DataTable)?.Dispose();
    }

    internal static string OutcomeExplanation(string? provider, string? state)
    {
        if (provider == "WhatsAppBusinessManual")
            return state switch
            {
                "ReportedSent" => "Manually reported sent — delivery is not provider-verified",
                "Opened" => "WhatsApp opened — sending is not confirmed",
                "Skipped" => "Skipped by operator — no delivery claim",
                "Excluded" => "Excluded — no message sent",
                _ => "Waiting for operator handoff"
            };
        return state switch
        {
            "Delivered" => "Provider-confirmed delivered",
            "Accepted" => "Accepted by provider — delivery not yet confirmed",
            "Submitted" => "Submitted to provider — waiting for confirmation",
            "Unknown" => "Outcome unknown — do not resend automatically",
            "Failed" => "Provider reported failure — review before retry",
            "Excluded" => "Excluded — no message sent",
            "Pending" => "Waiting for a reviewed provider attempt",
            _ => "Outcome requires review"
        };
    }

    private static string DisplayValue(DataRow row, string name)
    {
        return row.Table.Columns.Contains(name) && row[name] != DBNull.Value
            ? Convert.ToString(row[name])?.Trim() ?? "" : "";
    }

    private static string LocalWhen(DataRow row, string field)
    {
        if (!row.Table.Columns.Contains(field) || row[field] is not DateTime date)
            return "time not recorded";
        return DateTime.SpecifyKind(date, DateTimeKind.Utc)
            .ToLocalTime().ToString("dd MMM yyyy HH:mm");
    }

    internal static bool CanCheckProviderDelivery(string? provider, string? state, long? attemptId) =>
        provider == "Twilio" && state is ("Accepted" or "Submitted" or "Unknown")
        && attemptId is > 0;

    /// <summary>Runs an authenticated provider-status GET only after a real
    /// operator click. Never sends a new message or retries an unknown attempt.
    /// </summary>
    private async Task CheckDeliveryAsync()
    {
        if (checkingDelivery || !CurrentUser.HasPermission("Communication.View")
            || !CurrentUser.HasPermission("Communication.Send")) return;
        DataRow? selected = (recipients.CurrentRow?.DataBoundItem as DataRowView)?.Row;
        if (selected is null) return;
        long? attempt = selected.Table.Columns.Contains("AttemptId")
            && selected["AttemptId"] != DBNull.Value ? Convert.ToInt64(selected["AttemptId"]) : null;
        if (DisplayValue(selected,"ProviderMessageId").Length==0 ||
            !CanCheckProviderDelivery(DisplayValue(selected,"ProviderCode"),
                DisplayValue(selected,"Outcome"),attempt)) return;
        int actor = CurrentUser.UserId;
        int rowIndex = recipients.CurrentRow?.Index ?? 0;
        checkingDelivery = true;
        recipients.Enabled = false;
        checkDelivery.Enabled = sendAgain.Enabled = false;
        try
        {
            string status = await pollAttempt(attempt!.Value);
            if (IsDisposed || actor != CurrentUser.UserId) return;
            if (status == "Unknown")
            {
                evidence.Text = "The provider has not confirmed a new outcome. "
                    + "This message will not be resent automatically.";
                return;
            }
            DataTable rows = await readHistory(session.BatchId);
            if (IsDisposed || actor != CurrentUser.UserId)
            {
                rows.Dispose();
                return;
            }
            if (rows.Rows.Count == 0) { rows.Dispose(); throw new InvalidDataException("History is no longer available."); }
            var previous = recipients.DataSource as DataTable;
            recipients.DataSource = rows;
            previous?.Dispose();
            if (rowIndex < recipients.Rows.Count)
                recipients.CurrentCell = recipients.Rows[rowIndex].Cells[0];
            Draft = CommunicationHistoryStore.CreateDraft(rows);
            ShowMessage();
        }
        catch (Exception ex)
        {
            if (!IsDisposed) evidence.Text = DbMessage.UserMessage(ex,
                "The provider status could not be checked. No message was sent.");
        }
        finally
        {
            checkingDelivery = false;
            if (!IsDisposed)
            {
                recipients.Enabled = true;
                sendAgain.Visible = sendAgain.Enabled = Draft is not null;
                UpdateCheckDeliveryButton();
            }
        }
    }

    private void UpdateCheckDeliveryButton()
    {
        DataRow? selected = (recipients.CurrentRow?.DataBoundItem as DataRowView)?.Row;
        long? attempt = selected is not null && selected.Table.Columns.Contains("AttemptId")
            && selected["AttemptId"] != DBNull.Value ? Convert.ToInt64(selected["AttemptId"]) : null;
        bool allowed = selected is not null &&
            DisplayValue(selected,"ProviderMessageId").Length>0 &&
            CurrentUser.HasPermission("Communication.Send") &&
            CurrentUser.HasPermission("Communication.View") &&
            CanCheckProviderDelivery(DisplayValue(selected,"ProviderCode"),
                DisplayValue(selected,"Outcome"),attempt);
        checkDelivery.Visible = allowed;
        checkDelivery.Enabled = allowed && !checkingDelivery;
    }

    private void ShowMessage()
    {
        DataRow? row = (recipients.CurrentRow?.DataBoundItem as DataRowView)?.Row
            ?? (recipients.DataSource as DataTable)?.AsEnumerable().FirstOrDefault();
        if (row is null) { preview.Text = evidence.Text = ""; UpdateCheckDeliveryButton(); return; }
        UpdateCheckDeliveryButton();
        preview.Text = row["RenderedBody"] as string ?? (string)row["Body"];
        string provider = DisplayValue(row, "ProviderCode");
        string state = DisplayValue(row, "Outcome");
        string narrative = OutcomeExplanation(provider, state);
        if (provider == "WhatsAppBusinessManual")
        {
            string reporter = DisplayValue(row, "ManualReportedByName");
            if (state == "ReportedSent")
                narrative += $" · Reported {LocalWhen(row, "ReportedUtc")}"
                    + (reporter.Length == 0 ? "" : " by " + reporter);
            else if (state == "Opened")
                narrative += $" · Opened {LocalWhen(row, "LastOpenedUtc")}";
        }
        else
        {
            string id = DisplayValue(row, "ProviderMessageId");
            string failure = DisplayValue(row, "FailureCode");
            if (id.Length > 0)
                narrative += " · Provider reference ending " + id.Substring(Math.Max(0, id.Length - 12));
            if (failure.Length > 0) narrative += " · Failure code " + failure;
            if (row.Table.Columns.Contains("RequestUtc") && row["RequestUtc"] != DBNull.Value)
                narrative += $" · Sent {LocalWhen(row, "RequestUtc")}";
            if (row.Table.Columns.Contains("ResponseUtc") && row["ResponseUtc"] != DBNull.Value)
                narrative += $" · Updated {LocalWhen(row, "ResponseUtc")}";
        }
        string takeoverReason = DisplayValue(row, "TakeoverReason");
        if (takeoverReason.Length > 0)
            narrative += " · Administrator takeover reason: " + takeoverReason;
        evidence.Text = narrative;
    }
}
