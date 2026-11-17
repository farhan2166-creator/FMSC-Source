using System.Data;
using FMSC_SMS.Core;

namespace FMSC_SMS.Pages.Communication;

/// <summary>One explicit, bounded paid-channel approval per school event.</summary>
internal sealed class CommunicationAutomaticRuleDialog : ApplicationWindowForm
{
    private sealed record TemplateChoice(int Id,string Name,string Body){public override string ToString()=>Name;}
    private readonly string code;
    private readonly int actor=CurrentUser.UserId;
    private readonly ModernComboBox method=HumanUi.Combo(),language=HumanUi.Combo(),template=HumanUi.Combo();
    private readonly ModernTextBox preview=HumanUi.TextBox(true,1600);
    private readonly NumericUpDown daily=new(){Minimum=1,Maximum=20,Value=3},expiry=new(){Minimum=1,Maximum=168,Value=24};
    private readonly CheckBox paid=new(){Text="Allow automatic provider charges for this event within the school limits.",AutoSize=true};
    private readonly ModernButton save=new(){Text="Authorize Automatic",ThemeRole="Primary"},chooseMeta=new(){Text="Choose Approved Meta Template…",ThemeRole="Secondary"};
    private readonly Label status=new(){Dock=DockStyle.Fill,AutoEllipsis=true};
    private DataTable? schoolTemplates;
    private CommunicationMetaTransport.Selection? meta;
    private Guid request=Guid.NewGuid();
    private bool busy;
    private int campusId,classId;
    private readonly Label scopeLabel=new(){Dock=DockStyle.Fill,AutoEllipsis=true};
    internal bool Changed{get;private set;}
    private string Language=>language.SelectedIndex==1 ? "ur" : "en";
    private string Channel=>method.SelectedIndex==1 ? "WhatsApp" : "Sms";
    private string Provider=>method.SelectedIndex==1 ? "MetaCloud" : "Twilio";
    internal CommunicationAutomaticRuleDialog(string eventCode)
    {
        if(!CurrentUser.IsAdministrator || !CurrentUser.HasPermission("Communication.Configure"))throw new UnauthorizedAccessException();
        code=eventCode;Text="Automatic · "+CommunicationAutomationInboxStore.EventLabel(code);StartPosition=FormStartPosition.CenterParent;ShowInTaskbar=false;
        ClientSize=new Size(AppScale.Scale(770),AppScale.Scale(680));MinimumSize=new Size(AppScale.Scale(680),AppScale.Scale(590));
        method.Items.AddRange(new object[]{"SMS · Twilio","WhatsApp · Meta API"});method.SelectedIndex=0;
        language.Items.AddRange(new object[]{"English","اردو / Urdu"});language.SelectedIndex=0;
        preview.ReadOnly=true;preview.ScrollBars=ScrollBars.Vertical;
        if(CommunicationAutomaticMessages.IsFeeEvent(code))daily.Value=1;
        if(code=="PaymentReceived")daily.Value=10;
        var close=new ModernButton{Text="Cancel",DialogResult=DialogResult.Cancel};CancelButton=close;
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=8,Padding=new Padding(AppScale.Scale(16))};
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        foreach(var height in new[]{AppScale.Scale(80),AppMetrics.FieldRowHeight,AppMetrics.FieldRowHeight,AppMetrics.ButtonHeight+AppScale.Scale(8),0,AppMetrics.FieldRowHeight,AppScale.Scale(65),AppMetrics.ButtonHeight+AppScale.Scale(12)})
            layout.RowStyles.Add(height==0 ? new RowStyle(SizeType.Percent,100) : new RowStyle(SizeType.Absolute,height));
        var intro=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2};
        intro.RowStyles.Add(new RowStyle(SizeType.Absolute,AppScale.Scale(24)));intro.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        intro.Controls.Add(scopeLabel,0,0);
        intro.Controls.Add(new Label{Dock=DockStyle.Fill,Text="Only future qualifying admissions/payments are discovered; old transactions are not replayed. Current unpaid fees and today's birthdays may qualify. Consent, family, current scope, school hours, expiry and allowances are checked again before sending. Manual WhatsApp is never automatic."},0,1);
        layout.Controls.Add(intro,0,0);
        var options=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2};options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,60));options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,40));
        options.Controls.Add(HumanUi.Field("Method",method,true),0,0);options.Controls.Add(HumanUi.Field("Language",language,true),1,0);
        layout.Controls.Add(options,0,1);layout.Controls.Add(HumanUi.Field("Approved school format",template,true),0,2);layout.Controls.Add(chooseMeta,0,3);
        layout.Controls.Add(preview,0,4);
        var bounds=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2};bounds.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));bounds.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));
        bounds.Controls.Add(HumanUi.Field("Maximum per family / school day",daily,true),0,0);bounds.Controls.Add(HumanUi.Field("Expire after (hours)",expiry,true),1,0);layout.Controls.Add(bounds,0,5);
        var consent=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2};consent.RowStyles.Add(new RowStyle(SizeType.Absolute,AppScale.Scale(27)));consent.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        consent.Controls.Add(paid,0,0);consent.Controls.Add(status,0,1);layout.Controls.Add(consent,0,6);layout.Controls.Add(HumanUi.DialogButtons(save,close),0,7);Controls.Add(layout);
        void ChangedInput(){request=Guid.NewGuid();paid.Checked=false;}
        method.SelectedIndexChanged+=(_,_)=>{meta=null;ChangedInput();RefreshChoices();};
        language.SelectedIndexChanged+=(_,_)=>{meta=null;ChangedInput();RefreshChoices();};
        template.SelectedIndexChanged+=(_,_)=>{ChangedInput();if(method.SelectedIndex==0)preview.Text=(template.SelectedItem as TemplateChoice)?.Body??"";};
        daily.ValueChanged+=(_,_)=>ChangedInput();expiry.ValueChanged+=(_,_)=>ChangedInput();
        chooseMeta.Click+=async(_,_)=>await RunAsync(async()=>{
            var choices=await CommunicationApiBatchStore.ReadMetaTemplatesAsync();Guard();
            IReadOnlyDictionary<string,string>? context=code=="PaymentReceived" ? new Dictionary<string,string>{
                ["Payment details"]="[PaymentDetails]",["Payment amount"]="[PaymentAmount]",["Credit applied"]="[CreditApplied]",["Receipt number"]="[ReceiptNumber]"}:null;
            using var dialog=new CommunicationMetaTemplateDialog(choices.Where(item=>item.Language==Language || item.Language.StartsWith(Language+"_",StringComparison.Ordinal)).ToArray(),
                CommunicationAutomaticMessages.IsFeeEvent(code) ? "FeeReminder" : "Individual",allowStudentFields:!CommunicationAutomaticMessages.IsFeeEvent(code),contextFields:context,allowEventContext:true);
            if(dialog.ShowDialog(this)==DialogResult.OK && dialog.Selection is { } selection)
            {meta=selection;preview.Text=selection.Format(true);ChangedInput();}
        });
        save.Click+=async(_,_)=>await RunAsync(async()=>{
            if(!paid.Checked)throw new CommunicationValidationException("Review the format and explicitly allow this event's provider charges.");
            if(Channel=="WhatsApp" && meta is null)throw new CommunicationValidationException("Choose an approved Meta template.");
            string body=preview.Text;CommunicationAutomaticMessages.Validate(code,body);
            string cost=Channel=="Sms" ? "Long or Urdu SMS can use multiple separately billed segments." : "Meta API charges may apply. Acceptance is not delivery.";
            if(ModernMessageDialog.Show(this,"Enable this automatic event? "+cost+"\nMaximum "+daily.Value+" per family per school day; school provider limits and pauses still apply.",
                Text,MessageBoxButtons.YesNo,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2)!=DialogResult.Yes)return;
            await CommunicationAutomaticMessages.SaveAsync(request,code,Channel=="Sms" ? (template.SelectedItem as TemplateChoice)?.Id ?? 0 : 0,
                body,Language,Channel,Provider,meta,(int)daily.Value,(int)expiry.Value,campusId,classId);Guard();Changed=true;
        });
        Shown+=async(_,_)=>await RunAsync(async()=>{
            schoolTemplates=await CommunicationManagementStore.ReadTemplatesAsync();Guard();
            using var scope=await DatabaseHelper.QueryUserAsync("msg.ReadAutomaticScope",DatabaseHelper.P("@EventCode",code));Guard();
            if(scope.Rows.Count!=1)throw new CommunicationValidationException("The event scope is unavailable.");
            campusId=Convert.ToInt32(scope.Rows[0]["CampusId"]);classId=Convert.ToInt32(scope.Rows[0]["ClassId"]);scopeLabel.Text="Audience: "+Convert.ToString(scope.Rows[0]["ScopeLabel"]);
            using var preferred=await CommunicationManagementStore.ReadMessageLanguageAsync();Guard();
            if(preferred.Rows.Count==1)language.SelectedIndex=Convert.ToString(preferred.Rows[0]["DefaultMessageLanguage"])=="ur" ? 1 : 0;
            RefreshChoices();
        });
        FormClosing+=(_,e)=>{if(busy)e.Cancel=true;};Disposed+=(_,_)=>schoolTemplates?.Dispose();
    }
    private void RefreshChoices()
    {
        template.Items.Clear();
        string purpose=CommunicationAutomaticMessages.IsFeeEvent(code) ? "FeeReminder" : "Individual";
        template.Items.Add(new TemplateChoice(0,"Suggested school format",CommunicationAutomaticMessages.DefaultBody(code,Language)));
        if(schoolTemplates is not null)foreach(DataRow row in schoolTemplates.Rows)
            if(Convert.ToBoolean(row["Active"]) && Convert.ToString(row["Channel"])==Channel && Convert.ToString(row["LanguageCode"])==Language && Convert.ToString(row["Purpose"])==purpose)
                template.Items.Add(new TemplateChoice(Convert.ToInt32(row["TemplateId"]),Convert.ToString(row["Name"])!,Convert.ToString(row["Body"])!));
        template.SelectedIndex=0;template.Enabled=method.SelectedIndex==0;chooseMeta.Visible=method.SelectedIndex==1;
        preview.Text=method.SelectedIndex==0 ? (template.SelectedItem as TemplateChoice)?.Body??"" : meta?.Format(true)??"";
        status.Text=Channel=="Sms" ? "Long/Urdu messages may cost several SMS segments. Payment and receipt fields resolve from the actual posted payment." : "Only a provider-approved template in the chosen language can be used.";
    }
    private void Guard(){if(actor!=CurrentUser.UserId || !CurrentUser.IsAdministrator)throw new UnauthorizedAccessException("The authorizing administrator changed.");}
    private async Task RunAsync(Func<Task> action)
    {
        if(busy)return;busy=true;save.Enabled=chooseMeta.Enabled=method.Enabled=language.Enabled=template.Enabled=daily.Enabled=expiry.Enabled=paid.Enabled=false;
        try{Guard();await action();}
        catch(Exception ex){status.Text=DbMessage.UserMessage(ex,"Automatic authorization was not confirmed. Refresh Settings to check its saved state.");}
        finally{busy=false;if(!IsDisposed){save.Enabled=chooseMeta.Enabled=method.Enabled=language.Enabled=daily.Enabled=expiry.Enabled=paid.Enabled=true;template.Enabled=method.SelectedIndex==0;if(Changed){DialogResult=DialogResult.OK;Close();}}}
    }
}
