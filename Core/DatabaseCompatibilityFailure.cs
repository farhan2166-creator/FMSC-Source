using System.Globalization;

namespace FMSC_SMS.Core;

internal enum DatabaseCompatibilityIssue
{
    SchemaVersion, SchemaStructure, SqlCompatibility, DatabaseIdentity, ApplicationDefinition
}

/// <summary>Preserves the reason and version information through startup and recovery.</summary>
internal sealed record DatabaseCompatibilityFailure(
    DatabaseCompatibilityIssue Issue, string RequiredVersion, string? FoundVersion = null,
    string Details = "", int? SqlCompatibilityLevel = null)
{
    private string VersionSummary => $"Database schema version: {(string.IsNullOrWhiteSpace(FoundVersion) ? "unknown" : FoundVersion)}. Required by this application: {RequiredVersion}.";

    private int? VersionDirection =>
        long.TryParse(FoundVersion, NumberStyles.None, CultureInfo.InvariantCulture, out long found) &&
        long.TryParse(RequiredVersion, NumberStyles.None, CultureInfo.InvariantCulture, out long required)
            ? found.CompareTo(required) : null;

    internal bool IsDatabaseOlder => Issue == DatabaseCompatibilityIssue.SchemaVersion && VersionDirection < 0;
    internal bool IsDatabaseNewer => Issue == DatabaseCompatibilityIssue.SchemaVersion && VersionDirection > 0;

    internal string Title => Issue switch {
        DatabaseCompatibilityIssue.SchemaVersion when VersionDirection < 0 => "Database update required",
        DatabaseCompatibilityIssue.SchemaVersion when VersionDirection > 0 => "Application update required",
        DatabaseCompatibilityIssue.SchemaVersion => "Database version could not be verified",
        DatabaseCompatibilityIssue.SchemaStructure => "Database structure does not match",
        DatabaseCompatibilityIssue.SqlCompatibility => "SQL database settings need attention",
        DatabaseCompatibilityIssue.DatabaseIdentity => "Different database connected",
        _ => "Application installation needs attention"
    };

    internal string Message => Issue switch {
        DatabaseCompatibilityIssue.SchemaVersion when VersionDirection < 0 =>
            $"{VersionSummary}\r\n\r\nThis database is older than the application. Ask the administrator to back up the school data and apply this release's database migrations using FMSC Setup. Then choose Reconnect.",
        DatabaseCompatibilityIssue.SchemaVersion when VersionDirection > 0 =>
            $"{VersionSummary}\r\n\r\nThis database is newer than the application. Install the FMSC release that supports this database, then reopen FMSC. Do not downgrade the database or replace the school data.",
        DatabaseCompatibilityIssue.SchemaVersion =>
            $"{VersionSummary}\r\n\r\nFMSC cannot confirm a supported database version. Ask the administrator to verify the school database and its version history before applying any updates. See connection details for the check that failed.",
        DatabaseCompatibilityIssue.SchemaStructure =>
            $"{VersionSummary}\r\n\r\nThe version matches, but required database components are missing or changed. Ask the administrator to review connection details and complete the approved database update or recovery before choosing Reconnect.",
        DatabaseCompatibilityIssue.SqlCompatibility =>
            $"SQL compatibility level: {SqlCompatibilityLevel?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}. Required: 130 or later.\r\n\r\nAsk the administrator to verify the SQL Server version and update the database compatibility setting. Then choose Reconnect.",
        DatabaseCompatibilityIssue.DatabaseIdentity =>
            "The connected database does not match the approved FMSC school database. Ask the administrator to connect the correct school records, then choose Reconnect. See connection details for the expected database and file.",
        _ => "The application could not load its database compatibility definition. Ask the administrator to reinstall the complete FMSC application package, then reopen FMSC."
    };

    internal string ConnectionMessage => Message + "\r\n\r\nRestart Connection and Repair Connection do not resolve this compatibility issue.";
}

internal sealed class DatabaseCompatibilityException : InvalidOperationException
{
    internal DatabaseCompatibilityFailure Failure { get; }

    internal DatabaseCompatibilityException(DatabaseCompatibilityFailure failure)
        : base(failure.Message + (string.IsNullOrWhiteSpace(failure.Details) ? "" : "\r\n\r\n" + failure.Details)) => Failure = failure;
}
