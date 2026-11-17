using FMSC_SMS.Core;

namespace FMSC_SMS.Pages.Communication;

/// <summary>
/// Only collects an operator-approved local sending time. The database is
/// authoritative and independently enforces due/expiry at recipient claim.
/// </summary>
internal sealed class CommunicationScheduleDialog : ApplicationWindowForm
{
    private readonly TimeZoneInfo schoolZone;
    private readonly DateTimePicker when = new()
    {
        Name = "communicationScheduleWhen",
        Format = DateTimePickerFormat.Custom,
        CustomFormat = "dd MMM yyyy  hh:mm tt",
    };
    private readonly ModernComboBox priority = HumanUi.Combo();
    private readonly CheckBox expire = new() { Text = "Expire unsent messages after", Checked = true, AutoSize = true };
    private readonly NumericUpDown hours = new() { Minimum = 1, Maximum = 720, Value = 24, Width = AppScale.Scale(90) };
    private readonly ModernComboBox repeat=HumanUi.Combo();
    private readonly ModernComboBox repeatUnit=HumanUi.Combo();
    private readonly NumericUpDown repeatEvery=new(){Minimum=1,Maximum=365,Value=1,Width=AppScale.Scale(70)};
    private readonly NumericUpDown maximum=new(){Minimum=2,Maximum=366,Value=12,Width=AppScale.Scale(75)};
    private readonly DateTimePicker until=new(){Format=DateTimePickerFormat.Custom,CustomFormat="dd MMM yyyy"};
    internal CommunicationRecurrence? Recurrence {get;private set;}
    internal string RepeatDisclosure=>Recurrence is null ? "" :
        $" Repeats every {Recurrence.Every} {Recurrence.Frequency.ToLowerInvariant()} until {until.Value:dd MMM yyyy}, at most {Recurrence.MaximumOccurrences} occurrences. " +
        "Each occurrence resolves the current eligible audience and may incur provider charges. Fee reminder limits still apply.";
    private readonly Label problem = new() { AutoSize = true, ForeColor = AppTheme.Danger };
    internal DateTimeOffset Due { get; private set; }
    internal DateTimeOffset? Expires { get; private set; }
    internal string Priority => priority.SelectedIndex switch { 0 => "High", 2 => "Low", _ => "Normal" };
    internal string SchoolTimeLabel => TimeZoneInfo.ConvertTime(Due, schoolZone).ToString("dd MMM yyyy h:mm tt") + " (" + schoolZone.Id + ")";

    internal CommunicationScheduleDialog(TimeZoneInfo schoolZone)
    {
        this.schoolZone = schoolZone;
        var now = CommunicationSchoolTime.LocalNow(schoolZone);
        when.MinDate = now.Date;
        when.Value = now.AddHours(1);
        Text = "Schedule Reviewed Messages";
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ClientSize = new Size(AppScale.Scale(525), AppScale.Scale(310));
        BackColor = AppTheme.PageBackground;
        priority.Items.AddRange(new object[] { "High", "Normal", "Low" });
        priority.SelectedIndex = 1;
        var save = new ModernButton { Name = "communicationConfirmSchedule", Text = "Confirm Schedule", ThemeRole = "Primary" };
        var cancel = new ModernButton { Name = "communicationCancelSchedule", Text = "Cancel", DialogResult = DialogResult.Cancel };
        CancelButton = cancel;
        var expiryRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        expiryRow.Controls.Add(expire);
        expiryRow.Controls.Add(hours);
        expiryRow.Controls.Add(new Label { Text = "hours", AutoSize = true });
        repeat.Name="communicationRecurrence";
        repeat.Items.AddRange(new object[]{"One-time","Daily","Weekly","Monthly","Custom interval"});
        repeat.SelectedIndex=0;
        repeatUnit.Items.AddRange(new object[]{"Days","Weeks","Months"});repeatUnit.SelectedIndex=0;
        until.Value=now.AddMonths(6).Date;until.MaxDate=now.AddDays(365).Date;until.MinDate=now.Date;
        var recurringDetails=new FlowLayoutPanel {Dock=DockStyle.Fill,WrapContents=true,Visible=false};
        foreach(var control in new Control[]{new Label{Text="Every",AutoSize=true},repeatEvery,repeatUnit,
            new Label{Text="Until",AutoSize=true},until,new Label{Text="Maximum occurrences",AutoSize=true},maximum})recurringDetails.Controls.Add(control);
        repeat.SelectedIndexChanged+=(_,_)=>{
            recurringDetails.Visible=repeat.SelectedIndex>0;
            repeatEvery.Enabled=repeatUnit.Enabled=repeat.SelectedIndex==4;
            ClientSize=new Size(AppScale.Scale(560),AppScale.Scale(repeat.SelectedIndex==0 ? 375 : 485));
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7,
            Padding = new Padding(AppScale.Scale(18))
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.FieldRowHeight));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.FieldRowHeight));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.FieldRowHeight));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppScale.Scale(50)));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.ButtonHeight + AppScale.Scale(12)));
        layout.Controls.Add(HumanUi.Field("Send on (" + schoolZone.Id + ")", when, true), 0, 0);
        layout.Controls.Add(HumanUi.Field("Priority", priority), 0, 1);
        layout.Controls.Add(HumanUi.Field("Repeat",repeat),0,2);
        layout.Controls.Add(recurringDetails,0,3);
        layout.Controls.Add(expiryRow, 0, 4);
        layout.Controls.Add(problem, 0, 5);
        layout.Controls.Add(HumanUi.DialogButtons(save, cancel), 0, 6);
        Controls.Add(layout);
        ClientSize=new Size(AppScale.Scale(560),AppScale.Scale(375));
        hours.Enabled = expire.Checked;
        expire.CheckedChanged += (_, _) => hours.Enabled = expire.Checked;
        save.Click += (_, _) =>
        {
            DateTimeOffset chosen;
            try { chosen = CommunicationSchoolTime.ToInstant(when.Value, schoolZone); }
            catch (CommunicationValidationException ex) { problem.Text = ex.Message; return; }
            if (chosen <= DateTimeOffset.UtcNow.AddMinutes(1))
            {
                problem.Text = "Choose a time at least one minute in the future.";
                return;
            }
            Due = chosen;
            Expires = expire.Checked ? Due.AddHours(Decimal.ToDouble(hours.Value)) : null;
            Recurrence=null;
            if(repeat.SelectedIndex>0)
            {
                if(!expire.Checked || hours.Value>168){problem.Text="Recurring messages need an expiry of 1–168 hours.";return;}
                try
                {
                    string frequency=repeat.SelectedIndex switch{1=>"Days",2=>"Weeks",3=>"Months",_=>Convert.ToString(repeatUnit.SelectedItem)!};
                    int interval=repeat.SelectedIndex==4 ? (int)repeatEvery.Value : 1;
                    var end=CommunicationSchoolTime.ToInstant(until.Value.Date.AddHours(23).AddMinutes(59),schoolZone);
                    var recurrence=new CommunicationRecurrence(frequency,interval,when.Value,schoolZone.Id,end,(int)maximum.Value,(int)hours.Value);
                    if(end<=Due || recurrence.NextAfter(Due)>end)
                        throw new CommunicationValidationException("The end date must include at least two occurrences.");
                    Recurrence=recurrence;
                }
                catch(CommunicationValidationException ex){problem.Text=ex.Message;return;}
            }
            DialogResult = DialogResult.OK;
        };
    }
}
