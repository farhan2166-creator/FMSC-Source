using System.Data;
using System.Reflection;
using System.Xml.Linq;
using FMSC_SMS.Core;
using Microsoft.Reporting.WinForms;

internal static partial class Program
{
    private static void CheckReportDocumentTools()
    {
        using var bills=new DataTable();
        foreach(string field in new[]{"ReceiptNumber","ReceiptPageKey","AdmissionNumberText","StudentName","DocumentTitle"})bills.Columns.Add(field);
        bills.Rows.Add("B-2026-00057","1","1916","Hadia","STUDENT FEE BILL");
        string single=ReportDocumentNaming.PreviewTitle("Student Fee Bill","StudentBill.rdlc",bills);
        Check(single.Contains("B-2026-00057")&&single.Contains("1916")&&single.Contains("Hadia"),"Single preview identifies its actual bill and student");
        bills.Rows.Add("B-2026-00058","2","1917","Another Student","STUDENT FEE BILL");
        string batch=ReportDocumentNaming.PreviewTitle("Student Fee Bill","StudentBill.rdlc",bills);
        Check(batch=="Student Fee Bills — 2 bills","A bulk preview is named as a batch, not a single bill");
        bills.Rows.Add("B-2026-00058","2","1917","Another Student","STUDENT FEE BILL");
        Check(ReportDocumentNaming.PreviewTitle("Student Fee Bill","StudentBill.rdlc",bills)==batch,"Fee lines do not inflate the document count");
        string filename=ReportDocumentNaming.ExportFileName("Collections / Receipts: Summary",new DateTime(2026,11,3,11,6,22));
        Check(filename.EndsWith("_2026-11-03_11-06-22")&&!filename.Any(c=>"<>:\"/\\|?*".Contains(c)),"Export names contain a readable title and safe date/time to seconds");
        using var certificate=new DataTable();
        foreach(string field in new[]{"AdmissionNumber","StudentName","CertificateNumber"})
            certificate.Columns.Add(field);
        certificate.Rows.Add("6578","Eman Do Shafaqat","SLC-2026-00057");
        Check(ReportDocumentNaming.DocumentExportFileName("School Leaving Certificate","LeaveCertificate.rdlc",certificate)
            == "6578-Eman Do Shafaqat (SLC-2026-00057)",
            "Single certificate PDF suggests admission, name and real certificate number");
        certificate.Rows.Add("6579","Another Student","SLC-2026-00058");
        Check(ReportDocumentNaming.DocumentExportFileName("School Leaving Certificates","LeaveCertificate.rdlc",
            certificate,new DateTime(2026,11,9,12,0,0)).StartsWith("School Leaving Certificates_"),
            "Batch PDF does not falsely identify a single child");
        using var receipt=new DataTable();
        foreach(string field in new[]{"AdmissionNumberText","StudentName","ReceiptNumber"})
            receipt.Columns.Add(field);
        receipt.Rows.Add("6578","Eman Do Shafaqat","R-2026-00057");
        Check(ReportDocumentNaming.DocumentExportFileName("Student Payment Receipt","StudentReceipt.rdlc",receipt)
            == "6578-Eman Do Shafaqat (R-2026-00057)",
            "Single receipt PDF uses the saved receipt reference");
        Check(ReportDocumentNaming.ReportExportFileName("Outstanding","Class 10-A",
            new DateTime(2026,11,9,12,0,0)).StartsWith("Outstanding (Class 10-A)_"),
            "Report PDFs include their selected scope");
        bills.Rows.Clear();bills.Rows.Add("FR-2026-00001","1","1916","Hadia","STUDENT COPY OF FAMILY RECEIPT");
        Check(ReportDocumentNaming.PreviewTitle("Student Payment Receipt","StudentReceipt.rdlc",bills).StartsWith("Student Copy of Family Receipt"),"Child-copy preview agrees with the saved document's purpose");

        // Each 2.4-inch band occupies one physical page, independently of data rows.
        XNamespace ns="http://schemas.microsoft.com/sqlserver/reporting/2008/01/reportdefinition";
        XElement E(string name,params object[] content)=>new(ns+name,content);
        string definition=new XDocument(E("Report",E("Body",E("ReportItems",Enumerable.Range(0,25).Select(i=>
            E("Textbox",new XAttribute("Name","Student"+i),E("CanGrow",true),E("Paragraphs",E("Paragraph",E("TextRuns",E("TextRun",
                E("Value",$"Admn {1900+i} Test Student {i}"),E("Style",E("FontSize","12pt")))),E("Style"))),
                E("Top",(i*2.4).ToString("0.0",System.Globalization.CultureInfo.InvariantCulture)+"in"),
                E("Height","0.4in"),E("Width","6in"),E("Style")))),E("Height","60in"),E("Style")),E("Width","7in"),
            E("Page",E("PageHeight","3in"),E("PageWidth","8in"),E("LeftMargin","0.3in"),E("RightMargin","0.3in"),
                E("TopMargin","0.3in"),E("BottomMargin","0.3in"),E("Style")))).ToString();
        using var report=new LocalReport();report.LoadReportDefinition(new StringReader(definition));
        var index=ReportPageSearchIndex.FromPdf(report.Render("PDF"));
        Check(index.FindPages("1916").SequenceEqual(new[]{17}),"An admission number maps to the actual PDF/print page in a 25-page report");
        Check(index.FindPages("test   student").Length==25,"Search normalizes whitespace and case");
        Check(index.FindPages("").Length==0&&index.FindPages("not present").Length==0,"Blank and missing searches never choose a wrong page");
        using var owner=new ApplicationWindowForm();int selected=0,builds=0;
        using var search=new ReportPreviewSearchBar(owner,()=>{builds++;return Task.FromResult(index);},page=>selected=page);
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var query=(ModernTextBox)typeof(ReportPreviewSearchBar).GetField("query",flags)!.GetValue(search)!;
        void Find(int direction)
        {
            var task=(Task)typeof(ReportPreviewSearchBar).GetMethod("FindAsync",flags)!.Invoke(search,new object[]{direction})!;
            PumpUntil(()=>task.IsCompleted,"Page search completes");task.GetAwaiter().GetResult();
        }
        query.Text="1916";Find(1);Check(selected==17,"Search navigates to the matching page");
        query.Text="Test Student";Find(1);Find(1);Check(selected==2,"Repeated find advances through matching pages");
        Find(-1);Find(-1);Check(selected==25,"Previous match wraps to the last matching page");
        Check(builds==1,"Repeated searches reuse the same rendered-page index");
        search.ResetIndex();query.Text="1916";Find(1);Check(selected==17&&builds==2,"Page setup invalidation rebuilds the search index");
        query.Text="missing";Find(1);Check(selected==17,"An unmatched query leaves the current page unchanged");

        Exception? previewError=null;Task? findTask=null;
        DateTime deadline=DateTime.UtcNow.AddSeconds(30);
        using var timer=new System.Windows.Forms.Timer{Interval=80};
        timer.Tick+=(_,_)=>{
            var preview=Application.OpenForms.Cast<Form>().FirstOrDefault(form=>form.Text=="Page Search Fixture");
            if(preview is null)return;
            try
            {
                if(DateTime.UtcNow>deadline)throw new TimeoutException("Native report search did not complete.");
                IEnumerable<Control> Walk(Control root)=>root.Controls.Cast<Control>().SelectMany(child=>new[]{child}.Concat(Walk(child)));
                var bar=Walk(preview).OfType<ReportPreviewSearchBar>().Single();
                var viewer=Walk(preview).OfType<ReportViewer>().Single();
                if(!bar.Enabled)return;
                if(findTask is null)
                {
                    ((ModernTextBox)typeof(ReportPreviewSearchBar).GetField("query",flags)!.GetValue(bar)!).Text="1916";
                    findTask=(Task)typeof(ReportPreviewSearchBar).GetMethod("FindAsync",flags)!.Invoke(bar,new object[]{1})!;
                    return;
                }
                if(!findTask.IsCompleted)return;
                findTask.GetAwaiter().GetResult();
                Check(viewer.CurrentPage==17,"The real print-layout viewer jumps to the admission number's physical page");
                string output=Path.GetFullPath("artifacts/report-document-tools");Directory.CreateDirectory(output);
                using var image=new Bitmap(preview.Width,preview.Height);preview.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));
                image.Save(Path.Combine(output,"find-admission.png"));
            }
            catch(Exception ex){previewError=ex;}
            timer.Stop();preview.Close();
        };
        using var empty=new DataTable();
        timer.Start();RdlcReportPreview.Show(null,"Page Search Fixture","","Unused",empty,reportDefinition:definition);
        if(previewError is not null)throw previewError;
    }
}
