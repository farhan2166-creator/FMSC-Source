using System.Data;
using System.Reflection;
using Microsoft.Data.SqlClient;
using FMSC_SMS.Core;
using FMSC_SMS.Pages.Finance.Collection;
using FMSC_SMS.Pages.Finance.StudentAccounts;
using FMSC_SMS.Pages.Security;

internal static partial class Program
{
    private static void CheckCollectionFeeActions()
    {
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var previous=(CurrentUser.UserId,CurrentUser.Username,CurrentUser.RoleName,CurrentUser.MustChangePassword,Permissions:CurrentUser.GetPermissions().ToArray());
        T Field<T>(object owner,string name)=>(T)owner.GetType().GetField(name,flags)!.GetValue(owner)!;
        object? Call(object owner,string name,params object[] args)=>owner.GetType().GetMethod(name,flags)!.Invoke(owner,args);
        try
        {
            CurrentUser.SignIn(7781,"Collection action fixture","Administrator",false,Array.Empty<string>());
            using var context=new DataTable();
            foreach(string name in new[]{"Frequency","Status","ConcessionLabel","StudentName","AdmissionNumber","YearName"})context.Columns.Add(name);
            foreach(string name in new[]{"StartDate","EndDate","EnrollmentDate","BillingMonth"})context.Columns.Add(name,typeof(DateTime));
            context.Columns.Add("StudentFeeAssignmentId",typeof(int));context.Columns.Add("BillingChargeId",typeof(long));
            context.Columns.Add("Balance",typeof(decimal));
            int year=DateTime.Today.Year;var month=new DateTime(year,3,1);
            var current=new DateTime(year,DateTime.Today.Month,1);
            var row=context.Rows.Add("Monthly","Active",DBNull.Value,"Preview Student","542","Preview year",new DateTime(year,1,1),new DateTime(year,12,31),new DateTime(year,1,1),month,41,321L,300m);
            using(var routed=context.Copy())
            {
                routed.Columns.Add("FeeTypeName");routed.Columns.Add("StandardAmount",typeof(decimal));routed.Columns.Add("DiscountAmount",typeof(decimal));
                var bill=routed.Rows[0];bill["FeeTypeName"]="Library Fee";bill["StandardAmount"]=700m;bill["DiscountAmount"]=200m;
                using var planRoute=routed.Copy();planRoute.Columns.Remove("BillingChargeId");
                var choiceType=typeof(FeeCollectionPage).GetNestedType("ChargeConcessionAction",BindingFlags.NonPublic)!;
                var create=typeof(FeeCollectionPage).GetMethod("CreateCollectionFeeDialog",BindingFlags.Static|BindingFlags.NonPublic)!;
                foreach(int choice in new[]{0,1,2,3})
                {
                    using var form=(ChangeFeeDiscountForm)create.Invoke(null,new object?[]{41,planRoute.Rows[0],Enum.ToObject(choiceType,choice),null})!;
                    form.Show();Application.DoEvents();
                    Check(Field<ModernButton>(form,"save").Visible&&!Field<ModernButton>(form,"review").Visible,
                        "Each collection action confirms on its entry screen without a separate review stage");
                    Check(Field<Panel>(form,"scopeField").Visible&&Field<Panel>(form,"reasonField").Visible,
                        "Collection keeps the admission editor's scope and reason choices");
                    Check(!Field<Panel>(form,"actionField").Visible&&!Field<TabControl>(form,"comparisons").Visible,
                        "A collection menu action hides only the action tabs, with no review screen");
                }
                var clickedMonth=current.Month==1?current.AddMonths(1):current.AddMonths(-1);
                using var correction=(ChangeFeeDiscountForm)create.Invoke(null,new object?[]{41,planRoute.Rows[0],Enum.ToObject(choiceType,2),clickedMonth})!;
                Check(Equals(Field<ModernComboBox>(correction,"fromMonth").SelectedItem,current)&&Equals(Field<ModernComboBox>(correction,"throughMonth").SelectedItem,current),
                    "Monthly collection actions start in the current month even when opened from a charge for a different month");
            }
            using(var plan=context.Copy())
            {
                plan.Columns.Remove("BillingChargeId");
                for(int choice=0;choice<4;choice++)
                {
                    using var form=new ChangeFeeDiscountForm(41,"Tuition Fee",700m,0m,plan.Rows[0],
                        _=>Task.FromResult(new DataSet()),_=>throw new InvalidOperationException("Unexpected write"));
                    form.ConfigureCollectionAction(choice);form.Show();Application.DoEvents();
                    Check(!Field<Panel>(form,"actionField").Visible,"Collection action dialogs hide the four action tabs");
                    Check(Field<ModernComboBox>(form,"action").SelectedIndex==choice,"Each collection dialog selects its menu action");
                    Check(Equals(Field<ModernComboBox>(form,"fromMonth").SelectedItem,current)&&Equals(Field<ModernComboBox>(form,"throughMonth").SelectedItem,current),
                        "The owner-approved collection default resets both months to the starting month");
                    Check(Field<ModernComboBox>(form,"scope").Items.Count==3,"Collection retains the shared fee-plan and existing-bill scopes");
                }
                using var admission=new ChangeFeeDiscountForm(41,"Tuition Fee",700m,0m,plan.Rows[0],
                    _=>Task.FromResult(new DataSet()),_=>throw new InvalidOperationException("Unexpected write"));
                Check(Equals(Field<ModernComboBox>(admission,"throughMonth").SelectedItem,new DateTime(year,12,1)),"Admission retains the academic-year end default");
                using var future=plan.Copy();
                foreach(string name in new[]{"EffectiveFrom","EffectiveTo","BillingStartDate"})future.Columns.Add(name,typeof(DateTime));
                var firstBillingMonth=new DateTime(DateTime.Today.Year,DateTime.Today.Month,1).AddMonths(1);
                future.Rows[0]["EffectiveFrom"]=firstBillingMonth;
                future.Rows[0]["BillingStartDate"]=firstBillingMonth;
                future.Rows[0]["EffectiveTo"]=firstBillingMonth.AddMonths(2);
                future.Rows[0]["EndDate"]=firstBillingMonth.AddMonths(6);
                using var futureCorrection=new ChangeFeeDiscountForm(41,"Tuition Fee",700m,0m,future.Rows[0],
                    _=>Task.FromResult(new DataSet()),_=>throw new InvalidOperationException("Unexpected write"));
                futureCorrection.ConfigureCollectionAction(2);
                foreach(string name in new[]{"fromMonth","throughMonth"})
                {
                    var picker=Field<ModernComboBox>(futureCorrection,name);
                    Check(Equals(picker.SelectedItem,firstBillingMonth)&&picker.Items.Cast<DateTime>().SequenceEqual(Enumerable.Range(0,3).Select(firstBillingMonth.AddMonths)),
                        "A future-start fee defaults to its first billing month and limits corrections to its effective period");
                }
                var from=Field<ModernComboBox>(futureCorrection,"fromMonth");
                var through=Field<ModernComboBox>(futureCorrection,"throughMonth");
                from.SelectedItem=firstBillingMonth.AddMonths(1);
                Check(through.Items.Cast<DateTime>().SequenceEqual(new[]{firstBillingMonth.AddMonths(2)})&&Equals(through.SelectedItem,firstBillingMonth.AddMonths(2)),
                    "Moving Collection's From month forward starts Through choices one month later");
            }
            SqlParameter[]? captured=null;int reads=0,writes=0;bool stale=false;
            Task<DataSet> Review(SqlParameter[] args)
            {
                reads++;captured=args;var result=new DataSet();var summary=result.Tables.Add();
                summary.Columns.Add("Snapshot");summary.Columns.Add("Difference",typeof(decimal));summary.Columns.Add("ReleasedPaidAmount",typeof(decimal));summary.Columns.Add("IssuedBills",typeof(int));
                decimal net=700m-Convert.ToDecimal(args.Single(p=>p.ParameterName=="@DiscountAmount").Value)-(stale?50m:0m);
                summary.Rows.Add("focused-bill",net-500m,0m,1);
                var bills=result.Tables.Add();bills.Columns.Add("Current Bill",typeof(decimal));bills.Columns.Add("Proposed Bill",typeof(decimal));bills.Rows.Add(500m,net);
                result.Tables.Add();return Task.FromResult(result);
            }
            Task<MutationWriteResult> Save(SqlParameter[] args){writes++;captured=args;return Task.FromResult(new MutationWriteResult(MutationOutcome.Saved));}
            object Arg(string name)=>captured!.Single(p=>p.ParameterName==name).Value;
            void Show(Form form){form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-20000,-20000);form.ShowInTaskbar=false;form.Show();Application.DoEvents();}
            void Finish(Task task)
            {
                DateTime deadline=DateTime.UtcNow.AddSeconds(5);
                while(!task.IsCompleted&&DateTime.UtcNow<deadline)Application.DoEvents();
                Check(task.IsCompleted,"Asynchronous bill confirmation finishes without another operator step");
                task.GetAwaiter().GetResult();
            }
            void Capture(Form form,string name)
            {
                Directory.CreateDirectory("artifacts/collection-actions");
                using var bitmap=new Bitmap(form.Width,form.Height);form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));
                bitmap.Save(Path.GetFullPath($"artifacts/collection-actions/{name}.png"));
                foreach(var button in Descendants(form).OfType<ModernButton>().Where(button=>button.Visible))
                    Check(form.ClientRectangle.Contains(form.RectangleToClient(button.RectangleToScreen(button.ClientRectangle))),"Focused dialog buttons stay within the window");
            }
            using(var form=new ChangeFeeDiscountForm(41,"Library Fee",700m,200m,row,Review,Save))
            {
                form.ConfigureCollectionBill();Show(form);
                Check(Field<ModernButton[]>(form,"taskButtons").All(button=>!button.Visible&&!button.Enabled),"A collection discount has no action-switching buttons");
                Check(!Field<ModernComboBox>(form,"scope").Visible&&!Field<ModernComboBox>(form,"concession").Visible
                    &&!Field<ModernTextBox>(form,"newRate").Visible&&!Field<ModernComboBox>(form,"fromMonth").Visible,
                    "The focused bill has no fee-plan, type, rate, waiver or date choices");
                Check(Field<ModernButton>(form,"save").Visible&&!Field<ModernButton>(form,"review").Visible
                    &&!Field<ModernButton>(form,"editChoices").Visible&&!Field<TabControl>(form,"comparisons").Visible,
                    "The collection shortcut offers confirmation on the entry screen without a review or issued-bills step");
                Field<ModernTextBox>(form,"amount").Text="200";
                Call(form,"SaveAsync");Check(reads==0&&writes==0,"An unchanged discount never submits a write");
                Field<ModernTextBox>(form,"amount").Text="500";
                Call(form,"SaveAsync");Check(reads==0&&Field<Label>(form,"impact").Text.Contains("Waive Remaining Dues"),"A full remaining waiver must use the dedicated action");
                Field<ModernTextBox>(form,"amount").Text="300";
                Check(Field<Label>(form,"taskNote").Text.Contains("Rs. 200"),"The draft preserves paid money and shows the resulting unpaid balance");
                Capture(form,"change-discount-entry");
                Field<ModernButton>(form,"save").PerformClick();Application.DoEvents();
                Check(reads==1&&!(bool)Arg("@UpdateAgreement")&&(bool)Arg("@ApplyToBills")&&(long)Arg("@BillingChargeId")==321L
                    &&!(bool)Arg("@FullFeeWaiver")&&Arg("@NewAmount") is DBNull&&Arg("@ConcessionLabel") is DBNull,
                    "The focused discount changes only the selected bill with no hidden plan/rate/waiver action");
                Check((DateTime)Arg("@EffectiveFrom")==month&&(DateTime)Arg("@EffectiveThrough")==month,"A monthly fee still targets exactly its clicked bill period");
                Check(writes==1&&form.Changed&&(string)Arg("@ExpectedSnapshot")=="focused-bill","One confirmation checks the bill and saves with that snapshot exactly once");
                Check((string)Arg("@Reason")=="Concession revised","Direct confirmation retains the optional reason and neutral description");
            }
            using(var form=new ChangeFeeDiscountForm(41,"Library Fee",700m,200m,row,Review,Save))
            {
                form.ConfigureCollectionBill(removeDiscount:true);Show(form);
                Check(!Field<ModernTextBox>(form,"amount").Visible&&Field<ModernTextBox>(form,"reason").Visible,"Removing a discount asks only for its reason");
                Field<ModernTextBox>(form,"reason").Text="Discount withdrawn";Call(form,"SaveAsync");
                Check((decimal)Arg("@DiscountAmount")==0m&&!(bool)Arg("@FullFeeWaiver")&&!(bool)Arg("@UpdateAgreement"),"Removal clears only the selected bill discount");
                Check(form.Changed&&writes==2,"Removing a discount also saves from its entry screen");
            }
            using(var form=new ChangeFeeDiscountForm(41,"Library Fee",700m,200m,row,Review,Save))
            {
                form.ConfigureCollectionBill();Show(form);
                Field<ModernComboBox>(form,"action").SelectedIndex=1;int before=reads;Call(form,"SaveAsync");
                Check(reads==before,"A hidden action change cannot escape the locked task");
            }
            using(var form=new ChangeFeeDiscountForm(41,"Library Fee",700m,200m,row,Review,Save))
            {
                form.ConfigureCollectionBill();Show(form);Field<ModernTextBox>(form,"amount").Text="300";stale=true;
                int before=writes;Call(form,"SaveAsync");stale=false;
                Check(!form.Changed&&writes==before&&Field<Label>(form,"impact").Text.Contains("reload"),
                    "A changed or clamped bill is rejected before any write instead of silently saving another discount");
                Check(Field<ModernTextBox>(form,"amount").Text=="300"&&Field<ModernButton>(form,"save").Visible
                    &&!Field<TabControl>(form,"comparisons").Visible,"A failed bill check retains the entered amount on the same screen");
                Capture(form,"discount-changed-before-confirmation");
            }
            using(var firstBill=context.Copy())
            {
                firstBill.Rows[0]["Balance"]=10000m;
                Task<DataSet> NewDiscount(SqlParameter[] args)
                {
                    var result=Review(args).GetAwaiter().GetResult();
                    decimal entered=Convert.ToDecimal(args.Single(p=>p.ParameterName=="@DiscountAmount").Value);
                    result.Tables[0].Rows[0]["Difference"]=-entered;
                    result.Tables[1].Rows[0]["Current Bill"]=10000m;
                    result.Tables[1].Rows[0]["Proposed Bill"]=10000m-entered;
                    return Task.FromResult(result);
                }
                using var form=new ChangeFeeDiscountForm(41,"Examination Fee",10000m,0m,firstBill.Rows[0],NewDiscount,Save);
                form.ConfigureCollectionBill();Show(form);Field<ModernTextBox>(form,"amount").Text="3000";
                Check(form.Text.StartsWith("Give Discount")&&Field<Label>(form,"taskNote").Text.Contains("Rs. 7,000"),
                    "A new 3,000 discount on a 10,000 fee immediately shows 7,000 remaining on the entry screen");
                Capture(form,"give-discount-single-step");
                int before=writes;Field<ModernButton>(form,"save").PerformClick();Application.DoEvents();
                Check(form.Changed&&writes==before+1&&(decimal)Arg("@DiscountAmount")==3000m,
                    "Give Discount saves the entered whole amount with one confirmation");
            }
            var held=new TaskCompletionSource<DataSet>(TaskCreationOptions.RunContinuationsAsynchronously);
            DataSet? heldResult=null;
            Task<DataSet> Hold(SqlParameter[] args){heldResult=Review(args).GetAwaiter().GetResult();return held.Task;}
            using(var form=new ChangeFeeDiscountForm(41,"Library Fee",700m,200m,row,Hold,Save))
            {
                form.ConfigureCollectionBill();Show(form);Field<ModernTextBox>(form,"amount").Text="300";
                int beforeReads=reads,beforeWrites=writes;
                var saving=(Task)Call(form,"SaveAsync")!;
                Check(!saving.IsCompleted&&!Field<ModernButton>(form,"save").Enabled&&!Field<TableLayoutPanel>(form,"fields").Enabled,
                    "Inputs and confirmation stay disabled throughout the background bill check");
                Finish((Task)Call(form,"SaveAsync")!);
                Check(reads==beforeReads+1&&writes==beforeWrites,"A duplicate confirmation cannot start another preview or save");
                held.SetResult(heldResult!);Finish(saving);
                Check(form.Changed&&writes==beforeWrites+1,"The original confirmation saves once when its background check finishes");
            }
            held=new TaskCompletionSource<DataSet>(TaskCreationOptions.RunContinuationsAsynchronously);
            using(var form=new ChangeFeeDiscountForm(41,"Library Fee",700m,200m,row,Hold,Save))
            {
                form.ConfigureCollectionBill();Show(form);Field<ModernTextBox>(form,"amount").Text="300";
                int before=writes;var saving=(Task)Call(form,"SaveAsync")!;
                Field<ModernTextBox>(form,"amount").Text="350";
                held.SetResult(heldResult!);Finish(saving);
                Check(!form.Changed&&writes==before&&Field<ModernTextBox>(form,"amount").Text=="350",
                    "A draft changed during the background check is retained and never submitted with an older snapshot");
            }
            int uncertainWrites=0;
            Task<MutationWriteResult> Unconfirmed(SqlParameter[] args)
            {uncertainWrites++;return Task.FromResult(new MutationWriteResult(MutationOutcome.Unconfirmed));}
            using(var form=new ChangeFeeDiscountForm(41,"Library Fee",700m,200m,row,Review,Unconfirmed))
            {
                form.ConfigureCollectionBill();Show(form);Field<ModernTextBox>(form,"amount").Text="300";
                Finish((Task)Call(form,"SaveAsync")!);
                Check(!form.Changed&&Field<ModernButton>(form,"check").Visible&&!Field<ModernButton>(form,"save").Visible,
                    "An uncertain save exposes recovery and blocks another confirmation");
                Finish((Task)Call(form,"SaveAsync")!);
                Check(uncertainWrites==1,"An uncertain save can never be repeated by clicking confirmation again");
            }
            row["Balance"]=0m;
            using(var form=new ChangeFeeDiscountForm(41,"Library Fee",700m,700m,row,Review,Save))
            {
                bool refused=false;try{form.ConfigureCollectionBill();}catch(InvalidOperationException){refused=true;}
                Check(refused,"A newly settled or waived bill is rechecked before opening an editable collection dialog");
            }
            using(var form=new ChangeFeeDiscountForm(0,"Fee Change Recovery",0m,0m))
            {
                form.ConfigureRecoveryOnly();Show(form);
                Check(Field<ModernButton[]>(form,"taskButtons").All(button=>!button.Visible)&&!Field<ModernButton>(form,"review").Visible,
                    "Save recovery is available separately without opening financial edit choices");
            }
            var waiver=FeeConcessionActions.WaiverArgs(row,false,"snapshot");
            Check(!(bool)waiver.Single(p=>p.ParameterName=="@UpdateAgreement").Value
                &&(bool)waiver.Single(p=>p.ParameterName=="@FullFeeWaiver").Value
                &&!(bool)waiver.Single(p=>p.ParameterName=="@ApplyAvailableCredit").Value,
                "Direct waiver still preserves the saved fee plan and does not spend credit");
            using(var page=new FeeCollectionPage(false))
            {
                var grid=Field<DataGridView>(page,"charges");var fee=grid.Rows[grid.Rows.Add()];
                fee.Cells["BillingChargeId"].Value=321L;fee.Cells["FeeTypeName"].Value="Library Fee";fee.Cells["BillingMonth"].Value="Mar";
                fee.Cells["Balance"].Value=300m;fee.Cells["GrossAmount"].Value=700m;fee.Cells["GrossAmount"].Tag=0m;
                fee.Cells["AppliedDiscount"].Value=0m;fee.Cells["NetAmount"].Value=700m;fee.Cells["Amount"].Value=0m;
                var menu=Field<ContextMenuStrip>(page,"chargeMenu");
                string[] Menu(){Call(page,"BuildChargeMenu",fee.Index);return menu.Items.OfType<ToolStripMenuItem>().Where(item=>item.Enabled).Select(item=>item.Text??"").ToArray();}
                var items=Menu();
                Check(new[]{"Give Discount…","Waive Fee…","Correct Fee Amount…","View Fee Details"}.All(items.Contains),"Unpaid fees expose applicable separate fee actions and details");
                Check(!items.Contains("Remove Discount…"),"Fees without a discount do not offer Remove Discount");
                Check(!items.Any(text=>text.Contains("Sibling")||text.Contains("Review bill terms")),"Collection has no generic action-picker entry");
                fee.Cells["AppliedDiscount"].Value=200m;fee.Cells["GrossAmount"].Tag=200m;items=Menu();
                Check(items.Contains("Give Discount…")&&items.Contains("Remove Discount…")&&items.Contains("Correct Fee Amount…"),"The four action entries remain available with an existing discount");
                fee.Cells["Balance"].Value=0m;fee.Cells["AppliedDiscount"].Value=700m;fee.Cells["GrossAmount"].Tag=700m;
                using var source=new DataTable();source.Columns.Add("PendingWaiver",typeof(bool));source.Columns.Add("PaidAmount",typeof(decimal));var waived=source.Rows.Add(true,0m);
                typeof(FeeCollectionPage).GetMethod("ApplyWaiverReviewState",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,new object[]{fee,waived});
                Check(fee.ReadOnly&&fee.Cells["Pay"] is DataGridViewTextBoxCell&&fee.Cells["FeeTypeName"].Value!.ToString()!.Contains("Waived"),"Waived rows are clearly identified and cannot receive payment");
                items=Menu();Check(items.SequenceEqual(new[]{"View Fee Details"}),"A fully waived fee offers no discount, waive, correction or cancellation action");
                Check(!(bool)Call(page,"HasOutstandingCharges")!,"A saved waiver does not enable collection");
                Check(!((IEnumerable<DataGridViewRow>)Call(page,"SettlementRows",null!,null!)!).Any(),"A saved waiver is excluded from payment and credit allocation");
                Call(page,"ClearPayNowAmounts");Check((string)fee.Cells["Pay"].Value=="—","Payment resets preserve the nonpayable row marker");
                Call(page,"UpdateProcessCollectionAvailability");
                Check((bool)Call(page,"HasPendingWaiverRows")! && !(bool)Call(page,"HasOutstandingCharges")!
                    && !Field<ModernButton>(page,"executeButton").Enabled,
                    "A waiver-only visit retains its waiver result without offering payment or a zero-total collection");
                CurrentUser.SignIn(7782,"Read-only fixture","Accountant",false,new[]{"Fees.View"});
                fee.Cells["Balance"].Value=100m;items=Menu();Check(items.SequenceEqual(new[]{"View Fee Details"}),"Read-only users get only fee details even when dues remain");
            }
        }
        finally{CurrentUser.SignIn(previous.UserId,previous.Username,previous.RoleName,previous.MustChangePassword,previous.Permissions);}
    }
}
