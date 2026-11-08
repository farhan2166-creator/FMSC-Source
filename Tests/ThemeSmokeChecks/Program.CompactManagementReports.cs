using System.Data;
using System.Text.Json;
using FMSC_SMS.Core;

internal static partial class Program
{
    private static int RenderLayoutScenario(string name,GridReportPreview.Report built,Action<ReportPageSearchIndex,int>? verify=null)
    {
        using(built.Data)
        {
            string folder=Path.GetFullPath("artifacts/compact-management-reports");Directory.CreateDirectory(folder);
            string path=Path.Combine(folder,name+".pdf");GridReportPreview.ExportPdf(path,built);
            using var pdf=UglyToad.PdfPig.PdfDocument.Open(path);
            var search=ReportPageSearchIndex.FromPdf(File.ReadAllBytes(path));
            Check(pdf.NumberOfPages>0,"Scenario renders selectable PDF: "+name);
            foreach(var page in pdf.GetPages())
            {
                Check(page.Text.Contains("Fictional Meadow"),"Institution repeats on every page: "+name);
                Check(page.Letters.All(letter=>letter.BoundingBox.Left>=0&&letter.BoundingBox.Right<=page.Width+.5
                    &&letter.BoundingBox.Bottom>=0&&letter.BoundingBox.Top<=page.Height+.5),"Text stays on the paper: "+name);
            }
            verify?.Invoke(search,pdf.NumberOfPages);
            Console.WriteLine($"Layout: {name}: {pdf.NumberOfPages} pages");
            return pdf.NumberOfPages;
        }
    }

    private static void CheckCompactReceiptScenarios()
    {
        using var source=new DataTable();
        foreach(string field in new[]{"Receipt / Reference","Scope","Student / Family","Campus","Class / Section","Payment Method","Received By","__ReceiptStudents","__ReceiptSnapshot"})source.Columns.Add(field);
        source.Columns.Add("Date",typeof(DateTime));
        foreach(string field in new[]{"Money Received","Refunds","Corrections","Credit Applied","Adjusted Collection"})source.Columns.Add(field,typeof(decimal));
        void Add(int number,int children)
        {
            var row=source.NewRow();
            string campus=number%2==0?"Campus Boys":"Campus Girls";
            var students=Enumerable.Range(1,children).Select(i=>new {
                AdmissionId=number*100+i,StudentName=$"Child{number:D3}X{i:D2}"+(i%3==0?" With An Extended Student Name":""),
                FatherName=$"Parent{number:D3}",Gender="M",
                Placements=new[]{new{CampusName=campus,ClassName="Class "+((i+1)/2),SectionName="M"}}
            }).ToArray();
            row["Receipt / Reference"]=$"{(children>1?"FR":"R")}-2026-{number:D5}";
            row["Scope"]=children>1?"Family":"Individual";row["Student / Family"]="Pending";
            row["Campus"]=campus;row["Class / Section"]="Pending";row["Date"]=new DateTime(2026,11,2);
            row["Payment Method"]="Cash + Account Credit";row["Received By"]="Example Operator";
            row["Money Received"]=500m+number*25;row["Credit Applied"]=number%7==0?250m:0m;
            row["Refunds"]=0m;row["Corrections"]=0m;row["Adjusted Collection"]=row["Money Received"];
            row["__ReceiptStudents"]=JsonSerializer.Serialize(students);
            row["__ReceiptSnapshot"]=JsonSerializer.Serialize(new {
                Students=students.Select(s=>new{s.AdmissionId,s.StudentName,s.FatherName,s.Gender}),
                Lines=students.Select(s=>new{s.AdmissionId,s.Placements[0].CampusName,s.Placements[0].ClassName,s.Placements[0].SectionName})
            });source.Rows.Add(row);
        }
        void Render(string name,string grouping)
        {
            var review=ReceiptExportReview(source,grouping);using var exported=review.Rows;
            var built=GridReportPreview.Build("Fictional Meadow School & College",review.Title,review.Scope,exported,"Example Operator");
            Check(built.Data.Rows.Count==source.Rows.Count,"Every receipt is printed once in "+name);
            Check(built.Data.AsEnumerable().Sum(row=>row.Field<decimal>("N4"))==source.AsEnumerable().Sum(row=>row.Field<decimal>("Money Received")),"Receipt amounts are not multiplied by child count");
            RenderLayoutScenario(name,built,(search,pages)=>{
                Check(search.FindPages("Grand Total").SequenceEqual(new[]{pages}),"Grand total occurs only on the last page");
                foreach(DataRow row in source.Rows)
                {
                    string reference=(string)row["Receipt / Reference"];
                    int[] receiptPages=search.FindPages(reference);
                    Check(receiptPages.Length==1,"Receipt reference occurs once: "+reference);
                    using var roster=JsonDocument.Parse((string)row["__ReceiptStudents"]);
                    foreach(var student in roster.RootElement.EnumerateArray())
                    {
                        string token=student.GetProperty("StudentName").GetString()!.Split(' ')[0];
                        int[] childPages=search.FindPages(token);
                        Check(childPages.SequenceEqual(receiptPages),$"Complete receipt stays on one page: {reference}, {token}: child pages {string.Join(',',childPages)}, receipt pages {string.Join(',',receiptPages)}");
                    }
                }
                if(grouping=="Campus")Check(search.FindPages("Subtotal — Campus:").Length>0,"Named campus subtotals survive pagination");
            });
        }
        Add(1,3);Render("receipt-three-children","Whole School");source.Rows.Clear();
        Add(1,10);Render("receipt-ten-children","Whole School");source.Rows.Clear();
        for(int i=1;i<=60;i++)Add(i,i%11==0?10:i%3==0?3:1);
        Render("receipts-sixty","Whole School");Render("receipts-sixty-campus","Campus");
    }

