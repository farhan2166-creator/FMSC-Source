using System.Diagnostics;
using FMSC_SMS.Core;

namespace FMSC_SMS.Pages.Communication;

/// <summary>
/// One reviewed WhatsApp Business chat at a time. The operator presses Send
/// outside FMSC and explicitly reports that action inside FMSC.
/// This window never claims delivery or automates the WhatsApp client.
/// </summary>
internal sealed class CommunicationManualHandoffDialog : ApplicationWindowForm
{
    private readonly Guid batchId;
    private readonly int actorId;
    private readonly bool openFirstChat;
    private readonly Label progress=new()
    {
        Dock=DockStyle.Fill,AutoEllipsis=true,
        TextAlign=ContentAlignment.MiddleLeft,Font=AppTheme.ButtonFont
    };
    private readonly Label destination=new()
    {
        Dock=DockStyle.Fill,AutoEllipsis=true,
        TextAlign=ContentAlignment.MiddleLeft,Font=AppTheme.NormalFont
    };
    private readonly Label explanation=new()
    {
        Dock=DockStyle.Fill,AutoEllipsis=true,
        TextAlign=ContentAlignment.TopLeft,Font=AppTheme.NormalFont,
        Text="Check the school WhatsApp account and press Send there. "
           +"FMSC only records your report; it cannot verify delivery."
    };
    private readonly ModernTextBox preparedMessage = HumanUi.TextBox(true, 1600);
    private readonly ModernButton open=new()
    {
        Text="Open WhatsApp",ThemeRole="Primary",
        Width=AppScale.Scale(180),Height=AppMetrics.ButtonHeight
    };
    private readonly ModernButton reported=new()
    {
        Text="I Sent It",ThemeRole="Success",
        Width=AppScale.Scale(150),Height=AppMetrics.ButtonHeight
    };
    private readonly ModernButton skip=new()
    {
        Text="Skip",ThemeRole="Secondary",
        Width=AppScale.Scale(105),Height=AppMetrics.ButtonHeight
    };
    private readonly ModernButton close=new()
    {
        Text="Finish Later",ThemeRole="Secondary",
        Width=AppScale.Scale(180),Height=AppMetrics.ButtonHeight
    };
    private CommunicationManualRecipient? current;
    private int remainingAfterCurrent;
    private bool busy;
    private FileStream? batchLease;
    private readonly HashSet<long> failedOpen = new();
    internal sealed record Operations(
        Func<Guid, Task<IReadOnlyList<CommunicationManualRecipient>>> Read,
        Func<long, Task<CommunicationManualOpen>> Open,
        Func<long, string, Task> Mark,
        Action<Uri> Launch,
        Action<string> Notify,
        Func<long, Task>? RecordShellHandoff = null,
        Func<bool>? ConfirmRecoveredSend = null,
        Func<long, Task>? MarkRecovered = null);
    private readonly Operations operations;


    internal CommunicationManualHandoffDialog(Guid batchId, bool openFirstChat = false)
        : this(batchId, null, openFirstChat) { }
    internal CommunicationManualHandoffDialog(Guid batchId, Operations? operations, bool openFirstChat = false)
    {
        this.operations = operations ?? new Operations(CommunicationManualHandoffStore.ReadAsync,
            CommunicationManualHandoffStore.OpenAsync, (id, outcome) => CommunicationManualHandoffStore.MarkAsync(id, outcome, false),
            uri => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }),
            message => ModernMessageDialog.Show(this, message, "Manual WhatsApp", MessageBoxButtons.OK, MessageBoxIcon.Warning),
            CommunicationManualHandoffStore.RecordShellHandoffAsync,
            () => ModernMessageDialog.Show(this,
                "FMSC could not confirm the chat opened. Check the conversation in WhatsApp. Did you already send this message?",
                "Check WhatsApp", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes,
            id => CommunicationManualHandoffStore.MarkAsync(id, "ReportedSent", true));
        this.batchId=batchId;
        this.openFirstChat = openFirstChat;
        actorId=CurrentUser.UserId;
        Text="Manual WhatsApp Business · School Communication";
        StartPosition=FormStartPosition.CenterParent;
        FormBorderStyle=FormBorderStyle.FixedDialog;
        MaximizeBox=MinimizeBox=false;
        ShowInTaskbar=false;
        ClientSize=new Size(AppScale.Scale(730),AppScale.Scale(410));
        MinimumSize=ClientSize;
        BackColor=AppTheme.PageBackground;

