<#
.SYNOPSIS
    Apply FMSC numbered SQL migrations without running the application installer.
.DESCRIPTION
    Mirrors Installer/core.ps1 migration safety: pending numeric files only,
    verified CHECKSUM backup, one sqlcmd session/transaction per migration,
    sp_getapplock, version checks, and stop-on-error.
    Run under the Windows account that owns the intended LocalDB instance.
.EXAMPLE
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Apply-Migrations.ps1
.EXAMPLE
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Apply-Migrations.ps1 -PlanOnly
.EXAMPLE
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Apply-Migrations.ps1 -Apply
#>
[CmdletBinding()]
param(
    [string]$SqlInstance = '(localdb)\MSSQLLocalDB',
    [string]$DatabaseName = 'FMSC_DB',
    [string]$SqlCmdPath,
    [string]$BackupRoot = 'C:\ProgramData\FMSC_Database\Backups',
    [switch]$PlanOnly,
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
$script:LogPath = Join-Path $BackupRoot 'ApplyMigrations.log'
$script:ResolvedSqlCmd = $null

function Write-Status {
    param([string]$Message)
    Write-Host $Message
    if (Test-Path -LiteralPath $BackupRoot -PathType Container) {
        try {
            Add-Content -LiteralPath $script:LogPath -Value ("[{0}] {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message) -ErrorAction Stop
        } catch {
            Write-Host "Warning: could not write migration log: $($_.Exception.Message)" -ForegroundColor Yellow
        }
    }
}

function Get-RequiredVersion {
    param([object[]]$Migrations)
    # Installer/core.ps1 is the only version authority in a source checkout.
    # In an extracted release, core.ps1 lives directly beside Migrations.
    $root = Split-Path -Parent $PSScriptRoot
    $sources = @(
        @{ Path = (Join-Path $root 'Installer\core.ps1'); Pattern = '\$DatabaseVersion\s*=\s*''([0-9]+)'''; Label = 'installer' },
        @{ Path = (Join-Path $root 'core.ps1'); Pattern = '\$DatabaseVersion\s*=\s*''([0-9]+)'''; Label = 'release installer' }
    )
    $found = @()
    foreach ($item in $sources) {
        if (-not (Test-Path -LiteralPath $item.Path -PathType Leaf)) { continue }
        $source = Get-Content -LiteralPath $item.Path -Raw
        $versionMatch = [regex]::Match($source, $item.Pattern)
        if (-not $versionMatch.Success) {
            throw "Could not determine required schema version from $($item.Path). Refusing to guess."
        }
        $found += [pscustomobject]@{ Label = $item.Label; Version = [int]$versionMatch.Groups[1].Value }
    }
    if ($found.Count -gt 0) {
        $different = @($found | Where-Object { $_.Version -ne $found[0].Version })
        if ($different.Count -gt 0) {
            throw "Multiple installer core.ps1 schema declarations disagree. No migrations were run."
        }
        return [int]$found[0].Version
    }
    # A stand-alone copy of Migrations has no installer metadata; its highest
    # numbered SQL file is the only available release target.
    if ($Migrations.Count -eq 0) { throw 'No numbered SQL files or release schema version were found.' }
    $latest = ($Migrations | Measure-Object -Property Version -Maximum).Maximum
    Write-Host "Warning: application/installer metadata not found. Using highest migration ($latest) as the target; confirm it matches your app." -ForegroundColor Yellow
    return [int]$latest
}

function Find-SqlCmd {
    if (-not [string]::IsNullOrWhiteSpace($SqlCmdPath)) {
        if (-not (Test-Path -LiteralPath $SqlCmdPath -PathType Leaf)) {
            throw "sqlcmd.exe was not found at $SqlCmdPath"
        }
        return (Resolve-Path -LiteralPath $SqlCmdPath).Path
    }
    $command = Get-Command sqlcmd.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $root = Split-Path -Parent $PSScriptRoot
    foreach ($path in @(
        (Join-Path $root 'Tools\sqlcmd.exe'),
        (Join-Path $root 'Installer\Tools\sqlcmd.exe')
    )) {
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            return (Resolve-Path -LiteralPath $path).Path
        }
    }

    # Same pinned Microsoft go-sqlcmd release and SHA-256 values as the installer.
    $architecture = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'amd64' }
    $sha256 = if ($architecture -eq 'arm64') {
        'AC6F257DCE55E7F34D618EA9AF736D9077854CADB6AF6FDB0259C90D42E0B8D2'
    } else {
        'A4C28332FCC6E497D655E53AC8F4939F4AB170F9CFD32D0FA5081B60F4D9D691'
    }
    $cache = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'FMSC\MigrationTools'
    $cached = Join-Path $cache 'sqlcmd.exe'
    if (Test-Path -LiteralPath $cached -PathType Leaf) { return $cached }
    New-Item -ItemType Directory -Path $cache -Force | Out-Null
    $archiveName = "sqlcmd-windows-$architecture.zip"
    $archive = Join-Path $cache $archiveName
    $url = "https://github.com/microsoft/go-sqlcmd/releases/download/v1.10.0/$archiveName"
    Write-Host 'sqlcmd was not found. Downloading the installer-pinned Microsoft sqlcmd...'
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $oldProgress = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'
    try {
        Invoke-WebRequest -Uri $url -OutFile $archive -UseBasicParsing
    } finally {
        $ProgressPreference = $oldProgress
    }
    try {
        if (-not (Test-Path -LiteralPath $archive -PathType Leaf) -or (Get-Item -LiteralPath $archive).Length -eq 0) {
            throw 'sqlcmd download failed or was empty.'
        }
        $actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
        if ($actual -ne $sha256) { throw 'Downloaded sqlcmd failed the installer-pinned SHA-256 integrity check.' }
        Expand-Archive -LiteralPath $archive -DestinationPath $cache -Force
        if (-not (Test-Path -LiteralPath $cached -PathType Leaf)) {
            $found = Get-ChildItem -LiteralPath $cache -Filter sqlcmd.exe -Recurse -File | Select-Object -First 1
            if (-not $found) { throw 'Downloaded archive did not contain sqlcmd.exe.' }
            Copy-Item -LiteralPath $found.FullName -Destination $cached -Force
        }
    } finally {
        Remove-Item -LiteralPath $archive -Force -ErrorAction SilentlyContinue
    }
    return $cached
}

