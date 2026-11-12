using System.Data;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Data.SqlClient;
using FMSC_SMS.Core;
using FMSC_SMS.Core.Updates;

namespace FMSC_SMS.Services.Backup;

/// <summary>
/// Explicit enrollment of a recovered school on a replacement Windows PC.
/// Unlike a normal RestoreBackup, this path only runs when FMSC_DB is absent
/// or is an independently validated, never-configured first-run template.
/// It requires a live publisher-approved computer authorization
/// plus the school's pre-existing Administrator credentials in an isolated
/// disposable restoration. It never replaces an existing school's database.
/// </summary>
internal static class ReplacementSchoolRecovery
{
    private static readonly string Root =
        Path.Combine(DatabaseHelper.DatabaseFolder, "ReplacementRecoveryStaging");

    internal static void RequireAbsentSchool()
    {
#if FMSC_LEVEL2
        throw new InvalidOperationException("Replacement-computer recovery is disabled in test-only builds.");
#else
        if (File.Exists(DatabaseHelper.DatabaseFile) ||
            File.Exists(Path.Combine(DatabaseHelper.DatabaseFolder, "FMSC_DB_log.ldf")))
            throw new InvalidOperationException(
                "Existing school database files were found. Replacement recovery will never overwrite or remove them.");
        using var master = DatabaseHelper.GetMasterConnection();
        DatabaseHelper.OpenConnection(master);
        using var lookup = new SqlCommand(
            "SELECT COUNT(*) FROM sys.databases WHERE name=N'FMSC_DB';", master);
        if (Convert.ToInt32(lookup.ExecuteScalar()) != 0)
            throw new InvalidOperationException(
                "FMSC_DB is already attached. Use the normal authorized Restore page for an existing school.");
#endif
    }

    /// <summary>Reject existing schools, even when the Windows user owns the MDF.
    /// Only an absent database or an unconfigured, zero-school-data template can
    /// enter the first-run wizard. This is independent of authentication state.</summary>
    internal static bool RequireAbsentOrVirginSchool()
    {
#if FMSC_LEVEL2
        throw new InvalidOperationException("New-computer recovery is disabled in test-only builds.");
#else
        bool hasMdf = File.Exists(DatabaseHelper.DatabaseFile);
        using var master = DatabaseHelper.GetMasterConnection();
        DatabaseHelper.OpenConnection(master);
        using var cmd = new SqlCommand("SELECT COUNT(*) FROM sys.databases WHERE name=N'FMSC_DB';", master);
        bool attached = Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        if (!hasMdf && !attached)
        {
            if (File.Exists(Path.Combine(DatabaseHelper.DatabaseFolder, "FMSC_DB_log.ldf")))
                throw new InvalidOperationException("Existing SQL log files prevent replacement-computer recovery.");
            return false; // genuinely absent
        }
        if (!hasMdf || !attached)
            throw new InvalidOperationException("Uncertain attached database state. FMSC will not overwrite school files.");
        if (!DatabaseHelper.TestConnection(out string reason))
            throw new InvalidOperationException("Existing school database cannot be authenticated as a clean first-run template. " + reason);
        using var original = DatabaseHelper.GetConnection();
        DatabaseHelper.OpenConnection(original);
        // This stored procedure is already the authoritative first-run gate.
        using (var first = new SqlCommand("dbo.GetFirstRunStatus", original) {
            CommandType = CommandType.StoredProcedure
        })
        using (var reader = first.ExecuteReader())
        {
            if (!reader.Read() || !reader.GetBoolean(reader.GetOrdinal("IsFirstRun")))
                throw new UnauthorizedAccessException(
                    "The computer already has an initialized school. Use the normal authenticated Restore page.");
        }
        // A missing Administrator alone is NOT sufficient: that may indicate
        // damaged access on a real school. Require no operator-owned records.
        using (var check = new SqlCommand("""
            SELECT COUNT(*) FROM sys.dm_db_partition_stats S
            JOIN sys.tables T ON T.object_id=S.object_id
            WHERE S.index_id IN (0,1) AND S.row_count>0 AND
                T.name NOT IN (N'DatabaseVersion',N'Roles',N'Permissions',
                    N'RolePermissions',N'BillingAutomationState',N'FeeTypes');
            """, original))
        {
            if (Convert.ToInt32(check.ExecuteScalar()) > 0)
                throw new UnauthorizedAccessException(
                    "School records or setup data already exist. The database is not a clean first-run template; recovery was refused.");
        }
        return true; // verified virgin template; mandatory safety backup first
#endif
    }

