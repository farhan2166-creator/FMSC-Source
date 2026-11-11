using System.Text.Json;
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
        // Keep non-executing syntax validation fast. Financial meaning, family
        // membership, due buckets, actual payment methods and private roster
        // metadata are verified by CheckDebtSql / CheckCollectionSql against
        // an owned disposable SQL Server database in mandatory SQL CI.
        new TSql160Parser(true).Parse(new StringReader(migration),out var migrationErrors);
        Check(migrationErrors.Count==0,"Baseline outstanding procedure parses as SQL Server syntax: "+string.Join("; ",migrationErrors.Select(e=>e.Message)));
        // Verify against the application's committed production SQL contract.
        // The root DACPAC is solely an optional owner inspection artifact.
        using var contract=JsonDocument.Parse(File.ReadAllText(Path.Combine(folder.FullName,"Core","DatabaseCompatibilityRequirements.json")));
        var columns=contract.RootElement.GetProperty("columns").EnumerateArray()
            .Select(e=>$"[{e.GetProperty("schema").GetString()}].[{e.GetProperty("objectName").GetString()}].[{e.GetProperty("name").GetString()}]")
            .ToHashSet(StringComparer.Ordinal);
        // The production manifest intentionally lists selected columns, not
        // every legacy field. The current StudentCreditEntries projection is
        // now checked against a real migrated database by mandatory
        // LifecycleSqlChecks/CollectionCreditChecks, not a frozen DACPAC here.
        Check(contract.RootElement.GetProperty("objects").EnumerateArray().Any(e=>
            e.GetProperty("schema").GetString()=="dbo" && e.GetProperty("name").GetString()=="StudentCreditEntries"),
            "Production structural contract includes the credit ledger table");

        var required=new Dictionary<string,string[]> {
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
            Check(columns.Contains(fullName),
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
