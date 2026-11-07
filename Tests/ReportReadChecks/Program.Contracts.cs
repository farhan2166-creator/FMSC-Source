using System.Text.Json;
using System.IO.Compression;
using System.Xml.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using FMSC_SMS.Pages.Reports.Finance;
using FMSC_SMS.Pages.Reports.Certificates;

internal static partial class Program
{
    private static void CheckQueryContracts()
    {
        foreach(string query in new[] { CreditBalanceReportData.Query,ConcessionSummaryData.Query,MonthlyFeeReconciliationData.Query,
            CertificateIssueRegisterData.BuildQuery(true,true),CertificateIssueRegisterData.BuildQuery(true,false),CertificateIssueRegisterData.BuildQuery(false,true) })
        {
            var fragment=(TSqlScript)new TSql160Parser(true).Parse(new StringReader(query),out var errors);
            Check(errors.Count==0,"Read query parses as SQL Server 2022 syntax: "+string.Join("; ",errors.Select(e=>e.Message)));
            foreach(var statement in fragment.Batches.SelectMany(b=>b.Statements))
            {
                Check(statement is SelectStatement or ExecuteStatement,"No financial DML or schema change is present in report SQL");
                if(statement is ExecuteStatement)
                    Check(query.Substring(statement.StartOffset,statement.FragmentLength).StartsWith("EXEC dbo.RequirePermission",StringComparison.Ordinal),"Only permission-check procedures execute in report SQL");
            }
        }
        var folder=new DirectoryInfo(AppContext.BaseDirectory);
        while(folder is not null && !File.Exists(Path.Combine(folder.FullName,"Core","DatabaseCompatibilityRequirements.json")))folder=folder.Parent;
        if(folder is null)throw new InvalidOperationException("Run checks from a checkout containing the production SQL manifest.");
        string migration=File.ReadAllText(Path.Combine(folder.FullName,"Tests","LifecycleSqlChecks","Baseline","Sql","GetOutstandingAndAgingReport.sql"));
        string collectionMigration=File.ReadAllText(Path.Combine(folder.FullName,"Tests","LifecycleSqlChecks","Baseline","Sql","GetCollectionsAndReceiptsReport.sql"));
        new TSql160Parser(true).Parse(new StringReader(collectionMigration),out var collectionErrors);
        Check(collectionErrors.Count==0,"Baseline collections procedure parses: "+string.Join("; ",collectionErrors.Select(e=>e.Message)));
        Check(collectionMigration.Contains("[__ReceiptStudents]")&&collectionMigration.Contains("A.__ReceiptSnapshot")&&!collectionMigration.Contains("THEN N'Mixed'"),"Collections exposes roster metadata and actual distinct methods");
        new TSql160Parser(true).Parse(new StringReader(migration),out var migrationErrors);
        Check(migrationErrors.Count==0,"Baseline outstanding procedure parses as SQL Server syntax: "+string.Join("; ",migrationErrors.Select(e=>e.Message)));
        Check(migration.Contains("LEFT JOIN dbo.FamilyMembers") && migration.Contains("CASE WHEN F.FamilyId IS NULL THEN M.AdmissionId END"),
            "Family debt retains and separately groups students without family links");
        Check(System.Text.RegularExpressions.Regex.Matches(migration,@"\[Due Date Missing\]").Count==3,
            "All three debt views expose missing-date money instead of losing it from the split");
        Check(migration.Contains("DueDate < @AsOfDate") && migration.Contains("DueDate = @AsOfDate") && migration.Contains("DueDate > @AsOfDate"),
            "Debt reader separates overdue, due today and future charges");
        Check(System.Text.RegularExpressions.Regex.Matches(migration,@"FatherName \[__StudentFatherName\], Gender \[__StudentGender\]").Count==2,
            "Student and charge debt views return private recorded parent/gender metadata");
        // Verify against the application's committed production SQL contract.
        // The root DACPAC is solely an optional owner inspection artifact.
        using var contract=JsonDocument.Parse(File.ReadAllText(Path.Combine(folder.FullName,"Core","DatabaseCompatibilityRequirements.json")));
        var columns=contract.RootElement.GetProperty("columns").EnumerateArray()
            .Select(e=>$"[{e.GetProperty("schema").GetString()}].[{e.GetProperty("objectName").GetString()}].[{e.GetProperty("name").GetString()}]")
            .ToHashSet(StringComparer.Ordinal);
        // The structural production contract deliberately lists selected column checks,
        // not every baseline column. The three legacy credit-ledger fields are verified
        // against the pinned data-free SQL model rather than pretending the manifest
        // lists columns it has never required.
        string baselinePackagePath=Path.Combine(folder.FullName,"Tests","LifecycleSqlChecks","Baseline","schema.dacpac");
        using var baselinePackage=ZipFile.OpenRead(baselinePackagePath);
        using var baselineModelStream=(baselinePackage.GetEntry("model.xml")
            ?? throw new InvalidDataException("Pinned baseline DACPAC lacks model.xml.")).Open();
        var baselineColumnNames=XDocument.Load(baselineModelStream)
            .Descendants()
            .Where(e=>e.Name.LocalName=="Element" && e.Attribute("Type")?.Value=="SqlSimpleColumn")
            .Select(e=>e.Attribute("Name")?.Value)
            .Where(name=>name is not null)
            .ToHashSet(StringComparer.Ordinal);
        Check(contract.RootElement.GetProperty("objects").EnumerateArray().Any(e=>
            e.GetProperty("schema").GetString()=="dbo" && e.GetProperty("name").GetString()=="StudentCreditEntries"),
            "Production structural contract includes the credit ledger table");

        var required=new Dictionary<string,string[]> {
            ["StudentCreditEntries"]=new[]{"AdmissionId","Amount","CreatedAt"},
            ["Students"]=new[]{"AdmissionId","AdmissionNumber","QuickReference","StudentName","FatherName","Gender","Status"},
            ["Families"]=new[]{"FamilyId","FamilyCode"},["FamilyMembers"]=new[]{"AdmissionId","FamilyId"},
            ["Enrollments"]=new[]{"AdmissionId","EnrollmentId","EnrollmentDate","LeavingDate","Status","ClassSectionId","AcademicYearId","BillingStartDate"},
            ["StudentFeeAssignments"]=new[]{"StudentFeeAssignmentId","AdmissionId","EnrollmentId","FeeTypeId","Frequency","ConcessionLabel","DiscountAmount","CancelledAt","EffectiveFrom","EffectiveTo"},
            ["AcademicYears"]=new[]{"AcademicYearId","StartDate","EndDate"},["FeeTypes"]=new[]{"FeeTypeId","FeeTypeName"},
            ["CampusClassSections"]=new[]{"ClassSectionId","CampusId","ClassId","SectionId"},
            ["BillingCharges"]=new[]{"BillingChargeId","BillingMonth","ChargeDate","AdmissionId","EnrollmentId","FeeTypeId","GrossAmount","DiscountAmount","NetAmount","Status"},
            ["FeePayments"]=new[]{"FeePaymentId","Status","IsCreditApplication"},["FeePaymentAllocations"]=new[]{"FeePaymentId","BillingChargeId","Amount"},
            ["Users"]=new[]{"UserId","Username"},
            ["Campuses"]=new[]{"CampusId","CampusName","DisplayOrder"},["Classes"]=new[]{"ClassId","ClassName","DisplayOrder"},["Sections"]=new[]{"SectionId","SectionName","DisplayOrder"}
        };
        foreach(var (table,fields) in required)foreach(string field in fields)
        {
            string fullName=$"[dbo].[{table}].[{field}]";
            bool inStructuralContract=columns.Contains(fullName);
            bool inPinnedBaseline=table=="StudentCreditEntries" && baselineColumnNames.Contains(fullName);
            Check(inStructuralContract || inPinnedBaseline,
                $"Production schema evidence includes {table}.{field}");
        }
        foreach(var (table,key,staff,date) in new[] {
            ("CharacterCertificates","CertificateRecordId",false,"CertificateDate"),("DateOfBirthCertificates","CertificateRecordId",false,"CertificateDate"),
            ("LeaveCertificates","LeaveRecordId",false,"LeaveDate"),("RegularStudentCertificates","RegularStudentCertificateId",false,"CertificateDate"),
            ("SportsCertificates","SportsCertificateId",false,"CertificateDate"),("NoObjectionCertificates","NoObjectionCertificateId",false,"CertificateDate"),
            ("ProvisionalCertificates","ProvisionalCertificateId",false,"CertificateDate"),("RegularLecturerCertificates","RegularLecturerCertificateId",true,"CertificateDate"),
            ("BankAccountOpeningCertificates","BankAccountOpeningCertificateId",true,"CertificateDate") })
            foreach(string field in new[] {key,date,"CertificateNumber","CreatedAt","CreatedByUserId",staff ? "StaffId" : "AdmissionId",staff ? "StaffName" : "StudentName"})
                Check(columns.Contains($"[dbo].[{table}].[{field}]"),$"Production SQL contract contains register source {table}.{field}");
        foreach(var (table,key,record) in new[] { ("CharacterCertificateIssues","CharacterCertificateIssueId","CertificateRecordId"),
            ("DateOfBirthCertificateIssues","DateOfBirthCertificateIssueId","CertificateRecordId"),("LeaveCertificateIssues","LeaveCertificateIssueId","LeaveRecordId") })
            foreach(string field in new[] {key,record,"IssuedAt","IssuedByUserId","CollectedAt","CollectedBy"})
                Check(columns.Contains($"[dbo].[{table}].[{field}]"),$"Production SQL contract contains issue log {table}.{field}");
        Check(columns.Contains("[dbo].[LeaveCertificates].[CertificateType]") && columns.Contains("[dbo].[LeaveCertificateIssues].[IssueType]"),"Leaving register preserves school/college and original/duplicate distinctions");
        Check(contract.RootElement.GetProperty("uniqueIndexes").EnumerateArray().Any(e =>
            e.GetProperty("schema").GetString()=="dbo" &&
            e.GetProperty("objectName").GetString()=="FamilyMembers" &&
            e.GetProperty("name").GetString()=="UQ_FamilyMembers_Student" &&
            e.GetProperty("keys").GetString()=="AdmissionId"),
            "Production SQL contract requires one family membership per student, preventing credit multiplication");
    }
}
