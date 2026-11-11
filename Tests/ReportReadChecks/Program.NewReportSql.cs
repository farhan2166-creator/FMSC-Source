using Microsoft.Data.SqlClient;
using FMSC_SMS.Pages.Reports.Finance;
using FMSC_SMS.Pages.Reports.Certificates;

internal static partial class Program
{
    // Synthetic tables in the runner's uniquely named disposable database only.
    private static void CheckNewSql(SqlConnection connection)
    {
        Execute(connection,"""
            CREATE TABLE dbo.BillingCharges(BillingChargeId bigint PRIMARY KEY,AdmissionId int,EnrollmentId int,FeeTypeId int,
                BillingMonth date,GrossAmount decimal(12,2),DiscountAmount decimal(12,2),NetAmount decimal(12,2),Status nvarchar(30));
            CREATE TABLE dbo.FeePayments(FeePaymentId bigint PRIMARY KEY,Status nvarchar(30),IsCreditApplication bit,PaymentDate date);
            CREATE TABLE dbo.FeePaymentAllocations(FeePaymentId bigint,BillingChargeId bigint,Amount decimal(12,2));
            INSERT dbo.BillingCharges VALUES(1,1,2,1,'2026-10-01',1000,100,900,N'Posted'),(2,2,3,1,'2026-10-01',500,0,500,N'Posted'),
                (3,6,4,1,'2026-11-01',200,0,200,N'Posted'),(4,1,2,1,'2026-10-01',9999,0,9999,N'Cancelled'),
                (5,1,1,1,'2026-09-01',700,0,700,N'Posted');
            INSERT dbo.FeePayments VALUES(1,N'Completed',0,'2026-10-01'),(2,N'Completed',0,'2026-10-02'),(3,N'Completed',0,'2026-10-03'),
                (4,N'Completed',1,'2026-11-01'),(5,N'Completed',0,'2026-10-01'),(6,N'Completed',0,'2026-10-02'),
                (7,N'Completed',0,'2026-10-03'),(8,N'Completed',1,'2026-11-01'),(9,N'Reversed',0,'2026-10-04');
            INSERT dbo.FeePaymentAllocations VALUES(1,1,1000),(2,1,-100),(3,1,-50),(4,1,100),
                (5,2,100),(6,2,-20),(7,2,-30),(8,2,100),(9,2,100);
            CREATE TABLE dbo.Users(UserId int PRIMARY KEY,Username nvarchar(100));INSERT dbo.Users VALUES(1,N'Operator');
            ALTER TABLE dbo.BillingCharges ADD ChargeDate date NOT NULL DEFAULT('2026-10-10');
            INSERT dbo.BillingCharges(BillingChargeId,AdmissionId,EnrollmentId,FeeTypeId,BillingMonth,GrossAmount,DiscountAmount,NetAmount,Status)
                VALUES(6,1,2,1,NULL,60,60,0,N'Posted');
            """);
        using var charges=Read(connection,MonthlyFeeReconciliationData.Query);
        Check(charges.Rows.Count==5,"SQL reconciliation excludes cancelled charges and avoids multiplying multiple allocations");
        Check(charges.AsEnumerable().Single(r=>r.Field<long>("BillingChargeId")==6).Field<DateTime>("BillingMonth")==new DateTime(2026,10,1),"SQL uses charge month when billing month is null, including fully waived fees");
        var (rows,totals)=MonthlyFeeReconciliationData.Create(charges,"Whole School");
        Check((decimal)totals.Rows[0]["Net Fees"]==2300m && (decimal)totals.Rows[0]["Settled"]==1100m
            && (decimal)totals.Rows[0]["Outstanding"]==1250m && (decimal)totals.Rows[0]["Excess Settlement"]==50m,
            "SQL signed allocations reconcile; reversals are excluded and overallocations remain visible");
        using var october=Read(connection,MonthlyFeeReconciliationData.Query,configure:c=>{
            c.Parameters["@FromDate"].Value=new DateTime(2026,10,20);c.Parameters["@ToDate"].Value=new DateTime(2026,10,21);
        });
        Check(october.Rows.Count==3 && october.AsEnumerable().Sum(r=>r.Field<decimal>("Credit Applied"))==200m,
            "SQL dates select whole billing months and keep later settlements in that cohort");
        using var campus=Read(connection,MonthlyFeeReconciliationData.Query,campus:1);
        Check(campus.Rows.Count==3,"SQL reconciliation scopes charges by their recorded enrollment, including historic campus");
        using var year=Read(connection,MonthlyFeeReconciliationData.Query,configure:c=>c.Parameters["@AcademicYearId"].Value=99);
        Check(year.Rows.Count==0,"SQL reconciliation respects academic year");
        bool denied=false;try { Read(connection,MonthlyFeeReconciliationData.Query,user:0); } catch(SqlException) { denied=true; }
        Check(denied,"SQL reconciliation enforces report access");

        foreach(var (table,key,staff,date) in new[] {
            ("CharacterCertificates","CertificateRecordId",false,"CertificateDate"),("DateOfBirthCertificates","CertificateRecordId",false,"CertificateDate"),
            ("LeaveCertificates","LeaveRecordId",false,"LeaveDate"),("RegularStudentCertificates","RegularStudentCertificateId",false,"CertificateDate"),
            ("SportsCertificates","SportsCertificateId",false,"CertificateDate"),("NoObjectionCertificates","NoObjectionCertificateId",false,"CertificateDate"),
            ("ProvisionalCertificates","ProvisionalCertificateId",false,"CertificateDate"),("RegularLecturerCertificates","RegularLecturerCertificateId",true,"CertificateDate"),
            ("BankAccountOpeningCertificates","BankAccountOpeningCertificateId",true,"CertificateDate") })
        {
            string collectionColumns = !staff && (table is "SportsCertificates" or "NoObjectionCertificates" or "ProvisionalCertificates")
                ? ",CollectedAt datetime2,CollectedBy nvarchar(200)" : "";
            Execute(connection,$"CREATE TABLE dbo.{table}({key} int PRIMARY KEY,{(staff ? "StaffId" : "AdmissionId")} int,{(staff ? "StaffName" : "StudentName")} nvarchar(100),CertificateNumber nvarchar(50),{date} date,CreatedAt datetime2,CreatedByUserId int{collectionColumns}{(table=="LeaveCertificates" ? ",CertificateType nvarchar(20)" : "")});");
            Execute(connection,$"INSERT dbo.{table}({key},{(staff ? "StaffId" : "AdmissionId")},{(staff ? "StaffName" : "StudentName")},CertificateNumber,{date},CreatedAt,CreatedByUserId{(table=="LeaveCertificates" ? ",CertificateType" : "")}) VALUES(1,1,N'Saved snapshot',N'TEST-2026-0001','2020-01-01','2026-10-22 13:45',1{(table=="LeaveCertificates" ? ",N'School'" : "")});");
        }
        Execute(connection,"INSERT dbo.LeaveCertificates(LeaveRecordId,AdmissionId,StudentName,CertificateNumber,LeaveDate,CreatedAt,CreatedByUserId,CertificateType) VALUES(2,2,N'Saved college snapshot',N'CL-2026-0001','2026-10-01','2026-10-22',1,N'College');");
        foreach(var (table,key,record,leave) in new[] { ("CharacterCertificateIssues","CharacterCertificateIssueId","CertificateRecordId",false),
            ("DateOfBirthCertificateIssues","DateOfBirthCertificateIssueId","CertificateRecordId",false),("LeaveCertificateIssues","LeaveCertificateIssueId","LeaveRecordId",true) })
        {
            Execute(connection,$"CREATE TABLE dbo.{table}({key} int PRIMARY KEY,{record} int,IssuedAt datetime2,IssuedByUserId int,CollectedAt datetime2,CollectedBy nvarchar(200){(leave ? ",IssueType nvarchar(50)" : "")});");
            Execute(connection,$"INSERT dbo.{table} VALUES(1,1,'2026-10-22 14:30',1,NULL,NULL{(leave ? ",N'Original'" : "")});");
        }
        // The live register reads the immutable issue snapshots and regular
        // student issue log, not the original editable certificate records.
        // Model their *current* contracts in this disposable SQL fixture.
        Execute(connection,"""
            CREATE TABLE dbo.CharacterCertificateIssueSnapshots (
                CharacterCertificateIssueId int PRIMARY KEY,StudentName nvarchar(100),
                CertificateDate date,ExamType nvarchar(60),CorrectionReason nvarchar(100));
            CREATE TABLE dbo.DateOfBirthCertificateIssueSnapshots (
                DateOfBirthCertificateIssueId int PRIMARY KEY,StudentName nvarchar(100),CertificateDate date);
            CREATE TABLE dbo.LeaveCertificateIssueSnapshots (
                LeaveCertificateIssueId int PRIMARY KEY,StudentName nvarchar(100),LeaveDate date);
            CREATE TABLE dbo.RegularStudentCertificateIssues (
                RegularStudentCertificateIssueId int PRIMARY KEY,RegularStudentCertificateId int,
                StudentName nvarchar(100),CertificateDate date,IssuedAt datetime2,
                IssuedByUserId int,CollectedAt datetime2,CollectedBy nvarchar(200));
            INSERT dbo.CharacterCertificateIssueSnapshots VALUES
                (1,N'Issued character snapshot','2020-01-01',N'School',NULL),
                (2,N'Reissued character snapshot','2020-01-01',N'School',NULL);
            INSERT dbo.DateOfBirthCertificateIssueSnapshots VALUES
                (1,N'Issued birth snapshot','2020-01-01');
            INSERT dbo.LeaveCertificateIssueSnapshots VALUES
                (1,N'Issued leaving snapshot','2020-01-01'),
                (2,N'Duplicate leaving snapshot','2020-01-01'),
                (3,N'College leaving snapshot','2026-10-01');
            """);
        Execute(connection,"""
            UPDATE dbo.CharacterCertificateIssues SET CollectedAt='2026-10-22 15:00',CollectedBy=N'Parent' WHERE CharacterCertificateIssueId=1;
            INSERT dbo.CharacterCertificateIssues VALUES(2,1,'2026-10-23 10:00',1,NULL,NULL);
            INSERT dbo.LeaveCertificateIssues VALUES(2,1,'2026-10-23 10:00',1,NULL,NULL,N'Duplicate'),(3,2,'2026-10-23 11:00',1,NULL,NULL,N'Original');
            UPDATE dbo.SportsCertificates SET CertificateNumber=NULL;
            UPDATE dbo.NoObjectionCertificates SET CollectedAt='2026-10-24 10:00',CollectedBy=N'Guardian';
            """);
        string query=CertificateIssueRegisterData.BuildQuery(true,true);
        using var register=Read(connection,query);
        Check(register.Rows.Count==12 && register.AsEnumerable().Select(r=>r.Field<string>("Certificate Type")).Distinct().Count()==10,"SQL register consolidates all ten types and keeps repeated issues");
        Check(register.AsEnumerable().Any(r=>r.Field<string>("Recipient")=="Issued character snapshot")
            && register.AsEnumerable().Any(r=>r.Field<string>("Recipient")=="College leaving snapshot"),
            "Actual certificate register uses preserved issue snapshots rather than mutable source names");
        Check(register.AsEnumerable().Where(r=>r.Field<string>("Entry")=="Saved record only")
            .All(r=>r.Field<string>("Collection")=="Not tracked" && r.IsNull("Collected On")
                && r.IsNull("Collected By")),
            "Untracked saved certificates are never represented as issued, awaiting collection or collected");
        var counts=CertificateIssueRegisterData.Totals(register);
        Check((int)counts.Rows[0]["Logged issues"]==9 && (int)counts.Rows[0]["Saved records only"]==3 && (int)counts.Rows[0]["Awaiting collection"]==7,"SQL register counts logged issues separately from untracked saved records");
        Check(register.AsEnumerable().Any(r=>r.Field<string>("Certificate No.")=="Record #1"),"SQL preserves older records with no certificate number without inventing an issued number");
        using var dateRange=Read(connection,query,configure:c=>c.Parameters["@FromDate"].Value=new DateTime(2026,10,23));
        Check(dateRange.Rows.Count==3,"SQL register filters event date, not certificate date");
        using var staffRows=Read(connection,query,configure:c=>c.Parameters["@RecipientType"].Value="Staff");
        Check(staffRows.Rows.Count==2 && staffRows.AsEnumerable().All(r=>r.Field<string>("Student / Staff No.")=="Staff #1"),"SQL register keeps staff references separate from student references");
        using var collected=Read(connection,query,configure:c=>c.Parameters["@CollectionStatus"].Value="Collected");
        Check(collected.Rows.Count==2 && collected.AsEnumerable().Any(row => row.Field<string>("Certificate Type")=="No Objection"
            && row.Field<string>("Collected By")=="Guardian"),"SQL collection filter includes row-tracked NOC collection and its actual collector");
        using var literal=Read(connection,query,configure:c=>c.Parameters["@Search"].Value="%");
        Check(literal.Rows.Count==0,"SQL search treats wildcard characters literally");
        using var allowedGeneral=Read(connection,CertificateIssueRegisterData.BuildQuery(true,false),user:3);
        using var allowedLeave=Read(connection,CertificateIssueRegisterData.BuildQuery(false,true),user:4);
        Check(allowedGeneral.Rows.Count==9 && allowedLeave.Rows.Count==3,"SQL limited access can read its own source set");
        foreach(int user in new[]{0,2,3,4})
        {
            denied=false;try { Read(connection,query,user:user); } catch(SqlException) { denied=true; }
            Check(denied,"SQL combined-source query enforces every source permission for user "+user);
        }
    }
}
