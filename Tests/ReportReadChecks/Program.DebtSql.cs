using Microsoft.Data.SqlClient;

internal static partial class Program
{
    // Invoked only by --sql inside the runner's owned disposable database.
    private static void CheckDebtSql(SqlConnection connection)
    {
        // SQL Server compiles a batch before executing its ALTER TABLE clauses.
        // Add DueDate in its own batch so subsequent UPDATE/INSERT statements
        // bind the new column without a false SQL 207 failure.
        Execute(connection,"ALTER TABLE dbo.BillingCharges ADD DueDate date NULL;");
        Execute(connection,"""
            ALTER TABLE dbo.Students ADD RegistrationNumber nvarchar(50),AdmissionNumberBase bigint,AdmissionNumberSuffix nvarchar(20);
            ALTER TABLE dbo.FeeTypes ADD DisplayOrder int NOT NULL DEFAULT(1);
            UPDATE dbo.BillingCharges SET DueDate='2026-10-01';
            UPDATE dbo.BillingCharges SET DueDate='2026-11-01' WHERE BillingChargeId=3;
            INSERT dbo.Enrollments(EnrollmentId,AdmissionId,AcademicYearId,ClassSectionId,Status,EnrollmentDate,BillingStartDate)
                VALUES(5,3,1,1,N'Active','2026-01-01','2026-01-01'),(6,4,1,1,N'Active','2026-01-01','2026-01-01');
            INSERT dbo.BillingCharges(BillingChargeId,AdmissionId,EnrollmentId,FeeTypeId,BillingMonth,GrossAmount,DiscountAmount,NetAmount,Status,DueDate)
                VALUES(7,3,5,1,'2026-10-01',100,0,100,N'Posted','2026-10-21'),(8,4,6,1,'2026-10-01',80,0,80,N'Posted','2026-10-01'),(9,4,6,1,'2026-10-01',50,0,50,N'Posted',NULL);
            CREATE TABLE dbo.DatabaseVersion(VersionId int IDENTITY,VersionNumber nvarchar(50),AppliedOn datetime2,AppliedBy nvarchar(200),Notes nvarchar(max));
            INSERT dbo.DatabaseVersion(VersionNumber) VALUES(N'1');
            """);
        Execute(connection,"""
            CREATE FUNCTION dbo.GetFeeChargeBalance(@Id bigint) RETURNS decimal(12,2) AS
            BEGIN
                DECLARE @Balance decimal(12,2);
                SELECT @Balance=B.NetAmount-COALESCE((SELECT SUM(A.Amount) FROM dbo.FeePaymentAllocations A
                    JOIN dbo.FeePayments P ON P.FeePaymentId=A.FeePaymentId AND P.Status=N'Completed'
                    WHERE A.BillingChargeId=B.BillingChargeId),0)
                FROM dbo.BillingCharges B WHERE B.BillingChargeId=@Id;
                RETURN CASE WHEN @Balance>0 THEN @Balance ELSE 0 END;
            END;
            """);
        var folder=new DirectoryInfo(AppContext.BaseDirectory);
        while(folder is not null && !File.Exists(Path.Combine(folder.FullName,"Core","DatabaseCompatibilityRequirements.json")))folder=folder.Parent;
        string sql=File.ReadAllText(Path.Combine(folder!.FullName,"Tests","LifecycleSqlChecks","Baseline","Sql","GetOutstandingAndAgingReport.sql"));
        // Private report identity must reflect persisted student records, not
        // an exact stored-procedure SELECT clause or its formatting.
        Execute(connection,"UPDATE dbo.Students SET FatherName=N'Parent of Three', Gender=N'Female' WHERE AdmissionId=3;");
        Execute(connection,"BEGIN TRANSACTION;");
        try
        {
            foreach(string batch in System.Text.RegularExpressions.Regex.Split(sql,@"(?im)^GO\s*$"))
                if(!string.IsNullOrWhiteSpace(batch))Execute(connection,batch);
            Execute(connection,"COMMIT TRANSACTION;");
        }
        catch { Execute(connection,"IF @@TRANCOUNT>0 ROLLBACK TRANSACTION;");throw; }
        foreach(string view in new[]{"Charges","Students","Families"})
        {
            using var rows=Read(connection,"EXEC dbo.GetOutstandingAndAgingReport @UserId,@View,@CampusId,@AcademicYearId,@ClassId,@SectionId,@AsOfDate;",
                configure:c=>c.Parameters.AddWithValue("@View",view));
            Check(rows.AsEnumerable().Sum(r=>r.Field<decimal>("Outstanding"))==1480m,view+": all debt retained including unlinked students");
            Check(rows.AsEnumerable().Sum(r=>r.Field<decimal>("Overdue Amount"))==1130m
                && rows.AsEnumerable().Sum(r=>r.Field<decimal>("Due Today"))==100m
                && rows.AsEnumerable().Sum(r=>r.Field<decimal>("Not Yet Due"))==200m
                && rows.AsEnumerable().Sum(r=>r.Field<decimal>("Due Date Missing"))==50m,view+": due-status boundaries reconcile exactly");
            if (view is "Charges" or "Students")
            {
                Check(rows.Columns.Contains("__StudentFatherName")
                    && rows.Columns.Contains("__StudentGender"),
                    view+": private recorded parent/gender metadata is available to authorized renderers");
                Check(rows.AsEnumerable().Any(r=>r.Field<string>("Student")=="Three"
                    && r.Field<string>("__StudentFatherName")=="Parent of Three"
                    && r.Field<string>("__StudentGender")=="Female"),
                    view+": report metadata matches the actual student's saved parent/gender");
            }
            if(view=="Families")Check(rows.Rows.Count==4,"Separate standalone groups retain both unlinked students");
        }
        using var scoped=Read(connection,"EXEC dbo.GetOutstandingAndAgingReport @UserId,N'Families',@CampusId,@AcademicYearId,@ClassId,@SectionId,@AsOfDate;",campus:1);
        Check(scoped.AsEnumerable().Sum(r=>r.Field<decimal>("Outstanding"))==580m,"Family scope follows charge campus, including standalone students");
        bool denied=false;
        try{Read(connection,"EXEC dbo.GetOutstandingAndAgingReport @UserId,N'Families',@CampusId,@AcademicYearId,@ClassId,@SectionId,@AsOfDate;",user:0);}
        catch(SqlException){denied=true;}
        Check(denied,"Migrated debt reader retains report permission enforcement");
    }
}
