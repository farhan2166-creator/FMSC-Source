using System.Text.Json;
using Microsoft.Data.SqlClient;

internal static partial class Program
{
    // Executed only after CheckNewSql/CheckDebtSql in a uniquely named, owned
    // disposable SQL database. No school data or production credentials.
    private static void CheckCollectionSql(SqlConnection connection)
    {
        // The other report fixtures have already checked their own totals.
        // Isolate this receipt scenario without changing production tables.
        Execute(connection,"""
            UPDATE dbo.FeePayments SET Status=N'FixtureOnly' WHERE FeePaymentId<10;
            ALTER TABLE dbo.FeePayments ADD
                EnrollmentId int NULL, ReceiptNumber nvarchar(50) NULL,
                CreatedAt datetime2 NULL, PaymentMethod nvarchar(40) NULL,
                ReferenceNumber nvarchar(60) NULL, RelatedFeePaymentId bigint NULL,
                AdmissionId int NULL, TotalAmount decimal(12,2) NULL,
                CreatedByUserId int NULL;
            CREATE TABLE dbo.CollectionReceipts(
                CollectionReceiptId int PRIMARY KEY, ReceiptNumber nvarchar(50),
                SnapshotJson nvarchar(max));
            CREATE TABLE dbo.CollectionReceiptPayments(
                CollectionReceiptId int, FeePaymentId bigint);
            UPDATE dbo.Students SET Gender=
                CASE AdmissionId WHEN 1 THEN N'Female' WHEN 2 THEN N'Male' END
                WHERE AdmissionId IN(1,2);
            INSERT dbo.CollectionReceipts VALUES
                (700,N'FR-TEST-001',N'{"Students":[{"AdmissionId":1,"StudentName":"One"},{"AdmissionId":2,"StudentName":"Two"}]}');
            INSERT dbo.FeePayments
                (FeePaymentId,Status,IsCreditApplication,PaymentDate,EnrollmentId,
                 ReceiptNumber,CreatedAt,PaymentMethod,ReferenceNumber,RelatedFeePaymentId,
                 AdmissionId,TotalAmount,CreatedByUserId)
            VALUES
                (10,N'Completed',0,'2026-10-10',2,N'FR-TEST-001','2026-10-10T10:00:00',N'Cash',N'C1',NULL,1,200,1),
                (11,N'Completed',0,'2026-10-10',3,N'FR-TEST-001','2026-10-10T10:01:00',N'Bank Transfer',N'B1',NULL,2,100,1),
                (12,N'Completed',1,'2026-10-10',2,N'FR-TEST-001','2026-10-10T10:02:00',N'Student Credit',N'CR1',NULL,1,50,1),
                (13,N'Completed',0,'2026-10-10',4,N'SR-TEST-002','2026-10-10T10:03:00',N'Cash',N'C2',NULL,6,70,1),
                (14,N'Completed',0,'2026-10-10',4,N'SR-TEST-002','2026-10-10T10:04:00',N'Refund',N'REF2',13,6,-20,1),
                (15,N'Completed',0,'2026-10-10',4,N'SR-TEST-002','2026-10-10T10:05:00',N'Correction',N'COR2',13,6,-5,1);
            INSERT dbo.CollectionReceiptPayments VALUES(700,10),(700,11),(700,12);
            """);

        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName,
            "Core","DatabaseCompatibilityRequirements.json"))) folder=folder.Parent;
        if (folder is null) throw new InvalidOperationException("SQL contract checkout missing.");
        var sql = File.ReadAllText(Path.Combine(folder.FullName,"Tests",
            "LifecycleSqlChecks","Baseline","Sql","GetCollectionsAndReceiptsReport.sql"));
        foreach (string batch in System.Text.RegularExpressions.Regex.Split(sql,@"(?im)^GO\s*$"))
            if (!string.IsNullOrWhiteSpace(batch)) Execute(connection,batch);

        DataTable Report(string view="Receipts",string grouping="Day",int campus=0,
            string? paymentMethod=null,int user=1, bool badDates=false) =>
            Read(connection,
                "EXEC dbo.GetCollectionsAndReceiptsReport @UserId,@View,@GroupBy,@FromDate,@ToDate,@CampusId,@ClassId,@SectionId,@PaymentMethod;",
                user:user,campus:campus,configure:command => {
                    command.Parameters.AddWithValue("@View",view);
                    command.Parameters.AddWithValue("@GroupBy",grouping);
                    command.Parameters.Add("@PaymentMethod",SqlDbType.NVarChar,40).Value =
                        (object?)paymentMethod ?? DBNull.Value;
                    if (badDates)
                    {
                        command.Parameters["@FromDate"].Value=new DateTime(2026,11,1);
                        command.Parameters["@ToDate"].Value=new DateTime(2026,10,1);
                    }
                });

        using var receipts=Report();
        Check(receipts.Rows.Count==2,"Family receipt and individual receipt each appear exactly once");
        var family=receipts.AsEnumerable().Single(row=>
            row.Field<string>("Receipt / Reference")=="FR-TEST-001");
        var individual=receipts.AsEnumerable().Single(row=>
            row.Field<string>("Receipt / Reference")=="SR-TEST-002");
        Check(family.Field<string>("Scope")=="Family"
            && family.Field<string>("Student / Family")!.Contains("One")
            && family.Field<string>("Student / Family")!.Contains("Two")
            && family.Field<decimal>("Money Received")==300m
            && family.Field<decimal>("Credit Applied")==50m
            && family.Field<decimal>("Adjusted Collection")==300m,
            "One family receipt retains both students, two cash/bank payments and separate credit exactly once");
        var methods=family.Field<string>("Payment Method") ?? "";
        Check(new[]{"Cash","Bank Transfer","Student Credit"}.All(methods.Contains)
            && !methods.Contains("Mixed",StringComparison.OrdinalIgnoreCase),
            "Receipt lists actual distinct sending methods, not a fabricated mixed-method label");
        Check(individual.Field<string>("Scope")=="Individual"
            && individual.Field<decimal>("Money Received")==70m
            && individual.Field<decimal>("Refunds")==20m
            && individual.Field<decimal>("Corrections")==5m
            && individual.Field<decimal>("Adjusted Collection")==45m,
            "Negative refunds and corrections reduce cash collection but do not change gross receipts");
        using (var roster=JsonDocument.Parse(family.Field<string>("__ReceiptStudents")!))
        {
            var students=roster.RootElement.EnumerateArray().ToArray();
            Check(students.Length==2
                && students.Select(s=>s.GetProperty("AdmissionId").GetInt32()).Distinct().Count()==2,
                "Family roster contains both students exactly once, independently of payment count");
            Check(students.Any(s=>s.GetProperty("StudentName").GetString()=="One"
                && s.GetProperty("FatherName").GetString()=="Parent"
                && s.GetProperty("Gender").GetString()=="Female"
                && s.GetProperty("Placements").GetArrayLength()>0)
                && students.Any(s=>s.GetProperty("StudentName").GetString()=="Two"
                && s.GetProperty("Gender").GetString()=="Male"),
                "Authorized receipt roster includes actual recorded parent, gender and placement");
        }
        using var campusOnly=Report(campus:1);
        Check(campusOnly.Rows.Count==1
            && campusOnly.Rows[0].Field<string>("Receipt / Reference")=="FR-TEST-001"
            && campusOnly.Rows[0].Field<decimal>("Money Received")==300m
            && campusOnly.Rows[0].Field<decimal>("Credit Applied")==50m,
            "Campus scope excludes another campus without multiplying sibling receipts");
        using var cashOnly=Report(paymentMethod:"Cash");
        Check(cashOnly.Rows.Count==2
            && cashOnly.AsEnumerable().Sum(r=>r.Field<decimal>("Money Received"))==270m
            && cashOnly.AsEnumerable().Sum(r=>r.Field<decimal>("Credit Applied"))==0m,
            "Payment method filter includes only actual cash, not credit or bank transfers");
        using var summary=Report("Summary","Payment Method");
        Check(summary.AsEnumerable().Sum(r=>r.Field<decimal>("Money Received"))==370m
            && summary.AsEnumerable().Sum(r=>r.Field<decimal>("Refunds"))==20m
            && summary.AsEnumerable().Sum(r=>r.Field<decimal>("Corrections"))==5m
            && summary.AsEnumerable().Sum(r=>r.Field<decimal>("Credit Applied"))==50m
            && summary.AsEnumerable().Sum(r=>r.Field<decimal>("Adjusted Collection"))==345m,
            "Grouped financial summary reconciles actual money, corrections and credit without double-counting");
        bool denied=false;
        try { using var noPermission=Report(user:0); } catch(SqlException){ denied=true; }
        Check(denied,"Collections report refuses users without report permission");
        bool invalidDates=false;
        try { using var badRange=Report(badDates:true); } catch(SqlException){ invalidDates=true; }
        Check(invalidDates,"Collections report refuses an inverted date range");
    }
}
