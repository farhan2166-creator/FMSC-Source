using System.Data;
using FMSC_SMS.Core;

internal static partial class Program
{
    private static void CheckFamilyDocumentSections(InstitutionProfile.PrintIdentity school,Func<string,DataTable,int> render)
    {
        using var header=new DataTable();
        foreach(string field in new[]{"ReceiptNumber","PaymentMethod","ReceiptStatus"})header.Columns.Add(field);
        header.Columns.Add("TransactionDate",typeof(DateTime));
        foreach(string field in new[]{"CashReceived","CashApplied","CreditApplied","DiscountApplied","BalanceRemaining","CreditRemaining"})header.Columns.Add(field,typeof(decimal));
        var h=header.Rows.Add("FR-2026-00005","Cash","",new DateTime(2026,10,25,13,0,0),4700m,4500m,0m,500m,1500m,200m);
        using var students=new DataTable();students.Columns.Add("AdmissionId",typeof(int));
        foreach(string field in new[]{"AdmissionNumber","StudentName","FatherName"})students.Columns.Add(field);
        foreach(string field in new[]{"CashApplied","CreditApplied","BalanceRemaining","CreditRemaining","DiscountApplied"})students.Columns.Add(field,typeof(decimal));
        students.Rows.Add(1,"3334","Eman","Muhammad Sajid",1500m,0m,500m,0m,500m);
        students.Rows.Add(2,"3728","Fahad Ahmad","Muhammad Sajid",3000m,0m,0m,0m,0m);
        students.Rows.Add(3,"6590","Zero account","Muhammad Sajid",0m,0m,0m,0m,0m);
        students.Rows.Add(4,"6591","Unpaid child","Muhammad Sajid",0m,0m,1000m,0m,0m);
        students.Rows.Add(5,"6592","Advance child","Muhammad Sajid",0m,0m,0m,200m,0m);
        using var cash=new DataTable();cash.Columns.Add("AdmissionId",typeof(int));cash.Columns.Add("CashReceived",typeof(decimal));
        cash.Rows.Add(1,1500m);cash.Rows.Add(2,3000m);cash.Rows.Add(5,200m);
        FeeReceiptDocuments.ApplyChildCashReceived(h,students,cash);
        Check(students.Rows[4].Field<decimal>("CashReceived")==200m && students.Rows[0].Field<decimal>("CashReceived")==1500m,
            "Original linked payments retain the advance with its actual child");
        using(var wrong=cash.Copy())
        {
            wrong.Rows[2]["CashReceived"]=300m;
            bool rejected=false;try{FeeReceiptDocuments.ApplyChildCashReceived(h,students,wrong);}catch(InvalidOperationException){rejected=true;}
            Check(rejected,"An unmatched original payment total is rejected rather than assigning an advance to another child");
        }
        using var lines=new DataTable();lines.Columns.Add("AdmissionId",typeof(int));
        foreach(string field in new[]{"FeeTypeName","ClassName","SectionName","CampusName"})lines.Columns.Add(field);
        lines.Columns.Add("FeePeriod",typeof(DateTime));
        foreach(string field in new[]{"AmountBefore","Cash","Credit","Discount"})lines.Columns.Add(field,typeof(decimal));
        lines.Rows.Add(1,"Tuition Fee","Class 1","A","Campus Girls",new DateTime(2026,11,1),2000m,1500m,0m,500m);
        lines.Rows.Add(1,"Transport Fee","Class 1","A","Campus Girls",new DateTime(2026,11,1),500m,0m,0m,0m);
        lines.Rows.Add(2,"Tuition Fee","Class 5","B","Campus Boys",new DateTime(2026,11,1),3000m,3000m,0m,0m);
        using var receipt=FeeReceiptDocuments.BuildData(school,h,students,lines);
        using(var display=FamilyFeeDocumentPresentation.Receipt(receipt))
        {
            DataRow Child(string admission)=>display.AsEnumerable().First(row=>row.Field<string>("ChildAdmissionNumberText")==admission);
            var first=Child("3334");var second=Child("3728");var zero=Child("6590");var owing=Child("6591");var advance=Child("6592");
            Check(first.Field<decimal>("ChildFeesCovered")==2500m&&first.Field<decimal>("ChildDiscountApplied")==500m
                &&first.Field<decimal>("ChildPayable")==2000m&&first.Field<decimal>("ChildCashReceived")==1500m
                &&first.Field<decimal>("ChildBalanceRemaining")==500m,"Each child slip has its own fee, discount, payable, received and due calculation");
            Check(second.Field<string>("ChildStudentName")=="Fahad Ahmad"&&second.Field<decimal>("ChildCashReceived")==3000m,
                "A sibling's slip does not repeat the full family payment");
            Check(!zero.Field<bool>("ShowChildDocument")&&owing.Field<bool>("ShowChildDocument")&&owing.Field<decimal>("ChildCashReceived")==0m,
                "A zero account is hidden but an unpaid child with a balance is printed");
            Check(advance.Field<bool>("ShowChildDocument")&&advance.Field<decimal>("ChildCreditRetained")==200m
                &&first.Field<decimal>("ChildCreditRetained")==0m&&second.Field<decimal>("ChildCreditRetained")==0m,
                "An advance-only child is printed and only that child shows the advance");
            Check(first.Field<decimal>("GroupFeeTotal")==5500m&&first.Field<decimal>("GroupDiscount")==500m
                &&first.Field<decimal>("GroupPayable")==5000m&&first.Field<decimal>("GroupReceived")==4700m&&first.Field<decimal>("GroupDue")==1500m,
                "The single group summary retains total fees, discount, actual cash received and all outstanding child balances");
            Check(first.Field<string>("ChildCampusName")=="Campus Girls"&&second.Field<string>("ChildCampusName")=="Campus Boys",
                "Each child retains their own campus beneath the common school heading");
        }
        Check(render("family-separate-child-receipts",receipt)==1,"Child receipts and the final summary render on one continuous page");
        var attachmentData = receipt.Copy();
        if (!attachmentData.Columns.Contains("ParentRelationLabel")) attachmentData.Columns.Add("ParentRelationLabel");
        foreach (DataRow row in attachmentData.Rows) row["ParentRelationLabel"] = "S/O";
        using (var share = new FeeMessageDocument(attachmentData, 1, new[] { 1, 2, 3, 4, 5 }, Array.Empty<int>(),
            family: true, receipt: true, amount: 4700m, receiptNumber: "FR-2026-00005"))
        {
            // The test fixture carries printable labels so export never loads live student details.
            // The report copy must preserve the saved family totals and every printable child.
            string path = Path.Combine("artifacts", "fee-documents", $"fmsc-shared-receipt-{Guid.NewGuid():N}.pdf");
            try
            {
                share.ExportAsync(path).GetAwaiter().GetResult();
                using var pdf = PdfSharp.Pdf.IO.PdfReader.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
                Check(pdf.PageCount == 1, "The WhatsApp attachment exports a complete saved family receipt PDF");
                var rendering = share.RenderImageAsync();
                PumpUntil(() => rendering.IsCompleted, "Render the saved family receipt image", TimeSpan.FromSeconds(60));
                using var image = rendering.GetAwaiter().GetResult();
                Check(image.Width >= 500 && image.Height > image.Width && image.HorizontalResolution == FeeMessageImage.Dpi,
                    "The same saved family receipt renders a readable continuous image without a saved attachment");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
        using(var largeStudents=students.Clone())
        using(var largeLines=lines.Clone())
        using(var largeHeader=header.Copy())
        {
            for(int i=0;i<6;i++)
            {
                largeStudents.Rows.Add(100+i,(6591+i).ToString(),new[]{"Agha Khan","Haris Ali","Ali Test","Naz Ali","Khan Ali","Afnan Niazi"}[i],"Fazal Muhammad Niazi",5000m,0m,0m,0m,0m,5000m);
                largeLines.Rows.Add(100+i,"Admission Fee","Play Group","F","Campus Girls",new DateTime(2026,10,1),3960m,3960m,0m,0m);
                largeLines.Rows.Add(100+i,"Tuition Fee","Play Group","F","Campus Girls",new DateTime(2026,11,1),1040m,1040m,0m,0m);
            }
            var lh=largeHeader.Rows[0];lh["CashReceived"]=30000m;lh["CashApplied"]=30000m;lh["DiscountApplied"]=0m;lh["BalanceRemaining"]=2761m;lh["CreditRemaining"]=0m;
            using var largeReceipt=FeeReceiptDocuments.BuildData(school,lh,largeStudents,largeLines);
            Check(render("family-six-child-receipt",largeReceipt)==1,"Six child slips and the family totals stay on one continuous receipt");
            using(var tenStudents=largeStudents.Copy())
            using(var tenLines=largeLines.Copy())
            using(var tenHeader=largeHeader.Copy())
            {
                string[] additionalNames = { "Ayesha Khan", "Hamza Ali", "Fatima Tariq", "Bilal Shah" };
                for(int i=6;i<10;i++)
                {
                    int id=100+i;
                    tenStudents.Rows.Add(id,(6591+i).ToString(),additionalNames[i-6],
                        "Fazal Muhammad Niazi",5000m,0m,0m,0m,0m,5000m);
                    tenLines.Rows.Add(id,"Admission Fee","Play Group","F","Campus Girls",
                        new DateTime(2026,10,1),3960m,3960m,0m,0m);
                    tenLines.Rows.Add(id,"Tuition Fee","Play Group","F","Campus Girls",
                        new DateTime(2026,11,1),1040m,1040m,0m,0m);
                }
                tenHeader.Rows[0]["CashReceived"]=50000m;
                tenHeader.Rows[0]["CashApplied"]=50000m;
                tenHeader.Rows[0]["BalanceRemaining"]=0m;
                using var tenReceipt=FeeReceiptDocuments.BuildData(school,tenHeader.Rows[0],tenStudents,tenLines);
                Check(tenReceipt.AsEnumerable().Select(row=>row.Field<int>("ReceiptStudentKey")).Distinct().Count()==10,
                    "Ten-child family receipt keeps a distinct student identity for every sibling");
                Check(render("family-ten-child-receipt",tenReceipt)>=1,
                    "The production family receipt layout handles ten children and their combined settlement");
            }
            using var saved=new DataSet();
            saved.Tables.Add(largeHeader.Copy());saved.Tables.Add(largeStudents.Copy());saved.Tables.Add(largeLines.Copy());
            using var copy=FeeReceiptChildCopy.Extract(saved,100);
            using var childReceipt=FeeReceiptDocuments.BuildData(school,copy.Tables[0].Rows[0],copy.Tables[1],copy.Tables[2]);
            foreach(DataRow row in childReceipt.Rows)row["CopyLabel"]="CHILD COPY";
            Check(childReceipt.AsEnumerable().All(row=>string.IsNullOrWhiteSpace(row.Field<string>("ReceiptStatus"))),"A child copy needs no redundant status explanation beside its own payment figures");
            Check(render("family-child-copy-concise",childReceipt)==1,"The concise child copy renders on one page");
            // A modern saved receipt must stay clean; only legacy snapshots
            // carry the historical limitation on both full and child reprints.
            saved.Tables[0].Columns.Add("SnapshotVersion",typeof(int));
            saved.Tables[0].Rows[0]["SnapshotVersion"]=2;
            FeeReceiptDocuments.ApplyLegacyReceiptNotice(saved.Tables[0].Rows[0]);
            Check(string.IsNullOrWhiteSpace(saved.Tables[0].Rows[0].Field<string>("ReceiptStatus")),
                "Complete modern receipts do not show the legacy warning");
            saved.Tables[0].Rows[0]["SnapshotVersion"]=1;
            FeeReceiptDocuments.ApplyLegacyReceiptNotice(saved.Tables[0].Rows[0]);
            FeeReceiptDocuments.ApplyLegacyReceiptNotice(saved.Tables[0].Rows[0]);
            string familyStatus=Convert.ToString(saved.Tables[0].Rows[0]["ReceiptStatus"])??"";
            Check(familyStatus.Contains("Legacy receipt:")&&familyStatus.Split("Legacy receipt:").Length==2,
                "Legacy family receipt receives the limitation warning once");
            using(var legacyChild=FeeReceiptChildCopy.Extract(saved,100))
            using(var legacyChildPrint=FeeReceiptDocuments.BuildData(school,legacyChild.Tables[0].Rows[0],legacyChild.Tables[1],legacyChild.Tables[2]))
            {
                Check(Convert.ToString(legacyChild.Tables[0].Rows[0]["ReceiptStatus"])==familyStatus
                    &&legacyChildPrint.AsEnumerable().All(row=>(row.Field<string>("ReceiptStatus")??"").Contains("Legacy receipt:")),
                    "Legacy child copy inherits the same warning as the original family receipt");
            }
        }
        using(var advanceStudents=students.Clone())
        using(var advanceHeader=header.Copy())
        using(var emptyLines=lines.Clone())
        {
            advanceStudents.Rows.Add(10,"7001","Advance one","Parent",0m,0m,0m,125m,0m,125m);
            advanceStudents.Rows.Add(11,"7002","Zero child","Parent",0m,0m,0m,0m,0m,0m);
            var ah=advanceHeader.Rows[0];ah["CashReceived"]=125m;ah["CashApplied"]=0m;ah["DiscountApplied"]=0m;ah["BalanceRemaining"]=0m;ah["CreditRemaining"]=125m;
            using var advanceReceipt=FeeReceiptDocuments.BuildData(school,ah,advanceStudents,emptyLines);
            Check(render("family-advance-only-receipt",advanceReceipt)==1,"An advance-only family collection still prints its owner's receipt without empty sibling slips");
        }
        using var charges=new DataTable();foreach(string field in new[]{"Reference","StudentName","FeeTypeName"})charges.Columns.Add(field);
        charges.Columns.Add("BillingMonth",typeof(DateTime));
        foreach(string field in new[]{"DiscountAmount","Due","GrossAmount"})charges.Columns.Add(field,typeof(decimal));
        charges.Columns.Add("CampusName");
        charges.Rows.Add("3334","Eman","Tuition Fee",new DateTime(2026,11,1),500m,500m,2500m,"Campus Girls");
        charges.Rows.Add("3728","Fahad Ahmad","Tuition Fee",new DateTime(2026,11,1),0m,3000m,3000m,"Campus Boys");
        charges.Rows.Add("6590","Free child","Tuition Fee — Free",new DateTime(2026,11,1),2100m,0m,2100m,"Campus Girls");
        using var bill=FamilyBillReport.BuildData(school.Name,school.ContactNumber,school.Address,"Muhammad Sajid",charges);
        string snapshot=FeeBillDocuments.Snapshot(bill);
        using(var display=FamilyFeeDocumentPresentation.Bill(bill))
        {
            var first=display.Rows[0];var zero=display.Rows[^1];
            Check(first.Field<decimal>("ChildFeesCovered")==2500m&&first.Field<decimal>("ChildDiscountApplied")==500m
                &&first.Field<decimal>("ChildBillPreviouslySettled")==1500m&&first.Field<decimal>("ChildTotalPaid")==500m,
                "A child's bill keeps previous settlements separate from discounts and actual due");
            Check(zero.Field<bool>("ShowChildDocument")&&zero.Field<decimal>("Amount")==0m
                &&zero.Field<decimal>("ChildFeesCovered")==2100m&&zero.Field<decimal>("ChildDiscountApplied")==2100m,
                "A fully discounted child stays visible with the full fee and discount, preserving the zero due");
            Check(first.Field<decimal>("GroupFeeTotal")==7600m&&first.Field<decimal>("GroupDiscount")==2600m
                &&first.Field<decimal>("GroupDue")==3500m&&first.Field<decimal>("GroupPreviouslySettled")==1500m,
                "The family bill summary totals the visible child bills and preserves their unpaid total");
        }
        Check(FeeBillDocuments.Snapshot(bill)==snapshot,"Preparing child bills leaves the saved family bill snapshot intact");
        Check(render("family-separate-child-bills",bill)==1,"Child bills share one long page and one final summary");
        using(var tenCharges=charges.Copy())
        {
            string[] names={"Ayesha Khan","Hamza Ali","Fatima Tariq","Bilal Shah",
                "Maryam Iqbal","Usman Farooq","Zainab Ahmad"};
            for(int i=0;i<names.Length;i++)
                tenCharges.Rows.Add((6701+i).ToString(),names[i],"Monthly Tuition",
                    new DateTime(2026,11,1),i%3==0?350m:0m,
                    2050m+(i%4)*275m,2400m+(i%4)*275m,
                    i%2==0?"Campus Girls":"Campus Boys");
            using var tenBill=FamilyBillReport.BuildData(school.Name,school.ContactNumber,
                school.Address,"Muhammad Sajid",tenCharges);
            Check(render("family-ten-child-bill",tenBill)>=1,
                "The production family bill layout keeps all ten children and the family total");
        }
        using(var legacy=FeeDocumentData.Create())
        {
            foreach(var (key,admission,name,amount,refund,balance) in new[]{(1,"8001","Same Name",100m,40m,40m),(2,"8002","Same Name",200m,0m,0m)})
            {
                var row=legacy.Rows.Add(key,school.Name,"STUDENT PAYMENT RECEIPT","PARENT COPY","R-2026-00010",name,admission,"Class 1",
                    "25-10-2026 01:00 PM","","Tuition Fee",amount,0m,amount,balance,"Cash","",false,true,school.ContactNumber,school.Address,"Campus Boys","Parent","Nov-2026");
                row["OriginalPaid"]=amount;row["NetPaid"]=amount-refund;row["RefundAmount"]=refund;row["ShowAdjustments"]=refund>0m;
            }
            using var combined=FamilyReceiptReport.CombineLegacyData(legacy);
            using(var display=FamilyFeeDocumentPresentation.Receipt(combined))
            {
                var children=display.AsEnumerable().GroupBy(row=>row.Field<string>("ChildAdmissionNumberText")).Select(group=>group.First()).ToArray();
                Check(children.Length==2&&children[0].Field<string>("ChildStudentName")==children[1].Field<string>("ChildStudentName"),
                    "Legacy siblings with the same name retain distinct admission identities");
                Check(children[0].Field<decimal>("ChildRefundAmount")==40m&&children[1].Field<decimal>("ChildRefundAmount")==0m
                    &&children[0].Field<decimal>("ChildNetPaid")==60m&&children[1].Field<decimal>("ChildNetPaid")==200m,
                    "Legacy refunds and net payments stay with their own child instead of repeating group totals");
            }
            Check(render("family-legacy-child-adjustments",combined)==1,"Legacy child adjustments render on the continuous family receipt");
        }
    }
}
