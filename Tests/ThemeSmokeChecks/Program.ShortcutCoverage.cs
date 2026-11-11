using System.Reflection;
using FMSC_SMS.Core;
using FMSC_SMS.Pages.Finance.Collection;
using FMSC_SMS.Pages.Reports.Students;
using FMSC_SMS.Pages.Security;
using FMSC_SMS.Pages.Setup;

internal static partial class Program
{
    private static void CheckNewShortcutCoverage()
    {
        using var tip=new ToolTip();
        void DistinctCommands(params (string Caption,Keys Key)[] commands)
        {
            using var host=new Form {ClientSize=new Size(500,500),ShowInTaskbar=false};
            var controller=new PageActionShortcutController(host,tip);
            int[] clicks=new int[commands.Length];
            var buttons=new ModernButton[commands.Length];
            for(int i=0;i<commands.Length;i++)
            {
                int index=i;
                var button=new ModernButton {Text=commands[i].Caption,Top=i*45,Width=400};
                button.Click+=(_,_)=>clicks[index]++;
                host.Controls.Add(button);buttons[i]=button;
            }
            host.Show();
            for(int i=0;i<commands.Length;i++)
            {
                int before=clicks.Sum(),previous=clicks[i];
                Check(controller.TryHandle(commands[i].Key) && clicks[i]==previous+1 && clicks.Sum()==before+1,
                    commands[i].Caption+": the key invokes only its intended action among competing buttons");
                buttons[i].Enabled=false;
                before=clicks.Sum();
                Check(!controller.TryHandle(commands[i].Key) && clicks.Sum()==before,
                    commands[i].Caption+": a disabled action cannot fall through to a competing button");
                buttons[i].Enabled=true;
            }
            host.Close();
        }
        DistinctCommands(("Print Preview",Keys.Control|Keys.P),("Class Summary A4",Keys.Alt|Keys.Y),
            ("Columns",Keys.Alt|Keys.O),("Rename View",Keys.Control|Keys.M),
            ("Clear Filters",Keys.Alt|Keys.C),("Reset Filters",Keys.Control|Keys.T));
        DistinctCommands(("+ Add Fee",Keys.Alt|Keys.A),("Admit Sibling",Keys.Alt|Keys.L));
        DistinctCommands(("Save",Keys.Control|Keys.S),("Check Saved Admission",Keys.Alt|Keys.K),
            ("Check Registration Save",Keys.Control|Keys.J));
        DistinctCommands(("Preview Selected Bills",Keys.Control|Keys.P),("Export",Keys.Alt|Keys.E));

        var previousUser=(CurrentUser.UserId,CurrentUser.Username,CurrentUser.RoleName,CurrentUser.MustChangePassword,Grants:CurrentUser.GetPermissions().ToArray());
        try
        {
            CurrentUser.SignIn(7779,"Shortcut coverage","Accountant",false,new[]{"Reports.View","Students.View","Fees.View"});
            const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
            var find=typeof(PageActionShortcutController).GetMethod("Find",flags)!;
            void Target(V2Page page,Keys key,string caption)
            {
                var shortcut=AppHotKeys.PageActions.Single(s=>s.KeyData==key);
                var matches=(List<ModernButton>)find.Invoke(page.ShortcutController,new object[]{shortcut.ButtonPrefixes})!;
                Check(matches.Count==1 && matches[0].Text==caption,
                    page.GetType().Name+": real page resolves "+shortcut.DisplayKey+" to "+caption+" without ambiguity");
            }
            using var bills=new OutstandingBillsPage(false,null);
            Target(bills,Keys.Control|Keys.P,"Preview Selected Bills");
            Check(!bills.ShortcutController.TryHandle(Keys.Control|Keys.P),"A bill preview shortcut respects the page's disabled readiness state");
            using var students=new StudentReportsPage();
            Target(students,Keys.Control|Keys.P,"Print Preview");
            Target(students,Keys.Alt|Keys.Y,"Class Summary A4");
            Target(students,Keys.Alt|Keys.O,"Columns");
            Target(students,Keys.Control|Keys.M,"Rename View");
            Target(students,Keys.Control|Keys.T,"Reset Filters");
            Target(students,Keys.Alt|Keys.C,"Clear Filters");
            using var profile=new SchoolProfilePage(false);
            Target(profile,Keys.Control|Keys.S,"Save");
            var explicitSchoolSave=typeof(PageActionShortcutController)
                .GetField("explicitSaveButton",flags)!.GetValue(profile.ShortcutController) as ModernButton;
            Check(explicitSchoolSave is {Text:"Save"},
                "School Profile Ctrl+S is explicitly bound to school information, not independent image saves");
            var profileSurface=(Panel)typeof(V2Page).GetField("_content",flags)!.GetValue(profile)!;
            var profileLayout=profileSurface.Controls.OfType<TableLayoutPanel>().Single();
            var logoCard=profileLayout.GetControlFromPosition(1,0) as HumanCard;
            var signatureCard=profileLayout.GetControlFromPosition(0,2) as HumanCard;
            Check(logoCard is not null && signatureCard is not null &&
                profileLayout.GetRowSpan(logoCard)==profileLayout.RowCount &&
                profileLayout.RowStyles[^1].SizeType==SizeType.Percent,
                "School Profile logo fills the entire right column while excess height remains available");
            var signatureFields=signatureCard?.Content.Controls.OfType<TableLayoutPanel>().SingleOrDefault();
            var signatureEditor=signatureFields?.Controls.OfType<TableLayoutPanel>().SingleOrDefault();
            var signatureButtons=signatureEditor?.Controls.OfType<TableLayoutPanel>().SingleOrDefault();
            Check(signatureButtons is {ColumnCount:3,RowCount:1} &&
                signatureButtons.Controls.OfType<ModernButton>().Count()==3 &&
                profileLayout.RowStyles[2].SizeType==SizeType.Absolute,
                "Principal signature stays compact and exposes all three actions in one horizontal row");
            Check(!students.IsHandleCreated && !bills.IsHandleCreated,"Real-page shortcut coverage does not start database initialization");
        }
        finally {CurrentUser.SignIn(previousUser.UserId,previousUser.Username,previousUser.RoleName,previousUser.MustChangePassword,previousUser.Grants);}
    }
}