    internal static async Task CheckComputerAuthorizationAsync(CancellationToken token = default)
    {
#if FMSC_LEVEL2
        throw new InvalidOperationException("Replacement recovery is not available in test-only builds.");
#else
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent())
                .IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException(
                "Run FMSC as a Windows administrator to recover onto a replacement computer.");
        if (!UpdateDeviceKey.IsEnrolled)
            throw new UnauthorizedAccessException(
                "Authorize this computer in FMSC Updates first. An unapproved key cannot recover a school.");
        // This is an ONLINE proof that the publisher has approved this
        // specific Windows user's key for the private distribution repository.
        await using var proof = await UpdateGitSession.OpenAsync(token);
        token.ThrowIfCancellationRequested();
#endif
    }

    internal static async Task RecoverAsync(
        string? localBak, CloudRemoteBackup? remote, string recoveryPassphrase,
        string administratorUsername, string administratorPassword,
        IProgress<BackupProgressUpdate>? progress = null,
        CancellationToken token = default,
        IReadOnlyList<CloudRemoteBackup>? alternatives = null
    {
        if ((localBak is null) == (remote is null))
            throw new ArgumentException("Select exactly one local or cloud recovery source.");
        if (string.IsNullOrWhiteSpace(administratorUsername) ||
            string.IsNullOrEmpty(administratorPassword))
            throw new ArgumentException("Enter the recovered school's original Administrator username and password.");
        bool virgin = RequireAbsentOrVirginSchool();
        progress?.Report(new BackupProgressUpdate("Authorizing replacement computer",
            "Checking the publisher-approved device key and Windows administrator account."));
        await CheckComputerAuthorizationAsync(token);
        if (RequireAbsentOrVirginSchool() != virgin)
            throw new InvalidOperationException("The target school database changed while authorizing recovery.");

        string stage = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        string bak = localBak ?? Path.Combine(stage, "restored-school.bak");
        bool activationAttempted = false;
        try
        {
            if (remote is not null)
            {
                if (string.IsNullOrWhiteSpace(recoveryPassphrase) || recoveryPassphrase.Length < 12)
                    throw new ArgumentException("Enter the school's existing cloud recovery passphrase.");
                Directory.CreateDirectory(stage);
                RestrictStage(stage);
                string package = Path.Combine(stage, "download.fmscbackup");
                var samePoint = new[] { remote }.Concat(alternatives ?? Array.Empty<CloudRemoteBackup>())
                    .Where(x => string.Equals(x.PackageSha256, remote.PackageSha256,
                            StringComparison.OrdinalIgnoreCase) &&
                        (x.SqlBackupCreatedUtc - remote.SqlBackupCreatedUtc).Duration() <= TimeSpan.FromSeconds(1))
                    .DistinctBy(x => (x.Provider, x.RemoteId)).ToArray();
                var errors = new List<string>();
                bool acquired = false;
                foreach (CloudRemoteBackup copy in samePoint)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        progress?.Report(new BackupProgressUpdate("Downloading encrypted school backup",
                            "Testing the " + copy.Provider + " copy without changing SQL.", copy.Provider.ToString()));
                        await CloudBackupTransport.DownloadAsync(copy.Provider, copy.RemoteId, package, token);
                        await using (var stream = File.OpenRead(package))
                        {
                            string sha = Convert.ToHexString(await SHA256.HashDataAsync(stream, token))
                                .ToLowerInvariant();
                            if (!string.Equals(sha, copy.PackageSha256, StringComparison.OrdinalIgnoreCase))
                                throw new InvalidDataException(
                                    "Downloaded backup bytes do not match the advertised remote object identity.");
                        }
                        progress?.Report(new BackupProgressUpdate("Authenticating and decrypting",
                            "The passphrase and full encrypted package are checked before any SQL operation."));
                        await Task.Run(() => CloudBackupCrypto.Decrypt(package, bak, recoveryPassphrase), token);
                        DateTime sqlLocal = CloudBackupCrypto.ReadSqlHeaderLocalAfterVerification(package);
                        DateTime sqlUtc = CloudBackupCrypto.ReadUnverifiedCreationUtc(package);
                        var header = DatabaseBackupManager.GetBackupInfo(bak);
                        if (header?.IsForFmscDatabase != true || header.BackupStartDate == default ||
                            (header.BackupStartDate - sqlLocal).Duration() > TimeSpan.FromSeconds(1) ||
                            (sqlUtc - remote.SqlBackupCreatedUtc).Duration() > TimeSpan.FromSeconds(1))
                            throw new InvalidDataException("The encrypted package does not match the SQL backup identity.");
                        acquired = true;
                        break;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                        InvalidDataException or CryptographicException or HttpRequestException)
                    {
                        AppExceptionHandler.LogNonFatal(ex, "Rejected replacement-computer cloud copy before SQL restore");
                        errors.Add(copy.Provider + ": backup could not be downloaded or verified.");
                        progress?.Report(new BackupProgressUpdate("Trying another identical recovery copy",
                            "The selected source failed before SQL; only copies of the same package are eligible.",
                            copy.Provider.ToString(), true));
                        if (File.Exists(package)) File.Delete(package);
                        if (File.Exists(bak)) File.Delete(bak);
                    }
                }
                if (!acquired)
                    throw new InvalidDataException("No cloud copy of the exact selected recovery point passed download and decryption. " +
                        string.Join(" ", errors));
            }
            if (!File.Exists(bak)) throw new FileNotFoundException("The selected SQL backup is missing.", bak);
            progress?.Report(new BackupProgressUpdate("Verifying isolated school recovery",
                "Checking SQL structure, school identity and the original Administrator without touching FMSC_DB."));
            Guid candidateScope = Guid.Empty;
            await Task.Run(() =>
            {
                using var master = DatabaseHelper.GetMasterConnection();
                DatabaseHelper.OpenConnection(master);
                string checkedRoot = Path.Combine(DatabaseHelper.DatabaseFolder,
                    "ReplacementRecoveryChecks");
                Directory.CreateDirectory(checkedRoot);
                RestrictStage(checkedRoot);
                BackupRestoreIdentity.ValidateCandidateForNewComputer(master.ConnectionString,
                    bak, checkedRoot, candidate =>
                    {
                        candidateScope = BackupRestoreIdentity.ReadScope(candidate);
                        DatabaseCompatibility.Validate(candidate);
                        VerifyRecoveredAdministrator(candidate, administratorUsername, administratorPassword);
                    });
            }, token);
            if (candidateScope == Guid.Empty)
                throw new InvalidDataException("The recovered school's identity was not established.");
            token.ThrowIfCancellationRequested();
            if (RequireAbsentOrVirginSchool() != virgin)
                throw new InvalidOperationException("An existing school was created during recovery. Activation was refused.");
            // The operator has seen the recovery-point date in the wizard.
            // There is no existing school to back up or overwrite on this PC.
            progress?.Report(new BackupProgressUpdate("Activating verified school database",
                "Creating FMSC_DB only after the disposable school and Administrator checks passed."));
            activationAttempted = true;
            await Task.Run(() => ActivateRecoveryTarget(bak, candidateScope, virgin), CancellationToken.None);
            progress?.Report(new BackupProgressUpdate("Recovery completed",
                "The restored school database is ready for normal Administrator sign-in."));
        }
        finally
        {
            // Keep evidence only if the live activation attempt started and
            // could have left SQL in a partial state. In that case never
            // silently remove backup material or drop the attached database.
            if (!activationAttempted && Directory.Exists(stage))
            {
                try { Directory.Delete(stage, recursive: true); }
                catch (Exception ex) { AppExceptionHandler.LogNonFatal(ex, "Cleaning rejected new-school recovery staging"); }
            }
            else if (activationAttempted && Directory.Exists(stage))
            {
                // This path can also run on a successful recovery. Retain only
                // when SQL did not reach an established compatible database.
                bool usable = false;
                try { usable = DatabaseHelper.TestConnection(out _); } catch { }
                if (usable)
                {
                    try { Directory.Delete(stage, recursive: true); }
                    catch (Exception ex) { AppExceptionHandler.LogNonFatal(ex, "Cleaning completed new-school staging"); }
                }
            }
        }
    }

    private static void VerifyRecoveredAdministrator(SqlConnection candidate, string username, string password)
    {
        string entered = username.Trim();
        using var cmd = new SqlCommand("dbo.GetUserForLogin", candidate) {
            CommandType = CommandType.StoredProcedure, CommandTimeout = 30
        };
        cmd.Parameters.Add("@Username", SqlDbType.NVarChar, 100).Value = entered;
        using var adapter = new SqlDataAdapter(cmd);
        var table = new DataTable();
        adapter.Fill(table);
        if (table.Rows.Count != 1) throw new UnauthorizedAccessException(
            "The supplied school Administrator credentials could not be verified against the recovered database.");
        DataRow row = table.Rows[0];
        if (!string.Equals(Convert.ToString(row["Username"]), entered, StringComparison.Ordinal) ||
            !string.Equals(Convert.ToString(row["RoleName"]), "Administrator", StringComparison.OrdinalIgnoreCase) ||
            row["PasswordHash"] is not byte[] hash || hash.Length != 64 ||
            row["PasswordSalt"] is not byte[] salt || salt.Length != 32 ||
            table.Columns.Contains("LockedUntil") &&
                row["LockedUntil"] is DateTime lockedUntil && lockedUntil > DateTime.Now)
            throw new UnauthorizedAccessException(
                "An active, unlocked school Administrator is required to authorize replacement recovery.");
        byte[] computed = Rfc2898DeriveBytes.Pbkdf2(password, salt,
            100_000, HashAlgorithmName.SHA512, hash.Length);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(hash, computed))
                throw new UnauthorizedAccessException(
                    "The original school Administrator credentials were not accepted.");
        }
        finally { CryptographicOperations.ZeroMemory(computed); }
    }

    private static void ActivateRecoveryTarget(string bak, Guid verifiedScope, bool virgin)
    {
        if (RequireAbsentOrVirginSchool() != virgin)
            throw new InvalidOperationException("Recovery target changed before activation.");
        string data = DatabaseHelper.DatabaseFile;
        string log = Path.Combine(DatabaseHelper.DatabaseFolder, "FMSC_DB_log.ldf");
        Directory.CreateDirectory(DatabaseHelper.DatabaseFolder);
        if (virgin)
        {
            // The empty template still receives a verified, protected safety
            // backup; no automatic retention operation can delete this file.
            string safety = Path.Combine(DatabaseHelper.DatabaseFolder, "ReplacementSafetyBackups");
            Directory.CreateDirectory(safety);
            RestrictStage(safety);
            string safetyFile = Path.Combine(safety,
                "FMSC_BlankFirstRun_Safety_" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") +
                "_" + Guid.NewGuid().ToString("N") + ".bak");
            using var safetyMaster = DatabaseHelper.GetMasterConnection();
            DatabaseHelper.OpenConnection(safetyMaster);
            using (var backup = new SqlCommand(
                "BACKUP DATABASE [FMSC_DB] TO DISK=@Path WITH INIT, COPY_ONLY, CHECKSUM;", safetyMaster) {
                CommandTimeout = 0
            })
            {
                backup.Parameters.Add("@Path", SqlDbType.NVarChar, 1000).Value = safetyFile;
                backup.ExecuteNonQuery();
            }
            using (var verify = new SqlCommand(
                "RESTORE VERIFYONLY FROM DISK=@Path WITH CHECKSUM;", safetyMaster) {
                CommandTimeout = 0
            })
            {
                verify.Parameters.Add("@Path", SqlDbType.NVarChar, 1000).Value = safetyFile;
                verify.ExecuteNonQuery();
            }
            if (!File.Exists(safetyFile) || new FileInfo(safetyFile).Length == 0)
                throw new IOException("First-run safety backup was not verified; no SQL restoration was attempted.");
        }
        else if (File.Exists(data) || File.Exists(log))
            throw new IOException("Unexpected school files appeared before the new database could be installed.");
        using var master = DatabaseHelper.GetMasterConnection();
        DatabaseHelper.OpenConnection(master);
        using var list = new SqlCommand("RESTORE FILELISTONLY FROM DISK=@Path WITH FILE=1;", master) {
            CommandTimeout = 0
        };
        list.Parameters.Add("@Path", SqlDbType.NVarChar, 1000).Value = bak;
        using var table = new DataTable();
        using (var adapter = new SqlDataAdapter(list)) adapter.Fill(table);
        var dataRows = table.Rows.Cast<DataRow>().Where(r => Convert.ToString(r["Type"]) == "D").ToArray();
        var logRows = table.Rows.Cast<DataRow>().Where(r => Convert.ToString(r["Type"]) == "L").ToArray();
        if (table.Rows.Count != 2 || dataRows.Length != 1 || logRows.Length != 1)
            throw new InvalidDataException(
                "The recovered FMSC database must contain exactly one SQL data file and one log file.");
        if (RequireAbsentOrVirginSchool() != virgin)
            throw new IOException("The target school changed while the verified backup was prepared.");
        SqlConnection.ClearAllPools();
        if (virgin)
        {
            using var exclusive = new SqlCommand(
                "ALTER DATABASE [FMSC_DB] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;", master) {
                CommandTimeout = 60
            };
            exclusive.ExecuteNonQuery();
        }
        string restoreSql = "RESTORE DATABASE [FMSC_DB] FROM DISK=@Path " +
            "WITH FILE=1, RECOVERY, CHECKSUM, " +
            "MOVE @DataLogical TO @DataPath, MOVE @LogLogical TO @LogPath" +
            (virgin ? ", REPLACE;" : ";");
        using (var restore = new SqlCommand(restoreSql, master) { CommandTimeout = 0 })
        {
            restore.Parameters.Add("@Path", SqlDbType.NVarChar, 1000).Value = bak;
            restore.Parameters.Add("@DataLogical", SqlDbType.NVarChar, 128).Value = Convert.ToString(dataRows[0]["LogicalName"]);
            restore.Parameters.Add("@LogLogical", SqlDbType.NVarChar, 128).Value = Convert.ToString(logRows[0]["LogicalName"]);
            restore.Parameters.Add("@DataPath", SqlDbType.NVarChar, 1000).Value = data;
            restore.Parameters.Add("@LogPath", SqlDbType.NVarChar, 1000).Value = log;
            restore.ExecuteNonQuery();
        }
        DatabaseHelper.RefreshDatabaseConnection();
        if (!DatabaseHelper.TestConnection(out string reason))
            throw new InvalidDataException("SQL recovered the database but its final compatibility check failed. " +
                reason + " The files were preserved for administrator inspection.");
        using var recovered = DatabaseHelper.GetConnection();
        DatabaseHelper.OpenConnection(recovered);
        BackupRestoreIdentity.RequireSame(verifiedScope, BackupRestoreIdentity.ReadScope(recovered));
    }

    private static void RestrictStage(string folder)
    {
        var user = WindowsIdentity.GetCurrent().User
            ?? throw new UnauthorizedAccessException("The Windows recovery account is unknown.");
        var acl = new DirectorySecurity();
        acl.SetOwner(user);
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        acl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(folder).SetAccessControl(acl);
    }
}
