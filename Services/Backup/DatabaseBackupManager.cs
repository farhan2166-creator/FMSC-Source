using FMSC_SMS.Core;
using Microsoft.Data.SqlClient;
namespace FMSC_SMS.Services.Backup;
public static class DatabaseBackupManager
{
    private static string MasterConnectionString =>
        new SqlConnectionStringBuilder {
            DataSource = DatabaseHelper.DatabaseEndpoint,
            InitialCatalog = "master",
            IntegratedSecurity = true,
            TrustServerCertificate = true,
            ConnectTimeout = 30
        }.ConnectionString;
    private const int UnlimitedCommandTimeout = 0;
    private static readonly TimeSpan StaleBackupWarningThreshold = TimeSpan.FromDays(14);
    private const double DiskSpaceSafetyFactor = 1.25;
    private const long MinimumDiskSpaceBufferBytes = 200L * 1024 * 1024;
    public sealed class BackupResult
    {
        public bool Success { get; init; }
        public string Message { get; init; } = string.Empty;
    }
    public sealed class BackupInfo
    {
        public required string DatabaseName { get; init; }
        public DateTime BackupStartDate { get; init; }
        public DateTime? BackupFinishDate { get; init; }
        public TimeSpan Age => DateTime.Now - BackupStartDate;
        public bool IsStale => Age > StaleBackupWarningThreshold;
        public bool IsForFmscDatabase =>
            string.Equals(DatabaseName, "FMSC_DB", StringComparison.OrdinalIgnoreCase);
    }
    public static string CreateBackupFileName() =>
        $"FMSC_DB_Backup_{DateTime.Now:yyyy-MM-dd_HHmm}.bak";
    public static string CreatePreRestoreSafetyBackupFileName() =>
        $"FMSC_DB_PreRestore_{DateTime.Now:yyyy-MM-dd_HHmmss}.bak";
    public static string CreateUniqueBackupPath(string folder, string fileName)
    {
        string name = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        int counter = 2;
        string path = Path.Combine(folder, fileName);
        while (File.Exists(path))
        {
            path = Path.Combine(folder, $"{name}_{counter}{extension}");
            counter++;
        }
        return path;
    }
    public static BackupResult CreateBackup(
        string backupPath,
        IProgress<string>? progress = null,
        bool isSafetyBackup = false)
    {
        if (string.IsNullOrWhiteSpace(backupPath))
            return Failed("Backup path was not specified.");
        try
        {
            string? folder = Path.GetDirectoryName(backupPath);
            if (string.IsNullOrWhiteSpace(folder))
                return Failed("Invalid backup folder.");
            Directory.CreateDirectory(folder);
            string? spaceProblem = CheckAvailableDiskSpace(folder);
            if (spaceProblem is not null)
            {
                RecordHistorySafe("BACKUP", backupPath, null, "Failed", spaceProblem, isSafetyBackup);
                return Failed(spaceProblem);
            }
            progress?.Report("Backing up database...");
            DatabaseHelper.ExecuteUser(
                "BackupFmscDatabase",
                UnlimitedCommandTimeout,
                DatabaseHelper.P("@BackupPath", backupPath));
            progress?.Report("Verifying backup file...");
            if (!File.Exists(backupPath))
            {
                RecordHistorySafe("BACKUP", backupPath, null, "Failed", "The backup file was not found after BACKUP DATABASE completed.", isSafetyBackup);
                return Failed("The backup command completed, but the backup file was not found.");
            }
            long fileSize = new FileInfo(backupPath).Length;
            if (fileSize <= 0)
            {
                RecordHistorySafe("BACKUP", backupPath, fileSize, "Failed", "The backup file was created but is empty.", isSafetyBackup);
                return Failed("The backup file was created but is empty.");
            }
            progress?.Report("Verifying backup integrity...");
            try
            {
                ValidateBackupFile(backupPath);
            }
            catch (Exception verifyEx)
            {
                string verifyMessage =
                    $"The backup file was created but failed integrity verification " +
                    $"(RESTORE VERIFYONLY): {DbMessage.UserMessage(verifyEx)}";
                RecordHistorySafe("BACKUP", backupPath, fileSize, "Failed", verifyMessage, isSafetyBackup);
                progress?.Report("Backup failed verification.");
                return Failed(
                    "The backup completed, but the resulting file did not pass integrity " +
                    "verification and cannot be trusted. It has not been recorded as a " +
                    "successful backup.\n\n" +
                    DbMessage.UserMessage(verifyEx));
            }
            /* The backup file is verified by this point, so it is real. Say so rather than silently losing the record. */
            bool recorded = RecordHistorySafe("BACKUP", backupPath, fileSize, "Success", null, isSafetyBackup);
            progress?.Report("Backup completed successfully.");
            return recorded
                ? Success("Database backup completed and verified successfully.")
                : Success(
                    "Database backup completed and verified successfully, but it could " +
                    "not be recorded in the backup history. The file is valid - note its " +
                    "location now, because it will not appear in the history list.\n\n" +
                    $"File: {backupPath}\n\n" +
                    $"See the log for details: {AppExceptionHandler.LogPath}");
        }
        catch (Exception ex)
        {
            RecordHistorySafe("BACKUP", backupPath, null, "Failed", ex.Message, isSafetyBackup);
            progress?.Report("Backup failed.");
            return Failed($"Backup failed:\n{DbMessage.UserMessage(ex)}");
        }
    }
    /// <summary>The total physical size of the database - data AND log files.</summary>
    private static long? TryGetTotalDatabaseSizeBytes()
    {
        try
        {
            var builder = new SqlConnectionStringBuilder {
                DataSource = DatabaseHelper.DatabaseEndpoint,
                InitialCatalog = "FMSC_DB",
                IntegratedSecurity = true,
                TrustServerCertificate = true,
                ConnectTimeout = 30
            };
            using SqlConnection connection = new(builder.ConnectionString);
            DatabaseHelper.OpenConnection(connection);
            using SqlCommand command = new("""
                SELECT ISNULL(SUM(CONVERT(BIGINT, size)), 0) * 8192
                FROM sys.database_files;
                """, connection);
            command.CommandTimeout = 30;
            object? result = command.ExecuteScalar();
            if (result is null || result == DBNull.Value)
                return null;
            long total = Convert.ToInt64(result);
            return total > 0 ? total : null;
        }
        catch (Exception nonFatalException78)
        {
            FMSC_SMS.Core.AppExceptionHandler.LogNonFatal(nonFatalException78, "DatabaseBackupManager: TryGetTotalDatabaseSizeBytes fallback");
            /* Not fatal: a size we cannot read must not block a backup. The caller falls back to the MDF size it can measure. */
            return null;
        }
    }
    private static string? CheckAvailableDiskSpace(string folder)
    {
        try
        {
            if (!File.Exists(DatabaseHelper.DatabaseFile))
                return null;
            /* prefer the real database footprint (data + log) from SQL Server; fall back to the MDF size only when it cannot be read. */
            long? measured = TryGetTotalDatabaseSizeBytes();
            long currentDatabaseSize =
                measured ?? new FileInfo(DatabaseHelper.DatabaseFile).Length;
            long requiredBytes = Math.Max(
                (long)(currentDatabaseSize * DiskSpaceSafetyFactor),
                MinimumDiskSpaceBufferBytes);
            string fullFolder = Path.GetFullPath(folder);
            string? rootPath = Path.GetPathRoot(fullFolder);
            if (string.IsNullOrEmpty(rootPath))
                return null;
            var drive = new DriveInfo(rootPath);
            if (!drive.IsReady)
                return null;
            if (drive.AvailableFreeSpace < requiredBytes)
            {
                return
                    $"The backup folder's drive ({drive.Name}) does not have enough free " +
                    $"space for a safe backup.\n\nAvailable: {FormatBytes(drive.AvailableFreeSpace)}" +
                    $"\nEstimated space needed: {FormatBytes(requiredBytes)}" +
                    (measured is null
                        ? "\n(Estimated from the data file only - the database size could not be read.)"
                        : "\n(Based on the total size of the database data and log files.)") +
                    "\n\nFree up space or choose a different backup folder, then try again.";
            }
            return null;
        }
        catch (Exception nonFatalException79)
        {
            FMSC_SMS.Core.AppExceptionHandler.LogNonFatal(nonFatalException79, "DatabaseBackupManager: CheckAvailableDiskSpace fallback");
            return null;
        }
    }
    private static string FormatBytes(long bytes)
    {
        double mb = bytes / 1024.0 / 1024.0;
        return mb >= 1024
            ? $"{mb / 1024.0:0.0} GB"
            : $"{mb:0} MB";
    }
    /// <summary>read the backup header and THROW when it cannot be read. A null there cannot be told apart from "not an FMSC_DB backup", so a restore must not use it to decide.</summary>
    private static BackupInfo ReadBackupHeader(string backupPath)
    {
        if (string.IsNullOrWhiteSpace(backupPath))
            throw new InvalidOperationException("Backup file was not specified.");
        if (!File.Exists(backupPath))
            throw new InvalidOperationException("The selected backup file does not exist.");
        using SqlConnection connection = new(MasterConnectionString);
        using SqlCommand command = new("""
            RESTORE HEADERONLY
            FROM DISK = @BackupPath;
            """, connection);
        command.CommandTimeout = UnlimitedCommandTimeout;
        command.Parameters.Add(new SqlParameter("@BackupPath", SqlDbType.NVarChar, 1000) { Value = backupPath });
        DatabaseHelper.OpenConnection(connection);
        using SqlDataReader reader = command.ExecuteReader();
        if (!reader.Read())
            throw new InvalidOperationException(
                "The selected file has no readable SQL Server backup header. It may not be a backup file.");
        // Never substitute the current clock if SQL Server fails to return
        // a genuine backup timestamp: recovery chronology must stay truthful.
        if (reader["BackupStartDate"] is not DateTime backupStart)
            throw new InvalidDataException("SQL backup header is missing its original backup creation time.");
        return new BackupInfo {
            DatabaseName = reader["DatabaseName"] as string ?? "(unknown)",
            BackupStartDate = backupStart,
            BackupFinishDate = reader["BackupFinishDate"] is DateTime finish ? finish : null
        };
    }
    public static BackupInfo? GetBackupInfo(string backupPath)
    {
        try
        {
            return ReadBackupHeader(backupPath);
        }
        catch (Exception nonFatalException80)
        {
            FMSC_SMS.Core.AppExceptionHandler.LogNonFatal(nonFatalException80, "DatabaseBackupManager: GetBackupInfo fallback");
            /* Callers that must act on the result use ReadBackupHeader so the reason is not discarded. */
            return null;
        }
    }
    /// <param name="safetyBackupCreated">
    /// Receives the full path after the mandatory safety backup has been verified.
    /// </param>
    public static BackupResult RestoreBackup(
        string backupPath,
        IProgress<string>? progress = null,
        Action<string>? safetyBackupCreated = null,
        string? safetyBackupFolderOverride = null,
        string? historyPathOverride = null)
    {
        if (string.IsNullOrWhiteSpace(backupPath))
            return Failed("Backup file was not specified.");
        if (!File.Exists(backupPath))
            return Failed("The selected backup file does not exist.");
        bool singleUserSet = false;
        bool restoreStarted = false;
        string? safetyPath = null;
        Guid expectedScope=Guid.Empty;
        try
        {
            // Keep this exact backup readable but prevent it being overwritten
            // or swapped between identity verification and the live restore.
            using var backupLock=new FileStream(backupPath,FileMode.Open,FileAccess.Read,FileShare.Read);
            progress?.Report("Validating backup file...");
            ValidateBackupFile(backupPath);
            using(var current=new SqlConnection(DatabaseHelper.GetConnectionString("FMSC_DB")))
            {
                DatabaseHelper.OpenConnection(current);expectedScope=BackupRestoreIdentity.ReadScope(current);
            }
            progress?.Report("Checking the backup's school identity and compatibility...");
            BackupRestoreIdentity.ValidateCandidate(MasterConnectionString,backupPath,Path.Combine(DatabaseHelper.DatabaseFolder,"RestoreChecks"),expectedScope,DatabaseCompatibility.Validate);
            progress?.Report("Taking a safety backup of the current database...");
            safetyPath = TryCreatePreRestoreSafetyBackup(backupPath, progress, safetyBackupCreated, safetyBackupFolderOverride);
            using(var current=new SqlConnection(DatabaseHelper.GetConnectionString("FMSC_DB")))
            {
                DatabaseHelper.OpenConnection(current);BackupRestoreIdentity.RequireSame(expectedScope,BackupRestoreIdentity.ReadScope(current));
            }
            SqlConnection.ClearAllPools();
            progress?.Report("Connecting to SQL Server...");
            using SqlConnection connection = new(MasterConnectionString);
            DatabaseHelper.OpenConnection(connection);
            progress?.Report("Preparing database for restore...");
            ExecuteCommand(connection, """
                ALTER DATABASE [FMSC_DB]
                SET SINGLE_USER
                WITH ROLLBACK IMMEDIATE;
                """);
            singleUserSet = true;
            progress?.Report("Restoring database...");
            using (SqlCommand restoreCommand = new("""
                RESTORE DATABASE [FMSC_DB]
                FROM DISK = @BackupPath
                WITH REPLACE, RECOVERY, CHECKSUM, STATS = 10;
                """, connection))
            {
                restoreCommand.CommandTimeout = UnlimitedCommandTimeout;
                restoreCommand.Parameters.Add(new SqlParameter("@BackupPath", SqlDbType.NVarChar, 1000) { Value = backupPath });
                restoreStarted = true;
                restoreCommand.ExecuteNonQuery();
            }
            progress?.Report("Returning database to normal access...");
            ExecuteCommand(connection, """
                ALTER DATABASE [FMSC_DB]
                SET MULTI_USER;
                """);
            singleUserSet = false;
            progress?.Report("Verifying the restored database...");
            using (var restored = new SqlConnection(DatabaseHelper.GetConnectionString("FMSC_DB")))
            {
                DatabaseHelper.OpenConnection(restored);
                DatabaseCompatibility.Validate(restored);
                BackupRestoreIdentity.RequireSame(expectedScope,BackupRestoreIdentity.ReadScope(restored));
            }
            progress?.Report("Recording restore history...");
            long fileSize = new FileInfo(backupPath).Length;
            /* same reasoning as the backup path: the restore succeeded, so report it, but do not claim the history was recorded when it was not. */
            bool recorded = RecordHistorySafe("RESTORE", historyPathOverride ?? backupPath, fileSize, "Success", null, isSafetyBackup: false);
            progress?.Report("Restore completed successfully.");
            return recorded
                ? Success("Database restore completed successfully.")
                : Success(
                    "Database restore completed successfully, but it could not be " +
                    "recorded in the backup history.\n\n" +
                    $"See the log for details: {AppExceptionHandler.LogPath}");
        }
        catch (Exception ex)
        {
            if (singleUserSet)
            {
                try
                {
                    using SqlConnection recoveryConnection = new(MasterConnectionString);
                    DatabaseHelper.OpenConnection(recoveryConnection);
                    ExecuteCommand(recoveryConnection, """
                        ALTER DATABASE [FMSC_DB]
                        SET MULTI_USER;
                        """);
                }
                catch (Exception recoveryEx)
                {
                    AppExceptionHandler.LogNonFatal(
                        recoveryEx,
                        "Restoring MULTI_USER after a failed database restore - the database may be left in SINGLE_USER and unavailable");
                }
            }
            long? fileSize = null;
            try
            {
                if (File.Exists(backupPath))
                    fileSize = new FileInfo(backupPath).Length;
            }
            catch (Exception nonFatalException81)
            {
                FMSC_SMS.Core.AppExceptionHandler.LogNonFatal(nonFatalException81, "DatabaseBackupManager: RestoreBackup fallback");
            }
            AppExceptionHandler.LogNonFatal(ex, "Requested database restore failed compatibility or execution checks");
            string recoveryMessage = "";
            if (restoreStarted && safetyPath is not null)
            {
                progress?.Report("Restoring the pre-restore safety backup...");
                try
                {
                    SqlConnection.ClearAllPools();
                    using var recovery = new SqlConnection(MasterConnectionString);
                    DatabaseHelper.OpenConnection(recovery);
                    ExecuteCommand(recovery, "ALTER DATABASE [FMSC_DB] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;");
                    using var rollback = new SqlCommand("RESTORE DATABASE [FMSC_DB] FROM DISK=@SafetyPath WITH REPLACE, RECOVERY, CHECKSUM;", recovery);
                    rollback.CommandTimeout = UnlimitedCommandTimeout;
                    rollback.Parameters.Add("@SafetyPath", SqlDbType.NVarChar, 1000).Value = safetyPath;
                    try { rollback.ExecuteNonQuery(); }
                    finally { ExecuteCommand(recovery, "ALTER DATABASE [FMSC_DB] SET MULTI_USER;"); }
                    using var recovered = new SqlConnection(DatabaseHelper.GetConnectionString("FMSC_DB"));
                    DatabaseHelper.OpenConnection(recovered);
                    DatabaseCompatibility.Validate(recovered);
                    BackupRestoreIdentity.RequireSame(expectedScope,BackupRestoreIdentity.ReadScope(recovered));
                    StudentSearchCache.Invalidate();
                    recoveryMessage = "\n\nThe pre-restore safety backup was automatically restored. Your original working database has been preserved.";
                }
                catch (Exception recoveryEx)
                {
                    AppExceptionHandler.LogNonFatal(recoveryEx, "Automatic pre-restore safety backup recovery failed");
                    recoveryMessage = $"\n\nAutomatic recovery failed. Do not use the database until the safety backup is restored manually:\n{safetyPath}\n{DbMessage.UserMessage(recoveryEx)}";
                }
            }
            RecordHistorySafe("RESTORE", historyPathOverride ?? backupPath, fileSize, "Failed", ex.Message, isSafetyBackup: false);
            progress?.Report("Restore failed.");
            return Failed($"Restore failed:\n{DbMessage.UserMessage(ex)}{recoveryMessage}");
        }
        finally
        {
            // A restore can change the database even when its acknowledgement or
            // automatic recovery fails. Subsequent reads/writes must verify again.
            if (restoreStarted)
                DatabaseHelper.RefreshDatabaseConnection();
        }
    }
    /// <summary>The pre-restore safety backup is MANDATORY.</summary>
    private static string TryCreatePreRestoreSafetyBackup(
        string aboutToRestoreFrom,
        IProgress<string>? progress,
        Action<string>? safetyBackupCreated = null,
        string? safetyBackupFolderOverride = null)
    {
        string folder;
        string safetyPath;
        try
        {
            folder = !string.IsNullOrWhiteSpace(safetyBackupFolderOverride)
                ? safetyBackupFolderOverride
                : BackupSettings.GetBackupFolder() is { Length: > 0 } configuredFolder
                    ? configuredFolder
                    : Path.GetDirectoryName(aboutToRestoreFrom) ?? DatabaseHelper.DatabaseFolder;
            safetyPath = CreateUniqueBackupPath(folder, CreatePreRestoreSafetyBackupFileName());
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "The restore was stopped because a safety backup of the current database could not be started. " +
                $"Nothing has been changed.\n\n{DbMessage.UserMessage(ex)}", ex);
        }
        BackupResult safetyResult = CreateBackup(safetyPath, progress: null, isSafetyBackup: true);
        if (!safetyResult.Success)
        {
            /* The restore has NOT started, so this message must say plainly that nothing was changed. */
            throw new InvalidOperationException(
                "The restore was stopped because a safety backup of the current database could not be created. " +
                "Nothing has been changed. A restore replaces the live database, so it will not proceed " +
                $"without a verified copy of it first.\n\n{safetyResult.Message}");
        }
        progress?.Report($"Safety backup saved: {Path.GetFileName(safetyPath)}");
        safetyBackupCreated?.Invoke(safetyPath);
        return safetyPath;
    }
    /// <summary>verify the backup is INTEGRITY-VALID **and** is an FMSC_DB backup. The header is read first so an unreadable or foreign file is rejected before the more expensive integrity pass runs.</summary>
    private static void ValidateBackupFile(string backupPath)
    {
        BackupInfo info = ReadBackupHeader(backupPath);
        if (!info.IsForFmscDatabase)
        {
            throw new InvalidOperationException(
                $"This backup was taken from \"{info.DatabaseName}\", not FMSC_DB. " +
                "Restoring it would replace the school database with a different database. " +
                "Restoring another database's backup is not permitted.");
        }
        using SqlConnection connection = new(MasterConnectionString);
        using SqlCommand command = new("""
            RESTORE VERIFYONLY
            FROM DISK = @BackupPath;
            """, connection);
        command.CommandTimeout = UnlimitedCommandTimeout;
        command.Parameters.Add(new SqlParameter("@BackupPath", SqlDbType.NVarChar, 1000) { Value = backupPath });
        DatabaseHelper.OpenConnection(connection);
        command.ExecuteNonQuery();
    }
    private static void ExecuteCommand(SqlConnection connection, string sql)
    {
        using SqlCommand command = new(sql, connection);
        command.CommandTimeout = UnlimitedCommandTimeout;
        command.ExecuteNonQuery();
    }
    public static BackupResult RelocateBackupPath(long backupId, string newFilePath)
    {
        if (string.IsNullOrWhiteSpace(newFilePath))
            return Failed("A new file path was not specified.");
        if (!File.Exists(newFilePath))
            return Failed("The selected file does not exist.");
        try
        {
            DatabaseHelper.ExecuteUser(
                "UpdateDatabaseBackupPath",
                DatabaseHelper.P("@BackupId", backupId),
                DatabaseHelper.P("@NewFilePath", newFilePath),
                DatabaseHelper.P("@NewFileName", Path.GetFileName(newFilePath)));
            return Success("Backup record updated with the new file location.");
        }
        catch (Exception ex)
        {
            return Failed($"Could not update the backup record:\n{DbMessage.UserMessage(ex)}");
        }
    }
    public static int TryAutoRelinkMissingBackups(string newFolder)
    {
        return AutoRelinkMissingBackups(newFolder, applyChanges: true);
    }
    /// <summary>report what <see cref="TryAutoRelinkMissingBackups"/> would relink, WITHOUT changing anything. Read-only: no history row is written here.</summary>
    public static int CountAutoRelinkableBackups(string newFolder)
    {
        return AutoRelinkMissingBackups(newFolder, applyChanges: false);
    }
    private static int AutoRelinkMissingBackups(string newFolder, bool applyChanges)
    {
        if (string.IsNullOrWhiteSpace(newFolder) || !Directory.Exists(newFolder))
            return 0;
        int relinked = 0;
        try
        {
            DataTable history = DatabaseHelper.QueryUser("GetDatabaseBackupHistory");
            foreach (DataRow row in history.Rows)
            {
                string? recordedPath = row["FilePath"] as string;
                string? fileName = row["FileName"] as string;
                if (string.IsNullOrWhiteSpace(recordedPath) || string.IsNullOrWhiteSpace(fileName))
                    continue;
                if (File.Exists(recordedPath))
                    continue;
                string candidatePath = Path.Combine(newFolder, fileName);
                if (string.Equals(
                        Path.GetFullPath(candidatePath),
                        Path.GetFullPath(recordedPath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (!File.Exists(candidatePath))
                    continue;
                long backupId = Convert.ToInt64(row["BackupId"]);
                if (!applyChanges)
                {
                    /* Count-only pass for the folder-change confirmation: this row matches every relink criterion, so the applying pass will relocate it. */
                    relinked++;
                    continue;
                }
                BackupResult result = RelocateBackupPath(backupId, candidatePath);
                if (result.Success)
                    relinked++;
            }
        }
        catch (Exception ex)
        {
            /* this was an empty catch around the whole relink sweep. The caller reports "0 backup(s) relinked" either way, so a real failure looked like a clean no-op. */
            AppExceptionHandler.LogNonFatal(
                ex,
                applyChanges
                    ? "Auto-relinking missing database backups to the newly selected folder"
                    : "Counting backup records that would be relinked to the newly selected folder");
        }
        return relinked;
    }
    /* Returns whether the history row was actually written, and logs the failure with context if not. */
    private static bool RecordHistorySafe(
        string operation,
        string filePath,
        long? fileSize,
        string status,
        string? remarks,
        bool isSafetyBackup)
    {
        try
        {
            string? effectiveRemarks = isSafetyBackup
                ? Truncate(
                    string.IsNullOrWhiteSpace(remarks)
                        ? "Automatic safety backup taken before a database restore."
                        : $"Automatic safety backup taken before a database restore. {remarks}",
                    500)
                : Truncate(remarks, 500);
            DatabaseHelper.Execute(
                "RecordDatabaseBackup",
                DatabaseHelper.P("@Operation", operation),
                DatabaseHelper.P("@FileName", Path.GetFileName(filePath)),
                DatabaseHelper.P("@FilePath", filePath),
                DatabaseHelper.P("@FileSizeBytes", fileSize),
                DatabaseHelper.P("@Status", status),
                DatabaseHelper.P("@UserId", CurrentUser.UserId),
                DatabaseHelper.P("@Remarks", effectiveRemarks));
            return true;
        }
        catch (Exception ex)
        {
            AppExceptionHandler.LogNonFatal(
                ex,
                $"Recording {operation} history ({status}) for '{Path.GetFileName(filePath)}' - the operation itself was not affected, but it will not appear in the backup history");
            return false;
        }
    }
    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrEmpty(value) || value.Length <= maxLength
            ? value
            : value[..maxLength];
    public static DataTable GetHistory()
    {
        DataTable history = DatabaseHelper.QueryUser("GetDatabaseBackupHistory");
        history.Columns.Add("FileAvailable", typeof(string));
        foreach (DataRow row in history.Rows)
        {
            string? path = row["FilePath"] as string;
            row["FileAvailable"] =
                path?.StartsWith("cloud://", StringComparison.OrdinalIgnoreCase) == true
                    ? "Cloud copy"
                    : !string.IsNullOrWhiteSpace(path) && File.Exists(path)
                        ? "Available" : "Missing";
        }
        return history;
    }
    /// <summary>
    /// Read the actual SQL header for local history entries; file timestamps
    /// and recent-history dates are NOT authoritative recovery-point times.
    /// </summary>
    public static DateTime? GetNewestAvailableLocalBackupUtc()
    {
        var history = GetHistory();
        DateTime? newest = null;
        foreach (DataRow row in history.Rows)
        {
            if (Convert.ToString(row["FileAvailable"]) != "Available") continue;
            if (history.Columns.Contains("Operation") &&
                !string.Equals(Convert.ToString(row["Operation"]), "BACKUP", StringComparison.OrdinalIgnoreCase))
                continue;
            if (history.Columns.Contains("Status") &&
                !string.Equals(Convert.ToString(row["Status"]), "Success", StringComparison.OrdinalIgnoreCase))
                continue;
            string? path = Convert.ToString(row["FilePath"]);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
            var info = GetBackupInfo(path);
            if (info?.IsForFmscDatabase != true || info.BackupStartDate == default) continue;
            DateTime actual = DateTime.SpecifyKind(info.BackupStartDate, DateTimeKind.Local).ToUniversalTime();
            if (newest is null || newest < actual) newest = actual;
        }
        return newest;
    }
    /// <summary>How many history entries are kept. The newest entries are the ones that matter, so the oldest beyond this are the ones dropped.</summary>
    public const int HistoryLimit = 50;
    /// <summary>Prunes backup history using the configured retention rules.</summary>
    public static (int Removed, int Failed, string? FirstFailure) PruneHistory()
    {
        int removed = 0;
        int failed = 0;
        string? firstFailure = null;
        try
        {
            DataTable history = GetHistory();
            /* Newest first, so "beyond the limit" means the oldest. */
            var ordered = history.Rows
                .Cast<DataRow>()
                .OrderByDescending(row => Convert.ToDateTime(row["BackupDate"]))
                .ToList();
            int kept = 0;
            foreach (DataRow row in ordered)
            {
                bool gone = IsHistoryPathGone(Convert.ToString(row["FilePath"]));
                if (!gone && kept < HistoryLimit)
                {
                    kept++;
                    continue;
                }
                long backupId = Convert.ToInt64(row["BackupId"]);
                try
                {
                    DatabaseHelper.ExecuteUser(
                        "DeleteDatabaseBackupRecord",
                        DatabaseHelper.P("@BackupId", backupId));
                    removed++;
                }
                catch (Exception ex)
                {
                    failed++;
                    firstFailure ??= DbMessage.UserMessage(ex);
                }
            }
        }
        catch (Exception ex)
        {
            /* The history could not even be read; the page's own load reports that failure. */
            failed++;
            firstFailure ??= DbMessage.UserMessage(ex);
        }
        return (removed, failed, firstFailure);
    }
    /// <summary>True when a recorded backup file can no longer be reached from this machine.</summary>
    private static bool IsHistoryPathGone(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return true;
        // Retain audit records of cloud recoveries after downloaded temp files
        // are cleared. Upload/remote retention is handled independently.
        if (path.StartsWith("cloud://", StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            if (File.Exists(path))
                return false;
            string? folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
                return true;
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            return !string.IsNullOrWhiteSpace(root) &&
                   new DriveInfo(root).IsReady;
        }
        catch (Exception nonFatalException82)
        {
            FMSC_SMS.Core.AppExceptionHandler.LogNonFatal(nonFatalException82, "DatabaseBackupManager: IsHistoryPathGone fallback");
            /* A path this machine cannot parse, or a volume it cannot ask about: kept, because a wrong delete throws away the only record of a backup. */
            return false;
        }
    }
    private static BackupResult Success(string message) => new() { Success = true, Message = message };
    private static BackupResult Failed(string message) => new() { Success = false, Message = message };
}
