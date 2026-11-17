using System.Data;
using FMSC_SMS.Core;

namespace FMSC_SMS.Pages.Communication;

/// <summary>
/// Approves immutable source notices; this dialog never creates a batch,
/// transmits a message, or invents examination marks/results.
/// </summary>
internal sealed class CommunicationSchoolNoticeDialog : ApplicationWindowForm
{
    private readonly ModernComboBox kind=HumanUi.Combo(),language=HumanUi.Combo();
    private readonly ModernTextBox heading=HumanUi.TextBox(false,120),
        noticeText=HumanUi.TextBox(true,1000),sourceReference=HumanUi.TextBox(false,160);
    private readonly NumericUpDown expiryHours=new() {Minimum=1,Maximum=720,Value=24,Dock=DockStyle.Fill};
    private readonly Label audience=new() {Dock=DockStyle.Fill,AutoEllipsis=true},
        feedback=new() {Dock=DockStyle.Fill,AutoEllipsis=true,ForeColor=AppTheme.MutedText};
    private readonly DataGridView grid=HumanUi.Grid(false);
    private readonly ModernButton approve=new()
    {
        Name="communicationApproveSchoolNotice",Text="Approve School Notice",ThemeRole="Primary",
        Width=AppScale.Scale(210)
    };
    private readonly ModernButton revoke=new()
    {
        Name="communicationRevokeSchoolNotice",Text="Revoke Selected",Width=AppScale.Scale(170),
        Enabled=false
    };
    private bool busy;
    private int actor,campusId,classId;
    private string currentMode="Off";
    private DataTable? results;
    internal bool Changed {get;private set;}

    private string Code=>kind.SelectedIndex==1 ? "ExaminationResult" : "Announcement";
    private string Language=>language.SelectedIndex==1 ? "ur" : "en";

