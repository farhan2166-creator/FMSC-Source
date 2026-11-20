using System.Data;
using System.Reflection;
using FMSC_SMS.Core;
using FMSC_SMS.Pages.Finance.StudentAccounts;
using FMSC_SMS.Pages.Finance.Collection;
using FMSC_SMS.Pages.Setup;
using FMSC_SMS.Pages.Security;

internal static partial class Program
{
    private static DataSet TransportFixture(bool stop=false)
    {
        var data=new DataSet();var summary=data.Tables.Add();
        summary.Columns.Add("Snapshot");summary.Columns.Add("Today",typeof(DateTime));summary.Columns.Add("TransportCutoffDay",typeof(int));
        foreach(string name in new[]{"CutoffMonth","BillingStartMonth","YearStartMonth","YearEndMonth"})summary.Columns.Add(name,typeof(DateTime));
        summary.Columns.Add("BillingReady",typeof(bool));summary.Columns.Add("EnrollmentStatus");
        summary.Rows.Add("transport-snapshot",new DateTime(2026,10,15),16,new DateTime(2026,10,1),new DateTime(2026,4,1),new DateTime(2026,4,1),new DateTime(2027,3,1),true,"Active");
        var plans=data.Tables.Add();plans.Columns.Add("FeeTypeId",typeof(int));plans.Columns.Add("FeeTypeName");plans.Columns.Add("Amount",typeof(decimal));
        foreach(string name in new[]{"EffectiveFrom","EffectiveTo","CancelledAt"})plans.Columns.Add(name,typeof(DateTime));
        if(stop)plans.Rows.Add(4,"Transport Fee",2600m,new DateTime(2026,4,1),DBNull.Value,DBNull.Value);
        var rates=data.Tables.Add();rates.Columns.Add("FeeTypeId",typeof(int));rates.Columns.Add("FeeTypeName");rates.Columns.Add("Amount",typeof(decimal));rates.Rows.Add(4,"Transport Fee",2600m);
        var charges=data.Tables.Add();charges.Columns.Add("FeeTypeId",typeof(int));charges.Columns.Add("BillingMonth",typeof(DateTime));
        charges.Columns.Add("NetAmount",typeof(decimal));charges.Columns.Add("Balance",typeof(decimal));charges.Columns.Add("HasPayment",typeof(bool));
        if(stop){charges.Rows.Add(4,new DateTime(2026,10,1),2600m,2600m,false);charges.Rows.Add(4,new DateTime(2026,11,1),2600m,2600m,false);}
        return data;
    }
    private static void CheckTransportService()
    {
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var original=(CurrentUser.UserId,CurrentUser.Username,CurrentUser.RoleName,CurrentUser.MustChangePassword,Permissions:CurrentUser.GetPermissions().ToArray());
        CurrentUser.SignIn(7781,"Transport fixture","Administrator",false,Array.Empty<string>());
        try
        {
            Check(TransportMonthRules.CutoffMonth(new DateTime(2026,10,15),16)==new DateTime(2026,10,1),"Before the transport cutoff, this month is allowed");
            Check(TransportMonthRules.CutoffMonth(new DateTime(2026,10,16),16)==new DateTime(2026,11,1),"The transport cutoff day itself moves changes to next month");
            Check(TransportMonthRules.CutoffMonth(new DateTime(2026,12,27),16)==new DateTime(2027,1,1),"Transport cutoff crosses the calendar year correctly");
            Check(TransportMonthRules.CutoffMonth(new DateTime(2026,10,27),null)==new DateTime(2026,10,1),"A blank independent transport cutoff permits the current month");
            using var context=TransportFixture();
            Check(TransportMonthRules.Months(context,4,true).First()==new DateTime(2026,10,1),"Starting defaults to the first cutoff-permitted month");
            Check(TransportMonthRules.Months(context,4,true).Last()==new DateTime(2027,3,1),"Starts stay within the academic year");
            Check(TransportMonthRules.Months(context,4,false).Last()==new DateTime(2027,4,1),"Stopping can close transport immediately after the academic year");
            context.Tables[3].Rows.Add(4,new DateTime(2026,10,1),2600m,1600m,true);
            Check(TransportMonthRules.Months(context,4,true).First()==new DateTime(2026,11,1)&&TransportMonthRules.Months(context,4,false).First()==new DateTime(2026,11,1),"A partially paid month is protected for both start and stop");
            context.Tables[3].Rows[0]["Balance"]=0m;
            Check(TransportMonthRules.Months(context,4,false).First()==new DateTime(2026,11,1),"A fully paid month is also protected before the cutoff");
            context.Tables[3].Rows.Clear();context.Tables[3].Rows.Add(4,new DateTime(2026,10,1),2600m,2600m,false);
            Check(TransportMonthRules.Months(context,4,false).First()==new DateTime(2026,10,1),"A wholly unpaid current month can be stopped before cutoff");
            T Field<T>(object owner,string name)=>(T)owner.GetType().GetField(name,flags)!.GetValue(owner)!;
            void Show(Form form){form.ShowInTaskbar=false;form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-30000,-30000);form.Show();Application.DoEvents();}
            Directory.CreateDirectory("artifacts/transport-service");
            foreach(bool stop in new[]{false,true})
            {
                using var fixture=TransportFixture(stop);int writes=0;DateTime chosen=default;string? note=null;
                using var form=new TransportServiceDialog(1,!stop,null,()=>Task.FromResult(fixture.Copy()),(fee,from,reason,snapshot,request)=>{
                    writes++;chosen=from;note=reason;Check(fee==4&&snapshot=="transport-snapshot"&&request!=Guid.Empty,"Transport confirmation retains its reviewed identity, snapshot and request marker");
                    var result=new DataTable();result.Columns.Add("Saved");result.Rows.Add("yes");return Task.FromResult(result);
                });
                Show(form);
                var month=Field<ModernComboBox>(form,"month");
                Check(Convert.ToString(month.SelectedItem)=="October 2026","Both dialogs select the transport cutoff default");
                var explanation=Field<Label>(form,"consequence").Text;
                Check(explanation.Contains("cutoff: day 16")&&explanation.Contains("partial payment"),"Both dialogs explain cutoff and payment protection");
                Check(stop?explanation.Contains("5,200"):explanation.Contains("posted immediately"),"The dialog describes posted cancellations or immediate starting charges");
                Check(Field<ModernTextBox>(form,"reason").Text=="","Transport reason is optional and begins blank");
                Check(form.ClientRectangle.Contains(form.RectangleToClient(Field<ModernTextBox>(form,"reason").RectangleToScreen(Field<ModernTextBox>(form,"reason").ClientRectangle))),"The optional reason fits on the same screen without scrolling");
                using(var picture=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(picture,new Rectangle(Point.Empty,picture.Size));picture.Save($"artifacts/transport-service/{(stop?"stop":"start")}-dialog.png");}
                month.SelectedIndex=1;Field<ModernButton>(form,"confirm").PerformClick();Application.DoEvents();
                Check(writes==1&&form.DialogResult==DialogResult.OK&&chosen==new DateTime(2026,11,1)&&note=="","Start and stop save the selected month with no mandatory reason");
            }
            using(var page=new FeeCollectionPage(false))
            {
                var menuButton=Field<ModernButton>(page,"studentFeesMenuButton");var transport=Field<ModernButton>(page,"transportButton");
                Check(transport.Parent==menuButton.Parent && transport.Width>0 && transport.Height>0,
                    "Transport remains a usable standalone action without fixing its position");
                var menu=Field<ContextMenuStrip>(page,"studentFeesMenu");Check(!menu.Items.Cast<ToolStripItem>().Any(item=>item.Text?.Contains("Transport")==true),"Student Fees no longer duplicates separate start/stop transport menu items");
                typeof(FeeCollectionPage).GetField("transportStarts",flags)!.SetValue(page,false);typeof(FeeCollectionPage).GetMethod("UpdateTransportButton",flags)!.Invoke(page,null);
                Check(!string.IsNullOrWhiteSpace(transport.Text) && !transport.Enabled,
                    "The transport action stays unavailable without a loaded placement");
            }
            using(var profile=new SchoolProfilePage(false))
            using(var host=new Form{ClientSize=new Size(1100,760)})
            {
                var admission=Field<ModernNumericTextBox>(profile,"feeProrationCutoffDay");var transport=Field<ModernNumericTextBox>(profile,"transportCutoffDay");
                admission.Value=16;transport.Value=20;Check(admission.Value==16&&transport.Value==20,"School Profile keeps transport cutoff independent of the general fee cutoff");
                host.Controls.Add(profile);Show(host);using var picture=new Bitmap(host.Width,host.Height);host.DrawToBitmap(picture,new Rectangle(Point.Empty,picture.Size));picture.Save("artifacts/transport-service/profile-cutoffs.png");
            }
        }
        finally{CurrentUser.SignIn(original.UserId,original.Username,original.RoleName,original.MustChangePassword,original.Permissions);}
    }
}
