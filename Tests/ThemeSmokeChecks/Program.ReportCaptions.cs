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
            if(certificate)
                document=XDocument.Parse(CertificateLetterhead.Definition(path,school,
                    mode:CertificateLetterhead.DefaultMode(path)));
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
            if(certificate)
            {
                double? bodyTop = null;
                double? bodyBottomReservation = null;
                foreach(var mode in new[]{ CertificatePaperMode.PlainPaper, CertificatePaperMode.PreprintedLetterhead })
                {
                    var layout = XDocument.Parse(CertificateLetterhead.Definition(path,school,mode:mode));
                    var page = layout.Root!.Element(ns+"Page")!;
                    var header = page.Element(ns+"PageHeader");
                    var footer = page.Element(ns+"PageFooter");
                    bool preprinted = mode == CertificatePaperMode.PreprintedLetterhead;
                    Check(header is not null && footer is not null,
                        name+" "+mode+" reserves matching header and footer bands.");
                    Check(page.Element(ns+"TopMargin")?.Value=="0.4in" &&
                          page.Element(ns+"BottomMargin")?.Value=="0.4in",
                        name+" "+mode+" has matching page margins.");
                    Check(header!.Element(ns+"Height")?.Value=="3.1in" &&
                          footer!.Element(ns+"Height")?.Value=="0.75in",
                        name+" "+mode+" reserves the correct stationery geometry.");
                    Check((header.Element(ns+"ReportItems")?.HasElements ?? false) != preprinted &&
                          (footer.Element(ns+"ReportItems")?.HasElements ?? false) != preprinted,
                        name+" "+mode+" "+(preprinted?"prints no stationery artwork.":"prints school stationery."));
                    double currentTop = ReportInches(page.Element(ns+"TopMargin")?.Value)
                        + ReportInches(header.Element(ns+"Height")?.Value);
                    double currentBottom = ReportInches(page.Element(ns+"BottomMargin")?.Value)
                        + ReportInches(footer.Element(ns+"Height")?.Value);
                    if(bodyTop is null) { bodyTop=currentTop; bodyBottomReservation=currentBottom; }
                    else Check(Math.Abs(bodyTop.Value-currentTop)<0.001 &&
                               Math.Abs(bodyBottomReservation!.Value-currentBottom)<0.001,
                        name+" keeps body coordinates identical between both paper modes.");
                    if(CertificateLetterhead.UsesPreprintedPad(path))
                    {
                        var copyLabel = layout.Descendants(ns+"Textbox").Single(
                            element => (string?)element.Attribute("Name")=="txtCopyType");
                        var values = copyLabel.Descendants(ns+"TextRun")
                            .Select(run => (string?)run.Element(ns+"Value")).ToArray();
                        Check(values.Length==1 && values[0]!.Contains("DUPLICATE COPY",StringComparison.Ordinal),
                            name+" prints no stray red parentheses on an original and labels duplicates.");
                    }
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
                using var originalDocument = UglyToad.PdfPig.PdfDocument.Open(pdf);
                Check(originalDocument.NumberOfPages==1 && alternateDocument.NumberOfPages==1,
                    name+" fits on one physical sheet in both stationery modes.");
                var originalTitle = originalDocument.GetPage(1).GetWords()
                    .FirstOrDefault(word => word.Text.Contains("CERTIFICATE",StringComparison.OrdinalIgnoreCase));
                var alternateTitle = alternateDocument.GetPage(1).GetWords()
                    .FirstOrDefault(word => word.Text.Contains("CERTIFICATE",StringComparison.OrdinalIgnoreCase));
                Check(originalTitle is not null && alternateTitle is not null,
                    name+" keeps its certificate heading readable in both stationery modes.");
                Check(Math.Abs(originalTitle!.BoundingBox.Bottom-alternateTitle!.BoundingBox.Bottom)<2.0 &&
                      Math.Abs(originalTitle.BoundingBox.Left-alternateTitle.BoundingBox.Left)<2.0,
                    name+" keeps the certificate heading at the same printed page coordinates.");

                // Exercise both stationery modes with the real optional principal
                // signature and preserve the approved unsigned layout unchanged.
                if(name is "LeaveCertificate" or "RegularStudentCertificate")
                {
                    using var signatureBitmap = new Bitmap(150, 40);
                    using (var graphics = Graphics.FromImage(signatureBitmap))
                    {
                        graphics.Clear(Color.White);
                        graphics.DrawLine(Pens.Black, 8, 22, 140, 14);
                    }
                    using var bytes = new MemoryStream();
                    signatureBitmap.Save(bytes, System.Drawing.Imaging.ImageFormat.Png);
                    byte[] signature = bytes.ToArray();
                    foreach(var signatureMode in new[]{ CertificatePaperMode.PreprintedLetterhead,
                        CertificatePaperMode.PlainPaper })
                    {
                        string unsigned = CertificateLetterhead.Definition(path, school, mode:signatureMode);
                        Check(CertificateSignature.Apply(unsigned, signature, "image/png", false)==unsigned,
                            name+" "+signatureMode+" retains its exact unsigned certificate definition.");
                        string signed = CertificateSignature.Apply(unsigned, signature, "image/png", true);
                        var signedLayout = XDocument.Parse(signed);
                        Check(signedLayout.Descendants(ns+"Image").Count(element=>
                                (string?)element.Attribute("Name")=="PrincipalSignaturePrintImage")==1
                              && signedLayout.Descendants(ns+"Line").Any(element=>
                                (string?)element.Attribute("Name")=="lnPrincipal"),
                            name+" "+signatureMode+" prints the optional image without removing the principal line.");
                        using var signedReport = new LocalReport();
                        signedReport.LoadReportDefinition(new StringReader(signed));
                        signedReport.EnableExternalImages = true;
                        foreach(ReportDataSource source in report.DataSources)
                            signedReport.DataSources.Add(new ReportDataSource(source.Name, source.Value));
                        using var signedPdf = UglyToad.PdfPig.PdfDocument.Open(signedReport.Render("PDF"));
                        Check(signedPdf.NumberOfPages==1,
                            name+" "+signatureMode+" with principal signature renders on one PDF page.");
                    }
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
