using Microsoft.Data.SqlClient;

internal static class Program
{
    private static readonly Report Evidence = new();
    internal static async Task<int> Main(string[] args)
    {
        string path = Environment.GetEnvironmentVariable("FMSC_TEST_REPORT_PATH") ?? throw new InvalidOperationException("A test evidence path is required.");
        if (args.Contains("--level3-journal-child")) return await ReliabilityChecks.JournalChild();
        Fixture? fixture = null;
        bool ready = await Evidence.Stage("Fixture: isolated target and synthetic school", () => {
            Sql.ValidateTarget();
            Evidence.Engine = Convert.ToString(Sql.Scalar("SELECT CONVERT(nvarchar(40),SERVERPROPERTY('ProductVersion'));"));
            fixture = Fixture.Create();
            Evidence.Check(Sql.Count("SELECT COUNT(*) FROM dbo.SchoolProfile;") == 1, "Exactly one fictional school exists.");
            Evidence.Check(Sql.Count("SELECT COUNT(*) FROM dbo.AcademicYears;") == 2, "Exactly two consecutive non-overlapping years exist.");
            Evidence.Check(Sql.Count("SELECT COUNT(*) FROM dbo.CampusClassSections WHERE IsActive=1;") == 2, "Both class/section placements are active.");
            Evidence.Check(Sql.Count("SELECT COUNT(*) FROM dbo.FeeConfigurations;") == 4, "Mandatory fees are configured for both classes.");
            return Task.CompletedTask;
        });
        if (ready)
            ready = await Evidence.Stage("Certificate signature print authority",
                () => CertificatePrintAuthorityChecks.Run(fixture!, Evidence));
        if (ready)
            ready = await Evidence.Stage("Fresh-school fee type policy and Setup Readiness",
                () => { FeeSetupBootstrapChecks.Run(fixture!, Evidence); return Task.CompletedTask; });
        if (ready)
            ready = await Evidence.Stage("First admission: no manual counter seed",
                () => { FirstAdmissionCounterChecks.Run(fixture!, Evidence); return Task.CompletedTask; });
        if (ready)
        {
            if (args.Contains("--mutation-fixture")) await SeedLegacyMutation(fixture!);
            else if (args.Contains("--school-scenarios")) await new SchoolScenarios(fixture!, Evidence).Run();
            else if (args.Contains("--level3")) await new ReliabilityChecks(fixture!, Evidence).Run();
            else if (!args.Contains("--fixture-only")) await new Lifecycle(fixture!, Evidence).Run();
        }
        Evidence.Save(path);
        return Evidence.Scenarios.Any(s => s.Result != "PASS") ? 1 : 0;
    }

    private static async Task SeedLegacyMutation(Fixture f)
    {
        await Evidence.Stage("Fixture: existing mutation-suite synthetic prerequisites", async () => {
            var saved = Sql.Proc("AdmitStudent", f.AdmissionArgs("Recovery Example", current: true));
            int student = Convert.ToInt32(saved.Rows[0]["AdmissionId"]), enrollment = Convert.ToInt32(saved.Rows[0]["EnrollmentId"]);
            long charge = Convert.ToInt64(Sql.Scalar("SELECT TOP(1) BillingChargeId FROM dbo.BillingCharges WHERE EnrollmentId=@Enrollment AND NetAmount>=1000 ORDER BY BillingChargeId;", Sql.P("@Enrollment", enrollment)));
            await CollectionDiscountWriter.CollectAsync("ProcessFeeTransaction", Array.Empty<CollectionDiscountWriter.Edit>(), "Synthetic mutation fixture",
                Lifecycle.CollectionArgs(student, enrollment, f.PromotionDate, 1000, new[] { (charge, 1000m) }, "Audit initial collection"));
            Sql.Query("INSERT dbo.Staff(StaffNumber,StaffName,Gender,Status) VALUES(N'L1STAFF',N'FQA bank fixture',N'Male',N'Active');");
            Evidence.Check(Sql.Count("SELECT COUNT(*) FROM dbo.FeePayments WHERE Remarks=N'Audit initial collection';") == 1, "One prerequisite collection was recorded through the production writer.");
        });
    }
}
