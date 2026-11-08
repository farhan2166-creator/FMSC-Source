using Microsoft.Data.SqlClient;

// End-to-end business examples against the complete migrated, data-free SQL fixture.
// The reference DACPAC is not a deployment or fixture-publishing source.
internal sealed partial class SchoolScenarios(Fixture f, Report r)
{
    private (int Student, int Enrollment) Admit(string name, int? family = null)
    {
        var row = Sql.Proc("AdmitStudent", f.AdmissionArgs(name, family, current: true)).Rows[0];
        return (Convert.ToInt32(row["AdmissionId"]), Convert.ToInt32(row["EnrollmentId"]));
    }
    private long Tuition(int enrollment) => Convert.ToInt64(Sql.Scalar(
        "SELECT TOP(1) BillingChargeId FROM dbo.BillingCharges WHERE EnrollmentId=@E AND FeeTypeId=@F ORDER BY BillingChargeId;",
        Sql.P("@E", enrollment), Sql.P("@F", f.Tuition)));
    private decimal Credit(int student) => Sql.Money("SELECT COALESCE(SUM(Amount),0) FROM dbo.StudentCreditEntries WHERE AdmissionId=@S;", Sql.P("@S", student));
    private decimal Due(long charge) => Sql.Money("SELECT dbo.GetFeeChargeBalance(@C);", Sql.P("@C", charge));
    private Task<DataTable> Pay(int student, int enrollment, decimal amount, params (long, decimal)[] rows) =>
        CollectionDiscountWriter.CollectAsync("ProcessFeeTransaction", Array.Empty<CollectionDiscountWriter.Edit>(), "Fictional school scenario",
            Lifecycle.CollectionArgs(student, enrollment, DateTime.Today, amount, rows, "Fictional school scenario"));
    private void CreditAction(int student, string action, decimal amount, Guid request, int? target = null) =>
        Sql.Proc("ProcessStudentCredit", Sql.P("@UserId", f.Admin), Sql.P("@AdmissionId", student), Sql.P("@Action", action),
            Sql.P("@Amount", amount), Sql.P("@RequestId", request), Sql.P("@TargetAdmissionId", target), Sql.P("@Reason", "Fictional school scenario"));
    private void Rate(long charge, decimal amount, string action = "Credit") => Sql.Proc("AdjustBillingChargeRate",
        Sql.P("@UserId", f.Admin), Sql.P("@BillingChargeId", charge), Sql.P("@NewAmount", amount),
        Sql.P("@PaidReductionAction", action), Sql.P("@Reason", "Fictional rate correction"), Sql.P("@RequestId", Guid.NewGuid()));

    internal async Task Run()
    {
        await r.Stage("Operator: admission-month concession choice", AdmissionConcessionChoice);
        await r.Stage("Operator: temporary concessions, paid bills and exact recovery", ConcessionChoices);
        await r.Stage("Operator: stale concession preview and atomic refusal", ConcessionRefusals);
        await r.Stage("Operator: leaving choices reconcile complete bill chains", LeavingChoices);
        await r.Stage("Operator: reviewed rates, unchanged net terms and explicit waiver mode", ReviewedRates);
        await r.Stage("Operator: annual and individual purchase corrections", AnnualAndPurchaseTerms);
        await r.Stage("Operator: closed-placement bill corrections preserve history", ClosedBillTerms);
        await r.Stage("Operator: promotion resets or carries monthly concessions", PromotionConcessionChoices);
        await r.Stage("School: legacy activation acknowledges historical admission fees", LegacyActivation);
        await r.Stage("School: opening fees honor cutoff and prospective discounts", OpeningDiscounts);
        await r.Stage("School: partial payment and remaining-balance waiver", PartialWaiver);
        await r.Stage("School: paid bill reductions, credit and repeated rate changes", PaidRateCorrection);
        await r.Stage("School: credit deposit replay, sibling transfer and cash refund", Credits);
        await r.Stage("School: collection credit, future bills and atomic cash settlement", () => CollectionCreditChecks.Run(f,r));
        await r.Stage("School: pending registration confirmation preserves paid opening bill", PendingConfirmation);
        await r.Stage("School: automatic monthly posting excludes legacy and reversed fees", AutomaticBilling);
    }

