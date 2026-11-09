# Real SQL acceptance, restricted to an approved disposable hosted Windows job.
param([string]$ChildRoot = '', [string]$ChildDatabase = '', [string]$Checkpoint = '')
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_OS -ne 'Windows' -or
    $env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or $env:FMSC_DISPOSABLE_INSTALL -ne 'approved-synthetic-only') {
    throw 'Real recovery SQL tests require the approved synthetic-only hosted Windows runner.'
}
$project = Split-Path $PSScriptRoot -Parent
. (Join-Path $project 'Installer/UpdateRecovery.ps1')
. (Join-Path $project 'Installer/MaintenanceTools.ps1')
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseInput((Get-Content (Join-Path $project 'Installer/core.ps1') -Raw),[ref]$tokens,[ref]$errors)
if ($errors.Count) { throw 'Installer does not parse.' }
foreach ($name in @('Convert-ToDbVersion','Invoke-SqlMigration','Invoke-PendingMigrations','New-PreMigrationBackup',
    'Resolve-SqlCmd','Invoke-FmscRestoreMigrationBaseline','Invoke-FmscRollbackFailedUpdate')) {
    $definition=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name},$true)
    Invoke-Expression $definition.Extent.Text
}
$runner=${function:Invoke-SqlMigration}
function Write-Step { param($Message) }
function Write-Log { param($Message) }
function Write-Success { param($Message) }
function Pause-Long { }
$root = if ($ChildRoot) { [IO.Path]::GetFullPath($ChildRoot) } else { Join-Path $env:RUNNER_TEMP ('fmsc-recovery-sql-' + [Guid]::NewGuid().ToString('N')) }
if (-not $root.StartsWith([IO.Path]::GetFullPath($env:RUNNER_TEMP),[StringComparison]::OrdinalIgnoreCase) -or
    (Split-Path -Leaf $root) -notmatch '^fmsc-recovery-sql-[a-f0-9]{32}$') { throw 'Invalid synthetic recovery workspace.' }
