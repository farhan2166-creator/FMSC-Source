using System.ComponentModel;
using System.Reflection;
using System.Data;
using FMSC_SMS.Core;
using FMSC_SMS.Pages.Security;
using FMSC_SMS.Pages.Students.Admissions;

internal static partial class Program
{
    private static void CheckAdmissionActions()
    {
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        int id=CurrentUser.UserId;string user=CurrentUser.Username,role=CurrentUser.RoleName;bool password=CurrentUser.MustChangePassword;
        var grants=CurrentUser.GetPermissions().ToArray();
        try
        {
            CurrentUser.SignIn(1,"Synthetic menu fixture","Administrator",false,Array.Empty<string>());
            using var page=new StudentAdmissionPage();var type=typeof(StudentAdmissionPage);
            void Set(string field,object value)=>type.GetField(field,flags)!.SetValue(page,value);
            var menu=(ContextMenuStrip)type.GetField("enrollmentActionsMenu",flags)!.GetValue(page)!;
            void Refresh()=>typeof(ToolStripDropDown).GetMethod("OnOpening",flags)!.Invoke(menu,new object[]{new CancelEventArgs()});
            ToolStripItem Item(string caption)=>menu.Items.Cast<ToolStripItem>().Single(i=>i.Text==caption);
            Set("currentAdmissionId",41);Set("pendingPlacementId",0);Set("_workspaceStudentStatus","Active");Set("_hasOfficialAdmissionNumber",true);
            var leavingIndicator=(Label)type.GetField("withdrawalDateIndicator",flags)!.GetValue(page)!;
            var identityRow=(TableLayoutPanel)leavingIndicator.Parent!;
            Check(identityRow.GetColumn(leavingIndicator)==2 && identityRow.ColumnStyles[2].SizeType==SizeType.Percent,
                "Leaving date occupies the flexible center, not the status or identity columns");
            Set("_workspaceWithdrawalDate",new DateTime(2024,11,21));
            var refreshWorkspace=type.GetMethod("UpdateStudentWorkspace",flags)!;
            refreshWorkspace.Invoke(page,null);
            Check(string.IsNullOrEmpty(leavingIndicator.Text),"Active students do not show a stale leaving date");
            Set("_workspaceStudentStatus","Left");refreshWorkspace.Invoke(page,null);
            Check(leavingIndicator.Text=="Date Left: 21 Nov 2024","Left students show the recorded withdrawal date");
            Set("studentPostingHeld",true);refreshWorkspace.Invoke(page,null);
            Check(string.IsNullOrEmpty(leavingIndicator.Text),"An on-hold student is not shown as departed");
            Set("studentPostingHeld",false);Set("_workspaceStudentStatus","Active");refreshWorkspace.Invoke(page,null);
            Set("placementYearStart",DateTime.Today.AddMonths(-1));Set("placementYearEnd",DateTime.Today.AddMonths(10));Refresh();
            Check(Item("Put on Hold…").Available&&Item("Record Departure…").Available&&!Item("Re-Admit…").Available,"Active menu presents current-student actions");
            var transfer=(ToolStripMenuItem)Item("Transfer");
            Check(transfer.Available&&transfer.DropDownItems.Cast<ToolStripItem>().Select(i=>i.Text).SequenceEqual(new[]{"Transfer Section","Transfer Class","Transfer Campus"}),"One Transfer submenu retains original operation codes");
            Set("_workspaceStudentStatus","Left");Refresh();
            Check(Item("Placement History…").Available&&Item("Re-Admit…").Available&&!Item("Record Departure…").Available,"Former students retain history and appropriate readmission");
            Set("_hasOfficialAdmissionNumber",false);Refresh();
            Check(!Item("Re-Admit…").Available,"Unnumbered registrations cannot enter readmission");
            Set("_hasOfficialAdmissionNumber",true);Refresh();
            var advanced=(ToolStripMenuItem)Item("Corrections / Advanced");
            Check(advanced.Available&&advanced.DropDownItems.Count==1&&!menu.Items.Cast<ToolStripItem>().Any(i=>(i.Text ?? "").StartsWith("Review Left")),"Exceptional adoption is under Advanced");
            Set("studentPostingHeld",true);Refresh();
            Check(Item("Lift Hold / Resume…").Available&&!Item("Re-Admit…").Available&&!advanced.Available,"A held former student has one return path");
            Set("studentPostingHeld",false);Set("_workspaceStudentStatus","Active");Set("legacySetupPending",true);Refresh();
            Check(Item("Activate Current Student…").Available&&Item("Placement History…").Available&&!Item("Complete Placement…").Available,"Legacy activation is separate from history and ordinary drafts");
            Set("legacySetupPending",false);Set("pendingPlacementId",52);Refresh();
            Check(Item("Complete Placement…").Available&&!transfer.Available,"Drafts offer completion with movement restricted");
            Set("saveBusy",true);Refresh();Check(!Item("Complete Placement…").Enabled&&!Item("Placement History…").Enabled,"Saving disables lifecycle entry points");
            Set("saveBusy",false);Set("pendingPlacementId",-1);Refresh();Check(!Item("Placement History…").Enabled,"Unknown confirmation state fails closed");
            Check(!menu.Items.Cast<ToolStripItem>().Any(i=>i.Text=="Confirm Placement"||i.Text=="Placement Setup"),"No duplicate confirmation or ambiguous setup entry remains");
            Check(!page.IsHandleCreated,"Menu inspection starts no deferred SQL loaders");
            using var draft=StudentPlacementCorrectionDialog.ForDraft(41,"Synthetic student",52,false);
            Check(draft.Text=="Complete Placement","Draft completion opens the existing editor directly");
            Check(!(bool)typeof(StudentPlacementCorrectionDialog).GetProperty("CanConfirmPlacement",flags)!.GetValue(draft)!,"Confirmation requires a successfully loaded record");
            var draftType=typeof(StudentPlacementCorrectionDialog);
            var records=new DataTable();records.Columns.Add("EnrollmentId",typeof(int));records.Columns.Add("PlacementConfirmedAt",typeof(DateTime));records.Rows.Add(52,DBNull.Value);
            draftType.GetField("enrollments",flags)!.SetValue(draft,records);draftType.GetField("loadSucceeded",flags)!.SetValue(draft,true);
            bool CanConfirm()=>(bool)draftType.GetProperty("CanConfirmPlacement",flags)!.GetValue(draft)!;
            Check(CanConfirm(),"A successfully loaded ordinary draft can proceed to the existing confirmation review");
            draftType.GetField("legacySetupPending",flags)!.SetValue(draft,true);Check(!CanConfirm(),"Legacy activation cannot be bypassed through draft confirmation");
            draftType.GetField("legacySetupPending",flags)!.SetValue(draft,false);records.Rows[0]["PlacementConfirmedAt"]=DateTime.Today;
            Check(!CanConfirm(),"An already confirmed placement cannot be confirmed again");
            using var history=new StudentPlacementCorrectionDialog(41,"Synthetic student",false);
            Check(history.Text=="Placement History","History remains an independent browser");
            bool refused=false;try{StudentPlacementCorrectionDialog.ForDraft(41,"Synthetic student",0,false);}catch(ArgumentOutOfRangeException){refused=true;}
            Check(refused,"Draft route cannot accidentally create a new placement");
        }
        finally{CurrentUser.SignIn(id,user,role,password,grants);}
    }
}