function Invoke-Sql {
    param([string]$Database, [string]$Query)
    $output = & $script:ResolvedSqlCmd -S $SqlInstance -E -d $Database -b -Q $Query 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw ("SQL command failed against {0}: {1}" -f $Database, ($output -join [Environment]::NewLine))
    }
    return $output
}

function Get-DatabaseVersion {
    $query = 'SET NOCOUNT ON; SELECT TOP 1 VersionNumber FROM dbo.DatabaseVersion ORDER BY VersionId DESC;'
    $output = Invoke-Sql -Database $DatabaseName -Query $query
    $version = $null
    foreach ($line in $output) {
        $text = $line.ToString().Trim()
        if ($text -match '^[0-9]+$') { $version = $text }
    }
    if ($null -eq $version) {
        throw 'No valid latest version in dbo.DatabaseVersion. Refusing to migrate.'
    }
    return [int]$version
}

function New-VerifiedBackup {
    param([int]$FromVersion, [int]$ToVersion)
    New-Item -ItemType Directory -Path $BackupRoot -Force | Out-Null
    $base = "FMSC_DB_BeforeMigration_v{0}_to_v{1}_{2}" -f $FromVersion, $ToVersion, (Get-Date -Format 'yyyyMMdd_HHmmss')
    $file = Join-Path $BackupRoot ($base + '.bak')
    $suffix = 1
    while (Test-Path -LiteralPath $file) {
        $file = Join-Path $BackupRoot ("{0}_{1}.bak" -f $base, $suffix)
        $suffix++
    }
    $escapedName = $DatabaseName.Replace(']', ']]')
    $escapedPath = $file.Replace("'", "''")
    Write-Status "Creating recovery backup: $file"
    try {
        Invoke-Sql -Database 'master' -Query "BACKUP DATABASE [$escapedName] TO DISK = N'$escapedPath' WITH INIT, CHECKSUM, STATS = 10" | Out-Null
        Invoke-Sql -Database 'master' -Query "RESTORE VERIFYONLY FROM DISK = N'$escapedPath' WITH CHECKSUM" | Out-Null
    } catch {
        throw "Backup or RESTORE VERIFYONLY failed. No migration was run. $($_.Exception.Message)"
    }
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        throw "SQL reported backup success, but the backup file is missing. No migration was run: $file"
    }
    $size = (Get-Item -LiteralPath $file).Length
    if ($size -le 0) { throw "Backup file is empty. No migration was run: $file" }
    $record = Join-Path $BackupRoot 'LastPreMigrationBackup.txt'
    @(
        "Backup file       : $file",
        ("Backup size (MB)  : {0}" -f [math]::Round($size / 1MB, 2)),
        ("Created (UTC)     : {0}" -f (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss')),
        "Database          : $DatabaseName",
        "SQL instance      : $SqlInstance",
        "Schema version    : $FromVersion -> $ToVersion",
        "Applied by        : standalone migration applier",
        'Verified with     : RESTORE VERIFYONLY WITH CHECKSUM'
    ) | Set-Content -LiteralPath $record -Encoding UTF8
    Write-Status "Recovery backup verified: $file"
    return $file
}

function Invoke-OneMigration {
    param([string]$FilePath, [int]$VersionBefore, [int]$ExpectedVersion)
    $fullPath = [IO.Path]::GetFullPath($FilePath)
    $migrationSql = [IO.File]::ReadAllText($fullPath, [Text.Encoding]::UTF8)
    if ($fullPath.Contains('"') -or $fullPath.Contains([char]10) -or $fullPath.Contains([char]13)) {
        throw 'Migration file path contains characters unsafe for sqlcmd include syntax.'
    }
    $wrapperPath = Join-Path ([IO.Path]::GetTempPath()) ('FMSC_migration_' + [Guid]::NewGuid().ToString('N') + '.sql')
    # Inline UTF-8 SQL preserves GO batches without sqlcmd-specific include quoting.
    # Closing the connection on any sqlcmd error rolls back the outer transaction.
    $wrapper = @"
:on error exit
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;
SET NUMERIC_ROUNDABORT OFF;
DECLARE @Lock int;
EXEC @Lock=sys.sp_getapplock @Resource=N'FMSC_SchemaMigration',@LockMode=N'Exclusive',@LockOwner=N'Session',@LockTimeout=0;
IF @Lock<0 THROW 51000,'Another database migration is running.',1;
DECLARE @Before int=(SELECT TOP(1) TRY_CONVERT(int,VersionNumber) FROM dbo.DatabaseVersion ORDER BY VersionId DESC);
IF @Before IS NULL OR @Before<>$VersionBefore THROW 51000,'Database version changed since migration planning; rerun the applier.',1;
BEGIN TRANSACTION;
GO
$migrationSql
GO
IF @@TRANCOUNT<>1 THROW 51000,'Migration did not preserve the installer transaction.',1;
DECLARE @After int=(SELECT TOP(1) TRY_CONVERT(int,VersionNumber) FROM dbo.DatabaseVersion ORDER BY VersionId DESC);
IF @After IS NULL OR @After<>$ExpectedVersion THROW 51000,'Migration did not record its expected database version.',1;
COMMIT TRANSACTION;
"@
    try {
        [IO.File]::WriteAllText($wrapperPath, $wrapper, [Text.UTF8Encoding]::new($false))
        # Pinned Microsoft go-sqlcmd reads UTF-8 input natively; it does not
        # support the ODBC sqlcmd-only -f code-page switch.
        $output = & $script:ResolvedSqlCmd -S $SqlInstance -E -d $DatabaseName -C -b -i $wrapperPath 2>&1
        foreach ($line in $output) { Write-Status $line.ToString() }
        if ($LASTEXITCODE -ne 0) {
            throw "Migration $ExpectedVersion failed. Keep the verified recovery backup and inspect the database before any retry."
        }
    } finally {
        if (Test-Path -LiteralPath $wrapperPath) { Remove-Item -LiteralPath $wrapperPath -Force }
    }
}

function Invoke-MigrationApplier {
    if ($PlanOnly -and $Apply) { throw 'Use either -PlanOnly or -Apply, not both.' }
    if ([string]::IsNullOrWhiteSpace($SqlInstance) -or [string]::IsNullOrWhiteSpace($DatabaseName)) {
        throw 'SQL instance and database name are required.'
    }
    $files = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.sql' -File)
    $versions = @{}
    $migrations = @(foreach ($file in $files) {
        if ($file.BaseName -notmatch '^[1-9][0-9]*$') {
            throw "Invalid migration filename $($file.Name). Expected a positive integer such as 69.sql. No migrations were run."
        }
        $version = 0
        if (-not [int]::TryParse($file.BaseName, [ref]$version)) { throw "Migration number is too large: $($file.Name)" }
        if ($versions.ContainsKey($version)) { throw "Duplicate migration version: $version. No migrations were run." }
        $versions[$version] = $true
        [pscustomobject]@{ Version = $version; FilePath = $file.FullName }
    })
    $required = Get-RequiredVersion -Migrations $migrations
    $script:ResolvedSqlCmd = Find-SqlCmd
    $current = Get-DatabaseVersion
    $pending = @($migrations | Where-Object { $_.Version -gt $current } | Sort-Object Version)

    Write-Host ''
    Write-Host 'FMSC DATABASE MIGRATION APPLIER' -ForegroundColor Cyan
    Write-Host "SQL instance        : $SqlInstance"
    Write-Host "Database            : $DatabaseName"
    Write-Host "Windows account     : $([Security.Principal.WindowsIdentity]::GetCurrent().Name)"
    Write-Host "Current schema      : $current"
    Write-Host "Required schema     : $required"
    Write-Host "Pending migrations  : $(($pending | ForEach-Object { $_.Version }) -join ', ')"
    Write-Host "Verified backup dir : $BackupRoot"
    Write-Host ''

    if ($current -gt $required -or @($pending | Where-Object { $_.Version -gt $required }).Count -gt 0) {
        throw "Database or pending SQL is newer than supported schema $required. No migrations were run."
    }
    if ($pending.Count -eq 0) {
        if ($current -ne $required) {
            throw "No pending migration files can upgrade schema $current to required schema $required."
        }
        Write-Host 'Database already has the required schema. Nothing to apply.' -ForegroundColor Green
        return
    }
    if ($pending[-1].Version -ne $required) {
        throw "Available migrations stop at $($pending[-1].Version), but the release requires $required. No migrations were run."
    }
    # Never skip a version in the required migration chain. The earliest
    # available script must upgrade the database's exact current version.
    # Detect missing files before prompting, creating a backup, or changing SQL.
    $expected = $current + 1
    foreach ($migration in $pending) {
        if ($migration.Version -ne $expected) {
            throw "Missing required migration $expected.sql (database schema $current, target $required). No migrations were run."
        }
        $expected++
    }

    if ($PlanOnly) {
        Write-Host 'Plan only: no backup or migrations executed.' -ForegroundColor Yellow
        return
    }
    if (-not $Apply) {
        Write-Host 'Close all FMSC sessions and keep a separate recoverable backup. Test on a disposable database first.' -ForegroundColor Yellow
        $confirmation = Read-Host 'Type APPLY to create the verified backup and execute the listed migrations'
        if ($confirmation -cne 'APPLY') {
            Write-Host 'Cancelled. No backup or migrations executed.'
            return
        }
    }

    $backup = New-VerifiedBackup -FromVersion $current -ToVersion $pending[-1].Version
    Write-Status "Migration run started. Verified recovery point: $backup"
    foreach ($migration in $pending) {
        Write-Status "Applying $($migration.Version).sql (from schema $current)..."
        Invoke-OneMigration -FilePath $migration.FilePath -VersionBefore $current -ExpectedVersion $migration.Version
        $actual = Get-DatabaseVersion
        if ($actual -ne $migration.Version) {
            throw "Migration $($migration.Version) did not leave its expected database version. Stop and inspect the backup and database."
        }
        Write-Status "Migration $actual completed."
        $current = $actual
    }
    if ($current -ne $required) { throw "Final schema is $current instead of $required." }
    Write-Status "SUCCESS: database schema is $current. Recovery backup: $backup"
    Write-Host 'Migration application is complete. This tool does not install or activate the FMSC application.' -ForegroundColor Green
}

try {
    Invoke-MigrationApplier
    exit 0
} catch {
    Write-Host ''
    Write-Host "FAILED: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host 'Do not blindly rerun migrations or replace MDF/LDF files. Preserve the backup and review the failure.'
    exit 1
}
