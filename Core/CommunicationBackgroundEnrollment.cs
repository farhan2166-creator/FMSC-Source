using System.Data;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using FMSC_SMS.Core.Updates;

namespace FMSC_SMS.Core;

/// <summary>
/// Each Windows account has one DPAPI-protected authorization to run the school
/// background processor. Enrollment is issued only after interactive FMSC
/// administrator sign-in and verified existing computer update authorization.
/// The machine credential is never an ordinary FMSC password or SQL login.
/// </summary>
internal static class CommunicationBackgroundEnrollment
{
    private static readonly byte[] Entropy = SHA256.HashData(
        Encoding.UTF8.GetBytes("FMSC/Communication/background-worker/v1"));
    private const string CredentialFile = "processor-enrollment.v1.dpapi";
    internal static string Root => CommunicationProviderCredentialStore.DirectoryPath;
    internal static string CredentialPath => Path.Combine(Root, CredentialFile);

    internal sealed record Enrollment(Guid EnrollmentId,int UserId,string WindowsSid,
        string TokenBase64,long MaxObservedAttemptId,int Version=1);

    private static string CurrentSid() =>
        WindowsIdentity.GetCurrent().User?.Value
        ?? throw new UnauthorizedAccessException("Cannot identify the Windows communication processor owner.");

    internal static bool HasLocalEnrollment => File.Exists(CredentialPath);

