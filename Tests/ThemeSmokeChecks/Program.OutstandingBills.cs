using System.Data;
using System.Reflection;
using FMSC_SMS.Core;
using FMSC_SMS.Pages.Finance.Collection;
using FMSC_SMS.Pages.Security;

internal static partial class Program
{
    private static void CheckOutstandingBills()
    {
        CheckReportClassSectionFilters();
        CheckOutstandingBillOrder();
        OutstandingBillScope Scope(BillBalanceComparison comparison,decimal amount=1000,decimal through=2000)=>new(0,0,0,0,comparison,amount,through);
        foreach(var comparison in Enum.GetValues<BillBalanceComparison>())
        {
            Check(!Scope(comparison).Matches(0m)&&!Scope(comparison).Matches(-1m),"Paid and credit balances never become bills");
        }
        Check(Scope(BillBalanceComparison.AtLeast).Matches(1000)&&!Scope(BillBalanceComparison.AtLeast).Matches(999),"At least includes the boundary");
        Check(!Scope(BillBalanceComparison.MoreThan).Matches(1000)&&Scope(BillBalanceComparison.MoreThan).Matches(1001),"More than excludes the boundary");
        Check(Scope(BillBalanceComparison.AtMost).Matches(1000)&&!Scope(BillBalanceComparison.AtMost).Matches(1001),"At most includes the boundary");
        Check(!Scope(BillBalanceComparison.LessThan).Matches(1000)&&Scope(BillBalanceComparison.LessThan).Matches(999),"Less than excludes the boundary");
        Check(Scope(BillBalanceComparison.Exactly).Matches(1000)&&!Scope(BillBalanceComparison.Exactly).Matches(1001),"Exactly matches only the chosen amount");
        Check(Scope(BillBalanceComparison.Between).Matches(1000)&&Scope(BillBalanceComparison.Between).Matches(2000)&&!Scope(BillBalanceComparison.Between).Matches(2001),"Between includes both endpoints");
        using var balances=new DataTable();balances.Columns.Add("AdmissionId",typeof(int));
        foreach(string field in new[]{"AdmissionNumber","StudentName","FatherName","CampusName","ClassSection","AcademicYear"})balances.Columns.Add(field);
        balances.Columns.Add("Outstanding",typeof(decimal));
        balances.Rows.Add(1,"101","Fictional Ali","Example Parent","Campus A","Class 1 – A","2026-2027",800m);
        balances.Rows.Add(2,"QR-102","Fictional Bilal","Example Parent","Campus A","Class 1 – A","2026-2027",6900m);
        balances.Rows.Add(3,"103","Fictional Sara","Example Parent","Campus B","Class 1 – A","2026-2027",6900m);
        using(var filtered=OutstandingBillSelection.Select(balances,Scope(BillBalanceComparison.AtLeast,6900)))
        {
            Check(filtered.Rows.Count==2,"Balance comparisons select matching students, without changing their balance");
        }
        var previous=(CurrentUser.UserId,CurrentUser.Username,CurrentUser.RoleName,CurrentUser.MustChangePassword,CurrentUser.GetPermissions().ToArray());
        try
        {
            CurrentUser.SignIn(990,"Bill fixture","Administrator",false,Array.Empty<string>());
            using(var shell=new FMSC_SMS.MainDashboardForm((_,_)=>{}))
            {
                var commands=(List<NavigationCommand>)typeof(FMSC_SMS.MainDashboardForm).GetField("navigationCommands",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(shell)!;
                Check(commands.Any(command=>command.Caption==PageTitles.OutstandingBills),"Bill printing is a standalone navigation destination");
            }
            using var page=new OutstandingBillsPage(false,async _=>{await Task.Delay(20);return balances.Copy();});
            using var owner=new Form{ClientSize=new Size(1500,900),AutoScaleMode=AutoScaleMode.None};owner.Controls.Add(page);owner.Show();Application.DoEvents();
            var flags=BindingFlags.Instance|BindingFlags.NonPublic;
            Check(((ModernComboBox)typeof(OutstandingBillsPage).GetField("billGrouping",flags)!.GetValue(page)!).SelectedIndex==0,"Bill grouping defaults to Individual on the existing page");
            T Field<T>(string name)=>(T)typeof(OutstandingBillsPage).GetField(name,flags)!.GetValue(page)!;
            IEnumerable<Control> Walk(Control root)=>root.Controls.Cast<Control>().SelectMany(c=>new[]{c}.Concat(Walk(c)));
            ModernButton Button(string caption)=>Walk(page).OfType<ModernButton>().Single(button=>button.Text==caption);
            void ClickButton(ModernButton control)
            {
                control.Focus();control.PerformClick();
                Application.DoEvents();
            }
            ClickButton(Button("Load Students"));
            PumpUntil(()=>!page.IsBusy&&Button("Preview Selected Bills").Enabled,"Load Students click completes its asynchronous read");
            Check(Button("Preview Selected Bills").Enabled,"Loaded owing students can be previewed");
            var grid=(DataGridView)typeof(V2Page).GetField("Grid",flags)!.GetValue(page)!;
            Check(grid.Rows.Count==3&&grid.Columns["CampusName"].Visible&&grid.Columns["ClassSection"].Visible,"Grid distinguishes campus and class/section");
            Check(Field<ModernComboBox>("orderBy").SelectedIndex==0,"Bill ordering defaults to the grouping's standard order");
            grid.Rows[0].Cells["Include"].Value=false;
            Field<ModernComboBox>("orderBy").SelectedIndex=(int)BillSortOrder.HighestBalance;
            Check(((DataRowView)grid.Rows[0].DataBoundItem).Row.Field<int>("AdmissionId")==3,"Order selector immediately sorts the loaded balances");
            var selectedIds=(List<int>)typeof(OutstandingBillsPage).GetMethod("Selected",flags)!.Invoke(page,null)!;
            Check(selectedIds.SequenceEqual(new[]{3,2})&&Button("Preview Selected Bills").Enabled,"Sorting preserves checks and supplies preview IDs in displayed order without reloading");
            Field<ModernComboBox>("orderBy").SelectedIndex=0;
            ClickButton(Button("Clear Selection"));
            Check(grid.Rows.Cast<DataGridViewRow>().All(row=>Equals(row.Cells["Include"].Value,false)),"Clear Selection unchecks every displayed row after an asynchronous load");
            ClickButton(Button("Select All"));
            Check(grid.Rows.Cast<DataGridViewRow>().All(row=>Equals(row.Cells["Include"].Value,true)),"Select All checks every displayed row after an asynchronous load");
            grid.CurrentCell=grid.Rows[0].Cells["Include"];grid.Focus();grid.BeginEdit(false);
            grid.CurrentCell.Value=false;grid.NotifyCurrentCellDirty(true);
            ClickButton(Button("Select All"));
            Check(grid.Rows.Cast<DataGridViewRow>().All(row=>Equals(row.Cells["Include"].Value,true)),"Select All replaces a current checkbox edit");
            grid.CurrentCell=grid.Rows[1].Cells["Include"];grid.Focus();grid.BeginEdit(false);
            grid.CurrentCell.Value=true;grid.NotifyCurrentCellDirty(true);
            ClickButton(Button("Clear Selection"));
            Check(grid.Rows.Cast<DataGridViewRow>().All(row=>Equals(row.Cells["Include"].Value,false))&&!Button("Preview Selected Bills").Enabled,
                "Clear Selection finishes an active checkbox edit and clears the actual preview selection");
            Button("Select All").PerformClick();
            Check(Field<DataTable>("roster").AsEnumerable().All(row=>row.Field<bool>("Include")),"Select All checks the complete loaded bulk list");
            Check(!Walk(page).Any(c=>c.GetType()==typeof(ModernTextBox)),"Bulk bills have no individual admission/name search input");
            Check(!Walk(page).OfType<ModernButton>().Any(b=>b.Text=="Select Shown"),"No duplicate search-only selection action remains");
            Control Card(Control control)
            {
                while(control.Parent is not null&&control.GetType().Name!="HumanCard")control=control.Parent;
                return control;
            }
            var filterCard=Card(Field<ModernComboBox>("billGrouping"));
            Check(new Control[]{Field<LookupBox>("campus"),Field<LookupBox>("year"),Field<LookupBox>("cls"),Field<LookupBox>("section"),
                Field<ModernComboBox>("comparison"),Field<ModernComboBox>("paper"),Field<ModernNumericTextBox>("amount"),Field<ModernNumericTextBox>("through")}
                .All(c=>ReferenceEquals(Card(c),filterCard)),"Placement, balance, paper and grouping share one filter panel");
            Check(ReferenceEquals(Card(grid),filterCard)&&new[]{"Load Students","Select All","Clear Selection","Preview Selected Bills"}
                .All(text=>ReferenceEquals(Card(Button(text)),filterCard)),"The grid, filters and every action share one merged section");
            Check(Walk(page).OfType<HumanCard>().Count()==1,"Bulk billing has one heading and one panel instead of duplicate sections");
            Field<ModernComboBox>("comparison").SelectedIndex=(int)BillBalanceComparison.AtLeast;
            Field<ModernNumericTextBox>("amount").Value=6900;
            Check(!Button("Preview Selected Bills").Enabled,"Changing amount filters invalidates preview until reload");
            var load=(Task)typeof(OutstandingBillsPage).GetMethod("LoadStudentsAsync",flags)!.Invoke(page,null)!;PumpUntil(()=>load.IsCompleted,"Changed balance filter reloads");
            Check(Field<DataTable>("roster").Rows.Count==2,"Reload applies the requested threshold to the complete balances");
            foreach(var size in new[]{new Size(1500,900),new Size(1000,720)})
            {
                owner.ClientSize=size;owner.PerformLayout();Application.DoEvents();
                var filterBar=Field<LookupBox>("campus").Parent!.Parent!;
                var filterBounds=filterBar.RectangleToScreen(filterBar.ClientRectangle);
                foreach(var control in new Control[]{Field<LookupBox>("campus"),Field<LookupBox>("year"),Field<LookupBox>("cls"),Field<LookupBox>("section"),
                    Field<ModernComboBox>("comparison"),Field<ModernComboBox>("paper"),Field<ModernComboBox>("billGrouping"),Field<ModernComboBox>("orderBy"),Field<ModernNumericTextBox>("amount"),Field<ModernNumericTextBox>("through")})
                {
                    var bounds=control.RectangleToScreen(control.ClientRectangle);
                    Check(control.Visible&&control.Height>=AppMetrics.InputControlHeight&&bounds.Top>=filterBounds.Top&&bounds.Bottom<=filterBounds.Bottom,
                        "Every filter remains visible inside the compact panel");
                }
                var sequence=new[]{"Load Students","Select All","Clear Selection","Preview Selected Bills"};
                var selectionButtons=Button("Select All").Parent!;
                Check(selectionButtons.Controls.Cast<Control>().Select(control=>control.Text).SequenceEqual(sequence),
                    "Selection tools appear before bill preview in natural order");
                Check(sequence.Select(Button).Select(button=>button.TabIndex).SequenceEqual(Enumerable.Range(0,4)),
                    "Keyboard navigation follows the selection then preview order");
                foreach(string caption in new[]{"Load Students","Preview Selected Bills","Select All","Clear Selection"})
                {
                    var button=Button(caption);Check(button.Visible,"All bill actions remain visible");
                    Check(button.Right<=button.Parent!.ClientSize.Width && button.Bottom<=button.Parent.ClientSize.Height,"Bill actions fit their container");
                }
                Check(grid.Height>=100,"Bill selection keeps usable grid height");
                Check(grid.PointToScreen(Point.Empty).Y-page.PointToScreen(Point.Empty).Y<AppScale.Scale(400),"Compact filters leave the grid near the top of the page");
                var folder=Path.GetFullPath("artifacts/outstanding-bills");Directory.CreateDirectory(folder);
                using var bitmap=new Bitmap(owner.Width,owner.Height);owner.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine(folder,$"selection-{size.Width}.png"));
            }
            Check(!File.Exists(Path.Combine(AppContext.BaseDirectory,"Reports","FeeReminder.rdlc")),"The retired reminder report is not shipped");
        }
        finally{CurrentUser.SignIn(previous.UserId,previous.Username,previous.RoleName,previous.MustChangePassword,previous.Item5);}
    }

