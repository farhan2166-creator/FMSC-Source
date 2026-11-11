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
            using var profileHost=new Form {
                ClientSize=new Size(1280,850),ShowInTaskbar=false,
                StartPosition=FormStartPosition.Manual,Location=new Point(-24000,-24000)
            };
            profile.Dock=DockStyle.Fill;
            profileHost.Controls.Add(profile);
            profileHost.Show();
            Application.DoEvents();

            // Test the user's shortcut hint, not the controller's private field.
            // No Ctrl+S click is performed: real school SQL must never be written
            // by this SQL-free fixture.
            var saveHints=profile.ShortcutController.AvailableHints()
                .Where(hint => hint.Key == "Ctrl+S").ToArray();
            Check(saveHints.Length==1 &&
                saveHints[0].Target is ModernButton {Text:"Save"} &&
                !saveHints[0].Target.IsDisposed,
                "School information exposes one Ctrl+S save target distinct from image saves");

            HumanCard CardWithTitle(string title)=>Descendants(profile).OfType<HumanCard>()
                .Single(card=>card.Header.Controls.OfType<Label>()
                    .Any(label=>label.Text==title));
            var detailsCard=CardWithTitle("School Identity");
            var logoCard=CardWithTitle("Logo");
            var signatureCard=CardWithTitle("Principal Signature");
            var logoPreview=Descendants(logoCard).OfType<PictureBox>().Single();
            var signaturePreview=Descendants(signatureCard).OfType<PictureBox>().Single();
            var logoButtons=Descendants(logoCard).OfType<ModernButton>().ToArray();
            var signatureButtons=Descendants(signatureCard).OfType<ModernButton>().ToArray();
            Check(new[]{"Choose Logo…","Save Logo","Remove Logo"}.All(caption=>
                logoButtons.Any(button=>button.Text==caption)) &&
                new[]{"Choose Signature…","Save Signature","Remove Signature"}.All(caption=>
                    signatureButtons.Any(button=>button.Text==caption)),
                "School Profile exposes independent logo and signature actions");

            foreach(int width in new[]{1280,1600})
            {
                profileHost.ClientSize=new Size(width,850);
                Application.DoEvents();
                Rectangle logoBounds=logoCard.RectangleToScreen(logoCard.ClientRectangle);
                Rectangle detailsBounds=detailsCard.RectangleToScreen(detailsCard.ClientRectangle);
                Rectangle signatureBounds=signatureCard.RectangleToScreen(signatureCard.ClientRectangle);
                Check(logoBounds.Top<=detailsBounds.Top+2 &&
                    logoBounds.Bottom>=signatureBounds.Bottom-2 &&
                    logoPreview.Height>signaturePreview.Height,
                    "School Profile logo preview spans the visible school-details and compact-signature area");

                foreach(var (card,buttons) in new[]{
                    (Card:logoCard,Buttons:logoButtons),
                    (Card:signatureCard,Buttons:signatureButtons)})
                {
                    Rectangle cardBounds=card.RectangleToScreen(card.ClientRectangle);
                    foreach(var button in buttons)
                    {
                        Rectangle buttonBounds=button.RectangleToScreen(button.ClientRectangle);
                        Check(button.Visible && button.Enabled &&
                            buttonBounds.Width>0 && buttonBounds.Height>0 &&
                            cardBounds.Contains(buttonBounds),
                            "School Profile image action remains visible and unclipped: "+button.Text);
                    }
                }
            }
            profileHost.Close();
            Check(!students.IsHandleCreated && !bills.IsHandleCreated,"Real-page shortcut coverage does not start database initialization");
        }
        finally {CurrentUser.SignIn(previousUser.UserId,previousUser.Username,previousUser.RoleName,previousUser.MustChangePassword,previousUser.Grants);}
    }
}
