using Microsoft.Data.SqlClient;

// Runs only after the caller provisions and validates a disposable synthetic fixture.
internal static class CollectionCreditChecks
{
    internal static async Task Run(Fixture f, Report report)
    {
        // Bind the reporting projection to the actually migrated SQL schema;
        // TOP (0) checks column availability without reading real school data.
        // This is the mandatory live-SQL counterpart to ReportReadChecks' fast
        // structural contract and replaces the pinned historic DACPAC probe.
        report.Check(Sql.Count("SELECT COUNT(*) FROM (SELECT TOP (0) AdmissionId, Amount, CreatedAt FROM dbo.StudentCreditEntries) AS CurrentLedger")==0,
            "Current migrated credit ledger exposes admission, amount and activity timestamp");
        var student = Sql.Proc("AdmitStudent", f.AdmissionArgs("Credit settlement example", current:true)).Rows[0];
        int admission=Convert.ToInt32(student["AdmissionId"]), enrollment=Convert.ToInt32(student["EnrollmentId"]);
        var sibling = Sql.Proc("AdmitStudent", f.AdmissionArgs("Sibling settlement example", admission, current:true)).Rows[0];
        int siblingId=Convert.ToInt32(sibling["AdmissionId"]), siblingEnrollment=Convert.ToInt32(sibling["EnrollmentId"]);
        decimal Credit(int id) => Sql.Money("SELECT COALESCE(SUM(Amount),0) FROM dbo.StudentCreditEntries WHERE AdmissionId=@Id",Sql.P("@Id",id));
        decimal Due(long bill) => Sql.Money("SELECT dbo.GetFeeChargeBalance(@Bill)",Sql.P("@Bill",bill));
        // Optional remarks must work at the SQL boundary, including request replay.
        Guid optionalRequest=Guid.NewGuid();
        SqlParameter[] OptionalDeposit()=>new[]{Sql.P("@UserId",f.Admin),Sql.P("@AdmissionId",admission),
            Sql.P("@Action","Deposit"),Sql.P("@Amount",100m),Sql.P("@RequestId",optionalRequest)};
        Sql.Proc("ProcessStudentCredit",OptionalDeposit());
        Sql.Proc("ProcessStudentCredit",OptionalDeposit());
        report.Check(Credit(admission)==100m && Sql.Count("SELECT COUNT(*) FROM dbo.StudentCreditEntries WHERE RequestId=@Request",Sql.P("@Request",optionalRequest))==1,
            "A deposit without remarks replays once without duplicating money");
        Sql.Proc("ProcessStudentCredit",Sql.P("@UserId",f.Admin),Sql.P("@AdmissionId",admission),Sql.P("@Action","Transfer"),
            Sql.P("@Amount",100m),Sql.P("@RequestId",Guid.NewGuid()),Sql.P("@Reason",null),Sql.P("@TargetAdmissionId",siblingId));
        report.Check(Credit(admission)==0m && Credit(siblingId)==100m,"A sibling transfer accepts NULL remarks and conserves credit");
        Sql.Proc("ProcessStudentCredit",Sql.P("@UserId",f.Admin),Sql.P("@AdmissionId",siblingId),Sql.P("@Action","Refund"),
            Sql.P("@Amount",100m),Sql.P("@RequestId",Guid.NewGuid()),Sql.P("@Reason","   "));
        report.Check(Credit(siblingId)==0m && Sql.Count("SELECT COUNT(*) FROM dbo.StudentCreditEntries WHERE AdmissionId IN(@A,@B) AND Reason<>N''",Sql.P("@A",admission),Sql.P("@B",siblingId))==0,
            "A refund accepts blank remarks and optional notes remain empty in the ledger");
        void Deposit(int id, decimal amount) => Sql.Proc("ProcessStudentCredit", Sql.P("@UserId",f.Admin),
            Sql.P("@AdmissionId",id),Sql.P("@Action","Deposit"),Sql.P("@Amount",amount),
            Sql.P("@RequestId",Guid.NewGuid()),Sql.P("@Reason","Synthetic credit check"));
        long Bill(int ownerEnrollment,int fee) => Convert.ToInt64(Sql.Scalar("SELECT TOP(1) BillingChargeId FROM dbo.BillingCharges WHERE EnrollmentId=@E AND FeeTypeId=@F ORDER BY BillingChargeId",Sql.P("@E",ownerEnrollment),Sql.P("@F",fee)));
        CollectionDiscountWriter.CreditEdit Plan(long bill,int owner,int ownerEnrollment,decimal amount) => new(bill,owner,ownerEnrollment,amount,Due(bill),Credit(owner));
        Task<DataTable> Save(string? procedure,CollectionDiscountWriter.CreditEdit[] credits,params SqlParameter[] args)
        {
            var planned=new Dictionary<long,(int Owner,int Enrollment,decimal Cash,decimal Discount,decimal Credit)>();
            foreach(var credit in credits) planned[credit.ChargeId]=(credit.AdmissionId,credit.EnrollmentId,0,0,credit.Amount);
            if(args.FirstOrDefault(arg=>arg.ParameterName=="@Allocations")?.Value is DataTable allocations)
                foreach(DataRow row in allocations.Rows)
                {
                    long bill=Convert.ToInt64(row["BillingChargeId"]);decimal value=Convert.ToDecimal(row["Amount"]);
                    if(!planned.TryGetValue(bill,out var plan)) plan=(admission,enrollment,0,0,0);
                    if(allocations.Columns.Contains("AdmissionId")) {plan.Owner=Convert.ToInt32(row["AdmissionId"]);plan.Enrollment=Convert.ToInt32(row["EnrollmentId"]);}
                    if(value>0) plan.Cash+=value;else plan.Discount-=value;
                    planned[bill]=plan;
                }
            decimal received=Convert.ToDecimal(args.FirstOrDefault(arg=>arg.ParameterName is "@ReceivedAmount" or "@Amount")?.Value??0m);
            var lines=planned.Select(pair=>new CollectionDiscountWriter.ReceiptLine(pair.Key,pair.Value.Owner,pair.Value.Enrollment,Due(pair.Key),pair.Value.Discount,pair.Value.Cash,pair.Value.Credit)).ToArray();
            var receipt=new CollectionDiscountWriter.ReceiptDraft(admission,planned.Values.Select(value=>value.Owner).Append(admission).Distinct().ToArray(),
                DateTime.Today,"Cash","Synthetic receipt",received,lines.Sum(line=>line.Discount),lines);
            return CollectionDiscountWriter.CollectWithReceiptAsync(procedure,Array.Empty<CollectionDiscountWriter.Edit>(),credits,DateTime.Today,"Synthetic credit settlement",receipt,args);
        }
        SqlParameter[] Cash(long bill,decimal received,decimal discount=0) => new[] {
            Sql.P("@AdmissionId",admission),Sql.P("@EnrollmentId",enrollment),Sql.P("@TransactionType","Collection"),
            Sql.P("@Amount",received),Sql.P("@TransactionDate",DateTime.Today),Sql.P("@PaymentMethod","Cash"),
            new SqlParameter("@FeePaymentId",SqlDbType.Int) {Value=DBNull.Value},new SqlParameter("@BillingChargeId",SqlDbType.BigInt) {Value=DBNull.Value},
            Sql.P("@ReferenceNumber",""),Sql.P("@Remarks","Synthetic mixed settlement"),
            Sql.TvpParameter("@Allocations","FeePaymentAllocationType",Tvp.FeeAllocations(discount>0 ? new[] {(bill,received),(bill,-discount)} : new[] {(bill,received)}))
        };
        long tuition=Bill(enrollment,f.Tuition), annual=Bill(enrollment,f.AdmissionFee), siblingAnnual=Bill(siblingEnrollment,f.AdmissionFee);
        Deposit(admission,8750); Deposit(siblingId,1000);
        decimal beforeCredit=Credit(admission); decimal beforeDue=Due(tuition);
        string backdatedCreditJson=System.Text.Json.JsonSerializer.Serialize(new[] {
            new { ChargeId=tuition, AdmissionId=admission, EnrollmentId=enrollment, Amount=100m,
                ExpectedBalance=beforeDue, ExpectedCredit=beforeCredit }
        });
        // This isolated direct-SQL boundary test uses a raw batch, whereas the
        // application uses an RPC command. SQL Server requires these session SET
        // options for tables with filtered/computed indexes; without them error
        // 1934 obscures the intended date validation.
        const string creditBoundaryBatch = """
            SET ANSI_NULLS ON; SET QUOTED_IDENTIFIER ON; SET ANSI_PADDING ON;
            SET ANSI_WARNINGS ON; SET ARITHABORT ON;
            SET CONCAT_NULL_YIELDS_NULL ON; SET NUMERIC_ROUNDABORT OFF;
            BEGIN TRAN;
            EXEC dbo.ApplyCollectionCredit @UserId=@Actor,@TransactionDate=@When,@CreditsJson=@Plan;
            COMMIT;
            """;
        await report.Refused("Credit-only collection cannot precede enrollment",()=>Task.Run(()=>{
            Sql.Query(creditBoundaryBatch,
                Sql.P("@Actor",f.Admin),Sql.P("@When",DateTime.Today.AddYears(-10)),Sql.P("@Plan",backdatedCreditJson));
        }),54902);
        report.Check(Credit(admission)==beforeCredit && Due(tuition)==beforeDue,
            "Rejected pre-enrollment credit date changes no account or bill");
        await report.Refused("Advance is not available for a backdated collection",()=>Task.Run(()=>{
            Sql.Query(creditBoundaryBatch,
                Sql.P("@Actor",f.Admin),Sql.P("@When",DateTime.Today.AddDays(-1)),Sql.P("@Plan",backdatedCreditJson));
        }),54902);
        report.Check(Credit(admission)==beforeCredit && Due(tuition)==beforeDue,
            "Backdated credit refusal preserves the account and existing bill");
        await report.Refused("Changed credit is refused before settlement",()=>Save(null,new[] {Plan(tuition,admission,enrollment,100) with { ExpectedCredit=beforeCredit+1 }}),53104);
        report.Check(Credit(admission)==beforeCredit && Due(tuition)==beforeDue,"Stale credit preview changes neither credit nor bills");
        await report.Refused("Sibling credit cannot settle another child's bill",()=>Save(null,new[] {Plan(tuition,siblingId,siblingEnrollment,100)}),53104);
        report.Check(Credit(siblingId)==1000 && Due(tuition)==beforeDue,"Sibling ownership refusal preserves both accounts");
        int payments=Sql.Count("SELECT COUNT(*) FROM dbo.FeePayments");
        bool refused=false;
        try { await Save("ProcessFeeTransaction",new[] {Plan(tuition,admission,enrollment,1000)},Cash(tuition,10000)); }
        catch(SqlException) { refused=true; }
        report.Check(refused && Credit(admission)==beforeCredit && Due(tuition)==beforeDue && Sql.Count("SELECT COUNT(*) FROM dbo.FeePayments")==payments,
            "A rejected cash payment rolls back its preceding credit application and ledger entries");
        await Save("ProcessFeeTransaction",new[] {Plan(tuition,admission,enrollment,1000)},Cash(tuition,3500,500));
        report.Check(Due(tuition)==0 && Credit(admission)==7750,"Cash 3500, credit 1000 and discount 500 settle a 5000 bill exactly");
        report.Check(Sql.Money("SELECT SUM(TotalAmount) FROM dbo.FeePayments WHERE AdmissionId=@Id AND IsCreditApplication=0 AND FeePaymentId NOT IN(SELECT FeePaymentId FROM dbo.StudentCreditEntries WHERE FeePaymentId IS NOT NULL)",Sql.P("@Id",admission))==3500,
            "Using credit and discount does not inflate collected cash");
        decimal beforeReceiptFailure=Credit(admission);int receiptsBeforeFailure=Sql.Count("SELECT COUNT(*) FROM dbo.CollectionReceipts");
        var badDraft=new CollectionDiscountWriter.ReceiptDraft(admission,new[]{admission},DateTime.Today,"Cash","Invalid synthetic receipt",0m,0m,
            new[]{new CollectionDiscountWriter.ReceiptLine(annual,admission,enrollment,Due(annual)+1,0m,0m,100m)});
        await report.Refused("An inconsistent receipt prevents the collection from committing",()=>CollectionDiscountWriter.CollectWithReceiptAsync(null,
            Array.Empty<CollectionDiscountWriter.Edit>(),new[]{Plan(annual,admission,enrollment,100)},DateTime.Today,"Synthetic receipt rollback",badDraft),54930);
        report.Check(Credit(admission)==beforeReceiptFailure && Due(annual)==2000m && Sql.Count("SELECT COUNT(*) FROM dbo.CollectionReceipts")==receiptsBeforeFailure,
            "Receipt validation failure rolls back credit, allocations and document creation together");
        var family=await Save("ProcessFamilyFeeTransaction",new[] {Plan(annual,admission,enrollment,500),Plan(siblingAnnual,siblingId,siblingEnrollment,500)},
            Sql.P("@TransactionType","Collection"),Sql.P("@TransactionDate",DateTime.Today),Sql.P("@PaymentMethod","Cash"),Sql.P("@ReferenceNumber",""),Sql.P("@Remarks","Synthetic family settlement"),
            Sql.P("@ReceivedAmount",3000),Sql.P("@CreditAdmissionId",admission),Sql.P("@CreditRequestId",Guid.NewGuid()),Sql.P("@IncludedStudentsJson",System.Text.Json.JsonSerializer.Serialize(new[] {admission,siblingId})),
            Sql.TvpParameter("@Allocations","FamilyFeeAllocationType",Tvp.FamilyAllocations(new[] {(admission,enrollment,(IEnumerable<(long,decimal)>)new[] {(annual,1500m)}),(siblingId,siblingEnrollment,(IEnumerable<(long,decimal)>)new[] {(siblingAnnual,1500m)})})));
        report.Check(Due(annual)==0 && Due(siblingAnnual)==0 && Credit(siblingId)==500,"Family save uses each child's own credit and settles both bills");
        report.Check(family.Rows.Count==2 && family.AsEnumerable().Select(row=>Convert.ToString(row["ReceiptNumber"])).Distinct().Count()==1,"Family cash still has one shared receipt; credit remains a separate noncash movement");
        string savedReceipt=Convert.ToString(family.Rows[0]["ReceiptNumber"])!;
        string snapshot=Convert.ToString(Sql.Scalar("SELECT SnapshotJson FROM dbo.CollectionReceipts WHERE ReceiptNumber=@Receipt",Sql.P("@Receipt",savedReceipt)))!;
        var month=DateTime.Today.AddMonths(1); month=new DateTime(month.Year,month.Month,1);
        if (month.Year==DateTime.Today.Year)
        {
            Sql.Proc("PostSelectedMonthlyFees",Sql.P("@UserId",f.Admin),Sql.P("@AcademicYearId",f.NextYear),Sql.P("@BillingMonth",month),
                Sql.TvpParameter("@Selection","PostingSelectionType",Tvp.PostingSelections(new[] {(enrollment,f.Tuition,month)})),Sql.P("@Reason","Synthetic advance bill"));
            long future=Convert.ToInt64(Sql.Scalar("SELECT BillingChargeId FROM dbo.BillingCharges WHERE EnrollmentId=@E AND FeeTypeId=@F AND BillingMonth=@M",Sql.P("@E",enrollment),Sql.P("@F",f.Tuition),Sql.P("@M",month)));
            decimal expectedRemaining = Credit(admission)-5000;
            await Save(null,new[] {Plan(future,admission,enrollment,5000)});
            report.Check(Due(future)==0 && Credit(admission)==expectedRemaining,"Credit-only save settles a posted future bill and preserves the exact remainder");
        }
        report.Check(Sql.Count("SELECT COUNT(*) FROM dbo.CollectionReceipts")>=3,"Cash, family and credit-only saves issue durable collection receipts");
        report.Check(Sql.Count("SELECT COUNT(*) FROM dbo.CollectionReceiptPayments L JOIN dbo.FeePayments P ON P.FeePaymentId=L.FeePaymentId WHERE P.IsCreditApplication=1")>=3,
            "Credit applications are linked to their printable settlement instead of being orphan print references");
        report.Check(snapshot==Convert.ToString(Sql.Scalar("SELECT SnapshotJson FROM dbo.CollectionReceipts WHERE ReceiptNumber=@Receipt",Sql.P("@Receipt",savedReceipt))),
            "Later credit use does not rewrite an earlier receipt's issue-time balances");
        decimal siblingCreditBefore=Credit(siblingId);
        int paymentsBeforeCheck=Sql.Count("SELECT COUNT(*) FROM dbo.FeePayments");
        Sql.Query("BEGIN TRAN;EXEC dbo.ApplyStudentCredit_Internal @Student,@UserId;COMMIT;",Sql.P("@Student",siblingId),Sql.P("@UserId",f.Admin));
        report.Check(Credit(siblingId)==siblingCreditBefore && Sql.Count("SELECT COUNT(*) FROM dbo.FeePayments")==paymentsBeforeCheck,
            "Background credit compatibility entry point cannot spend held advance or create a payment");
        using(var connection=DatabaseHelper.GetConnection())
        using(var command=new SqlCommand("dbo.GetCollectionReceipt",connection) {CommandType=CommandType.StoredProcedure})
        using(var adapter=new SqlDataAdapter(command))
        using(var print=new DataSet())
        {
            command.Parameters.AddRange(new[]{Sql.P("@UserId",f.Admin),Sql.P("@ReceiptNumber",savedReceipt)});adapter.Fill(print);
            // Since migration 56 the reader returns four result sets:
            // receipt, students, visit lines and saved fee agreements.
            report.Check(print.Tables.Count==4 && print.Tables[0].Rows.Count==1
                && Convert.ToDecimal(print.Tables[0].Rows[0]["CashReceived"])==3000m
                && Convert.ToDecimal(print.Tables[0].Rows[0]["CreditApplied"])==1000m
                && Convert.ToInt32(print.Tables[0].Rows[0]["SnapshotVersion"])>=3,
                $"Explicit fee collection returns a durable cash-plus-credit receipt and four saved document result sets (sets={print.Tables.Count}).");
        }
        report.Check(Sql.Count("SELECT COUNT(*) FROM dbo.FeePayments")==paymentsBeforeCheck,"Reading a receipt never posts another payment or applies credit again");
        report.Check(CollectionRecoveryJournal.ReadPending() is null,"Confirmed and refused transactions clear the recovery intent");
    }
}
