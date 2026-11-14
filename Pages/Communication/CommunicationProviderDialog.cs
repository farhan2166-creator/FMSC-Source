using FMSC_SMS.Core;

namespace FMSC_SMS.Pages.Communication;

/// <summary>Saving protected credentials pauses the channel. Verification and
/// activation remain a separate administrator decision; secrets are never read back into controls.</summary>
internal sealed class CommunicationProviderDialog : ApplicationWindowForm
{
    private readonly ModernTextBox account=HumanUi.TextBox(false,128);
    private readonly ModernTextBox sender=HumanUi.TextBox(false,128);
    private readonly ModernTextBox token=HumanUi.TextBox(false,4096);
    private readonly ModernTextBox version=HumanUi.TextBox(false,8);
    private CommunicationProviderCredentialStore.Credential? selectedProfile;
    internal CommunicationProviderCredentialStore.Credential Profile => selectedProfile
        ?? throw new InvalidOperationException("No provider profile was confirmed.");
    internal CommunicationProviderDialog(string selectedChannel)
    {
        bool meta = selectedChannel == "WhatsApp";
        Text = meta ? "Configure WhatsApp · Meta API" : "Configure SMS · Twilio";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=MinimizeBox=false;ShowInTaskbar=false;
        BackColor=AppTheme.PageBackground;
        token.UseSystemPasswordChar=true;version.Text="v23.0";
        account.PlaceholderText = meta ? "Digits from the Meta account" : "AC followed by 32 hexadecimal characters";
        sender.PlaceholderText = meta ? "Numeric ID from Meta" : "+ followed by the SMS-capable number";
        var accountField=HumanUi.Field(meta ? "WhatsApp Business Account ID" : "Account SID",account,true);
        var senderField=HumanUi.Field(meta ? "Phone Number ID" : "Twilio phone number",sender,true);
        var tokenField = HumanUi.Field(meta ? "Access Token" : "Auth Token", token, true);
        var versionField=HumanUi.Field("Graph API version",version,true);
        var hint = new Label
        {
            Dock = DockStyle.Fill, ForeColor = AppTheme.MutedText, UseMnemonic = false,
            Text = (meta ? "Use a token with WhatsApp management and messaging permissions. Sending uses approved Meta templates. "
                : "Provider charges may apply when sending. ")
                + "Credentials are encrypted for this Windows user. Saving pauses sending until you verify and enable the method in Settings."
        };
        var save=new ModernButton {Text="Save Profile",ThemeRole="Success",Width=AppScale.Scale(165),Height=AppMetrics.ButtonHeight};
        var cancel=new ModernButton {Text="Cancel",ThemeRole="Secondary",DialogResult=DialogResult.Cancel,Height=AppMetrics.ButtonHeight};
        save.Click+=(_,_)=>{
            try
            {
                var c=new CommunicationProviderCredentialStore.Credential(1,meta ? "WhatsApp" : "Sms",
                    meta ? "MetaCloud" : "Twilio",account.Text.Trim(),sender.Text.Trim(),token.Text,meta ? "" : token.Text)
                    {GraphVersion=meta ? version.Text.Trim() : "v23.0"};
                if(meta) CommunicationMetaTransport.ValidateCredentials(c); else CommunicationTwilioTransport.ValidateCredentials(c);
                selectedProfile=c;token.Clear();DialogResult=DialogResult.OK;Close();
            }
            catch(Exception ex) when(ex is ArgumentException or InvalidOperationException)
            { ModernMessageDialog.Show(this, meta
                ? "Check the WhatsApp Business Account ID, Phone Number ID, Access Token and Graph API version."
                : "Check the Account SID, Twilio phone number and Auth Token.",
                "Provider Configuration",MessageBoxButtons.OK,MessageBoxIcon.Warning); }
        };
        int fieldCount = meta ? 4 : 3;
        int hintHeight = AppScale.Scale(meta ? 112 : 90);
        int footerHeight = AppMetrics.ButtonHeight + AppScale.Scale(12);
        int padding = AppScale.Scale(16);
        ClientSize = new Size(AppScale.Scale(565), fieldCount * AppMetrics.FieldRowHeight + hintHeight + footerHeight + 2 * padding);
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = fieldCount + 2, Padding = new Padding(padding)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        for (int i = 0; i < fieldCount; i++)
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.FieldRowHeight));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, hintHeight));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, footerHeight));
        layout.Controls.Add(accountField, 0, 0);
        layout.Controls.Add(senderField, 0, 1);
        layout.Controls.Add(tokenField, 0, 2);
        if (meta) layout.Controls.Add(versionField, 0, 3);
        else versionField.Dispose();
        layout.Controls.Add(hint, 0, fieldCount);
        layout.Controls.Add(HumanUi.DialogButtons(save, cancel), 0, fieldCount + 1);
        Controls.Add(layout);AcceptButton=save;CancelButton=cancel;FormClosed+=(_,_)=>token.Clear();
    }
}
