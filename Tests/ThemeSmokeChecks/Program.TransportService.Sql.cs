using System.Data;
using Microsoft.Data.SqlClient;
using FMSC_SMS.Core;

internal static partial class Program
{
    private static void CheckTransportServiceSql()
    {
        string connectionText=Environment.GetEnvironmentVariable("FMSC_TRANSPORT_TEST_CONNECTION")??throw new InvalidOperationException("Set the isolated transport test connection.");
        var builder=new SqlConnectionStringBuilder(connectionText);
        if(!builder.InitialCatalog.StartsWith("FMSC_Transport58_Test_",StringComparison.Ordinal))throw new InvalidOperationException("Transport SQL checks require their own disposable test database.");
        using var connection=new SqlConnection(connectionText);connection.Open();
        object Scalar(string sql,params SqlParameter[] args){using var command=new SqlCommand(sql,connection);command.Parameters.AddRange(args);return command.ExecuteScalar()!;}
        void Execute(string sql,params SqlParameter[] args){using var command=new SqlCommand(sql,connection){CommandTimeout=60};command.Parameters.AddRange(args);command.ExecuteNonQuery();}
        SqlParameter P(string name,object? value)=>new(name,value??DBNull.Value);
        DataSet Manage(int enrollment,bool start,int? fee=null,DateTime? from=null,string? snapshot=null,Guid? request=null)
        {
            using var command=new SqlCommand("dbo.ManageStudentTransport",connection){CommandType=CommandType.StoredProcedure,CommandTimeout=60};
            command.Parameters.AddRange(new[]{P("@UserId",1),P("@EnrollmentId",enrollment),P("@IsStart",start),P("@FeeTypeId",fee),P("@FromMonth",from),P("@Preview",snapshot is null),P("@ExpectedSnapshot",snapshot),P("@RequestId",request)});
            using var adapter=new SqlDataAdapter(command);var data=new DataSet();adapter.Fill(data);return data;
        }
        string Token(int enrollment,bool start){using var data=Manage(enrollment,start);return Convert.ToString(data.Tables[0].Rows[0]["Snapshot"])!;}
        bool Rejected(Action action,int code)
        {try{action();return false;}catch(SqlException ex){return ex.Number==code;}}
        Check(Convert.ToString(Scalar("SELECT TOP(1) VersionNumber FROM dbo.DatabaseVersion ORDER BY VersionId DESC"))==FMSC_SMS.DatabaseHelper.RequiredDatabaseSchemaVersion,"Transport integration uses the current migrated schema fixture");
        DatabaseCompatibility.Validate(connection);Check(true,"The migrated database satisfies the application's complete startup and recovery contract");
        string originalProcedure=System.Text.RegularExpressions.Regex.Replace(Convert.ToString(Scalar("SELECT OBJECT_DEFINITION(OBJECT_ID(N'dbo.ManageStudentTransport'))"))!,@"\ACREATE\s+(?:OR\s+ALTER\s+)?PROCEDURE","ALTER PROCEDURE",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var month=new DateTime(2026,10,1);
        // Only this disposable database receives a controlled clock. Production
        // always reads GETDATE; the source migration has no clock override.
        Execute(originalProcedure.Replace("CONVERT(date,GETDATE())","CONVERT(date,'20261015',112)"));
        try
        {
            void SaveProfile(int? cutoff)=>Execute("EXEC dbo.SaveSchoolProfile @UserId=1,@SchoolName=N'Transport Test School',@AddressLine=NULL,@City=NULL,@ContactNumber=NULL,@Email=NULL,@LogoImage=NULL,@LogoFileName=NULL,@LogoContentType=NULL,@FeeProrationCutoffDay=1,@TransportCutoffDay=@Cutoff",P("@Cutoff",cutoff));
            SaveProfile(19);
            Check(Convert.ToInt32(Scalar("SELECT TransportCutoffDay FROM dbo.SchoolProfile"))==19&&Convert.ToInt32(Scalar("SELECT FeeProrationCutoffDay FROM dbo.SchoolProfile"))==1,"School Profile saves the transport cutoff independently");
            Check(Rejected(()=>SaveProfile(29),59001),"The transport cutoff rejects days outside 1–28");

            // Independent media operations must not silently save or overwrite
            // unrelated School Profile fields or the certificate signature.
            Execute("EXEC dbo.SaveSchoolLogo @UserId=1,@LogoImage=0x89504E470D0A1A0A,@LogoFileName=N'fixture.png',@LogoContentType=N'image/png';");
            Execute("EXEC dbo.SaveSchoolCertificateSignature @UserId=1,@SignatureImage=0x89504E470D0A1A0A,@SignatureContentType=N'image/png',@PrintSignature=1;");
            const string schoolPhones="03001234567, 03121234567, 03331234567, 03441234567";
            Execute("EXEC dbo.SaveSchoolProfileDetails @UserId=1,@SchoolName=N'Profile-only edit',@AddressLine=NULL,@City=NULL,@ContactNumber=@Phones,@Email=NULL,@FeeProrationCutoffDay=1,@TransportCutoffDay=19;",P("@Phones",schoolPhones));
            Check(Convert.ToString(Scalar("SELECT ContactNumber FROM dbo.SchoolProfile WHERE SchoolProfileId=1"))==schoolPhones,
                "More than two complete school phone numbers survive SQL persistence without truncation");
            Check(Convert.ToInt32(Scalar("SELECT COUNT(*) FROM dbo.SchoolProfile WHERE SchoolName=N'Profile-only edit' AND LogoImage=0x89504E470D0A1A0A AND LogoContentType=N'image/png' AND PrincipalSignatureImage=0x89504E470D0A1A0A AND PrintPrincipalSignature=1"))==1,
                "Saving school details preserves both saved media objects and signature printing");

            Execute("EXEC dbo.SaveSchoolLogo @UserId=1,@LogoImage=NULL,@LogoFileName=NULL,@LogoContentType=NULL;");
            Check(Convert.ToInt32(Scalar("SELECT COUNT(*) FROM dbo.SchoolProfile WHERE SchoolName=N'Profile-only edit' AND LogoImage IS NULL AND LogoFileName IS NULL AND LogoContentType IS NULL AND PrincipalSignatureImage IS NOT NULL AND PrintPrincipalSignature=1 AND TransportCutoffDay=19"))==1,
                "Removing the logo never removes the signature or modifies school details");

            Execute("EXEC dbo.SaveSchoolCertificateSignature @UserId=1,@SignatureImage=NULL,@SignatureContentType=NULL,@PrintSignature=0;");
            Check(Convert.ToInt32(Scalar("SELECT COUNT(*) FROM dbo.SchoolProfile WHERE SchoolName=N'Profile-only edit' AND PrincipalSignatureImage IS NULL AND PrincipalSignatureContentType IS NULL AND PrintPrincipalSignature=0 AND LogoImage IS NULL"))==1,
                "Removing the signature with SQL NULL content type works independently");
            Check(Rejected(()=>Execute("EXEC dbo.SaveSchoolCertificateSignature @UserId=1,@SignatureImage=NULL,@SignatureContentType=N'';"),51121),
                "An empty signature MIME type is not sent in place of SQL NULL");

            SaveProfile(null);Check(Convert.ToDateTime(Scalar("SELECT dbo.GetTransportBillingMonth('20261031')"))==month,"A blank transport cutoff permits this month without using the general fee cutoff");
            Execute("UPDATE dbo.AcademicYears SET StartDate='20260401',EndDate='20270331' WHERE IsCurrent=1; UPDATE dbo.SchoolProfile SET FeeProrationCutoffDay=1,TransportCutoffDay=16;");
            using var configured=new DataTable();
            using(var command=new SqlCommand("SELECT TOP(1) C.FeeTypeId,C.CampusId,C.ClassId,CS.ClassSectionId,Y.AcademicYearId FROM dbo.FeeConfigurations C JOIN dbo.FeeTypes F ON F.FeeTypeId=C.FeeTypeId AND F.IsTransportLinked=1 JOIN dbo.CampusClassSections CS ON CS.CampusId=C.CampusId AND CS.ClassId=C.ClassId CROSS JOIN dbo.AcademicYears Y WHERE C.Frequency=N'Monthly' AND Y.IsCurrent=1 ORDER BY CS.ClassSectionId,C.FeeTypeId",connection))
            using(var adapter=new SqlDataAdapter(command))adapter.Fill(configured);
            Check(configured.Rows.Count==1,"A configured transport class is available in the disposable snapshot");
            var config=configured.Rows[0];int fee=Convert.ToInt32(config["FeeTypeId"]);
            Execute("UPDATE dbo.FeeConfigurations SET Amount=2600 WHERE FeeTypeId=@Fee AND CampusId=@Campus AND ClassId=@Class",P("@Fee",fee),P("@Campus",config["CampusId"]),P("@Class",config["ClassId"]));
            int NewPlacement()
            {
                return Convert.ToInt32(Scalar("""
                    DECLARE @Number varchar(40)=CONVERT(varchar(40),900001+(SELECT COALESCE(MAX(AdmissionId),0) FROM dbo.Students));
                    INSERT dbo.Students(AdmissionNumber,AdmissionDate,AdmissionCampusId,StudentName,FatherName,Gender,Status,LegacySetupPending)
                     VALUES(@Number,'20261001',@Campus,N'Transport Test Student',N'Transport Test Parent',N'Male',N'Active',0);
                    DECLARE @Student int=CONVERT(int,SCOPE_IDENTITY());
                    INSERT dbo.Enrollments(AdmissionId,AcademicYearId,ClassSectionId,EnrollmentDate,BillingStartDate,Status,BillingReady,PlacementConfirmedAt,UsesTransport)
                     VALUES(@Student,@Year,@Placement,'20261001','20261001',N'Active',1,SYSDATETIME(),0);
                    SELECT CONVERT(int,SCOPE_IDENTITY());
                    """,P("@Campus",config["CampusId"]),P("@Year",config["AcademicYearId"]),P("@Placement",config["ClassSectionId"])));
            }
            void Pay(int enrollment,decimal amount)
            {
                Execute("""
                    DECLARE @Student int=(SELECT AdmissionId FROM dbo.Enrollments WHERE EnrollmentId=@Enrollment),
                     @Bill bigint=(SELECT MAX(BillingChargeId) FROM dbo.BillingCharges WHERE EnrollmentId=@Enrollment AND Status<>N'Cancelled');
                    INSERT dbo.FeePayments(AdmissionId,EnrollmentId,PaymentDate,ReceiptNumber,TotalAmount,PaymentMethod,Status,CreatedByUserId)
                     VALUES(@Student,@Enrollment,'20261015',CONCAT(N'T-',NEWID()),@Amount,N'Cash',N'Completed',1);
                    DECLARE @Payment int=CONVERT(int,SCOPE_IDENTITY());
                    INSERT dbo.FeePaymentAllocations(FeePaymentId,AdmissionId,BillingChargeId,Amount) VALUES(@Payment,@Student,@Bill,@Amount);
                    UPDATE dbo.BillingCharges SET Status=CASE WHEN dbo.GetFeeChargeBalance(@Bill)=0 THEN N'Paid' ELSE N'PartiallyPaid' END WHERE BillingChargeId=@Bill;
                    """,P("@Enrollment",enrollment),P("@Amount",amount));
            }
            int current=NewPlacement();var key=Guid.NewGuid();string before=Token(current,true);
            using(var started=Manage(current,true,fee,month,before,key))
                Check(Convert.ToInt32(started.Tables[0].Rows[0]["ChargesCreated"])==1,"Before transport cutoff a current-month start posts immediately");
            Check(Convert.ToDecimal(Scalar("SELECT SUM(dbo.GetFeeChargeBalance(BillingChargeId)) FROM dbo.BillingCharges WHERE EnrollmentId=@Id",P("@Id",current)))==2600m,"Immediate transport posting has the configured whole amount");
            using(var retried=Manage(current,true,fee,month,before,key))
                Check(Convert.ToInt32(retried.Tables[0].Rows[0]["ChargesCreated"])==1&&Convert.ToInt32(Scalar("SELECT COUNT(*) FROM dbo.BillingCharges WHERE EnrollmentId=@Id",P("@Id",current)))==1,"Retrying a committed request returns its result without a duplicate charge");
            Check(Rejected(()=>Manage(current,true,fee,month.AddMonths(1),before,key).Dispose(),53140),"A request marker cannot be reused for different choices");
            using(var stopped=Manage(current,false,fee,month,Token(current,false),Guid.NewGuid()))
                Check(Convert.ToInt32(stopped.Tables[0].Rows[0]["ChargesCancelled"])==1&&Convert.ToDecimal(stopped.Tables[0].Rows[0]["CancelledAmount"])==2600m,"A wholly unpaid current charge is cancelled before transport cutoff");
            Check(Convert.ToInt32(Scalar("SELECT COUNT(*) FROM dbo.BillingCharges WHERE EnrollmentId=@Id AND Status=N'Cancelled'",P("@Id",current)))==1,"Cancellation keeps the original charge as history");
            using(var restarted=Manage(current,true,fee,month,Token(current,true),Guid.NewGuid()))
                Check(Convert.ToInt32(restarted.Tables[0].Rows[0]["ChargesCreated"])==1,"Restarting the same permitted month posts a new charge without reviving the cancelled copy");
            Check(Convert.ToInt32(Scalar("SELECT COUNT(*) FROM dbo.BillingCharges WHERE EnrollmentId=@Id",P("@Id",current)))==2,"Restart retains cancelled and current charge identities separately");
            foreach(decimal paid in new[]{1000m,2600m})
            {
                int placement=NewPlacement();using var start=Manage(placement,true,fee,month,Token(placement,true),Guid.NewGuid());Pay(placement,paid);
                string snapshot=Token(placement,false);
                Check(Rejected(()=>Manage(placement,false,fee,month,snapshot,Guid.NewGuid()).Dispose(),53144),"Any recorded payment protects the current transport month: "+paid);
                using var stop=Manage(placement,false,fee,month.AddMonths(1),Token(placement,false),Guid.NewGuid());
                Check(Convert.ToInt32(stop.Tables[0].Rows[0]["ChargesCancelled"])==0&&Convert.ToDecimal(Scalar("SELECT SUM(dbo.GetFeeChargeBalance(BillingChargeId)) FROM dbo.BillingCharges WHERE EnrollmentId=@Id",P("@Id",placement)))==2600m-paid,"Next-month stopping preserves paid money and any unpaid current remainder: "+paid);
                Check(Convert.ToDecimal(Scalar("SELECT SUM(A.Amount) FROM dbo.FeePaymentAllocations A JOIN dbo.FeePayments P ON P.FeePaymentId=A.FeePaymentId WHERE P.EnrollmentId=@Id",P("@Id",placement)))==paid,"Transport never alters the recorded payment allocations");
            }
            int late=NewPlacement();Execute("UPDATE dbo.SchoolProfile SET TransportCutoffDay=15");
            Check(Convert.ToDateTime(Scalar("SELECT dbo.GetTransportBillingMonth('20261015')"))==month.AddMonths(1),"The cutoff day itself defaults to the next transport month");
            Check(Rejected(()=>Manage(late,true,fee,month,Token(late,true),Guid.NewGuid()).Dispose(),53144),"Current-month start is refused on or after the transport cutoff");
            Check(Rejected(()=>Manage(current,false,fee,month,Token(current,false),Guid.NewGuid()).Dispose(),53144),"Even a wholly unpaid current month stays owed when stopping on or after cutoff");
            using(var laterStop=Manage(current,false,fee,month.AddMonths(1),Token(current,false),Guid.NewGuid()))
                Check(Convert.ToInt32(laterStop.Tables[0].Rows[0]["ChargesCancelled"])==0&&Convert.ToDecimal(Scalar("SELECT SUM(dbo.GetFeeChargeBalance(BillingChargeId)) FROM dbo.BillingCharges WHERE EnrollmentId=@Id AND Status<>N'Cancelled'",P("@Id",current)))==2600m,"Next-month stopping after cutoff preserves the current unpaid transport charge");
            using(var future=Manage(late,true,fee,month.AddMonths(1),Token(late,true),Guid.NewGuid()))
                Check(Convert.ToInt32(future.Tables[0].Rows[0]["ChargesCreated"])==0,"A future transport start creates the plan without posting future dues now");
            Execute("UPDATE dbo.SchoolProfile SET TransportCutoffDay=16");
            int stale=NewPlacement();string oldToken=Token(stale,true);Execute("UPDATE dbo.SchoolProfile SET TransportCutoffDay=17");
            Check(Rejected(()=>Manage(stale,true,fee,month,oldToken,Guid.NewGuid()).Dispose(),53141),"A school cutoff changed after loading invalidates the transport confirmation");
            int payRace=NewPlacement();using var raceStart=Manage(payRace,true,fee,month,Token(payRace,true),Guid.NewGuid());string prePayment=Token(payRace,false);Pay(payRace,500m);
            Check(Rejected(()=>Manage(payRace,false,fee,month,prePayment,Guid.NewGuid()).Dispose(),53141),"A payment recorded after loading cannot be cancelled using an old snapshot");
            int futureCancelled=NewPlacement();using var advance=Manage(futureCancelled,true,fee,month.AddMonths(1),Token(futureCancelled,true),Guid.NewGuid());
            Execute("INSERT dbo.BillingCharges(StudentFeeAssignmentId,EnrollmentId,AdmissionId,FeeTypeId,BillingMonth,ChargeDate,DueDate,GrossAmount,DiscountAmount,ChargeKind) SELECT StudentFeeAssignmentId,EnrollmentId,AdmissionId,FeeTypeId,'20261101','20261101','20261101',Amount,0,N'Monthly' FROM dbo.StudentFeeAssignments WHERE EnrollmentId=@Id AND CancelledAt IS NULL AND EffectiveTo IS NULL",P("@Id",futureCancelled));
            using var futureStop=Manage(futureCancelled,false,fee,month.AddMonths(1),Token(futureCancelled,false),Guid.NewGuid());
            using var futureRestart=Manage(futureCancelled,true,fee,month.AddMonths(1),Token(futureCancelled,true),Guid.NewGuid());
            Check(Convert.ToBoolean(Scalar("SELECT PreviouslyCancelled FROM dbo.fn_MonthlyBillingCandidates('20261101',0) WHERE EnrollmentId=@Id AND BlockReason IS NULL",P("@Id",futureCancelled)))==false,"Normal future billing recognizes the new transport period rather than blocking it on an older cancelled copy");
            using(var post=new SqlCommand("DECLARE @Rows dbo.PostingSelectionType; INSERT @Rows VALUES(@Id,@Fee,'20261101'); EXEC dbo.PostSelectedMonthlyFees 1,'20261101',@Rows,N'Transport restart test',@Year,@AllowRepost=0;",connection))
            {post.Parameters.AddRange(new[]{P("@Id",futureCancelled),P("@Fee",fee),P("@Year",config["AcademicYearId"])});post.ExecuteNonQuery();}
            Check(Convert.ToInt32(Scalar("SELECT COUNT(*) FROM dbo.BillingCharges WHERE EnrollmentId=@Id AND Status<>N'Cancelled'",P("@Id",futureCancelled)))==1,"Normal automatic-style posting charges the future restart exactly once");
            Check(Convert.ToInt32(Scalar("SELECT COUNT(*) FROM dbo.TransportServiceRequests WHERE RequestId=@Request",P("@Request",key)))==1,"Transport keeps a committed recovery marker for its saved request");
        }
        finally{Execute(originalProcedure);}
    }
}
