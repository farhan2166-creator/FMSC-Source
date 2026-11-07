using Microsoft.Data.SqlClient;

internal sealed class Lifecycle(Fixture f, Report r)
{
    private int student, sibling, originalEnrollment, historicalStudent, historicalEnrollment, newEnrollment;
    private string oldFinancialSnapshot = "";
    private DateTime paidDate => DateTime.UtcNow.Date;

    internal async Task Run()
    {
        bool a = await r.Stage("A: admission, official identities and opening bill", Admission);
        bool b = await r.Stage("B: class rates, monthly posting and duplicate protection", Posting, a);
        bool c = await r.Stage("C: partial/final collection and reconciliation", Collection, b);
        bool d = await r.Stage("D: manual workbook charge and collection", AdditionalCharge, c);
        bool e = await r.Stage("E: bulk promotion and original-year preservation", Promotion, d);
        bool next = await r.Stage("F: return confirmation and new-year fee/payment cycle", NextYear, e);
        await r.Stage("Negative: admission fields, permissions and whole-rupee boundary", AdmissionRefusals);
        await r.Stage("Recovery: committed adjustment, lost acknowledgement and exact retry", Recovery, d);
        await r.Stage("Recovery: pending collection blocks ambiguous resubmission", CollectionJournalGuard, next);
        await r.Stage("Negative: stale collection edit and unauthorized finance", FinanceRefusals, next);
        await r.Stage("G: complete two-year history and database invariants", FinalAudit, next);
    }

    private Task Admission()
    {
        var saved = Sql.Proc("AdmitStudent", f.AdmissionArgs());
        r.Check(saved.Rows.Count == 1, "Admission returns one authoritative student/enrollment pair.");
        student = Convert.ToInt32(saved.Rows[0]["AdmissionId"]); originalEnrollment = Convert.ToInt32(saved.Rows[0]["EnrollmentId"]);
        var details = Sql.Query("SELECT * FROM dbo.Students WHERE AdmissionId=@Id;", Sql.P("@Id", student)).Rows[0];
        r.Check(Convert.ToString(details["StudentName"]) == "Ayaan Example" && Convert.ToString(details["FatherName"]) == "Fictional Parent" &&
            (DateTime)details["DateOfBirth"] == f.BirthDate && Convert.ToString(details["Gender"]) == "Male" && details.IsNull("ContactNumber"), "Persisted admission details match the submitted fictional draft, including optional phone.");
        r.Check(!details.IsNull("AdmissionNumber") && !details.IsNull("RegistrationNumber") && Convert.ToString(details["Status"]) == "Active", "Official admission and registration identities were assigned to an active student.");
        r.Check(Sql.Count("SELECT COUNT(*) FROM dbo.Enrollments WHERE EnrollmentId=@Enrollment AND AdmissionId=@Student AND AcademicYearId=@Year AND ClassSectionId=@Placement AND BillingReady=1 AND PlacementConfirmedAt IS NOT NULL;",
            Sql.P("@Enrollment", originalEnrollment), Sql.P("@Student", student), Sql.P("@Year", f.NextYear), Sql.P("@Placement", f.Placement6)) == 1, "Confirmed current-year enrollment retains the exact year/campus/class/section.");
        r.Check(Sql.Money("SELECT SUM(NetAmount) FROM dbo.BillingCharges WHERE EnrollmentId=@Enrollment;", Sql.P("@Enrollment", originalEnrollment)) == 7000, "Opening tuition 5000 plus annual admission 2000 equals 7000.");
        r.Check(Sql.Count("SELECT COUNT(*) FROM dbo.StudentFeeAssignments WHERE EnrollmentId=@Enrollment AND CancelledAt IS NULL;", Sql.P("@Enrollment", originalEnrollment)) == 2, "Exactly two mandatory opening assignments were persisted.");
        var second = Sql.Proc("AdmitStudent", f.AdmissionArgs("Zara Example", student)); sibling = Convert.ToInt32(second.Rows[0]["AdmissionId"]);
        r.Check(Sql.Count("SELECT COUNT(DISTINCT FamilyId) FROM dbo.FamilyMembers WHERE AdmissionId IN(@First,@Second);", Sql.P("@First", student), Sql.P("@Second", sibling)) == 1 &&
            Sql.Count("SELECT COUNT(*) FROM dbo.FamilyMembers WHERE AdmissionId IN(@First,@Second);", Sql.P("@First", student), Sql.P("@Second", sibling)) == 2, "The two admitted siblings share one family with two membership rows.");
        r.Check(Sql.Count("SELECT COUNT(DISTINCT AdmissionNumber) FROM dbo.Students;") == 2 && Sql.Count("SELECT COUNT(DISTINCT RegistrationNumber) FROM dbo.Students;") == 2, "Sibling admission preserves unique official identities.");
        return Task.CompletedTask;
    }

