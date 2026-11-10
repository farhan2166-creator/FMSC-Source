using FMSC_SMS.Core;
using Microsoft.Data.SqlClient;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FMSC_SMS.Pages.Students.Admissions;

// Keep preview validation and the existing stored procedure inside one transaction.
// Locks protect the comparison through the procedure's nested transaction/commit.
// BD-01/02/33: preserve the pending identity and durable request outcome; recheck placement/fees at commit.
// Only verified rollback is a correctable refusal; timeouts and lost responses stay uncertain.
internal static class QuickRegistrationWriter
{
    internal const string FeeQuery = """
        SELECT F.FeeTypeId,F.FeeTypeName,F.IsMandatory,F.IsTransportLinked,C.Amount,C.Frequency,
            CONVERT(bit,CASE WHEN F.IsMandatory=1 AND F.IsTransportLinked=0 THEN 1 ELSE 0 END) [Include],CONVERT(decimal(12,2),0) Discount,CONVERT(nvarchar(20),N'None') ConcessionLabel
        FROM dbo.CampusClassSections P CROSS JOIN dbo.FeeTypes F
        LEFT JOIN dbo.FeeConfigurations C ON C.CampusId=P.CampusId AND C.ClassId=P.ClassId AND C.FeeTypeId=F.FeeTypeId
        WHERE P.ClassSectionId=@Placement AND ((F.IsMandatory=1 AND F.IsTransportLinked=0) OR C.FeeTypeId IS NOT NULL)
        ORDER BY F.DisplayOrder,F.FeeTypeName;
        """;

    private const string SnapshotSql = """
        EXEC dbo.RequirePermission @UserId,N'Students.View';
        SELECT CONCAT(
            (SELECT S.* FROM dbo.Students S WITH(UPDLOCK,HOLDLOCK) WHERE AdmissionId=@Admission
                FOR JSON PATH,INCLUDE_NULL_VALUES),
            (SELECT E.* FROM dbo.Enrollments E WITH(UPDLOCK,HOLDLOCK) WHERE AdmissionId=@Admission
                ORDER BY EnrollmentId FOR JSON PATH,INCLUDE_NULL_VALUES),
            (SELECT A.* FROM dbo.StudentFeeAssignments A WITH(UPDLOCK,HOLDLOCK) WHERE AdmissionId=@Admission
                ORDER BY StudentFeeAssignmentId FOR JSON PATH,INCLUDE_NULL_VALUES),
            (SELECT FM.* FROM dbo.FamilyMembers FM WITH(UPDLOCK,HOLDLOCK)
                WHERE FM.AdmissionId=@Admission OR FM.FamilyId IN
                    (SELECT FamilyId FROM dbo.FamilyMembers WHERE AdmissionId=@Admission)
                ORDER BY FamilyMemberId FOR JSON PATH,INCLUDE_NULL_VALUES));
        """;

    private const string StoredFeesSql = """
        SELECT A.StudentFeeAssignmentId,F.FeeTypeId,F.FeeTypeName,F.IsMandatory,F.IsTransportLinked,
            A.Amount,A.Frequency,CONVERT(bit,1) [Include],A.DiscountAmount Discount,ISNULL(A.ConcessionLabel,N'None') ConcessionLabel,
            CONVERT(bit,CASE WHEN EXISTS(SELECT 1 FROM dbo.BillingCharges B JOIN dbo.FeePaymentAllocations PA ON PA.BillingChargeId=B.BillingChargeId
                JOIN dbo.FeePayments FP ON FP.FeePaymentId=PA.FeePaymentId WHERE B.StudentFeeAssignmentId=A.StudentFeeAssignmentId AND FP.Status=N'Completed') THEN 1 ELSE 0 END) HasPayments
        FROM dbo.StudentFeeAssignments A JOIN dbo.FeeTypes F ON F.FeeTypeId=A.FeeTypeId
        JOIN dbo.Enrollments E ON E.EnrollmentId=A.EnrollmentId
        WHERE E.AdmissionId=@Admission AND E.Status IN(N'Pending',N'Active') AND A.CancelledAt IS NULL AND A.EffectiveTo IS NULL
        ORDER BY F.DisplayOrder,F.FeeTypeName;
        """;

    internal sealed record Registration(DataTable Details, DataTable Fees, string Snapshot);