    private async Task LegacyActivation()
    {
        int student = Sql.Count("""
            INSERT dbo.Students(AdmissionNumber,AdmissionDate,AdmissionCampusId,StudentName,FatherName,Status,LegacySetupPending)
            VALUES(N'99999991',@Date,@Campus,N'Historical Fee Example',N'Fictional Parent',N'Active',1);
            SELECT CONVERT(int,SCOPE_IDENTITY());
            """, Sql.P("@Date", f.PromotionDate), Sql.P("@Campus", f.Campus));
        string fees = System.Text.Json.JsonSerializer.Serialize(new[] { new { FeeTypeId=f.Tuition, Amount=5000, DiscountAmount=0, Frequency="Monthly" } });
        SqlParameter[] Args(bool preview, string? snapshot = null) => new[] {
            Sql.P("@UserId", f.Admin), Sql.P("@AdmissionId", student), Sql.P("@YearId", f.NextYear), Sql.P("@PlacementId", f.Placement6),
            Sql.P("@StartDate", f.PromotionDate), Sql.P("@BillingStart", new DateTime(DateTime.Today.Year,DateTime.Today.Month,1)),
            Sql.P("@StudentName", "Historical Fee Example"), Sql.P("@FatherName", "Fictional Parent"), Sql.P("@Fees", fees),
            Sql.P("@Reason", "Fictional individually reviewed activation"), Sql.P("@AdmissionFeeAlreadyPaid", true), Sql.P("@Preview", preview), Sql.P("@ExpectedSnapshot", snapshot)
        };
        string snapshot = Convert.ToString(Sql.Proc("ActivateStudentBilling", Args(true)).Rows[0]["Snapshot"])!;
        int enrollment = Convert.ToInt32(Sql.Proc("ActivateStudentBilling", Args(false, snapshot)).Rows[0]["EnrollmentId"]);
        r.Check(Sql.Count("SELECT COUNT(*) FROM dbo.StudentAdmissionFeeAcknowledgements WHERE AdmissionId=@S;", Sql.P("@S", student))==1,
            "Historical admission fees have one explicit acknowledgement.");
        r.Check(Sql.Count("SELECT COUNT(*) FROM dbo.FeePayments WHERE AdmissionId=@S;", Sql.P("@S", student))==0 &&
            Sql.Money("SELECT SUM(NetAmount) FROM dbo.BillingCharges WHERE AdmissionId=@S;", Sql.P("@S", student))==5000,
            "Activation posts only reviewed tuition, without inventing an admission payment or back-billing earlier months.");
        await r.Refused("An acknowledged admission fee cannot be charged later", () => {
            Sql.Proc("AddStudentFeeAssignment", Sql.P("@UserId", f.Admin), Sql.P("@EnrollmentId", enrollment), Sql.P("@FeeTypeId", f.AdmissionFee));
            return Task.CompletedTask;
        },59971);
    }

    private Task OpeningDiscounts()
    {
        object? old = Sql.Scalar("SELECT FeeProrationCutoffDay FROM dbo.SchoolProfile;");
        try
        {
            // For every calendar day, NULL means no cutoff and cutoff day 1
            // means the following month. Avoid historical admissions: the
            // production policy intentionally suppresses their opening charges.
            foreach (var (cutoff,expected) in new (int?,decimal)[] { (null,5000m), (1,4000m) })
            {
                Sql.Query("UPDATE dbo.SchoolProfile SET FeeProrationCutoffDay=@Cutoff;",Sql.P("@Cutoff",cutoff));
                var args = f.AdmissionArgs("Cutoff Discount Example " + (cutoff?.ToString()??"None"));
                args.Single(p=>p.ParameterName=="@AdmissionDate").Value=DateTime.Today;
                args.Single(p=>p.ParameterName=="@AdmissionDiscounts").Value=Tvp.AdmissionFeeDiscounts(new[] { (f.Tuition,1000m) });
                int enrollment = Convert.ToInt32(Sql.Proc("AdmitStudent",args).Rows[0]["EnrollmentId"]);
                r.Check(Sql.Money("SELECT SUM(NetAmount) FROM dbo.BillingCharges WHERE EnrollmentId=@E AND FeeTypeId=@F;",
                    Sql.P("@E",enrollment),Sql.P("@F",f.Tuition))==expected,
                    cutoff is null
                        ? "Without a cutoff, this month's opening tuition retains the original rate until the next-month concession."
                        : "At the configured cutoff, next-month opening tuition starts with the prospective discount.");
            }
        }
        finally { Sql.Query("UPDATE dbo.SchoolProfile SET FeeProrationCutoffDay=@Old;",Sql.P("@Old",old)); }
        return Task.CompletedTask;
    }

