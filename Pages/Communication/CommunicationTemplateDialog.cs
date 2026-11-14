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
    private sealed record ChannelChoice(string Code, string Label) { public override string ToString() => Label; }
    internal string Channel => (channel.SelectedItem as ChannelChoice)?.Code ?? "WhatsApp";
    private sealed record PurposeChoice(string Code)
    {
        public override string ToString() => PurposeLabel(Code);
    }
    internal string Purpose => (purpose.SelectedItem as PurposeChoice)?.Code ?? "Announcement";
    internal bool Active => active.Checked;

    internal static string PurposeLabel(string code) => code switch
    {
        "FeeReminder" => "Fee reminder",
        "Individual" => "Student or family",
        "Staff" => "Staff member",
        _ => code
    };

    internal CommunicationTemplateDialog(string nameValue="",string channelValue="Sms",
        string purposeValue="Announcement",string bodyValue="",bool isActive=true)
    {
        Text=string.IsNullOrEmpty(nameValue)?"New school message template":"Edit school message template";
        StartPosition=FormStartPosition.CenterParent;
        FormBorderStyle=FormBorderStyle.FixedDialog;
        MaximizeBox=MinimizeBox=false;
        ShowInTaskbar=false;
        ClientSize=new Size(AppScale.Scale(730),AppScale.Scale(600));
        BackColor=AppTheme.PageBackground;
        channel.Items.AddRange(new object[] {
            new ChannelChoice("Sms", "SMS"),
            new ChannelChoice("WhatsApp", "WhatsApp Business (manual)"),
            new ChannelChoice("Email", "Email (unavailable)") });
        purpose.Items.AddRange(new[] { "Announcement", "FeeReminder", "Individual", "Staff" }
            .Select(code => new PurposeChoice(code)).ToArray());
        name.Text=nameValue;body.Text=bodyValue;active.Checked=isActive;
        channel.SelectedItem=channel.Items.Cast<ChannelChoice>().FirstOrDefault(choice => choice.Code==channelValue);
        if(channel.SelectedIndex<0)channel.SelectedIndex=0;
        purpose.SelectedItem = purpose.Items.Cast<PurposeChoice>().FirstOrDefault(choice => choice.Code == purposeValue);
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
                    "Provide a name and a complete message. Use supported prepared fields in [FieldName] format, not {{...}}.",
                    "Message Template",MessageBoxButtons.OK,MessageBoxIcon.Warning);
                return;
            }
            try { CommunicationPreparedMessages.ValidateFormat(Body, Purpose); }
            catch (CommunicationValidationException ex)
            {
                ModernMessageDialog.Show(this, ex.Message, "Message Template", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            DialogResult=DialogResult.OK;
            Close();
        };
        var insert = new ModernButton
        {
            Name = "communicationInsertTemplateField", Text = "Insert Field",
            ThemeRole = "Secondary", Width = AppScale.Scale(132), Height = AppMetrics.ButtonHeight
        };
        var fieldsMenu = new ContextMenuStrip();
        void UpdateFields()
        {
            while (fieldsMenu.Items.Count > 0) fieldsMenu.Items[0].Dispose();
            void AddField(string label, string token) => fieldsMenu.Items.Add(label, null, (_, _) =>
            {
                int start = body.SelectionStart;
                int length = body.SelectionLength;
                if (body.Text.Length - length + token.Length > body.MaxLength) return;
                body.Text = body.Text.Remove(start, length).Insert(start, token);
                body.Focus();
                body.SelectionStart = start + token.Length;
            });
            if (Purpose != "Staff")
            {
                AddField("Student name", "[StudentName]");
                AddField("Admission number", "[AdmissionNumber]");
                AddField("Admission date", "[AdmissionDate]");
            }
            if (Purpose == "FeeReminder") AddField("Outstanding amount", "[OutstandingAmount]");
            insert.Enabled = fieldsMenu.Items.Count > 0;
        }
        purpose.SelectedIndexChanged += (_, _) => UpdateFields();
        insert.Click += (_, _) => fieldsMenu.Show(insert, new Point(0, insert.Height));
        Disposed += (_, _) => fieldsMenu.Dispose();
        UpdateFields();
        var options = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty
        };
        active.Margin = new Padding(AppScale.Scale(12), AppScale.Scale(10), 0, 0);
        options.Controls.Add(insert);
        options.Controls.Add(active);
        var layout=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=1,RowCount=6,
            Padding=new Padding(16) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        for(int i=0;i<3;i++)layout.RowStyles.Add(new RowStyle(SizeType.Absolute,AppMetrics.FieldRowHeight));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,AppMetrics.ButtonHeight + AppScale.Scale(8)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,AppScale.Scale(57)));
        layout.Controls.Add(HumanUi.Field("Template name",name,true),0,0);
        layout.Controls.Add(HumanUi.Field("Channel",channel,true),0,1);
        layout.Controls.Add(HumanUi.Field("Purpose",purpose,true),0,2);
        layout.Controls.Add(HumanUi.Field("Message",body,true),0,3);
        layout.Controls.Add(options,0,4);
        layout.Controls.Add(HumanUi.DialogButtons(save,cancel),0,5);
        Controls.Add(layout);
        AcceptButton=save;CancelButton=cancel;
    }
}
