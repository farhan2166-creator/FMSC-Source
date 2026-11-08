# Read-only owner-side release provenance and LocalDB compatibility probe.
# Snapshot of the published 1.0.16 release as independently read on 2026-11-08.
# No install, update, migration, SQL write, sending or public evidence upload.
#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$ExecutablePath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Pinned to the private 1.0.16 update-manifest.json (not the newer Source/main).
$publishedVersion = '1.0.16'
$publishedSchema = 10
$publishedSourceSha = '4abe2d037f9ce51c0922875e7c0241cb112ac88d'
$sourceMainAtReview = 'c49619e6e762cc47ca9e400a5d1b2577aeb4ef32'
$publishedExeSha256 = '4c5b5201851b202e2b7ff33c26cabc20bcd00b9c4486be5e99df22de819c8990'
$publishedBuildInfoSha256 = '755b790aac1ef8f38a68a7369ade0fc7d6b97ddf1b519acd401e1f55cd346d03'

$runningPaths = @(
    Get-Process -Name 'FMSC_SMS' -ErrorAction SilentlyContinue |
        ForEach-Object { try { $_.Path } catch { $null } } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Sort-Object -Unique
)
if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
    if ($runningPaths.Count -ne 1) {
        throw 'Run FMSC under this Windows account, or specify -ExecutablePath for the installed FMSC_SMS.exe. Refusing to guess among zero/multiple processes.'
    }
    $ExecutablePath = $runningPaths[0]
}
$exe = Get-Item -LiteralPath $ExecutablePath -ErrorAction Stop
if ($exe.PSIsContainer -or $exe.Name -cne 'FMSC_SMS.exe') {
    throw 'The selected file must be FMSC_SMS.exe; no other executable will be run.'
}
$actualExeSha256 = (Get-FileHash -LiteralPath $exe.FullName -Algorithm SHA256 -ErrorAction Stop).Hash.ToLowerInvariant()
$binaryMatchesPublication = $actualExeSha256 -ceq $publishedExeSha256
$versionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exe.FullName)
$versionString = [string]$versionInfo.ProductVersion
$runningSelection = if ($runningPaths.Count -eq 0) { 'NOT RUNNING (file inspected only)' }
    elseif ($runningPaths.Count -eq 1 -and
        [string]::Equals($runningPaths[0],$exe.FullName,[StringComparison]::OrdinalIgnoreCase)) { 'MATCH' }
    else { 'DIFFERENT RUNNING EXECUTABLE OR MULTIPLE PROCESSES' }

$buildInfo = Join-Path $exe.DirectoryName 'BUILD_INFO.txt'
$buildInfoStatus = 'NOT PRESENT'
if (Test-Path -LiteralPath $buildInfo -PathType Leaf) {
    $buildInfoHash = (Get-FileHash -LiteralPath $buildInfo -Algorithm SHA256 -ErrorAction Stop).Hash.ToLowerInvariant()
    $buildInfoStatus = if ($buildInfoHash -ceq $publishedBuildInfoSha256) {
        'MATCHES PUBLISHED BUILD_INFO'
    } else { 'DIFFERS FROM PUBLISHED BUILD_INFO' }
}

# Read only the version number; do not query guardian, student or fee data.
$schema = $null
$schemaStatus = 'NOT CHECKED'
try {
    Add-Type -AssemblyName System.Data
    $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
    $builder.DataSource = '(localdb)\MSSQLLocalDB'
    $builder.InitialCatalog = 'FMSC_DB'
    $builder.IntegratedSecurity = $true
    $builder.ConnectTimeout = 15
    $builder.Encrypt = $false
    $connection = New-Object System.Data.SqlClient.SqlConnection($builder.ConnectionString)
    try {
        $connection.Open()
        $command = $connection.CreateCommand()
        try {
            $command.CommandTimeout = 15
            $command.CommandText = 'SET NOCOUNT ON; SELECT TOP(1) TRY_CONVERT(int, VersionNumber) FROM dbo.DatabaseVersion ORDER BY VersionId DESC;'
            $value = $command.ExecuteScalar()
            if ($null -ne $value -and $value -isnot [DBNull]) {
                $schema = [int]$value
            }
        } finally { $command.Dispose() }
    } finally { $connection.Dispose() }
    $schemaStatus = if ($schema -eq $publishedSchema) { 'PASS' } else { 'SCHEMA MISMATCH OR UNAVAILABLE' }
} catch {
    $schemaStatus = 'UNVERIFIED: Windows-authenticated FMSC_DB could not be read by this account'
}

# Only execute the shipped read-only verification entry point if this EXE's
# bytes match the trusted publication. Do not launch unknown/older binaries.
$compatibilityStatus = 'NOT RUN: exact published EXE or schema-10 match required'
if ($binaryMatchesPublication -and $schema -eq $publishedSchema) {
    try {
        $check = Start-Process -FilePath $exe.FullName -ArgumentList @(
            '--verify-installation', '(localdb)\MSSQLLocalDB', 'FMSC_DB'
        ) -Wait -PassThru -ErrorAction Stop
        $compatibilityStatus = if ($check.ExitCode -eq 0) {
            'PASS: published application structural compatibility'
        } else { "FAILED: published application verifier exit $($check.ExitCode)" }
    } catch {
        $compatibilityStatus = 'FAILED: published application compatibility verifier could not run'
    }
}

$binaryStatus = if ($binaryMatchesPublication) { 'MATCHES PUBLISHED 1.0.16' }
    else { 'NOT THE VERIFIED PUBLISHED 1.0.16 EXECUTABLE' }
Write-Output 'FMSC AUDIT 04 — READ-ONLY LOCAL EVIDENCE'
Write-Output "Published version: $publishedVersion"
Write-Output "Published required SQL schema: $publishedSchema"
Write-Output "Published Source SHA: $publishedSourceSha"
Write-Output "Source/main at audit: $sourceMainAtReview (newer than published)"
Write-Output "Executable metadata product version: $versionString"
Write-Output "Executable SHA-256: $actualExeSha256"
Write-Output "Published-binary comparison: $binaryStatus"
Write-Output "BUILD_INFO provenance: $buildInfoStatus"
Write-Output "Running-process path comparison: $runningSelection"
Write-Output "Database schema: $(if ($null -eq $schema) { 'UNAVAILABLE' } else { $schema })"
Write-Output "Database version check: $schemaStatus"
Write-Output "Installed application structural check: $compatibilityStatus"
Write-Output 'LATEST SOURCE NOT PROVEN INSTALLED: this published release predates audit fixes 01-03.'
Write-Output 'COMMUNICATION CENTER UI: MANUAL ACCEPTANCE PENDING (use synthetic recipients; do not send real messages).'
Write-Output 'No files, school database rows, permissions, messages, or update channels were changed.'

$automatedPass = $binaryMatchesPublication -and $schema -eq $publishedSchema -and
    $compatibilityStatus -eq 'PASS: published application structural compatibility' -and
    $runningSelection -ne 'DIFFERENT RUNNING EXECUTABLE OR MULTIPLE PROCESSES'
if (-not $automatedPass) { exit 2 }
exit 0