    private static void CheckCompactFinancialScenarios()
    {
        using var outstanding=new DataTable();
        outstanding.Columns.Add("Adm. / QR No.");outstanding.Columns.Add("Student");outstanding.Columns.Add("Campus");outstanding.Columns.Add("Class / Section");
        foreach(string field in new[]{"Open Charges (Net)","Settled on Open Charges","Outstanding"})outstanding.Columns.Add(field,typeof(decimal));
        outstanding.Columns.Add("Oldest Due Date",typeof(DateTime));outstanding.Columns.Add("Aging");
        for(int i=1;i<=75;i++)outstanding.Rows.Add(i.ToString(),$"LongStudent{i:D3} With An Extended Family Name",i%2==0?"Campus Boys":"Campus Girls","Class 10 – M",7500m+i,2000m,5500m+i,new DateTime(2026,9,1),"Overdue");
        using var billing=new DataTable();billing.Columns.Add("Adm. / QR No.");billing.Columns.Add("Student");billing.Columns.Add("Fee Type");billing.Columns.Add("Charge Date",typeof(DateTime));
        foreach(string field in new[]{"Gross Billed","Discounts","Net Billed"})billing.Columns.Add(field,typeof(decimal));
        for(int i=1;i<=75;i++)
        {
            // Production queries return deduction magnitudes; reversals are negative.
            decimal discount=i%17==0?-75m:i%5==0?500m:0m;
            billing.Rows.Add(i.ToString(),$"BillingStudent{i:D3}",i%2==0?"Monthly Tuition":"Transport",new DateTime(2026,10,1).AddDays(i%28),5000m,discount,5000m-discount);
        }
        using var monthly=new DataTable();monthly.Columns.Add("Campus");monthly.Columns.Add("Billing Month",typeof(DateTime));
        foreach(string field in new[]{"Net Fees","Money Allocated","Credit Applied","Outstanding"})monthly.Columns.Add(field,typeof(decimal));
        for(int i=0;i<48;i++)monthly.Rows.Add(i%2==0?"Campus Boys":"Campus Girls",new DateTime(2023,1,1).AddMonths(i),100000m+i*1000,60000m+i*500,10000m,30000m+i*500);
        void Render(string name,string title,DataTable source,string? identity)
        {
            string before=source.GetXmlForTest();
            var built=GridReportPreview.Build("Fictional Meadow School & College",title,"Campus: All   Fictional readability review",source,"Example Operator");
            Check(before==source.GetXmlForTest()&&built.Data.Rows.Count==source.Rows.Count,"Print polish preserves source rows, money and dates: "+name);
            var columns=GridReportColumns.Create(source,"#,##0.##");
            bool landscape=columns.Count>6||columns.Sum(column=>column.Minimum)>7.25;
            ManagementReportPrintLayout.Apply(columns,landscape?10.67:7.25,title);
            for(int index=0;index<columns.Count;index++)if(columns[index].Numeric)
                Check(source.AsEnumerable().Sum(row=>ReportMoneyPresentation.Amount(source.Columns[columns[index].Fields[0]],Convert.ToDecimal(row[columns[index].Fields[0]])))
                    ==built.Data.AsEnumerable().Sum(row=>decimal.Parse(row.Field<string>("C"+index)!,System.Globalization.NumberStyles.Number)),"All signed financial values survive the print projection: "+name);
            if(name=="monthly-reconciliation")
                Check(columns.Any(column=>column.Caption=="Billing Month") &&
                      columns.Any(column=>column.Caption=="Campus"),
                    "Monthly reconciliation retains period and campus context without prescribing column order");
            RenderLayoutScenario(name,built,(search,pages)=>{
                if(identity is not null)for(int i=1;i<=75;i++)Check(search.FindPages(identity+i.ToString("D3")).Length==1,"Each student appears once: "+name);
                else foreach(DataRow row in monthly.Rows)Check(search.FindPages(((DateTime)row["Billing Month"]).ToString("MMM yyyy")).Length==1,"Each reconciliation period survives pagination");
                if(name=="billing-discounts")Check(search.FindPages("-500").Length>0,"Signed discounts remain visible");
            });
        }
        Render("outstanding-long-names","Outstanding & Aging — Students",outstanding,"LongStudent");
        Render("billing-discounts","Billing & Discounts — Billing Detail",billing,"BillingStudent");
        Render("monthly-reconciliation","Monthly Fee Reconciliation — Monthly Summary",monthly,null);
    }
}