    internal static Task<Registration> LoadAsync(int admission)
    {
        int user = CurrentUser.UserId;
        return Task.Run(() => {
            using var connection = DatabaseHelper.GetConnection();
            DatabaseHelper.OpenConnection(connection);
            using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
            // Read the token first to lock the same rows that supply the displayed values.
            string snapshot = Snapshot(connection, transaction, admission, user);
            using var details = new SqlCommand("GetStudentDetails", connection, transaction) { CommandType=CommandType.StoredProcedure };
            details.Parameters.AddRange(new[] { DatabaseHelper.Int("@UserId",user), DatabaseHelper.Int("@AdmissionId",admission) });
            var data = new DataTable();
            using (var adapter = new SqlDataAdapter(details)) adapter.Fill(data);
            using var family = new SqlCommand("""
                SELECT COALESCE(FM.FamilyMemberId,0) FamilyMemberId,
                    (SELECT COUNT(*) FROM dbo.FamilyMembers WHERE FamilyId=FM.FamilyId) FamilyMemberCount
                FROM dbo.Students S LEFT JOIN dbo.FamilyMembers FM ON FM.AdmissionId=S.AdmissionId
                WHERE S.AdmissionId=@Admission;
                """,connection,transaction);
            family.Parameters.Add(DatabaseHelper.Int("@Admission",admission));
            using (var reader=family.ExecuteReader())
            {
                data.Columns.Add("FamilyMemberId",typeof(int));
                data.Columns.Add("FamilyMemberCount",typeof(int));
                if(reader.Read()&&data.Rows.Count==1)
                {
                    data.Rows[0]["FamilyMemberId"]=reader.GetInt32(0);
                    data.Rows[0]["FamilyMemberCount"]=reader.GetInt32(1);
                }
            }
            using var fees = new SqlCommand(StoredFeesSql, connection, transaction);
            fees.Parameters.Add(DatabaseHelper.Int("@Admission",admission));
            var rates = new DataTable();
            using (var adapter = new SqlDataAdapter(fees)) adapter.Fill(rates);
            transaction.Commit();
            return new Registration(data,rates,snapshot);
        });
    }

    private static string Snapshot(SqlConnection connection, SqlTransaction transaction, int admission, int user)
    {
        using var command = new SqlCommand(SnapshotSql,connection,transaction);
        command.Parameters.AddRange(new[] { DatabaseHelper.Int("@UserId",user),DatabaseHelper.Int("@Admission",admission) });
        return Convert.ToString(command.ExecuteScalar()) ?? "";
    }

    internal static string Quote(DataTable rates) => QuickRegistrationQuote.Create(rates);

