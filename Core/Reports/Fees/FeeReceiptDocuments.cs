namespace FMSC_SMS.Core;

/// <summary>Loads saved collections and routes them to the matching receipt report.</summary>
// BD-18/19: reprint saved participants, visit lines, plans and balances; never substitute today's family or fees.
// A fee-change acknowledgement records changes without receiving cash or applying credit.
internal static class FeeReceiptDocuments
{
    internal static async Task<bool> TryShowAsync(Form? owner,InstitutionProfile.PrintIdentity institution,string receiptNumber,int? selectedChild=null)
    {
        int user=CurrentUser.UserId;
        bool Current()=>owner?.IsDisposed!=true&&CurrentUser.UserId==user&&CurrentUser.HasPermission("Fees.View");
        if(!Current())return true;
        using var result=await DatabaseHelper.QueryUserSetAsync("GetCollectionReceipt",DatabaseHelper.P("@ReceiptNumber",receiptNumber));
        if(!Current())return true;
        if(result.Tables.Count<3 || result.Tables[0].Rows.Count==0)return false;
        bool complete=IsCompleteSnapshot(result.Tables[0].Rows[0]);
        await ReadChildCashReceivedAsync(receiptNumber,result.Tables[0].Rows[0],result.Tables[1]);
        if(!Current())return true;
        if(complete&&FeeChangeReport.IsAcknowledgement(result.Tables[0].Rows[0]))
        {
            using var previousPlans=await FeeChangeReport.ReadPreviousPlansAsync(result.Tables[0].Rows[0]);
            using var changes=FeeChangeReport.BuildData(institution,result.Tables[0].Rows[0],result.Tables[1],result.Tables[2],result.Tables.Count>3?result.Tables[3]:null,previousPlans);
            await FeeDocumentParents.ApplyAsync(changes);
            if(Current())FeeChangeReport.Show(owner,changes);
            return true;
        }
        int? child=selectedChild ?? (complete&&result.Tables[1].Rows.Count>1?FeeReceiptCopyDialog.Choose(owner,result.Tables[1]):0);
        if(child is null||!Current())return true;
        // Annotate the saved header before Extract clones it for a child reprint.
        // Both the full family print and its individual child copy must disclose
        // the same limitation of an incomplete historical snapshot.
        ApplyLegacyReceiptNotice(result.Tables[0].Rows[0]);
        using var childCopy=child>0?FeeReceiptChildCopy.Extract(result,child.Value):null;
        var document=childCopy??result;
        using var printableLines=document.Tables[2].Copy();
        await FeeCarryForwardLabels.ApplyAsync(printableLines,receiptNumber);
        // Issued documents never acquire today's fees, concessions or balances.
        using var data=BuildData(institution,document.Tables[0].Rows[0],document.Tables[1],printableLines);
        await FeeDocumentParents.ApplyAsync(data);
        if(!Current())return true;
        Show(owner,data);
        return true;
    }

    internal static bool IsCompleteSnapshot(DataRow header)=>header.Table.Columns.Contains("SnapshotVersion")
        && !header.IsNull("SnapshotVersion") && Convert.ToInt32(header["SnapshotVersion"])>=2;

    internal static void ApplyLegacyReceiptNotice(DataRow header)
    {
        if(IsCompleteSnapshot(header))return;
        const string notice="Legacy receipt: saved fee details may exclude earlier discounts or waivers. See the account statement.";
        string current=Convert.ToString(header["ReceiptStatus"])??"";
        if(!current.Contains(notice,StringComparison.Ordinal))
            header["ReceiptStatus"]=(current+" "+notice).Trim();
    }

    internal static void AddAgreementNotes(DataTable document,DataTable agreements,DataTable students)
    {
        bool family=document.Columns.Contains("ReceiptStudentKey");
        foreach(var child in agreements.AsEnumerable().GroupBy(row=>Convert.ToInt32(row["AdmissionId"])))
        {
            // Grouped receipt student keys are positional. Admission references
            // come from the saved student block, never today's family membership.
            string reference=Convert.ToString(students.AsEnumerable().First(row=>Convert.ToInt32(row["AdmissionId"])==child.Key)["AdmissionNumber"])??"";
            var template=family?document.AsEnumerable().FirstOrDefault(row=>Convert.ToString(row["ChildAdmissionNumberText"])==reference)
                :document.Rows.Cast<DataRow>().FirstOrDefault();
            if(template is null)continue;
            if(family)foreach(var row in document.AsEnumerable().Where(row=>Convert.ToString(row["ChildAdmissionNumberText"])==reference))row["ShowChildDocument"]=true;
            foreach(var agreement in child)
            {
                var row=document.NewRow();row.ItemArray=(object[])template.ItemArray.Clone();
                string Text(string field)=>Convert.ToString(agreement[field])??"";
                string Period(string field)=>agreement.IsNull(field)?"":Convert.ToDateTime(agreement[field]).ToString("MMM yyyy");
                string dates=Period("EffectiveFrom");if(!agreement.IsNull("EffectiveTo"))dates+=" through "+Period("EffectiveTo");
                row["FeeDescription"]=$"Fee plan: {Text("FeeTypeName")} — Rs. {Convert.ToDecimal(agreement["Amount"]):N0}; discount/waived Rs. {Convert.ToDecimal(agreement["DiscountAmount"]):N0}; {Text("Frequency")}"+
                    (dates.Length>0?"; "+dates:"")+(!agreement.IsNull("CancelledAt")?"; Cancelled":"");
                row["Amount"]=0m;row["FeePeriod"]="Plan";
                if(family){row["FamilyRowKind"]="Fee";row["FamilyAmountText"]="";row["ShowChildDocument"]=true;}
                document.Rows.Add(row);
            }
        }
    }

