using System.Data;
using FMSC_SMS.Core;

namespace FMSC_SMS.Pages.Communication;

/// <summary>A visible, cancellable, sequential dispatch session. Accepted means
/// submitted to the provider. Unknown recipients are never sent again here.</summary>
internal sealed class CommunicationApiDispatchDialog : ApplicationWindowForm
{
    internal sealed record Operations(Func<Guid,Task<DataTable>> Read,
        Func<long,string,CancellationToken,Task<CommunicationDispatchStore.DispatchResult>> Send,
        Func<long,CancellationToken,Task<string>> Reconcile,Func<long,string,Task> Retry);
    private readonly Guid batch;
    private readonly int actor=CurrentUser.UserId;
    private readonly Operations operations;
    private readonly DataGridView grid=HumanUi.Grid(false);
    private readonly ModernTextBox messagePreview=HumanUi.TextBox(true,1600);
    private readonly Label status=new() {Dock=DockStyle.Fill,AutoEllipsis=true};
    private readonly ProgressBar progress=new() {Dock=DockStyle.Fill};
    private readonly ModernTextBox reason=HumanUi.TextBox(false,200);
    private readonly ModernButton send=new() {Text="Send Pending",ThemeRole="Primary",Width=AppScale.Scale(160),Height=AppMetrics.ButtonHeight,Enabled=false};
    private readonly ModernButton stop=new() {Text="Stop",ThemeRole="Secondary",Width=AppScale.Scale(100),Height=AppMetrics.ButtonHeight,Enabled=false};
    private readonly ModernButton check=new() {Text="Check SMS Status",ThemeRole="Secondary",Width=AppScale.Scale(180),Height=AppMetrics.ButtonHeight,Enabled=false};
    private readonly ModernButton retry=new() {Text="Approve Retry",ThemeRole="Secondary",Width=AppScale.Scale(150),Height=AppMetrics.ButtonHeight,Enabled=false};
    private readonly ModernButton cancelPending=new() {Text="Cancel Pending",ThemeRole="Secondary",Width=AppScale.Scale(165),Height=AppMetrics.ButtonHeight,Enabled=false};
    private readonly ModernButton close=new() {Text="Close",ThemeRole="Secondary",Width=AppScale.Scale(100),Height=AppMetrics.ButtonHeight};
    private CancellationTokenSource? cancellation;
    private bool busy,closeRequested,scheduleBlocked;
    private FileStream? lease;
    internal CommunicationApiDispatchDialog(Guid batch,bool startConfirmed=false,Operations? operations=null)
    {
        this.batch=batch;
        this.operations=operations ?? new Operations(CommunicationApiBatchStore.ReadAsync,
            CommunicationDispatchStore.SendOneReviewedAsync,CommunicationDispatchStore.ReconcileKnownAttemptAsync,
            CommunicationDispatchStore.ApproveDefinitiveRetryAsync);
        Text="API messages";StartPosition=FormStartPosition.CenterParent;ShowInTaskbar=false;
        ClientSize=new Size(AppScale.Scale(950),AppScale.Scale(570));MinimumSize=new Size(AppScale.Scale(800),AppScale.Scale(500));
        BackColor=AppTheme.PageBackground;
        grid.AutoGenerateColumns=false;
        foreach(var (field,title,width) in new[]{("RecipientName","School record",210),("Destination","Number ending",110),("State","Status",105),("OutcomeDetail","Details",230)})
            grid.Columns.Add(new DataGridViewTextBoxColumn {Name=field,DataPropertyName=field,HeaderText=title,
                MinimumWidth=width,AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill,SortMode=DataGridViewColumnSortMode.NotSortable});
        cancelPending.Click+=async(_,_)=>await RunAsync(async()=>{
            if(ModernMessageDialog.Show(this,"Cancel all remaining Pending messages in this batch? Submitted or uncertain attempts keep their existing status.",
                "Cancel Pending",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
            await DatabaseHelper.ExecuteUserAsync("msg.CancelPendingApiMessages",DatabaseHelper.P("@BatchId",batch));
            await RefreshAsync();
        });
        var buttons=new FlowLayoutPanel {Dock=DockStyle.Fill,WrapContents=false,AutoScroll=true};
        buttons.Controls.AddRange(new Control[]{send,stop,check,cancelPending,close});
        var retryRow=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,RowCount=1};
        retryRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));retryRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,AppScale.Scale(170)));
        retryRow.Controls.Add(HumanUi.Field("Reason (only a definitively failed message can be approved for retry)",reason),0,0);
        retryRow.Controls.Add(retry,1,0);
        var layout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=7,Padding=new Padding(16)};
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        foreach(var h in new[]{65,24})layout.RowStyles.Add(new RowStyle(SizeType.Absolute,AppScale.Scale(h)));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent,60));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent,40));
        foreach(var h in new[]{70,55,48})layout.RowStyles.Add(new RowStyle(SizeType.Absolute,AppScale.Scale(h)));
        layout.Controls.Add(status,0,0);layout.Controls.Add(progress,0,1);layout.Controls.Add(grid,0,2);
        messagePreview.ReadOnly=true;messagePreview.ScrollBars=ScrollBars.Vertical;
        layout.Controls.Add(HumanUi.Field("Reviewed message for selected recipient",messagePreview),0,3);
        layout.Controls.Add(retryRow,0,4);layout.Controls.Add(buttons,0,5);
        layout.Controls.Add(new Label {Dock=DockStyle.Fill,ForeColor=AppTheme.MutedText,
            Text="Accepted = submitted, not delivered. Meta delivery receipts require a webhook service and are not available here. Unknown = check the provider; never blindly retry."},0,6);
        Controls.Add(layout);CancelButton=close;
        send.Click+=async(_,_)=>await RunAsync(async()=>{
            await RefreshAsync();
            int pending=Rows.Count(r=>Convert.ToString(r["State"])=="Pending");
            if(pending==0)return;
            if(ModernMessageDialog.Show(this,$"Send the {pending} pending reviewed message(s) through the configured API? Provider charges may apply.",
                "Confirm Send",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
            await SendPendingAsync();
        });
        stop.Click+=(_,_)=>{cancellation?.Cancel();status.Text="Stopping after the current request is safely recorded…";};
        close.Click+=(_,_)=>Close();
        check.Click+=async(_,_)=>await RunAsync(async()=>{
            if(grid.CurrentRow?.DataBoundItem is not DataRowView selected || selected.Row.IsNull("AttemptId")
                || selected.Row.IsNull("ProviderMessageId") || Convert.ToString(selected["ProviderCode"])!="Twilio")return;
            await this.operations.Reconcile(Convert.ToInt64(selected["AttemptId"]),cancellation!.Token);
            await RefreshAsync();
        });
        retry.Click+=async(_,_)=>await RunAsync(async()=>{
            if(grid.CurrentRow?.DataBoundItem is not DataRowView selected || Convert.ToString(selected["State"])!="Failed")return;
            if(string.IsNullOrWhiteSpace(reason.Text))throw new CommunicationValidationException("Enter why this definitively failed message should be retried.");
            if(ModernMessageDialog.Show(this,"Approve this failed message for another reviewed attempt? This does not send it yet.",
                "Approve Retry",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
            await this.operations.Retry(Convert.ToInt64(selected["RecipientId"]),reason.Text.Trim());reason.Clear();await RefreshAsync();
        });
        grid.CurrentCellChanged+=(_,_)=>{ShowSelectedMessage();UpdateActions();};
        grid.DataBindingComplete+=(_,_)=>ShowSelectedMessage();
        Shown+=async(_,_)=>await RunAsync(async()=>{
            lease=CommunicationHandoffLease.Acquire(batch);
            await RefreshAsync();
            if(startConfirmed)await SendPendingAsync();
        });
        FormClosing+=(_,e)=>{if(busy){closeRequested=true;cancellation?.Cancel();e.Cancel=true;}};
        FormClosed+=(_,_)=>{lease?.Dispose();cancellation?.Dispose();(grid.DataSource as DataTable)?.Dispose();};
    }
    private DataRow[] Rows => (grid.DataSource as DataTable)?.AsEnumerable().ToArray() ?? Array.Empty<DataRow>();
    private async Task RefreshAsync()
    {
        Guard();
        var table=await operations.Read(batch);
        if(IsDisposed || actor!=CurrentUser.UserId){table.Dispose();throw new UnauthorizedAccessException("The signed-in user changed.");}
        if(table.Rows.Count==0){table.Dispose();throw new CommunicationValidationException("This batch has no reviewed API messages.");}
        table.Columns.Add("OutcomeDetail",typeof(string));
        foreach(DataRow row in table.Rows)
        {
            string? code=table.Columns.Contains("FailureCode") ? Convert.ToString(row["FailureCode"]) : null;
            row["OutcomeDetail"]=code switch {
                "HTTP401" or "HTTP403"=>"Provider access denied. Check the token and permissions in Settings.",
                "HTTP404"=>"Provider account or sender not found. Check Settings.",
                "HTTP400" or "HTTP422"=>"Provider rejected the message. Check the template, sender and destination.",
                "TransportUncertain"=>"No definite response. Check the provider before further action.",
                _=>Convert.ToString(row["State"])=="Unknown" ? "Unverified result; do not blindly resend."
                    : Convert.ToString(row["State"])=="Failed" ? "Provider rejected the message"
                        + (code is { Length: >0 and <=50 } && code.All(c=>char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? " ("+code+")" : "")
                        + ". Check the provider account for details."
                    : ""
            };
        }
        table.Columns.Add("Destination",typeof(string));
        foreach(DataRow row in table.Rows)row["Destination"]="•••• "+Convert.ToString(row["PhoneE164"])![^4..];
        var old=grid.DataSource as DataTable;grid.DataSource=table;old?.Dispose();
        var rows=Rows;
        int pending=rows.Count(r=>Convert.ToString(r["State"])=="Pending");
        int unknown=rows.Count(r=>Convert.ToString(r["State"])=="Unknown");
        int accepted=rows.Count(r=>Convert.ToString(r["State"]) is "Accepted" or "Submitted");
        int delivered=rows.Count(r=>Convert.ToString(r["State"])=="Delivered");
        int failed=rows.Count(r=>Convert.ToString(r["State"])=="Failed");
        progress.Maximum=Math.Max(1,rows.Length);progress.Value=rows.Length-pending;
        scheduleBlocked=false;
        string? scheduleStatus=null;
        if (table.Columns.Contains("DueUtc") && rows.Length>0)
        {
            DataRow row=rows[0];
            if (!row.IsNull("DueUtc"))
            {
                DateTime due=DateTime.SpecifyKind(Convert.ToDateTime(row["DueUtc"]),DateTimeKind.Utc);
                if (due>DateTime.UtcNow)
                {
                    scheduleBlocked=true;
                    scheduleStatus="Scheduled for "+due.ToLocalTime().ToString("dd MMM yyyy h:mm tt")+".";
                }
                else if (table.Columns.Contains("ExpiresUtc") && !row.IsNull("ExpiresUtc") &&
                         DateTime.SpecifyKind(Convert.ToDateTime(row["ExpiresUtc"]),DateTimeKind.Utc)<=DateTime.UtcNow)
                {
                    scheduleBlocked=true;
                    scheduleStatus="This scheduled batch has expired. Unsent messages are not eligible for sending.";
                }
                else if (table.Columns.Contains("HoldForReview") &&
                         !row.IsNull("HoldForReview") && Convert.ToBoolean(row["HoldForReview"]))
                {
                    scheduleBlocked=true;
                    scheduleStatus="This batch is paused pending review.";
                }
            }
        }
        status.Text=$"{pending} pending · {accepted} accepted · {delivered} delivered · {failed} failed · {unknown} unknown\n"+
            (scheduleStatus ?? "Only Pending messages can be sent. Stopping keeps the remainder for later.");
        ShowSelectedMessage();
        UpdateActions();
    }
    private void ShowSelectedMessage()
    {
        // WinForms can bind the rows after CurrentCellChanged has already
        // fired. The initial preview must not depend on a second mouse click.
        DataRow? row=(grid.CurrentRow?.DataBoundItem as DataRowView)?.Row ?? Rows.FirstOrDefault();
        messagePreview.Text=row is not null && row.Table.Columns.Contains("RenderedBody")
            ? Convert.ToString(row["RenderedBody"]) ?? "" : "";
    }
    private void Guard()
    {
        if(actor!=CurrentUser.UserId || !CurrentUser.HasPermission("Communication.Send"))
            throw new UnauthorizedAccessException("The sending user or permission changed. Close this session.");
    }
    private async Task SendPendingAsync()
    {
        var pending=Rows.Where(r=>Convert.ToString(r["State"])=="Pending")
            .Select(r=>(Id:Convert.ToInt64(r["RecipientId"]),Channel:Convert.ToString(r["Channel"])!)).ToArray();
        foreach(var item in pending)
        {
            cancellation!.Token.ThrowIfCancellationRequested();Guard();
            var result=await operations.Send(item.Id,item.Channel,cancellation.Token);
            await RefreshAsync();
            // A provider/network uncertainty must be reviewed before continuing
            // with more chargeable requests; never infer failure from a timeout.
            if(result.State=="Unknown")break;
        }
    }
    private async Task RunAsync(Func<Task> action)
    {
        if(busy)return;
        busy=true;cancellation=new CancellationTokenSource();UpdateActions();stop.Enabled=true;
        try{Guard();await action();}
        catch(OperationCanceledException){status.Text="Stopped. Pending messages remain unsent. Review any Unknown result before doing anything further.";}
        catch(CommunicationValidationException ex){ModernMessageDialog.Show(this,ex.Message,"Messages",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
        catch(Exception){ModernMessageDialog.Show(this,"The operation could not be completed. Reopen this batch to check its saved state. Any uncertain attempt remains Unknown and will not be retried automatically.","Messages",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
        finally{busy=false;stop.Enabled=false;cancellation.Dispose();cancellation=null;UpdateActions();if(closeRequested)Close();}
    }
    private void UpdateActions()
    {
        var selected=(grid.CurrentRow?.DataBoundItem as DataRowView)?.Row;
        send.Enabled=!busy && !scheduleBlocked && Rows.Any(r=>Convert.ToString(r["State"])=="Pending");
        cancelPending.Enabled=!busy && Rows.Any(r=>Convert.ToString(r["State"])=="Pending");
        check.Enabled=!busy && selected is not null && Convert.ToString(selected["ProviderCode"])=="Twilio"
            && !selected.IsNull("ProviderMessageId") && Convert.ToString(selected["State"]) is "Accepted" or "Submitted" or "Unknown";
        retry.Enabled=reason.Enabled=!busy && selected is not null && Convert.ToString(selected["State"])=="Failed";
    }
}