    private async Task PartialWaiver()
    {
        var child = Admit("Partial Waiver Example"); long charge = Tuition(child.Enrollment);
        await Pay(child.Student, child.Enrollment, 4000, (charge, 4000));
        r.Check(Due(charge) == 1000, "A 5000 tuition bill with 4000 received has 1000 unpaid.");
        int before = Sql.Count("SELECT COUNT(*) FROM dbo.FeePayments;");
        await Pay(child.Student, child.Enrollment, 0, (charge, -1000));
        r.Check(Due(charge) == 0, "Waiving only the unpaid 1000 settles the tuition bill.");
        r.Check(Sql.Count("SELECT COUNT(*) FROM dbo.FeePayments;") == before, "A noncash waiver creates no fake cash receipt.");
        r.Check(Sql.Money("SELECT SUM(TotalAmount) FROM dbo.FeePayments WHERE AdmissionId=@S AND IsCreditApplication=0;", Sql.P("@S", child.Student)) == 4000,
            "The original 4000 received remains recorded after the waiver.");
    }

    private async Task PaidRateCorrection()
    {
        var child = Admit("Rate Correction Example"); long charge = Tuition(child.Enrollment);
        await Pay(child.Student, child.Enrollment, 5000, (charge, 5000));
        Rate(charge, 4000);
        r.Check(Due(charge) == 0 && Credit(child.Student) == 1000, "Reducing a paid 5000 bill to 4000 releases exactly 1000 as credit.");
        r.Check(Sql.Money("SELECT SUM(TotalAmount) FROM dbo.FeePayments WHERE AdmissionId=@S AND IsCreditApplication=0;", Sql.P("@S", child.Student)) == 5000,
            "Moving a paid difference to credit does not invent a cash refund.");
        Rate(charge, 6000);
        decimal due = Sql.Money("SELECT SUM(dbo.GetFeeChargeBalance(BillingChargeId)) FROM dbo.BillingCharges WHERE BillingChargeId=@C OR CorrectionOfBillingChargeId=@C;", Sql.P("@C", charge));
        // Fee corrections do not spend held advance: the operator may apply it
        // only in a dated-today Fee Collection, with a saved settlement receipt.
        r.Check(Credit(child.Student) == 1000 && due == 2000,
            $"A later increase leaves advance intact for Fee Collection and records the full unpaid difference (credit={Credit(child.Student)}, due={due}).");
        Rate(charge, 3000);
        r.Check(Sql.Money("SELECT SUM(NetAmount) FROM dbo.BillingCharges WHERE (BillingChargeId=@C OR CorrectionOfBillingChargeId=@C) AND Status<>N'Cancelled';", Sql.P("@C", charge)) == 3000,
            "A repeated up/down correction reconciles original and difference charges to the final 3000 rate.");
        r.Check(Sql.Count("SELECT COUNT(*) FROM dbo.StudentCreditEntries GROUP BY AdmissionId HAVING SUM(Amount)<0;") == 0, "Rate correction never makes a student credit balance negative.");
    }

    private async Task Credits()
    {
        var parent = Admit("Credit Source Example"); var sibling = Admit("Credit Sibling Example", parent.Student);
        Guid request = Guid.NewGuid(); CreditAction(parent.Student, "Deposit", 3000, request); CreditAction(parent.Student, "Deposit", 3000, request);
        r.Check(Credit(parent.Student) == 3000, "Replaying an acknowledged 3000 deposit does not receive another 3000.");
        await r.Refused("Reusing a request with a changed amount is refused", () => { CreditAction(parent.Student, "Deposit", 4000, request); return Task.CompletedTask; }, 54905);
        CreditAction(parent.Student, "Transfer", 1000, Guid.NewGuid(), sibling.Student);
        r.Check(Credit(parent.Student) == 2000 && Credit(sibling.Student) == 1000, "Sibling credit transfer preserves the family's total credit.");
        CreditAction(parent.Student, "Refund", 500, Guid.NewGuid());
        r.Check(Credit(parent.Student) == 1500, "Refunding unused credit reduces the source account exactly once.");
        r.Check(Sql.Money("SELECT SUM(TotalAmount) FROM dbo.FeePayments WHERE AdmissionId IN(@A,@B) AND IsCreditApplication=0;", Sql.P("@A", parent.Student), Sql.P("@B", sibling.Student)) == 2500,
            "3000 cash received minus 500 refunded equals 2500; sibling transfer adds no cash.");
        await r.Refused("Credit cannot be refunded beyond the available amount", () => { CreditAction(parent.Student, "Refund", 2000, Guid.NewGuid()); return Task.CompletedTask; }, 54906);
    }

