using System.Data;
using FMSC_SMS.Core;

namespace FMSC_SMS.Pages.Communication;

/// <summary>
/// Explicitly audited handoff of held, already reviewed API work. No new
/// confirmation or provider attempt is created by this dialog.
/// </summary>
internal sealed class CommunicationTakeoverDialog : ApplicationWindowForm
{
    private readonly DataGridView batches = HumanUi.Grid(false);
    private readonly ModernTextBox reason = HumanUi.TextBox(false, 200);
    private readonly ModernButton approve = new()
    {
        Name = "communicationApproveTakeover",
        Text = "Approve & Resume", ThemeRole = "Primary", Enabled = false
    };
    private readonly Label feedback = new()
    {
        Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = AppTheme.MutedText
    };
    private readonly Func<Task<DataTable>> read;
    private readonly Func<Guid,string,Task> apply;
    private bool busy;
    internal bool Changed { get; private set; }

    internal CommunicationTakeoverDialog(
        Func<Task<DataTable>>? read = null, Func<Guid,string,Task>? apply = null)
    {
        if (!CurrentUser.HasPermission("Communication.Configure")
            || !CurrentUser.HasPermission("Communication.Send")
            || !CurrentUser.HasPermission("Communication.View")
            || !CurrentUser.HasPermission("Students.View")
            || !CurrentUser.HasPermission("Staff.View")
            || !CurrentUser.HasPermission("Fees.View"))
            throw new UnauthorizedAccessException("Only an authorized school communication administrator may take over held work.");
        this.read = read ?? CommunicationQueuePolicyStore.ReadTakeoverCandidatesAsync;
        this.apply = apply ?? CommunicationQueuePolicyStore.TakeOverScheduledBatchAsync;
        Text = "Review Held Messages";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(AppScale.Scale(800), AppScale.Scale(490));
        MinimumSize = new Size(AppScale.Scale(650), AppScale.Scale(410));
        BackColor = AppTheme.PageBackground;
        batches.Name = "communicationTakeoverQueue";
        batches.AutoGenerateColumns = false;
        foreach (var (key, title, weight) in new[]
        {
            ("CreatedUtc","Created (UTC)",95),("Channel","Method",65),
            ("Audience","Audience",100),("CurrentOwner","Responsible staff",105),
            ("PendingRecipients","Unsent",55)
        })
            batches.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = key, HeaderText = title, DataPropertyName = key,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = weight
            });
        batches.Columns["CreatedUtc"]!.DefaultCellStyle.Format = "dd MMM yyyy HH:mm";
        batches.CurrentCellChanged += (_, _) => UpdateApprove();
        reason.Name = "communicationTakeoverReason";
        reason.TextChanged += (_, _) => UpdateApprove();
        reason.PlaceholderText = "Why is administrator takeover necessary?";
        var note = new Label
        {
            Dock = DockStyle.Fill, ForeColor = AppTheme.MutedText,
            Text = "Only held, previously approved API batches appear here. Taking over preserves the original sender and review, and rechecks provider, permissions and recipients before sending. It never retries an uncertain attempt."
        };
        var close = new ModernButton { Text = "Close", DialogResult = DialogResult.Cancel };
        CancelButton = close;
        var footer = HumanUi.DialogButtons(approve,close);
        footer.Dock = DockStyle.Fill;
        var form = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5,
            Padding = new Padding(AppScale.Scale(14))
        };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        form.RowStyles.Add(new RowStyle(SizeType.Absolute,AppScale.Scale(58)));
        form.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        form.RowStyles.Add(new RowStyle(SizeType.Absolute,AppMetrics.FieldRowHeight));
        form.RowStyles.Add(new RowStyle(SizeType.Absolute,AppScale.Scale(31)));
        form.RowStyles.Add(new RowStyle(SizeType.Absolute,AppMetrics.ButtonHeight+AppScale.Scale(8)));
        form.Controls.Add(note,0,0);
        form.Controls.Add(batches,0,1);
        form.Controls.Add(HumanUi.Field("Reason for approval",reason),0,2);
        form.Controls.Add(feedback,0,3);
        form.Controls.Add(footer,0,4);
        Controls.Add(form);
        Shown += async (_, _) => await LoadAsync();
        approve.Click += async (_, _) => await ApproveAsync();
        FormClosing += (_, e) => e.Cancel |= busy;
        FormClosed += (_, _) => (batches.DataSource as DataTable)?.Dispose();
    }

    private void UpdateApprove()
    {
        int length=reason.Text.Trim().Length;
        approve.Enabled = !busy && length is >=5 and <=200
            && batches.CurrentRow?.DataBoundItem is DataRowView;
    }

    private async Task LoadAsync()
    {
        if (busy) return;
        busy = true;
        approve.Enabled = false;
        try
        {
            int actor=CurrentUser.UserId;
            DataTable rows=await read();
            if (IsDisposed || actor!=CurrentUser.UserId) { rows.Dispose(); return; }
            var old=batches.DataSource as DataTable;
            batches.DataSource=rows;
            old?.Dispose();
            UiHelper.SetGridEmptyMessage(batches,
                "There are no held reviewed batches requiring administrator takeover.");
        }
        catch(Exception ex) { if(!IsDisposed)feedback.Text=DbMessage.UserMessage(ex); }
        finally { busy=false; if(!IsDisposed)UpdateApprove(); }
    }

    private async Task ApproveAsync()
    {
        if (busy || batches.CurrentRow?.DataBoundItem is not DataRowView row
            || reason.Text.Trim().Length is <5 or >200) return;
        Guid batchId=row.Row.Field<Guid>("BatchId");
        int actor=CurrentUser.UserId;
        if (ModernMessageDialog.Show(this,
            "Approve administrator responsibility for this held batch? The original sender remains in History. Eligible, unattempted recipients will be checked again at dispatch.",
            "Take Over Reviewed Batch",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)!=DialogResult.Yes)
            return;
        busy=true;
        approve.Enabled=false;
        try
        {
            await apply(batchId,reason.Text.Trim());
            if (IsDisposed || actor!=CurrentUser.UserId) return;
            Changed=true;
            DialogResult=DialogResult.OK;
            Close();
        }
        catch(Exception ex) { if(!IsDisposed)feedback.Text=DbMessage.UserMessage(ex); }
        finally { busy=false; if(!IsDisposed)UpdateApprove(); }
    }
}