    private static void CheckOutstandingBillOrder()
    {
        using var rows=new DataTable();
        foreach(string column in new[]{"AdmissionId","FamilyId","ClassOrder","SectionOrder","CampusOrder"})rows.Columns.Add(column,typeof(int));
        foreach(string column in new[]{"AdmissionNumber","StudentName","ClassSection"})rows.Columns.Add(column);
        rows.Columns.Add("Outstanding",typeof(decimal));
        rows.Rows.Add(1,10,10,1,1,"10","Zara","Ten - A",100m);
        rows.Rows.Add(2,10,2,1,1,"2a","Sara","Two - A",400m);
        rows.Rows.Add(3,20,2,2,1,"3","Ali","Two - B",200m);
        rows.Rows.Add(4,DBNull.Value,2,1,1,"2","Bilal","Two - A",300m);
        int[] Order(BillSortOrder order,bool family=false)=>OutstandingBillSelection.Order(rows,order,family).Select(row=>row.Field<int>("AdmissionId")).ToArray();
        Check(Order(BillSortOrder.Default).SequenceEqual(new[]{4,2,3,1}),"Individual default respects numeric class display order, section and natural admission numbers");
        Check(Order(BillSortOrder.Default,true).SequenceEqual(new[]{4,2,1,3}),"Family default keeps siblings together by the family's lowest admission number");
        Check(Order(BillSortOrder.AdmissionNumber).SequenceEqual(new[]{4,2,3,1}),"Admission sorting places 2 before 2a and 10");
        Check(Order(BillSortOrder.StudentName).SequenceEqual(new[]{3,4,2,1}),"Student name ordering is alphabetical");
        Check(Order(BillSortOrder.HighestBalance).SequenceEqual(new[]{2,4,3,1})&&Order(BillSortOrder.LowestBalance).SequenceEqual(new[]{1,3,4,2}),"Balance ordering supports both directions");
        Check(Order(BillSortOrder.HighestBalance,true).SequenceEqual(new[]{2,1,4,3}),"Custom family sorting ranks by the first matching child while keeping siblings adjacent");
        Check(Order(BillSortOrder.ClassSection).SequenceEqual(Order(BillSortOrder.Default)),"Explicit class order matches the individual default");
    }
}
