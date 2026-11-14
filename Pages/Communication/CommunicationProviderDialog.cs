using FMSC_SMS.Core;

namespace FMSC_SMS.Pages.Communication;

/// <summary>Saving protected credentials pauses the channel. Verification and
/// activation remain a separate administrator decision; secrets are never read back into controls.</summary>
internal sealed class CommunicationProviderDialog : ApplicationWindowForm
{
    private readonly ModernComboBox channel=HumanUi.Combo();
    private readonly ModernTextBox account=HumanUi.TextBox(false,128);
    private readonly ModernTextBox sender=HumanUi.TextBox(false,128);
    private readonly ModernTextBox token=HumanUi.TextBox(false,4096);
    private readonly ModernTextBox version=HumanUi.TextBox(false,8);
    private CommunicationProviderCredentialStore.Credential? selectedProfile;
    internal CommunicationProviderCredentialStore.Credential Profile => selectedProfile
        ?? throw new InvalidOperationException("No provider profile was confirmed.");
    internal CommunicationProviderDialog(string selectedChannel="Sms")
    {
        Text="Messaging provider";StartPosition=FormStartPosition.CenterParent;
        FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=MinimizeBox=false;ShowInTaskbar=false;
        ClientSize=new Size(AppScale.Scale(680),AppScale.Scale(535));MinimumSize=ClientSize;
        BackColor=AppTheme.PageBackground;
        channel.Items.AddRange(new object[]{"SMS · Twilio","WhatsApp · Meta API"});
        channel.SelectedIndex=selectedChannel=="WhatsApp" ? 1 : 0;
        token.UseSystemPasswordChar=true;version.Text="v23.0";
        var accountField=HumanUi.Field("Account ID",account,true);
        var senderField=HumanUi.Field("Sender ID",sender,true);
        var versionField=HumanUi.Field("Graph API version",version,true);
        var hint=new Label {Dock=DockStyle.Fill,ForeColor=AppTheme.MutedText,
            Text="Credentials are encrypted for this Windows user. Save leaves sending paused. Use Verify & Enable in Settings when ready. API messaging may incur provider charges."};
        void UpdateFields()
        {
            bool meta=channel.SelectedIndex==1;
            account.PlaceholderText=meta ? "WhatsApp Business Account ID (digits)" : "Twilio Account SID: AC + 32 hexadecimal characters";
            sender.PlaceholderText=meta ? "Meta Phone Number ID (digits, not the phone number)" : "SMS-capable Twilio number: +...";
            token.PlaceholderText=meta ? "Meta access token with WhatsApp permissions" : "Twilio account Auth Token";
            versionField.Visible=meta;
            hint.Text=meta
                ? "Use a Meta token with whatsapp_business_management and whatsapp_business_messaging permissions. Approved text templates are required. Credentials stay encrypted for this Windows user; saving leaves sending paused."
                : "Use an SMS-capable Twilio number and account Auth Token. Credentials stay encrypted for this Windows user. Saving leaves sending paused; Verify & Enable when ready. Provider charges may apply.";
        }
        channel.SelectedIndexChanged+=(_,_)=>UpdateFields();UpdateFields();
        var save=new ModernButton {Text="Save Protected Profile",ThemeRole="Success",Width=AppScale.Scale(210),Height=AppMetrics.ButtonHeight};
        var cancel=new ModernButton {Text="Cancel",ThemeRole="Secondary",DialogResult=DialogResult.Cancel,Height=AppMetrics.ButtonHeight};
        save.Click+=(_,_)=>{
            try
            {
                bool meta=channel.SelectedIndex==1;
                var c=new CommunicationProviderCredentialStore.Credential(1,meta ? "WhatsApp" : "Sms",
                    meta ? "MetaCloud" : "Twilio",account.Text.Trim(),sender.Text.Trim(),token.Text,meta ? "" : token.Text)
                    {GraphVersion=version.Text.Trim()};
                if(meta) CommunicationMetaTransport.ValidateCredentials(c); else CommunicationTwilioTransport.ValidateCredentials(c);
                selectedProfile=c;token.Clear();DialogResult=DialogResult.OK;Close();
            }
            catch(Exception ex) when(ex is ArgumentException or InvalidOperationException)
            { ModernMessageDialog.Show(this,"Check the account ID, sender ID and token. Meta also requires a valid Graph API version (for example v23.0).",
                "Provider Configuration",MessageBoxButtons.OK,MessageBoxIcon.Warning); }
        };
        var layout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=7,Padding=new Padding(16)};
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        for(int i=0;i<5;i++)layout.RowStyles.Add(new RowStyle(SizeType.Absolute,AppMetrics.FieldRowHeight));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,AppScale.Scale(57)));
        layout.Controls.Add(HumanUi.Field("Send using",channel,true),0,0);
        layout.Controls.Add(accountField,0,1);layout.Controls.Add(senderField,0,2);
        layout.Controls.Add(HumanUi.Field("Access token",token,true),0,3);layout.Controls.Add(versionField,0,4);
        layout.Controls.Add(hint,0,5);layout.Controls.Add(HumanUi.DialogButtons(save,cancel),0,6);
        Controls.Add(layout);AcceptButton=save;CancelButton=cancel;FormClosed+=(_,_)=>token.Clear();
    }
}
