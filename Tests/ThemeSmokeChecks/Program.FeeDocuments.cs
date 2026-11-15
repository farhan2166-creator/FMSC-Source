using System.Data;
using FMSC_SMS.Core;
using Microsoft.Reporting.WinForms;
using System.Xml.Linq;

internal static partial class Program
{
    private static void CheckFeeDocuments(List<FeeDocumentReviewSample>? review=null)
    {
        CheckFeeMessageImages();
        foreach(string retired in new[]{"AdmissionBill.rdlc","FeePaymentReceipt.rdlc","FeeAgingReport.rdlc"})
            Check(!File.Exists(Path.Combine(AppContext.BaseDirectory,"Reports",retired)),"Retired templates are absent from the built application");
        foreach(var (template,dataSet) in new[]{(StudentBillReport.TemplateFile,StudentBillReport.DataSetName),
            (FamilyBillReport.TemplateFile,FamilyBillReport.DataSetName),(StudentReceiptReport.TemplateFile,StudentReceiptReport.DataSetName),
            (FamilyReceiptReport.TemplateFile,FamilyReceiptReport.DataSetName)})
        {
            var definition=XDocument.Load(Path.Combine(AppContext.BaseDirectory,"Reports",template));
            XNamespace ns=definition.Root!.Name.Namespace;
            Check(definition.Descendants(ns+"DataSet").Single().Attribute("Name")!.Value==dataSet
                && dataSet==Path.GetFileNameWithoutExtension(template)+"Data","Each C# report binds the matching RDLC dataset");
        }
        var school=new InstitutionProfile.PrintIdentity("Fahim Model School & College","0300-1234567","Sarband Peshawar");
        string folder=Path.GetFullPath("artifacts/fee-documents");Directory.CreateDirectory(folder);
        int Render(string name,DataTable data,string? templateName=null,bool annual=false)
        {
            bool bill=Convert.ToString(data.Rows[0]["DocumentKind"])=="Bill";
            // Match StudentReceiptReport.Show: group months before sizing or rendering.
            using var individualRows=!bill&&!data.Columns.Contains("FamilyRowKind")?FeeReceiptPresentation.Prepare(data):null;
            if(individualRows is not null)
            {
                FeeDocumentData.GroupPeriods(individualRows);
                data=individualRows;
                Check(data.AsEnumerable().Where(row=>!string.IsNullOrWhiteSpace(row.Field<string>("FeePeriod")))
                    .GroupBy(row=>(row.Field<int>("ReceiptPageKey"),row.Field<string>("FeePeriod"))).All(group=>group.Count()==1),
                    "Individual receipt samples print each month once per receipt, as the application preview does");
            }
            using var familyRows=!bill&&data.Columns.Contains("FamilyRowKind")?FamilyReceiptReport.PrepareData(data):null;
            if(familyRows is not null)data=familyRows;
            using var billRows=bill&&data.Columns.Contains("FamilyRowKind")?FamilyBillReport.PrepareData(data):null;
            if(billRows is not null)data=billRows;
            using var billDisplay=bill?(data.Columns.Contains("FamilyRowKind")?FamilyFeeDocumentPresentation.Bill(data):FeeBillPresentation.Prepare(data)):null;
            if(billDisplay is not null)data=billDisplay;
            using var receiptDisplay=!bill&&data.Columns.Contains("FamilyRowKind")?FamilyFeeDocumentPresentation.Receipt(data):null;
            if(receiptDisplay is not null)data=receiptDisplay;
            if(bill) foreach(DataRow row in data.Rows)
                if(string.IsNullOrWhiteSpace(Convert.ToString(row["ReceiptNumber"])))row["ReceiptNumber"]=data.Columns.Contains("FamilyRowKind")?"FB-2026-00001":"B-2026-00001";
            foreach(string number in data.AsEnumerable().Select(row=>Convert.ToString(row["ReceiptNumber"])??"").Distinct())
                Check(System.Text.RegularExpressions.Regex.IsMatch(number,bill?@"^(?:B|FB)-[0-9]{4}-[0-9]{5,}$":@"^(?:R|FR)-[0-9]{4}-[0-9]{5,}$"),
                    "Review documents retain production bill/receipt prefixes and five-digit minimum serials");
            string file=data.Columns.Contains("FamilyRowKind")?(bill?"FamilyBill.rdlc":"FamilyReceipt.rdlc"):(bill?"StudentBill.rdlc":"StudentReceipt.rdlc");
            file=templateName??file;
            string path=Path.Combine(AppContext.BaseDirectory,"Reports",file);
            Check(File.Exists(path),"Each document loads its own RDLC file");
            string definition=annual?FeeDocumentLayout.SizeToContent(path,data,annualSheet:true):FeeDocumentLayout.SizeToContent(path,data);
            var xml=XDocument.Parse(definition);XNamespace ns=xml.Root!.Name.Namespace;
            var receiptTable=xml.Descendants(ns+"Textbox").Single(box=>(string?)box.Attribute("Name")=="FamilyBatch")
                .Ancestors(ns+"Tablix").First();
            var printedRows=receiptTable.Element(ns+"TablixBody")!.Element(ns+"TablixRows")!.Elements(ns+"TablixRow").ToArray();
            var printedMembers=receiptTable.Element(ns+"TablixRowHierarchy")!.Descendants(ns+"TablixMember")
                .Where(member=>member.Element(ns+"TablixMembers") is null).ToArray();
            int familyDetail=Array.FindIndex(printedRows,row=>row.Descendants(ns+"Textbox")
                .Any(box=>(string?)box.Attribute("Name")=="FamilyBatch"));
            Check(printedMembers[familyDetail].Element(ns+"Visibility")?.Element(ns+"Hidden")?.Value=="true",
                "Bills and receipts suppress the redundant family student-count detail, including older snapshots");
            if(!bill)
            {
                string Caption(string name)=>xml.Descendants(ns+"Textbox").Single(box=>(string?)box.Attribute("Name")==name)
                    .Descendants(ns+"Value").First().Value;
                Check(new[]{"TotalLabelCashReceived","BalanceLabel","TotalLabel"}
                    .All(name=>!Caption(name).StartsWith('=')),
                    "Receipt captions are editable plain text, separate from monetary formulas");
            }
            var address = xml.Descendants(ns+"Textbox").Single(box=>(string?)box.Attribute("Name")=="SchoolAddress");
            Check(address.Descendants(ns+"Value").First().Value.Contains("Fields!CampusName.Value")
                && !xml.Descendants(ns+"Textbox").Any(box=>(string?)box.Attribute("Name")=="CampusHeading"),
                "School address and campus share one header line");
            Check(xml.Descendants(ns+"Textbox").Single(box=>(string?)box.Attribute("Name")=="ReceiptHeading")
                .Descendants(ns+"FontSize").All(size=>size.Value=="6.25pt"),"The complete document title uses the smaller font");
            Check(xml.Descendants(ns+"TablixMember").Where(member=>member.Element(ns+"Group")?.Attribute("Name")?.Value=="ReceiptPageGroup").Count()==1,"One document group is retained");
            // The operator's finalized individual receipt is the visual reference:
            // shaded header with a top rule, and borderless fee rows.
            foreach(string cell in new[]{"FeeHeader","AmountHeader"})
                Check(xml.Descendants(ns+"Textbox").Single(box=>(string?)box.Attribute("Name")==cell)
                    .Element(ns+"Style")?.Element(ns+"TopBorder")?.Element(ns+"Style")?.Value=="Solid","Fee tables retain the finalized receipt header rule");
            Check(xml.Descendants(ns+"Textbox").Any(box=>(string?)box.Attribute("Name")=="PeriodHeader")
                && xml.Descendants(ns+"Textbox").Single(box=>(string?)box.Attribute("Name")=="AmountHeader")
                    .Descendants(ns+"Value").First().Value=="Rs.","Every bill and receipt uses the approved Month / Fee / Rs. table");
            foreach(string cell in new[]{"FeeDescription","LineAmount"})
                Check(!xml.Descendants(ns+"Textbox").Single(box=>(string?)box.Attribute("Name")==cell)
                    .Element(ns+"Style")!.Elements().Where(element=>element.Name.LocalName.EndsWith("Border"))
                    .Any(border=>border.Element(ns+"Style")?.Value=="Solid"),"Fee detail rows remain borderless");
            if(bill && file=="FamilyBill.rdlc")
            {
                Check(xml.Descendants(ns+"Group").Any(group=>(string?)group.Attribute("Name")=="BillStudentGroup"),"Family bills group real fee rows under separate student headings");
            }
            if(file is "FamilyBill.rdlc" or "FamilyReceipt.rdlc")
            {
                string childGroup=bill?"BillStudentGroup":"ReceiptStudentGroup";
                int schoolRow=Array.FindIndex(printedRows,row=>row.Descendants(ns+"Textbox").Any(box=>(string?)box.Attribute("Name")=="InstitutionName"));
                Check(!printedMembers[schoolRow].Ancestors(ns+"TablixMember").Any(member=>(string?)member.Element(ns+"Group")?.Attribute("Name")==childGroup),
                    "The school heading is outside the repeating child group");
                int titleRow=Array.FindIndex(printedRows,row=>row.Descendants(ns+"Textbox").Any(box=>(string?)box.Attribute("Name")=="ReceiptHeading"));
                Check(printedMembers[titleRow].Ancestors(ns+"TablixMember").Any(member=>(string?)member.Element(ns+"Group")?.Attribute("Name")==childGroup),
                    "Document titles, identities and calculations repeat inside the child group");
                var breaks=xml.Descendants(ns+"PageBreak").ToArray();
                Check(breaks.Length==(bill?1:0)&&breaks.All(b=>(string?)b.Parent?.Attribute("Name")=="ReceiptPageGroup"),
                    "Family bill page breaks separate whole documents, never siblings within a family");
                Check(xml.Descendants(ns+"Group").Single(group=>(string?)group.Attribute("Name")==childGroup)
                    .Parent!.Element(ns+"Visibility")!.Value.Contains("ShowChildDocument"),"Each complete child slip follows its own visibility rule");
                Check(xml.Descendants(ns+"Textbox").Single(box=>(string?)box.Attribute("Name")=="GroupSummaryHeading")
                    .Descendants(ns+"Value").First().Value=="GROUP SUMMARY","The family printout has one final group summary");
                foreach(var (labelName,caption) in new[]{("GroupFeeTotalLabel","Net total"),("GroupPayableLabel","Net payable"),("GroupDueLabel","Net due")})
                    Check(xml.Descendants(ns+"Textbox").Single(box=>(string?)box.Attribute("Name")==labelName).Descendants(ns+"Value").First().Value==caption,
                        "Family summary uses the agreed net captions");
            }
            using var report=new LocalReport();report.LoadReportDefinition(new StringReader(definition));
            using var printData=review is null?null:RecordTextPolicy.PrintCopy(data);
            report.DataSources.Add(new ReportDataSource(xml.Descendants(ns+"DataSet").Single().Attribute("Name")!.Value,printData??data));
            using var roll=review is not null && bill && !annual
                ? FeeBillRollDocument.Render(path,xml.Descendants(ns+"DataSet").Single().Attribute("Name")!.Value,data) : null;
            if (name == "family-bill-months")
            {
                using var imageRoll = FeeBillRollDocument.Render(path, FamilyBillReport.DataSetName, data);
                using var image = FeeMessageImage.Combine(imageRoll.Pages.Select(page => page.Image));
                Check(image.Width >= 500 && image.Height >= imageRoll.Pages.Count * 100,
                    "The exact fitted family bill pages also form a complete image for manual sharing");
            }
            byte[] pdf=roll?.ExportPdf()??report.Render("PDF");File.WriteAllBytes(Path.Combine(folder,name+".pdf"),pdf);
            using var parsed=PdfSharp.Pdf.IO.PdfReader.Open(new MemoryStream(pdf),PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
            if(annual && file==StudentReceiptReport.TemplateFile)
            {
                var search=ReportPageSearchIndex.FromPdf(pdf);
                string admission=Convert.ToString(data.Rows[0]["AdmissionNumberText"])!;
                Check(search.FindPages(admission).Length>0,"A4 receipt-sheet search finds the printed admission number");
                if(name=="bulk-annual-search")
                    Check(parsed.PageCount>1&&search.FindPages("FR-2026-00114").Contains(parsed.PageCount),
                        "The last receipt in a multi-column A4 batch maps to its actual final printed page");
            }
            if(name=="family-six-child-receipt")
            {
                var printed=new List<MemoryStream>();
                try
                {
                    report.Render("IMAGE","<DeviceInfo><OutputFormat>EMF</OutputFormat><StartPage>0</StartPage><EndPage>0</EndPage></DeviceInfo>",
                        (streamName,extension,encoding,mimeType,willSeek)=>{var stream=new MemoryStream();printed.Add(stream);return stream;},out _);
                    Check(printed.Count==1,"The printer renderer keeps all six children and final totals on one slip");
                }
                finally{foreach(var stream in printed)stream.Dispose();}
            }
            if(name is "batch-outstanding-months" or "family-bill-months")
            {
                string csv=System.Text.Encoding.UTF8.GetString(report.Render("CSV"));
                File.WriteAllText(Path.Combine(folder,name+".csv"),csv);
                Check(System.Text.RegularExpressions.Regex.Matches(csv,"Oct-2026").Count==2
                    &&System.Text.RegularExpressions.Regex.Matches(csv,"Nov-2026").Count==2,
                    "Every student prints both October and November, even when the preceding bill or sibling has the same months");
            }
            byte[] png=roll is null?report.Render("IMAGE","<DeviceInfo><OutputFormat>PNG</OutputFormat><DpiX>130</DpiX><DpiY>130</DpiY></DeviceInfo>")
                : FeeReviewFirstPage(roll.Pages[0]);
            File.WriteAllBytes(Path.Combine(folder,name+".png"),png);
            Check(pdf.Length>1000 && png.Length>1000,"Established RDLC renders a real PDF and PNG");
            review?.Add(new FeeDocumentReviewSample(name,bill?"Bill":"Receipt",annual?"A4 sheet":roll is null?"Receipt slip":"Fitted thermal bill",
                parsed.PageCount,Path.GetFileName(file)));
            return parsed.PageCount;
        }
        void CheckCashOnly(string name,DataTable source)
        {
            using var familyRows=source.Columns.Contains("FamilyRowKind")?FamilyReceiptReport.PrepareData(source):null;
            if(familyRows is not null)source=familyRows;
            using var cash=source.Copy();
            foreach(DataRow row in cash.Rows)
            {
                row["CreditApplied"]=0m;row["CreditRemaining"]=0m;row["ShowCreditRemaining"]=false;
                row["CashReceived"]=row["TotalPaid"];row["PaymentMethod"]="Cash";
                if(cash.Columns.Contains("ChildCreditApplied"))
                {
                    row["ChildCreditApplied"]=0m;row["ChildCreditRemaining"]=0m;row["ChildShowCreditRemaining"]=false;
                    row["ChildCashReceived"]=row["ChildTotalPaid"];
                }
            }
            string file=source.Columns.Contains("FamilyRowKind")?"FamilyReceipt.rdlc":"StudentReceipt.rdlc";
            string path=Path.Combine(AppContext.BaseDirectory,"Reports",file);
            XNamespace ns="http://schemas.microsoft.com/sqlserver/reporting/2008/01/reportdefinition";
            using var fullData=source.Columns.Contains("FamilyRowKind")?FamilyFeeDocumentPresentation.Receipt(source):FeeReceiptPresentation.Prepare(source);
            using var compactData=cash.Columns.Contains("FamilyRowKind")?FamilyFeeDocumentPresentation.Receipt(cash):FeeReceiptPresentation.Prepare(cash);
            var full=XDocument.Parse(FeeDocumentLayout.SizeToContent(path,fullData));
            var compact=XDocument.Parse(FeeDocumentLayout.SizeToContent(path,compactData));
            double Height(XDocument document)=>double.Parse(document.Descendants(ns+"PageHeight").Single().Value.Replace("in",""),System.Globalization.CultureInfo.InvariantCulture);
            Check(Height(compact)<Height(full),"Zero credit rows collapse completely, including their paper space");
            var table=compact.Descendants(ns+"Tablix").First();
            var rows=table.Descendants(ns+"TablixRow").ToArray();
            var members=table.Element(ns+"TablixRowHierarchy")!.Descendants(ns+"TablixMember")
                .Where(member=>member.Element(ns+"TablixMembers") is null).ToArray();
            Check(!compact.Descendants(ns+"Textbox").Any(box=>(string?)box.Attribute("Name")=="TotalPaidCreditApplied"),
                "Applied credit is included once in the deduction instead of a second settlement row");
            foreach(string textbox in new[]{"TotalPaidCreditRemaining"})
            {
                int index=Array.FindIndex(rows,row=>row.Descendants(ns+"Textbox").Any(box=>(string?)box.Attribute("Name")==textbox));
                string field="ShowCreditRemaining";
                Check(members[index].Element(ns+"Visibility")!.Element(ns+"Hidden")!.Value.Contains(field),"Credit visibility is applied to the complete table row");
            }
            Check(Render(name,cash)==1,"A cash-only receipt renders with credit rows collapsed");
            foreach(var (applied,remaining,label) in new[]{(0m,50m,"remaining-only"),(100m,0m,"fully-used"),(100m,50m,"used-and-remaining")})
            {
                foreach(DataRow row in cash.Rows)
                {
                    row["CreditApplied"]=applied;row["CreditRemaining"]=remaining;
                    row["ShowCreditRemaining"]=applied!=0m||remaining!=0m;
                    row["CashReceived"]=Convert.ToDecimal(row["TotalPaid"])-applied;
                    if(cash.Columns.Contains("ChildCreditApplied"))
                    {
                        bool firstChild=Convert.ToInt32(row["ReceiptStudentKey"])==Convert.ToInt32(cash.Rows[0]["ReceiptStudentKey"]);
                        row["ChildCreditApplied"]=firstChild?applied:0m;row["ChildCreditRemaining"]=firstChild?remaining:0m;
                        row["ChildShowCreditRemaining"]=firstChild&&(applied!=0m||remaining!=0m);
                        row["ChildCashReceived"]=Convert.ToDecimal(row["ChildTotalPaid"])-(firstChild?applied:0m);
                    }
                }
                using var visibleData=cash.Columns.Contains("FamilyRowKind")?FamilyFeeDocumentPresentation.Receipt(cash):FeeReceiptPresentation.Prepare(cash);
                var visible=XDocument.Parse(FeeDocumentLayout.SizeToContent(path,visibleData));
                Check(applied==0m?Height(visible)>Height(compact)&&Height(visible)<Height(full):Height(visible)>Height(compact),
                    "Remaining-only credit occupies one row; used credit adds the deduction and remaining-credit rows even when exhausted");
                Check(Render(name+"-"+label,cash)==1,"Credit visibility scenario prints on one page");
            }
        }
        CheckFeeDocumentConsistency(school,(name,data)=>Render(name,data));
        CheckReceiptConcessions(school,(name,data)=>Render(name,data));
        CheckReceiptPaperTrail(school,(name,data)=>Render(name,data));
        CheckFeeVisitDocuments(school,(name,data)=>Render(name,data));
        using var header=new DataTable();
        foreach(string name in new[]{"ReceiptNumber","PaymentMethod","ReceiptStatus"})header.Columns.Add(name);
        header.Columns.Add("TransactionDate",typeof(DateTime));
        foreach(string name in new[]{"CashReceived","CashApplied","CreditApplied","DiscountApplied","BalanceRemaining","CreditRemaining"})header.Columns.Add(name,typeof(decimal));
        var h=header.Rows.Add("R-2026-00001","Account Credit","",new DateTime(2026,10,19,10,15,0),0m,0m,8700m,0m,0m,50m);
        using var students=new DataTable();
        students.Columns.Add("AdmissionId",typeof(int));foreach(string name in new[]{"AdmissionNumber","StudentName","FatherName"})students.Columns.Add(name);
        foreach(string name in new[]{"CashApplied","CreditApplied","BalanceRemaining"})students.Columns.Add(name,typeof(decimal));
        students.Rows.Add(1,"1001","Ali Example","Example Parent",0m,8700m,0m);
        using var lines=new DataTable();lines.Columns.Add("AdmissionId",typeof(int));
        foreach(string name in new[]{"FeeTypeName","ClassName","SectionName","CampusName"})lines.Columns.Add(name);
        lines.Columns.Add("FeePeriod",typeof(DateTime));foreach(string name in new[]{"AmountBefore","Cash","Credit"})lines.Columns.Add(name,typeof(decimal));
        for(int month=11;month<=13;month++)
        {
            var date=new DateTime(2026,11,1).AddMonths(month-11);
            lines.Rows.Add(1,"Tuition","Class 1","A","Campus Boys",date,400m,0m,400m);
            lines.Rows.Add(1,"Transport","Class 1","A","Campus Boys",date,2500m,0m,2500m);
        }
        using(var credit=FeeReceiptDocuments.BuildData(school,h,students,lines))
        {
            Check(Convert.ToDecimal(credit.Rows[0]["TotalPaid"])==8700m && Convert.ToDecimal(credit.Rows[0]["CashReceived"])==0m
                && Convert.ToDecimal(credit.Rows[0]["CreditRemaining"])==50m,"Credit-only receipt distinguishes settlement, new money and remaining credit");
            Check(Render("individual-credit-receipt",credit)==1,"A six-line credit settlement prints on one receipt page");
            if(review is not null)CheckFeeReviewReceiptCases(credit,(name,data,a4)=>Render(name,data,annual:a4));
            CheckCashOnly("individual-cash-only-receipt",credit);
            using var legacy=credit.Copy();foreach(DataRow row in legacy.Rows) {row["ShowSettlement"]=false;row["ReceiptNumber"]="R-2026-00100";}
            Check(Render("legacy-individual-receipt",legacy)==1,"Original cash receipt fields remain printable through the RDLC template");
            Check(Render("annual-receipt-sheet",legacy,StudentReceiptReport.TemplateFile,annual:true)==1,"Annual printing uses StudentReceipt.rdlc with its native adjustment rows");
            using var bulk=legacy.Clone();
            for(int receipt=0;receipt<15;receipt++)foreach(DataRow original in legacy.Rows)
            {
                bulk.ImportRow(original);var row=bulk.Rows[^1];row["ReceiptPageKey"]=receipt+1;row["ReceiptNumber"]=$"FR-2026-{receipt+100:D5}";
            }
            Render("bulk-annual-search",bulk,StudentReceiptReport.TemplateFile,annual:true);
        }
        using(var partialHeader=header.Copy())
        using(var partialStudents=students.Copy())
        using(var partialLines=lines.Clone())
        {
            var payment=partialHeader.Rows[0];
            payment["CashReceived"]=200m;payment["CashApplied"]=200m;payment["CreditApplied"]=0m;
            payment["BalanceRemaining"]=2200m;payment["PaymentMethod"]="Cash";
            partialStudents.Rows[0]["CashApplied"]=200m;partialStudents.Rows[0]["CreditApplied"]=0m;
            partialStudents.Rows[0]["BalanceRemaining"]=2200m;
            partialLines.Rows.Add(1,"Transport Fee","Class 1","A","Campus Boys",new DateTime(2026,11,1),2400m,200m,0m);
            using var partial=FeeReceiptDocuments.BuildData(school,payment,partialStudents,partialLines);
            Check(partial.Rows[0].Field<decimal>("Amount")==2400m && partial.Rows[0].Field<decimal>("FeesCovered")==2400m
                && partial.Rows[0].Field<decimal>("CashReceived")==200m && partial.Rows[0].Field<decimal>("TotalPaid")==200m
                && partial.Rows[0].Field<decimal>("BalanceRemaining")==2200m,
                "A partial receipt shows the saved full fee, total, received and remaining balance independently");
            Check(Render("individual-partial-payment",partial)==1,"A partial payment receipt renders with its full charge amount");
        }
        // A family account can include debts beyond the fee lines on this
        // receipt. Keep these scopes visible rather than suggesting that the
        // account balance is simply this receipt's fee total minus its payment.
        using(var familyHeader=header.Copy())
        using(var familyStudents=students.Copy())
        using(var familyLines=lines.Clone())
        {
            var payment=familyHeader.Rows[0];
            payment["ReceiptNumber"]="FR-2026-00002";payment["PaymentMethod"]="Cash";
            payment["CashReceived"]=3000m;payment["CashApplied"]=3000m;payment["CreditApplied"]=0m;
            payment["BalanceRemaining"]=6500m;payment["CreditRemaining"]=0m;
            familyStudents.Rows[0]["CashApplied"]=3000m;familyStudents.Rows[0]["CreditApplied"]=0m;
            familyStudents.Rows[0]["BalanceRemaining"]=4300m;
            familyStudents.Rows.Add(2,"1002","Sara Example","Example Parent",0m,0m,2200m);
            familyLines.Rows.Add(1,"Tuition Fee","Class 1","A","Campus Boys",new DateTime(2026,10,1),400m,400m,0m);
            familyLines.Rows.Add(1,"Admission Fee","Class 1","A","Campus Boys",new DateTime(2026,10,1),4000m,2600m,0m);
            familyLines.Rows.Add(1,"Tuition Fee","Class 1","A","Campus Boys",new DateTime(2026,11,1),400m,0m,0m);
            using var family=FeeReceiptDocuments.BuildData(school,payment,familyStudents,familyLines);
            Check(family.Rows[0].Field<decimal>("FeesCovered")==4800m && family.Rows[0].Field<decimal>("CashReceived")==3000m
                && family.Rows[0].Field<decimal>("StudentOutstanding")==4300m && family.Rows[0].Field<decimal>("BalanceRemaining")==6500m,
                "Receipt fees, payment, child debt and family debt retain their separate financial scopes");
            var unpaidChild=family.AsEnumerable().Single(row=>row.Field<string>("ReceiptStudentName")=="1002 · Sara Example");
            Check(unpaidChild.Field<string>("FeeDescription")=="No payment applied to this child"
                && unpaidChild.Field<decimal>("Amount")==0m && unpaidChild.Field<decimal>("StudentSettled")==0m
                && unpaidChild.Field<decimal>("StudentOutstanding")==2200m,
                "A listed sibling without fee allocations remains visible with zero paid and their own balance");
            Check(family.Rows[0].Field<string>("AdmissionNumberText")=="1001, 1002",
                "Family receipt admission numbers match the children shown in its body");
            Check(Render("family-partial-payment",family)==1,"A partial family payment fits with explicit student and family balance labels");
            int unpaidReads=0;
            Task<DataTable> UnpaidFees(int id)
            {
                Check(id==2,"Only the child without saved receipt lines needs outstanding fee details");
                unpaidReads++;
                var dues=familyLines.Clone();
                dues.Rows.Add(2,"Tuition Fee","Class 2","B","Campus Boys",new DateTime(2026,11,1),2200m,0m,0m);
                return Task.FromResult(dues);
            }
            FeeReceiptDocuments.AddUnpaidChildFeesAsync(payment,familyStudents,familyLines,UnpaidFees).GetAwaiter().GetResult();
            FeeReceiptDocuments.AddUnpaidChildFeesAsync(payment,familyStudents,familyLines,UnpaidFees).GetAwaiter().GetResult();
            Check(unpaidReads==1 && familyLines.Rows.Count==4,"Outstanding lines are added once without duplicating saved fees");
            using(var withDues=FeeReceiptDocuments.BuildData(school,payment,familyStudents,familyLines))
            {
                var child=withDues.AsEnumerable().Single(row=>row.Field<string>("ReceiptStudentName")=="1002 · Sara Example");
                Check(child.Field<string>("FeeDescription")=="Tuition Fee" && child.Field<string>("FeePeriod")=="Nov-2026"
                    && child.Field<decimal>("Amount")==2200m && child.Field<decimal>("ChildPayable")==2200m
                    && child.Field<decimal>("ChildTotalPaid")==0m && child.Field<decimal>("ChildBalanceRemaining")==2200m,
                    "An unpaid child shows their fee and month, zero payment, and current balance");
                Check(withDues.Rows[0].Field<decimal>("CashReceived")==3000m && withDues.Rows[0].Field<decimal>("FeesCovered")==7000m,
                    "Showing unpaid fees increases displayed fee totals without changing money received");
                Check(Render("family-unpaid-child-fees",withDues)==1,"Unpaid child fee details render on the family receipt");
            }
            string sourcePath=Path.Combine(AppContext.BaseDirectory,"Reports",FamilyReceiptReport.TemplateFile);
            var editable=XDocument.Load(sourcePath);XNamespace editableNs=editable.Root!.Name.Namespace;
            editable.Descendants(editableNs+"Textbox").Single(box=>(string?)box.Attribute("Name")=="DocumentDateFooter")
                .Descendants(editableNs+"Value").First().Value=new string('W',150)+" ";
            string editedPath=Path.Combine(folder,"edited-caption.rdlc");editable.Save(editedPath);
            using var familyDisplay=FamilyFeeDocumentPresentation.Receipt(family);
            decimal PaperHeight(string path)=>decimal.Parse(XDocument.Parse(FeeDocumentLayout.SizeToContent(path,familyDisplay))
                .Descendants(editableNs+"PageHeight").Single().Value.Replace("in",""),System.Globalization.CultureInfo.InvariantCulture);
            Check(PaperHeight(editedPath)>PaperHeight(sourcePath),
                "Editing a literal date caption grows the receipt paper while its separate date formula remains intact");
            Check(Render("edited-caption-receipt",family,editedPath)==1,
                "An edited multi-run receipt caption renders without adding an unintended second page");
        }
        h["CashReceived"]=1000m;h["CashApplied"]=1000m;h["CreditApplied"]=7700m;
        students.Rows[0]["CashApplied"]=1000m;students.Rows[0]["CreditApplied"]=7700m;
        lines.Rows[1]["Cash"]=1000m;lines.Rows[1]["Credit"]=1500m;
        using(var mixed=FeeReceiptDocuments.BuildData(school,h,students,lines))Check(Render("individual-mixed-receipt",mixed)==1,"Mixed receipt uses the same layout");
        students.Rows.Add(2,"1002","Sara Example","Example Parent",300m,100m,600m);
        lines.Rows.Add(2,"Tuition","Class 2","B","Campus Boys",new DateTime(2026,11,1),1000m,300m,100m);
        h["CashReceived"]=1300m;h["CashApplied"]=1300m;h["CreditApplied"]=7800m;h["BalanceRemaining"]=600m;
        h["ReceiptNumber"]="FR-2026-00001";
        using(var family=FeeReceiptDocuments.BuildData(school,h,students,lines))
        {
            Check(family.AsEnumerable().All(row=>row.Field<string>("CampusName")=="Campus Boys"),
                "A family receipt retains the common campus in its header");
            using var mixedCampuses=lines.Copy();mixedCampuses.Rows[^1]["CampusName"]="Campus Girls";
            Check(FeeDocumentData.CampusHeading(mixedCampuses)=="Multiple campuses",
                "A family spanning campuses does not claim one child's campus for everyone");
            Check(family.AsEnumerable().Select(row=>row.Field<int>("ReceiptPageKey")).Distinct().Single()==1,"All children share one family document key");
            Check(family.AsEnumerable().Select(row=>row.Field<int>("ReceiptStudentKey")).Distinct().Count()==2,"Family receipt identifies both children");
            using(var displayed=FamilyReceiptReport.PrepareData(family))
            {
                Check(displayed.Rows.Count==lines.Rows.Count && displayed.AsEnumerable().Sum(row=>row.Field<decimal>("Amount"))==9700m,
                    "Family layout keeps full pre-payment fee amounts without summing heading or subtotal rows");
                Check(displayed.AsEnumerable().Select(row=>row.Field<int>("ReceiptStudentKey")).Distinct().Count()==2
                    && displayed.AsEnumerable().Count(row=>!string.IsNullOrWhiteSpace(row.Field<string>("FeePeriod")))==4,
                    "Family months appear once per month per child under separate student groups");
                Check(displayed.Rows[^1].Field<decimal>("StudentOutstanding")==600m
                    && displayed.Rows[0].Field<decimal>("StudentSettled")==8700m,"Per-child settlement and outstanding values remain unchanged");
            }
            Check(Render("family-mixed-receipt",family)==1,"Family receipt has no forced page per child");
            CheckCashOnly("family-cash-only-receipt",family);
            using var legacyFamily=family.Copy();foreach(DataRow row in legacyFamily.Rows) row["ShowSettlement"]=false;
            Check(Render("legacy-family-receipt",legacyFamily)==1,"Legacy family receipts use the same combined RDLC layout");
        }
        using var charges=new DataTable();foreach(string name in new[]{"Reference","StudentName","FeeTypeName"})charges.Columns.Add(name);
        charges.Columns.Add("BillingMonth",typeof(DateTime));charges.Columns.Add("DiscountAmount",typeof(decimal));charges.Columns.Add("Due",typeof(decimal));charges.Columns.Add("CampusName").DefaultValue="Campus Boys";
        charges.Rows.Add("1001","Ali Example","Tuition",new DateTime(2026,11,1),0m,400m);
        charges.Rows.Add("1001","Ali Example","Transport",new DateTime(2026,11,1),0m,600m);
        charges.Rows.Add("1001","Ali Example","Tuition",new DateTime(2026,12,1),0m,400m);
        using(var bill=FeeBillDataBuilder.BuildGrouped(school.Name,school.ContactNumber,school.Address,"Example Parent",charges))
        {
            Check(bill.AsEnumerable().All(row=>row.Field<string>("CampusName")=="Campus Boys"),
                "Grouped bills retain the campus supplied by the posted charge placement");
            using var original=StudentBillReport.FromGroupedData(bill);
            string snapshot=FeeBillDocuments.Snapshot(original);
            using var restored=FeeBillDocuments.ReadSnapshot(snapshot);
            Check(restored.Rows.Count==original.Rows.Count && restored.AsEnumerable().Sum(row=>row.Field<decimal>("Amount"))==1400m,
                "Saved bill snapshots round-trip the complete report schema and every amount");
            foreach(DataRow row in original.Rows) {row["ReceiptNumber"]="B-2026-00001";row["PaymentDateText"]="different print date";}
            Check(FeeBillDocuments.Snapshot(original)==snapshot,"Reprint date and number do not create a new issued bill");
            foreach(DataRow row in original.Rows)row["BillSourceKey"]="new posted charge";
            Check(FeeBillDocuments.Snapshot(original)!=snapshot,"Replacement charges cannot reuse an older bill merely because amounts match");
            File.WriteAllText(Path.Combine(folder,"bill-snapshot.xml"),snapshot);
            string definition=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Reports","StudentBill.rdlc"));
            Check(definition.Contains("TOTAL DUE") && definition.Contains("Bill No.") && !definition.Contains("TOTAL SETTLED"),"Bills contain bill-specific titles and totals");
            using var studentBill=StudentBillReport.FromGroupedData(bill);
            Check(Render("individual-bill",studentBill)==1,"Individual bill uses the established receipt design");
            Check(Render("individual-bill-a4",studentBill,annual:true)>=1,"A4 bill printing reuses the approved bill RDLC on a sheet");
            if(review is not null)
            {
                using var longBill=studentBill.Copy();
                foreach(DataRow row in longBill.Rows)
                {
                    row["StudentName"]="Muhammad Abdul Rahman Example Student With A Long Recorded Name";
                    row["FatherName"]="Abdul Hameed Example Parent With A Long Recorded Name";
                    row["FeeDescription"]="Annual examination preparation and supplementary learning materials fee";
                }
                Check(Render("long-names-and-fees-bill",longBill)>=1,"Long bill names and fee labels render through the fitted thermal print path");
            }
        }
        using(var waivedCharges=charges.Clone())
        {
            waivedCharges.Columns.Add("GrossAmount",typeof(decimal));
            waivedCharges.Rows.Add("6590","Aqsa","Tuition Fee — Free",new DateTime(2026,10,1),2100m,0m,"Campus Girls",2100m);
            using var waived=StudentBillReport.BuildData(school.Name,school.ContactNumber,school.Address,"Example Parent",waivedCharges);
            string saved=FeeBillDocuments.Snapshot(waived);
            using(var display=FeeBillPresentation.Prepare(waived))
            {
                var row=display.Rows[0];
                Check(row.Field<decimal>("Amount")==0m && row.Field<decimal>("BillDisplayAmount")==2100m
                    && row.Field<string>("BillDiscountText")==(-2100m).ToString("N0") && row.Field<decimal>("TotalPaid")==0m,
                    "Free bill displays original fee and negative concession while preserving its zero unpaid amount");
                Check(row.Field<string>("BillDiscountCaption")=="Less: Discount / Waived" && row.Field<string>("BillPaymentPrompt")=="No payment required",
                    "Free bills explain the concession and never request payment");
            }
            Check(FeeBillDocuments.Snapshot(waived)==saved,"Preparing the printable bill does not rewrite the financial snapshot");
            Check(Render("individual-free-bill",waived)==1,"The fully waived student bill fits one clear page");
            using(var legacy=waived.Copy())
            {
                legacy.Columns.Remove("BillGrossAmount");legacy.Columns.Remove("BillLineDiscount");
                using var display=FeeBillPresentation.Prepare(legacy);
                Check(display.Rows[0].Field<decimal>("BillDisplayAmount")==2100m,"An existing single-fee free bill can show its original amount from saved concession evidence");
                Check(Render("existing-free-bill",legacy)==1,"An existing free bill renders without requiring new snapshot fields");
            }
            waivedCharges.Rows[0]["FeeTypeName"]="Tuition Fee — Scholarship";
            waivedCharges.Rows[0]["DiscountAmount"]=500m;waivedCharges.Rows[0]["Due"]=600m;
            using var partial=StudentBillReport.BuildData(school.Name,school.ContactNumber,school.Address,"Example Parent",waivedCharges);
            using(var display=FeeBillPresentation.Prepare(partial))
            {
                var row=display.Rows[0];
                Check(row.Field<decimal>("BillDisplayAmount")==2100m && row.Field<decimal>("BillPreviouslySettled")==1000m
                    && row.Field<bool>("ShowBillPreviouslySettled") && row.Field<decimal>("TotalPaid")==600m,
                    "A scholarship bill separates the original fee, concession and previous settlement without inflating the unpaid balance");
                Check(row.Field<string>("BillPaymentPrompt")=="Please pay at the fee counter","A positive bill continues to request payment");
            }
            Check(Render("scholarship-partly-paid-bill",partial)==1,"A partly paid scholarship bill renders the complete breakdown on one page");
            waivedCharges.Rows.Add("6500","Other Child","Transport Fee",new DateTime(2026,10,1),0m,900m,"Campus Boys",900m);
            using var family=FamilyBillReport.BuildData(school.Name,school.ContactNumber,school.Address,"Example Parent",waivedCharges);
            Check(Render("family-concession-bill",family)==1,"A family bill keeps separate child headings with the shared concession and settlement breakdown");
        }
        charges.Rows.Add("1002","Sara Example","Tuition",new DateTime(2026,11,1),0m,500m);
        charges.Rows.Add("1001","Ali Example","Transport",new DateTime(2026,12,1),0m,600m);
        charges.Rows.Add("1002","Sara Example","Admission",DBNull.Value,0m,200m);
        using(var familyBill=FeeBillDataBuilder.BuildGrouped(school.Name,school.ContactNumber,school.Address,"Example Parent",charges))
            Check(Render("family-bill",familyBill)==1,"Family bill is one combined document in the same design");
        CheckFamilyDocumentSections(school,(name,data)=>Render(name,data));
        using(var periodCharges=charges.Clone())
        {
            periodCharges.Columns.Add("GrossAmount",typeof(decimal));
            periodCharges.Rows.Add("3334","Eman","Tuition Fee",new DateTime(2026,10,1),0m,1600m,"Campus Girls",3000m);
            periodCharges.Rows.Add("3334","Eman","Tuition Fee",new DateTime(2026,11,1),0m,3000m,"Campus Girls",3000m);
            using var original=StudentBillReport.BuildData(school.Name,school.ContactNumber,school.Address,"Example Parent",periodCharges);
            using var saved=FeeBillDocuments.ReadSnapshot(FeeBillDocuments.Snapshot(original));
            using var batch=saved.Clone();
            for(int page=1;page<=2;page++)
                foreach(DataRow row in saved.Rows)
                {
                    batch.ImportRow(row);var line=batch.Rows[^1];line["ReceiptPageKey"]=page;
                    line["AdmissionNumberText"]=page==1?"1001":"3334";
                    line["StudentName"]=page==1?"First Student":"Eman";
                }
            Check(Render("batch-outstanding-months",batch)==2,"Print Outstanding renders each saved bill on its own page without losing fee months");
            periodCharges.Rows.Add("1001","Other Child","Tuition Fee",new DateTime(2026,10,1),0m,3000m,"Campus Girls",3000m);
            periodCharges.Rows.Add("1001","Other Child","Tuition Fee",new DateTime(2026,11,1),0m,3000m,"Campus Girls",3000m);
            using var family=FamilyBillReport.BuildData(school.Name,school.ContactNumber,school.Address,"Example Parent",periodCharges);
            Check(Render("family-bill-months",family)==1,"Family bill prints both months for each child on one continuous page");
            DataTable Saved(DataTable data,string number)
            {
                var table=new DataTable();table.Columns.Add("BillNumber");table.Columns.Add("IssuedAt",typeof(DateTime));table.Columns.Add("SnapshotXml");
                table.Rows.Add(number,DateTime.Today,FeeBillDocuments.Snapshot(data));return table;
            }
            using var savedFamily1=Saved(family,"FB-2026-00001");using var savedFamily2=Saved(family,"FB-2026-00002");
            using var savedSingle=Saved(original,"B-2026-00003");
            using var groupedBatch=OutstandingBillsPreview.CombineSaved(new[]{savedFamily1,savedFamily2,savedSingle},true);
            Check(Render("family-batch-separate-pages",groupedBatch)==3,"Two families and a standalone student open in one preview with each bill on a separate page");
            Check(groupedBatch.AsEnumerable().GroupBy(r=>r.Field<int>("ReceiptPageKey")).Select(g=>g.First().Field<string>("ReceiptNumber"))
                .SequenceEqual(new[]{"FB-2026-00001","FB-2026-00002","B-2026-00003"}),"Grouped batch keeps original bill numbers instead of replacing or combining their totals");
        }
    }
}
