using System.Data;
using FMSC_SMS.Core;

namespace FMSC_SMS.Pages.Communication;

/// <summary>
/// Administrator overrides for specific registered school families. Selecting
/// a student resolves their family in SQL; the UI never asks staff for a
/// database family ID or changes Admission consent.
/// </summary>
internal sealed class CommunicationFamilyExceptionsDialog : ApplicationWindowForm
{
    private readonly StudentPicker student=new();
    private readonly ModernComboBox eventCode=HumanUi.Combo();
    private readonly ModernTextBox reason=HumanUi.TextBox(false,200);
    private readonly CheckBox hasExpiry=new()
    {
        Text="Expire this exception on",AutoSize=true,Checked=true
    };
    private readonly DateTimePicker until=new()
    {
        Format=DateTimePickerFormat.Short,
        MinDate=DateTime.Today,Value=DateTime.Today.AddDays(30)
    };
    private readonly DataGridView grid=HumanUi.Grid(false);
    private readonly Label status=new()
    {
        Dock=DockStyle.Fill,AutoSize=false,ForeColor=AppTheme.MutedText,
        // Guidance is already supplied by the field captions; reserve this area for results/errors.
        Text=""
    };
    private readonly ModernButton save=new()
    {
        Name="communicationAddFamilyException",Text="Save Exception",ThemeRole="Primary",
        Width=AppScale.Scale(155)
    };
    private readonly ModernButton revoke=new()
    {
        Name="communicationRevokeFamilyException",Text="Remove Selected",
        ThemeRole="Secondary",Width=AppScale.Scale(165),Enabled=false
    };
    private readonly ModernButton close=new()
    {
        Text="Close",DialogResult=DialogResult.Cancel,Width=AppScale.Scale(95)
    };
    private sealed record EventChoice(string Code,string Label)
    {
        public override string ToString()=>Label;
    }
    private DataTable? rows;
    private bool busy;
    internal bool Changed { get; private set; }

