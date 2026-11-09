using System.Data;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Data.SqlClient;
using FMSC_SMS.Core;
using FMSC_SMS.Core.Updates;

namespace FMSC_SMS.Pages.Security;

/// <summary>
/// Emergency recovery of the ONE protected Administrator account.
/// This is never a substitute for normal Users & Roles password resets.
/// No local recovery key, shared master credential, or recovery SQL script is created.
/// A successful GitHub read-only deploy-key check proves the preapproved Windows
/// identity is still allowed into the private distribution repository.
/// LocalDB ownership + the existing database's host binding are additional gates.
/// </summary>
internal static class AdministratorRecoveryService
{
    internal static bool IsLocalDatabaseEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return false;
        if (string.Equals(endpoint, DatabaseHelper.LocalDbInstance, StringComparison.OrdinalIgnoreCase))
            return true;
        const string prefix = @"np:\\.\pipe\LOCALDB#";
        const string suffix = @"\tsql\query";
        return endpoint.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && endpoint.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            && endpoint.Length > prefix.Length + suffix.Length
            && endpoint.AsSpan(prefix.Length,
                endpoint.Length - prefix.Length - suffix.Length).ToArray()
                .All(char.IsAsciiLetterOrDigit);
    }

    private static bool WindowsAdministrator() =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent())
            .IsInRole(WindowsBuiltInRole.Administrator);

    private static string WindowsSid() =>
        WindowsIdentity.GetCurrent().User?.Value
        ?? throw new UnauthorizedAccessException("The Windows account could not be verified.");

    private static byte[] DeviceFingerprint()
    {
        string[] fields = UpdateDeviceKey.GetPublicKey().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 2 || fields[0] != "ssh-rsa")
            throw new InvalidDataException("The computer's update authorization key is invalid.");
        return SHA256.HashData(Encoding.ASCII.GetBytes(fields[0] + " " + fields[1]));
    }

    private static SqlConnection OpenApprovedLocalDatabase()
    {
        if (!DatabaseHelper.TestConnection(out string guidance))
            throw new InvalidOperationException("Administrator recovery requires a compatible school database. " + guidance);
        var connection = DatabaseHelper.GetConnection();
        try
        {
            string endpoint = new SqlConnectionStringBuilder(connection.ConnectionString).DataSource;
            if (!IsLocalDatabaseEndpoint(endpoint))
                throw new UnauthorizedAccessException(
                    "Administrator recovery must run on the school database host. Remote clients cannot reset the Administrator.");
            DatabaseHelper.OpenConnection(connection);
            // TestConnection validates both schema and the actual MDF file path.
            // Repeat both identity checks on this new connection, before touching credentials.
            using var check = new SqlCommand(
                "SELECT DB_NAME(), physical_name FROM sys.database_files WHERE file_id=1;", connection);
            using var reader = check.ExecuteReader();
            if (!reader.Read() ||
                !string.Equals(Convert.ToString(reader.GetValue(0)), "FMSC_DB", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFullPath(Convert.ToString(reader.GetValue(1)) ?? ""),
                    Path.GetFullPath(DatabaseHelper.DatabaseFile), StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("The connected school database is not the verified local FMSC database.");
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Bind the normal approved update key only AFTER a successful Administrator
    /// sign-in on the actual LocalDB host. Never create or replace a binding from
    /// an unauthenticated login screen or another Windows user/remote client.
    /// Missing/unapproved keys leave ordinary sign-in unaffected.
    /// </summary>
    internal static void TryBindAfterAdministratorSignIn()
    {
#if FMSC_LEVEL2
        return;
#else
        if (!CurrentUser.IsAdministrator || !UpdateDeviceKey.IsEnrolled) return;
        try
        {
            byte[] fingerprint = DeviceFingerprint();
            using var cn = OpenApprovedLocalDatabase();
            AdministratorRecoverySql.RegisterHost(cn, fingerprint, WindowsSid());
            CryptographicOperations.ZeroMemory(fingerprint);
        }
        catch (Exception ex)
        {
            // Recovery enrollment is optional; never turn a valid login into a failure.
            AppExceptionHandler.LogNonFatal(ex, "Administrator recovery binding was not established");
        }
#endif
    }

    internal static async Task RecoverAsync(string username, string password, CancellationToken token = default)
    {
#if FMSC_LEVEL2
        throw new InvalidOperationException("Administrator recovery is disabled in test-only application builds.");
#else
        string name = username.Trim();
        if (name.Length is < 1 or > 100 || name.Any(char.IsControl))
            throw new ArgumentException("Enter a valid Administrator username of at most 100 characters.");
        if (!PasswordPolicy.IsValid(password))
            throw new ArgumentException(PasswordPolicy.Requirement);
        if (!WindowsAdministrator())
            throw new UnauthorizedAccessException(
                "Run FMSC as administrator from the Windows account that owns the school database, then retry recovery.");
        if (!UpdateDeviceKey.IsEnrolled)
            throw new UnauthorizedAccessException(
                "This Windows account has no approved GitHub update authorization. Recovery is unavailable.");
        token.ThrowIfCancellationRequested();

        // A successful SSH clone of the private repository is the ONLINE
        // approval test, not mere presence of a DPAPI key on disk.
        // The read-only deploy key is never given database access or Git write access.
        await using var proof = await UpdateGitSession.OpenAsync(token).ConfigureAwait(false);
        byte[] fingerprint = DeviceFingerprint();
        try
        {
            await Task.Run(() => ResetCredentials(name, password, fingerprint, token), token)
                .ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(fingerprint);
        }
#endif
    }

    private static void ResetCredentials(string username, string password,
        byte[] fingerprint, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var cn = OpenApprovedLocalDatabase();
        AdministratorRecoverySql.ResetOnVerifiedHost(cn, username, password,
            fingerprint, WindowsSid(), Environment.MachineName, token);
    }
}
