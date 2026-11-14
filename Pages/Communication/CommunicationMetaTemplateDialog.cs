using FMSC_SMS.Core;

namespace FMSC_SMS.Pages.Communication;

/// <summary>Map approved Meta fields explicitly rather than guessing what
/// {{1}} means. The rendered school message remains reviewable before sending.</summary>
internal sealed class CommunicationMetaTemplateDialog : ApplicationWindowForm
{
    private readonly ModernComboBox choice=HumanUi.Combo();
    private readonly ModernTextBox source=HumanUi.TextBox(true,4000);
    private readonly TableLayoutPanel fields=new() {Dock=DockStyle.Top,AutoSize=true,ColumnCount=1};
    private readonly List<ModernComboBox> mappings=new();
    internal CommunicationMetaTransport.Selection? Selection {get;private set;}
    internal CommunicationMetaTemplateDialog(IReadOnlyList<CommunicationMetaTransport.Template> templates,string purpose)
    {
        Text="Approved Meta template";StartPosition=FormStartPosition.CenterParent;ShowInTaskbar=false;
        ClientSize=new Size(AppScale.Scale(710),AppScale.Scale(650));MinimumSize=new Size(AppScale.Scale(590),AppScale.Scale(490));
        BackColor=AppTheme.PageBackground;
        source.ReadOnly=true;source.ScrollBars=ScrollBars.Vertical;
        choice.Items.AddRange(templates.Cast<object>().ToArray());
        var scroll=new Panel {Dock=DockStyle.Fill,AutoScroll=true};scroll.Controls.Add(fields);
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        choice.SelectedIndexChanged+=(_,_)=>{
            foreach(Control control in fields.Controls.Cast<Control>().ToArray()) control.Dispose();
            fields.Controls.Clear();fields.RowStyles.Clear();mappings.Clear();
            if(choice.SelectedItem is not CommunicationMetaTransport.Template selected) return;
            source.Text=string.Join(Environment.NewLine,new[]{selected.Header,selected.Body,selected.Footer}.Where(s=>s.Length>0));
            fields.RowCount=selected.ParameterCount;
            for(int i=0;i<selected.ParameterCount;i++)
            {
                var input=HumanUi.Combo();input.DropDownStyle=ComboBoxStyle.DropDown;
                input.Items.AddRange(new object[]{"[StudentName]","[AdmissionNumber]","[AdmissionDate]"});
                if(purpose=="FeeReminder")input.Items.Add("[OutstandingAmount]");
                mappings.Add(input);fields.RowStyles.Add(new RowStyle(SizeType.Absolute,AppMetrics.FieldRowHeight));
                fields.Controls.Add(HumanUi.Field("Field "+(i+1)+" · choose school data or type a value",input,true),0,i);
            }
        };
        if(choice.Items.Count>0)choice.SelectedIndex=0;
        var apply=new ModernButton {Text="Use Template",ThemeRole="Primary",Width=AppScale.Scale(160),Height=AppMetrics.ButtonHeight,Enabled=templates.Count>0};
        var cancel=new ModernButton {Text="Cancel",DialogResult=DialogResult.Cancel,ThemeRole="Secondary",Height=AppMetrics.ButtonHeight};
        apply.Click+=(_,_)=>{
            if(choice.SelectedItem is not CommunicationMetaTransport.Template selected)return;
            try
            {
                var selection=new CommunicationMetaTransport.Selection(selected,mappings.Select(m=>m.Text.Trim()).ToArray());
                CommunicationPreparedMessages.ValidateFormat(selection.Format(),purpose);
                Selection=selection;DialogResult=DialogResult.OK;Close();
            }
            catch(CommunicationValidationException ex) {ModernMessageDialog.Show(this,ex.Message,"Template",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
        };
        var layout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=5,Padding=new Padding(16)};
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,AppScale.Scale(60)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,AppMetrics.FieldRowHeight));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent,45));layout.RowStyles.Add(new RowStyle(SizeType.Percent,55));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,AppScale.Scale(57)));
        layout.Controls.Add(new Label {Dock=DockStyle.Fill,Text=templates.Count==0
            ? "No supported approved templates were found. Create a text template in Meta first. Media, buttons and variable headers are not supported."
            : "Choose an approved text template, then map its fields. FMSC will show the final message for your recipients before sending."},0,0);
        layout.Controls.Add(HumanUi.Field("Approved template / language",choice,true),0,1);
        layout.Controls.Add(source,0,2);layout.Controls.Add(scroll,0,3);layout.Controls.Add(HumanUi.DialogButtons(apply,cancel),0,4);
        Controls.Add(layout);AcceptButton=apply;CancelButton=cancel;
    }
}
