using System.Security.Cryptography;
namespace FMSC_SMS.Pages.Security;
public static class AuthenticationService
{
    // In-memory (per application run) brute-force throttle.
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(5);
    /// <summary>The same failure sentence for unknown usernames, missing credentials and wrong passwords.</summary>
    private const string GenericSignInFailure =
        "The username or password is incorrect.";
    /// <summary>shared by the real password check and the decoy one below, so the two can never drift apart and make the decoy cheaper than the real thing.</summary>
    private const int PasswordIterations = 100_000;
    /* ed, meaningless values used only to spend the same work as a real password check when there is no real hash to check. */
    private static readonly byte[] DecoySalt =
    {
        0x7B, 0x07, 0x3B, 0x2E, 0xBD, 0x4D, 0x35, 0x21,
        0x80, 0xB8, 0x69, 0xF0, 0x33, 0x9F, 0x8A, 0x0B,
        0x12, 0x0A, 0x9B, 0x35, 0xFB, 0x52, 0x2F, 0x60,
        0x5E, 0xDB, 0x78, 0xA5, 0x71, 0x84, 0xA8, 0x40
    };
    private static readonly byte[] DecoyHash =
    {
        0xC1, 0xCB, 0x8B, 0x2C, 0xC1, 0x67, 0xDE, 0xE3,
        0xD8, 0xCB, 0x77, 0xFB, 0x57, 0xE1, 0x83, 0x30,
        0xD8, 0x1A, 0x54, 0xBC, 0x34, 0x19, 0xAC, 0x4F,
        0x72, 0x6E, 0x44, 0x9D, 0x97, 0x61, 0xA2, 0x4D,
        0x7D, 0x64, 0x7B, 0x11, 0x0C, 0x43, 0x72, 0x90,
        0x64, 0x07, 0xE9, 0x28, 0x43, 0xFE, 0x16, 0x24,
        0xAA, 0x37, 0x0E, 0x9E, 0x4D, 0xAF, 0x48, 0x20,
        0xDC, 0x03, 0x6C, 0xC7, 0xC6, 0x82, 0x4C, 0x3A
    };
    private static readonly Dictionary<string, (int FailedCount, DateTime? LockedUntil)> LoginAttempts =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly object LoginAttemptsLock = new();
    public static bool Authenticate(string username, string password, out string message) =>
        Authenticate(username, password, out message, out _);
    internal static bool Authenticate(string username, string password, out string message, out Exception? failure)
    {
        failure = null;
        message = string.Empty;
        if (string.IsNullOrWhiteSpace(username))
        {
            message = "Please enter your username.";
            return false;
        }
        if (string.IsNullOrEmpty(password))
        {
            message = "Please enter your password.";
            return false;
        }
        var cleanUsername = username.Trim();
        if (IsLockedOut(cleanUsername, out TimeSpan remaining))
        {
            message =
                $"Too many failed sign-in attempts for this username. " +
                $"Please try again in {Math.Ceiling(remaining.TotalMinutes):0} minute(s).";
            return false;
        }
        try
        {
            var dt = DatabaseHelper.Query("GetUserForLogin", DatabaseHelper.P("@Username", cleanUsername));
            bool found = dt.Rows.Count > 0;
            DataRow? r = found ? dt.Rows[0] : null;
            /* FIRST, every failure now answers with one sentence. */
            bool hasCredentials =
                r is not null &&
                r["PasswordHash"] != DBNull.Value &&
                r["PasswordSalt"] != DBNull.Value;
            // SQL lookup can use a case-insensitive collation; sign-in must still match exactly.
            bool usernameMatches = r is not null && string.Equals(
                Convert.ToString(r["Username"]), cleanUsername, StringComparison.Ordinal);
            if (r is null || !hasCredentials || !usernameMatches)
            {
                SpendEquivalentPasswordWork(password);
                RecordFailedAttempt(cleanUsername);
                if (r is not null)
                {
                    // (option A): also record it durably, so the count and the lockout survive a restart and are visible to an administrator in dbo.Users.
                    RecordFailedLoginInDatabase(Convert.ToInt32(r["UserId"]));
                    if (!hasCredentials)
                        LogSignInAnomaly(
                            cleanUsername,
                            "the account has no stored password credentials");
                }
                message = GenericSignInFailure;
                return false;
            }
            /* (option A): the durable lockout, read from the row we already have so it survives the application being closed and reopened. */
            if (IsPersistedLockout(r, out TimeSpan persistedRemaining))
            {
                message =
                    $"Too many failed sign-in attempts for this username. " +
                    $"Please try again in {Math.Ceiling(persistedRemaining.TotalMinutes):0} minute(s).";
                return false;
            }
            var hash = (byte[])r["PasswordHash"];
            var salt = (byte[])r["PasswordSalt"];
            var computed = Rfc2898DeriveBytes.Pbkdf2(password, salt, PasswordIterations, HashAlgorithmName.SHA512, hash.Length);
            if (!CryptographicOperations.FixedTimeEquals(hash, computed))
            {
                RecordFailedAttempt(cleanUsername);
                // (option A): the durable half of the same count.
                RecordFailedLoginInDatabase(userId: Convert.ToInt32(r["UserId"]));
                message = GenericSignInFailure;
                return false;
            }
            var userId = Convert.ToInt32(r["UserId"]);
            var roleName =
                Convert.ToString(r["RoleName"]) ?? "";
            var mustChange =
                r["MustChangePassword"] != DBNull.Value &&
                Convert.ToBoolean(r["MustChangePassword"]);
            var permissionsTable = DatabaseHelper.Query("GetUserPermissions", DatabaseHelper.Int("@UserId", userId));
            var permissions = permissionsTable
                .AsEnumerable()
                .Select(row =>
                    Convert.ToString(row["PermissionCode"]) ?? "")
                .Where(code =>
                    !string.IsNullOrWhiteSpace(code))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            CurrentUser.SignIn(userId, cleanUsername, roleName, mustChange, permissions);
            ResetFailedAttempts(cleanUsername);
            // (option A): clear the durable count too, so a normal sign-in wipes the record of earlier mistyping on this account.
            ResetFailedLoginInDatabase(userId);
            // The existing GitHub update key may register the local recovery host
            // only after normal, successful Administrator sign-in.
            AdministratorRecoveryService.TryBindAfterAdministratorSignIn();
            return true;
        }
        catch (Exception ex)
        {
            Core.AppExceptionHandler.LogNonFatal(ex, "Authenticating sign-in");
            failure = ex;
            message = Core.DbMessage.UserMessage(ex);
            return false;
        }
    }
    /// <summary>One password derivation with exactly the parameters a real check uses, against a fixed decoy pair, so that "no such username" costs the same as "wrong password". See the comment in Authenticate.</summary>
    private static void SpendEquivalentPasswordWork(string password)
    {
        var computed = Rfc2898DeriveBytes.Pbkdf2(password, DecoySalt, PasswordIterations, HashAlgorithmName.SHA512, DecoyHash.Length);
        // Same comparison routine as the real path, so that cost matches too.
        _ = CryptographicOperations.FixedTimeEquals(DecoyHash, computed);
    }
    /// <summary>The specific reason a sign-in was refused goes to the diagnostics log rather than to the screen.</summary>
    private static void LogSignInAnomaly(string username, string reason)
    {
        Core.AppExceptionHandler.LogNonFatal(
            new InvalidOperationException(
                $"Sign-in refused for '{username}': {reason}. "
                + "The operator was shown the generic sign-in failure message."),
            "Sign-In");
    }
    /// <summary>(option A): the durable half of the throttle, read from the user row so that closing and reopening the application does not clear it.</summary>
    private static bool IsPersistedLockout(DataRow row, out TimeSpan remaining)
    {
        remaining = TimeSpan.Zero;
        if (!row.Table.Columns.Contains("LockedUntil") ||
            row["LockedUntil"] == DBNull.Value)
        {
            return false;
        }
        // dbo.RecordFailedLogin writes SYSDATETIME (server-local), and this application talks to a database on the same machine, so DateTime.Now keeps both sides in the same frame.
        remaining = Convert.ToDateTime(row["LockedUntil"]) - DateTime.Now;
        return remaining > TimeSpan.Zero;
    }
    /// <summary>(option A): persists one failed attempt against the account. It is logged rather than swallowed, and a missing procedure ( not yet applied) is handled by the same path.</summary>
    private static void RecordFailedLoginInDatabase(int userId)
    {
        try
        {
            DatabaseHelper.Query(
                "RecordFailedLogin",
                DatabaseHelper.Int("@TargetUserId", userId),
                DatabaseHelper.Int("@MaxFailedAttempts", MaxFailedAttempts),
                DatabaseHelper.Int("@LockoutMinutes", (int)LockoutDuration.TotalMinutes));
        }
        catch (Exception ex)
        {
            Core.AppExceptionHandler.LogNonFatal(ex, "Recording a failed sign-in");
        }
    }
    /// <summary>(option A): clears the durable count after a good sign-in.</summary>
    private static void ResetFailedLoginInDatabase(int userId)
    {
        try
        {
            DatabaseHelper.Query("ResetFailedLogin", DatabaseHelper.Int("@TargetUserId", userId));
        }
        catch (Exception ex)
        {
            Core.AppExceptionHandler.LogNonFatal(ex, "Clearing a failed sign-in count");
        }
    }
    private static bool IsLockedOut(string username, out TimeSpan remaining)
    {
        remaining = TimeSpan.Zero;
        lock (LoginAttemptsLock)
        {
            if (!LoginAttempts.TryGetValue(username, out var entry) || entry.LockedUntil is null)
                return false;
            if (DateTime.UtcNow >= entry.LockedUntil.Value)
            {
                // Lockout window has passed - give the account a fresh start.
                LoginAttempts.Remove(username);
                return false;
            }
            remaining = entry.LockedUntil.Value - DateTime.UtcNow;
            return true;
        }
    }
    private static void RecordFailedAttempt(string username)
    {
        lock (LoginAttemptsLock)
        {
            var (failedCount, _) = LoginAttempts.TryGetValue(username, out var existing)
                ? existing
                : (0, null);
            failedCount++;
            DateTime? lockedUntil = failedCount >= MaxFailedAttempts
                ? DateTime.UtcNow.Add(LockoutDuration)
                : null;
            LoginAttempts[username] = (failedCount, lockedUntil);
        }
    }
    private static void ResetFailedAttempts(string username)
    {
        lock (LoginAttemptsLock)
        {
            LoginAttempts.Remove(username);
        }
    }
}