    private Task Posting()
    {
        // The current month's opening charge was already posted by admission.
        // Trying that same month must not create a duplicate.
        var posted = Post(originalEnrollment, f.NextYear, f.MonthlyDate, f.Tuition);
        r.Check(Convert.ToInt32(posted.Rows[0]["ChargesCreated"]) == 0, "Posting the opening month again creates no duplicate charge.");
        r.Check(Sql.Count("SELECT COUNT(*) FROM dbo.BillingCharges WHERE EnrollmentId=@Enrollment AND AdmissionId=@Student AND FeeTypeId=@Fee AND BillingMonth=@Month AND ChargeDate=@Month AND NetAmount=5000 AND ChargeKind=N'Monthly';",
            Sql.P("@Enrollment", originalEnrollment), Sql.P("@Student", student), Sql.P("@Fee", f.Tuition), Sql.P("@Month", f.MonthlyDate)) == 1, "The posted charge has the exact fee, month, enrollment and amount.");
        r.Check(Convert.ToInt32(Post(originalEnrollment, f.NextYear, f.MonthlyDate, f.Tuition).Rows[0]["ChargesCreated"]) == 0, "Repeating monthly posting remains idempotent.");
        r.Check(Convert.ToInt32(Post(originalEnrollment, f.NextYear, f.MonthlyDate, f.Purchase).Rows[0]["ChargesCreated"]) == 0, "An unassigned/unconfigured fee is reported as not posted rather than silently invented.");
        r.Refused("Posting cannot assign a charge to the wrong academic year", () => { Post(originalEnrollment, f.SourceYear, f.MonthlyDate, f.Tuition); return Task.CompletedTask; }, 54744).GetAwaiter().GetResult();
        r.Check(Balance(originalEnrollment) == 7000, "Current-year opening charges reconcile to 7000 without a duplicate monthly fee.");
        return Task.CompletedTask;
    }

    private async Task Collection()
    {
        long first = Convert.ToInt64(Sql.Scalar("SELECT MIN(BillingChargeId) FROM dbo.BillingCharges WHERE EnrollmentId=@Id;", Sql.P("@Id", originalEnrollment)));
        int initial = PaymentCount(originalEnrollment);
        await r.Refused("Payment amount must equal its allocations", async () => await CollectExact(originalEnrollment, paidDate, 1000, new[] { (first, 999m) }), 54011);
        await r.Refused("An allocation cannot exceed its unpaid charge", async () => await CollectExact(originalEnrollment, paidDate, 9000, new[] { (first, 9000m) }), 54013);
        r.Check(PaymentCount(originalEnrollment) == initial && Balance(originalEnrollment) == 7000, "Invalid allocations roll back and retain all original dues.");
        var partial = await Collect(originalEnrollment, paidDate, 4000);
        r.Check(Convert.ToInt32(partial.Rows[0]["FeePaymentId"]) > 0 && !string.IsNullOrWhiteSpace(Convert.ToString(partial.Rows[0]["ReceiptNumber"])), "Partial collection returns a receipt and durable payment identity.");
        r.Check(Balance(originalEnrollment) == 3000, "Partial collection leaves exactly 3000 outstanding.");
        r.Check(Payments(originalEnrollment) == 4000 && Allocations(originalEnrollment) == 4000, "Partial payment total equals persisted allocations.");
        await Collect(originalEnrollment, paidDate, 3000);
        r.Check(Balance(originalEnrollment) == 0 && Payments(originalEnrollment) == 7000 && Allocations(originalEnrollment) == 7000, "Final collection settles the ledger and reconciles all 7000.");
        r.Check(Sql.Count("SELECT COUNT(*) FROM dbo.BillingCharges WHERE EnrollmentId=@Enrollment AND Status<>N'Paid';", Sql.P("@Enrollment", originalEnrollment)) == 0, "All original-year posted charges are Paid after settlement.");
        int count = PaymentCount(originalEnrollment);
        long paidCharge = Convert.ToInt64(Sql.Scalar("SELECT MIN(BillingChargeId) FROM dbo.BillingCharges WHERE EnrollmentId=@Enrollment;", Sql.P("@Enrollment", originalEnrollment)));
        await r.Refused("Replaying a settled collection cannot record another payment", async () => await CollectExact(originalEnrollment, paidDate, 4000, new[] { (paidCharge, 4000m) }), 54013);
        r.Check(PaymentCount(originalEnrollment) == count && Balance(originalEnrollment) == 0, "Rejected settled replay leaves payment count and balance unchanged.");
    }