    internal CommunicationFamilyExceptionsDialog()
    {
        Text="Family Communication Exceptions";
        ClientSize=new Size(AppScale.Scale(780),AppScale.Scale(535));
        StartPosition=FormStartPosition.CenterParent;
        ShowInTaskbar=false;
        MinimizeBox=MaximizeBox=false;
        BackColor=AppTheme.PageBackground;
        foreach(var (code,label) in new[]
        {
            ("Admission","Student admitted"),("PaymentReceived","Payment received"),
            ("OutstandingFees","Outstanding fee reminders"),("UpcomingDue","Upcoming fee date"),
            ("OverdueFollowUp","Overdue fee follow-up"),("Announcement","Announcements"),
            ("ExaminationResult","Examinations and results"),("Birthday","Birthday greetings")
        })
            eventCode.Items.Add(new EventChoice(code,label));
        eventCode.SelectedIndex=0;
        student.Name="communicationExceptionStudent";
        eventCode.Name="communicationExceptionEvent";
        reason.Name="communicationExceptionReason";
        grid.Name="communicationFamilyExceptions";
        grid.AutoGenerateColumns=false;
        grid.Dock=DockStyle.Fill;
        grid.SelectionMode=DataGridViewSelectionMode.FullRowSelect;
        grid.MultiSelect=false;
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name="Representative",HeaderText="Family representative",
            DataPropertyName="Representative",MinimumWidth=AppScale.Scale(195),
            AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill,FillWeight=130
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name="EventCode",HeaderText="Event",
            DataPropertyName="EventCode",MinimumWidth=AppScale.Scale(155),
            AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill,FillWeight=90
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name="ExpiryDisplay",HeaderText="Until",
            DataPropertyName="ExpiryDisplay",MinimumWidth=AppScale.Scale(130),
            AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill,FillWeight=90
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name="Reason",HeaderText="Reason",
            DataPropertyName="Reason",MinimumWidth=AppScale.Scale(180),
            AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill,FillWeight=140
        });
        grid.Columns["EventCode"]!.DefaultCellStyle.Alignment=DataGridViewContentAlignment.MiddleCenter;
        grid.Columns["ExpiryDisplay"]!.DefaultCellStyle.Alignment=DataGridViewContentAlignment.MiddleCenter;
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
        var pickerRow=new TableLayoutPanel
        {
            Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=Padding.Empty
        };
        pickerRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,53));
        pickerRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,47));
        pickerRow.Controls.Add(HumanUi.Field("Student in family",student),0,0);
        pickerRow.Controls.Add(HumanUi.Field("Communication event",eventCode),1,0);
        var expiryRow=new FlowLayoutPanel
        {
            Dock=DockStyle.Fill,WrapContents=false,AutoScroll=true
        };
        expiryRow.Controls.Add(hasExpiry);
        expiryRow.Controls.Add(until);
        var actions=HumanUi.DialogButtons(save,revoke,close);
        var layout=new TableLayoutPanel
        {
            Dock=DockStyle.Fill,ColumnCount=1,RowCount=6,
            Padding=new Padding(AppScale.Scale(14))
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,AppScale.Scale(67)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,AppScale.Scale(62)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,AppScale.Scale(56)));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,AppScale.Scale(53)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,AppScale.Scale(45)));
        layout.Controls.Add(pickerRow,0,0);
        layout.Controls.Add(HumanUi.Field("Reason",reason),0,1);
        layout.Controls.Add(expiryRow,0,2);
        layout.Controls.Add(grid,0,3);
        layout.Controls.Add(actions,0,4);
        layout.Controls.Add(status,0,5);
        Controls.Add(layout);

        until.Enabled=hasExpiry.Checked;
        hasExpiry.CheckedChanged+=(_,_)=>until.Enabled=hasExpiry.Checked;
        grid.SelectionChanged+=(_,_)=>revoke.Enabled=!busy &&
            grid.CurrentRow?.DataBoundItem is DataRowView;
        FormClosing+=(_,e)=>e.Cancel|=busy;
        FormClosed+=(_,_)=>rows?.Dispose();
        Shown+=async (_,_)=>await RunAsync(RefreshAsync);

        save.Click+=async (_,_)=>await RunAsync(async ()=>
        {
            if(student.SelectedId<=0)
                throw new ArgumentException("Choose a current student from the registered family.");
            if(eventCode.SelectedItem is not EventChoice selected)
                throw new ArgumentException("Choose a communication event.");
            string why=reason.Text.Trim();
            if(why.Length==0)
                throw new ArgumentException("Explain why the family should be excluded from this event.");
            DateTime? expireAt=hasExpiry.Checked
                ? until.Value.Date.AddDays(1).AddTicks(-1).ToUniversalTime() : null;
            await CommunicationAutomationInboxStore.SaveFamilyExceptionAsync(
                student.SelectedId,selected.Code,expireAt,why);
            Changed=true;
            await RefreshAsync();
            status.Text="Family exception saved. The family's original admission consent is unchanged.";
        });
        revoke.Click+=async (_,_)=>await RunAsync(async ()=>
        {
            if(grid.CurrentRow?.DataBoundItem is not DataRowView selected)return;
            long id=Convert.ToInt64(selected.Row["ExceptionId"]);
            if(ModernMessageDialog.Show(this,
                "Remove the selected family's automation exception?",
                Text,MessageBoxButtons.YesNo,MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2)!=DialogResult.Yes)return;
            await CommunicationAutomationInboxStore.RevokeFamilyExceptionAsync(id);
            Changed=true;
            await RefreshAsync();
            status.Text="Exception removed. Existing admission consent remains unchanged.";
        });
    }

    private async Task RefreshAsync()
    {
        var updated=await CommunicationAutomationInboxStore.ReadFamilyExceptionsAsync();
        if(IsDisposed){updated.Dispose();return;}
        updated.Columns.Add("ExpiryDisplay",typeof(string));
        foreach(DataRow row in updated.Rows)
            row["ExpiryDisplay"]=row.IsNull("UntilUtc")?"Until revoked":
                DateTime.SpecifyKind(Convert.ToDateTime(row["UntilUtc"]),DateTimeKind.Utc)
                    .ToLocalTime().ToString("dd MMM yyyy");
        var old=rows;
        rows=updated;
        grid.DataSource=updated;
        old?.Dispose();
        revoke.Enabled=grid.CurrentRow?.DataBoundItem is DataRowView;
    }

    private async Task RunAsync(Func<Task> action)
    {
        if(busy || IsDisposed)return;
        busy=true;
        save.Enabled=revoke.Enabled=false;
        try
        {
            await action();
            status.ForeColor=AppTheme.MutedText;
        }
        catch(Exception ex)
        {
            if(!IsDisposed)
            {
                status.ForeColor=AppTheme.Danger;
                status.Text=DbMessage.UserMessage(ex);
            }
        }
        finally
        {
            busy=false;
            if(!IsDisposed)
            {
                save.Enabled=true;
                revoke.Enabled=grid.CurrentRow?.DataBoundItem is DataRowView;
            }
        }
    }
}