    internal static async Task EnrollAsync(CancellationToken token=default)
    {
        if (!CurrentUser.IsAdministrator ||
            !CurrentUser.HasPermission("Communication.Configure") ||
            !CurrentUser.HasPermission("Communication.Send"))
            throw new UnauthorizedAccessException("An authorized FMSC administrator must enroll the background processor.");
        if (!UpdateDeviceKey.IsEnrolled)
            throw new UnauthorizedAccessException("Authorize this computer for FMSC updates before enrolling unattended communication.");
        var authorization=await UpdateGitReader.CheckAsync(token);
        if (!authorization.Authorized)
            throw new UnauthorizedAccessException("Computer authorization could not be confirmed. " + authorization.Message);
        int actor=CurrentUser.UserId;
        string sid=CurrentSid();
        byte[] secret=RandomNumberGenerator.GetBytes(32);
        try
        {
            byte[] hash=SHA256.HashData(secret);
            Guid enrollmentId=Guid.NewGuid();
            if (actor!=CurrentUser.UserId || !CurrentUser.IsAdministrator)
                throw new UnauthorizedAccessException("The school administrator changed during enrollment.");
            using var water=await DatabaseHelper.QuerySqlAsync(
                "EXEC dbo.RequirePermission @UserId,N'Communication.Configure'; " +
                "SELECT ISNULL(MAX(AttemptId),CONVERT(bigint,0)) AS MaxAttemptId FROM msg.Attempt;",
                DatabaseHelper.Int("@UserId",actor));
            long max=water.Rows.Count==1 ? Convert.ToInt64(water.Rows[0]["MaxAttemptId"])
                : throw new InvalidOperationException("Could not establish the school attempt watermark.");
            // SQL registration holds all existing schedules for deliberate
            // review; a restored or preexisting pending batch never auto-starts.
            await DatabaseHelper.ExecuteUserAsync("msg.RegisterBackgroundWorker",
                DatabaseHelper.P("@EnrollmentId",enrollmentId),
                DatabaseHelper.P("@WindowsSid",sid),
                new SqlParameter("@SecretHash",SqlDbType.VarBinary,32) { Value=hash });
            Write(new Enrollment(enrollmentId,actor,sid,Convert.ToBase64String(secret),max));
            await CommunicationBackgroundTaskSchedule.ConfigureAsync(true,token);
            await DatabaseHelper.ExecuteUserAsync("msg.SetUnattendedSending",
                DatabaseHelper.Bit("@Enabled",true));
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    internal static async Task DisableAsync(CancellationToken token=default)
    {
        if (!CurrentUser.IsAdministrator || !CurrentUser.HasPermission("Communication.Configure"))
            throw new UnauthorizedAccessException("Only a school administrator can disable background messaging.");
        // SQL is disabled first: a failed Task Scheduler deletion cannot
        // leave unattended dispatch enabled.
        await DatabaseHelper.ExecuteUserAsync("msg.SetUnattendedSending",
            DatabaseHelper.Bit("@Enabled",false));
        await CommunicationBackgroundTaskSchedule.ConfigureAsync(false,token);
    }

    internal static Enrollment Read()
    {
        var info=new FileInfo(CredentialPath);
        if (!info.Exists || info.Length is < 32 or > 65536)
            throw new UnauthorizedAccessException("The Windows account has no valid background sending enrollment.");
        byte[] cipher=File.ReadAllBytes(CredentialPath),clear=Array.Empty<byte>();
        try
        {
            clear=ProtectedData.Unprotect(cipher,Entropy,DataProtectionScope.CurrentUser);
            var record=JsonSerializer.Deserialize<Enrollment>(clear)
                ?? throw new InvalidDataException("The background processor enrollment is empty.");
            if (record.Version!=1 || record.EnrollmentId==Guid.Empty ||
                record.UserId<=0 || record.WindowsSid!=CurrentSid() ||
                record.MaxObservedAttemptId<0)
                throw new UnauthorizedAccessException("The background processor enrollment does not match this Windows account.");
            byte[] token=Convert.FromBase64String(record.TokenBase64);
            try
            {
                if (token.Length!=32) throw new InvalidDataException("Invalid background processor proof.");
            }
            finally { CryptographicOperations.ZeroMemory(token); }
            return record;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
            CryptographicOperations.ZeroMemory(cipher);
        }
    }

    internal static void JournalClaim(long attemptId)
    {
        if (attemptId<=0) throw new ArgumentOutOfRangeException(nameof(attemptId));
        var current=Read();
        if (attemptId<current.MaxObservedAttemptId)
            throw new InvalidOperationException("Database attempt numbers moved backward. Background sending must stop.");
        Write(current with { MaxObservedAttemptId=attemptId });
    }

    internal static void JournalObserved(long maxAttemptId)
    {
        if (maxAttemptId<0) throw new ArgumentOutOfRangeException(nameof(maxAttemptId));
        var current=Read();
        if (maxAttemptId<current.MaxObservedAttemptId)
            throw new InvalidOperationException("The database has an older sending history. Review the restored database.");
        if (maxAttemptId>current.MaxObservedAttemptId)
            Write(current with { MaxObservedAttemptId=maxAttemptId });
    }

    private static void Write(Enrollment record)
    {
        Directory.CreateDirectory(Root);
        UpdateDeviceKey.RestrictDirectory(Root);
        string temp=CredentialPath+"."+Guid.NewGuid().ToString("N")+".tmp";
        byte[] clear=Array.Empty<byte>(),cipher=Array.Empty<byte>();
        try
        {
            clear=JsonSerializer.SerializeToUtf8Bytes(record);
            cipher=ProtectedData.Protect(clear,Entropy,DataProtectionScope.CurrentUser);
            using (var file=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,
                       FileShare.None,4096,FileOptions.WriteThrough))
            {
                file.Write(cipher);
                file.Flush(flushToDisk:true);
            }
            File.Move(temp,CredentialPath,overwrite:true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
            CryptographicOperations.ZeroMemory(cipher);
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    internal static async Task<long> VerifyAsync(Enrollment enrollment,CancellationToken token=default)
    {
        if (enrollment.WindowsSid!=CurrentSid())
            throw new UnauthorizedAccessException("This Windows account is not enrolled for this school.");
        byte[] secret=Convert.FromBase64String(enrollment.TokenBase64);
        try
        {
            if (secret.Length!=32) throw new UnauthorizedAccessException("Invalid background processor proof.");
            byte[] hash=SHA256.HashData(secret);
            using var result=await DatabaseHelper.QuerySqlAsync(
                "EXEC msg.VerifyBackgroundWorker @UserId,@EnrollmentId,@WindowsSid,@SecretHash;",
                token,
                DatabaseHelper.Int("@UserId",enrollment.UserId),
                DatabaseHelper.P("@EnrollmentId",enrollment.EnrollmentId),
                DatabaseHelper.P("@WindowsSid",enrollment.WindowsSid),
                new SqlParameter("@SecretHash",SqlDbType.VarBinary,32) { Value=hash });
            if (result.Rows.Count!=1 || !Convert.ToBoolean(result.Rows[0]["Authorized"]))
                throw new UnauthorizedAccessException("The school rejected background sending authorization.");
            return Convert.ToInt64(result.Rows[0]["MaxAttemptId"]);
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    internal static SqlParameter[] Proof(Enrollment enrollment,Guid? nonce=null)
    {
        byte[] secret=Convert.FromBase64String(enrollment.TokenBase64);
        try
        {
            var list=new List<SqlParameter> {
                DatabaseHelper.P("@EnrollmentId",enrollment.EnrollmentId),
                DatabaseHelper.P("@WindowsSid",enrollment.WindowsSid),
                new("@SecretHash",SqlDbType.VarBinary,32) { Value=SHA256.HashData(secret) }
            };
            if(nonce.HasValue)list.Add(DatabaseHelper.P("@LeaseNonce",nonce.Value));
            return list.ToArray();
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }
}