    internal CommunicationSchoolNoticeDialog()
    {
        if(!CurrentUser.IsAdministrator || !CurrentUser.HasPermission("Communication.Configure")
            || !CurrentUser.HasPermission("Communication.Send") || !CurrentUser.HasPermission("Students.View"))
            throw new UnauthorizedAccessException("Only a school communication administrator can approve notices.");
        actor=CurrentUser.UserId;
        Text="School Notices";StartPosition=FormStartPosition.CenterParent;
        ShowInTaskbar=MaximizeBox=MinimizeBox=false;BackColor=AppTheme.PageBackground;
        ClientSize=new Size(AppScale.Scale(850),AppScale.Scale(700));
        MinimumSize=new Size(AppScale.Scale(690),AppScale.Scale(590));
        kind.Items.AddRange(new object[]{"School announcement","Examination / result notice"});kind.SelectedIndex=0;
        language.Items.AddRange(new object[]{"English","اردو / Urdu"});language.SelectedIndex=0;
        heading.PlaceholderText="Short title of this official notice";
        noticeText.PlaceholderText="Exact notice approved by the school";
        sourceReference.PlaceholderText="Official circular / examination reference";
        noticeText.ScrollBars=ScrollBars.Vertical;
        audience.ForeColor=AppTheme.MutedText;
        grid.Name="communicationSchoolNoticesGrid";
        grid.ReadOnly=true;grid.MultiSelect=false;grid.SelectionMode=DataGridViewSelectionMode.FullRowSelect;
        var root=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=9,
            Padding=new Padding(AppScale.Scale(12)),Margin=Padding.Empty};
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        foreach(int fixedHeight in new[]{44,52,48,48,132,48,48,26})
            root.RowStyles.Add(new RowStyle(SizeType.Absolute,AppScale.Scale(fixedHeight)));
        root.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        root.Controls.Add(new Label{Dock=DockStyle.Fill,AutoEllipsis=true,ForeColor=AppTheme.MutedText,
            Text="Approve a real school-issued notice, then use Event Suggestions or separately authorized API automation. This does not generate examination results or send messages."},0,0);
        var header=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2};
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,55));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,45));
        header.Controls.Add(HumanUi.Field("Notice type",kind,true),0,0);
        header.Controls.Add(HumanUi.Field("Original language",language,true),1,0);
        root.Controls.Add(header,0,1);
        root.Controls.Add(audience,0,2);
        root.Controls.Add(HumanUi.Field("Notice title",heading,true),0,3);
        root.Controls.Add(HumanUi.Field("Official notice text",noticeText,true),0,4);
        var options=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2};
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,67));
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,33));
        options.Controls.Add(HumanUi.Field("Source reference (required for exam/result)",sourceReference),0,0);
        options.Controls.Add(HumanUi.Field("Valid for (hours)",expiryHours,true),1,0);
        root.Controls.Add(options,0,5);
        var commands=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false,Margin=Padding.Empty};
        var close=new ModernButton{Text="Close",Width=AppScale.Scale(95)};
        close.Click+=(_,_)=>Close();
        commands.Controls.Add(approve);commands.Controls.Add(revoke);commands.Controls.Add(close);
        root.Controls.Add(commands,0,6);
        root.Controls.Add(feedback,0,7);
        root.Controls.Add(grid,0,8);
        Controls.Add(root);
        grid.CurrentCellChanged+=(_,_)=>UpdateActions();
        kind.SelectedIndexChanged+=async(_,_)=>{UpdateActions();if(IsHandleCreated)await RunAsync(LoadScopeAsync);};
        heading.TextChanged+=(_,_)=>UpdateActions();
        noticeText.TextChanged+=(_,_)=>UpdateActions();
        sourceReference.TextChanged+=(_,_)=>UpdateActions();
        approve.Click+=async(_,_)=>await RunAsync(ApproveAsync);
        revoke.Click+=async(_,_)=>await RunAsync(RevokeAsync);
        Shown+=async(_,_)=>await RunAsync(async()=>{await LoadScopeAsync();await RefreshAsync();});
        FormClosing+=(_,e)=>{if(busy)e.Cancel=true;};
        Disposed+=(_,_)=>results?.Dispose();
    }

    private void Guard()
    {
        if(actor!=CurrentUser.UserId || !CurrentUser.IsAdministrator
            || !CurrentUser.HasPermission("Communication.Configure")
            || !CurrentUser.HasPermission("Communication.Send"))
            throw new UnauthorizedAccessException("The approving administrator changed.");
    }

    private async Task LoadScopeAsync()
    {
        Guard();
        using var read=await DatabaseHelper.QueryUserAsync("msg.ReadAutomaticScope",
            DatabaseHelper.P("@EventCode",Code));
        Guard();
        if(read.Rows.Count!=1)throw new CommunicationValidationException("School notice settings could not be read.");
        var row=read.Rows[0];
        campusId=Convert.ToInt32(row["CampusId"]);classId=Convert.ToInt32(row["ClassId"]);
        currentMode=Convert.ToString(row["Mode"])??"Off";
        audience.Text=currentMode=="Off"
            ? "Enable Prepare for Review for this event in Automation Settings before approving notices."
            : "Approved roster: "+Convert.ToString(row["ScopeLabel"])+" · mode: "+currentMode;
        UpdateActions();
    }

    private async Task RefreshAsync()
    {
        Guard();
        var table=await DatabaseHelper.QueryUserAsync("msg.ReadSchoolNotices");
        if(IsDisposed || actor!=CurrentUser.UserId){table.Dispose();return;}
        var old=results;results=table;grid.DataSource=table;old?.Dispose();
        foreach(DataGridViewColumn col in grid.Columns)
            col.Visible=col.Name is "NoticeId" or "EventCode" or "Title" or "ApprovedAudience" or "ApprovedUtc" or "ExpiresUtc" or "RevokedUtc";
        UpdateActions();
    }

    private void UpdateActions()
    {
        if(IsDisposed)return;
        approve.Enabled=!busy && currentMode!="Off" && heading.Text.Trim().Length>0
            && noticeText.Text.Trim().Length>0 && (Code!="ExaminationResult" || sourceReference.Text.Trim().Length>=5);
        revoke.Enabled=!busy && grid.CurrentRow?.DataBoundItem is DataRowView view
            && view.Row["RevokedUtc"] is DBNull;
    }

    private async Task ApproveAsync()
    {
        Guard();UpdateActions();
        if(currentMode=="Off")throw new CommunicationValidationException("Enable this event's review mode first.");
        string title=heading.Text.Trim(),text=noticeText.Text.Trim(),reference=sourceReference.Text.Trim();
        if(title.Length is <1 or >120 || text.Length is <1 or >1000 ||
            title.Contains('[') || title.Contains(']') || text.Contains('[') || text.Contains(']') ||
            (Code=="ExaminationResult" && reference.Length<5))
            throw new CommunicationValidationException("Complete the official notice and source reference. Placeholder brackets are not allowed in notice text.");
        string warning="Approve this school-issued notice for the current consenting roster in "+
            audience.Text+"?\n\nApproval stores an immutable notice; changes require a new notice. "+
            "If this event has separately authorized automatic API sending, eligible messages may be prepared without another prompt. "+
            "No examination marks or grades are generated.";
        if(ModernMessageDialog.Show(this,warning,"Approve School Notice",MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2)!=DialogResult.Yes)return;
        using var result=await DatabaseHelper.QueryUserAsync("msg.ApproveSchoolNotice",
            DatabaseHelper.P("@EventCode",Code),DatabaseHelper.P("@Title",title),
            DatabaseHelper.P("@NoticeText",text),DatabaseHelper.P("@LanguageCode",Language),
            DatabaseHelper.P("@SourceReference",reference.Length==0 ? null : reference),
            DatabaseHelper.P("@ExpiresUtc",DateTime.UtcNow.AddHours((double)expiryHours.Value)),
            DatabaseHelper.Int("@ExpectedCampusId",campusId),DatabaseHelper.Int("@ExpectedClassId",classId));
        Guard();
        if(result.Rows.Count!=1)throw new InvalidOperationException("School notice approval is not confirmed.");
        Changed=true;heading.Clear();noticeText.Clear();sourceReference.Clear();
        feedback.Text="Notice approved. Current school recipients may appear under Queue → Event Suggestions.";
        await RefreshAsync();
    }

    private async Task RevokeAsync()
    {
        Guard();
        if(grid.CurrentRow?.DataBoundItem is not DataRowView chosen
            || chosen.Row["RevokedUtc"] is not DBNull)return;
        long id=Convert.ToInt64(chosen.Row["NoticeId"]);
        using var reason=new SchoolNoticeRevokeReasonDialog();
        if(reason.ShowDialog(this)!=DialogResult.OK)return;
        await DatabaseHelper.ExecuteUserAsync("msg.RevokeSchoolNotice",
            DatabaseHelper.BigInt("@NoticeId",id),DatabaseHelper.P("@Reason",reason.Reason));
        Guard();Changed=true;
        feedback.Text="Notice revoked. Pending automatic preparation is held; earlier provider submissions remain in History.";
        await RefreshAsync();
    }

    private async Task RunAsync(Func<Task> work)
    {
        if(busy)return;
        busy=true;approve.Enabled=revoke.Enabled=false;
        try{Guard();await work();}
        catch(Exception ex){if(!IsDisposed)feedback.Text=DbMessage.UserMessage(ex,"School notice change was not confirmed. Refresh before retrying.");}
        finally{busy=false;if(!IsDisposed)UpdateActions();}
    }
}