    private async Task AdditionalCharge()
    {
        Guid request = Guid.NewGuid();
        SqlParameter[] Args(decimal discount = 200) => new[] { Sql.P("@UserId", f.Admin), Sql.P("@RequestId", request), Sql.P("@FeeTypeId", f.Purchase),
            Sql.P("@ChargeDate", paidDate), Sql.P("@Amount", 1200m), Sql.P("@DiscountAmount", discount), Sql.P("@Reason", "Fictional workbook"),
            Sql.TvpParameter("@Selection", "ManualChargeSelectionType", Tvp.ManualChargeSelection(new[] { originalEnrollment })) };
        int before = Sql.Count("SELECT COUNT(*) FROM dbo.BillingCharges WHERE EnrollmentId=@Id;", Sql.P("@Id", originalEnrollment));
        foreach (int excluded in new[] { f.AdmissionFee, f.Tuition })
            await r.Refused("Bulk additional posting refuses admission and tuition fees", () => {
                var args = Args(); args.Single(p => p.ParameterName == "@FeeTypeId").Value = excluded;
                Sql.Proc("PostAdditionalFee", args); return Task.CompletedTask;
            }, 53024);
        Sql.Query("UPDATE dbo.FeeTypes SET DefaultFrequency=N'Monthly' WHERE FeeTypeId=@Id;", Sql.P("@Id", f.Purchase));
        try
        {
            await r.Refused("Bulk additional posting refuses other recurring fee types", () => {
                Sql.Proc("PostAdditionalFee", Args()); return Task.CompletedTask;
            }, 53024);
        }
        finally { Sql.Query("UPDATE dbo.FeeTypes SET DefaultFrequency=N'As Needed' WHERE FeeTypeId=@Id;", Sql.P("@Id", f.Purchase)); }
        r.Check(Sql.Count("SELECT COUNT(*) FROM dbo.BillingCharges WHERE EnrollmentId=@Id;", Sql.P("@Id", originalEnrollment)) == before,
            "Rejected fee types leave existing charges unchanged");
        var posted = Sql.Proc("PostAdditionalFee", Args()); long charge = Convert.ToInt64(posted.Rows[0]["BillingChargeId"]);
        r.Check(Sql.Money("SELECT NetAmount FROM dbo.BillingCharges WHERE BillingChargeId=@Id AND AdmissionId=@Student AND EnrollmentId=@Enrollment;",
            Sql.P("@Id", charge), Sql.P("@Student", student), Sql.P("@Enrollment", originalEnrollment)) == 1000, "Manual charge persists gross 1200 less discount 200 against the intended enrollment.");
        r.Check(Convert.ToInt64(Sql.Proc("PostAdditionalFee", Args()).Rows[0]["BillingChargeId"]) == charge, "Exact manual request replay returns the original charge identity.");
        await r.Refused("A manual request ID cannot be reused with a different discount", () => { Sql.Proc("PostAdditionalFee", Args(100)); return Task.CompletedTask; }, 53026);
        await CollectExact(originalEnrollment, paidDate, 1000, new[] { (charge, 1000m) });
        r.Check(Balance(originalEnrollment) == 0 && Payments(originalEnrollment) == 8000 && Allocations(originalEnrollment) == 8000, "Manual purchase settles exactly 1000 and current-year collections total 8000.");
        oldFinancialSnapshot = FinancialSnapshot(originalEnrollment);
    }

