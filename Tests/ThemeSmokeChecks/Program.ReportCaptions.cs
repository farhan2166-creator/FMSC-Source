using System.Data;
using FMSC_SMS.Core;
using System.Xml.Linq;
using Microsoft.Reporting.WinForms;

internal static partial class Program
{
    private static void CheckReportCaptions(string[]? selectedReports = null)
    {
        string output=Path.GetFullPath("artifacts/report-captions");Directory.CreateDirectory(output);
        using var profile=new DataTable();profile.Columns.Add("SchoolName");
        var school=profile.Rows.Add("Fictional School");
        foreach(string name in selectedReports ?? new[]{"AdmissionForm","ClassMarksSheet","ClassAttendanceSheet",
            "ClassMonthlyAttendanceSheet","ClassOutstandingCharges","ClassSectionSummary",
            "BankAccountOpeningCertificate","CharacterCertificate","CollegeLeaveCertificate","DateOfBirthCertificate",
            "LeaveCertificate","NoObjectionCertificate","ProvisionalCertificate","RegularLecturerCertificate",
            "RegularStudentCertificate","SportsCertificate"})
        {
            string path=Path.Combine(AppContext.BaseDirectory,"Reports",name+".rdlc");
            var document=XDocument.Load(path);XNamespace ns=document.Root!.Name.Namespace;
            bool certificate=name.EndsWith("Certificate",StringComparison.Ordinal);
            if(certificate && !CertificateLetterhead.UsesPreprintedPad(path))
                document=XDocument.Parse(CertificateLetterhead.Definition(path,school));
            if(certificate && !CertificateLetterhead.UsesPreprintedPad(path))
            {
                var image=document.Descendants(ns+"Image").Single(element=>(string?)element.Attribute("Name")=="CertificateHeaderImage");
                var page=document.Root!.Element(ns+"Page")!;
                double printableWidth=ReportInches(page.Element(ns+"PageWidth")?.Value)
                    -ReportInches(page.Element(ns+"LeftMargin")?.Value)
                    -ReportInches(page.Element(ns+"RightMargin")?.Value);
                double artworkWidth=ReportInches(image.Element(ns+"Width")?.Value);
                double artworkHeight=ReportInches(image.Element(ns+"Height")?.Value);
                Check(image.Element(ns+"Sizing")?.Value=="FitProportional" &&
                      artworkWidth>0 && artworkHeight>0 && artworkWidth<=printableWidth+0.02,
                    name+" keeps proportional certificate artwork within the printable page, regardless of header dimensions or margins.");
            }
            if(CertificateLetterhead.UsesPreprintedPad(path))
                Check(document.Descendants(ns+"TopMargin").Single().Value=="2in",name+" reserves two inches for preprinted letterhead.");
            if(certificate)
            {
                foreach(var mode in new[]{ CertificatePaperMode.PlainPaper, CertificatePaperMode.PreprintedLetterhead })
                {
                    var layout = XDocument.Parse(CertificateLetterhead.Definition(path,school,mode:mode));
                    var page = layout.Root!.Element(ns+"Page")!;
                    bool pad = mode==CertificatePaperMode.PreprintedLetterhead;
                    Check((page.Element(ns+"PageHeader") is null)==pad &&
                        (page.Element(ns+"PageFooter") is null)==pad,
                        name+" "+mode+" shows exactly the requested stationery.");
                    Check(page.Element(ns+"TopMargin")?.Value==(pad?"2in":"0.4in") &&
                        page.Element(ns+"BottomMargin")?.Value==(pad?"2.5in":"0.4in"),
                        name+" "+mode+" reserves the correct printable margins.");
                    if(pad && CertificateLetterhead.UsesPreprintedPad(path))
                        Check(XNode.DeepEquals(XDocument.Load(path),layout),
                            name+" preserves the approved preprinted leaving-certificate layout.");
                }
            }
            using var report=new LocalReport();report.LoadReportDefinition(new StringReader(document.ToString()));
            report.EnableExternalImages=true;
            foreach(var dataset in document.Descendants(ns+"DataSet"))
            {
                var data=new DataTable();
                foreach(var field in dataset.Element(ns+"Fields")!.Elements(ns+"Field"))
                {
                    string typeName=field.Elements().FirstOrDefault(element=>element.Name.LocalName=="TypeName")?.Value??"System.String";
                    data.Columns.Add(field.Element(ns+"DataField")!.Value,Type.GetType(typeName,true)!);
                }
                var row=data.NewRow();
                foreach(DataColumn column in data.Columns)
                    row[column]=column.DataType==typeof(string)?column.ColumnName=="SchoolName"?"Fictional Meadow School & College":
                        column.ColumnName=="Scope"?"Campus: All campuses | Academic year: All years":
                        column.ColumnName=="Generated"?"03-11-2026 13:17":
                        column.ColumnName=="CampusName"?"Campus Boys":column.ColumnName=="ClassName"?"Four":column.ColumnName=="SectionName"?"F":
                        column.ColumnName=="YearName"?"2026-2027":column.ColumnName=="YearSelection"?"All academic years":
                        column.ColumnName=="SerialNumber"?"1":column.ColumnName=="PageLabel"?"Page 1 of 1":
                        column.ColumnName=="ExamDate"?(name=="ClassMonthlyAttendanceSheet"?"October 2026":"03-11-2026"):
                        column.ColumnName.StartsWith("DayHeader",StringComparison.Ordinal)?int.Parse(column.ColumnName[9..]).ToString():
                        column.ColumnName.StartsWith("Number",StringComparison.Ordinal)&&int.TryParse(column.ColumnName[6..],out int serial)?serial.ToString():
                        column.ColumnName=="DateOfBirthInWords"?"24 October 2026":
                        column.ColumnName=="DateOfBirthInFigures"?"24-10-2026":
                        column.ColumnName=="ClassPromotedTo"?"Class 8 — 8":
                        column.ColumnName=="InstitutionLine"?"Fictional School Boys Campus Fictional City":"Example":column.DataType==typeof(DateTime)?new DateTime(2026,10,24):
                        column.DataType==typeof(bool)?true:Convert.ChangeType(1,column.DataType);
                data.Rows.Add(row);
                if(name=="ClassSectionSummary")
                {row["ActiveStudents"]=3;row["Boys"]=2;row["Girls"]=1;row["Other"]=0;row["Outstanding"]=1500m;}
                if(name=="ProvisionalCertificate")
                {
                    document=XDocument.Parse(CertificateLetterhead.Definition(path,school,row,
                        new[]{"English","Urdu","Islamiyat","Pakistan Studies","Mathematics","Physics","Chemistry","Biology","Computer Science","Economics","Statistics","Civics"}));
                    report.LoadReportDefinition(new StringReader(document.ToString()));
                }
                using var printable=CertificateUi.PrintData(path,data);
                if(CertificateLetterhead.UsesPreprintedPad(path))
                    Check(printable.Rows[0].Field<string>("ClassPromotedTo")=="Class 8"
                        && data.Rows[0].Field<string>("ClassPromotedTo")=="Class 8 — 8",
                        name+" prints the class once without rewriting the saved certificate.");
                report.DataSources.Add(new ReportDataSource((string)dataset.Attribute("Name")!,printable.Copy()));
            }
            byte[] pdf=report.Render("PDF");File.WriteAllBytes(Path.Combine(output,name+".pdf"),pdf);
            if(name.StartsWith("Class",StringComparison.Ordinal))CheckClassReportHeadingPdf(name,pdf);
            if(certificate && !CertificateLetterhead.UsesPreprintedPad(path))
                Check(System.Text.RegularExpressions.Regex.Matches(System.Text.Encoding.Latin1.GetString(pdf),@"/Type\s*/Page\b").Count==1,
                    name+" fits its taller header and certificate body on one PDF page.");
            byte[] png=report.Render("IMAGE","<DeviceInfo><OutputFormat>PNG</OutputFormat><DpiX>110</DpiX><DpiY>110</DpiY></DeviceInfo>");
            File.WriteAllBytes(Path.Combine(output,name+".png"),png);
            Check(pdf.Length>1000&&png.Length>1000,name+" renders its split text/data runs with representative typed fixture values.");
            if(certificate)
            {
                string html=System.Text.Encoding.UTF8.GetString(report.Render("HTML4.0"));
                Check(html.Contains("24-10-2026",StringComparison.Ordinal),name+" prints its standard date as day-month-year.");
                if(name is "CollegeLeaveCertificate" or "LeaveCertificate" or "ProvisionalCertificate" or "DateOfBirthCertificate")
                    Check(html.Contains("24 October 2026",StringComparison.Ordinal),name+" retains the full month in its written birth date.");
                if(name=="RegularStudentCertificate")
                    Check(html.Contains("Fictional School",StringComparison.Ordinal) && !html.Contains("Boys Campus",StringComparison.Ordinal),
                        "Regular student certificate prints the school name without the campus.");

                // Rendering the non-default mode catches RDLC schema and pagination regressions.
                var alternativeMode = CertificateLetterhead.DefaultMode(path)==CertificatePaperMode.PlainPaper
                    ? CertificatePaperMode.PreprintedLetterhead : CertificatePaperMode.PlainPaper;
                using var alternate = new LocalReport();
                alternate.LoadReportDefinition(new StringReader(CertificateLetterhead.Definition(path,school,mode:alternativeMode)));
                alternate.EnableExternalImages = true;
                foreach(ReportDataSource source in report.DataSources)
                    alternate.DataSources.Add(new ReportDataSource(source.Name,source.Value));
                byte[] alternatePdf = alternate.Render("PDF");
                using var alternateDocument = UglyToad.PdfPig.PdfDocument.Open(alternatePdf);
                Check(alternateDocument.NumberOfPages==1,
                    name+" "+alternativeMode+" fits its content on one page.");

                // Windows rendering acceptance for optional School Profile
                // signature: both approved leaving-pad and ordinary certificates
                // must remain printable when the image is enabled.
                if(name is "LeaveCertificate" or "RegularStudentCertificate")
                {
                    using var signatureBitmap = new Bitmap(150, 40);
                    using var graphics = Graphics.FromImage(signatureBitmap);
                    graphics.Clear(Color.White);
                    graphics.DrawLine(Pens.Black, 8, 22, 140, 14);
                    using var imageBytes = new MemoryStream();
                    signatureBitmap.Save(imageBytes, System.Drawing.Imaging.ImageFormat.Png);
                    string unsigned = CertificateLetterhead.Definition(path,school,
                        mode:CertificateLetterhead.DefaultMode(path));
                    Check(CertificateSignature.Apply(unsigned,imageBytes.ToArray(),"image/png",false)==unsigned,
                        name+" keeps byte-identical certificate content when signature printing is disabled.");
                    string signed = CertificateSignature.Apply(unsigned,imageBytes.ToArray(),"image/png",true);
                    var signedDefinition = XDocument.Parse(signed);
                    Check(signedDefinition.Descendants(ns+"Image").Any(element=>
                        (string?)element.Attribute("Name")=="PrincipalSignaturePrintImage"),
                        name+" inserts the optional principal signature without replacing the existing line.");
                    using var signedReport = new LocalReport();
                    signedReport.LoadReportDefinition(new StringReader(signed));
                    signedReport.EnableExternalImages=true;
                    foreach(ReportDataSource source in report.DataSources)
                        signedReport.DataSources.Add(new ReportDataSource(source.Name,source.Value));
                    byte[] signedPdf=signedReport.Render("PDF");
                    using var signedDocument=UglyToad.PdfPig.PdfDocument.Open(signedPdf);
                    Check(signedDocument.NumberOfPages==1,
                        name+" enabled signature renders on one PDF page at its approved paper-mode margins.");
                }

            }
        }
    }

    private static double ReportInches(string? value)
    {
        if(string.IsNullOrWhiteSpace(value))return 0;
        double number=double.Parse(value[..^2],System.Globalization.CultureInfo.InvariantCulture);
        return value[^2..] switch {"cm"=>number/2.54,"mm"=>number/25.4,"pt"=>number/72,_=>number};
    }
    private static void CheckClassReportHeadingPdf(string name,byte[] bytes)
    {
        using var pdf=UglyToad.PdfPig.PdfDocument.Open(bytes);
        Check(pdf.NumberOfPages==1,
            name+": representative class report renders on one printable sheet");
        var page=pdf.GetPage(1);
        Check(page.GetWords().Any(word=>word.Text=="Fictional"),
            name+": the school heading remains present in the rendered report");
        Check(page.Letters.All(letter=>letter.BoundingBox.Left>=-1 &&
              letter.BoundingBox.Right<=page.Width+1 &&
              letter.BoundingBox.Bottom>=-1 &&
              letter.BoundingBox.Top<=page.Height+1),
            name+": printed class report text stays on the page without clipping");
    }
}
