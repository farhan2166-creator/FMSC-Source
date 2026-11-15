using System.Data;
using FMSC_SMS.Core;

namespace FMSC_SMS.Pages.Communication;

internal sealed class CommunicationHistoryDialog : ApplicationWindowForm
{
    private readonly DataGridView recipients = HumanUi.Grid(false);
    private readonly ModernTextBox preview = HumanUi.TextBox(true, 1600);
    private readonly ModernButton sendAgain = new()
    {
        Name = "communicationSendAgain", Text = "Send Again…", ThemeRole = "Secondary",
        Enabled = false, Visible = false
    };

    internal CommunicationHistoryDraft? Draft { get; private set; }

    internal CommunicationHistoryDialog(CommunicationSessionRow session, Func<Guid, Task<DataTable>>? read = null)
    {
        Text = "Message History";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(AppScale.Scale(720), AppScale.Scale(470));
        MinimumSize = new Size(AppScale.Scale(530), AppScale.Scale(380));
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
            ("RecipientName", "Recipient"), ("RecordNumber", "Admission / staff no."), ("LastFour", "Number ending"), ("Outcome", "Status")
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
        var buttons = HumanUi.DialogButtons(sendAgain, close);
        buttons.Dock = DockStyle.Fill;
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(AppScale.Scale(16))
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(32)));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 52));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.ButtonHeight + AppScale.Scale(10)));
        layout.Controls.Add(details, 0, 0);
        layout.Controls.Add(recipients, 0, 1);
        layout.Controls.Add(message, 0, 2);
        layout.Controls.Add(buttons, 0, 3);
        Controls.Add(layout);
        recipients.CurrentCellChanged += (_, _) => ShowMessage();
        Shown += async (_, _) =>
        {
            try
            {
                int actor = CurrentUser.UserId;
                var rows = await (read ?? CommunicationHistoryStore.ReadAsync)(session.BatchId);
                if (IsDisposed || actor != CurrentUser.UserId)
                {
                    rows.Dispose();
                    return;
                }
                recipients.DataSource = rows;
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
        FormClosed += (_, _) => (recipients.DataSource as DataTable)?.Dispose();
    }

    private void ShowMessage()
    {
        DataRow? row = (recipients.CurrentRow?.DataBoundItem as DataRowView)?.Row
            ?? (recipients.DataSource as DataTable)?.AsEnumerable().FirstOrDefault();
        preview.Text = row is null ? "" : row["RenderedBody"] as string ?? (string)row["Body"];
    }
}