    private async Task Promotion()
    {
        // Historical admissions retain their recorded placement but are deliberately
        // not billed when entered after the source academic year has ended.
        // Test the promotion/return path on a separate historical student so the
        // independently tested current-year collection is not misclassified.
        var archived = Sql.Proc("AdmitStudent", f.AdmissionArgs("Fictional Historical Pupil",current:false));
        historicalStudent=Convert.ToInt32(archived.Rows[0]["AdmissionId"]);
        historicalEnrollment=Convert.ToInt32(archived.Rows[0]["EnrollmentId"]);
        r.Check(Sql.Count("SELECT COUNT(*) FROM dbo.Enrollments WHERE EnrollmentId=@Id AND AcademicYearId=@Year AND BillingReady=0 AND PlacementConfirmedAt IS NOT NULL;",
            Sql.P("@Id",historicalEnrollment),Sql.P("@Year",f.SourceYear))==1 &&
            Sql.Count("SELECT COUNT(*) FROM dbo.BillingCharges WHERE EnrollmentId=@Id;",Sql.P("@Id",historicalEnrollment))==0,
            "A historical admission retains its verified source placement without inventing past fees.");
        string historicalFinancialSnapshot=FinancialSnapshot(historicalEnrollment);
        int before = Sql.Count("SELECT COUNT(*) FROM dbo.PromotionBatches;");
        await r.Refused("Same-year promotion is refused", () => { Promote(f.SourceYear, f.Placement7); return Task.CompletedTask; }, 52503);
        await r.Refused("An invalid destination section is refused", () => { Promote(f.NextYear, int.MaxValue); return Task.CompletedTask; }, 52516);
        Sql.Query("DELETE dbo.FeeConfigurations WHERE ClassId=@Class AND FeeTypeId=@Fee;", Sql.P("@Class", f.Class7), Sql.P("@Fee", f.Tuition));
        try { await r.Refused("Promotion requires all destination mandatory rates", () => { Promote(f.NextYear, f.Placement7); return Task.CompletedTask; }, 52519); }
        finally { f.Rate(f.Class7, f.Tuition, 6000, "Monthly"); }
        r.Check(Sql.Count("SELECT COUNT(*) FROM dbo.PromotionBatches;") == before &&
            Sql.Count("SELECT COUNT(*) FROM dbo.Enrollments WHERE AdmissionId=@Student;", Sql.P("@Student", historicalStudent)) == 1,
            "Refused promotions preserve the historical source placement and create no batch.");
        Promote(f.NextYear, f.Placement7);
        newEnrollment = Sql.Count("SELECT EnrollmentId FROM dbo.Enrollments WHERE AdmissionId=@Student AND AcademicYearId=@Year AND Status=N'Active';",
            Sql.P("@Student", historicalStudent), Sql.P("@Year", f.NextYear));
        r.Check(newEnrollment != historicalEnrollment && Sql.Count("SELECT COUNT(*) FROM dbo.Enrollments WHERE EnrollmentId=@Id AND Status=N'Promoted' AND LeavingDate=@Date;",
            Sql.P("@Id", historicalEnrollment), Sql.P("@Date", f.PromotionDate)) == 1,
            "Promotion preserves the historical source episode and creates a distinct destination enrollment.");
        r.Check(Sql.Count("SELECT COUNT(*) FROM dbo.Enrollments WHERE EnrollmentId=@Id AND ClassSectionId=@Placement AND BillingReady=0;",
            Sql.P("@Id", newEnrollment), Sql.P("@Placement", f.Placement7)) == 1,
            "Destination Class 7 correctly awaits return confirmation before billing.");
        r.Check(Sql.Count("SELECT COUNT(*) FROM dbo.PromotionBatchStudents WHERE AdmissionId=@Student AND OldEnrollmentId=@Old AND NewEnrollmentId=@New AND Decision=N'PROMOTE';",
            Sql.P("@Student", historicalStudent), Sql.P("@Old", historicalEnrollment), Sql.P("@New", newEnrollment)) == 1 &&
            Sql.Count("SELECT COUNT(*) FROM dbo.EnrollmentHistory WHERE AdmissionId=@Student AND ActionType=N'BULK_PROMOTE';",
                Sql.P("@Student", historicalStudent)) == 1, "One historical-student promotion and one movement record are saved.");
        await r.Refused("Duplicate promotion of the old enrollment is refused", () => { Promote(f.NextYear, f.Placement7); return Task.CompletedTask; }, 52521);
        r.Check(FinancialSnapshot(historicalEnrollment) == historicalFinancialSnapshot,
            "Promotion does not backfill or modify earlier-year historical finance.");
        r.Check(FinancialSnapshot(originalEnrollment) == oldFinancialSnapshot,
            "Promotion of an archived student leaves a different student's existing collections intact.");
    }

    private async Task NextYear()
    {
        r.Check(Sql.Count("SELECT COUNT(*) FROM dbo.StudentFeeAssignments WHERE EnrollmentId=@Id AND FeeTypeId=@Fee AND Amount=6000;", Sql.P("@Id", newEnrollment), Sql.P("@Fee", f.Tuition)) == 1, "Class 7 tuition assignment uses its destination rate.");
        r.Check(Convert.ToInt32(Post(newEnrollment, f.NextYear, f.NextMonthlyDate, f.Tuition).Rows[0]["ChargesCreated"]) == 0 && Balance(newEnrollment) == 0, "Posting before return confirmation is reported as ineligible with zero charges.");
        Sql.Proc("ConfirmPromotedAdmission", Sql.P("@UserId", f.Admin), Sql.P("@EnrollmentId", newEnrollment), Sql.P("@ReturnDate", f.PromotionDate), Sql.P("@UsesTransport", false),
            Sql.TvpParameter("@FeeChoices", "ReturnFeeChoiceType", Tvp.ReturnFeeChoices(new[] { (f.Tuition, 6000m, 0m), (f.AdmissionFee, 2000m, 0m) })));
        r.Check(Balance(newEnrollment) == 8000 && Sql.Count("SELECT COUNT(*) FROM dbo.Enrollments WHERE EnrollmentId=@Id AND BillingReady=1 AND ReturnConfirmedAt IS NOT NULL;", Sql.P("@Id", newEnrollment)) == 1, "Actual return confirmation activates billing and posts the 8000 opening bill.");
        var posted = Post(newEnrollment, f.NextYear, f.NextMonthlyDate, f.Tuition);
        int expectedNew = f.NextMonthlyDate.Month == 1 ? 0 : 1;
        r.Check(Convert.ToInt32(posted.Rows[0]["ChargesCreated"]) == expectedNew, "Current-month next-year tuition posts once, or is already present in the January opening bill.");
        await Collect(newEnrollment, f.NextMonthlyDate, 3000);
        r.Check(Payments(newEnrollment) == 3000 && Allocations(newEnrollment) == 3000 && Balance(newEnrollment) == 5000 + expectedNew * 6000, "New-year partial collection has the exact enrollment and remaining balance.");
        r.Check(FinancialSnapshot(originalEnrollment) == oldFinancialSnapshot, "New-year posting and payment preserve all original-year financial records.");
        r.Check(Sql.Count("SELECT COUNT(*) FROM dbo.Enrollments WHERE AdmissionId=@Student;", Sql.P("@Student", historicalStudent)) == 2,
            "The promoted historical student retains exactly two academic-year enrollments.");
    }

