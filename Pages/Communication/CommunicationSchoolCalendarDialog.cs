using System.Data;
using FMSC_SMS.Core;

namespace FMSC_SMS.Pages.Communication;

/// <summary>
/// Compact school calendar editor. The SQL worker and SQL attempt claim both
/// recheck this calendar; changing a weekday or holiday never directly sends.
/// </summary>
internal sealed class CommunicationSchoolCalendarDialog : ApplicationWindowForm
{
    private readonly CheckBox[] days = Enumerable.Range(0,7)
        .Select(i => new CheckBox { AutoSize = true, Text = new[]
        { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" }[i] }).ToArray();
    private readonly DateTimePicker holidayDate = new()
    {
        Name="communicationHolidayDate", Format=DateTimePickerFormat.Short,
        MinDate=new DateTime(2000,1,1), MaxDate=new DateTime(2100,1,1),
        Value=DateTime.Today
    };
    private readonly ModernTextBox holidayDescription = HumanUi.TextBox(false,150);
    private readonly ListBox holidayList = new()
    {
        Name="communicationSchoolHolidays", Dock=DockStyle.Fill,
        IntegralHeight=false
    };
    private readonly ModernButton saveHoliday = new()
    {
        Name="communicationSaveHoliday", Text="Save Holiday",
        ThemeRole="Primary", Width=AppScale.Scale(145)
    };
    private readonly ModernButton removeHoliday = new()
    {
        Name="communicationRemoveHoliday", Text="Remove Selected",
        ThemeRole="Secondary", Width=AppScale.Scale(155)
    };
    private readonly Label feedback = new()
    {
        AutoSize=false, Dock=DockStyle.Fill, ForeColor=AppTheme.MutedText,
        Text="Select the days on which scheduled messages may be sent."
    };
    private bool busy,loading;
    internal bool Changed { get; private set; }
    private sealed record Holiday(DateOnly Date,string Description)
    {
        public override string ToString() => Date.ToString("dd MMM yyyy")
            + "  ·  " + Description;
    }

    internal CommunicationSchoolCalendarDialog()
    {
        Text="School Working Days & Holidays";
        ClientSize=new Size(AppScale.Scale(620),AppScale.Scale(510));
        StartPosition=FormStartPosition.CenterParent;
        ShowInTaskbar=false;
        FormBorderStyle=FormBorderStyle.FixedDialog;
        MaximizeBox=MinimizeBox=false;
        BackColor=AppTheme.PageBackground;

        var layout=new TableLayoutPanel
        {
            Dock=DockStyle.Fill,ColumnCount=1,RowCount=6,
            Padding=new Padding(AppScale.Scale(16))
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,AppScale.Scale(42)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,AppScale.Scale(50)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,AppScale.Scale(68)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,AppScale.Scale(52)));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,AppScale.Scale(50)));
        layout.Controls.Add(HumanUi.GroupHeading("School working days"),0,0);
        var weekdays=new FlowLayoutPanel
        {
            Name="communicationWeekdays",Dock=DockStyle.Fill,WrapContents=false,
            AutoScroll=true
        };
        foreach(var checkbox in days)
        {
            checkbox.Margin=new Padding(AppScale.Scale(8),AppScale.Scale(9),AppScale.Scale(8),0);
            weekdays.Controls.Add(checkbox);
            int weekday=Array.IndexOf(days,checkbox)+1;
            checkbox.CheckedChanged+=async (_,_) =>
            {
                if(loading)return;
                await RunAsync(async () =>
                {
                    await CommunicationQueuePolicyStore.SetSchoolWeekdayAsync(weekday,checkbox.Checked);
                    Changed=true;
                    feedback.Text="School working days saved.";
                },reloadOnError:true);
            };
        }
        layout.Controls.Add(weekdays,0,1);
        var dateAndDescription=new TableLayoutPanel
        {
            Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=Padding.Empty
        };
        dateAndDescription.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,40));
        dateAndDescription.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,60));
        dateAndDescription.Controls.Add(HumanUi.Field("Holiday date",holidayDate),0,0);
        dateAndDescription.Controls.Add(HumanUi.Field("Holiday description",holidayDescription),1,0);
        layout.Controls.Add(dateAndDescription,0,2);
        var actions=new FlowLayoutPanel
        {
            Dock=DockStyle.Fill,WrapContents=false,AutoScroll=true
        };
        saveHoliday.Height=removeHoliday.Height=AppMetrics.ButtonHeight;
        actions.Controls.Add(saveHoliday);
        actions.Controls.Add(removeHoliday);
        layout.Controls.Add(actions,0,3);
        layout.Controls.Add(holidayList,0,4);
        layout.Controls.Add(feedback,0,5);
        Controls.Add(layout);

        holidayList.SelectedIndexChanged+=(_,_) =>
        {
            if(holidayList.SelectedItem is not Holiday item)return;
            holidayDate.Value=item.Date.ToDateTime(TimeOnly.MinValue);
            holidayDescription.Text=item.Description;
        };
        saveHoliday.Click+=async (_,_)=>await RunAsync(async ()=>
        {
            string description=holidayDescription.Text.Trim();
            if(description.Length==0 || description.Length>150)
                throw new ArgumentException("Enter a brief school holiday description.");
            await CommunicationQueuePolicyStore.SetSchoolHolidayAsync(
                DateOnly.FromDateTime(holidayDate.Value),description,false);
            Changed=true;
            await RefreshHolidaysAsync();
            feedback.Text="Holiday saved. Scheduled messages will skip that date.";
        });
        removeHoliday.Click+=async (_,_)=>
        {
            if(holidayList.SelectedItem is not Holiday selected)return;
            if(ModernMessageDialog.Show(this,
                "Remove the holiday for "+selected.Date.ToString("dd MMM yyyy")+"?",
                Text,MessageBoxButtons.YesNo,MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2)!=DialogResult.Yes)return;
            await RunAsync(async ()=>
            {
                await CommunicationQueuePolicyStore.SetSchoolHolidayAsync(
                    selected.Date,null,true);
                Changed=true;
                await RefreshHolidaysAsync();
                feedback.Text="Holiday removed.";
            });
        };
        Shown+=async (_,_)=>await RunAsync(RefreshAsync);
    }

    private async Task RefreshAsync()
    {
        using var weekdays=await CommunicationQueuePolicyStore.ReadSchoolWeekdaysAsync();
        if(weekdays.Rows.Count!=7)
            throw new InvalidOperationException("School working-day settings are incomplete.");
        loading=true;
        try
        {
            foreach(DataRow row in weekdays.Rows)
            {
                int weekday=Convert.ToInt32(row["IsoWeekday"]);
                if(weekday is < 1 or > 7)
                    throw new InvalidDataException("Unexpected weekday in school settings.");
                days[weekday-1].Checked=Convert.ToBoolean(row["Enabled"]);
            }
        }
        finally { loading=false; }
        await RefreshHolidaysAsync();
    }

    private async Task RefreshHolidaysAsync()
    {
        using var table=await CommunicationQueuePolicyStore.ReadSchoolHolidaysAsync();
        holidayList.BeginUpdate();
        try
        {
            holidayList.Items.Clear();
            foreach(DataRow row in table.Rows)
                holidayList.Items.Add(new Holiday(
                    DateOnly.FromDateTime(Convert.ToDateTime(row["HolidayDate"])),
                    Convert.ToString(row["Description"])??""));
        }
        finally { holidayList.EndUpdate(); }
    }

    private async Task RunAsync(Func<Task> operation,bool reloadOnError=false)
    {
        if(busy || IsDisposed)return;
        busy=true;
        try
        {
            await operation();
        }
        catch(Exception ex)
        {
            feedback.Text=DbMessage.UserMessage(ex);
            feedback.ForeColor=AppTheme.Danger;
            if(reloadOnError)
            {
                try { await RefreshAsync(); }
                catch(Exception reloadFailure)
                {
                    AppExceptionHandler.LogNonFatal(reloadFailure,
                        "Reloading school communication sending calendar");
                }
            }
        }
        finally { busy=false; }
    }
}
