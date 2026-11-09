using System.Data;
using System.Security.Cryptography;
using System.Text;
using FMSC_SMS.Pages.Security;
using Microsoft.Data.SqlClient;

/// <summary>
/// Uses the real production SQL credential-reset writer against the privately
/// owned FMSC_AuditChecks_* disposable database created by LifecycleSqlChecks.
/// It never calls GitHub and cannot be run against the installed school MDF.
/// The live GitHub+Windows+LocalDB host gates are independently enforced in
/// AdministratorRecoveryService; this suite checks the database transaction.
/// </summary>
internal static class AdministratorRecoveryChecks
{
    internal static void Run(Action<bool, string> check, string connectionString,
        int administrator, int ordinaryUser)
    {
        using var cn = new SqlConnection(connectionString);
        cn.Open();
        DataTable Read(string sql, params SqlParameter[] args)
        {
            using var cmd = new SqlCommand(sql, cn) { CommandTimeout = 60 };
            cmd.Parameters.AddRange(args);
            using var adapter = new SqlDataAdapter(cmd);
            var result = new DataTable();
            adapter.Fill(result);
            return result;
        }
        int Count(string sql, params SqlParameter[] args) =>
            Convert.ToInt32(Read(sql, args).Rows[0][0]);
        SqlParameter Id(int id) => new("@Id", SqlDbType.Int) { Value = id };
        DataRow Admin() => Read(
            "SELECT Username,PasswordHash,PasswordSalt,RoleId,MustChangePassword,FailedLoginCount FROM dbo.Users WHERE UserId=@Id;",
            Id(administrator)).Rows[0];
        byte[] CredentialProof(int id) => (byte[])Read(
            "SELECT HASHBYTES('SHA2_256',ISNULL(PasswordHash,0x)+ISNULL(PasswordSalt,0x)) AS Proof FROM dbo.Users WHERE UserId=@Id;",
            Id(id)).Rows[0][0];
        var original = Admin();
        string previousName = (string)original["Username"];
        byte[] previousProof = CredentialProof(administrator);
        byte[] ordinaryProof = CredentialProof(ordinaryUser);
        int previousRole = Convert.ToInt32(original["RoleId"]);
        int membershipBefore = Count("SELECT COUNT(*) FROM dbo.UserRoles WHERE UserId=@Id;", Id(administrator));
        int usersBefore = Count("SELECT COUNT(*) FROM dbo.Users;");
        int schoolBefore = Count("SELECT COUNT(*) FROM dbo.SchoolProfile;");
        const string sid = "S-1-5-21-fixture-host-owner";
        const string deviceName = "DisposableAuditMachine";
        const string replacementPassword = "Recovery audit replacement passphrase 123";
        string replacementName = "recovered-admin-" + Guid.NewGuid().ToString("N")[..8];
        byte[] approved = SHA256.HashData(Encoding.UTF8.GetBytes("approved-device-fixture"));
        byte[] other = SHA256.HashData(Encoding.UTF8.GetBytes("other-device-fixture"));

        void MustReject(int number, byte[] fingerprint, string accountSid, string username = "")
        {
            bool rejected = false;
            try
            {
                AdministratorRecoverySql.ResetOnVerifiedHost(cn,
                    username.Length == 0 ? replacementName : username,
                    replacementPassword, fingerprint, accountSid, deviceName);
            }
            catch (SqlException ex) when (ex.Number == number) { rejected = true; }
            check(rejected, "Administrator recovery refused with the expected SQL security code " + number);
            check(CryptographicOperations.FixedTimeEquals(previousProof, CredentialProof(administrator)),
                "A refused recovery never modifies Administrator credentials");
            check(Count("SELECT COUNT(*) FROM dbo.AdministratorRecoveryEvents;") == 0,
                "A refused recovery never writes a successful recovery audit");
        }

        check(Count("SELECT COUNT(*) FROM dbo.AdministratorRecoveryHost;") == 0,
            "Fresh schema-12 synthetic database has no recovery host registration");
        MustReject(51180, approved, sid);

        AdministratorRecoverySql.RegisterHost(cn, approved, sid);
        AdministratorRecoverySql.RegisterHost(cn, other, "S-1-5-21-untrusted");
        check(Count("SELECT COUNT(*) FROM dbo.AdministratorRecoveryHost;") == 1,
            "Existing recovery host registration is never replaced by another identity");
        MustReject(51180, other, sid);
        MustReject(51180, approved, "S-1-5-21-untrusted");
        string occupied = (string)Read("SELECT Username FROM dbo.Users WHERE UserId=@Id;", Id(ordinaryUser)).Rows[0][0];
        MustReject(51182, approved, sid, occupied);

        // One more Administrator membership must NEVER result in selecting
        // whichever account happens to be first.
        int adminRole = Count("SELECT RoleId FROM dbo.Roles WHERE RoleName=N'Administrator';");
        Read("INSERT dbo.UserRoles(UserId,RoleId) VALUES(@Id,@Role);",
            Id(ordinaryUser), new("@Role", SqlDbType.Int) { Value = adminRole });
        try { MustReject(51181, approved, sid); }
        finally
        {
            Read("DELETE dbo.UserRoles WHERE UserId=@Id AND RoleId=@Role;",
                Id(ordinaryUser), new("@Role", SqlDbType.Int) { Value = adminRole });
        }

        // Deliberately fail the SECOND SQL write (audit insert). The preceding
        // credential UPDATE must roll back atomically.
        Read("CREATE TRIGGER dbo.FMSC_Recovery_RejectAudit ON dbo.AdministratorRecoveryEvents AFTER INSERT AS BEGIN THROW 59965,'Injected recovery audit failure',1; END;");
        try { MustReject(59965, approved, sid); }
        finally { Read("DROP TRIGGER dbo.FMSC_Recovery_RejectAudit;"); }

        // Exact production writer, exact PBKDF2 policy, actual migrated SQL.
        AdministratorRecoverySql.ResetOnVerifiedHost(cn,
            replacementName, replacementPassword, approved, sid, deviceName);
        var after = Admin();
        byte[] hash = (byte[])after["PasswordHash"], salt = (byte[])after["PasswordSalt"];
        check((string)after["Username"] == replacementName &&
            hash.Length == 64 && salt.Length == 32 &&
            CryptographicOperations.FixedTimeEquals(hash,
                Rfc2898DeriveBytes.Pbkdf2(replacementPassword, salt, 100_000,
                    HashAlgorithmName.SHA512, 64)) &&
            !CryptographicOperations.FixedTimeEquals(previousProof, CredentialProof(administrator)),
            "Production recovery writer changes only the original Administrator to a valid PBKDF2 credential");
        check(Convert.ToInt32(after["RoleId"]) == previousRole &&
            Count("SELECT COUNT(*) FROM dbo.UserRoles WHERE UserId=@Id;", Id(administrator)) == membershipBefore,
            "Administrator identity and role memberships remain unchanged");
        check(Count("SELECT COUNT(*) FROM dbo.Users;") == usersBefore &&
            Count("SELECT COUNT(*) FROM dbo.SchoolProfile;") == schoolBefore &&
            CryptographicOperations.FixedTimeEquals(ordinaryProof, CredentialProof(ordinaryUser)),
            "Recovery preserves ordinary users and all school profile data");
        check(Count("SELECT COUNT(*) FROM dbo.AdministratorRecoveryEvents WHERE TargetUserId=@Id;", Id(administrator)) == 1,
            "Exactly one credential-free success audit is committed with the reset");

        var login = Read("EXEC dbo.GetUserForLogin @Username;",
            new("@Username", SqlDbType.NVarChar, 100) { Value = replacementName });
        check(login.Rows.Count == 1 &&
              Convert.ToInt32(login.Rows[0]["UserId"]) == administrator &&
              CryptographicOperations.FixedTimeEquals((byte[])login.Rows[0]["PasswordHash"], hash) &&
              CryptographicOperations.FixedTimeEquals((byte[])login.Rows[0]["PasswordSalt"], salt),
            "Existing production login lookup can authenticate the new Administrator credentials");
        check(Read("EXEC dbo.GetUserForLogin @Username;",
            new("@Username", SqlDbType.NVarChar, 100) { Value = previousName }).Rows.Count == 0,
            "Old Administrator username no longer authenticates after recovery");
    }
}
