using System.Data;
using FMSC_SMS.Core;

namespace FMSC_SMS.Pages.Communication;

/// <summary>Manage approved timing without mutating submitted messages or the original audience.</summary>
internal sealed class CommunicationRecurringSchedulesDialog : ApplicationWindowForm
{
    private readonly int actor=CurrentUser.UserId;
    private readonly DataGridView grid=HumanUi.Grid(false);
    private readonly ModernTextBox details=HumanUi.TextBox(true,2400);
    private readonly Label status=new(){Dock=DockStyle.Fill,AutoEllipsis=true};
    private readonly TableLayoutPanel layout;
    private readonly ModernButton pause=new(){Text="Pause",ThemeRole="Secondary"};
    private readonly ModernButton resume=new(){Text="Resume…",ThemeRole="Primary"};
    private readonly ModernButton cancel=new(){Text="Cancel Schedule",ThemeRole="Secondary"};
    private bool busy;
    private readonly Guid? focusRule;
    internal CommunicationHistoryDraft? Draft {get;private set;}
    internal CommunicationRecurringSchedulesDialog(Guid? selectedRule=null)
    {
        focusRule=selectedRule;
        Text="Recurring Schedules";StartPosition=FormStartPosition.CenterParent;ShowInTaskbar=false;
        ClientSize=new Size(AppScale.Scale(930),AppScale.Scale(610));MinimumSize=new Size(AppScale.Scale(760),AppScale.Scale(510));
        grid.AutoGenerateColumns=false;details.ReadOnly=true;details.ScrollBars=ScrollBars.Vertical;
        grid.SelectionMode=DataGridViewSelectionMode.FullRowSelect;
        foreach(var (name,title) in new[]{("Audience","Audience"),("Frequency","Interval"),("Every","Every"),("NextSchool","Next (school time)"),
            ("Occurrences","Occurrences"),("MaxOccurrences","Maximum"),("State","State"),("ReasonCode","Needs review")})
            grid.Columns.Add(new DataGridViewTextBoxColumn{Name=name,DataPropertyName=name,HeaderText=title,MinimumWidth=AppScale.Scale(85),AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill});
        foreach(string column in new[]{"Every","NextSchool","Occurrences","MaxOccurrences","State"})
            grid.Columns[column]!.DefaultCellStyle.Alignment=DataGridViewContentAlignment.MiddleCenter;
        var edit=new ModernButton{Text="Review in Composer",ThemeRole="Secondary"};
        edit.Click+=async(_,_)=>{
            if(busy || Selected is not { } row)return;
            await RunAsync(async()=>{
                if(Convert.ToString(row["State"]) is "Active" or "Held")
                    await CommunicationPlannedWorkStore.SetStateAsync((Guid)row["RuleId"],"Paused");
                Draft=new(new CommunicationTarget(Enum.Parse<CommunicationAudience>(Convert.ToString(row["Audience"])!),
                    Convert.ToInt32(row["CampusId"]),Convert.ToInt32(row["ClassId"]),Convert.ToInt32(row["StudentId"]),Convert.ToInt32(row["StaffId"]),Convert.ToString(row["Channel"])!),
                    Convert.ToString(row["ProviderCode"])!,Convert.ToString(row["Body"])!,Convert.ToString(row["StudentName"])??"",Convert.ToString(row["StaffName"])??"",null);
            });
            if(Draft is not null){DialogResult=DialogResult.OK;Close();}
        };
        var close=new ModernButton{Text="Close",DialogResult=DialogResult.Cancel};CancelButton=close;
        layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=5,Padding=new Padding(AppScale.Scale(14))};
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent,70));layout.RowStyles.Add(new RowStyle(SizeType.Percent,30));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,AppScale.Scale(42)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,AppMetrics.ButtonHeight+AppScale.Scale(12)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,AppMetrics.ButtonHeight+AppScale.Scale(12)));
        layout.Controls.Add(grid,0,0);layout.Controls.Add(details,0,1);layout.Controls.Add(status,0,2);
        // Five equally-sized buttons wrapped into a clipped second row in the old footer.
        layout.Controls.Add(HumanUi.DialogButtons(pause,resume,edit),0,3);
        layout.Controls.Add(HumanUi.DialogButtons(cancel,close),0,4);
        Controls.Add(layout);
        grid.CurrentCellChanged+=(_,_)=>Selection();
        pause.Click+=async(_,_)=>await RunAsync(async()=>{if(Selected is { } row)await CommunicationPlannedWorkStore.SetStateAsync((Guid)row["RuleId"],"Paused");});
        cancel.Click+=async(_,_)=>await RunAsync(async()=>{
            if(Selected is { } row && ModernMessageDialog.Show(this,"Cancel future occurrences and unsent recipients? Submitted and uncertain attempts keep their recorded outcomes.",
                Text,MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)==DialogResult.Yes)
                await CommunicationPlannedWorkStore.SetStateAsync((Guid)row["RuleId"],"Cancelled");
        });
        resume.Click+=async(_,_)=>await RunAsync(async()=>{
            if(Selected is not { } row)return;
            var zone=TimeZoneInfo.FindSystemTimeZoneById(Convert.ToString(row["TimeZoneId"])!);
            var recurrence=new CommunicationRecurrence(Convert.ToString(row["Frequency"])!,Convert.ToInt32(row["Every"]),
                Convert.ToDateTime(row["FirstLocal"]),zone.Id,new DateTimeOffset(DateTime.SpecifyKind(Convert.ToDateTime(row["EndUtc"]),DateTimeKind.Utc)),
                Convert.ToInt32(row["MaxOccurrences"]),Convert.ToInt32(row["ExpiryHours"]));
            var next=recurrence.NextAfter(DateTimeOffset.UtcNow);
            if(next>recurrence.End)throw new CommunicationValidationException("The approved end date has passed. Review a new recurrence.");
            if(ModernMessageDialog.Show(this,"Resume this approved audience, content and provider with the next occurrence at "+
                TimeZoneInfo.ConvertTime(next,zone).ToString("dd MMM yyyy h:mm tt")+"? Existing charges and unknown outcomes remain protected; provider charges may apply.",
                Text,MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)!=DialogResult.Yes)return;
            await CommunicationPlannedWorkStore.ResumeAsync(row,next);
        });
        Shown+=async(_,_)=>await RunAsync(()=>Task.CompletedTask);
        FormClosing+=(_,e)=>{if(busy)e.Cancel=true;};
        Disposed+=(_,_)=>{(grid.DataSource as DataTable)?.Dispose();};
    }
    private DataRow? Selected=>(grid.CurrentRow?.DataBoundItem as DataRowView)?.Row;
    private void Selection()
    {
        var row=Selected;string state=Convert.ToString(row?["State"])??"";
        // No selected schedule: collapse the unused details editor and give its space to the grid.
        details.Visible=row is not null;
        layout.RowStyles[0].Height=row is null?100:70;
        layout.RowStyles[1].Height=row is null?0:30;
        details.Text=row is null ? "" : Convert.ToString(row["Body"])+Environment.NewLine+Environment.NewLine+
            "End: "+Convert.ToString(row["EndUtc"])+" UTC · "+Convert.ToString(row["ProviderCode"])+
            " · Uncertain attempts: "+Convert.ToString(row["UncertainAttempts"]);
        pause.Enabled=!busy && state=="Active";
        resume.Enabled=!busy && state is ("Paused" or "Held") && Convert.ToInt64(row!["UncertainAttempts"])==0;
        cancel.Enabled=!busy && state is ("Active" or "Paused" or "Held");
    }
    private async Task RunAsync(Func<Task> action)
    {
        if(busy)return;busy=true;Selection();
        try
        {
            Guard();await action();Guard();
            var rows=await CommunicationPlannedWorkStore.ReadRulesAsync();Guard();
            var old=grid.DataSource as DataTable;grid.DataSource=rows;old?.Dispose();
            if(focusRule.HasValue)foreach(DataGridViewRow entry in grid.Rows)
                if(entry.DataBoundItem is DataRowView item && (Guid)item["RuleId"]==focusRule.Value){grid.CurrentCell=entry.Cells[0];break;}
            status.Text="Contacts are checked for each occurrence. In-flight messages cannot be recalled.";
        }
        catch(Exception ex){status.Text=DbMessage.UserMessage(ex,"The schedule could not be updated. Reopen it to check the saved state.");}
        finally{busy=false;if(!IsDisposed)Selection();}
    }
    private void Guard()
    {
        if(actor!=CurrentUser.UserId || !CurrentUser.HasPermission("Communication.Send"))
            throw new UnauthorizedAccessException("The signed-in sender changed. Close this window.");
    }
}
