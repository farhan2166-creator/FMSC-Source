# Pure Source/installer version parser used by Workflow 04 release preparation.
# Reject missing, duplicated and noncanonical definitions before any mutation.
# No GitHub API calls, files, SQL access or publication.
function Read-FmscWorkflow04SourceVersions {
    [CmdletBinding()]
    param([Parameter(Mandatory=$true)][string]$InstallerText)

    $appPattern='(?m)^\$AppVersion\s*=\s*''([0-9]+\.[0-9]+\.[0-9]+)''\r?$'
    $dbPattern='(?m)^\$DatabaseVersion\s*=\s*''([1-9][0-9]*)''\r?$'
    $appMatches=[regex]::Matches($InstallerText,$appPattern)
    $dbMatches=[regex]::Matches($InstallerText,$dbPattern)
    if ($appMatches.Count -ne 1 -or $dbMatches.Count -ne 1) {
        throw 'Release source must have exactly one canonical application version and database schema assignment.'
    }
    $appVersion=$appMatches[0].Groups[1].Value
    $schemaString=$dbMatches[0].Groups[1].Value
    $schema=0
    try {
        $version=[version]::Parse($appVersion)
        if ($version.ToString(3) -cne $appVersion) { throw 'noncanonical version' }
    }
    catch { throw 'Release application version is not canonical.' }
    if (-not [int]::TryParse($schemaString,[ref]$schema) -or
        $schema.ToString() -cne $schemaString) {
        throw 'Release database schema version is not canonical.'
    }
    return [pscustomobject]@{
        AppVersion=$appVersion
        Schema=$schema
        AppMatch=$appMatches[0]
        SchemaMatch=$dbMatches[0]
    }
}