    private async Task PendingConfirmation()
    {
        var args = f.AdmissionArgs("Pending Registration Example", current: true);
        args.Single(p => p.ParameterName == "@QuickRegistration").Value = true;
        DataRow row = Sql.Proc("AdmitStudent", args).Rows[0];
        int student = Convert.ToInt32(row["AdmissionId"]), enrollment = Convert.ToInt32(row["EnrollmentId"]);
        long charge = Tuition(enrollment); await Pay(student, enrollment, 2000, (charge, 2000));
        int count = Sql.Count("SELECT COUNT(*) FROM dbo.BillingCharges WHERE AdmissionId=@S;", Sql.P("@S", student));
        Sql.Proc("CompleteQuickAdmission", Sql.P("@UserId", f.Admin), Sql.P("@AdmissionId", student));
        r.Check(Sql.Count("SELECT COUNT(*) FROM dbo.Students WHERE AdmissionId=@S AND Status=N'Active' AND AdmissionNumber IS NOT NULL AND RegistrationNumber IS NOT NULL;", Sql.P("@S", student)) == 1,
            "Confirming pending admission allocates official identities and activates the same student.");
        r.Check(Sql.Count("SELECT COUNT(*) FROM dbo.BillingCharges WHERE AdmissionId=@S;", Sql.P("@S", student)) == count && Due(charge) == 3000,
            "Confirmation preserves the 2000 payment and does not post opening fees again.");
    }

    private Task AutomaticBilling()
    {
        var child = Admit("Automatic Billing Example"); var legacy = Admit("Legacy Excluded Example");
        Sql.Query("UPDATE dbo.Students SET LegacySetupPending=1 WHERE AdmissionId=@S;", Sql.P("@S", legacy.Student));
        Sql.Query("IF NOT EXISTS(SELECT 1 FROM dbo.BillingAutomationState WHERE StateId=1) INSERT dbo.BillingAutomationState(StateId,StartMonth) VALUES(1,DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1));");
        // Use a DataSet for the first summary table; Sql.Proc returns the last result table.
        using var connection = DatabaseHelper.GetConnection(); connection.Open();
        string Snapshot()
        {
            using var cmd = new SqlCommand("EXEC dbo.RunAutomaticMonthlyBilling @UserId=@U;", connection);
            cmd.Parameters.Add(Sql.P("@U", f.Admin)); using var reader = cmd.ExecuteReader(); reader.Read(); return reader.GetString(reader.GetOrdinal("Snapshot"));
        }
        void Post() => Sql.Proc("RunAutomaticMonthlyBilling", Sql.P("@UserId", f.Admin), Sql.P("@Preview", false), Sql.P("@ExpectedSnapshot", Snapshot()));
        Post();
        int count = Sql.Count("SELECT COUNT(*) FROM dbo.BillingCharges;"); Post();
        r.Check(Sql.Count("SELECT COUNT(*) FROM dbo.BillingCharges;") == count, "Repeated automatic billing creates no duplicate charges.");
        // Capture its pre-existing opening bills. Legacy activation must not
        // cause new automatic monthly fees, but earlier saved rows remain.
        r.Check(Sql.Count("SELECT COUNT(*) FROM dbo.BillingCharges WHERE AdmissionId=@S AND BillingMonth=DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1);",
                Sql.P("@S", legacy.Student)) == (DateTime.Today.Month == 1 ? 2 : 1),
            "Legacy billing protection retains its existing opening rows without adding monthly duplicates.");
        Sql.Query("UPDATE dbo.BillingCharges SET Status=N'Cancelled' WHERE AdmissionId=@S AND FeeTypeId=@F AND BillingMonth=DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1);", Sql.P("@S", child.Student), Sql.P("@F", f.Tuition));
        Post();
        r.Check(Sql.Count("SELECT COUNT(*) FROM dbo.BillingCharges;") == count, "Automatic billing does not recreate a deliberately reversed fee.");
        return Task.CompletedTask;
    }
}
