using FMSC_SMS.Core;

namespace FMSC_SMS.Pages.Communication;

/// <summary>Reason capture only: never changes or sends a saved message.</summary>
internal sealed class CommunicationCorrectionReasonDialog : ApplicationWindowForm
{
    private readonly ModernTextBox reason=HumanUi.TextBox(true,200);
    private readonly ModernButton approve=new()
    {
        Name="communicationSaveCorrectionReason",Text="Prepare Correction",
        ThemeRole="Primary",Enabled=false
    };
    internal string Reason => reason.Text.Trim();
    internal CommunicationCorrectionReasonDialog()
    {
        if (!CurrentUser.HasPermission("Communication.Send") || !CurrentUser.HasPermission("Communication.View"))
            throw new UnauthorizedAccessException("Corrections require sending and History permissions.");
        Text="Prepare a Correction";
        StartPosition=FormStartPosition.CenterParent;
        ClientSize=new Size(AppScale.Scale(520),AppScale.Scale(250));
        MinimumSize=new Size(AppScale.Scale(440),AppScale.Scale(230));
        MaximizeBox=MinimizeBox=ShowInTaskbar=false;
        BackColor=AppTheme.PageBackground;
        reason.Name="communicationCorrectionReason";
        reason.PlaceholderText="What was wrong with the earlier message?";
        reason.TextChanged+=(_,_) => approve.Enabled=Reason.Length is >=8 and <=200;
        var close=new ModernButton { Text="Cancel",DialogResult=DialogResult.Cancel };
        CancelButton=close;
        approve.Click+=(_,_) => { if(approve.Enabled)DialogResult=DialogResult.OK; };
        var buttons=HumanUi.DialogButtons(approve,close);
        buttons.Dock=DockStyle.Fill;
        var layout=new TableLayoutPanel
        {
            Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,
            Padding=new Padding(AppScale.Scale(14))
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,AppScale.Scale(52)));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,AppMetrics.ButtonHeight+AppScale.Scale(8)));
        layout.Controls.Add(new Label
        {
            Dock=DockStyle.Fill,ForeColor=AppTheme.MutedText,
            Text="Explain the correction. The original History remains unchanged; the new message still requires recipient review and confirmation."
        },0,0);
        layout.Controls.Add(HumanUi.Field("Reason for correction",reason),0,1);
        layout.Controls.Add(buttons,0,2);
        Controls.Add(layout);
    }
}
