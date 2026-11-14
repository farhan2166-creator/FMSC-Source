using System.Data;
using System.Reflection;
using FMSC_SMS.Core;
using FMSC_SMS.Pages.Finance.Collection;
using FMSC_SMS.Pages.Finance.StudentAccounts;
using FMSC_SMS.Pages.Security;

internal static partial class Program
{
    private static void CheckCollectionReturns()
    {
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var type=typeof(FeeCollectionPage);
        var previous=(CurrentUser.UserId,CurrentUser.Username,CurrentUser.RoleName,CurrentUser.MustChangePassword,Permissions:CurrentUser.GetPermissions().ToArray());
        try
        {
            CurrentUser.SignIn(7781,"Return fixture","Administrator",false,Array.Empty<string>());
            using var rows=new DataTable();
            foreach(string column in new[]{"AdmissionId","EnrollmentId"})rows.Columns.Add(column,typeof(int));
            foreach(string column in new[]{"Status","EnrollmentStatus"})rows.Columns.Add(column);
            foreach(string column in new[]{"BillingReady","IsOnHold"})rows.Columns.Add(column,typeof(bool));
            foreach(string column in new[]{"PlacementConfirmedAt","ReturnConfirmedAt"})rows.Columns.Add(column,typeof(DateTime));
            rows.Rows.Add(41,141,"Active","Active",false,false,DateTime.Today,DBNull.Value);
            rows.Rows.Add(42,142,"Active","Active",true,false,DateTime.Today,DateTime.Today);
            rows.Rows.Add(43,143,"Pending","Pending",false,false,DBNull.Value,DBNull.Value);
            int reads=0,reloads=0,opens=0,opened=0;
            TaskCompletionSource<bool>? reply=null;
            TaskCompletionSource<DataTable>? delayedRead=null;
            FeeCollectionPage? page=null;
            Task<DataTable> Read(int id){reads++;return delayedRead?.Task??Task.FromResult(rows.Copy());}
            Task OpenedAccount(int id)
            {
                reloads++;
                return (Task)type.GetMethod("RefreshReturnAvailabilityAsync",flags)!.Invoke(page,null)!;
            }
            using var fixture=new FeeCollectionPage(false,Read,id=>{opens++;opened=id;return reply?.Task??Task.FromResult(false);},OpenedAccount);
            page=fixture;
            T Field<T>(string name)=>(T)type.GetField(name,flags)!.GetValue(page)!;
            void Set(string name,object value)=>type.GetField(name,flags)!.SetValue(page,value);
            Task Call(string name)=>(Task)type.GetMethod(name,flags)!.Invoke(page,null)!;
            void Finish(Task task){PumpUntil(()=>task.IsCompleted,"The injected return workflow completes");task.GetAwaiter().GetResult();}
            Set("admissionId",41);Set("enrollmentId",141);
            Field<Label>("primaryPayer").Text="1001 · Fictional child\nClass 1 · 2026–2027";
            using var owner=new Form { ClientSize=new Size(1280,800),StartPosition=FormStartPosition.Manual,Location=new Point(-30000,-30000),ShowInTaskbar=false };
            page.Dock=DockStyle.Fill;owner.Controls.Add(page);owner.Show();Application.DoEvents();
            Finish(Call("RefreshReturnAvailabilityAsync"));
            var button=Field<ModernButton>("confirmReturnButton");
            var statuses=Field<Dictionary<int,string>>("returnStatusByStudent");
            Check(button.Visible&&button.Enabled,"The loaded promoted child has a permitted direct return action");
            Check(statuses[41]=="Awaiting Return"&&statuses[42]=="Return Confirmed"&&statuses[43]=="Pending Registration",
                "Waiting returns, confirmed returns and new registrations have distinct panel statuses");
            var current=Field<FamilyStudentList>("currentStudentAmounts");
            Check(current.PaymentBounds(0).Bottom<=current.ClientSize.Height && button.Bounds.Top>=current.Bounds.Bottom,
                "The small status, totals and return button fit without overlap at 1280 pixels");
            Check(button.Width>=TextRenderer.MeasureText(button.Text,button.Font).Width,"Confirm Return remains fully readable in the current-child panel");
            // A checked sibling must never expand the return operation's scope.
            Set("suppressSiblingEvents",true);
            type.GetMethod("AddSiblingChoice",flags)!.Invoke(page,new object[]{42,142,"Sibling","1002 · Sibling\nClass 2",true});
            Set("suppressSiblingEvents",false);
            Field<ModernTextBox>("paymentAmount").Text="125";
            Finish(Call("ConfirmSelectedReturnAsync"));
            Check(opens==0&&Field<ModernTextBox>("paymentAmount").Text=="125","Return review preserves an entered payment and refuses to open over it");
            Field<ModernTextBox>("paymentAmount").Text="0";
            foreach(string name in new[]{"reference","remarks"})
            {
                Field<ModernTextBox>(name).Text="Keep this draft";Finish(Call("ConfirmSelectedReturnAsync"));
                Check(opens==0&&Field<ModernTextBox>(name).Text=="Keep this draft","Return review retains payment "+name);
                Field<ModernTextBox>(name).Clear();
            }
            reply=new TaskCompletionSource<bool>();
            var confirmation=Call("ConfirmSelectedReturnAsync");
            Check(page.IsBusy&&!button.Enabled&&opens==1&&opened==41,"Only the loaded child opens and the payment page stays locked during review");
            Check(Call("ConfirmSelectedReturnAsync").IsCompleted&&opens==1,"Repeated clicks cannot open a second return review");
            rows.Rows[0]["BillingReady"]=true;rows.Rows[0]["ReturnConfirmedAt"]=DateTime.Today;
            reply.SetResult(true);Finish(confirmation);
            Check(reloads==1&&!button.Visible&&!page.IsBusy&&statuses[41]=="Return Confirmed",
                "Successful return reloads fees and replaces the waiting status without repeating confirmation");
            rows.Rows[0]["BillingReady"]=false;rows.Rows[0]["ReturnConfirmedAt"]=DBNull.Value;
            Finish(Call("RefreshReturnAvailabilityAsync"));reply=null;Finish(Call("ConfirmSelectedReturnAsync"));
            Check(reloads==2&&button.Visible&&opens==2,"Cancelling refreshes the account and leaves its return pending");
            rows.Rows[0]["IsOnHold"]=true;Finish(Call("RefreshReturnAvailabilityAsync"));
            Check(!button.Visible&&statuses[41]=="On Hold","A paused billing plan uses the hold status and cannot masquerade as a return");
            rows.Rows[0]["IsOnHold"]=false;Finish(Call("RefreshReturnAvailabilityAsync"));
            rows.Rows[0]["BillingReady"]=true;Finish(Call("ConfirmSelectedReturnAsync"));
            Check(opens==2&&reloads==3,"A return completed elsewhere is refreshed instead of opening a stale review");
            CurrentUser.SignIn(7782,"Fee viewer","Cashier",false,new[]{"Fees.View"});
            rows.Rows[0]["BillingReady"]=false;Finish(Call("RefreshReturnAvailabilityAsync"));
            Check(!button.Visible&&statuses[41]=="Awaiting Return","Fee viewers can distinguish readiness without gaining return-confirmation access");
            CurrentUser.SignIn(7781,"Return fixture","Administrator",false,Array.Empty<string>());
            delayedRead=new TaskCompletionSource<DataTable>();var oldRead=Call("RefreshReturnAvailabilityAsync");
            Set("admissionId",42);type.GetMethod("ResetReturnAvailability",flags)!.Invoke(page,null);
            delayedRead.SetResult(rows.Copy());Finish(oldRead);delayedRead=null;
            Check(statuses.Count==0&&!button.Visible,"An obsolete student read cannot show the previous child's return action or status");
            Set("admissionId",41);Finish(Call("RefreshReturnAvailabilityAsync"));
            Directory.CreateDirectory("artifacts/collection-returns");
            using(var picture=new Bitmap(owner.Width,owner.Height)){owner.DrawToBitmap(picture,new Rectangle(Point.Empty,picture.Size));picture.Save("artifacts/collection-returns/awaiting-return.png");}
            int before=reads;page.ConfigureCounter(new[]{41});Finish(Call("RefreshReturnAvailabilityAsync"));
            Check(!button.Visible&&reads==before,"A fixed payment visit cannot start return preparation");
        }
        finally{CurrentUser.SignIn(previous.UserId,previous.Username,previous.RoleName,previous.MustChangePassword,previous.Permissions);}
    }

