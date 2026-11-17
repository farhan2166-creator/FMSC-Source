using System.Data;
using System.Xml.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using FMSC_SMS.Core;
using Microsoft.Reporting.WinForms;

internal static partial class Program
{
    private static void CheckGridPdfReports()
    {
        var oversizedIdentity=new[]{new GridReportColumns.Column("Student",new[]{0},false,12,1)};
        Check(GridReportColumns.Bands(oversizedIdentity,10).Single().SequenceEqual(new[]{0}),"An oversized identity column remains present in the report");
        var reordered=new[]{new GridReportColumns.Column("Balance",new[]{0},true,4,1),new GridReportColumns.Column("Amount",new[]{1},true,4,1),
            new GridReportColumns.Column("Admission Number",new[]{2},false,1.4,1),new GridReportColumns.Column("Student",new[]{3},false,1.5,1)};
        Check(GridReportColumns.Bands(reordered,7).All(band=>band.Contains(2)&&band.Contains(3)),
            "Every wide-report section retains selected identities even when the operator moves money columns first");
        string folder=Path.GetFullPath("artifacts/grid-pdf");Directory.CreateDirectory(folder);
        XNamespace ns="http://schemas.microsoft.com/sqlserver/reporting/2008/01/reportdefinition";
        int Render(string name,string title,string subtitle,DataTable source)
        {
            string before=source.GetXmlForTest();
            if(source.Columns.Contains("Receipt / Reference"))
            {
                using var receiptRows=CollectionReceiptGrouping.DetailsOnly(source);
                foreach(DataRow row in receiptRows.Rows)
                    Check(Regex.IsMatch(Convert.ToString(row["Receipt / Reference"])??"",@"^(?:R|FR)-[0-9]{4}-[0-9]{5,}$|^(?:Credit|Payment) #[0-9]+$"),
                        "Review receipt references use the production R/FR format or a labelled unissued entry");
            }
            var built=GridReportPreview.Build("Fictional Meadow School & College",title,subtitle,source,"Amina Javed");
            var layout=CollectionReceiptGrouping.Layout(source);
            Check(source.GetXmlForTest()==before,"Printing must not modify source rows, values or types");
            var columns=GridReportColumns.Create(source,"#,##0.##");
            Check(columns.SelectMany(c=>c.Fields).OrderBy(i=>i).SequenceEqual(Enumerable.Range(0,source.Columns.Count)),"Every selected source field appears exactly once in the print projection");
            if(layout is not null)columns.Insert(0,new GridReportColumns.Column("#",Array.Empty<int>(),false,.3,.2));
            Check(built.Data.Rows.Count==(layout?.Count(row=>row.Kind=="Detail")??source.Rows.Count),"Print layout preserves all detail records without counting group headings or totals as receipts");
            int printedRow=0;
            for(int row=0;row<source.Rows.Count;row++)
            {
                if(layout is not null && layout[row].Kind!="Detail")continue;
                foreach(var printColumn in columns)
                {
                    // Test the actual compact representation, including identities,
                    // receipts and signed deductions, not only the unformatted fields.
                    string value=GridReportColumns.DisplayValue(source.Rows[row],printColumn,"#,##0.##");
                    if(value.Length>0)
                        Check(built.Data.Rows[printedRow].ItemArray.Any(cell=>
                            (Convert.ToString(cell)??"").Contains(value,StringComparison.Ordinal)),
                            "Printed data preserves its intended compact representation");
                }
                printedRow++;
            }
            var definition=XDocument.Parse(built.Definition);
            var boxes=definition.Descendants(ns+"Textbox").ToArray();
            string Align(XElement box)=>box.Descendants(ns+"TextAlign").First().Value;
            foreach(string item in new[]{"Institution","ReportTitle","Filters"})
                Check(Align(boxes.Single(box=>(string?)box.Attribute("Name")==item))=="Center","School, title and filter context are centered");
            Check(definition.Descendants(ns+"Color").All(color=>color.Value=="#000000"),"Direct exports use black text and rules only");
            Check(!definition.Descendants(ns+"BackgroundColor").Any(),"Direct exports have no colored fills or alternating shading");
            foreach(var box in boxes)
            {
                string boxName=(string)box.Attribute("Name")!;
                if(boxName.StartsWith("ReceiptHeading"))Check(Align(box)=="Left","Receipt group labels read from the start of the table");
                if(boxName.StartsWith("Heading") || boxName.StartsWith("MetricLabel") || boxName.StartsWith("MetricValue"))
                    Check(Align(box)=="Center","Table/group headings and summary labels/figures are centered");
                if(System.Text.RegularExpressions.Regex.Match(boxName,@"^Value\d+_(\d+)$") is {Success:true} match)
                {
                    var column=columns[int.Parse(match.Groups[1].Value)];
                    if(column.Numeric || column.Caption.StartsWith("Receipt") || column.Caption.StartsWith("Adm.") || column.Caption.StartsWith("Oldest Due"))
                        Check(Align(box)=="Center","Amounts, counts, references and due dates have centered data");
                    if(column.Caption.StartsWith("Student"))Check(Align(box)=="Left","Student names remain readable and left aligned");
                }
                if(boxName.StartsWith("ReceiptTotal") && box.Descendants(ns+"Value").First().Value.StartsWith("=Format(Sum("))
                    Check(Align(box)=="Center","Receipt subtotals and grand totals center monetary values like their detail columns");
            }
            Check(definition.Descendants(ns+"FontSize").Any(element=>element.Value=="9pt"),"Table text remains readable");
            Check(definition.Descendants(ns+"PageHeader").Any() && definition.Descendants(ns+"PageFooter").Any(),
                "Every report document repeats its identity header and page footer");
            Check(definition.Descendants(ns+"Value").Any(v=>v.Value.Contains("Globals!PageNumber")),"PDF includes Page X of Y footer");
            Check(built.Data.Columns.Contains("IsReportTotal"),"Total emphasis is retained independently of the first visible field");
            foreach(var table in definition.Descendants(ns+"Tablix"))
            {
                double width=table.Element(ns+"TablixBody")!.Element(ns+"TablixColumns")!.Elements().Sum(c=>double.Parse(c.Element(ns+"Width")!.Value.Replace("in",""),System.Globalization.CultureInfo.InvariantCulture));
                double body=double.Parse(definition.Root!.Element(ns+"Width")!.Value.Replace("in",""),System.Globalization.CultureInfo.InvariantCulture);
                Check(width<=body+.001,"Printed columns stay inside the printable page");
                Check(table.Descendants(ns+"RepeatOnNewPage").Any(e=>e.Value=="true"),"Every section repeats its table headings");
            }
            using var report=new LocalReport();report.LoadReportDefinition(new StringReader(built.Definition));report.DataSources.Add(new ReportDataSource("ReportData",built.Data));
            byte[] pdf=report.Render("PDF");File.WriteAllBytes(Path.Combine(folder,name+".pdf"),pdf);
            if(source.Columns.Contains("Receipt / Reference"))
            {
                var search=ReportPageSearchIndex.FromPdf(pdf);
                Check(search.FindPages("FMSC-").Length==0,"Review PDFs do not substitute FMSC references for R/FR receipts");
                Check(!columns.Any(column=>column.Caption.Contains("Campus",StringComparison.OrdinalIgnoreCase)),
                    "Receipt PDF detail columns do not repeat the campus supplied by scope and group headings");
            }
            File.WriteAllText(Path.Combine(folder,name+".rdlc"),built.Definition);
            using var parsed=PdfSharp.Pdf.IO.PdfReader.Open(new MemoryStream(pdf),PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
            Check(parsed.PageCount>0&&pdf.Length>1000,"The shared RDLC generates a valid PDF");
            string exported=Path.Combine(folder,name+"-export.pdf");
            GridReportPreview.ExportPdf(exported,built);
            using var exportedPdf=PdfSharp.Pdf.IO.PdfReader.Open(exported,PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
            Check(exportedPdf.PageCount==parsed.PageCount,"Saved PDF uses the exact preview document and pagination");
            Check(source.GetXmlForTest()==before,"PDF export does not mutate source transactions");
            built.Data.Dispose();return parsed.PageCount;
        }
        using(var empty=new DataTable())
        {
            empty.Columns.Add("Student");empty.Columns.Add("Outstanding",typeof(decimal));
            Check(Render("empty-report","Outstanding & Aging — Students","View: Students   Campus: All",empty)==1,"Empty report is a readable one-page document");
        }
        using var receipts=new DataTable();
        foreach(string name in new[]{"Receipt / Reference","Date","Scope","Student / Family","Campus","Class / Section","Payment Method","Money Received","Refunds","Corrections","Credit Applied","Adjusted Collection","Received By"})
            receipts.Columns.Add(name,name=="Date"?typeof(DateTime):new[]{"Money Received","Refunds","Corrections","Credit Applied","Adjusted Collection"}.Contains(name)?typeof(decimal):typeof(string));
        for(int index=0;index<10;index++)
        {
            decimal money=index switch{0=>2800m,1=>1600m,2 or 3=>200m,7=>10000m,_=>0m};
            decimal refund=index==5?1200m:0m,correction=index==4?1300m:index==6?150m:0m,credit=index==8?2250m:index==9?8700m:0m;
            receipts.Rows.Add(index==8?"Credit #19":$"{(index%3==0?"FR":"R")}-2026-{index+9:D5}",new DateTime(2026,10,index==9?19:18),index%3==0?"Family":"Individual",
                index%3==0?"Hamid, Kausar":"Hamid","Campus Boys","Play Group – F",
                credit>0?"Account Credit":refund>0?"Refund":correction>0?"Correction":"Cash",money,refund,correction,credit,money-refund-correction,"Amina Javed");
        }
        string totals="   Receipts: 9     Money Received: Rs. 14,800     Refunds: Rs. 1,200     Corrections: Rs. 1,450     Credit Applied: Rs. 10,950     Adjusted Collection: Rs. 12,150";
        receipts.Columns.Add("__ReceiptStudents");receipts.Columns.Add("__ReceiptSnapshot");
        void DescribeReceipts(DataTable data)
        {
            foreach(DataRow row in data.Rows)
            {
                var children=Enumerable.Range(0,Equals(row["Scope"],"Family")?2:1).Select(index=>new {
                    AdmissionId=101+index,StudentName=index==0?"Hamid":"Kausar",FatherName="Ali",Gender=index==0?"M":"F",
                    Placements=new[]{new{CampusName=Convert.ToString(row["Campus"]),ClassName="Play Group",SectionName="F"}}
                }).ToArray();
                row["__ReceiptStudents"]=JsonSerializer.Serialize(children);
                row["__ReceiptSnapshot"]=JsonSerializer.Serialize(new {
                    Students=children.Select(child=>new{child.AdmissionId,child.StudentName,child.FatherName,child.Gender}),
                    Lines=children.Select(child=>new{child.AdmissionId,child.Placements[0].CampusName,child.Placements[0].ClassName,child.Placements[0].SectionName})
                });
            }
        }
        using(var wholeSchool=receipts.Copy())
        {
            DescribeReceipts(wholeSchool);
            var review=ReceiptExportReview(wholeSchool,"Whole School");using var projected=review.Rows;
            Check(!projected.Columns.Contains("Campus")&&!projected.Columns.Cast<DataColumn>().Any(column=>column.ColumnName.StartsWith("__")),
                "Receipt review cannot reintroduce a campus detail column or private metadata omitted by the application");
            int pages=Render("collections-receipts",review.Title,review.Scope+totals,projected);
            Check(pages==1,$"Ten payment/credit entries and their whole-school total fit one readable page; actual pages: {pages}");
        }
        using(var byCampus=receipts.Copy())
        {
            for(int row=5;row<byCampus.Rows.Count;row++)byCampus.Rows[row]["Campus"]="Campus Girls";
            DescribeReceipts(byCampus);
            var review=ReceiptExportReview(byCampus,"Campus");using var projected=review.Rows;
            var groupedLayout=CollectionReceiptGrouping.Layout(projected)!;
            Check(groupedLayout.Count(row=>row.Kind=="Heading")==2 && groupedLayout.Count(row=>row.Kind=="Subtotal")==2,
                "The campus sample includes two real receipt groups and their subtotals");
            Render("collections-by-campus",review.Title,review.Scope+totals,projected);
        }
        using var selectedReceipts=ReceiptExportReview(receipts,"Whole School").Rows;
        var receiptColumns=GridReportColumns.Create(selectedReceipts,"#,##0.##");
        Check(receiptColumns.Count==8,"Receipt identity and class share a cell instead of squeezing thirteen columns");
        Check(receiptColumns.Single(c=>c.Caption=="Receipt / Date").Minimum>=1.3,"Receipt references and dates have a readable minimum width");
        using var outstanding=new DataTable();
        foreach(string name in new[]{"Adm. / QR No.","Student","Campus","Class / Section","Open Charges (Net)","Settled on Open Charges","Outstanding","Oldest Due Date","Aging"})
            outstanding.Columns.Add(name,name=="Oldest Due Date"?typeof(DateTime):new[]{"Open Charges (Net)","Settled on Open Charges","Outstanding"}.Contains(name)?typeof(decimal):typeof(string));
        outstanding.Rows.Add("101","Hamza Ali","Campus Boys","Play Group – M",4650m,1200m,3450m,new DateTime(2026,11,5),"Due Today");
        outstanding.Rows.Add("QR-102","Ayesha Khan","Campus Girls","Play Group – F",5600m,0m,5600m,new DateTime(2026,11,5),"Due Today");
        outstanding.Rows.Add("103","Maryam Iqbal","Campus Girls","Class 10 – A",3500m,2150m,1350m,new DateTime(2027,1,1),"Not Yet Due");
        Check(Render("outstanding-aging","Outstanding & Aging — Students","View: Students   Campus: All   Aging date: 05-11-2026   Students Owing: 3     Total Outstanding: Rs. 10,400     Overdue: Rs. 0     90+ Days: Rs. 0",outstanding)==1,"Outstanding report fits one clear page");
        using(var summaryRows=new DataTable())
        {
            summaryRows.Columns.Add("Charge Date");summaryRows.Columns.Add("Gross Billed",typeof(decimal));
            summaryRows.Rows.Add("01-11-2026",2875.50m);summaryRows.Rows.Add("TOTAL",2875.50m);
            var values=new[]{new ReportPrintMetric("Overdue Amount","Rs. 200"),new ReportPrintMetric("Due Today","Rs. 100"),
                new ReportPrintMetric("Not Yet Due","Rs. 700"),new ReportPrintMetric("Due Date Missing","Rs. 0")};
            var report=GridReportPreview.Build("Fictional Meadow School","Debt summary","Campus: All",summaryRows,"Fixture",summaryMetrics:values);
            var xml=XDocument.Parse(report.Definition);
            Check(xml.Descendants(ns+"Textbox").Count(box=>((string?)box.Attribute("Name"))?.StartsWith("MetricLabel")==true)==4,
                "Every structured due-status amount becomes a readable summary card");
            foreach(var label in xml.Descendants(ns+"Textbox").Where(box=>((string?)box.Attribute("Name"))?.StartsWith("MetricLabel")==true))
            {
                string index=((string)label.Attribute("Name")!)["MetricLabel".Length..];
                var value=xml.Descendants(ns+"Textbox").Single(box=>(string?)box.Attribute("Name")=="MetricValue"+index);
                double Inches(XElement box,string property)=>double.Parse(box.Element(ns+property)!.Value.Replace("in",""),System.Globalization.CultureInfo.InvariantCulture);
                Check(Inches(label,"Top")+Inches(label,"Height")<=Inches(value,"Top")+.0001,"Summary labels reserve their own height above the values");
                Check(Inches(value,"Top")+Inches(value,"Height")<=double.Parse(xml.Descendants(ns+"PageHeader").Single().Element(ns+"Height")!.Value.Replace("in",""),System.Globalization.CultureInfo.InvariantCulture),
                    "Summary figures fit inside the reserved header");
            }
            Check(!xml.Descendants(ns+"Value").Any(v=>v.Value.Contains("Data rows:")),"Printed reports omit technical data-row counts");
            Check(report.Data.Rows[1].Field<bool>("IsReportTotal") && report.Data.Rows[0].Field<string>("C1")==2875.5m.ToString("#,##0.##"),
                "Totals are emphasized and decimal money retains its readable precision");
            report.Data.Dispose();
        }
        using var wide=new DataTable();wide.Columns.Add("Admission Number");wide.Columns.Add("Student");
        for(int column=0;column<16;column++)wide.Columns.Add("Detailed Amount "+column,typeof(decimal));
        for(int row=0;row<36;row++)wide.Rows.Add(new object[]{(row+1).ToString(),SampleStudentName(row)}.Concat(Enumerable.Range(0,16).Select(column=>(object)(1750m+row*15m+column*125m))).ToArray());
        var plan=GridReportColumns.Create(wide,"#,##0.##");
        Check(GridReportColumns.Bands(plan,10.67).Count>1,"Extra-wide reports split into identified sections instead of shrinking to tiny text");
        Check(Render("wide-multipage","Wide layout check","Synthetic layout stress case   Records: 36",wide)>1,"Wide reports paginate without overflowing the sheet");
    }
}

internal static class GridPdfFixtureXml
{
    internal static string GetXmlForTest(this DataTable table)
    {using var copy=table.Copy();copy.TableName="Fixture";using var text=new StringWriter();copy.WriteXml(text,XmlWriteMode.WriteSchema);return text.ToString();}
}
