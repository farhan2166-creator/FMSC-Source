using System.Data;
using System.Reflection;
using FMSC_SMS.Core;
using FMSC_SMS.Pages.Reports;
using FMSC_SMS.Pages.Reports.Finance;
using FMSC_SMS.Pages.Reports.Certificates;
using FMSC_SMS.Pages.Security;
using Microsoft.Reporting.WinForms;

internal static partial class Program
{
    private static void CheckNewReportForms()
    {
        int user=CurrentUser.UserId;string name=CurrentUser.Username,role=CurrentUser.RoleName;bool mustChange=CurrentUser.MustChangePassword;
        var permissions=CurrentUser.GetPermissions().ToArray();
        try
        {
            CurrentUser.SignIn(1,"Report fixture","Administrator",false,Array.Empty<string>());
            const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
            using var monthly=new MonthlyFeeReconciliationPage();
            var group=(ModernComboBox)typeof(CanonicalFinanceReportPage).GetField("GroupBy",flags)!.GetValue(monthly)!;
            Check(group.Enabled && group.Items.Cast<string>().SequenceEqual(MonthlyFeeReconciliationData.Groupings),"Reconciliation exposes all five groupings");
            var from=(ModernDatePicker)typeof(CanonicalFinanceReportPage).GetField("From",flags)!.GetValue(monthly)!;
            Check(!from.Checked,"Reconciliation starts with an unrestricted optional month filter");
            from.Checked=true;from.Value=new DateTime(2026,10,31);Check(from.Value==new DateTime(2026,10,1) && from.Checked,"Month selection normalizes to month start");
            using var certificates=new CertificateIssueRegisterPage();
            var type=(ModernComboBox)typeof(CertificateIssueRegisterPage).GetField("type",flags)!.GetValue(certificates)!;
            var expectedTypes=CertificateIssueRegisterData.GeneralTypes.Concat(CertificateIssueRegisterData.LeavingTypes).ToArray();
            Check(type.Items.Contains("All") && expectedTypes.All(type.Items.Contains) &&
                  type.Items.Count==expectedTypes.Length+1,
                "Administrator can select every registered certificate category without a frozen type count");
            var print=(ModernButton)typeof(CertificateIssueRegisterPage).GetField("print",flags)!.GetValue(certificates)!;
            Check(!print.Enabled,"Certificate print requires generated results");
            Check(!monthly.IsHandleCreated && !certificates.IsHandleCreated,"Constructing report controls performs no database initialization");
            CurrentUser.SignIn(3,"General certificates","Viewer",false,new[]{"Reports.View","Certificates.Create"});
            using var general=new CertificateIssueRegisterPage();
            var generalTypes=(ModernComboBox)typeof(CertificateIssueRegisterPage).GetField("type",flags)!.GetValue(general)!;
            Check(generalTypes.Items.Contains("All") &&
                  CertificateIssueRegisterData.GeneralTypes.All(generalTypes.Items.Contains) &&
                  !CertificateIssueRegisterData.LeavingTypes.Any(generalTypes.Items.Contains) &&
                  generalTypes.Items.Count==CertificateIssueRegisterData.GeneralTypes.Length+1,
                "General certificate access includes its current catalog but excludes every leaving type");
            CurrentUser.SignIn(4,"Leaving only","Viewer",false,new[]{"Reports.View","Students.Leave"});
            using var leaving=new CertificateIssueRegisterPage();
            var leaveTypes=(ModernComboBox)typeof(CertificateIssueRegisterPage).GetField("type",flags)!.GetValue(leaving)!;
            Check(leaveTypes.Items.Contains("All") &&
                  CertificateIssueRegisterData.LeavingTypes.All(leaveTypes.Items.Contains) &&
                  !CertificateIssueRegisterData.GeneralTypes.Any(leaveTypes.Items.Contains) &&
                  leaveTypes.Items.Count==CertificateIssueRegisterData.LeavingTypes.Length+1,
                "Leaving-only access includes only the authorized leaving-type catalog, regardless of ordering");
            CurrentUser.SignIn(2,"Reports only","Viewer",false,new[]{"Reports.View"});
            using var reports=new ReportsHomePage((Action<Func<Control>,string,string>)((_,_,_)=>{}));
            Check(!ReleaseReportControls(reports).Any(c=>c.Text=="Certificate Issue Register"),"Register tile requires certificate access in addition to report access");
            Check(ReleaseReportControls(reports).Any(c=>c.Text=="Monthly Fee Reconciliation"),"Reconciliation is available with report access");
            CurrentUser.SignIn(1,"Report fixture","Administrator",false,Array.Empty<string>());
            var data=new DataTable();data.Columns.Add("BillingChargeId",typeof(long));data.Columns.Add("BillingMonth",typeof(DateTime));
            foreach(string field in new[]{"CampusId","ClassId","SectionId","FeeTypeId"})data.Columns.Add(field,typeof(int));
            foreach(string field in new[]{"Campus","Class","Section","Fee Type"})data.Columns.Add(field);
            foreach(string field in new[]{"GrossAmount","DiscountAmount","NetAmount","Money Allocated","Credit Applied","Outstanding","Excess Settlement"})data.Columns.Add(field,typeof(decimal));
            data.Rows.Add(1L,new DateTime(2026,10,1),1,1,1,1,"Campus Boys","Class 1","A","Tuition Fee",1000m,100m,900m,500m,100m,300m,0m);
            var reconciled=MonthlyFeeReconciliationData.Create(data,"Whole School").Rows;
            var certificateData=new DataTable();
            foreach(string field in new[]{"Recorded On","Certificate Type","Certificate No.","Recipient Type","Recipient","Student / Staff No.","Certificate Date","Entry","Issue Type","Recorded By","Collection","Collected On","Collected By"})certificateData.Columns.Add(field);
            certificateData.Rows.Add("23-Oct-2026 14:35","Character","CHR-2026-0001","Student","Synthetic Student","QR-1001","21-Oct-2026","Issue logged","","Operator","Not Collected","","");
            string folder=Path.GetFullPath("artifacts/new-reports");Directory.CreateDirectory(folder);
            foreach(var (title,rows) in new[]{("Monthly Fee Reconciliation",reconciled),("Certificate Issue Register",certificateData)})
            {
                var built=GridReportPreview.Build("Synthetic School",title,"Synthetic fixture | Report generated: 23-Oct-2026 15:00",rows,"Fixture");
                Check(built.Data.Rows.Count==rows.Rows.Count,title+": RDLC keeps each original row");
                using var report=new LocalReport();report.LoadReportDefinition(new StringReader(built.Definition));report.DataSources.Add(new ReportDataSource("ReportData",built.Data));
                byte[] pdf=report.Render("PDF");
                using var parsed=PdfSharp.Pdf.IO.PdfReader.Open(new MemoryStream(pdf),PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
                Check(parsed.PageCount>=1,title+": established RDLC preview renders to PDF");
                File.WriteAllBytes(Path.Combine(folder,title+".pdf"),pdf);
                string csv=Path.Combine(folder,title+".csv");ReportExportBuilder.WriteCsv(rows,csv);
                Check(File.ReadAllText(csv).Contains(title=="Certificate Issue Register" ? "23-Oct-2026 14:35" : "Oct 2026"),title+": CSV retains the displayed period/time");
            }
        }
        finally { CurrentUser.SignIn(user,name,role,mustChange,permissions); }
    }
}
