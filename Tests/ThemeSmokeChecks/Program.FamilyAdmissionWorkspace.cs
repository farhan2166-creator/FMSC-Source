using System.Data;
using System.Reflection;
using FMSC_SMS.Core;
using FMSC_SMS;
using FMSC_SMS.Pages.Security;
using FMSC_SMS.Pages.Students.Admissions;
using FMSC_SMS.Pages.Finance.StudentAccounts;

internal static partial class Program
{
    private static void CheckFamilyAdmissionWorkspace()
    {
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var previous=(CurrentUser.UserId,CurrentUser.Username,CurrentUser.RoleName,CurrentUser.MustChangePassword,Permissions:CurrentUser.GetPermissions().ToArray());
        CurrentUser.SignIn(988,"Workspace fixture","Administrator",false,Array.Empty<string>());
        typeof(AdmissionNumbers).GetField("_numbers",BindingFlags.Static|BindingFlags.NonPublic)!.SetValue(null,new Dictionary<int,string>{{41,"41"},{42,"42"}});
        typeof(AdmissionNumbers).GetField("_loadedAt",BindingFlags.Static|BindingFlags.NonPublic)!.SetValue(null,DateTime.UtcNow);
        T Field<T>(object owner,string name)=>(T)owner.GetType().GetField(name,flags)!.GetValue(owner)!;
        async Task Call(object owner,string name,params object[] args)=>await (Task)owner.GetType().GetMethod(name,flags)!.Invoke(owner,args)!;
        void Run(Task task){PumpUntil(()=>task.IsCompleted,"Family workspace design action completes");task.GetAwaiter().GetResult();}
        try
        {
            using var rows=new DataTable();
            foreach(string name in new[]{"StudentName","FatherName","ContactNumber","Address","Status","Reference","EnrollmentStatus","AcademicYear","CampusName","ClassName","SectionName"})rows.Columns.Add(name);
            rows.Columns.Add("AdmissionId",typeof(int));rows.Columns.Add("EnrollmentId",typeof(int));
            rows.Columns.Add("BillingReady",typeof(bool));rows.Columns.Add("PlacementConfirmedAt",typeof(DateTime));rows.Columns.Add("Outstanding",typeof(decimal));
            void Member(int id,string name,bool ready)
            {
                var row=rows.NewRow();row["AdmissionId"]=id;row["EnrollmentId"]=id+100;row["StudentName"]=name;
                row["FatherName"]="Example Parent";row["ContactNumber"]="03001234567";row["Address"]="Example address";
                row["Status"]="Active";row["EnrollmentStatus"]="Active";row["Reference"]=id.ToString();row["BillingReady"]=ready;
                row["PlacementConfirmedAt"]=DateTime.Today;row["Outstanding"]=ready?2400m:0m;row["AcademicYear"]="2026-2027";
                row["CampusName"]="Campus Boys";row["ClassName"]=ready?"Class 4":"Class 2";row["SectionName"]="A";rows.Rows.Add(row);
            }
            Member(41,"Example Older Child",true);Member(42,"Example Returning Child",false);
            using var workspace=new FamilyAdmissionWorkspace((_,_,_)=>{},_=>Task.FromResult(rows.Copy()),false);
            using var owner=new Form{ClientSize=new Size(1450,980),StartPosition=FormStartPosition.Manual,Location=new Point(-30000,-30000),ShowInTaskbar=false};
            owner.Controls.Add(workspace);owner.Show();Application.DoEvents();
            Run(Call(workspace,"LoadFamilyAsync",41));
            var list=Field<FamilyStudentList>(workspace,"children");
            Check(list.Items.Count==2 && list.CheckedItems.Count==2,"Loading one student brings their established siblings into the workspace");
            Check(!Field<ModernButton>(workspace,"print").Enabled,"An included unconfirmed return blocks premature family billing");
            Check(!Field<ModernButton>(workspace,"collect").Enabled,"Unconfirmed returns also block premature family collection");
            var feePanel=Field<StudentFeeAssignmentsPanel>(list.Items[0]!,"Assignments");
            var feeGrid=Field<DataGridView>(feePanel,"grid");
            int feeRow=feeGrid.Rows.Add("Tuition Fee",400m,100m,300m,"Monthly","Oct-2026","Ongoing","Active","Oct-2026",71,1,true,DateTime.Today,false,0);
            feeGrid.Rows[feeRow].Cells["ConcessionLabel"].Value="Sibling";
            Check(feeGrid.Columns.Cast<DataGridViewColumn>().Count(column=>column.Visible)==5,
                "The family fee overview shows five combined columns");
            Check(Convert.ToString(feeGrid.Rows[0].Cells["FeeTypeName"].FormattedValue)=="Tuition Fee (Monthly)" &&
                Convert.ToString(feeGrid.Rows[0].Cells["DiscountAmount"].FormattedValue)!.Contains("Sibling") &&
                Convert.ToString(feeGrid.Rows[0].Cells["FamilyPeriod"].FormattedValue)!.Contains("Active") &&
                Convert.ToString(feeGrid.Rows[0].Cells["FamilyPeriod"].FormattedValue)!.Contains("Last posted: Oct-2026"),
                "Merged cells retain frequency, concession, rate status and posting information");
            Check(Equals(feeGrid.Rows[0].Cells["DiscountAmount"].Value,100m) && Equals(feeGrid.Rows[0].Cells["NetAmount"].Value,300m) &&
                Equals(feeGrid.Rows[0].Cells["StudentFeeAssignmentId"].Value,71),
                "Compact presentation preserves the monetary values and assignment identity used by fee actions");
            using(var fullPanel=new StudentFeeAssignmentsPanel())
                Check(fullPanel.Controls.Find("FamilyFeeGrid",true).Length==0,
                    "Other student fee screens keep their established detailed presentation");
            Directory.CreateDirectory("artifacts/family-admission");
            foreach(var size in new[]{new Size(1600,900),new Size(1450,980),new Size(1100,800)})
            {
                owner.ClientSize=size;Application.DoEvents();
                var family=workspace.Controls.Find("FamilyDetailsCard",true).Single();
                var children=workspace.Controls.Find("FamilyChildrenCard",true).Single();
                var editorSurface=workspace.Controls.Find("FamilyEditorCard",true).Single();
                Rectangle Bounds(Control control)=>owner.RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
                Rectangle a=Bounds(family),b=Bounds(children),c=Bounds(editorSurface);
                // Adjacent cards can round to different physical pixels at other
                // DPI scales. Preserve the stack, full-height fees and near-aligned
                // edges without treating one-pixel rounding as a functional defect.
                double edgeTolerance = Math.Max(AppScale.Scale(8), a.Width * 0.04d);
                Check(Math.Abs(a.Left-b.Left)<=edgeTolerance &&
                    Math.Abs(a.Right-b.Right)<=edgeTolerance &&
                    b.Top>=a.Bottom &&
                    Math.Abs(c.Top-a.Top)<=Math.Max(AppScale.Scale(8), owner.ClientSize.Height * 0.03d) &&
                    Math.Abs(c.Bottom-b.Bottom)<=Math.Max(AppScale.Scale(8), owner.ClientSize.Height * 0.03d),
                    "Family details and children stack on the left while fees span their full combined height");
                Check((double)c.Width/(c.Width+a.Width) is >0.58 and <0.62,
                    "The family workspace uses the requested forty/sixty width split");
                Check(feeGrid.DisplayedColumnCount(false)==5 && !feeGrid.Controls.OfType<HScrollBar>().Any(bar=>bar.Visible),
                    "All five fee columns fit without sideways scrolling at supported desktop widths");
                var home=Field<ModernTextBox>(workspace,"familyAddress");
                Check(home.Height>=AppMetrics.InputControlHeight && home.Bottom<=home.Parent!.ClientSize.Height,
                    "The stacked family details retain a full-height address input");
                Check(feeGrid.Right<=feePanel.ClientSize.Width,
                    "The fee grid shrinks with its panel instead of extending beyond the visible area");
                Check(Field<TableLayoutPanel>(feePanel,"actionLayout").Controls.OfType<FlowLayoutPanel>().Single().Controls.Cast<Control>().All(button=>button.Height>=AppMetrics.ButtonHeight),
                    "Fee actions keep their full button height when wrapping into additional rows");
                Check(Field<TableLayoutPanel>(feePanel,"actionLayout").Controls.OfType<FlowLayoutPanel>().Single().Controls.Cast<Control>()
                    .All(button=>feePanel.RectangleToClient(button.RectangleToScreen(button.ClientRectangle)).Bottom<=feePanel.ClientSize.Height),
                    "Every wrapped fee action remains visible inside the fee panel");
                var phone=Field<ModernTextBox>(workspace,"familyPhone");
                Check(phone.Right<=phone.Parent!.ClientSize.Width && phone.Height>=AppMetrics.InputControlHeight,
                    "The optional phone input fits fully within the narrower family card");
                using var image=new Bitmap(owner.Width,owner.Height);owner.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));
                image.Save($"artifacts/family-admission/fees-{size.Width}.png");
            }
            void ClickChild(int index, bool checkbox)
            {
                list.TopIndex=index;
                Rectangle bounds = checkbox ? list.CheckBoxBounds(index) : list.GetItemRectangle(index);
                var point = checkbox ? new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2)
                    : new Point(100, bounds.Top + 15);
                var position = new IntPtr((point.Y << 16) | point.X);
                SearchInputMessage(list.Handle, 0x0201, new IntPtr(1), position);
                SearchInputMessage(list.Handle, 0x0202, IntPtr.Zero, position);
                Application.DoEvents();
            }
            ClickChild(1, false);
            Check(ReferenceEquals(Field<object>(workspace,"current"), list.Items[1]) && list.CheckedItems.Count == 2 &&
                Field<bool>(list.Items[1]!,"Included"),
                "Clicking a child's row loads their editor without changing inclusion in the family workspace");
            ClickChild(0, false);
            Check(ReferenceEquals(Field<object>(workspace,"current"), list.Items[0]) && list.CheckedItems.Count == 2,
                "Switching back to another child also preserves both checked children");
            ClickChild(1, true);
            Check(!Field<bool>(list.Items[1]!,"Included") && list.CheckedItems.Count == 1 &&
                Field<ModernButton>(workspace,"print").Enabled && Field<ModernButton>(workspace,"collect").Enabled,
                "Only an explicit checkbox click excludes a child and updates bill and payment availability");
            list.SetItemChecked(1, true);
            Run(Call(workspace,"AddNewChildAsync"));
            var current=Field<object>(workspace,"current");
            var draft=Field<Control>(current,"Editor");
            Field<ModernTextBox>(draft,"name").Text="Example New Child";
            Check(Field<ModernTextBox>(draft,"parent").Text=="Example Parent","A new child inherits the established family's parent details");
            using var fees=new DataTable();
            fees.Columns.Add("FeeTypeId",typeof(int));fees.Columns.Add("FeeTypeName");fees.Columns.Add("IsMandatory",typeof(bool));
            fees.Columns.Add("IsTransportLinked",typeof(bool));fees.Columns.Add("Amount",typeof(decimal));fees.Columns.Add("Frequency");
            fees.Columns.Add("Include",typeof(bool));fees.Columns.Add("Discount",typeof(decimal));
            fees.Rows.Add(1,"Tuition",true,false,1200m,"Monthly",true,200m);fees.Rows.Add(2,"Admission",true,false,4000m,"OneTime",true,0m);
            draft.GetType().GetMethod("SetRates",flags)!.Invoke(draft,new object[]{fees});
            draft.GetType().GetMethod("UpdateTotal",flags)!.Invoke(draft,null);
            var first=list.Items[0]!;
            Run(Call(workspace,"ShowChildAsync",first));Run(Call(workspace,"ShowChildAsync",current));
            Check(ReferenceEquals(Field<Control>(current,"Editor"),draft) && Field<ModernTextBox>(draft,"name").Text=="Example New Child",
                "Switching siblings retains the original unsaved child editor and its inputs");
            Check(list.Items.Count==3 && workspace.HasUnsavedChanges,"Existing and new children share one workspace with unsaved-change protection");
            Check(!Field<ModernButton>(workspace,"print").Enabled,"Unsaved new children cannot be omitted silently from a selected bill");
            list.SetItemChecked(1,false);list.SetItemChecked(2,false);Application.DoEvents();
            Check(Field<ModernButton>(workspace,"print").Enabled,"Excluding unfinished children permits a bill for a ready selected child");
            Check(Field<ModernButton>(workspace,"collect").Enabled,"The same saved selection can proceed directly to existing collection");
            Check(Field<ModernButton>(workspace,"removeDraft").Enabled,"Only the current unsaved child can be removed as a draft");
            list.SetItemChecked(1,true);list.SetItemChecked(2,true);Application.DoEvents();
            Directory.CreateDirectory("artifacts/family-admission");
            foreach(var size in new[]{new Size(1450,980),new Size(1100,800)})
            {
                owner.ClientSize=size;Application.DoEvents();
                using var image=new Bitmap(owner.Width,owner.Height);owner.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));
                image.Save($"artifacts/family-admission/workspace-{size.Width}.png");
            }
            Check(FamilyAdmissionData.CanUse(p=>p is "Students.View" or "Students.Administer") &&
                !FamilyAdmissionData.CanUse(p=>p=="Students.Admit"),"Workspace access retains separate viewing and admission/administration permissions");
            Run(Call(workspace,"ShowChildAsync",list.Items[0]!));
            var retained=Field<object>(workspace,"current");
            var editor=new QuickRegistrationFormPage((_,_,_)=>{},0,false);
            retained.GetType().GetField("Editor",flags|BindingFlags.Public)!.SetValue(retained,editor);
            typeof(QuickRegistrationFormPage).GetField("savedStudent",flags)!.SetValue(editor,9876);
            workspace.GetType().GetMethod("UpdateWorkspace",flags)!.Invoke(workspace,null);
            Check(Field<int>(retained,"Id")==41 && !Field<bool>(retained,"MembershipVerified"),
                "A different recovered student cannot replace an already loaded family child");
        }
        finally{CurrentUser.SignIn(previous.UserId,previous.Username,previous.RoleName,previous.MustChangePassword,previous.Permissions);}
    }
}