    private Task FinalAudit()
    {
        r.Check(Sql.Count("SELECT COUNT(*) FROM (SELECT AdmissionNumber FROM dbo.Students WHERE AdmissionNumber IS NOT NULL GROUP BY AdmissionNumber HAVING COUNT(*)>1) X;") == 0, "No duplicate official admission identities.");
        r.Check(Sql.Count("SELECT COUNT(*) FROM (SELECT RegistrationNumber FROM dbo.Students WHERE RegistrationNumber IS NOT NULL GROUP BY RegistrationNumber HAVING COUNT(*)>1) X;") == 0, "No duplicate registration identities.");
        r.Check(Sql.Count("SELECT COUNT(*) FROM (SELECT AdmissionId,AcademicYearId FROM dbo.Enrollments GROUP BY AdmissionId,AcademicYearId HAVING COUNT(*)>1) X;") == 0, "No duplicate student/year enrollments.");
        r.Check(Sql.Count("SELECT COUNT(*) FROM dbo.FeePaymentAllocations A LEFT JOIN dbo.FeePayments P ON P.FeePaymentId=A.FeePaymentId LEFT JOIN dbo.BillingCharges C ON C.BillingChargeId=A.BillingChargeId WHERE P.FeePaymentId IS NULL OR C.BillingChargeId IS NULL OR P.AdmissionId<>C.AdmissionId OR P.EnrollmentId<>C.EnrollmentId;") == 0, "No orphan allocation or payment against the wrong enrollment.");
        r.Check(Sql.Count("SELECT COUNT(*) FROM dbo.BillingCharges WHERE dbo.GetFeeChargeBalance(BillingChargeId)<0;") == 0, "No unexplained negative charge balances.");
        r.Check(Sql.Count("SELECT COUNT(*) FROM dbo.FeePayments P WHERE P.Status=N'Completed' AND P.PaymentMethod NOT IN(N'Refund',N'Correction') AND P.TotalAmount<>ISNULL((SELECT SUM(A.Amount) FROM dbo.FeePaymentAllocations A WHERE A.FeePaymentId=P.FeePaymentId),0);") == 0, "All completed collections reconcile exactly with allocation rows.");
        r.Check(Sql.Count("SELECT COUNT(*) FROM dbo.PromotionBatchStudents WHERE AdmissionId=@Student;", Sql.P("@Student", historicalStudent)) == 1, "Exactly one historical-student promotion decision exists.");
        r.Check(Sql.Count("SELECT COUNT(*) FROM (SELECT EnrollmentId,FeeTypeId,BillingMonth,ChargeKind FROM dbo.BillingCharges WHERE Status<>N'Cancelled' AND ChargeKind IN(N'Monthly',N'Annual') GROUP BY EnrollmentId,FeeTypeId,BillingMonth,ChargeKind HAVING COUNT(*)>1) X;") == 0, "No duplicate active monthly or annual billing charges.");
        r.Check(Sql.Count("SELECT COUNT(*) FROM sys.foreign_keys WHERE is_disabled=1 OR is_not_trusted=1;") == 0 &&
            Sql.Count("SELECT COUNT(*) FROM sys.check_constraints WHERE is_disabled=1 OR is_not_trusted=1;") == 0, "Referential and financial constraints remain trusted and enabled.");
        r.Check(Sql.Count("SELECT COUNT(*) FROM dbo.Enrollments WHERE AdmissionId=@Student AND Status=N'Active' AND ClassSectionId=@Placement;", Sql.P("@Student", historicalStudent), Sql.P("@Placement", f.Placement7)) == 1 &&
            Convert.ToString(Sql.Scalar("SELECT Status FROM dbo.Students WHERE AdmissionId=@Student;", Sql.P("@Student", historicalStudent))) == "Active", "Promoted historical student is active in Class 7, retaining Class 6 history.");
        r.Check(Payments(originalEnrollment) == 8000 && Payments(newEnrollment) == 3000, "Current-year collections and promoted student's collections retain separate enrollment identities.");
        return Task.CompletedTask;
    }

