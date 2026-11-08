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
            if(name.StartsWith("Class",StringComparison.Ordinal))CheckClassReportHeadingLayout(name,document);
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
            if(name.StartsWith("Class",StringComparison.Ordinal))CheckClassReportHeadingPdf(name,document,pdf);
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
            }
        }
    }

    private static double ReportInches(string? value)
    {
        if(string.IsNullOrWhiteSpace(value))return 0;
        double number=double.Parse(value[..^2],System.Globalization.CultureInfo.InvariantCulture);
        return value[^2..] switch {"cm"=>number/2.54,"mm"=>number/25.4,"pt"=>number/72,_=>number};
    }
    private static void CheckClassReportHeadingLayout(string name,XDocument document)
    {
        XNamespace ns=document.Root!.Name.Namespace;
        XElement Box(string key)=>document.Descendants(ns+"Textbox").Single(box=>(string?)box.Attribute("Name")==key);
        void Center(string key,string reference)
        {
            var box=Box(key);var span=Box(reference);
            Check(box.Descendants(ns+"Paragraph").All(paragraph=>paragraph.Element(ns+"Style")?.Element(ns+"TextAlign")?.Value=="Center"),name+": "+key+" is centered");
            Check(Math.Abs(ReportInches(box.Element(ns+"Left")?.Value)-ReportInches(span.Element(ns+"Left")?.Value))<.002 &&
                Math.Abs(ReportInches(box.Element(ns+"Width")?.Value)-ReportInches(span.Element(ns+"Width")?.Value))<.002,name+": "+key+" centers across the full report/copy width");
        }
        if(name=="ClassSectionSummary")foreach(string key in new[]{"School","Title","Scope","Note","Prepared"})Center(key,"School");
        else if(name=="ClassOutstandingCharges")foreach(string key in new[]{"School","ReportTitle","Placement","Context"})Center(key,"School");
        else if(name=="ClassMonthlyAttendanceSheet")foreach(string key in new[]{"SchoolHeading","RegisterHeading","ClassHeading"})Center(key,"SchoolHeading");
        else foreach(string copy in new[]{"Copy1","Copy2"})foreach(string key in new[]{"School","Title","Campus","Class","Exam","Subject"})Center(copy+key,copy+"School");
    }
    private static void CheckClassReportHeadingPdf(string name,XDocument definition,byte[] bytes)
    {
        XNamespace ns=definition.Root!.Name.Namespace;
        using var pdf=UglyToad.PdfPig.PdfDocument.Open(bytes);
        Check(pdf.NumberOfPages==1,name+": representative headers and table fit a complete page; pages="+pdf.NumberOfPages+", text lengths="+string.Join(",",pdf.GetPages().Select(page=>page.Text.Length)));
        var words=pdf.GetPage(1).GetWords().ToArray();
        string[] schools=name is "ClassMarksSheet" or "ClassAttendanceSheet"?new[]{"Copy1School","Copy2School"}:new[]{name=="ClassMonthlyAttendanceSheet"?"SchoolHeading":"School"};
        double margin=ReportInches(definition.Root!.Element(ns+"Page")!.Element(ns+"LeftMargin")?.Value)*72;
        foreach(string key in schools)
        {
            var box=definition.Descendants(ns+"Textbox").Single(element=>(string?)element.Attribute("Name")==key);
            double left=margin+ReportInches(box.Element(ns+"Left")?.Value)*72,width=ReportInches(box.Element(ns+"Width")?.Value)*72;
            var first=words.Single(word=>word.Text=="Fictional"&&word.BoundingBox.Left>=left-1&&word.BoundingBox.Right<=left+width+1);
            var line=words.Where(word=>Math.Abs(word.BoundingBox.Bottom-first.BoundingBox.Bottom)<2 && word.BoundingBox.Left>=left-1 && word.BoundingBox.Right<=left+width+1).ToArray();
            double actual=(line.Min(word=>word.BoundingBox.Left)+line.Max(word=>word.BoundingBox.Right))/2;
            Check(Math.Abs(actual-(left+width/2))<2,name+": rendered school heading is centered on its report/copy");
        }
    }
}
