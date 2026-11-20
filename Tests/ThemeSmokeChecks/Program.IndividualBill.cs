using System.Reflection;
using FMSC_SMS.Core;
using FMSC_SMS.Pages.Finance.Collection;
using FMSC_SMS.Pages.Students.Admissions;
using FMSC_SMS.Pages.Security;

internal static partial class Program
{
    private static void CheckHistoricalLifecycleControls()
    {
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var previous=(CurrentUser.UserId,CurrentUser.Username,CurrentUser.RoleName,CurrentUser.MustChangePassword,Permissions:CurrentUser.GetPermissions().ToArray());
        try
        {
            CurrentUser.SignIn(7781,"History fixture","Administrator",false,Array.Empty<string>());
            using var page=new StudentAdmissionPage();var type=typeof(StudentAdmissionPage);
            void Set(string field,object value)=>type.GetField(field,flags)!.SetValue(page,value);
            Set("currentAdmissionId",41);Set("pendingPlacementId",0);Set("_workspaceStudentStatus","Active");
            Set("placementYearStart",DateTime.Today.AddYears(-2));Set("placementYearEnd",DateTime.Today.AddYears(-1));
            var menu=(ContextMenuStrip)type.GetField("enrollmentActionsMenu",flags)!.GetValue(page)!;
            typeof(ToolStripDropDown).GetMethod("OnOpening",flags)!.Invoke(menu,new object[]{new System.ComponentModel.CancelEventArgs()});
            ToolStripItem Item(string caption)=>menu.Items.Cast<ToolStripItem>().Single(i=>i.Text==caption);
            Check(Item("Transfer").Available&&Item("Placement History…").Available&&Item("Record Departure…").Available,
                "Historical placement retains its history-building actions");
            Check(!Item("Put on Hold…").Available&&!Item("Lift Hold / Resume…").Available,
                "Expired placement offers no routine hold or resume");
            Check(Item("Transfer").ToolTipText?.Contains("Historical",StringComparison.OrdinalIgnoreCase)==true,"Historical transfer explains date and fee restrictions");
            int reads=0;
            using var departure=new CloseAdmissionDialog(41,"Fictional old student",false,(proc,args)=>{
                reads++;throw new Exception("Unchecked historical date must not reach SQL");
            },DateTime.Today.AddYears(-2),DateTime.Today.AddYears(-1));
            var date=(ModernDatePicker)typeof(CloseAdmissionDialog).GetField("date",flags)!.GetValue(departure)!;
            Check(date.ShowCheckBox&&!date.Checked&&date.MaxDate.Date==DateTime.Today.AddYears(-1),
                "Historical departure requires an explicit date and caps it at the recorded year end");
            ((Task)typeof(CloseAdmissionDialog).GetMethod("ReviewAsync",flags)!.Invoke(departure,null)!).GetAwaiter().GetResult();
            Check(reads==0,"Review refuses a missing historical departure date before querying SQL");
            Check(!page.IsHandleCreated&&!departure.IsHandleCreated,"Historical menu and date checks start no SQL loaders");
        }
        finally {CurrentUser.SignIn(previous.UserId,previous.Username,previous.RoleName,previous.MustChangePassword,previous.Permissions);}
    }

