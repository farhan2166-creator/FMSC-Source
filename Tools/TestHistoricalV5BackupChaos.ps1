# DEVELOPMENT PC ONLY. Historical schema 1..5 -> disposable SQL database -> migration faults.
# Never uses RESTORE WITH REPLACE; never changes FMSC_DB or the real installer.
[CmdletBinding()]
param(
    [string]$Backup = '',
    [string]$DatabaseFilesRoot = ''
)
$ErrorActionPreference = 'Stop'
function Require([bool]$pass,[string]$message) { if (-not $pass) { throw "CHAOS REFUSED: $message" } }
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
Require ($PSVersionTable.PSVersion.Major -eq 5 -and $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) 'Run in elevated Windows PowerShell 5.1.'
Require (-not (Get-Process FMSC_SMS -ErrorAction SilentlyContinue)) 'Close FMSC first.'
$app = 'C:\FMSC School Management'
$exe = Join-Path $app 'FMSC_SMS.exe'
Require ((Get-Item -LiteralPath $exe).VersionInfo.ProductVersion -match '^1\.0\.11(?:\+|$)') 'Expected installed FMSC 1.0.11.'
$journal = Get-Content 'C:\ProgramData\FMSC_Updater\recovery.json' -Raw | ConvertFrom-Json
Require ($journal.Phase -eq 'Completed' -and $journal.Version -eq '1.0.11') 'Live recovery must be completed.'
# A historical SQL .bak is optional. If the user has detached MDF/LDF instead,
# copy those files and create the initial CHECKSUM backup from a temporary attach.
# Never attach the ORIGINAL files or reuse the live FMSC_DB SQL database name.
$usingFiles = [string]::IsNullOrWhiteSpace($Backup)
$historical = $null
if ($usingFiles) {
    Require (-not [string]::IsNullOrWhiteSpace($DatabaseFilesRoot)) 'Supply -DatabaseFilesRoot pointing to the original detached v5 MDF/LDF folder.'
    $fileRoot = [IO.Path]::GetFullPath($DatabaseFilesRoot).TrimEnd('\')
    Require ($fileRoot -ine 'C:\ProgramData\FMSC_Database' -and
        -not $fileRoot.StartsWith('C:\ProgramData\FMSC_Database\',[StringComparison]::OrdinalIgnoreCase)) 'Live database directory cannot be a chaos-test input.'
    $sourceMdf = Join-Path $fileRoot 'FMSC_DB.mdf'
    $sourceLdf = Join-Path $fileRoot 'FMSC_DB_log.ldf'
    Require ((Test-Path -LiteralPath $sourceMdf -PathType Leaf) -and
        (Test-Path -LiteralPath $sourceLdf -PathType Leaf) -and
        (Get-Item -LiteralPath $sourceMdf).Length -gt 0 -and
        (Get-Item -LiteralPath $sourceLdf).Length -gt 0) 'Expected FMSC_DB.mdf and FMSC_DB_log.ldf in -DatabaseFilesRoot.'
    foreach ($source in @($sourceMdf,$sourceLdf)) {
        for ($item = Get-Item -LiteralPath $source -Force; $null -ne $item; $item = $item.Parent) {
            Require (-not ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) 'Original source database path contains a link.'
        }
    }
} else {
    Require ((Test-Path -LiteralPath $Backup -PathType Leaf) -and
        (Get-Item -LiteralPath $Backup).Length -gt 0) 'Historical backup unavailable.'
    $historical = [IO.Path]::GetFullPath($Backup)
}
$liveHash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
$id = [Guid]::NewGuid().ToString('N')
$target = 'FMSC_Chaos_' + $id
$restored = $target + '_Restored'
$root = Join-Path (Join-Path $env:LOCALAPPDATA 'FMSC\ChaosRuns') ('run-' + $id)
Require (-not (Test-Path -LiteralPath $root)) 'Test workspace already exists.'
$null = New-Item -ItemType Directory -Path $root -Force
$success = $false
$created = $false
$recovered = $false
Add-Type -AssemblyName System.Data
function Sql([string]$Database,[string]$Query,[switch]$Rows) {
    $cn = New-Object System.Data.SqlClient.SqlConnection
    $cn.ConnectionString = "Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=$Database;Integrated Security=True;Encrypt=False;Connect Timeout=25;Application Name=FMSC_Chaos"
    try {
        $cn.Open()
        $cmd = $cn.CreateCommand()
        $cmd.CommandText = $Query
        $cmd.CommandTimeout = 240
        if ($Rows) {
            $table = New-Object System.Data.DataTable
            $adapter = New-Object System.Data.SqlClient.SqlDataAdapter($cmd)
            [void]$adapter.Fill($table)
            return ,$table
        }
        [void]$cmd.ExecuteNonQuery()
    } finally { $cn.Dispose() }
}
function Q([string]$v) { return $v.Replace("'","''") }
function Version([string]$Db) {
    $t = Sql $Db 'SET NOCOUNT ON; SELECT TOP(1) TRY_CONVERT(int,VersionNumber) FROM dbo.DatabaseVersion ORDER BY VersionId DESC;' -Rows
    Require ($t.Rows.Count -eq 1 -and -not $t.Rows[0].IsNull(0)) 'Database schema version not readable.'
    return [int]$t.Rows[0][0]
}
function RestoreFixture([string]$Database,[string]$Source,[string]$Stem) {
    Require ($Database -match '^FMSC_Chaos_[a-f0-9]{32}(_Restored)?$') 'Unsafe disposable SQL name.'
    Require ($Stem.StartsWith($root + '\',[StringComparison]::OrdinalIgnoreCase)) 'Unsafe fixture file path.'
    $quoted = Q $Source
    $list = Sql master ("RESTORE FILELISTONLY FROM DISK=N'" + $quoted + "';") -Rows
    $data = @($list.Rows | Where-Object { [string]$_['Type'] -eq 'D' })
    $log = @($list.Rows | Where-Object { [string]$_['Type'] -eq 'L' })
    Require ($list.Rows.Count -eq 2 -and $data.Count -eq 1 -and $log.Count -eq 1) 'Expected a backup with exactly one MDF and one LDF.'
    $query = "RESTORE DATABASE [$Database] FROM DISK=N'$quoted' WITH MOVE N'" + (Q ([string]$data[0]['LogicalName'])) + "' TO N'" + (Q ($Stem + '.mdf')) + "', MOVE N'" + (Q ([string]$log[0]['LogicalName'])) + "' TO N'" + (Q ($Stem + '_log.ldf')) + "', CHECKSUM, RECOVERY;"
    Sql master $query
}
function AssertFixtureFiles([string]$Db) {
    Require ($Db -match '^FMSC_Chaos_[a-f0-9]{32}(_Restored)?$') 'Unsafe disposable database name.'
    $rows = Sql master "SELECT physical_name FROM sys.master_files WHERE database_id=DB_ID(N'$Db');" -Rows
    Require ($rows.Rows.Count -eq 2) 'Cannot verify disposable database location.'
    foreach ($entry in $rows.Rows) {
        $physical = [IO.Path]::GetFullPath([string]$entry[0])
        Require ($physical.StartsWith($root + '\',[StringComparison]::OrdinalIgnoreCase)) 'Refusing to drop a database outside the fixture root.'
    }
}
function CheckLive {
    Require ((Version 'FMSC_DB') -eq 7) 'Real FMSC_DB changed version.'
    Require ((Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash -eq $liveHash) 'Real installed executable changed.'
}
function Resolve-LocalDbNamedPipe {
    # go-sqlcmd 1.10.0 cannot reliably resolve (localdb)\\MSSQLLocalDB.
    # The production recovery path already passes its explicit named pipe.
    $found = Get-Command 'sqllocaldb.exe' -ErrorAction SilentlyContinue
    $localDbExe = if ($found) { $found.Source } else {
        Join-Path $env:ProgramFiles 'Microsoft SQL Server\160\Tools\Binn\SqlLocalDB.exe'
    }
    Require (Test-Path -LiteralPath $localDbExe -PathType Leaf) 'SQL LocalDB executable was not found.'
    $null = @(& $localDbExe start MSSQLLocalDB 2>&1)
    Require ($LASTEXITCODE -eq 0) 'Cannot start the current user MSSQLLocalDB instance.'
    $info = @(& $localDbExe info MSSQLLocalDB 2>&1)
    Require ($LASTEXITCODE -eq 0) 'Cannot inspect the current user MSSQLLocalDB instance.'
    $prefix = [regex]::Escape('\\.\pipe\LOCALDB#')
    $suffix = [regex]::Escape('\tsql\query')
    $foundPipe = [regex]::Match(($info -join "`n"), '(?:np:)?(' + $prefix + '[A-Za-z0-9]+' + $suffix + ')')
    Require ($foundPipe.Success) 'Cannot discover the SQL LocalDB named-pipe endpoint.'
    return 'np:' + $foundPipe.Groups[1].Value
}
function Apply([string]$Label) {
    Write-Host "RUN: $Label"
    $ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    # Do not return both the child process output AND its exit code. PowerShell
    # otherwise produces an array that can evaluate as true even after failure.
    $lines = @(& $ps -NoProfile -ExecutionPolicy Bypass -File $script:applyScript -DatabaseName $target -SqlInstance $script:pipeInstance -BackupRoot (Join-Path $root 'Backups') -SqlCmdPath $script:sqlcmd -Apply 2>&1)
    $exitCode = [int]$LASTEXITCODE
    foreach ($line in $lines) { Write-Host ([string]$line) }
    Write-Host "MIGRATION RUNNER EXIT CODE: $exitCode"
    return [pscustomobject]@{ ExitCode=$exitCode; Output=($lines -join [Environment]::NewLine) }
}
try {
    $live = Version 'FMSC_DB'
    Require ($live -eq 7) 'Real school database must currently be schema 7.'
    Write-Host 'TEST 1/5: build verified historical recovery backup and restore under unique SQL database names.'
    if ($usingFiles) {
        # Refuse SQL files that are already attached to THIS LocalDB instance.
        # Copying an in-use database's MDF and LDF is not a supported backup.
        $attached = Sql master 'SELECT physical_name FROM sys.master_files;' -Rows
        foreach ($entry in $attached.Rows) {
            $physical = [IO.Path]::GetFullPath([string]$entry[0])
            Require ($physical -ine $sourceMdf -and $physical -ine $sourceLdf) 'Source database is attached to LocalDB. Use SQL BACKUP instead of copying in-use MDF/LDF.'
        }
        $copyMdf = Join-Path $root 'detached-source.mdf'
        $copyLdf = Join-Path $root 'detached-source_log.ldf'
        Copy-Item -LiteralPath $sourceMdf -Destination $copyMdf -ErrorAction Stop
        Copy-Item -LiteralPath $sourceLdf -Destination $copyLdf -ErrorAction Stop
        foreach ($pair in @(@($sourceMdf,$copyMdf),@($sourceLdf,$copyLdf))) {
            Require ((Get-FileHash -LiteralPath $pair[0] -Algorithm SHA256).Hash -eq
                (Get-FileHash -LiteralPath $pair[1] -Algorithm SHA256).Hash) 'Original MDF/LDF changed during copying or copied bytes differ.'
        }
        $sql = "CREATE DATABASE [$target] ON (FILENAME=N'" + (Q $copyMdf) +
            "'), (FILENAME=N'" + (Q $copyLdf) + "') FOR ATTACH;"
        Sql master $sql
        $created = $true
        AssertFixtureFiles $target
        $sourceVersion = Version $target
        Write-Host "Detected ORIGINAL detached database schema: $sourceVersion" -ForegroundColor Cyan
        Require ($sourceVersion -ge 1 -and $sourceVersion -le 6) "Unsupported source schema ${sourceVersion}: requires an independently reviewed migration path (1-6 supported)."
        $historical = Join-Path $root ("verified-v$sourceVersion-from-detached-files.bak")
        Sql master ("BACKUP DATABASE [$target] TO DISK=N'" + (Q $historical) + "' WITH INIT, CHECKSUM;")
        Sql master ("RESTORE VERIFYONLY FROM DISK=N'" + (Q $historical) + "' WITH CHECKSUM;")
        # Drop ONLY the newly attached copy; not the original source files.
        AssertFixtureFiles $target
        Sql master ("ALTER DATABASE [$target] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$target];")
        $created = $false
    } else {
        Sql master ("RESTORE VERIFYONLY FROM DISK=N'" + (Q $historical) + "' WITH CHECKSUM;")
    }
    RestoreFixture $target $historical (Join-Path $root 'source')
    $created = $true
    AssertFixtureFiles $target
    $actualSource = Version $target
    Write-Host "Original database schema detected: $actualSource" -ForegroundColor Cyan
    Require ($actualSource -ge 1 -and $actualSource -le 6) "Unsupported source schema $actualSource. No migrations were executed."
    if ($usingFiles) {
        Require ($sourceVersion -eq $actualSource) 'Backup restored to an unexpected schema.'
    }
    CheckLive
    Write-Host "PASS: historical database verified, independently restored at schema $actualSource." -ForegroundColor Green

    $migrationRoot = Join-Path $root 'Migrations'
    $null = New-Item -ItemType Directory -Path $migrationRoot
    $installedMigrations = Join-Path $app 'UpdateSupport\Migrations'
    $firstMigration = $actualSource + 1
    $nextMigration = $actualSource + 2
    Require ($nextMigration -le 7) 'Need two pending schema versions for the interruption-and-resume test.'
    foreach ($number in ($firstMigration..7)) {
        $source = Join-Path $installedMigrations ($number.ToString() + '.sql')
        Require (Test-Path -LiteralPath $source -PathType Leaf) "Installed migration $number is missing. No migration has been applied."
        Copy-Item -LiteralPath $source -Destination (Join-Path $migrationRoot ($number.ToString() + '.sql'))
    }
    $script:applyScript = Join-Path $migrationRoot 'Apply-Migrations.ps1'
    . (Join-Path $PSScriptRoot 'ResolveFmscGitHubRepository.ps1')
    $sourceRepo=Resolve-FmscGitHubRepository -Repository Source
    Invoke-WebRequest -UseBasicParsing -Uri ("https://raw.githubusercontent.com/" + $sourceRepo.FullName + "/287b16e606f99aa139f6c98cc6021753f7f476a0/Migrations/Apply-Migrations.ps1") -OutFile $script:applyScript
    . (Join-Path $app 'UpdateSupport\UpdateRecovery.ps1')
    . (Join-Path $app 'UpdateSupport\MaintenanceTools.ps1') -Action Library
    $valid = Find-FmscMaintenanceTool 'SqlCmd'
    Require ($null -ne $valid) 'Validated maintenance sqlcmd not available.'
    $script:sqlcmd = Join-Path $valid 'sqlcmd.exe'
    $script:pipeInstance = Resolve-LocalDbNamedPipe
    # Verify the exact transport used by the child migration runner before
    # any schema mutation, so this cannot masquerade as a migration rollback.
    $probe = @(& $script:sqlcmd -S $script:pipeInstance -E -d master -C -b -Q 'SET NOCOUNT ON; SELECT 1;' 2>&1)
    Require ($LASTEXITCODE -eq 0 -and ($probe -join ' ') -match '\b1\b') ("go-sqlcmd LocalDB named-pipe preflight failed: " + ($probe -join ' '))
    Write-Host 'PASS: go-sqlcmd connected to current-user LocalDB through verified named pipe.' -ForegroundColor Green
    $realFirst = Join-Path $installedMigrations ($firstMigration.ToString() + '.sql')
    $realNext = Join-Path $installedMigrations ($nextMigration.ToString() + '.sql')
    $script:applyScript = [IO.Path]::GetFullPath($script:applyScript)

    Write-Host "TEST 2/5: intentionally fail before migration $firstMigration (rollback must preserve schema $actualSource)."
    [IO.File]::WriteAllText((Join-Path $migrationRoot ($firstMigration.ToString() + '.sql')), "THROW 51000,'FMSC deliberate chaos failure before schema $firstMigration',1;", [Text.UTF8Encoding]::new($false))
    $run = Apply "v$actualSource -> injected failure at $firstMigration"
    Require ($run.ExitCode -ne 0 -and $run.Output -match "Migration $firstMigration failed" -and
        (Version $target) -eq $actualSource) "Migration $firstMigration did not fail at the intended SQL boundary; review the printed child output."
    CheckLive
    Write-Host "PASS: failed first migration preserves baseline $actualSource." -ForegroundColor Green

    Write-Host "TEST 3/5: commit migration $firstMigration, then fail before migration $nextMigration."
    Copy-Item -LiteralPath $realFirst -Destination (Join-Path $migrationRoot ($firstMigration.ToString() + '.sql')) -Force
    [IO.File]::WriteAllText((Join-Path $migrationRoot ($nextMigration.ToString() + '.sql')), "THROW 51000,'FMSC deliberate chaos failure before schema $nextMigration',1;", [Text.UTF8Encoding]::new($false))
    $run = Apply "v$actualSource -> schema $firstMigration, then fail $nextMigration"
    $observed = Version $target
    Require ($run.ExitCode -ne 0 -and $run.Output -match "Migration $nextMigration failed" -and
        $observed -eq $firstMigration) "Expected committed intermediate schema $firstMigration missing (actual $observed). Inspect child failure above."
    CheckLive
    Write-Host "PASS: migration $firstMigration committed; failed migration $nextMigration rolled back." -ForegroundColor Green

    Write-Host "TEST 4/5: restore original migration $nextMigration and resume through schema 7."
    Copy-Item -LiteralPath $realNext -Destination (Join-Path $migrationRoot ($nextMigration.ToString() + '.sql')) -Force
    $run = Apply "resume $firstMigration -> 7"
    Require ($run.ExitCode -eq 0 -and (Version $target) -eq 7) 'Forward recovery to schema 7 failed; inspect child failure above.'
    CheckLive
    Write-Host "PASS: original migrations advanced schema $actualSource through 7." -ForegroundColor Green

    Write-Host 'TEST 5/5: restore verified pre-migration recovery backup to another isolated database.'
    $backupDir = Join-Path $root 'Backups'
    $generated = @(Get-ChildItem -LiteralPath $backupDir -Filter 'FMSC_DB_BeforeMigration*.bak' -File | Sort-Object CreationTimeUtc)
    Require ($generated.Count -gt 0) 'Migration runner created no recovery backups.'
    $first = $generated[0].FullName
    Sql master ("RESTORE VERIFYONLY FROM DISK=N'" + (Q $first) + "' WITH CHECKSUM;")
    RestoreFixture $restored $first (Join-Path $root 'recovered')
    $recovered = $true
    AssertFixtureFiles $restored
    Require ((Version $restored) -eq $actualSource -and (Version $target) -eq 7) "Recovered backup did not reproduce schema $actualSource while target stays v7."
    CheckLive
    Write-Host "PASS: backup restored original schema $actualSource; migrated target remains v7." -ForegroundColor Green
    $success = $true
} finally {
    foreach ($entry in @(@($restored,$recovered),@($target,$created))) {
        if ($entry[1]) {
            AssertFixtureFiles $entry[0]
            Sql master ("ALTER DATABASE [" + $entry[0] + "] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [" + $entry[0] + "];")
        }
    }
    if ($success) {
        Remove-Item -LiteralPath $root -Recurse -Force
        Write-Host 'PASS: disposable DB copies and test files cleaned. Real FMSC and FMSC_DB unchanged.' -ForegroundColor Green
    } else {
        Write-Warning "Test failed. Disposable SQL drop attempted; temporary files retained at $root for investigation."
    }
}
