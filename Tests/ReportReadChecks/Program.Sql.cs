using Microsoft.Data.SqlClient;
using FMSC_SMS.Pages.Reports.Finance;

internal static partial class Program
{
    private static DataTable Read(SqlConnection connection,string query,int user=1,int campus=0,Action<SqlCommand>? configure=null)
    {
        using var command=new SqlCommand(query,connection);
        command.Parameters.AddWithValue("@UserId",user); command.Parameters.AddWithValue("@CampusId",campus);
        command.Parameters.AddWithValue("@ClassId",0);command.Parameters.AddWithValue("@SectionId",0);
        command.Parameters.Add("@FromDate",SqlDbType.Date).Value=new DateTime(2026,1,1);
        command.Parameters.Add("@ToDate",SqlDbType.Date).Value=new DateTime(2026,12,31);
        command.Parameters.AddWithValue("@AcademicYearId",0);
        command.Parameters.Add("@AsOfDate",SqlDbType.Date).Value=new DateTime(2026,10,21);
        foreach(string parameter in new[]{"@CertificateType","@RecipientType","@EntryKind","@CollectionStatus"}) command.Parameters.Add(parameter,SqlDbType.NVarChar,100).Value=DBNull.Value;
        command.Parameters.AddWithValue("@Search","");
        configure?.Invoke(command);
        var data=new DataTable();using var adapter=new SqlDataAdapter(command);adapter.Fill(data);return data;
    }
    private static void Execute(SqlConnection connection,string sql)
    { using var command=new SqlCommand(sql,connection);command.ExecuteNonQuery(); }
    private static void CheckSql()
    {
        var supplied=Environment.GetEnvironmentVariable("FMSC_REPORT_TEST_CONNECTION")
            ??throw new InvalidOperationException("SQL checks need FMSC_REPORT_TEST_CONNECTION for a local disposable SQL Server, with Initial Catalog=master.");
        var builder=new SqlConnectionStringBuilder(supplied);
        if (builder.InitialCatalog!="master" || !(builder.DataSource.StartsWith("127.0.0.1",StringComparison.OrdinalIgnoreCase)
            ||builder.DataSource.StartsWith("localhost",StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("SQL fixture refuses non-local servers and any catalog other than master.");
        string database="Fmsc_ReportReadChecks_"+Guid.NewGuid().ToString("N");
        using var master=new SqlConnection(builder.ConnectionString);master.Open();Execute(master,$"CREATE DATABASE [{database}];");
        string sqlStage="initial fixture";
        try
        {
            builder.InitialCatalog=database;
            using var connection=new SqlConnection(builder.ConnectionString);connection.Open();
            Execute(connection,"""
                CREATE TABLE dbo.Students(AdmissionId int PRIMARY KEY,AdmissionNumber nvarchar(50),QuickReference nvarchar(50),StudentName nvarchar(100),FatherName nvarchar(100),Status nvarchar(30));
                CREATE TABLE dbo.Families(FamilyId int PRIMARY KEY,FamilyCode nvarchar(50));
                CREATE TABLE dbo.FamilyMembers(AdmissionId int UNIQUE,FamilyId int);
                CREATE TABLE dbo.StudentCreditEntries(AdmissionId int,Amount decimal(12,2),CreatedAt datetime2);
                CREATE TABLE dbo.Campuses(CampusId int PRIMARY KEY,CampusName nvarchar(100),DisplayOrder int);
                CREATE TABLE dbo.Classes(ClassId int PRIMARY KEY,ClassName nvarchar(100),DisplayOrder int);
                CREATE TABLE dbo.Sections(SectionId int PRIMARY KEY,SectionName nvarchar(100),DisplayOrder int);
                CREATE TABLE dbo.CampusClassSections(ClassSectionId int PRIMARY KEY,CampusId int,ClassId int,SectionId int);
                CREATE TABLE dbo.Enrollments(EnrollmentId int PRIMARY KEY,AdmissionId int,AcademicYearId int,ClassSectionId int,Status nvarchar(30),EnrollmentDate date,BillingStartDate date);
                CREATE TABLE dbo.AcademicYears(AcademicYearId int PRIMARY KEY,EndDate date);
                CREATE TABLE dbo.FeeTypes(FeeTypeId int PRIMARY KEY,FeeTypeName nvarchar(100));
                CREATE TABLE dbo.StudentFeeAssignments(AdmissionId int,EnrollmentId int,FeeTypeId int,Frequency nvarchar(30),ConcessionLabel nvarchar(100),DiscountAmount decimal(12,2),CancelledAt datetime2,EffectiveFrom date,EffectiveTo date);
                INSERT dbo.Students VALUES(1,N'1001',NULL,N'One',N'Parent',N'Active'),(2,N'1002',NULL,N'Two',N'Parent',N'Active'),(3,NULL,N'QR-3',N'Three',N'Parent',N'Pending'),(4,N'1004',NULL,N'Four',N'Parent',N'Active'),(5,N'1005',NULL,N'Five',N'Parent',N'Active'),(6,N'1006',NULL,N'Six',N'Parent',N'Active');
                ALTER TABLE dbo.Students ADD Gender nvarchar(20) NULL;
                INSERT dbo.Families VALUES(10,N'FAM-10'),(20,N'FAM-20');INSERT dbo.FamilyMembers VALUES(1,10),(2,10),(6,20);
                INSERT dbo.StudentCreditEntries VALUES(1,1000,'2026-10-01'),(1,-200,'2026-10-02'),(1,-100,'2026-10-03'),(2,100,'2026-10-03'),(2,50,'2026-10-04'),(3,100,'2026-10-03'),(4,500,'2026-10-01'),(4,-500,'2026-10-02'),(5,-20,'2026-10-01'),(6,250,'2026-10-01');
                INSERT dbo.Campuses VALUES(1,N'Boys',1),(2,N'Girls',2);INSERT dbo.Classes VALUES(1,N'Class 1',1);INSERT dbo.Sections VALUES(1,N'A',1);
                INSERT dbo.CampusClassSections VALUES(1,1,1,1),(2,2,1,1);
                INSERT dbo.Enrollments VALUES(1,1,1,2,N'Ended','2025-01-01','2025-01-01'),(2,1,1,1,N'Active','2026-01-01','2026-01-01'),(3,2,1,1,N'Active','2026-01-01','2026-01-01'),(4,6,1,2,N'Active','2026-01-01','2026-01-01');
                INSERT dbo.AcademicYears VALUES(1,'2026-12-31');INSERT dbo.FeeTypes VALUES(1,N'Tuition'),(2,N'Annual');
                INSERT dbo.StudentFeeAssignments VALUES(1,2,1,N'Monthly',N'Sibling',100,NULL,'2026-01-01',NULL),(2,3,1,N'Monthly',N'Sibling',50,NULL,'2026-01-01',NULL),(1,2,2,N'Yearly',N'Sibling',1000,NULL,'2026-01-01',NULL),(1,2,1,N'Monthly',N'Cancelled',900,'2026-01-02','2026-01-01',NULL),(1,1,1,N'Monthly',N'Old',700,NULL,'2025-01-01','2025-12-31'),(1,2,1,N'Monthly',N'Future',800,NULL,'2027-01-01','2027-12-31');
                ALTER TABLE dbo.Enrollments ADD LeavingDate date NULL;
                ALTER TABLE dbo.AcademicYears ADD StartDate date NOT NULL DEFAULT('2026-01-01');
                ALTER TABLE dbo.StudentFeeAssignments ADD StudentFeeAssignmentId int IDENTITY(1,1);
                INSERT dbo.StudentFeeAssignments(AdmissionId,EnrollmentId,FeeTypeId,Frequency,ConcessionLabel,DiscountAmount,EffectiveFrom)
                    VALUES(1,2,1,N'Monthly',N'Sibling',150,'2026-10-01');
                """);
            Execute(connection,"""
                CREATE PROCEDURE dbo.RequirePermission @UserId int,@Permission nvarchar(100) AS
                BEGIN
                    IF @UserId NOT IN(1,2,3,4) THROW 51000,'Permission denied in synthetic fixture.',1;
                    IF @UserId=2 AND @Permission=N'Fees.View' THROW 51000,'Fee viewing denied in synthetic fixture.',1;
                    IF @UserId IN(2,4) AND @Permission=N'Certificates.Create' THROW 51000,'Certificate viewing denied in synthetic fixture.',1;
                    IF @UserId IN(2,3) AND @Permission=N'Students.Leave' THROW 51000,'Leaving certificate viewing denied in synthetic fixture.',1;
                END;
                """);
            using var credit=Read(connection,CreditBalanceReportData.Query);
            Check(credit.Rows.Count==4&&credit.AsEnumerable().Sum(r=>r.Field<decimal>("Available Credit"))==1200m,"SQL ledger sum includes signed activity and excludes zero/negative balances");
            Check(credit.AsEnumerable().Single(r=>r.Field<int>("AdmissionId")==1).Field<string>("Campus")=="Boys","SQL chooses current placement instead of multiplying historic enrollments");
            using var scoped=Read(connection,CreditBalanceReportData.Query,campus:1);
            Check(scoped.Rows.Count==2&&scoped.AsEnumerable().Sum(r=>r.Field<decimal>("Available Credit"))==850m,"SQL campus scope reconciles exactly");
            Check(CreditBalanceReportData.Create(credit,true,0).Rows.Rows.Count==3,"SQL family and unassigned groups remain distinct");
            using var concessions=Read(connection,ConcessionSummaryData.Query);
            Check(concessions.Rows.Count==3,"SQL concession scope excludes cancelled, expired and future assignments");
            var currentTerms=ConcessionSummaryData.Create(concessions).Rows;
            Check(currentTerms.Rows.Count==2,"SQL concession cycles stay separate");
            Check(currentTerms.AsEnumerable().Single(r=>r.Field<string>("Frequency")=="Monthly").Field<decimal>("Assigned Discount / Cycle (Rs.)")==200m,
                "As-of summary selects latest eligible term rather than summing superseded rates");
            using var earlier=Read(connection,ConcessionSummaryData.Query,configure:c=>c.Parameters["@AsOfDate"].Value=new DateTime(2026,9,30));
            Check(earlier.AsEnumerable().Single(r=>r.Field<int>("AdmissionId")==1 && r.Field<string>("Frequency")=="Monthly").Field<decimal>("Assigned Discount")==100m,
                "Earlier as-of date uses previous term");
            Execute(connection,"INSERT dbo.StudentFeeAssignments(AdmissionId,EnrollmentId,FeeTypeId,Frequency,ConcessionLabel,DiscountAmount,EffectiveFrom) VALUES(1,2,1,N'Monthly',NULL,0,'2026-10-15');");
            using var ordinary=Read(connection,ConcessionSummaryData.Query);
            Check(!ordinary.AsEnumerable().Any(r=>r.Field<int>("AdmissionId")==1 && r.Field<int>("FeeTypeId")==1),
                "A newer ordinary term does not revive the student's earlier concession");
            bool denied=false;try{Read(connection,CreditBalanceReportData.Query,user:2);}catch(SqlException){denied=true;}
            Check(denied,"Credit query enforces fee viewing in addition to report viewing");
            denied=false;try{Read(connection,ConcessionSummaryData.Query,user:0);}catch(SqlException){denied=true;}
            Check(denied,"Concession query enforces report permission");
            sqlStage="reconciliation and register";
            CheckNewSql(connection);
            sqlStage="outstanding debt";
            CheckDebtSql(connection);
            sqlStage="collection receipts";
            CheckCollectionSql(connection);
        }
        catch(SqlException ex)
        {
            // Public CI may retain only an allowlisted stage and numeric SQL
            // error. No connection string, SQL text or student rows are logged.
            Console.Error.WriteLine($"REPORT_SQL_FAIL: {sqlStage} SQL {ex.Number} line {ex.LineNumber}");
            if(ex.Number==207)
            {
                // SQL 207 is an invalid schema identifier, not a row value.
                // Emit only a narrowly formatted identifier from this owned
                // fictional fixture; reject paths, SQL text and user input.
                var column=System.Text.RegularExpressions.Regex.Match(ex.Message,
                    @"Invalid column name '([A-Za-z_][A-Za-z0-9_]{0,63})'");
                if(column.Success)
                    Console.Error.WriteLine($"REPORT_COLUMN_FAIL: {sqlStage} {column.Groups[1].Value}");
            }
            throw;
        }
        catch(Exception ex)
        {
            // Only report the fixed synthetic fixture filename and source line,
            // not its assertion text, SQL query, credentials or row values.
            string[] allowed={"Program.NewReportSql.cs","Program.DebtSql.cs","Program.CollectionSql.cs"};
            var location=new System.Diagnostics.StackTrace(ex,true).GetFrames()?
                .Select(frame => (File:Path.GetFileName(frame.GetFileName()),Line:frame.GetFileLineNumber()))
                .FirstOrDefault(frame=>allowed.Contains(frame.File) && frame.Line>0);
            if(location is { Line: > 0 })
                Console.Error.WriteLine($"REPORT_ASSERT_FAIL: {sqlStage} {location.Value.File} {location.Value.Line}");
            throw;
        }
        finally { SqlConnection.ClearAllPools();Execute(master,$"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];"); }
    }
}
