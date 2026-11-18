using FMSC_SMS.Core;

namespace FMSC_SMS.Pages.Communication;

/// <summary>
/// Small, action-focused inbox for school-event suggestions. "Prepare Message"
/// returns an event identifier to the regular composer; this dialog can never
/// authorize a provider attempt or silently send a notification.
/// </summary>
internal sealed class CommunicationAutomationInboxDialog : ApplicationWindowForm
{
    private readonly DataGridView grid=HumanUi.Grid(false);
    private readonly ModernButton prepare=new()
    {
        Name="communicationPrepareEvent",Text="Prepare Message",ThemeRole="Primary",
        Width=AppScale.Scale(165),Enabled=false
    };
    private readonly ModernButton dismiss=new()
    {
        Name="communicationDismissEvent",Text="Dismiss",ThemeRole="Secondary",
        Width=AppScale.Scale(120),Enabled=false
    };
    private readonly ModernButton refresh=new()
    {
        Name="communicationRefreshEvents",Text="Refresh",ThemeRole="Secondary",
        Width=AppScale.Scale(115)
    };
    private readonly ModernButton close=new()
    {
        Text="Close",DialogResult=DialogResult.Cancel,Width=AppScale.Scale(95)
    };
    private readonly Label note=new()
    {
        Dock=DockStyle.Fill,AutoSize=false,ForeColor=AppTheme.MutedText,
        Text="Select an event to prepare a message; sending still requires confirmation."
    };
    private readonly TableLayoutPanel layout;
    private readonly Func<Task<IReadOnlyList<CommunicationAutomationInboxStore.Suggestion>>> load;
    private readonly Func<long,Task> dismissAction;
    private bool busy;
    internal CommunicationAutomationInboxStore.Suggestion? SelectedSuggestion { get; private set; }

    internal CommunicationAutomationInboxDialog(
        Func<Task<IReadOnlyList<CommunicationAutomationInboxStore.Suggestion>>>? load=null,
        Func<long,Task>? dismissAction=null)
    {
        this.load=load??CommunicationAutomationInboxStore.RefreshAsync;
        this.dismissAction=dismissAction??CommunicationAutomationInboxStore.DismissAsync;
        Text="Messages Needing Review";
        ClientSize=new Size(AppScale.Scale(750),AppScale.Scale(460));
        StartPosition=FormStartPosition.CenterParent;
        ShowInTaskbar=false;
        BackColor=AppTheme.PageBackground;
        MinimizeBox=MaximizeBox=false;

        grid.Name="communicationAutomationInbox";
        grid.Dock=DockStyle.Fill;
        grid.AutoGenerateColumns=false;
        grid.SelectionMode=DataGridViewSelectionMode.FullRowSelect;
        grid.MultiSelect=false;
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName="EventCode",HeaderText="Event",
            Name="EventCode",MinimumWidth=AppScale.Scale(155),
            AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill,FillWeight=90
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName="StudentName",HeaderText="Student",
            MinimumWidth=AppScale.Scale(195),
            AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill,FillWeight=130
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName="AdmissionNumber",HeaderText="Admission no.",
            MinimumWidth=AppScale.Scale(140),
            AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill,FillWeight=80,
            DefaultCellStyle={Alignment=DataGridViewContentAlignment.MiddleCenter}
        });
        grid.CellFormatting+=(_,e)=>
        {
            if(e.RowIndex<0 || e.ColumnIndex<0 ||
               grid.Columns[e.ColumnIndex].Name!="EventCode")return;
            if(e.Value is string code)
            {
                e.Value=CommunicationAutomationInboxStore.EventLabel(code);
                e.FormattingApplied=true;
            }
        };

        var actions=HumanUi.DialogButtons(refresh,dismiss,prepare,close);
        layout=new TableLayoutPanel
        {
            Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,
            Padding=new Padding(AppScale.Scale(12))
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,AppScale.Scale(56)));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,AppMetrics.ButtonHeight+AppScale.Scale(12)));
        layout.Controls.Add(note,0,0);
        layout.Controls.Add(grid,0,1);
        layout.Controls.Add(actions,0,2);
        Controls.Add(layout);

        AcceptButton=prepare;
        CancelButton=close;
        FormClosing+=(_,e)=>e.Cancel|=busy;
        grid.SelectionChanged+=(_,_)=>UpdateActions();
        grid.CellDoubleClick+=(_,_)=>Choose();
        prepare.Click+=(_,_)=>Choose();
        refresh.Click+=async (_,_)=>await RunAsync(LoadRowsAsync);
        dismiss.Click+=async (_,_)=>await RunAsync(async ()=>
        {
            var selected=Current();
            if(selected is null)return;
            if(ModernMessageDialog.Show(this,
                "Dismiss the "+CommunicationAutomationInboxStore.EventLabel(selected.EventCode)+
                " suggestion for "+selected.StudentName+"?",
                Text,MessageBoxButtons.YesNo,MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2)!=DialogResult.Yes)return;
            await this.dismissAction(selected.CandidateId);
            await LoadRowsAsync();
        });
        Shown+=async (_,_)=>await RunAsync(LoadRowsAsync);
    }

    private CommunicationAutomationInboxStore.Suggestion? Current() =>
        grid.CurrentRow?.DataBoundItem as CommunicationAutomationInboxStore.Suggestion;

    private void UpdateActions()
    {
        prepare.Enabled=dismiss.Enabled=!busy && Current() is not null;
    }

    private void Choose()
    {
        if(busy || Current() is not { } item)return;
        SelectedSuggestion=item;
        DialogResult=DialogResult.OK;
    }

    private async Task LoadRowsAsync()
    {
        var rows=await load();
        if(IsDisposed)return;
        grid.DataSource=rows.ToList();
        // The grid supplies the sole empty-state message; avoid saying "no records" twice.
        note.ForeColor=AppTheme.MutedText;
        note.Text=rows.Count==0 ? "" : "Select an event to prepare a message; sending still requires confirmation.";
        note.Visible=rows.Count>0;
        layout.RowStyles[0].Height=rows.Count==0 ? 0 : AppScale.Scale(48);
        UpdateActions();
    }

    private async Task RunAsync(Func<Task> operation)
    {
        if(busy || IsDisposed)return;
        busy=true;
        prepare.Enabled=dismiss.Enabled=refresh.Enabled=false;
        try { await operation(); }
        catch(Exception ex)
        {
            if(!IsDisposed)
            {
                note.Text=DbMessage.UserMessage(ex);
                note.ForeColor=AppTheme.Danger;
                note.Visible=true;
                layout.RowStyles[0].Height=AppScale.Scale(56);
            }
        }
        finally
        {
            busy=false;
            if(!IsDisposed)
            {
                refresh.Enabled=true;
                UpdateActions();
            }
        }
    }
}