    internal static async Task ShowFeeChangesAsync(Form? owner,int admissionId,int[] students,Func<bool>? isCurrent=null)
    {
        int user=CurrentUser.UserId;
        bool Current()=>owner?.IsDisposed!=true&&CurrentUser.UserId==user&&CurrentUser.HasPermission("Fees.Collect")&&(isCurrent?.Invoke()??true);
        if(!Current())return;
        var institution=await InstitutionProfile.GetPrintIdentityAsync();
        if(!Current())return;
        if(string.IsNullOrWhiteSpace(institution.Name))throw new InvalidOperationException("Check School Profile before printing fee changes.");
        using var issued=await DatabaseHelper.QuerySqlAsync("""
            SET XACT_ABORT ON;
            BEGIN TRY
             BEGIN TRAN;
             DECLARE @Lock int;
             EXEC @Lock=sys.sp_getapplock @Resource=N'FMSC.MonthlyBilling',@LockMode=N'Exclusive',@LockOwner=N'Transaction',@LockTimeout=10000;
             IF @Lock<0 THROW 54901,'Billing is busy. Try again.',1;
             EXEC dbo.IssueCollectionReceipt @UserId,@Admission,@Date,N'Fee changes',N'Parent fee-change acknowledgement',
              @Ids,N'[]',N'[]',0,0,0,1;
             COMMIT;
            END TRY
            BEGIN CATCH IF @@TRANCOUNT>0 ROLLBACK; THROW; END CATCH;
            """,DatabaseHelper.Int("@UserId",CurrentUser.UserId),DatabaseHelper.Int("@Admission",admissionId),
            DatabaseHelper.P("@Date",DateTime.Now),DatabaseHelper.P("@Ids",System.Text.Json.JsonSerializer.Serialize(students)));
        if(Current())await TryShowAsync(owner,institution,Convert.ToString(issued.Rows[0]["ReceiptNumber"])!);
    }

    // A concession saved after collection changes live dues without changing the
    // original receipt. Reflect that change only in the latest printout, and only
    // for its saved fee lines; other charges and payments are not discounts.
    internal static async Task RefreshChargeConcessionsAsync(DataRow header,DataTable students,DataTable lines,bool latest,
        Func<string,Task<DataTable>>? loadChanges=null)
    {
        if(!latest)return;
        loadChanges ??= QueryChargeConcessionChangesAsync;
        using var changes=await loadChanges(Convert.ToString(header["ReceiptNumber"])??"");
        var byCharge=changes.AsEnumerable().ToDictionary(row=>Convert.ToInt64(row["ChargeId"]));
        foreach(DataRow student in students.Rows)
        {
            decimal difference=0m;
            foreach(var line in lines.AsEnumerable().Where(row=>Convert.ToInt32(row["AdmissionId"])==Convert.ToInt32(student["AdmissionId"])))
            {
                if(!byCharge.TryGetValue(Convert.ToInt64(line["ChargeId"]),out var change))continue;
                decimal original=Convert.ToDecimal(line["Discount"]);
                decimal discount=original+Convert.ToDecimal(change["DiscountChange"]);
                // AmountBefore already excluded concessions predating this
                // collection. Restore that part of the fee if one is removed.
                if(discount<0m)line["AmountBefore"]=Convert.ToDecimal(line["AmountBefore"])-discount;
                line["Discount"]=Math.Max(0m,discount);
                if(Convert.ToDecimal(line["Discount"])!=original)
                {
                    // The refresh query supplies the new amount, not its type.
                    // Do not reuse an older discount/waiver classification.
                    if(!lines.Columns.Contains("ReceiptConcessionTypeUnknown"))
                        lines.Columns.Add("ReceiptConcessionTypeUnknown",typeof(bool)).DefaultValue=false;
                    line["ReceiptConcessionTypeUnknown"]=true;
                }
                difference+=Convert.ToDecimal(line["Discount"])-original;
            }
            student["DiscountApplied"]=Convert.ToDecimal(student["DiscountApplied"])+difference;
            header["DiscountApplied"]=Convert.ToDecimal(header["DiscountApplied"])+difference;
        }
    }

