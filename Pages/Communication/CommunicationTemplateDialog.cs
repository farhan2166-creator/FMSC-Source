using FMSC_SMS.Core;

namespace FMSC_SMS.Pages.Communication;

/// <summary>One small administrator-only editor. Never contains provider secrets.</summary>
internal sealed class CommunicationTemplateDialog : ApplicationWindowForm
{
    private readonly ModernTextBox name = HumanUi.TextBox(false,100);
    private readonly ModernTextBox body = HumanUi.TextBox(true,1600);
    private readonly ModernComboBox channel=HumanUi.Combo(),purpose=HumanUi.Combo();
    private readonly CheckBox active=new() { Text="Active",Checked=true,AutoSize=true };

    internal string TemplateName => name.Text.Trim();
    internal string Body => body.Text.Trim();
    internal string Channel => channel.Text;
    internal string Purpose => purpose.Text;
    internal bool Active => active.Checked;

    internal CommunicationTemplateDialog(string nameValue="",string channelValue="Sms",
        string purposeValue="Announcement",string bodyValue="",bool isActive=true)
    {
        Text=string.IsNullOrEmpty(nameValue)?"New school message template":"Edit school message template";
        StartPosition=FormStartPosition.CenterParent;
        FormBorderStyle=FormBorderStyle.FixedDialog;
        MaximizeBox=MinimizeBox=false;
        ShowInTaskbar=false;
        ClientSize=new Size(AppScale.Scale(650),AppScale.Scale(525));
        MinimumSize=ClientSize;
        BackColor=AppTheme.PageBackground;
        channel.Items.AddRange(new object[]{"Sms","WhatsApp"});
        purpose.Items.AddRange(new object[]{"Announcement","FeeReminder","Individual","Staff"});
        name.Text=nameValue;body.Text=bodyValue;active.Checked=isActive;
        channel.SelectedItem=channelValue;
        if(channel.SelectedIndex<0)channel.SelectedIndex=0;
        purpose.SelectedItem=purposeValue;
        if(purpose.SelectedIndex<0)purpose.SelectedIndex=0;

        var save=new ModernButton { Text="Save Template",ThemeRole="Success",
            Width=AppScale.Scale(155),Height=AppMetrics.ButtonHeight };
        var cancel=new ModernButton { Text="Cancel",ThemeRole="Secondary",
            DialogResult=DialogResult.Cancel,
            Width=AppScale.Scale(100),Height=AppMetrics.ButtonHeight };
        save.Click+=(_,_)=>{
            if(string.IsNullOrWhiteSpace(TemplateName) ||
               string.IsNullOrWhiteSpace(Body) ||
               Body.Contains("{{",StringComparison.Ordinal) ||
               Body.Contains("}}",StringComparison.Ordinal))
            {
                ModernMessageDialog.Show(this,
                    "Provide a name and a complete message. Unexpanded placeholders are not allowed.",
                    "Message Template",MessageBoxButtons.OK,MessageBoxIcon.Warning);
                return;
            }
            DialogResult=DialogResult.OK;
            Close();
        };
        var layout=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=1,RowCount=6,
            Padding=new Padding(16) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        for(int i=0;i<3;i++)layout.RowStyles.Add(new RowStyle(SizeType.Absolute,AppMetrics.FieldRowHeight));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,AppScale.Scale(40)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,AppScale.Scale(57)));
        layout.Controls.Add(HumanUi.Field("Template name",name,true),0,0);
        layout.Controls.Add(HumanUi.Field("Channel",channel,true),0,1);
        layout.Controls.Add(HumanUi.Field("Purpose",purpose,true),0,2);
        layout.Controls.Add(HumanUi.Field("Message",body,true),0,3);
        layout.Controls.Add(active,0,4);
        layout.Controls.Add(HumanUi.DialogButtons(save,cancel),0,5);
        Controls.Add(layout);
        AcceptButton=save;CancelButton=cancel;
    }
}
