using System.Data;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using FMSC_SMS.Core;

namespace FMSC_SMS.Pages.Security;

/// <summary>
/// The SQL-only portion of recovery. The production caller MUST first verify
/// local-host database identity, elevated Windows user and ONLINE GitHub SSH
/// authorization. Keeping the SQL transaction here allows exercising exactly
/// the same write path against a disposable migrated SQL database in CI.
/// </summary>
internal static class AdministratorRecoverySql
{
    internal static void RegisterHost(SqlConnection connection, byte[] fingerprint, string sid)
    {
        ValidateIdentity(fingerprint, sid);
        if (connection.State != ConnectionState.Open)
            throw new InvalidOperationException("A verified open SQL connection is required.");
        using var command = new SqlCommand(
            """
            IF NOT EXISTS(SELECT 1 FROM dbo.AdministratorRecoveryHost WITH(UPDLOCK,HOLDLOCK) WHERE HostId=1)
                INSERT dbo.AdministratorRecoveryHost(HostId,DevicePublicKeySha256,WindowsSid)
                VALUES(1,@Fingerprint,@Sid);
            """, connection);
        command.Parameters.Add("@Fingerprint", SqlDbType.Binary, 32).Value = fingerprint;
        command.Parameters.Add("@Sid", SqlDbType.NVarChar, 184).Value = sid;
        command.ExecuteNonQuery();
    }

    internal static void ResetOnVerifiedHost(SqlConnection connection, string username,
        string password, byte[] fingerprint, string sid, string computer,
        CancellationToken token = default)
    {
        // No bypass/alternate credential is available through this SQL routine:
        // the persisted key fingerprint and Windows SID must ALREADY match.
        ValidateIdentity(fingerprint, sid);
        if (connection.State != ConnectionState.Open)
            throw new InvalidOperationException("A verified open SQL connection is required.");
        string name = username.Trim();
        if (name.Length is < 1 or > 100 || name.Any(char.IsControl))
            throw new ArgumentException("Enter a valid Administrator username.");
        if (!PasswordPolicy.IsValid(password))
            throw new ArgumentException(PasswordPolicy.Requirement);
        if (string.IsNullOrWhiteSpace(computer) || computer.Length > 128)
            throw new ArgumentException("Invalid computer identity.");
        token.ThrowIfCancellationRequested();
        byte[] salt = RandomNumberGenerator.GetBytes(32);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000,
            HashAlgorithmName.SHA512, 64);
        try
        {
            using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
            try
            {
                using var command = new SqlCommand(
                    """
                    SET NOCOUNT ON;
                    IF NOT EXISTS
                       (SELECT 1 FROM dbo.AdministratorRecoveryHost WITH(UPDLOCK,HOLDLOCK)
                        WHERE HostId=1 AND DevicePublicKeySha256=@Fingerprint AND WindowsSid=@Sid)
                        THROW 51180,'This database has no matching Administrator recovery host registration. Sign in as an Administrator on the original database host to register it.',1;

                    DECLARE @AdminCount int, @AdminId int, @PreviousUsername nvarchar(100);
                    SELECT @AdminCount=COUNT(*)
                    FROM dbo.Users U WITH(UPDLOCK,HOLDLOCK)
                    WHERE EXISTS(SELECT 1 FROM dbo.Roles R
                                 WHERE R.RoleId=U.RoleId AND R.RoleName=N'Administrator')
                       OR EXISTS(SELECT 1 FROM dbo.UserRoles UR JOIN dbo.Roles R ON R.RoleId=UR.RoleId
                                 WHERE UR.UserId=U.UserId AND R.RoleName=N'Administrator');
                    IF @AdminCount<>1
                        THROW 51181,'Recovery requires exactly one protected Administrator account. No changes were made.',1;

                    SELECT @AdminId=U.UserId,@PreviousUsername=U.Username
                    FROM dbo.Users U WITH(UPDLOCK,HOLDLOCK)
                    WHERE EXISTS(SELECT 1 FROM dbo.Roles R
                                 WHERE R.RoleId=U.RoleId AND R.RoleName=N'Administrator')
                       OR EXISTS(SELECT 1 FROM dbo.UserRoles UR JOIN dbo.Roles R ON R.RoleId=UR.RoleId
                                 WHERE UR.UserId=U.UserId AND R.RoleName=N'Administrator');
                    IF EXISTS(SELECT 1 FROM dbo.Users WITH(UPDLOCK,HOLDLOCK)
                              WHERE Username=@Username AND UserId<>@AdminId)
                        THROW 51182,'That username is already used by another account. Choose a different name.',1;

                    UPDATE dbo.Users
                       SET Username=@Username,PasswordHash=@Hash,PasswordSalt=@Salt,
                           MustChangePassword=0,FailedLoginCount=0,LockedUntil=NULL,
                           UpdatedAt=SYSDATETIME()
                     WHERE UserId=@AdminId;
                    IF @@ROWCOUNT<>1 THROW 51183,'Administrator identity changed during recovery. No changes were made.',1;

                    INSERT dbo.AdministratorRecoveryEvents
                        (TargetUserId,PreviousUsername,NewUsername,DevicePublicKeySha256,
                         WindowsSid,WindowsComputer)
                    VALUES(@AdminId,@PreviousUsername,@Username,@Fingerprint,@Sid,@Computer);
                    """, connection, transaction);
                command.CommandTimeout = 45;
                command.Parameters.Add("@Username", SqlDbType.NVarChar, 100).Value = name;
                command.Parameters.Add("@Hash", SqlDbType.VarBinary, 64).Value = hash;
                command.Parameters.Add("@Salt", SqlDbType.VarBinary, 32).Value = salt;
                command.Parameters.Add("@Fingerprint", SqlDbType.Binary, 32).Value = fingerprint;
                command.Parameters.Add("@Sid", SqlDbType.NVarChar, 184).Value = sid;
                command.Parameters.Add("@Computer", SqlDbType.NVarChar, 128).Value = computer;
                command.ExecuteNonQuery();
                token.ThrowIfCancellationRequested();
                transaction.Commit();
            }
            catch
            {
                try { transaction.Rollback(); } catch (InvalidOperationException) { }
                throw;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
            CryptographicOperations.ZeroMemory(salt);
        }
    }

    private static void ValidateIdentity(byte[] fingerprint, string sid)
    {
        if (fingerprint is not { Length: 32 })
            throw new ArgumentException("Invalid registered device fingerprint.");
        if (string.IsNullOrWhiteSpace(sid) || sid.Length > 184)
            throw new ArgumentException("Invalid Windows account identity.");
    }
}
