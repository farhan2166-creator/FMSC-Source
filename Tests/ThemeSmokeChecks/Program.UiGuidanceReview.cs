using FMSC_SMS.Core;
using FMSC_SMS.Pages.Reports.Finance;
using FMSC_SMS.Pages.Students.Certificates;
using FMSC_SMS.Pages.Students.Profile;
using FMSC_SMS.Pages.Security;

internal static partial class Program
{
    private static void CheckStudentCertificateLayouts()
    {
        string output=Path.GetFullPath("artifacts/student-certificate-layouts");Directory.CreateDirectory(output);
        var originalTheme=AppTheme.CurrentTheme;
        var previous=(CurrentUser.UserId,CurrentUser.Username,CurrentUser.RoleName,CurrentUser.MustChangePassword,CurrentUser.GetPermissions().ToArray());
        try
        {
            CurrentUser.SignIn(919,"Certificate layout fixture","Administrator",false,Array.Empty<string>());
            foreach(var theme in new[]{AppThemeType.OceanBlue,AppThemeType.ProfessionalSimple})
            foreach(var example in new (string Name,Func<Control> Create)[]{
                ("regular",()=>new RegularStudentCertificatePage(false)),
                ("school-leaving",()=>new LeaveCertificatePage(null,false)),
                ("college-leaving",()=>new CollegeLeaveCertificatePage(false)),
                ("birth-date",()=>new DateOfBirthCertificatePage(false)),
                ("sports",()=>new SportsCertificatePage(false)),
                ("no-objection",()=>new NoObjectionCertificatePage(false)),
                ("character",()=>new CharacterCertificatePage(false)) })
            {
                AppTheme.SetTheme(theme);
                using var page=example.Create();
                var picker=Descendants(page).OfType<StudentPicker>().Single();
                Control card=picker;while(card is not HumanCard)card=card.Parent!;
                var root=(TableLayoutPanel)card.Parent!;root.Parent!.Controls.Remove(root);
                using var form=new Form { AutoScaleMode=AutoScaleMode.None,ShowInTaskbar=false,
                    StartPosition=FormStartPosition.Manual,Location=new Point(-20000,-20000),ClientSize=new Size(850,620) };
                root.Dock=DockStyle.Fill;form.Controls.Add(root);form.Show();Application.DoEvents();
                foreach(var input in Descendants(card))input.Enabled=true;
                foreach(int width in new[]{850,1600,850})
                {
                    form.ClientSize=new Size(width,620);Application.DoEvents();
                    form.ClientSize=new Size(width,(int)root.RowStyles[0].Height);Application.DoEvents();
                    var fields=((HumanCard)card).Content.Controls.OfType<TableLayoutPanel>()
                        .Single(table=>Equals(table.Tag,CertificateUi.FieldGridTag));
                    Check(fields.Width>0 && fields.Height>0 && card.Height>=fields.Height,
                        $"{example.Name}/{theme}/{width}: visible certificate fields have usable space");
                    foreach(var input in Descendants(card).Where(control=>
                        control is StudentPicker or ModernDatePicker or ModernComboBox or ModernTextBox &&
                        control.Parent is TableLayoutPanel parent&&parent.Controls.OfType<Label>().Any()))
                    {
                        var label=input.Parent!.Controls.OfType<Label>().Single();
                        Check(input.Parent.ClientRectangle.Contains(input.Bounds),$"{example.Name}/{theme}/{width}: input fits its field");
                        Check(!input.Bounds.IntersectsWith(label.Bounds),$"{example.Name}/{theme}/{width}: input clears its caption");
                        Check(input.Bottom<=input.Parent.ClientSize.Height,$"{example.Name}/{theme}/{width}: input remains fully inside its field");
                    }
                    using var bitmap=new Bitmap(card.Width,card.Height);card.DrawToBitmap(bitmap,card.ClientRectangle);
                    bitmap.Save(Path.Combine(output,$"{example.Name}-{theme}-{width}.png"));
                }
                Check(!page.IsHandleCreated,"Certificate layout inspection starts no student lookup or SQL operation");
                form.Close();
            }
        }
        finally
        {
            AppTheme.SetTheme(originalTheme);
            CurrentUser.SignIn(previous.UserId,previous.Username,previous.RoleName,previous.MustChangePassword,previous.Item5);
        }
    }

    // Offline captures for the audit's remaining layout questions. No student
    // lookup, report creation, certificate issuance or database access is started.
    private static void CaptureUiGuidanceReview()
    {
        string output=Path.GetFullPath("artifacts/ui-guidance-review");
        Directory.CreateDirectory(output);
        var originalTheme=AppTheme.CurrentTheme;
        var previous=(CurrentUser.UserId,CurrentUser.Username,CurrentUser.RoleName,CurrentUser.MustChangePassword,CurrentUser.GetPermissions().ToArray());
        try
        {
            CurrentUser.SignIn(919,"Guidance rendering fixture","Administrator",false,Array.Empty<string>());
            foreach(var theme in new[]{AppThemeType.OceanBlue,AppThemeType.ProfessionalSimple})
            foreach(var size in new[]{new Size(1000,620),new Size(1280,720)})
            {
                AppTheme.SetTheme(theme);
                Draw("annual-receipts",()=>new AnnualFeeReceiptsPage(false));
                Draw("certificates",()=>new StudentCertificatesPage());
                Draw("student-profile",()=>new StudentProfilePage(null,false));

                void Draw(string name,Func<Control> create)
                {
                    using var page=create();
                    using var form=new Form {ClientSize=size,ShowInTaskbar=false,
                        StartPosition=FormStartPosition.Manual,Location=new Point(-32000,-32000)};
                    page.Dock=DockStyle.Fill;
                    form.Controls.Add(page);
                    form.Show();Application.DoEvents();form.PerformLayout();
                    Check(page.Width>0&&page.Height>0,name+" has a rendered client area.");
                    using var bitmap=new Bitmap(form.Width,form.Height);
                    form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));
                    bitmap.Save(Path.Combine(output,$"{name}-{theme}-{size.Width}.png"));
                    if(name=="annual-receipts")
                    {
                        var tip=Descendants(page).OfType<HumanCard>().Single(card=>
                            Descendants(card.Header).Any(c=>c.Text=="Tip"));
                        Console.WriteLine($"Annual receipts {theme} {size.Width}x{size.Height}, actual DPI {form.DeviceDpi}: Tip card {tip.Width}x{tip.Height}; empty content {tip.Content.Width}x{tip.Content.Height}.");
                    }
                    form.Close();
                }
            }
        }
        finally
        {
            AppTheme.SetTheme(originalTheme);
            CurrentUser.SignIn(previous.UserId,previous.Username,previous.RoleName,previous.MustChangePassword,previous.Item5);
        }
    }
}