internal sealed class SchoolNoticeRevokeReasonDialog:ApplicationWindowForm
{
    private readonly ModernTextBox input=HumanUi.TextBox(true,200);
    internal string Reason=>input.Text.Trim();
    internal SchoolNoticeRevokeReasonDialog()
    {
        Text="Revoke Approved Notice";StartPosition=FormStartPosition.CenterParent;
        ShowInTaskbar=MaximizeBox=MinimizeBox=false;
        ClientSize=new Size(AppScale.Scale(490),AppScale.Scale(220));
        var accept=new ModernButton{Text="Revoke Notice",ThemeRole="Primary",Enabled=false};
        var cancel=new ModernButton{Text="Cancel",DialogResult=DialogResult.Cancel};
        input.PlaceholderText="Reason recorded in the school audit history";
        input.TextChanged+=(_,_)=>accept.Enabled=Reason.Length is >=8 and <=200;
        accept.Click+=(_,_)=>{if(accept.Enabled)DialogResult=DialogResult.OK;};
        CancelButton=cancel;
        var root=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Padding=new Padding(AppScale.Scale(14))};
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute,AppScale.Scale(48)));
        root.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute,AppMetrics.ButtonHeight+AppScale.Scale(12)));
        root.Controls.Add(new Label{Dock=DockStyle.Fill,Text="Revocation stops future preparation. It cannot withdraw messages already submitted to a provider."},0,0);
        root.Controls.Add(HumanUi.Field("Reason",input,true),0,1);
        root.Controls.Add(HumanUi.DialogButtons(accept,cancel),0,2);
        Controls.Add(root);
    }
}
