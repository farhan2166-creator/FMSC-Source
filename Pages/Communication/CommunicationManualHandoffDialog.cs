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
        Text="Free WhatsApp Business mode: FMSC opens ONE prepared chat. "
           +"You must check the school account and press Send in WhatsApp yourself. "
           +"Marking 'I sent it' is your own report, not proof of delivery."
    };
    private readonly ModernButton open=new()
    {
        Text="Open WhatsApp Chat",ThemeRole="Primary",
        Width=AppScale.Scale(180),Height=AppMetrics.ButtonHeight
    };
    private readonly ModernButton reported=new()
    {
        Text="I Sent It",ThemeRole="Success",
        Width=AppScale.Scale(125),Height=AppMetrics.ButtonHeight
    };
    private readonly ModernButton skip=new()
    {
        Text="Skip",ThemeRole="Secondary",
        Width=AppScale.Scale(105),Height=AppMetrics.ButtonHeight
    };
    private readonly ModernButton close=new()
    {
        Text="Close / Resume Later",ThemeRole="Secondary",
        Width=AppScale.Scale(180),Height=AppMetrics.ButtonHeight
    };
    private CommunicationManualRecipient? current;
    private bool busy;

    internal CommunicationManualHandoffDialog(Guid batchId)
    {
        this.batchId=batchId;
        actorId=CurrentUser.UserId;
        Text="Manual WhatsApp Business · School Communication";
        StartPosition=FormStartPosition.CenterParent;
        FormBorderStyle=FormBorderStyle.FixedDialog;
        MaximizeBox=MinimizeBox=false;
        ShowInTaskbar=false;
        ClientSize=new Size(AppScale.Scale(730),AppScale.Scale(290));
        MinimumSize=ClientSize;
        BackColor=AppTheme.PageBackground;

        var layout=new TableLayoutPanel
        {
            Dock=DockStyle.Fill,ColumnCount=1,RowCount=4,
            Padding=new Padding(AppScale.Scale(20))
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,AppScale.Scale(45)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,AppScale.Scale(55)));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,AppScale.Scale(60)));
        layout.Controls.Add(progress,0,0);
        layout.Controls.Add(destination,0,1);
        layout.Controls.Add(explanation,0,2);
        var actions=new FlowLayoutPanel
        {
            Dock=DockStyle.Fill,WrapContents=false,AutoScroll=true,
            FlowDirection=FlowDirection.LeftToRight,Margin=Padding.Empty
        };
        actions.Controls.AddRange(new Control[]{open,reported,skip,close});
        layout.Controls.Add(actions,0,3);
        Controls.Add(layout);
        open.Click+=async (_,_)=>await OpenAsync();
        reported.Click+=async (_,_)=>await MarkAsync("ReportedSent");
        skip.Click+=async (_,_)=>await MarkAsync("Skipped");
        close.Click+=(_,_)=>Close();
        Load+=async (_,_)=>await RefreshAsync();
        ApplyAvailability();
    }

    private void ApplyAvailability()
    {
        bool allowed=!busy && actorId==CurrentUser.UserId;
        open.Enabled=allowed && current?.State is "Pending" or "Opened";
        reported.Enabled=allowed && current?.State=="Opened";
        skip.Enabled=allowed && current?.State is "Pending" or "Opened";
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
                await CommunicationManualHandoffStore.ReadAsync(batchId);
            if(IsDisposed)return;
            if(actorId!=CurrentUser.UserId)
                throw new InvalidOperationException("Signed-in account changed. Close this handoff.");
            current=entries.FirstOrDefault(e=>e.State is "Pending" or "Opened");
            int reportedCount=entries.Count(e=>e.State=="ReportedSent");
            int skippedCount=entries.Count(e=>e.State is "Skipped" or "Excluded");
            progress.Text=$"Manual WhatsApp · {reportedCount} operator-reported · {skippedCount} skipped/excluded · {entries.Count} total";
            destination.Text=current is null
                ? "No unprocessed recipients remain. You can close this window."
                : $"{current.RecordName}   •••• {current.LastFour}   ({current.State})";
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
        long id=current.RecipientId;
        busy=true;
        ApplyAvailability();
        try
        {
            if(actorId!=CurrentUser.UserId)
                throw new InvalidOperationException("Signed-in account changed.");
            CommunicationManualOpen handoff=await CommunicationManualHandoffStore.OpenAsync(id);
            if(!IsDisposed && handoff.Allowed && handoff.Link is not null)
            {
                // Browser or linked app opens only after SQL has persisted
                // Opened. No automatic Send or delivery acknowledgement.
                Process.Start(new ProcessStartInfo(handoff.Link.AbsoluteUri)
                { UseShellExecute=true });
            }
            else if(!IsDisposed&&!handoff.Allowed)
                Warn("Recipient is no longer eligible: "+handoff.ExclusionCode+".");
        }
        catch(Exception ex){if(!IsDisposed)Warn("Could not open WhatsApp: "+ex.Message+
            " You may reopen this chat without marking it sent.");}
        finally
        {
            busy=false;
            if(!IsDisposed)await RefreshAsync();
        }
    }

    private async Task MarkAsync(string state)
    {
        if(busy||current is null)return;
        if(state=="ReportedSent" &&
           ModernMessageDialog.Show(this,
               "Confirm you actually pressed Send in WhatsApp. FMSC cannot verify delivery.",
               "Operator report",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)
            return;
        long id=current.RecipientId;
        busy=true;
        ApplyAvailability();
        try
        {
            if(actorId!=CurrentUser.UserId)
                throw new InvalidOperationException("Signed-in account changed.");
            await CommunicationManualHandoffStore.MarkAsync(id,state);
        }
        catch(Exception ex){if(!IsDisposed)Warn(ex.Message);}
        finally
        {
            busy=false;
            if(!IsDisposed)await RefreshAsync();
        }
    }

    private void Warn(string message) =>
        ModernMessageDialog.Show(this,message,"Manual WhatsApp",
            MessageBoxButtons.OK,MessageBoxIcon.Warning);
}