    internal static string PayloadHash(int user,int placement,string quote,params SqlParameter[] parameters)
    {
        var values=parameters.Where(p=>!p.ParameterName.Equals("@UserId",StringComparison.OrdinalIgnoreCase))
            .OrderBy(p=>p.ParameterName,StringComparer.Ordinal).Select(p=>new {
                Name=p.ParameterName,
                Value=p.Value is DataTable table?(object)new {
                    Columns=table.Columns.Cast<DataColumn>().Select(c=>c.ColumnName).ToArray(),
                    Rows=table.Rows.Cast<DataRow>().Select(r=>r.ItemArray.Select(v=>v is DBNull?null:v).ToArray()).ToArray()
                }:p.Value is DBNull?null:p.Value
            });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{User=user,Placement=placement,Quote=quote,Values=values}))));
    }
    private static string Scope(SqlConnection connection)
    {
        using var command=new SqlCommand("SELECT TRY_CONVERT(uniqueidentifier,value) FROM sys.extended_properties WHERE class=0 AND major_id=0 AND minor_id=0 AND name=N'FMSC_QuickRegistrationScope';",connection);
        if(command.ExecuteScalar() is not Guid scope||scope==Guid.Empty)
            throw new InvalidDataException("The database recovery identity is missing. Verify migration 4 before registering students.");
        return Convert.ToHexString(SHA256.HashData(scope.ToByteArray()));
    }
    internal static Task<string> GetScopeAsync()=>Task.Run(()=>{
        using var connection=DatabaseHelper.GetConnection();DatabaseHelper.OpenConnection(connection);return Scope(connection);
    });
    private static void LockRequest(SqlConnection connection,SqlTransaction transaction,Guid id,bool readOnly)
    {
        using var command=new SqlCommand("""
            DECLARE @Result int;
            EXEC @Result=sys.sp_getapplock @Resource=@Resource,@LockMode=@Mode,@LockOwner=N'Transaction',@LockTimeout=8000;
            IF @Result<0 THROW 57510,'The registration request is still being processed. Wait, then check its save outcome again.',1;
            """,connection,transaction){CommandTimeout=12};
        command.Parameters.Add(new SqlParameter("@Resource",SqlDbType.NVarChar,255){Value="FMSC.QuickRegistration."+id.ToString("D")});
        command.Parameters.Add(new SqlParameter("@Mode",SqlDbType.NVarChar,32){Value=readOnly?"Shared":"Exclusive"});command.ExecuteNonQuery();
    }
    private static SavedQuickRegistration? ReadRequest(SqlConnection connection,SqlTransaction transaction,QuickRegistrationRecoveryJournal.Pending request)
    {
        using var command=new SqlCommand("""
            SELECT R.UserId,R.PayloadHash,S.AdmissionId,S.QuickReference FROM dbo.QuickRegistrationRequests R
            JOIN dbo.Students S ON S.AdmissionId=R.AdmissionId WHERE R.RequestId=@Request;
            """,connection,transaction);
        command.Parameters.Add(new SqlParameter("@Request",SqlDbType.UniqueIdentifier){Value=request.Id});
        using var reader=command.ExecuteReader();if(!reader.Read())return null;
        if(reader.GetInt32(0)!=request.UserId||!((byte[])reader[1]).SequenceEqual(Convert.FromHexString(request.PayloadHash)))
            throw new InvalidOperationException("The registration request belongs to different saved details. Keep its recovery record for administrator review.");
        var result=new SavedQuickRegistration(reader.GetInt32(2),reader.GetString(3));
        if(result.Id<=0||string.IsNullOrWhiteSpace(result.Reference))throw new InvalidDataException("The saved registration identity could not be verified.");
        return result;
    }
    internal static Task<SavedQuickRegistration?> CheckSaveAsync(QuickRegistrationRecoveryJournal.Pending request)
    {
        int user=CurrentUser.UserId;QuickRegistrationRecoveryJournal.Validate(request);
        return Task.Run(()=>{
            using var connection=DatabaseHelper.GetConnection();DatabaseHelper.OpenConnection(connection);
            if(Scope(connection)!=request.Scope)throw new InvalidOperationException("Reconnect to the database used for this registration before checking its save outcome.");
            using var transaction=connection.BeginTransaction();
            using var access=new SqlCommand("""
                EXEC dbo.RequirePermission @UserId,N'Students.View';
                IF @UserId<>@Owner EXEC dbo.RequirePermission @UserId,N'System.Administration';
                """,connection,transaction);
            access.Parameters.AddRange(new[]{DatabaseHelper.Int("@UserId",user),DatabaseHelper.Int("@Owner",request.UserId)});access.ExecuteNonQuery();
            // A missing marker is final only after the original request releases
            // its transaction lock. A timeout/offline check never authorizes retry.
            LockRequest(connection,transaction,request.Id,true);
            var saved=ReadRequest(connection,transaction,request);transaction.Commit();return saved;
        });
    }
    internal static Task<SavedQuickRegistration> SaveNewAsync(QuickRegistrationRecoveryJournal.Pending request,int placement, string expectedQuote, params SqlParameter[] parameters)
        =>SaveNewAsync(request,placement,expectedQuote,null,parameters);
    // The commit delegate permits native failure injection after an actual commit;
    // the application uses the overload above and the normal SQL transaction commit.
    internal static Task<SavedQuickRegistration> SaveNewAsync(QuickRegistrationRecoveryJournal.Pending request,int placement,string expectedQuote,
        Action<SqlTransaction>? commit,params SqlParameter[] parameters)
    {
        int user=CurrentUser.UserId;QuickRegistrationRecoveryJournal.Validate(request);
        if(request.UserId!=user||request.PayloadHash!=PayloadHash(user,placement,expectedQuote,parameters))
            throw new InvalidOperationException("The registration draft changed after its save request was prepared. Check Save Outcome before continuing.");
        return Task.Run(()=>{
            using var connection=DatabaseHelper.GetConnection();DatabaseHelper.OpenConnection(connection);
            if(Scope(connection)!=request.Scope)throw new InvalidOperationException("The registration database changed before saving. Reconnect and check the save outcome.");
            using var transaction=connection.BeginTransaction(IsolationLevel.Serializable);
            var state=new SaveTransaction(transaction,commit);
            var outcome=QuickRegistrationSaveCoordinator.Execute(state,()=>{
                using var access=new SqlCommand("EXEC dbo.RequirePermission @UserId,N'Students.Admit'; EXEC dbo.RequirePermission @UserId,N'Students.View';",connection,transaction);
                access.Parameters.Add(DatabaseHelper.Int("@UserId",user));access.ExecuteNonQuery();
                LockRequest(connection,transaction,request.Id,false);
                if(ReadRequest(connection,transaction,request) is { } prior)return prior;
                if (parameters.FirstOrDefault(p => p.ParameterName == "@RequireCurrentAcademicYear")?.Value is true)
                {
                    using var currentYear = new SqlCommand("SELECT COUNT(*) FROM dbo.AcademicYears WITH(UPDLOCK,HOLDLOCK) WHERE AcademicYearId=@Year AND IsCurrent=1;", connection, transaction);
                    currentYear.Parameters.Add(DatabaseHelper.Int("@Year", Convert.ToInt32(parameters.Single(p => p.ParameterName == "@AcademicYearId").Value)));
                    if (Convert.ToInt32(currentYear.ExecuteScalar()) != 1) throw new InvalidOperationException("The current academic year changed. Close and reopen Admit Sibling before saving.");
                }
                using var check = new SqlCommand("EXEC dbo.RequirePermission @UserId,N'Students.Admit';\n"+FeeQuery,connection,transaction);
                check.Parameters.AddRange(new[] { DatabaseHelper.Int("@UserId",user),DatabaseHelper.Int("@Placement",placement) });
                var current = new DataTable();
                using (var adapter = new SqlDataAdapter(check)) adapter.Fill(current);
                if (Quote(current)!=expectedQuote)
                    throw new InvalidOperationException("The class fees have changed. Choose the placement again to review the current fees before saving.");
                using var command=new SqlCommand("AdmitStudent",connection,transaction){CommandType=CommandType.StoredProcedure,CommandTimeout=120};
                command.Parameters.AddRange(parameters.Where(p=>!p.ParameterName.Equals("@UserId",StringComparison.OrdinalIgnoreCase)&&!p.ParameterName.Equals("@ExcludedFeeTypeIds",StringComparison.OrdinalIgnoreCase)&&p.ParameterName!="@RequireCurrentAcademicYear")
                    .Select(p=>(SqlParameter)((ICloneable)p).Clone()).ToArray());
                command.Parameters.Add(DatabaseHelper.Int("@UserId",user));
                state.SubmissionStarted=true;
                using var result=new DataSet();using(var adapter=new SqlDataAdapter(command))adapter.Fill(result);
                var saved=QuickRegistrationSaveCoordinator.ReadResponse(result);
                if(parameters.FirstOrDefault(p=>p.ParameterName=="@ExcludedFeeTypeIds")?.Value is DataTable excluded)
                {
                    // The existing admission procedure includes required fees. Explicit
                    // QR waivers cancel them within this same save/request transaction.
                    foreach(DataRow fee in excluded.Rows)
                    {
                        using var remove=new SqlCommand("DECLARE @Assignment int=(SELECT A.StudentFeeAssignmentId FROM dbo.StudentFeeAssignments A JOIN dbo.Enrollments E ON E.EnrollmentId=A.EnrollmentId WHERE E.AdmissionId=@Admission AND E.Status=N'Pending' AND A.FeeTypeId=@Fee AND A.CancelledAt IS NULL); IF @Assignment IS NULL THROW 57511,'The excluded fee could not be identified.',1; EXEC dbo.DeleteStudentFeeAssignment @UserId,@Assignment;",connection,transaction);
                        remove.Parameters.AddRange(new[]{DatabaseHelper.Int("@UserId",user),DatabaseHelper.Int("@Admission",saved.Id),DatabaseHelper.Int("@Fee",Convert.ToInt32(fee[0]))});remove.ExecuteNonQuery();
                    }
                }
                using var marker=new SqlCommand("INSERT dbo.QuickRegistrationRequests(RequestId,UserId,PayloadHash,AdmissionId) VALUES(@Request,@User,@Hash,@Admission);",connection,transaction);
                marker.Parameters.AddRange(new[]{new SqlParameter("@Request",SqlDbType.UniqueIdentifier){Value=request.Id},
                    DatabaseHelper.Int("@User",user),new SqlParameter("@Hash",SqlDbType.Binary,32){Value=Convert.FromHexString(request.PayloadHash)},DatabaseHelper.Int("@Admission",saved.Id)});
                marker.ExecuteNonQuery();return saved;
            });
            if(outcome.Outcome==QuickRegistrationWriteOutcome.Saved)return outcome.Student!;
            if(outcome.Outcome==QuickRegistrationWriteOutcome.Unconfirmed)throw new QuickRegistrationOutcomeUnknownException(outcome.Error!);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(outcome.Error!).Throw();throw new InvalidOperationException();
        });
    }
    private sealed class SaveTransaction(SqlTransaction transaction,Action<SqlTransaction>? commit):IQuickRegistrationTransaction
    {
        public bool SubmissionStarted{get;set;}
        public void Commit(){if(commit is null)transaction.Commit();else commit(transaction);}
        public void Rollback()=>transaction.Rollback();
    }

    internal static Task<DataSet> SavePendingAsync(int admission, string expectedSnapshot, params SqlParameter[] parameters)
    {
        int user = CurrentUser.UserId;
        return WriteAsync("SaveStudentDetailsWithFirstEnrollment",parameters,user,(connection,transaction)=>{
            if (string.IsNullOrEmpty(expectedSnapshot) || Snapshot(connection,transaction,admission,user)!=expectedSnapshot)
                throw new InvalidOperationException("This registration has changed since it was opened. Your edits have been kept. Reopen it and review the current details before saving.");
        });
    }

    internal static Task<DataSet> ChangeFeeAsync(int admission,string expectedSnapshot,int placement,int feeId,int? assignment,string? expectedQuote)
    {
        int user=CurrentUser.UserId;
        var parameters=assignment.HasValue?new[]{DatabaseHelper.Int("@StudentFeeAssignmentId",assignment.Value)}:
            new[]{DatabaseHelper.Int("@EnrollmentId",0),DatabaseHelper.Int("@FeeTypeId",feeId)};
        return WriteAsync(assignment.HasValue?"DeleteStudentFeeAssignment":"AddStudentFeeAssignment",parameters,user,(connection,transaction)=>{
            using var access=new SqlCommand("EXEC dbo.RequirePermission @UserId,N'Students.Administer'; EXEC dbo.RequirePermission @UserId,N'Fees.Adjust';",connection,transaction);
            access.Parameters.Add(DatabaseHelper.Int("@UserId",user));access.ExecuteNonQuery();
            if(string.IsNullOrEmpty(expectedSnapshot)||Snapshot(connection,transaction,admission,user)!=expectedSnapshot)
                throw new InvalidOperationException("This registration or its fees changed. Reload Registration before changing fees.");
            using var state=new SqlCommand("SELECT E.EnrollmentId FROM dbo.Enrollments E JOIN dbo.Students S ON S.AdmissionId=E.AdmissionId WHERE E.AdmissionId=@Admission AND E.ClassSectionId=@Placement AND E.Status=N'Pending' AND S.Status=N'Pending';",connection,transaction);
            state.Parameters.AddRange(new[]{DatabaseHelper.Int("@Admission",admission),DatabaseHelper.Int("@Placement",placement)});
            if(state.ExecuteScalar() is not int enrollment)throw new InvalidOperationException("This registration is no longer pending. Reload Registration.");
            if(assignment.HasValue)
            {
                using var owned=new SqlCommand("SELECT COUNT(*) FROM dbo.StudentFeeAssignments WHERE StudentFeeAssignmentId=@Assignment AND EnrollmentId=@Enrollment AND FeeTypeId=@Fee AND CancelledAt IS NULL;",connection,transaction);
                owned.Parameters.AddRange(new[]{DatabaseHelper.Int("@Assignment",assignment.Value),DatabaseHelper.Int("@Enrollment",enrollment),DatabaseHelper.Int("@Fee",feeId)});
                if(Convert.ToInt32(owned.ExecuteScalar())!=1)throw new InvalidOperationException("This fee is not part of the pending registration.");
            }
            else
            {
                using var preview=new SqlCommand(FeeQuery,connection,transaction);preview.Parameters.Add(DatabaseHelper.Int("@Placement",placement));
                var current=new DataTable();using(var adapter=new SqlDataAdapter(preview))adapter.Fill(current);
                if(Quote(current)!=expectedQuote||!current.AsEnumerable().Any(r=>Convert.ToInt32(r["FeeTypeId"])==feeId&&!r.IsNull("Amount")))throw new InvalidOperationException("The class fees changed. Open Add Fee again to review them.");
                using var existing=new SqlCommand("SELECT COUNT(*) FROM dbo.StudentFeeAssignments WHERE EnrollmentId=@Enrollment AND FeeTypeId=@Fee AND CancelledAt IS NULL;",connection,transaction);
                existing.Parameters.AddRange(new[]{DatabaseHelper.Int("@Enrollment",enrollment),DatabaseHelper.Int("@Fee",feeId)});
                if(Convert.ToInt32(existing.ExecuteScalar())!=0)throw new InvalidOperationException("This fee is already included. Reload Registration.");
                parameters[0].Value=enrollment;
            }
        });
    }

    internal static Task<DataSet> ChangeFamilyAsync(int admission, string expectedSnapshot, int? sibling)
    {
        int user=CurrentUser.UserId;
        return WriteAsync(sibling.HasValue?"AddStudentSibling":"DeleteStudentSibling",
            sibling.HasValue?new[]{DatabaseHelper.Int("@AdmissionId",admission),
                DatabaseHelper.Int("@SiblingAdmissionId",sibling.Value),DatabaseHelper.P("@RelationshipType","Sibling")}
                :new[]{DatabaseHelper.Int("@FamilyMemberId",0)},user,(connection,transaction)=>{
            using var access=new SqlCommand("EXEC dbo.RequirePermission @UserId,N'Students.Administer';",connection,transaction);
            access.Parameters.Add(DatabaseHelper.Int("@UserId",user));access.ExecuteNonQuery();
            if(string.IsNullOrEmpty(expectedSnapshot)||Snapshot(connection,transaction,admission,user)!=expectedSnapshot)
                throw new InvalidOperationException("This registration or family has changed. Reload Registration before changing its parent link.");
            using var state=new SqlCommand("SELECT Status FROM dbo.Students WHERE AdmissionId=@Admission",connection,transaction);
            state.Parameters.Add(DatabaseHelper.Int("@Admission",admission));
            if(Convert.ToString(state.ExecuteScalar()) is not ("Pending" or "Active"))
                throw new InvalidOperationException("Only pending or active students can change their parent link here.");
        },admission);
    }

    private static Task<DataSet> WriteAsync(string procedure, SqlParameter[] parameters, int user,
        Action<SqlConnection,SqlTransaction> verify, int unlinkAdmission=0) => Task.Run(()=>{
            using var connection = DatabaseHelper.GetConnection();
            DatabaseHelper.OpenConnection(connection);
            using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
            verify(connection,transaction);
            using var command = new SqlCommand(procedure,connection,transaction) { CommandType=CommandType.StoredProcedure,CommandTimeout=120 };
            // The UI's new-admission arguments already contain @UserId. Use the
            // captured signed-in actor exactly once, as DatabaseHelper does.
            command.Parameters.AddRange(parameters.Where(p=>!string.Equals(p.ParameterName,"@UserId",StringComparison.OrdinalIgnoreCase)).ToArray());
            command.Parameters.Add(DatabaseHelper.Int("@UserId",user));
            if(procedure=="DeleteStudentSibling")
            {
                using var member=new SqlCommand("SELECT FamilyMemberId FROM dbo.FamilyMembers WITH(UPDLOCK,HOLDLOCK) WHERE AdmissionId=@Admission",connection,transaction);
                member.Parameters.Add(DatabaseHelper.Int("@Admission",unlinkAdmission));
                object? id=member.ExecuteScalar();
                if(id is not int memberId)throw new InvalidOperationException("This student has no parent link to remove. Reload Registration.");
                command.Parameters["@FamilyMemberId"].Value=memberId;
            }
            var result = new DataSet();
            using (var adapter = new SqlDataAdapter(command)) adapter.Fill(result);
            transaction.Commit();
            return result;
        });
}