    private static Task<DataTable> QueryChargeConcessionChangesAsync(string receiptNumber) => DatabaseHelper.QuerySqlAsync("""
        EXEC dbo.RequirePermission @UserId,N'Fees.View';
        SELECT L.ChargeId,
            COALESCE(CurrentTerms.NewDiscount,B.DiscountAmount)
                -COALESCE(IssueTerms.NewDiscount,B.GrossAmount-FirstTerms.PreviousNet,B.DiscountAmount) DiscountChange
        FROM dbo.CollectionReceipts R
        CROSS APPLY OPENJSON(R.SnapshotJson,'$.Lines') WITH(ChargeId bigint) L
        JOIN dbo.BillingCharges B ON B.BillingChargeId=L.ChargeId
        OUTER APPLY(SELECT TOP(1) C.PreviousNet FROM dbo.FeeBillCorrections C
            WHERE C.BillingChargeId=L.ChargeId ORDER BY C.FeeBillCorrectionId) FirstTerms
        OUTER APPLY(SELECT TOP(1) C.NewDiscount FROM dbo.FeeBillCorrections C
            WHERE C.BillingChargeId=L.ChargeId AND C.CreatedAt<=R.CreatedAt
            ORDER BY C.FeeBillCorrectionId DESC) IssueTerms
        OUTER APPLY(SELECT TOP(1) C.NewDiscount FROM dbo.FeeBillCorrections C
            WHERE C.BillingChargeId=L.ChargeId ORDER BY C.FeeBillCorrectionId DESC) CurrentTerms
        WHERE R.ReceiptNumber=@ReceiptNumber;
        """,DatabaseHelper.Int("@UserId",CurrentUser.UserId),DatabaseHelper.P("@ReceiptNumber",receiptNumber));

    // Unallocated children have no saved receipt lines. Show their current dues
    // alongside the current balance, without changing the saved payment snapshot.
    internal static async Task AddUnpaidChildFeesAsync(DataRow header,DataTable students,DataTable lines,
        Func<int,Task<DataTable>>? loadFees=null)
    {
        if(students.Rows.Count<2)return;
        loadFees ??= QueryUnpaidFeesAsync;
        foreach(DataRow student in students.Rows)
        {
            int id=Convert.ToInt32(student["AdmissionId"]);
            if(lines.AsEnumerable().Any(row=>Convert.ToInt32(row["AdmissionId"])==id))continue;
            using var fees=await loadFees(id);
            foreach(DataRow fee in fees.Rows)
            {
                var row=lines.NewRow();
                foreach(DataColumn column in lines.Columns)
                    if(fees.Columns.Contains(column.ColumnName))row[column.ColumnName]=fee[column.ColumnName];
                lines.Rows.Add(row);
            }
            decimal discount=fees.AsEnumerable().Sum(row=>FamilyFeeDocumentPresentation.Money(row,"Discount"));
            if(discount!=0m)
            {
                student["DiscountApplied"]=Convert.ToDecimal(student["DiscountApplied"])+discount;
                header["DiscountApplied"]=Convert.ToDecimal(header["DiscountApplied"])+discount;
            }
        }
    }

    private static Task<DataTable> QueryUnpaidFeesAsync(int admissionId) => DatabaseHelper.QuerySqlAsync("""
        EXEC dbo.RequirePermission @UserId,N'Fees.View';
        SELECT B.BillingChargeId ChargeId,B.AdmissionId,B.EnrollmentId,
            dbo.GetFeeChargeBalance(B.BillingChargeId)+B.DiscountAmount AmountBefore,
            B.DiscountAmount Discount,CONVERT(decimal(12,2),0) Cash,CONVERT(decimal(12,2),0) Credit,
            F.FeeTypeName,COALESCE(B.BillingMonth,B.ChargeDate) FeePeriod,
            C.ClassName,SEC.SectionName,CA.CampusName,dbo.GetFeeChargeBalance(B.BillingChargeId) BalanceAfter
        FROM dbo.BillingCharges B
        JOIN dbo.FeeTypes F ON F.FeeTypeId=B.FeeTypeId
        JOIN dbo.Enrollments E ON E.EnrollmentId=B.EnrollmentId
        JOIN dbo.CampusClassSections CCS ON CCS.ClassSectionId=E.ClassSectionId
        JOIN dbo.Classes C ON C.ClassId=CCS.ClassId
        JOIN dbo.Sections SEC ON SEC.SectionId=CCS.SectionId
        JOIN dbo.Campuses CA ON CA.CampusId=CCS.CampusId
        WHERE B.AdmissionId=@AdmissionId AND B.Status<>N'Cancelled' AND dbo.GetFeeChargeBalance(B.BillingChargeId)>0
        ORDER BY COALESCE(B.BillingMonth,B.ChargeDate),F.DisplayOrder,B.BillingChargeId;
        """,DatabaseHelper.Int("@UserId",CurrentUser.UserId),DatabaseHelper.Int("@AdmissionId",admissionId));

