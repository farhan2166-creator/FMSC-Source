using FMSC_SMS.Core;

namespace FMSC_SMS.Pages.Communication;

/// <summary>
/// One administrator-only provider enrollment dialog. No secrets are ever
/// displayed from the vault. Saving a profile never activates a channel.
/// </summary>
internal sealed class CommunicationProviderDialog : ApplicationWindowForm
{
    private readonly ModernComboBox channel=HumanUi.Combo();
    private readonly ModernTextBox account=HumanUi.TextBox(false,60);
    private readonly ModernTextBox sender=HumanUi.TextBox(false,32);
    private readonly ModernTextBox token=HumanUi.TextBox(false,128);

    private CommunicationProviderCredentialStore.Credential? selectedProfile;
    internal CommunicationProviderCredentialStore.Credential Profile =>
        selectedProfile ?? throw new InvalidOperationException("No provider profile was confirmed.");

    internal CommunicationProviderDialog(string selectedChannel="Sms")
    {
        Text="Configure school messaging provider";
        StartPosition=FormStartPosition.CenterParent;
        FormBorderStyle=FormBorderStyle.FixedDialog;
        MaximizeBox=MinimizeBox=false;ShowInTaskbar=false;
        ClientSize=new Size(AppScale.Scale(660),AppScale.Scale(435));
        MinimumSize=ClientSize;
        BackColor=AppTheme.PageBackground;
        channel.Items.AddRange(new object[]{"Sms","WhatsApp"});
        channel.SelectedItem=selectedChannel;
        if(channel.SelectedIndex<0)channel.SelectedIndex=0;
        account.PlaceholderText="AC + 32 hexadecimal characters";
        sender.PlaceholderText="+923... for SMS; whatsapp:+... for WhatsApp";
        token.UseSystemPasswordChar=true;
        token.PlaceholderText="Twilio account Auth Token";
        var save=new ModernButton {Text="Save Protected Profile",ThemeRole="Success",
            Width=AppScale.Scale(195),Height=AppMetrics.ButtonHeight};
        var cancel=new ModernButton {Text="Cancel",ThemeRole="Secondary",
            DialogResult=DialogResult.Cancel,Height=AppMetrics.ButtonHeight};
        var hint=new Label {Dock=DockStyle.Fill,AutoEllipsis=true,
            ForeColor=AppTheme.MutedText,
            Text="Credentials are encrypted for this Windows user. The channel remains paused. No message will be sent."};
        save.Click+=(_,_)=>{
            try
            {
                var proposed = new CommunicationProviderCredentialStore.Credential(
                    1,channel.Text,"Twilio",account.Text.Trim(),sender.Text.Trim(),
                    token.Text,token.Text);
                CommunicationTwilioTransport.ValidateCredentials(proposed);
                selectedProfile=proposed;
                token.Clear();
                DialogResult=DialogResult.OK;Close();
            }
            catch(ArgumentException) { ShowInvalid(); }
            catch(InvalidOperationException) { ShowInvalid(); }
        };
        void ShowInvalid() => ModernMessageDialog.Show(this,
            "Enter a valid Twilio account SID, sender and account Auth Token. WhatsApp senders require the whatsapp: prefix.",
            "Provider Configuration",MessageBoxButtons.OK,MessageBoxIcon.Warning);
        var layout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=6,
            Padding=new Padding(16)};
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        for(int i=0;i<4;i++)layout.RowStyles.Add(new RowStyle(SizeType.Absolute,AppMetrics.FieldRowHeight));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,AppScale.Scale(55)));
        layout.Controls.Add(HumanUi.Field("Channel",channel,true),0,0);
        layout.Controls.Add(HumanUi.Field("Twilio Account SID",account,true),0,1);
        layout.Controls.Add(HumanUi.Field("Approved sender",sender,true),0,2);
        layout.Controls.Add(HumanUi.Field("Twilio Auth Token",token,true),0,3);
        layout.Controls.Add(hint,0,4);
        layout.Controls.Add(HumanUi.DialogButtons(save,cancel),0,5);
        Controls.Add(layout);AcceptButton=save;CancelButton=cancel;
        FormClosed+=(_,_)=>token.Clear();
    }
}