    private async Task AdmissionRefusals()
    {
        int before = Sql.Count("SELECT COUNT(*) FROM dbo.Students;");
        await r.Refused("Missing student name is refused at production SQL validation", () => { Sql.Proc("AdmitStudent", f.AdmissionArgs(" ")); return Task.CompletedTask; }, 51011);
        var denied = f.AdmissionArgs("Restricted Example"); denied.First(p => p.ParameterName == "@UserId").Value = f.Restricted;
        await r.Refused("A restricted operator cannot admit students", () => { Sql.Proc("AdmitStudent", denied); return Task.CompletedTask; }, 50003);
        var discount = f.AdmissionArgs("Discount Example"); discount.First(p => p.ParameterName == "@AdmissionDiscounts").Value = Tvp.AdmissionFeeDiscounts(new[] { (f.Tuition, 9000m) });
        await r.Refused("A discount cannot exceed the assigned fee", () => { Sql.Proc("AdmitStudent", discount); return Task.CompletedTask; }, 51024);
        Sql.Query("DELETE dbo.FeeConfigurations WHERE ClassId=@Class AND FeeTypeId=@Fee;", Sql.P("@Class", f.Class6), Sql.P("@Fee", f.Tuition));
        try { await r.Refused("Admission requires all mandatory class rates", () => { Sql.Proc("AdmitStudent", f.AdmissionArgs("Missing Rate Example")); return Task.CompletedTask; }, 51019); }
        finally { f.Rate(f.Class6, f.Tuition, 5000, "Monthly"); }
        r.Check(Sql.Count("SELECT COUNT(*) FROM dbo.Students;") == before, "Refused admissions create no student or enrollment.");
        await r.Refused("SQL rejects fractional fee configuration", () => { f.Rate(f.Class6, f.Purchase, 100.5m, "Monthly"); return Task.CompletedTask; }, 547);
        r.Check(Sql.Count("SELECT COUNT(*) FROM dbo.FeeConfigurations WHERE FeeTypeId=@Fee;", Sql.P("@Fee", f.Purchase)) == 0, "Fractional configuration refusal leaves no rate row.");
        try { WholeRupees.Require(10.5m); throw new AssertionFailure("C# accepted fractional rupees."); }
        catch (ArgumentException) { r.Check(true, "Production C# whole-rupee validation rejects fractions."); r.ExpectedRefusals++; }
    }

    private async Task Recovery()
    {
        // Use the sibling's financial fixture so the primary two-year history remains unchanged.
        int recoveryEnrollment = Sql.Count("SELECT EnrollmentId FROM dbo.Enrollments WHERE AdmissionId=@Id;", Sql.P("@Id", sibling));
        long charge = Convert.ToInt64(Sql.Scalar("SELECT MIN(BillingChargeId) FROM dbo.BillingCharges WHERE EnrollmentId=@Id;", Sql.P("@Id", recoveryEnrollment)));
        var collection = await CollectionDiscountWriter.CollectAsync("ProcessFeeTransaction", Array.Empty<CollectionDiscountWriter.Edit>(), "Synthetic recovery prerequisite",
            CollectionArgs(sibling, recoveryEnrollment, paidDate, 1000, new[] { (charge, 1000m) }));
        int origin = Convert.ToInt32(collection.Rows[0]["FeePaymentId"]);
        SqlParameter[] Refund() => new[] { Sql.P("@AdmissionId", sibling), Sql.P("@EnrollmentId", recoveryEnrollment), Sql.P("@TransactionType", "Refund"), Sql.P("@Amount", 200m),
            Sql.P("@TransactionDate", paidDate), Sql.P("@PaymentMethod", null), Sql.P("@ReferenceNumber", null), Sql.P("@Remarks", "Controlled lost acknowledgement"),
            Sql.P("@FeePaymentId", origin), new SqlParameter("@BillingChargeId", SqlDbType.BigInt) { Value = DBNull.Value }, Sql.TvpParameter("@Allocations", "FeePaymentAllocationType", Tvp.FeeAllocations(Array.Empty<(long,decimal)>())) };
        string scope = await RecoverableMutationWriter.GetScopeAsync();
        var intent = new MutationIntent(Guid.NewGuid(), MutationKind.FeeAdjustment, f.Admin, scope, RecoverableMutationWriter.PayloadHash(MutationKind.FeeAdjustment, f.Admin, Refund()), "Synthetic refund", DateTime.UtcNow);
        var result = await RecoverableMutationWriter.SaveAsync(intent, Refund(), t => { t.Commit(); throw new IOException("Controlled lost acknowledgement"); });
        r.Check(result.Outcome == MutationOutcome.Unconfirmed, "The existing commit seam distinguishes lost acknowledgement from rejection.");
        var reconciled = await RecoverableMutationWriter.CheckAsync(intent);
        r.Check(reconciled is not null, "Read-only reconciliation finds the committed request marker and saved refund.");
        int count = PaymentCount(recoveryEnrollment); var replay = await RecoverableMutationWriter.SaveAsync(intent, Refund());
        r.Check(replay.Outcome == MutationOutcome.Saved && PaymentCount(recoveryEnrollment) == count && replay.Result?.RecordId == reconciled!.RecordId, "Exact refund retry returns the original result without another financial transaction.");
        r.Check(Sql.Count("SELECT COUNT(*) FROM dbo.MutationRequests WHERE RequestId=@Id;", Sql.P("@Id", intent.Id)) == 1, "There is exactly one durable request marker.");
        r.Check(Balance(recoveryEnrollment) == 6200 && Sql.Money("SELECT SUM(TotalAmount) FROM dbo.FeePayments WHERE RelatedFeePaymentId=@Id AND PaymentMethod=N'Refund';", Sql.P("@Id", origin)) == -200,
            "The committed refund stores the contract's negative 200 and reopens exactly 200 of the sibling's due balance.");
    }