$DatabaseName = if ($ChildDatabase) { $ChildDatabase } else { 'FMSC_Recovery_' + [Guid]::NewGuid().ToString('N') }
if ($DatabaseName -notmatch '^FMSC_Recovery_[a-f0-9]{32}$') { throw 'Non-fixture SQL target refused.' }
$restoreName = $DatabaseName + '_Restored'
$SqlInstance = '(localdb)\MSSQLLocalDB'
$localDb = Get-Command sqllocaldb.exe -ErrorAction SilentlyContinue
$localDbPath = if ($localDb) { $localDb.Source } else { Join-Path $env:ProgramFiles 'Microsoft SQL Server/160/Tools/Binn/SqlLocalDB.exe' }
$null = & $localDbPath info MSSQLLocalDB 2>&1
if ($LASTEXITCODE -ne 0) {
    $null = & $localDbPath create MSSQLLocalDB 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'Cannot create the disposable runner LocalDB instance.' }
}
$null = & $localDbPath start MSSQLLocalDB 2>&1
if ($LASTEXITCODE -ne 0) { throw 'Cannot start the disposable runner LocalDB engine.' }
$info = @(& $localDbPath info MSSQLLocalDB 2>&1)
$endpoint = [regex]::Match(($info -join "`n"), '(?:np:)?(' + [regex]::Escape('\\.\pipe\LOCALDB#') + '[A-Za-z0-9]+' + [regex]::Escape('\tsql\query') + ')')
if (-not $endpoint.Success) { throw 'Cannot resolve the disposable runner LocalDB endpoint.' }
$SqlInstance = 'np:' + $endpoint.Groups[1].Value
$DatabaseDir = $root; $ReleaseRoot = $root
$AppSource = Join-Path $root 'application'
$AppVersion = '1.0.0'
$ColorDim = 'DarkGray'; $ColorInfo = 'White'; $ColorSuccess = 'Green'
$script:RecoveryRoot = Join-Path $root 'protected'
$null = New-Item -ItemType Directory -Path $root -Force
$script:SqlCmdPath = Resolve-SqlCmd
function Sql([string]$Database,[string]$Query) {
    $result = @(& $script:SqlCmdPath -S $SqlInstance -E -d $Database -C -b -l 30 -t 90 -h -1 -W -Q $Query 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "Synthetic SQL failed: $($result -join ' ')" }
    return $result
}
function Invoke-SqlMaster { param($Query) Sql 'master' $Query }
function Invoke-SqlDatabase { param($Query) Sql $DatabaseName $Query }
function Get-DatabaseVersion { return [int]((Sql $DatabaseName 'SET NOCOUNT ON; SELECT TOP(1) VersionNumber FROM dbo.DatabaseVersion ORDER BY VersionId DESC;') -join '').Trim() }
if ($ChildRoot) {
    $script:RecoveryJournal = Read-FmscRecoveryJournal $script:RecoveryRoot
    function Invoke-SqlMigration {
        param($FilePath,$VersionBefore,$ExpectedVersion)
        & $runner @PSBoundParameters
        if ($Checkpoint -eq 'AfterFirstCommit' -and $ExpectedVersion -eq 2) {
            Set-Content (Join-Path $root 'checkpoint') 'committed'
            while ($true) { Start-Sleep -Milliseconds 200 }
        }
    }
    Invoke-PendingMigrations (Join-Path $root 'migrations') 3
    throw 'Expected SQL interruption was not reached.'
}
$created=$false; $restored=$false
try {
    $null=Sql 'master' "CREATE DATABASE [$DatabaseName]; ALTER DATABASE [$DatabaseName] SET AUTO_CLOSE ON;"
    $created=$true
    $null=Sql $DatabaseName @"
CREATE TABLE dbo.DatabaseVersion(VersionId int IDENTITY PRIMARY KEY,VersionNumber nvarchar(20));
INSERT dbo.DatabaseVersion VALUES(N'1');
CREATE TABLE dbo.MigrationProbe(VersionNumber int PRIMARY KEY);
CREATE TABLE dbo.MigrationControl(Fail bit NOT NULL,Pause bit NOT NULL);
INSERT dbo.MigrationControl VALUES(1,0);
EXEC sys.sp_addextendedproperty @name=N'FMSC_RecoveryFixture',@value=N'$DatabaseName';
"@
    Initialize-FmscRecoveryStorage $script:RecoveryRoot
    $script:RecoveryJournal=[pscustomobject]@{
        Format='fmsc-recovery/v1';Transaction=[Guid]::NewGuid().ToString('N');Phase='Prepared';Version='1.0.0';Schema=3
        UserSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value;DownloadRoot=(Join-Path (Get-FmscDownloadParent) ('prepared-' + [Guid]::NewGuid().ToString('N')))
        Backup='';BackupSha256='';BeforeSchema=0;LastSchema=0;DatabaseIdentity='';ScriptSha256=('a'*64);HelperSha256=('b'*64)
    }
    try { Assert-FmscRecoveryDatabaseIdentity }
    catch {
        $diagnostic=Sql $DatabaseName 'SET NOCOUNT ON; SELECT DB_NAME(),CONVERT(nvarchar(36),database_guid) FROM sys.database_recovery_status WHERE database_id=DB_ID();'
        Write-Output ('Synthetic identity diagnostic: ' + ($diagnostic -join ' | '))
        throw
    }
    $identity=$script:RecoveryJournal.DatabaseIdentity
    $migrations=Join-Path $root 'migrations';$null=New-Item -ItemType Directory $migrations
    Set-Content (Join-Path $migrations '2.sql') "INSERT dbo.MigrationProbe VALUES(2); INSERT dbo.DatabaseVersion VALUES(N'2');"
    Set-Content (Join-Path $migrations '3.sql') "INSERT dbo.MigrationProbe VALUES(3); IF EXISTS(SELECT 1 FROM dbo.MigrationControl WHERE Pause=1) WAITFOR DELAY '00:05:00'; IF EXISTS(SELECT 1 FROM dbo.MigrationControl WHERE Fail=1) THROW 51000,'Deliberate disposable migration failure',1; INSERT dbo.DatabaseVersion VALUES(N'3');"
    $psi=[Diagnostics.ProcessStartInfo]::new('powershell.exe')
    $psi.UseShellExecute=$false;$psi.CreateNoWindow=$true;$psi.RedirectStandardOutput=$true;$psi.RedirectStandardError=$true
    $psi.Arguments='-NoProfile -ExecutionPolicy Bypass -File "'+$PSCommandPath+'" -ChildRoot "'+$root+'" -ChildDatabase '+$DatabaseName+' -Checkpoint AfterFirstCommit'
    $child=[Diagnostics.Process]::Start($psi)
    $out=$child.StandardOutput.ReadToEndAsync();$err=$child.StandardError.ReadToEndAsync()
    try {
        $watch=[Diagnostics.Stopwatch]::StartNew()
        while (-not (Test-Path (Join-Path $root 'checkpoint'))) {
            if ($child.HasExited) { throw "SQL child failed: $($err.GetAwaiter().GetResult()) $($out.GetAwaiter().GetResult())" }
            if ($watch.Elapsed.TotalSeconds -gt 90) { throw 'SQL recovery checkpoint timed out.' }
            Start-Sleep -Milliseconds 100
        }
        $child.Kill($true);$child.WaitForExit()
    } finally { if (-not $child.HasExited) { $child.Kill($true);$child.WaitForExit() }; $child.Dispose() }
    $script:RecoveryJournal=Read-FmscRecoveryJournal $script:RecoveryRoot
    if ((Get-DatabaseVersion) -ne 2 -or $script:RecoveryJournal.LastSchema -ne 0) { throw 'Commit-before-journal interruption was not reproduced.' }
    $originalBackup=$script:RecoveryJournal.Backup
    $originalHash=$script:RecoveryJournal.BackupSha256
    # Kill the actual SQL client while migration 3 has an open transaction with
    # an uncommitted inserted row. READ UNCOMMITTED is used only to locate this
    # disposable checkpoint; all recovery/version checks retain normal isolation.
    $null=Sql $DatabaseName 'UPDATE dbo.MigrationControl SET Pause=1;'
    $psi.Arguments='-NoProfile -ExecutionPolicy Bypass -File "'+$PSCommandPath+'" -ChildRoot "'+$root+'" -ChildDatabase '+$DatabaseName+' -Checkpoint DuringTransaction'
    $child=[Diagnostics.Process]::Start($psi)
    $out=$child.StandardOutput.ReadToEndAsync();$err=$child.StandardError.ReadToEndAsync()
    try {
        $watch=[Diagnostics.Stopwatch]::StartNew()
        while (((Sql $DatabaseName 'SET NOCOUNT ON; SELECT COUNT(*) FROM dbo.MigrationProbe WITH(NOLOCK) WHERE VersionNumber=3;') -join '').Trim() -ne '1') {
            if ($child.HasExited) { throw "In-flight SQL child failed: $($err.GetAwaiter().GetResult()) $($out.GetAwaiter().GetResult())" }
            if ($watch.Elapsed.TotalSeconds -gt 90) { throw 'In-flight migration checkpoint timed out.' }
            Start-Sleep -Milliseconds 100
        }
        $child.Kill($true);$child.WaitForExit()
    } finally { if (-not $child.HasExited) { $child.Kill($true);$child.WaitForExit() }; $child.Dispose() }
    if ((Get-DatabaseVersion) -ne 2 -or
        ((Sql $DatabaseName 'SET NOCOUNT ON; SELECT COUNT(*) FROM dbo.MigrationProbe WHERE VersionNumber=3;') -join '').Trim() -ne '0') {
        throw 'Killing an in-flight migration failed to roll back uncommitted data.'
    }
    $null=Sql $DatabaseName 'UPDATE dbo.MigrationControl SET Pause=0;'
    $failed=$false
    try { Invoke-PendingMigrations $migrations 3 } catch { $failed=$true }
    if (-not $failed -or (Get-DatabaseVersion) -ne 2 -or
        ((Sql $DatabaseName 'SET NOCOUNT ON; SELECT COUNT(*) FROM dbo.MigrationProbe WHERE VersionNumber=3;') -join '').Trim() -ne '0') {
        throw 'Failed migration did not roll back its data and schema marker together.'
    }
    $null=Sql $DatabaseName 'UPDATE dbo.MigrationControl SET Fail=0;'
    Invoke-PendingMigrations $migrations 3
    Assert-FmscRecoveryDatabaseIdentity
    if ((Get-DatabaseVersion) -ne 3 -or $script:RecoveryJournal.DatabaseIdentity -ne $identity -or
        $script:RecoveryJournal.Backup -ne $originalBackup -or
        (Get-FileHash $originalBackup -Algorithm SHA256).Hash -ne $originalHash -or
        @(Get-ChildItem (Join-Path $root 'Backups') -Filter '*.bak').Count -ne 1) {
        throw 'Recovery changed identity, replaced the original backup, or failed to resume to the target.'
    }
    # Restore only to a SECOND newly named fixture database; never overwrite the
    # migration target, a school database, or any supplied MDF/LDF pair.
    $bak=$originalBackup.Replace("'","''"); $data=(Join-Path $root 'restored.mdf').Replace("'","''");$log=(Join-Path $root 'restored_log.ldf').Replace("'","''")
    $null=Sql 'master' "RESTORE DATABASE [$restoreName] FROM DISK=N'$bak' WITH MOVE N'$DatabaseName' TO N'$data', MOVE N'${DatabaseName}_log' TO N'$log', CHECKSUM;"
    $restored=$true
    if (((Sql $restoreName 'SET NOCOUNT ON; SELECT TOP(1) VersionNumber FROM dbo.DatabaseVersion ORDER BY VersionId DESC;') -join '').Trim() -ne '1' -or
        (Get-DatabaseVersion) -ne 3) { throw 'Recovery backup restore did not preserve the original schema-1 state independently of the recovered target.' }
    # Now exercise a REAL restore to the synthetic database after multiple SQL
    # migrations committed, and verify the matching original application is
    # restored. Never target FMSC_DB or another database on the runner.
    if ($DatabaseName -notmatch '^FMSC_Recovery_[a-f0-9]{32}$' -or
        $script:RecoveryJournal.Backup -ne $originalBackup) {
        throw 'Unsafe synthetic rollback target.'
    }
    $AppDir=Join-Path $root 'synthetic-installed'
    $AppRollbackDir="$AppDir.rollback"
    $AppStagingDir="$AppDir.staging"
    $null=New-Item -ItemType Directory -Path $AppDir,$AppRollbackDir,$AppStagingDir -Force
    Set-Content (Join-Path $AppDir 'FMSC_SMS.exe') 'synthetic incoming application'
    Set-Content (Join-Path $AppRollbackDir 'FMSC_SMS.exe') 'synthetic original application'
    Set-Content (Join-Path $AppStagingDir 'FMSC_SMS.exe') 'synthetic staged files'
    $originalExeHash=(Get-FileHash (Join-Path $AppRollbackDir 'FMSC_SMS.exe') -Algorithm SHA256).Hash
    $fileRows=@(Sql master "SET NOCOUNT ON; SELECT physical_name FROM sys.master_files WHERE database_id=DB_ID(N'$DatabaseName') ORDER BY type;")
    if ($fileRows.Count -ne 2) { throw 'Synthetic database file identity is incomplete.' }
    $LiveMdf=[string]$fileRows[0];$LiveLdf=[string]$fileRows[1]
    $script:RecoveryJournal.Phase='Activating'
    $script:RecoveryJournal.LastSchema=3
    $script:RecoveryJournal | Add-Member -NotePropertyName BeforeAppSha256 -NotePropertyValue $originalExeHash -Force
    $script:RecoveryJournal | Add-Member -NotePropertyName RollbackRequested -NotePropertyValue $false -Force
    Save-FmscRecoveryJournal $script:RecoveryRoot $script:RecoveryJournal
    function Initialize-FmscRecoveryConnection { }
    Invoke-FmscRollbackFailedUpdate
    $rollbackRecord=Read-FmscRecoveryJournal $script:RecoveryRoot
    if ($rollbackRecord.Phase -ne 'Completed' -or $rollbackRecord.Outcome -ne 'Aborted' -or
        (Get-DatabaseVersion) -ne 1 -or
        ((Sql $DatabaseName 'SET NOCOUNT ON; SELECT COUNT(*) FROM dbo.MigrationProbe;') -join '').Trim() -ne '0' -or
        (Get-FileHash (Join-Path $AppDir 'FMSC_SMS.exe') -Algorithm SHA256).Hash -ne $originalExeHash -or
        (Test-Path $AppRollbackDir) -or (Test-Path $AppStagingDir) -or
        $rollbackRecord.DatabaseIdentity -ne $identity -or
        (Get-FileHash $originalBackup -Algorithm SHA256).Hash -ne $originalHash) {
        throw 'Real synthetic SQL/app rollback did not restore the original verified state.'
    }
    $failedCopies=@(Get-ChildItem (Join-Path $root 'Backups') -Filter 'FMSC_DB_FailedUpdateBeforeRollback_*.bak' -File)
    if ($failedCopies.Count -ne 1) { throw 'Partial-state database safety copy was not retained.' }
    Write-Output 'PASS: real SQL committed migration interruption, failed SQL transaction, forward recovery and complete precommit rollback to schema 1 plus exact original executable on a disposable database; original and partial-state backups retained. No live FMSC_DB touched.'
} finally {
    foreach ($item in @(@($DatabaseName,$created),@($restoreName,$restored))) {
        if ($item[1]) {
            $name=$item[0]
            if ($name -notmatch '^FMSC_Recovery_[a-f0-9]{32}(?:_Restored)?$') { throw 'Unsafe SQL cleanup name.' }
            $marker=((Sql $name "SET NOCOUNT ON; SELECT CONVERT(nvarchar(128),value) FROM sys.extended_properties WHERE class=0 AND name=N'FMSC_RecoveryFixture';") -join '').Trim()
            if ($marker -ne $DatabaseName) { throw 'Synthetic SQL ownership changed. Cleanup refused.' }
            $null=Sql 'master' "ALTER DATABASE [$name] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$name];"
        }
    }
    if (-not $root.StartsWith([IO.Path]::GetFullPath($env:RUNNER_TEMP),[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe workspace cleanup.' }
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