    private static void CheckOptionalFeeReason()
    {
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var previous=(CurrentUser.UserId,CurrentUser.Username,CurrentUser.RoleName,CurrentUser.MustChangePassword,Permissions:CurrentUser.GetPermissions().ToArray());
        try
        {
            CurrentUser.SignIn(7781,"Reason fixture","Administrator",false,Array.Empty<string>());
            using var rows=new DataTable();
            foreach(string column in new[]{"Frequency","Status","StudentName","AdmissionNumber","YearName"})rows.Columns.Add(column);
            foreach(string column in new[]{"StartDate","EndDate","EnrollmentDate","BillingMonth"})rows.Columns.Add(column,typeof(DateTime));
            rows.Columns.Add("BillingChargeId",typeof(long));rows.Columns.Add("Balance",typeof(decimal));
            rows.Rows.Add("Monthly","Active","Fixture child","1001","Fixture year",new DateTime(DateTime.Today.Year,1,1),new DateTime(DateTime.Today.Year,12,31),new DateTime(DateTime.Today.Year,1,1),DateTime.Today,321L,500m);
            using var form=new ChangeFeeDiscountForm(41,"Tuition",500,0,rows.Rows[0]);
            T Field<T>(string name)=>(T)typeof(ChangeFeeDiscountForm).GetField(name,flags)!.GetValue(form)!;
            var amount=Field<ModernTextBox>("amount");var reason=Field<ModernTextBox>("reason");
            form.ConfigureCollectionBill();amount.Text="100";
            form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-30000,-30000);form.Show();Application.DoEvents();
            bool Valid()=>(bool)typeof(ChangeFeeDiscountForm).GetMethod("ValidateChoices",flags)!.Invoke(form,null)!;
            string Reason()=>(string)((Microsoft.Data.SqlClient.SqlParameter[])typeof(ChangeFeeDiscountForm).GetMethod("Args",flags)!.Invoke(form,new object[]{true})!).Single(p=>p.ParameterName=="@Reason").Value;
            Check(Valid()&&Reason()=="Concession revised","An omitted optional reason uses the existing neutral financial-record description");
            var label=Field<Panel>("reasonField").Controls.OfType<Label>().Single();
            Check(label.Text.Contains("optional")&&!label.Text.Contains('*'),"The reason caption clearly shows it is optional");
            reason.Text="A";Application.DoEvents();
            int layouts=0,themeChanges=0,noteChanges=0;
            var fields=Field<TableLayoutPanel>("fields");fields.Layout+=(_,_)=>layouts++;
            foreach(var task in Field<ModernButton[]>("taskButtons"))task.BackColorChanged+=(_,_)=>themeChanges++;
            Field<Label>("taskNote").TextChanged+=(_,_)=>noteChanges++;
            reason.Focus();
            var before=reason.Bounds;var scroll=Field<Panel>("reviewScroll").AutoScrollPosition;
            foreach(char letter in "n optional note") {reason.Controls.OfType<TextBox>().Single().AppendText(letter.ToString());Application.DoEvents();}
            Check(layouts==0&&themeChanges==0&&noteChanges==0,"Reason keystrokes do not rebuild the fields, fee summary or task styling");
            Check(reason.Bounds==before&&Field<Panel>("reviewScroll").AutoScrollPosition==scroll&&reason.ContainsFocus,
                "Typing a reason preserves the field position, scroll position and caret focus");
            reason.Text="  School-approved concession  ";Check(Reason()=="School-approved concession","An entered reason is retained and trimmed");
            reason.Text="   ";Check(Valid()&&Reason()=="Concession revised","Whitespace also leaves the reason optional");
            Field<ModernComboBox>("action").SelectedIndex=-1; // A clean close for this non-saving fixture.
            typeof(ChangeFeeDiscountForm).GetField("initial",flags)!.SetValue(form,typeof(ChangeFeeDiscountForm).GetMethod("Draft",flags)!.Invoke(form,null));
        }
        finally{CurrentUser.SignIn(previous.UserId,previous.Username,previous.RoleName,previous.MustChangePassword,previous.Permissions);}
    }
}