    private async Task FinanceRefusals()
    {
        var open = Sql.Query("SELECT TOP(1) BillingChargeId,DiscountAmount,dbo.GetFeeChargeBalance(BillingChargeId) Balance FROM dbo.BillingCharges WHERE EnrollmentId=@Id AND dbo.GetFeeChargeBalance(BillingChargeId)>0 ORDER BY BillingChargeId;", Sql.P("@Id", newEnrollment)).Rows[0];
        int count = PaymentCount(newEnrollment); decimal balance = Balance(newEnrollment);
        var stale = new CollectionDiscountWriter.Edit(Convert.ToInt64(open["BillingChargeId"]), historicalStudent, newEnrollment, 1, 0, Convert.ToDecimal(open["Balance"]));
        await r.Refused("A stale bill-discount review cannot collect", async () => await CollectionDiscountWriter.CollectAsync("ProcessFeeTransaction", new[] { stale }, "Synthetic stale review",
            CollectionArgs(historicalStudent, newEnrollment, f.NextMonthlyDate, 500, new[] { (stale.ChargeId, 500m) })), 53104);
        CurrentUser.UserId = f.Restricted;
        try {
            var edit = stale with { OriginalDiscount = 0 };
            await r.Refused("A restricted operator cannot adjust a bill", async () => await CollectionDiscountWriter.CollectAsync("ProcessFeeTransaction", new[] { edit }, "Synthetic unauthorized edit",
                CollectionArgs(historicalStudent, newEnrollment, f.NextMonthlyDate, 500, new[] { (edit.ChargeId, 500m) })), 50003);
        }
        finally { CurrentUser.UserId = f.Admin; }
        r.Check(PaymentCount(newEnrollment) == count && Balance(newEnrollment) == balance, "Stale and unauthorized financial attempts leave payments and balances unchanged.");
    }

    private async Task CollectionJournalGuard()
    {
        int before = PaymentCount(newEnrollment);
        using (var retained = CollectionRecoveryJournal.Begin("Synthetic ambiguous collection")) { }
        var pending = CollectionRecoveryJournal.ReadPending()!;
        try {
            try { await Collect(newEnrollment, f.NextMonthlyDate, 500); throw new AssertionFailure("Pending intent allowed another collection."); }
            catch (CollectionRecoveryException) { r.Check(true, "Production collection writer refuses a new submission while an ambiguous journal is retained."); r.ExpectedRefusals++; }
            r.Check(PaymentCount(newEnrollment) == before && CollectionRecoveryJournal.ReadPending()?.Id == pending.Id, "Guarded collection preserves the existing intent and creates no extra payment.");
        }
        finally { CollectionRecoveryJournal.ClearReviewed(pending.Id); }
    }

