using System.Data;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.Reflection;
using FMSC_SMS.Core;
using Microsoft.Reporting.WinForms;
using PdfSharp.Pdf.IO;

internal static partial class Program
{
    private static void CheckBillRollHeights()
    {
        var school=new InstitutionProfile.PrintIdentity("Fahim Model School & College","0300-1234567","Sarband Peshawar");
        DataTable Bill(int children,int months,string number)
        {
            using var charges=new DataTable();foreach(string col in new[]{"Reference","StudentName","FeeTypeName","CampusName"})charges.Columns.Add(col);
            charges.Columns.Add("BillingMonth",typeof(DateTime));
            foreach(string col in new[]{"DiscountAmount","Due","GrossAmount"})charges.Columns.Add(col,typeof(decimal));
            for(int child=1;child<=children;child++)for(int month=0;month<months;month++)
            {
                charges.Rows.Add("10"+child,"Example Child "+child,"Tuition Fee","Campus Boys",new DateTime(2026,10,1).AddMonths(month),100m,1400m,1500m);
                charges.Rows.Add("10"+child,"Example Child "+child,"Transport Fee","Campus Boys",new DateTime(2026,10,1).AddMonths(month),0m,500m,500m);
            }
            using var data=children>1?FamilyBillReport.BuildData(school.Name,school.ContactNumber,school.Address,"Example Parent",charges)
                :StudentBillReport.BuildData(school.Name,school.ContactNumber,school.Address,"Example Parent",charges);
            var saved=new DataTable();saved.Columns.Add("BillNumber");saved.Columns.Add("IssuedAt",typeof(DateTime));saved.Columns.Add("SnapshotXml");
            saved.Rows.Add(number,new DateTime(2026,10,29,12,0,0),FeeBillDocuments.Snapshot(data));return saved;
        }
        using var shortBill=Bill(1,1,"B-SHORT");using var longBill=Bill(6,4,"FB-LONG");using var mediumBill=Bill(2,2,"FB-MEDIUM");
        using var combined=OutstandingBillsPreview.CombineSaved(new[]{shortBill,longBill,mediumBill},true);
        using var display=FamilyFeeDocumentPresentation.Bill(combined);
        string path=Path.Combine(AppContext.BaseDirectory,"Reports",FamilyBillReport.TemplateFile);
        using var roll=FeeBillRollDocument.Render(path,FamilyBillReport.DataSetName,display);
        Check(roll.Pages.Count==3,"Each selected bill renders as one complete slip without a totals-only continuation page");
        int[] heights=roll.Pages.Select(p=>p.Settings.PaperSize.Height).ToArray();
        Check(heights[0]<heights[2]&&heights[2]<heights[1]&&heights[0]<heights[1]/2,"Short and medium bills keep their own heights instead of inheriting the longest one");
        string folder=Path.GetFullPath("artifacts/bill-roll-heights");Directory.CreateDirectory(folder);
        byte[] exported=roll.ExportPdf();File.WriteAllBytes(Path.Combine(folder,"mixed-length-bills.pdf"),exported);
        var searchIndex=ReportPageSearchIndex.FromPdf(exported);
        Check(searchIndex.FindPages("FB-LONG").SequenceEqual(new[]{2}),"Search maps a long family bill to its actual physical slip page");
        Check(searchIndex.FindPages("FB-MEDIUM").SequenceEqual(new[]{3}),"Search works across mixed-height bill pages without guessing by source row");
        using(var pdf=PdfReader.Open(new MemoryStream(exported),PdfDocumentOpenMode.Import))
        {
            Check(pdf.PageCount==3,"Combined PDF contains three complete bills");
            for(int i=0;i<3;i++)Check(Math.Abs(pdf.Pages[i].Height.Point/72-heights[i]/100d)<0.02,"PDF page height matches that bill's printer geometry");
        }
        for(int i=0;i<roll.Pages.Count;i++)
        {
            var page=roll.Pages[i];page.Image.Position=0;using var emf=new Metafile(page.Image);
            using var image=new Bitmap(page.Settings.PaperSize.Width,heights[i]);image.SetResolution(100,100);
            using(var graphics=Graphics.FromImage(image)){graphics.Clear(Color.White);graphics.DrawImage(emf,new Rectangle(0,0,image.Width,image.Height));}
            image.Save(Path.Combine(folder,$"bill-{i+1}.png"));
            int lastInk=-1;
            for(int y=image.Height-1;y>=0&&lastInk<0;y--)
            {
                int ink=0;for(int x=0;x<image.Width;x++){var pixel=image.GetPixel(x,y);if(pixel.R<180&&pixel.G<180&&pixel.B<180)ink++;}
                if(ink>3)lastInk=y;
            }
            Check(lastInk>0&&image.Height-lastInk<=40,$"Bill {i+1} ends with only a small margin ({image.Height-lastInk} hundredths of an inch), not blank roll paper");
        }
        using(var print=roll.CreatePrintDocument("Bill height fixture"))
        {
            var begin=typeof(RdlcPrintDocument).GetMethod("OnBeginPrint",BindingFlags.NonPublic|BindingFlags.Instance)!;
            var query=typeof(RdlcPrintDocument).GetMethod("OnQueryPageSettings",BindingFlags.NonPublic|BindingFlags.Instance)!;
            for(int i=0;i<3;i++)
            {
                print.PrinterSettings.PrintRange=PrintRange.SomePages;print.PrinterSettings.FromPage=i+1;print.PrinterSettings.ToPage=i+1;
                begin.Invoke(print,new object[]{new PrintEventArgs()});var settings=new QueryPageSettingsEventArgs(print.DefaultPageSettings);
                query.Invoke(print,new object[]{settings});Check(settings.PageSettings.PaperSize.Height==heights[i],"Printing a selected bill uses its own page height");
                using var dialog=ReportPrintDialog.Create(print,i+1,true);
                begin.Invoke(print,new object[]{new PrintEventArgs()});settings=new QueryPageSettingsEventArgs(print.DefaultPageSettings);
                query.Invoke(print,new object[]{settings});Check(settings.PageSettings.PaperSize.Height==heights[i],"The shared current-page print choices retain that bill's own paper height");
            }
        }
        using(var individual=FeeBillDocuments.ReadSavedData(shortBill.Rows[0]))
        using(var prepared=FeeBillPresentation.Prepare(individual))
        using(var single=FeeBillRollDocument.Render(Path.Combine(AppContext.BaseDirectory,"Reports",StudentBillReport.TemplateFile),StudentBillReport.DataSetName,prepared))
            Check(single.Pages.Count==1&&single.Pages[0].Settings.PaperSize.Height<heights[1]/2,"Individual thermal bills use the same independently fitted path");

        Exception? error=null;using var timer=new System.Windows.Forms.Timer {Interval=100};
        timer.Tick+=(_,_)=>{
            timer.Stop();var form=Application.OpenForms.Cast<Form>().Single(f=>f.Text=="Variable Bill Heights");
            try
            {
                IEnumerable<Control> Walk(Control c)=>c.Controls.Cast<Control>().SelectMany(x=>new[]{x}.Concat(Walk(x)));
                var overview=Walk(form).OfType<ReportPageOverview>().Single();overview.SetSinglePage(true);overview.SetZoom(ZoomMode.Percent,100);Application.DoEvents();
                var rectangles=(List<Rectangle>)typeof(ReportPageOverview).GetField("rectangles",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(overview)!;
                Check(Math.Abs(rectangles[0].Width-roll.Pages[0].Settings.PaperSize.Width/100f*overview.DeviceDpi)<2,
                    "At 100 percent the preview uses the PDF paper width, ignoring mismatched EMF resolution metadata");
                Check(Math.Abs(rectangles[0].Height-heights[0]/100f*overview.DeviceDpi)<2,
                    "At 100 percent the preview uses the same bill height as PDF export and printing");
                overview.SetZoom(ZoomMode.FullPage,100);Application.DoEvents();
                var first=rectangles[0];
                Check(first.Width>0 && first.Height>0 && first.Left>=0 && first.Right<=overview.ClientSize.Width,
                    "Full-page preview shows the entire bill without horizontal clipping");
                using(var singleImage=new Bitmap(form.Width,form.Height))
                {form.DrawToBitmap(singleImage,new Rectangle(Point.Empty,singleImage.Size));singleImage.Save(Path.Combine(folder,"preview-single.png"));}
                overview.SetSinglePage(false);overview.SetZoom(ZoomMode.FullPage,100);Application.DoEvents();
                Walk(form).OfType<ModernComboBox>().Single(c=>c.AccessibleName=="Page view").SelectedIndex=1;
                Check(rectangles[0].Height<rectangles[2].Height&&rectangles[2].Height<rectangles[1].Height,"Multiple-page preview draws each bill at its own aspect ratio");
                using var image=new Bitmap(form.Width,form.Height);form.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(Path.Combine(folder,"preview.png"));
                Walk(form).OfType<ModernButton>().Single(b=>b.Text=="Next").PerformClick();
                Check(Walk(form).OfType<ModernButton>().Any(b=>b.Text=="Print page 2"),"Navigation and Print this page target the selected bill");
                var search=Walk(form).OfType<ReportPreviewSearchBar>().Single();
                var flags=BindingFlags.Instance|BindingFlags.NonPublic;
                ((ModernTextBox)typeof(ReportPreviewSearchBar).GetField("query",flags)!.GetValue(search)!).Text="FB-MEDIUM";
                var found=(Task)typeof(ReportPreviewSearchBar).GetMethod("FindAsync",flags)!.Invoke(search,new object[]{1})!;
                PumpUntil(()=>found.IsCompleted,"Thermal preview search completes");found.GetAwaiter().GetResult();
                Check(Walk(form).OfType<ModernButton>().Any(b=>b.Text=="Print page 3"),"Thermal preview search updates navigation and Print this page to the matching bill");
            }
            catch(Exception ex){error=ex;}
            finally{form.Close();}
        };
        timer.Start();FeeBillRollPreview.Show(null,"Variable Bill Heights",roll);if(error is not null)throw error;
    }
}
