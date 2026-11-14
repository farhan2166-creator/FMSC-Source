using System.Data;
using System.Reflection;
using FMSC_SMS;
using FMSC_SMS.Core;
using FMSC_SMS.Pages.Reports.Finance;
using FMSC_SMS.Pages.Security;
using FMSC_SMS.Pages.Finance.StudentAccounts;
using FMSC_SMS.Pages.Reports;

internal static partial class Program
{
    private static void CheckFeeAccountWorkspace()
    {
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var previous=(CurrentUser.UserId,CurrentUser.Username,CurrentUser.RoleName,CurrentUser.MustChangePassword,CurrentUser.GetPermissions().ToArray());
        var type=typeof(StudentAccountStatementPage);
        CurrentUser.SignIn(920,"Account fixture","Administrator",false,Array.Empty<string>());
        DataTable Statement(int id)
        {
            var table=new DataTable();
            foreach(string name in new[]{"TransactionType","StudentName","FeeTypeName","ReceiptNumber","PaymentMethod","Remarks"})table.Columns.Add(name);
            foreach(string name in new[]{"AdmissionId","TransactionId","FeePaymentId"})table.Columns.Add(name,typeof(int));
            table.Columns.Add("TransactionDate",typeof(DateTime));
            foreach(string name in new[]{"DebitAmount","PaidAmount","Balance"})table.Columns.Add(name,typeof(decimal));
            table.Rows.Add("CHARGE","Student "+id,"Tuition","","","Discount included",id,id,DBNull.Value,new DateTime(2026,10,1),1000m,400m,600m);
            table.Rows.Add("PAYMENT","Student "+id,"Payment","FR-test","Cash","Partial payment",id,id+100,id+100,new DateTime(2026,10,2),0m,400m,0m);
            return table;
        }
        Func<int,DateTime?,DateTime?,Task<(DataTable Transactions,decimal OpeningBalance)>> read=(id,_,_)=>Task.FromResult((Statement(id),0m));
        using var page=new StudentAccountStatementPage(read){Dock=DockStyle.Fill,BindingContext=new BindingContext()};
        T Field<T>(string name)=>(T)type.GetField(name,flags)!.GetValue(page)!;
        object? Invoke(string name,params object[] args)=>type.GetMethod(name,flags)!.Invoke(page,args);
        void Wait(Task task){PumpUntil(()=>task.IsCompleted,"Account asynchronous read finishes");task.GetAwaiter().GetResult();}
        StudentFeeAccountData Snapshot(int id)
        {
            var receipts=new DataTable();receipts.Columns.Add("ReceiptNumber");receipts.Columns.Add("TransactionDate",typeof(DateTime));
            receipts.Columns.Add("TotalSettled",typeof(decimal));receipts.Columns.Add("CashApplied",typeof(decimal));
            receipts.Rows.Add("FR-test",new DateTime(2026,10,2),800m,400m);
            var payments=new DataTable();
            foreach(string column in new[]{"ReceiptNumber","PaymentMethod","Status","StudentName"})payments.Columns.Add(column);
            foreach(string column in new[]{"FeePaymentId","RelatedFeePaymentId"})payments.Columns.Add(column,typeof(int));
            foreach(string column in new[]{"PaidAmount","Refunded","Corrected","ReleasedToCredit"})payments.Columns.Add(column,typeof(decimal));
            payments.Columns.Add("IsCreditEntry",typeof(bool));payments.Columns.Add("TransactionDate",typeof(DateTime));
            payments.Rows.Add("FR-test","Cash","Completed","Student "+id,id+100,DBNull.Value,400m,0m,0m,0m,false,new DateTime(2026,10,2));
            StudentFeeAccountData.DescribePayments(payments);
            return new(receipts,payments,600m,150m);
        }
        try
        {
            using(var collection=new FMSC_SMS.Pages.Finance.Collection.FeeCollectionPage(false))
            {
                var menu=(ContextMenuStrip)collection.GetType().GetField("studentFeesMenu",flags)!.GetValue(collection)!;
                Check(menu.Items.Cast<ToolStripItem>().Any(item=>item.Text==PageTitles.StudentFeeAccount)
                    &&!menu.Items.Cast<ToolStripItem>().Any(item=>item.Text=="Account Statement"),
                    "Fee Collection offers the consolidated Student Fee Account instead of the retired statement shortcut");
            }
            using(var contextual=new StudentFeeAccountDialog(new StudentAccountStatementPage(read)))
            {
                var account=DescendantControls(contextual).OfType<StudentAccountStatementPage>().Single();
                Check(contextual.Text==PageTitles.StudentFeeAccount&&type.GetField("accountMode",flags) is null
                    &&DescendantControls(account).OfType<TabControl>().Any(tabs=>tabs.Name=="accountViews"),
                    "The account has one consolidated workspace and no obsolete standalone mode");
                type.GetField("accountActionBusy",flags)!.SetValue(account,true);
                var closing=new FormClosingEventArgs(CloseReason.UserClosing,false);
                typeof(Form).GetMethod("OnFormClosing",flags)!.Invoke(contextual,new object[]{closing});
                Check(closing.Cancel,"The collection account dialog cannot close during an account operation");
                type.GetField("accountActionBusy",flags)!.SetValue(account,false);
                closing=new FormClosingEventArgs(CloseReason.UserClosing,false);
                typeof(Form).GetMethod("OnFormClosing",flags)!.Invoke(contextual,new object[]{closing});
                Check(!closing.Cancel&&!contextual.IsHandleCreated,"An idle contextual account closes normally without starting database loaders");
            }
            using(var shell=new MainDashboardForm((_,_)=>{}))
            {
                var commands=(List<NavigationCommand>)typeof(MainDashboardForm).GetField("navigationCommands",flags)!.GetValue(shell)!;
                Check(commands.Any(c=>c.Caption==PageTitles.StudentFeeAccount)&&!commands.Any(c=>c.Caption=="Student Fee Corrections"),"Full-access navigation consolidates statements and corrections into the account");
                Check(commands.Count(c=>c.Caption=="Fee Collection")==1,"The existing collection destination remains intact");
            }
            using(var reports=new ReportsHomePage((Action<Func<Control>,string,string>)((_,_,_)=>{})))
            {
                Check(!DescendantControls(reports).Any(c=>c.Text==PageTitles.StudentFeeAccount),"Reports no longer duplicates the operational account workspace");
                Check(!DescendantControls(reports).Any(c=>c.Text=="Family / Group Statements")&&DescendantControls(reports).Any(c=>c.Text==PageTitles.AnnualFeeReceipts),"Retired group statements stay absent while annual receipts remain available");
            }
            page.ReadAccount=id=>Task.FromResult(Snapshot(id));
            using var owner=new Form{ClientSize=new Size(1180,800),ShowInTaskbar=false};owner.Controls.Add(page);owner.Show();Application.DoEvents();
            var picker=Field<StudentPicker>("studentSearch");picker.SetStudent(41,"Student 41");Invoke("SelectSingleStudent");
            Wait((Task)Invoke("LoadStatement")!);
            Check(((DataGridView)typeof(V2Page).GetField("Grid",flags)!.GetValue(page)!).Rows.Count==1,"The established fee-balance statement retains its charge row");
            var receipts=Field<DataGridView>("accountReceipts");var payments=Field<DataGridView>("accountPayments");
            Check(Field<Label>("accountSummary").Text.Contains("Current dues: Rs. 600")&&Field<Label>("accountSummary").Text.Contains("Advance credit: Rs. 150"),"Student dues and credit are separate, without netting: "+Field<Label>("accountSummary").Text);
            Check(receipts.Rows.Count==1&&payments.Rows.Count==1,"Student receipts and payments share the selected account");
            var fees=(DataGridView)typeof(V2Page).GetField("Grid",flags)!.GetValue(page)!;
            Check(!Field<Label>("statementContext").Visible&&!fees.Columns["StatementStudent"].Visible&&!payments.Columns["StudentName"].Visible,"Single-student context is shown once in the search field");
            Check(!payments.Columns["Status"].Visible&&payments.Columns["AccountState"].Visible&&!payments.Columns["Refunded"].Visible,"Payment history shows one status and hides unused adjustment columns");
            Check(payments.Columns.Cast<DataGridViewColumn>().Where(column=>column.Visible).OrderBy(column=>column.DisplayIndex).First().Name=="TransactionDate","Payment history begins with its date");
            var paymentData=(DataTable)payments.DataSource!;paymentData.Rows[0]["Refunded"]=25m;
            Invoke("StyleAccountGrid",payments);
            Check(payments.Columns["Refunded"].Visible,"A real refund remains visible rather than being hidden with unused adjustments");
            paymentData.Rows[0]["Refunded"]=0m;Invoke("StyleAccountGrid",payments);
            Check(Field<DataGridView>("accountTransactions").Rows.Count==2,"Chronological statement retains charge and payment rows without changing existing statement calculations");
            Check(DescendantControls(page).OfType<ModernButton>().Any(button=>button.Text=="Charge & Payment Activity…"),
                "The account activity action names its charge/payment scope without implying a complete lifetime ledger");
            Exception? activityFailure=null;
            using(var activityTimer=new System.Windows.Forms.Timer {Interval=100})
            {
                activityTimer.Tick+=(_,_)=>{
                    activityTimer.Stop();
                    var dialog=Application.OpenForms.Cast<Form>().First(form=>form.Text=="Charge & Payment Activity");
                    try{Check(Field<DataGridView>("accountTransactions").FindForm()==dialog,"Full account activity keeps the detailed ledger available outside the main tabs");}
                    catch(Exception ex){activityFailure=ex;}
                    finally{dialog.Close();}
                };
                activityTimer.Start();Invoke("ShowAllAccountEntries");
            }
            if(activityFailure is not null)throw activityFailure;
            Check(!Field<DataGridView>("accountTransactions").IsDisposed&&Field<DataGridView>("accountTransactions").Parent is null,"Closing full activity preserves its records for reopening");
            Field<TabControl>("accountViews").SelectedIndex=1;
            Check(Field<TabControl>("accountViews").TabPages.Count==4
                && Field<TabControl>("accountViews").TabPages.Cast<TabPage>().All(tab=>!string.IsNullOrWhiteSpace(tab.Text)),
                "Account exposes four distinct usable financial workspaces without requiring historical tab captions");
            Check(!DescendantControls(Field<TabControl>("accountViews")).OfType<TabControl>().Any(), "Account views have no nested tabs");
            Application.DoEvents();Invoke("UpdateAccountActions");
            Check(Field<ModernButton>("correctPayment").Enabled,"Eligible cash payment exposes the existing correction workflow");
            Field<Dictionary<int,HashSet<int>>>("familySelections")[41]=new(){41,42};Invoke("OnFiltersChanged");
            Check(Field<Dictionary<int,HashSet<int>>>("familySelections")
                .TryGetValue(41, out var selectedFamily) && selectedFamily.SetEquals(new[] { 41, 42 }),
                "Family account scope contains the selected student and the selected sibling");
            Check(receipts.DataSource is null&&!Field<ModernButton>("correctPayment").Enabled,"Scope changes immediately clear actionable financial rows");
            Wait((Task)Invoke("LoadStatement")!);
            Check(Field<Label>("accountSummary").Text.Contains("2 students")&&Field<Label>("accountSummary").Text.Contains("1,200"),"Family summary sums the same existing student balance readers");
            Check(receipts.Rows.Count==1&&payments.Rows.Count==2,"Family receipt is deduplicated but both student payments remain selectable");
            Check(payments.Columns["StudentName"].Visible&&fees.Columns["StatementStudent"].Visible,"Family views retain student identities for each row");
            Check(!receipts.Columns["CashApplied"].Visible,"Deduplicated family list cannot mislabel one child's share as a family total");
            Field<TabControl>("accountViews").SelectedIndex=1;Application.DoEvents();
            Check(picker.SelectedId==41&&Field<Dictionary<int,HashSet<int>>>("familySelections").Count==1,"Switching views preserves student and family scope");
            Check(!DescendantControls(page).Any(control=>control.AccessibleName=="Bill grouping"),"Bill grouping no longer competes with sibling selection in the account header");
            Check(Field<ModernButton>("annualReceiptsButton").Enabled,"Annual receipts remain available for the main student while siblings are included");
            page.ReadAnnualYears=()=>{
                var years=new DataTable();years.Columns.Add("AcademicYearId",typeof(int));years.Columns.Add("YearName");years.Columns.Add("IsCurrent",typeof(bool));
                years.Rows.Add(76,"2025-2026",false);years.Rows.Add(77,"2026-2027",true);return Task.FromResult(years);
            };
            int annualStudent=0,annualYear=0;
            page.PreviewAnnualReceipts=(_,id,year,current,_)=>{Check(current(),"Annual preview retains its selected student context");annualStudent=id;annualYear=year;return Task.CompletedTask;};
            Exception? annualFailure=null;
            using(var annualTimer=new System.Windows.Forms.Timer {Interval=100})
            {
                annualTimer.Tick+=(_,_)=>{
                    annualTimer.Stop();var dialog=Application.OpenForms.OfType<AnnualReceiptYearDialog>().Single();
                    try
                    {
                        Check(dialog.ClientSize.Height <= AppMetrics.CompactChoiceDialogClientSize.Height +
                            SystemInformation.CaptionHeight + AppScale.S(8) &&
                            !DescendantControls(dialog).OfType<StudentPicker>().Any(),
                            "Annual receipt selector follows the compact choice size tier and keeps the existing student");
                        var year=DescendantControls(dialog).OfType<LookupBox>().Single();year.SelectedValue=76;
                        var primary=(ModernButton)dialog.AcceptButton!;
                        var cancel=(ModernButton)dialog.CancelButton!;
                        var buttons=new[]{primary,cancel};
                        Check(primary.DialogResult==DialogResult.OK && cancel.DialogResult==DialogResult.Cancel,
                            "Annual receipt chooser provides an explicit accept and cancel action");
                        var bounds=buttons.Select(button=>dialog.RectangleToClient(button.RectangleToScreen(button.ClientRectangle))).ToArray();
                        Check(bounds.Select(rect=>rect.Top).Distinct().Count()==1&&Math.Abs((bounds.Min(rect=>rect.Left)+bounds.Max(rect=>rect.Right))/2-dialog.ClientSize.Width/2)<=AppScale.Scale(8),"Annual selector actions are centered on one row: "+string.Join("; ",bounds.Select(rect=>rect.ToString()))+" in "+dialog.ClientSize);
                        using var image=new Bitmap(dialog.Width,dialog.Height);dialog.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(Path.GetFullPath("artifacts/annual-receipt-year.png"));
                        Check(year.SelectedId==76&&primary.Enabled,"Choosing a year enables the annual preview");
                        primary.PerformClick();
                    }
                    catch(Exception ex){using var failureImage=new Bitmap(dialog.Width,dialog.Height);dialog.DrawToBitmap(failureImage,new Rectangle(Point.Empty,failureImage.Size));failureImage.Save(Path.GetFullPath("artifacts/annual-receipt-year.png"));annualFailure=ex;dialog.Close();}
                };
                annualTimer.Start();Wait((Task)Invoke("OpenAnnualReceipts")!);
            }
            if(annualFailure is not null)throw annualFailure;
            Check(annualStudent==41&&annualYear==76,"The compact selector previews only the main student and the chosen year");
            int siblingRow=payments.Rows.Cast<DataGridViewRow>().Single(row=>Convert.ToInt32(row.Cells["AccountAdmissionId"].Value)==42).Index;
            Wait((Task)Invoke("OpenRowStudentAsync",payments,siblingRow)!);
            Check(picker.SelectedId==42&&Field<Dictionary<int,HashSet<int>>>("familySelections").Count==0&&payments.Rows.Count==1,"Opening a sibling row makes that child the main student and reloads their individual account");
            Check(Field<Dictionary<int,HashSet<int>>>("familySelections").Count==0 &&
                picker.SelectedId==42,
                "Opening a sibling resets family scope to that individual student");
            picker.SetStudent(41,"Student 41");Invoke("SelectSingleStudent");Wait((Task)Invoke("LoadStatement")!);
            CurrentUser.SignIn(921,"Read only","Operator",false,new[]{"Students.View","Reports.View","Fees.View"});
            Invoke("UpdateAccountActions");Check(!Field<ModernButton>("correctPayment").Enabled,"A different signed-in user cannot act on the preceding user's financial rows");
            Wait((Task)Invoke("RefreshAccountAsync")!);Invoke("UpdateAccountActions");
            Check(!Field<ModernButton>("correctPayment").Visible,"Read-only operator is not offered corrections");
            CurrentUser.SignIn(923,"Collections operator","Operator",false,new[]{"Students.View","Fees.Collect","Fees.View","Fees.Adjust"});
            using(var restricted=new StudentAccountStatementPage((_,_,_)=>throw new Exception("Report reader must not run for collection-only access")){BindingContext=new BindingContext()})
            {
                restricted.ReadAccount=id=>Task.FromResult(Snapshot(id));
                using(var shell=new MainDashboardForm((_,_)=>{}))
                {
                    var commands=(List<NavigationCommand>)typeof(MainDashboardForm).GetField("navigationCommands",flags)!.GetValue(shell)!;
                    Check(commands.Any(c=>c.Caption==PageTitles.StudentFeeAccount)&&!commands.Any(c=>c.Caption=="Student Fee Corrections"),"Collection-only operators reach the consolidated account without a redundant correction page");
                }
                using var restrictedWindow=new Form{ClientSize=new Size(1100,800),ShowInTaskbar=false};restrictedWindow.Controls.Add(restricted);restricted.Dock=DockStyle.Fill;restrictedWindow.Show();Application.DoEvents();
                var restrictedPicker=(StudentPicker)type.GetField("studentSearch",flags)!.GetValue(restricted)!;restrictedPicker.SetStudent(41,"Student 41");
                type.GetMethod("SelectSingleStudent",flags)!.Invoke(restricted,null);
                Wait((Task)type.GetMethod("LoadStatement",flags)!.Invoke(restricted,null)!);
                var restrictedViews=(TabControl)type.GetField("accountViews",flags)!.GetValue(restricted)!;
                Check(restrictedViews.SelectedIndex==1,"Collection-only account opens Payment History instead of unauthorized fees");
                restrictedViews.SelectedIndex=0;Check(restrictedViews.SelectedIndex==1,"Statement tab does not bypass report authorization");
                Check(((DataGridView)type.GetField("accountPayments",flags)!.GetValue(restricted)!).Rows.Count==1,"Existing authorized payment history remains accessible without report permission");
                Check(((ModernButton)type.GetField("recoverPayment",flags)!.GetValue(restricted)!).Enabled,"Save recovery remains reachable independently of payment eligibility");
                restrictedWindow.Close();
            }
            CurrentUser.SignIn(921,"Read only","Operator",false,new[]{"Students.View","Reports.View","Fees.View"});
            using(var context=(PaymentCorrectionReview)Activator.CreateInstance(typeof(PaymentCorrectionReview),flags,null,new object?[]{false,null,null,41,101},null)!)
            {
                var adjustmentType=context.GetType();
                adjustmentType.GetField("admissionId",flags)!.SetValue(context,42);
                adjustmentType.GetField("selectedPaymentId",flags)!.SetValue(context,101);
                Check(!(bool)adjustmentType.GetMethod("ExecuteIsAvailable",flags)!.Invoke(context,null)!,"A contextual correction refuses a different student's payment context");
            }
            CurrentUser.SignIn(920,"Account fixture","Administrator",false,Array.Empty<string>());
            var deferred=new TaskCompletionSource<StudentFeeAccountData>();page.ReadAccount=_=>deferred.Task;
            Task pending=(Task)Invoke("RefreshAccountAsync")!;
            picker.SetStudent(43,"Student 43");Invoke("SelectSingleStudent");deferred.SetResult(Snapshot(41));Wait(pending);
            Check(receipts.DataSource is null&&payments.DataSource is null,"An old asynchronous account response cannot populate a new student");
            page.ReadAccount=id=>Task.FromResult(Snapshot(id));Wait((Task)Invoke("LoadStatement")!);
            var paymentTable=(DataTable)payments.DataSource!;paymentTable.Rows[0]["IsCreditEntry"]=true;Invoke("UpdateAccountActions");
            Check(!Field<ModernButton>("correctPayment").Enabled,"Credit applications never expose cash-payment corrections");
            Directory.CreateDirectory("artifacts/fee-account");
            foreach(var size in new[]{new Size(1180,800),new Size(900,650),new Size(1500,950)})
            {
                owner.ClientSize=size;Field<TabControl>("accountViews").SelectedIndex=0;Application.DoEvents();
                var tabs=Field<TabControl>("accountViews");Check(tabs.Width>500&&tabs.Height>150,"Account views retain working space at "+size);
                var period=Field<Label>("closingBalance");
                Check(period.Height>=period.Font.GetHeight()&&period.Bottom<=period.Parent!.ClientSize.Height,"Period summary amounts remain fully visible at "+size);
                Check(page.SelectNextControl(picker,true,true,true,true),"Account keyboard navigation finds the next focusable control");
                foreach(var viewport in DescendantControls(page).OfType<ScrollableControl>())if(viewport.AutoScroll)viewport.AutoScrollPosition=Point.Empty;
                using var image=new Bitmap(page.Width,page.Height);page.DrawToBitmap(image,page.ClientRectangle);image.Save(Path.GetFullPath($"artifacts/fee-account/account-{size.Width}.png"));
                if(size.Width==1500)
                {
                    Field<TabControl>("accountViews").SelectedIndex=1;Application.DoEvents();
                    using var paymentImage=new Bitmap(page.Width,page.Height);page.DrawToBitmap(paymentImage,page.ClientRectangle);paymentImage.Save(Path.GetFullPath("artifacts/fee-account/payment-history.png"));
                }
            }
            // The actual snapshot presentation is shared across legacy/current document formats.
            foreach(int version in new[]{1,2,3})
            {
                using var saved=new DataSet();var header=new DataTable();header.Columns.Add("SnapshotVersion",typeof(int));header.Rows.Add(version);saved.Tables.Add(header);
                var members=new DataTable();members.Columns.Add("AdmissionId",typeof(int));members.Columns.Add("AdmissionNumber");members.Columns.Add("StudentName");members.Rows.Add(41,"6590","Student 41");saved.Tables.Add(members);
                var lines=new DataTable();lines.Columns.Add("AdmissionId",typeof(int));lines.Columns.Add("ChargeId",typeof(int));lines.Columns.Add("FeeTypeName");lines.Columns.Add("FeePeriod",typeof(DateTime));lines.Columns.Add("AmountBefore",typeof(decimal));lines.Rows.Add(41,100,"Tuition",new DateTime(2026,10,1),1000m);saved.Tables.Add(lines);
                using var later=Snapshot(41).Payments;
                using var content=(Control)Invoke("BuildReceiptDetail",saved,"FR-test",later)!;
                using var window=(ApplicationWindowForm)type.GetMethod("CreateRelatedReportDialog",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,new object[]{"Receipt fixture",content})!;
                window.Show();Application.DoEvents();Check(content.Width>700,"Snapshot version "+version+" opens the existing receipt-detail structure");
                var receiptTabs=DescendantControls(content).OfType<TabControl>().Single();receiptTabs.SelectedIndex=1;Application.DoEvents();
                var lineGrid=DescendantControls(content).OfType<DataGridView>().Single(grid=>grid.Name=="receiptLines");
                Check(!lineGrid.Columns["AdmissionId"].Visible&&!lineGrid.Columns["ChargeId"].Visible&&lineGrid.Columns["Student"].Visible,"Nested receipt lines identify students without exposing database IDs");
                Check(lineGrid.Columns.Cast<DataGridViewColumn>().Where(column=>column.Visible).OrderBy(column=>column.DisplayIndex).Select(column=>column.Name).SequenceEqual(new[]{"Student","FeePeriod","FeeTypeName","AmountBefore"}),"Nested receipt lines show student, month, fee and amount in natural order");
                Check(!saved.Tables[2].Columns.Contains("Student"),"Readable receipt presentation does not change the saved snapshot");
                Check(Convert.ToString(lineGrid.Rows[0].Cells["FeePeriod"].FormattedValue)=="Oct 2026","Nested receipt billing periods show a month rather than an artificial first-day date");
                foreach(int width in new[]{1180,820})
                {
                    window.Width=AppScale.Scale(width);Application.DoEvents();
                    var actions=DescendantControls(content).OfType<ModernButton>().Where(button=>button.Visible&&new[]{"Reprint / Child Copy","Row Details","Go to Payment History"}.Contains(button.Text)).ToArray();
                    var bounds=actions.Select(button=>content.RectangleToClient(button.RectangleToScreen(button.ClientRectangle))).ToArray();
                    Check(bounds.Length==3&&bounds.Select(rect=>rect.Top).Distinct().Count()==1,"Receipt actions stay on one row at width "+width);
                    Check(bounds.All(rect=>rect.Left>=0&&rect.Right<=content.ClientSize.Width)&&Math.Abs((bounds.Min(rect=>rect.Left)+bounds.Max(rect=>rect.Right))/2-content.ClientSize.Width/2)<=AppScale.Scale(8),"Receipt action group is centered and fully visible at width "+width);
                }
                window.ClientSize=new Size(AppScale.Scale(1180),AppScale.Scale(760));Application.DoEvents();
                if(version==3){using var image=new Bitmap(window.Width,window.Height);window.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(Path.GetFullPath("artifacts/fee-account/receipt-details.png"));}
                window.Close();
            }
            owner.Close();
            var scale=(AppScale.Dpi,AppScale.RawFactor);
            try
            {
                foreach(int dpi in new[]{96,120,144,192})
                {
                    var size=new Size(1280*dpi/96,850*dpi/96);AppScale.Recompute(size,dpi);
                    using var scaled=new StudentAccountStatementPage(read){Dock=DockStyle.Fill};
                    using var window=new Form{ClientSize=size,ShowInTaskbar=false};window.Controls.Add(scaled);window.Show();Application.DoEvents();
                    var tabs=(TabControl)type.GetField("accountViews",flags)!.GetValue(scaled)!;
                    Check(tabs.Width>AppScale.Scale(500)&&tabs.Height>AppScale.Scale(130),"Account layout has usable space with synthetic "+dpi+" DPI geometry");window.Close();
                }
            }
            finally{AppScale.Recompute(new Size((int)(1280*scale.RawFactor),(int)(720*scale.RawFactor)),scale.Dpi);}
        }
        finally{CurrentUser.SignIn(previous.UserId,previous.Username,previous.RoleName,previous.MustChangePassword,previous.Item5);}
    }
    private static IEnumerable<Control> DescendantControls(Control parent)=>parent.Controls.Cast<Control>().SelectMany(c=>new[]{c}.Concat(DescendantControls(c)));
}