        var layout=new TableLayoutPanel
        {
            Dock=DockStyle.Fill,ColumnCount=1,RowCount=5,
            Padding=new Padding(AppScale.Scale(20))
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,AppScale.Scale(45)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,AppScale.Scale(55)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,AppScale.Scale(48)));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,AppScale.Scale(60)));
        layout.Controls.Add(progress,0,0);
        layout.Controls.Add(destination,0,1);
        layout.Controls.Add(explanation,0,2);
        preparedMessage.ReadOnly = true;
        preparedMessage.ScrollBars = ScrollBars.Vertical;
        preparedMessage.Dock = DockStyle.Fill;
        layout.Controls.Add(preparedMessage,0,3);
        var actions=new FlowLayoutPanel
        {
            Dock=DockStyle.Fill,WrapContents=false,AutoScroll=true,
            FlowDirection=FlowDirection.LeftToRight,Margin=Padding.Empty
        };
        actions.Controls.AddRange(new Control[]{open,reported,skip,close});
        layout.Controls.Add(actions,0,4);
        Controls.Add(layout);
        open.Click+=async (_,_)=>await OpenAsync();
        reported.Click+=async (_,_)=>await MarkAsync("ReportedSent");
        skip.Click+=async (_,_)=>await MarkAsync("Skipped");
        close.Click+=(_,_)=>Close();
        Shown+=async (_,_)=> {
            try { batchLease=CommunicationHandoffLease.Acquire(batchId); }
            catch(Exception ex) { Warn(ex.Message); Close(); return; }
            await RefreshAsync();
            if (openFirstChat && !IsDisposed && current?.State == "Pending")
                await OpenAsync();
        };
        FormClosing+=(_,e)=> { if(busy)e.Cancel=true; };
        Disposed+=(_,_)=> { batchLease?.Dispose(); batchLease=null; };
        ApplyAvailability();
    }

    private void ApplyAvailability()
    {
        bool allowed=!busy && actorId==CurrentUser.UserId;
        open.Enabled=allowed && (current?.State is "Pending" or "Opened");
        bool canReport = current?.State=="Opened" && !failedOpen.Contains(current.RecipientId);
        reported.Enabled=allowed && canReport;
        reported.Text=canReport && !current!.ShellHandoffRecorded && operations.ConfirmRecoveredSend is not null
            ? "Check & Report" : remainingAfterCurrent > 0 ? "I Sent It · Next" : "I Sent It";
        skip.Enabled=allowed && (current?.State is "Pending" or "Opened");
        skip.Text=remainingAfterCurrent > 0 ? "Skip · Next" : "Skip";
        close.Enabled=!busy;
    }

    private async Task RefreshAsync()
    {
        if(IsDisposed)return;
        busy=true;
        ApplyAvailability();
        try
        {
            IReadOnlyList<CommunicationManualRecipient> entries=
                await operations.Read(batchId);
            if(IsDisposed)return;
            if(actorId!=CurrentUser.UserId)
                throw new InvalidOperationException("Signed-in account changed. Close this handoff.");
            var nextRecipient=entries.FirstOrDefault(e=>e.State is "Pending" or "Opened");
            if (current?.RecipientId != nextRecipient?.RecipientId) preparedMessage.Text = "";
            current=nextRecipient;
            int reportedCount=entries.Count(e=>e.State=="ReportedSent");
            int skippedCount=entries.Count(e=>e.State is "Skipped" or "Excluded");
            int remaining=entries.Count(e=>e.State is "Pending" or "Opened");
            remainingAfterCurrent=Math.Max(0,remaining-(nextRecipient is null ? 0 : 1));
            progress.Text=nextRecipient is null
                ? $"All done · {reportedCount} reported sent · {skippedCount} skipped"
                : $"Chat {entries.Count-remaining+1} of {entries.Count} · {reportedCount} reported sent · {skippedCount} skipped";
            destination.Text=current is null
                ? "No unprocessed recipients remain. You can close this window."
                : $"{current.RecordName}   •••• {current.LastFour}   ({DisplayState(current)})";
        }
        catch(Exception ex)
        {
            Warn(ex.Message);
        }
        finally{busy=false;if(!IsDisposed)ApplyAvailability();}
    }

    private async Task OpenAsync()
    {
        if(busy||current is null)return;
        if (current.State == "Opened" && !current.ShellHandoffRecorded
            && !failedOpen.Contains(current.RecipientId) && operations.ConfirmRecoveredSend is not null
            && ModernMessageDialog.Show(this, "Check WhatsApp first. Reopen only if you still need to send this message.",
                "Reopen WhatsApp Chat", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        long id=current.RecipientId;
        busy=true;
        ApplyAvailability();
        try
        {
            if(actorId!=CurrentUser.UserId)
                throw new InvalidOperationException("Signed-in account changed.");
            CommunicationManualOpen handoff=await operations.Open(id);
            if(actorId!=CurrentUser.UserId)
                throw new InvalidOperationException("Signed-in account changed. No chat was launched.");
            if(!IsDisposed && handoff.Allowed && handoff.Link is not null)
            {
                preparedMessage.Text = handoff.Message ?? "";
                // Browser or linked app opens only after SQL has persisted
                // Opened. No automatic Send or delivery acknowledgement.
                try { operations.Launch(handoff.Link); }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    throw new CommunicationValidationException("Windows could not open the WhatsApp link.");
                }
                failedOpen.Remove(id);
                if (operations.RecordShellHandoff is not null)
                    await operations.RecordShellHandoff(id);
            }
            else if(!IsDisposed&&!handoff.Allowed)
                Warn("Recipient is no longer eligible: "+handoff.ExclusionCode+".");
        }
        catch(Exception ex)
        {
            failedOpen.Add(id);
            if (!IsDisposed)
                Warn("Could not confirm the WhatsApp handoff: " + DbMessage.UserMessage(ex) +
                    " Check WhatsApp before trying again.");
        }
        finally
        {
            busy=false;
            if(!IsDisposed)await RefreshAsync();
        }
    }

    private async Task MarkAsync(string state)
    {
        if(busy||current is null)return;
        bool recoveredReport = state == "ReportedSent" && !current.ShellHandoffRecorded
            && operations.ConfirmRecoveredSend is not null;
        if (state=="ReportedSent" && (current.State!="Opened" || failedOpen.Contains(current.RecipientId)
            || (recoveredReport && !operations.ConfirmRecoveredSend!()))) return;
        long id=current.RecipientId;
        bool recorded=false;
        busy=true;
        ApplyAvailability();
        try
        {
            if(actorId!=CurrentUser.UserId)
                throw new InvalidOperationException("Signed-in account changed.");
            if (recoveredReport && operations.MarkRecovered is not null)
                await operations.MarkRecovered(id);
            else
                await operations.Mark(id,state);
            recorded=true;
        }
        catch(Exception ex){if(!IsDisposed)Warn(ex.Message);}
        finally
        {
            busy=false;
            if(!IsDisposed)
            {
                await RefreshAsync();
                // "I Sent It · Next" and "Skip · Next" explicitly authorize
                // opening the next reviewed contact. This never marks the next
                // message sent or bypasses SQL consent / destination rechecks.
                // Do not reopen a previously Opened or uncertain chat.
                if (recorded && current?.State=="Pending"
                    && state is ("ReportedSent" or "Skipped"))
                    await OpenAsync();
                if (!IsDisposed && openFirstChat && current is null) Close();
            }
        }
    }

    private void Warn(string message) => operations.Notify(message);

    private static string DisplayState(CommunicationManualRecipient recipient) =>
        recipient.State == "Opened" && !recipient.ShellHandoffRecorded
            ? "Check WhatsApp before reopening" : recipient.State;
}