    private DataTable Promote(int destinationYear, int placement)
    {
        var decisions = new DataTable(); decisions.Columns.Add("AdmissionId", typeof(int)); decisions.Columns.Add("Decision", typeof(string)); decisions.Rows.Add(historicalStudent, "PROMOTE");
        return Sql.Proc("BulkPromoteStudents", Sql.P("@UserId", f.Admin), Sql.P("@SourceAcademicYearId", f.SourceYear), Sql.P("@DestinationAcademicYearId", destinationYear),
            Sql.P("@SourceCampusId", f.Campus), Sql.P("@SourceClassId", f.Class6), Sql.P("@SourceSectionId", f.Section), Sql.P("@DestinationClassSectionId", placement),
            Sql.P("@EffectiveDate", f.PromotionDate), Sql.P("@Remarks", "Fictional year-end promotion"), Sql.TvpParameter("@Decisions", "BulkPromotionDecisionType", decisions));
    }
    private static DataTable Post(int enrollment, int year, DateTime month, int fee) => Sql.Proc("PostSelectedMonthlyFees", Sql.P("@UserId", CurrentUser.UserId), Sql.P("@AcademicYearId", year),
        Sql.P("@BillingMonth", month), Sql.TvpParameter("@Selection", "PostingSelectionType", Tvp.PostingSelections(new[] { (enrollment, fee, month) })), Sql.P("@Reason", "Fictional monthly posting"));
    private Task<DataTable> Collect(int enrollment, DateTime date, decimal amount)
    {
        decimal remaining = amount; var lines = new List<(long, decimal)>();
        foreach (DataRow row in Sql.Query("SELECT BillingChargeId,dbo.GetFeeChargeBalance(BillingChargeId) Balance FROM dbo.BillingCharges WHERE EnrollmentId=@Id AND Status<>N'Cancelled' ORDER BY BillingChargeId;", Sql.P("@Id", enrollment)).Rows)
        {
            decimal take = Math.Min(remaining, Math.Max(0, Convert.ToDecimal(row["Balance"])));
            if (take > 0) lines.Add((Convert.ToInt64(row["BillingChargeId"]), take)); remaining -= take;
        }
        r.Check(remaining == 0, "The collection allocator covers the exact requested amount from unpaid balances.");
        return CollectExact(enrollment, date, amount, lines);
    }
    private Task<DataTable> CollectExact(int enrollment, DateTime date, decimal amount, IEnumerable<(long,decimal)> allocations) =>
        CollectionDiscountWriter.CollectAsync("ProcessFeeTransaction", Array.Empty<CollectionDiscountWriter.Edit>(), "Fictional lifecycle collection",
            CollectionArgs(Sql.Count("SELECT AdmissionId FROM dbo.Enrollments WHERE EnrollmentId=@Id;", Sql.P("@Id", enrollment)),
                enrollment, date, amount, allocations));
    internal static SqlParameter[] CollectionArgs(int admission, int enrollment, DateTime date, decimal amount, IEnumerable<(long,decimal)> allocations, string remarks = "Fictional lifecycle collection") => new[] {
        Sql.P("@AdmissionId", admission), Sql.P("@EnrollmentId", enrollment), Sql.P("@TransactionType", "Collection"), Sql.P("@Amount", amount), Sql.P("@TransactionDate", date),
        Sql.P("@PaymentMethod", "Cash"), Sql.P("@ReferenceNumber", null), Sql.P("@Remarks", remarks), Sql.P("@FeePaymentId", null),
        new SqlParameter("@BillingChargeId", SqlDbType.BigInt) { Value = DBNull.Value }, Sql.TvpParameter("@Allocations", "FeePaymentAllocationType", Tvp.FeeAllocations(allocations)) };
    private static decimal Balance(int enrollment) => Sql.Money("SELECT ISNULL(SUM(dbo.GetFeeChargeBalance(BillingChargeId)),0) FROM dbo.BillingCharges WHERE EnrollmentId=@Id AND Status<>N'Cancelled';", Sql.P("@Id", enrollment));
    private static decimal Payments(int enrollment) => Sql.Money("SELECT ISNULL(SUM(TotalAmount),0) FROM dbo.FeePayments WHERE EnrollmentId=@Id AND Status=N'Completed' AND PaymentMethod NOT IN(N'Refund',N'Correction');", Sql.P("@Id", enrollment));
    private static decimal Allocations(int enrollment) => Sql.Money("SELECT ISNULL(SUM(A.Amount),0) FROM dbo.FeePaymentAllocations A JOIN dbo.FeePayments P ON P.FeePaymentId=A.FeePaymentId WHERE P.EnrollmentId=@Id AND P.Status=N'Completed' AND P.PaymentMethod NOT IN(N'Refund',N'Correction');", Sql.P("@Id", enrollment));
    private static int PaymentCount(int enrollment) => Sql.Count("SELECT COUNT(*) FROM dbo.FeePayments WHERE EnrollmentId=@Id;", Sql.P("@Id", enrollment));
    private static string FinancialSnapshot(int enrollment) => Convert.ToString(Sql.Scalar("""
        SELECT (SELECT * FROM dbo.BillingCharges WHERE EnrollmentId=@Id ORDER BY BillingChargeId FOR JSON PATH,INCLUDE_NULL_VALUES) Charges,
            (SELECT * FROM dbo.FeePayments WHERE EnrollmentId=@Id ORDER BY FeePaymentId FOR JSON PATH,INCLUDE_NULL_VALUES) Payments,
            (SELECT A.* FROM dbo.FeePaymentAllocations A JOIN dbo.FeePayments P ON P.FeePaymentId=A.FeePaymentId WHERE P.EnrollmentId=@Id ORDER BY A.FeePaymentId,A.BillingChargeId FOR JSON PATH,INCLUDE_NULL_VALUES) Allocations
        FOR JSON PATH,WITHOUT_ARRAY_WRAPPER;
        """, Sql.P("@Id", enrollment))) ?? "";
}