    private static void CheckIndividualOutstandingBill()
    {
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var previous=(CurrentUser.UserId,CurrentUser.Username,CurrentUser.RoleName,CurrentUser.MustChangePassword,Permissions:CurrentUser.GetPermissions().ToArray());
        try
        {
            CurrentUser.SignIn(7781,"Bill fixture","Administrator",false,Array.Empty<string>());
            using var page=new FeeCollectionPage(false);
            var type=typeof(FeeCollectionPage);
            T Field<T>(string field)=>(T)type.GetField(field,flags)!.GetValue(page)!;
            void Set(string field,object value)=>type.GetField(field,flags)!.SetValue(page,value);
            Set("admissionId",41);Set("enrollmentId",141);Set("suppressSiblingEvents",true);
            var siblings=Field<CheckedListBox>("siblings");
            var choice=type.GetNestedType("SiblingChoice",BindingFlags.NonPublic)!;
            siblings.Items.Add(Activator.CreateInstance(choice,new object[]{42,"Fictional sibling"})!,true);
            siblings.Items.Add(Activator.CreateInstance(choice,new object[]{43,"Fictional sibling"})!,false);
            var family=Field<ModernButton>("printBillButton");var individual=Field<ModernButton>("printStudentBillButton");
            Check(!string.IsNullOrWhiteSpace(family.Text) && !string.IsNullOrWhiteSpace(individual.Text) &&
                !string.Equals(family.Text,individual.Text,StringComparison.OrdinalIgnoreCase),
                "Distinct family and student bill actions remain recognizable without fixed captions");
            Check(family.Parent is not null && family.Parent==individual.Parent,
                "Both print actions remain available in the collection action area");
            Check(family.Width>0 && individual.Width>0 && family.Height>0 && individual.Height>0,
                "Both print actions retain usable dimensions without requiring identical sizes");
            int calls=0;int[]? printed=null;bool grouped=true;Func<bool>? stillCurrent=null;
            Set("showOutstandingBill",(Func<Form?,int,int[]?,Func<bool>?,bool,Task>)((owner,anchor,ids,current,byFamily)=>{
                calls++;printed=ids;grouped=byFamily;stillCurrent=current;return Task.CompletedTask;
            }));
            Task Print(bool one)=>(Task)type.GetMethod("PrintOutstandingBillAsync",flags)!.Invoke(page,new object[]{one})!;
            Print(false).GetAwaiter().GetResult();
            Check(calls==1&&printed!.SequenceEqual(new[]{41,42})&&grouped,
                "Family print combines checked children by default without selecting a grouping option");
            siblings.SetItemChecked(1,true);
            Print(false).GetAwaiter().GetResult();
            Check(calls==2&&printed!.SequenceEqual(new[]{41,42,43})&&grouped,
                "All three selected family members use one combined family bill");
            siblings.SetItemChecked(1,false);
            Check(!stillCurrent!(),"Unchecking a child invalidates the three-member family preview");
            calls=0;
            Print(true).GetAwaiter().GetResult();
            Check(calls==1&&printed!.SequenceEqual(new[]{41})&&!grouped,"Individual print always uses the main student and individual layout");
            Check(siblings.GetItemChecked(0)&&!siblings.GetItemChecked(1),"Individual print preserves the family's checkbox selections");
            Check(stillCurrent!(),"The individual preview recognizes the unchanged student context");
            siblings.SetItemChecked(0,false);
            Check(stillCurrent!(),"Changing sibling choices cannot change an individual preview's scope");
            siblings.SetItemChecked(0,true);
            Print(false).GetAwaiter().GetResult();
            Check(calls==2&&printed!.SequenceEqual(new[]{41,42})&&grouped,"Family print retains checked-child scope and family grouping");
            siblings.SetItemChecked(0,false);
            Check(!stillCurrent!(),"A family selection change invalidates its old preview");
            Print(true).GetAwaiter().GetResult();Set("admissionId",99);
            Check(!stillCurrent!(),"Changing the main student invalidates the individual preview");
            CurrentUser.SignIn(7782,"Denied bill fixture","Operator",false,Array.Empty<string>());
            Print(true).GetAwaiter().GetResult();
            Check(calls==3,"Individual print independently checks viewing permission before reading or printing");
            Check(!page.IsHandleCreated&&!page.IsBusy,"Bill routing checks do not start database loaders or retain busy state");
            var header=family.Parent!.Parent!;
            using var window=new Form{ClientSize=new Size(1300,header.Height),ShowInTaskbar=false,
                StartPosition=FormStartPosition.Manual,Location=new Point(-20000,-20000)};
            window.Controls.Add(header);window.Show();Application.DoEvents();
            Check(!family.RectangleToScreen(family.ClientRectangle).IntersectsWith(
                individual.RectangleToScreen(individual.ClientRectangle)),
                "Rendered bill actions do not overlap, regardless of their sizes or positions");
            Check(header.ClientRectangle.Contains(header.RectangleToClient(individual.RectangleToScreen(individual.ClientRectangle))),
                "Individual print remains inside the header at normal window width");
            Directory.CreateDirectory("artifacts/historical-placement");
            using(var bitmap=new Bitmap(header.Width,header.Height))
            {header.DrawToBitmap(bitmap,header.ClientRectangle);bitmap.Save("artifacts/historical-placement/print-buttons.png");}
            window.Close();
        }
        finally {CurrentUser.SignIn(previous.UserId,previous.Username,previous.RoleName,previous.MustChangePassword,previous.Permissions);}
    }
}