    // The collection snapshot stores each child's fee allocation. The linked
    // original payments identify who actually received any retained advance.
    internal static async Task ReadChildCashReceivedAsync(string receiptNumber,DataRow header,DataTable students)
    {
        using var payments=await DatabaseHelper.QuerySqlAsync("""
            EXEC dbo.RequirePermission @UserId,N'Fees.View';
            SELECT P.AdmissionId,SUM(P.TotalAmount) CashReceived
            FROM dbo.CollectionReceipts R
            JOIN dbo.CollectionReceiptPayments L ON L.CollectionReceiptId=R.CollectionReceiptId
            JOIN dbo.FeePayments P ON P.FeePaymentId=L.FeePaymentId
            WHERE R.ReceiptNumber=@ReceiptNumber AND P.IsCreditApplication=0
            GROUP BY P.AdmissionId;
            """,DatabaseHelper.Int("@UserId",CurrentUser.UserId),DatabaseHelper.P("@ReceiptNumber",receiptNumber));
        ApplyChildCashReceived(header,students,payments);
    }

    internal static void ApplyChildCashReceived(DataRow header,DataTable students,DataTable payments)
    {
        var received=payments.AsEnumerable().ToDictionary(row=>Convert.ToInt32(row["AdmissionId"]),row=>Convert.ToDecimal(row["CashReceived"]));
        var ids=students.AsEnumerable().Select(row=>Convert.ToInt32(row["AdmissionId"])).ToHashSet();
        if(received.Keys.Any(id=>!ids.Contains(id))||received.Values.Sum()!=Convert.ToDecimal(header["CashReceived"]))
            throw new InvalidOperationException("The receipt's original payments could not be matched to its children. Reload before printing.");
        if(!students.Columns.Contains("CashReceived"))students.Columns.Add("CashReceived",typeof(decimal));
        foreach(DataRow student in students.Rows)
        {
            decimal amount=received.GetValueOrDefault(Convert.ToInt32(student["AdmissionId"]));
            if(amount<Convert.ToDecimal(student["CashApplied"]))
                throw new InvalidOperationException("The child's received payment could not be confirmed. Reload before printing.");
            student["CashReceived"]=amount;
        }
    }

    // Latest collections use live dues. Historical collections retain their
    // recorded child balances and never acquire charges from later months.
    internal static async Task<bool> RefreshBalancesAsync(DataRow header,DataTable students,Func<int,Task<decimal>>? loadBalance=null,
        Func<string,Task<FeeReceiptQueries.ReceiptBalanceState>>? loadState=null,string? receiptNumber=null)
    {
        loadBalance ??= admissionId=>FeeReceiptQueries.QueryCurrentBalanceAsync(admissionId);
        loadState ??= receipt=>FeeReceiptQueries.QueryBalanceStateAsync(receipt);
        var state=await loadState(receiptNumber??Convert.ToString(header["ReceiptNumber"])??"");
        var balances=new Dictionary<int,decimal>();
        foreach(DataRow student in students.Rows)
        {
            int admissionId=Convert.ToInt32(student["AdmissionId"]);
            if(!balances.TryGetValue(admissionId,out decimal balance))
            {
                if(state.IsLatest)balance=await loadBalance(admissionId);
                else if(!state.RecordedBalances.TryGetValue(admissionId,out balance))
                    throw new InvalidOperationException("This older receipt has no recorded outstanding balance. Its original figure cannot be confirmed; today's balance has not been substituted.");
                balances[admissionId]=balance;
            }
            student["BalanceRemaining"]=balance;
        }
        header["BalanceRemaining"]=balances.Values.Sum();
        return state.IsLatest;
    }

    internal static DataTable BuildData(InstitutionProfile.PrintIdentity institution,DataRow header,DataTable students,DataTable lines) =>
        students.Rows.Count>1?FamilyReceiptReport.BuildData(institution,header,students,lines)
            :StudentReceiptReport.BuildData(institution,header,students,lines);

    internal static void Show(Form? owner,DataTable data)
    {
        if(data.Columns.Contains("FamilyRowKind"))FamilyReceiptReport.Show(owner,data);
        else StudentReceiptReport.Show(owner,data);
    }
}
