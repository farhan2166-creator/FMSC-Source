param([string]$ChildRoot = '', [string]$Checkpoint = '')
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$helper = Get-Content (Join-Path $project 'Installer/UpdateRecovery.ps1') -Raw
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
$ownerSid = 'S-1-5-32-544'
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    # A restricted local test token cannot assign Administrators as owner. Keep
    # the same DACL/ACL logic with a fixture owner; hosted elevated acceptance
    # runs the exact production Administrators/SYSTEM ownership unchanged.
    $ownerSid = $identity.User.Value
    $helper = $helper.Replace("'S-1-5-32-544'", "'$ownerSid'")
    if (-not $ChildRoot) { Write-Output 'LOCAL FIXTURE: current-user owner substituted; actual Administrators ownership is checked on the elevated Windows CI runner.' }
}
Invoke-Expression $helper
function Get-FmscDownloadParent { return Join-Path (Join-Path $script:fixture ('downloads-' + [char]0x0627)) 'FMSC/Updates' }
$tokens = $null; $errors = $null
$source = Get-Content (Join-Path $project 'Installer/core.ps1') -Raw
$ast = [Management.Automation.Language.Parser]::ParseInput($source,[ref]$tokens,[ref]$errors)
if ($errors.Count) { throw 'Installer does not parse.' }
$abortFunction = $ast.Find({
    param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and
    $n.Name -eq 'Invoke-FmscSafeUpdateAbort'
}, $true)
if (-not $abortFunction) { throw 'Safe recovery handler is missing.' }
$abortSource = $abortFunction.Extent.Text
foreach ($requiredCall in @('Get-InstalledApplicationVersion','Compare-ApplicationVersion',
    'Initialize-FmscRecoveryConnection','Assert-FmscRecoveryDatabaseIdentity',
    'Get-DatabaseVersion','Confirm-FmscRecoveryBackup','Complete-FmscAbortedTransaction')) {
    if ($abortSource -notmatch [regex]::Escape($requiredCall)) {
        throw "Recovery must verify $requiredCall before abandoning an update."
    }
}
if ($abortSource -match 'Invoke-SqlMigration|Enable-StagedApplication|RESTORE DATABASE') {
    throw 'A safe abort must not migrate, activate or restore a database.'
}
$launcher = Get-Content (Join-Path $project 'Installer/Launch-FMSC.ps1') -Raw
if ($launcher -notmatch [regex]::Escape("Phase -notin @('Completed','Aborted')")) {
    throw 'The launcher must not restart a verified Aborted transaction.'
}
$version = [regex]::Match($source, '(?m)^\$AppVersion = ''([^'']+)''').Groups[1].Value
$schema = [int][regex]::Match($source, '(?m)^\$DatabaseVersion = ''([0-9]+)''').Groups[1].Value
foreach ($name in @('Enable-StagedApplication','Remove-VerifiedUpdateDirectory','Copy-ApplicationFiles','Get-ApplicationFileManifest','Test-StagedApplication','Test-PreparedUpdate')) {
    $definition = $ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name},$true)
    Invoke-Expression $definition.Extent.Text
}
function Write-Log { param($Message) }
function Write-Step { param($Message) }
function Write-WarningMessage { param($Message) }
function Stop-AtCheckpoint {
    Set-Content -LiteralPath (Join-Path $script:fixture 'checkpoint') -Value $Checkpoint
    while ($true) { Start-Sleep -Milliseconds 200 }
}
if ($ChildRoot) {
    $script:fixture = [IO.Path]::GetFullPath($ChildRoot)
    if (-not $script:fixture.StartsWith([IO.Path]::GetTempPath(),[StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Leaf $script:fixture) -notmatch '^fmsc-recovery-[a-f0-9]{32}$') { throw 'Invalid child fixture path.' }
    $script:RecoveryRoot = Join-Path $script:fixture 'protected'
    $script:RecoveryJournal = Read-FmscRecoveryJournal $script:RecoveryRoot
    $app = Join-Path $script:fixture 'app'; $stage = "$app.staging"
    function Move-Item {
        [CmdletBinding()]param([string]$LiteralPath,[string]$Destination,[switch]$Force)
        if ($Checkpoint -eq 'BeforeOldMove' -and $LiteralPath -eq $app) { Stop-AtCheckpoint }
        Microsoft.PowerShell.Management\Move-Item -LiteralPath $LiteralPath -Destination $Destination -Force:$Force
        if (($Checkpoint -eq 'AfterOldMove' -and $LiteralPath -eq $app) -or
            ($Checkpoint -eq 'AfterNewMove' -and $LiteralPath -eq $stage)) { Stop-AtCheckpoint }
    }
    if ($Checkpoint -eq 'DuringCleanup') {
        Complete-FmscRecoveryTransaction
        $snapshot = Join-Path $script:RecoveryRoot ('transactions/' + $script:RecoveryJournal.Transaction)
        Remove-Item -LiteralPath (Join-Path $snapshot 'application/FMSC_SMS.exe') -Force
        Stop-AtCheckpoint
    }
    Enable-StagedApplication $stage $app "$app.rollback"
    throw 'Child did not reach its interruption checkpoint.'
}
$script:fixture = Join-Path ([IO.Path]::GetTempPath()) ('fmsc-recovery-' + [Guid]::NewGuid().ToString('N'))
$script:RecoveryRoot = Join-Path $script:fixture 'protected'
$prepared = Get-FmscDownloadParent
$prepared = Join-Path $prepared ('prepared-' + [Guid]::NewGuid().ToString('N'))
$application = Join-Path $prepared 'application'
$app = Join-Path $script:fixture 'app'; $stage = "$app.staging"; $rollback = "$app.rollback"
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
function Install-FmscStableLauncher { param($Source,$Root,$ShortcutPath,$Application) $script:shortcutInstalled = $true }
function Require($Condition,$Message) { if (-not $Condition) { throw $Message } }
function Refuse([scriptblock]$Action) { $refused=$false; try { & $Action } catch { $refused=$true }; Require $refused 'Unsafe recovery was accepted.' }
function Kill-Checkpoint([string]$Name) {
    $marker = Join-Path $script:fixture 'checkpoint'
    Remove-Item -LiteralPath $marker -Force -ErrorAction SilentlyContinue
    $psi = New-Object Diagnostics.ProcessStartInfo
    $psi.FileName = Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
    $psi.Arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + $PSCommandPath + '" -ChildRoot "' + $script:fixture + '" -Checkpoint ' + $Name
    $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
    $psi.RedirectStandardError = $true; $psi.RedirectStandardOutput = $true
    $process = [Diagnostics.Process]::Start($psi)
    $stderr = $process.StandardError.ReadToEndAsync(); $stdout = $process.StandardOutput.ReadToEndAsync()
    try {
        $watch = [Diagnostics.Stopwatch]::StartNew()
        while (-not (Test-Path -LiteralPath $marker)) {
            if ($process.HasExited) { throw "Fixture child failed: $($stderr.GetAwaiter().GetResult()) $($stdout.GetAwaiter().GetResult())" }
            if ($watch.Elapsed.TotalSeconds -gt 40) { throw "Checkpoint $Name timed out." }
            Start-Sleep -Milliseconds 50
        }
        $process.Kill(); $process.WaitForExit()
    } finally { if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }; $process.Dispose() }
}
try {
    $null = New-Item -ItemType Directory -Path (Join-Path $application 'UpdateSupport'),(Join-Path $prepared 'metadata') -Force
    Set-Content (Join-Path $application 'FMSC_SMS.exe') 'inert target application'
    foreach ($name in @('MaintenanceTools.ps1','ToolCatalog.json','core.ps1','UpdateRecovery.ps1','Launch-FMSC.ps1')) {
        Copy-Item -LiteralPath (Join-Path $project "Installer/$name") -Destination (Join-Path $application "UpdateSupport/$name")
    }
    $files = @(Get-ChildItem $application -Recurse -File | ForEach-Object {
        @{path=$_.FullName.Substring($application.Length+1).Replace('\','/');length=$_.Length;sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash}
    })
    @{format='fmsc-app-update/v1';version=$version;requiredDatabaseSchema=$schema;files=$files} |
        ConvertTo-Json -Depth 8 | Set-Content (Join-Path $prepared 'metadata/update-manifest.json')
    $script:RecoveryJournal = Initialize-FmscRecoveryTransaction $prepared $script:RecoveryRoot $version $schema $sid
    Require $script:shortcutInstalled 'Stable shortcut must be installed before journaling permits SQL.'
    $record = Read-FmscRecoveryJournal $script:RecoveryRoot
    $record.DatabaseIdentity = '11111111-1111-4111-8111-111111111111'
    Assert-FmscResumeIdentity $record $script:RecoveryRoot $version $schema (Join-Path $script:RecoveryRoot 'Recovery.ps1')
    Refuse { Assert-FmscResumeIdentity $record $script:RecoveryRoot '9999.0.0' $schema (Join-Path $script:RecoveryRoot 'Recovery.ps1') }
    $record.UserSid = 'S-1-5-21-1-2-3-1234'
    Refuse { Assert-FmscResumeIdentity $record $script:RecoveryRoot $version $schema (Join-Path $script:RecoveryRoot 'Recovery.ps1') }
    $record.UserSid = $sid
    Refuse { Initialize-FmscRecoveryTransaction $prepared $script:RecoveryRoot $version $schema $sid }
    # A partially written replacement is never treated as the active journal.
    Set-Content (Join-Path $script:RecoveryRoot 'recovery.json.abandoned.tmp') '{partial'
    $acl = Get-Acl (Join-Path $script:RecoveryRoot 'recovery.json.abandoned.tmp')
    $acl.SetOwner((New-Object Security.Principal.SecurityIdentifier($ownerSid)))
    Set-Acl (Join-Path $script:RecoveryRoot 'recovery.json.abandoned.tmp') $acl
    Require ((Read-FmscRecoveryJournal $script:RecoveryRoot).Phase -eq 'Prepared') 'Orphan temporary record displaced a committed record.'
    $record.Phase = 'Unknown'; Save-FmscRecoveryJournal $script:RecoveryRoot $record
    Refuse { Read-FmscRecoveryJournal $script:RecoveryRoot }
    $record.Phase = 'Prepared'; Save-FmscRecoveryJournal $script:RecoveryRoot $record
    $record.Schema = 3.5; Save-FmscRecoveryJournal $script:RecoveryRoot $record
    Refuse { Read-FmscRecoveryJournal $script:RecoveryRoot }
    $record.Schema = $schema; Save-FmscRecoveryJournal $script:RecoveryRoot $record
    $snapshot = Join-Path $script:RecoveryRoot ('transactions/' + $record.Transaction)
    foreach ($checkpointName in @('BeforeOldMove','AfterOldMove','AfterNewMove')) {
        foreach ($directory in @($app,$stage,$rollback)) { Remove-VerifiedUpdateDirectory $directory $directory }
        $null = New-Item -ItemType Directory -Path $app
        Set-Content (Join-Path $app 'FMSC_SMS.exe') 'old baseline - must survive'
        Copy-ApplicationFiles (Join-Path $snapshot 'application') $stage
        $script:RecoveryJournal = $record; Set-FmscRecoveryPhase 'Prepared'
        Kill-Checkpoint $checkpointName
        $script:RecoveryJournal = Read-FmscRecoveryJournal $script:RecoveryRoot
        Require ($script:RecoveryJournal.Phase -eq 'Activating') 'Rename intent was not durable before interruption.'
        Remove-VerifiedUpdateDirectory $stage $stage
        Copy-ApplicationFiles (Join-Path $snapshot 'application') $stage
        Test-PreparedUpdate $snapshot $version $schema $stage
        Enable-StagedApplication $stage $app $rollback
        Test-PreparedUpdate $snapshot $version $schema $app
        Require ((Get-Content (Join-Path $rollback 'FMSC_SMS.exe') -Raw).Contains('old baseline')) 'Recovery destroyed the correct rollback baseline.'
    }
    Kill-Checkpoint 'DuringCleanup'
    $script:RecoveryJournal = Read-FmscRecoveryJournal $script:RecoveryRoot
    Require ($script:RecoveryJournal.Phase -eq 'Verified') 'Verification was not committed before cleanup.'
    Test-PreparedUpdate (Join-Path $script:RecoveryRoot 'verification') $version $schema $app
    Complete-FmscRecoveryTransaction
    Remove-VerifiedUpdateDirectory $rollback $rollback
    Remove-FmscCompletedSnapshot
    Require ((Read-FmscRecoveryJournal $script:RecoveryRoot).Phase -eq 'Completed' -and -not (Test-Path $snapshot)) 'Interrupted cleanup did not converge to Completed.'
    # A failed pre-activation migration at the original schema is terminal:
    # no SQL is executed, and the previous application is left untouched.
    foreach ($directory in @($app,$stage,$rollback)) { Remove-VerifiedUpdateDirectory $directory $directory }
    $null = New-Item -ItemType Directory -Path $app
    Set-Content -LiteralPath (Join-Path $app 'FMSC_SMS.exe') -Value 'old baseline - must survive'
    $script:RecoveryJournal = Read-FmscRecoveryJournal $script:RecoveryRoot
    $script:RecoveryJournal.Phase = 'Migrating'
    $script:RecoveryJournal.BeforeSchema = 5
    $script:RecoveryJournal.LastSchema = 0
    $script:RecoveryJournal.Backup = Join-Path $script:fixture 'verified-pre-migration.bak'
    Set-Content -LiteralPath $script:RecoveryJournal.Backup -Value 'inert backup fixture'
    $script:RecoveryJournal.BackupSha256 = (Get-FileHash -LiteralPath $script:RecoveryJournal.Backup -Algorithm SHA256).Hash
    Save-FmscRecoveryJournal $script:RecoveryRoot $script:RecoveryJournal
    Refuse { Complete-FmscAbortedTransaction -ObservedSchema 6 -ApplicationDirectory $app -RollbackDirectory $rollback }
    Require ((Read-FmscRecoveryJournal $script:RecoveryRoot).Phase -eq 'Migrating') 'Advanced schema must preserve active recovery.'
    $script:RecoveryJournal.LastSchema = 6
    Save-FmscRecoveryJournal $script:RecoveryRoot $script:RecoveryJournal
    Refuse { Complete-FmscAbortedTransaction -ObservedSchema 5 -ApplicationDirectory $app -RollbackDirectory $rollback }
    $script:RecoveryJournal.LastSchema = 0
    Save-FmscRecoveryJournal $script:RecoveryRoot $script:RecoveryJournal
    $null = New-Item -ItemType Directory -Path $rollback
    Refuse { Complete-FmscAbortedTransaction -ObservedSchema 5 -ApplicationDirectory $app -RollbackDirectory $rollback }
    Remove-VerifiedUpdateDirectory $rollback $rollback
    Move-Item -LiteralPath (Join-Path $app 'FMSC_SMS.exe') -Destination (Join-Path $app 'missing.exe')
    Refuse { Complete-FmscAbortedTransaction -ObservedSchema 5 -ApplicationDirectory $app -RollbackDirectory $rollback }
    Move-Item -LiteralPath (Join-Path $app 'missing.exe') -Destination (Join-Path $app 'FMSC_SMS.exe')
    Complete-FmscAbortedTransaction -ObservedSchema 5 -ApplicationDirectory $app -RollbackDirectory $rollback
    Require ((Read-FmscRecoveryJournal $script:RecoveryRoot).Phase -eq 'Completed' -and
        $script:RecoveryJournal.Outcome -eq 'Aborted') 'Safe abort was not durably committed in a legacy-compatible state.'
    Require ((Get-Content -LiteralPath (Join-Path $app 'FMSC_SMS.exe') -Raw).Contains('old baseline')) 'Safe abort changed the previous application.'
    Require (Test-Path -LiteralPath $script:RecoveryJournal.Backup) 'Safe abort deleted its verified backup.'
    Refuse { Complete-FmscAbortedTransaction -ObservedSchema 5 -ApplicationDirectory $app -RollbackDirectory $rollback }
    # Exercise the actual core abort decision with inert SQL/backup substitutes.
    # This tests refusal before state mutation and successful no-migration closure.
    Invoke-Expression $abortFunction.Extent.Text
    function Get-InstalledApplicationVersion { return '1.0.8' }
    function Compare-ApplicationVersion { param($Left,$Right) return -1 }
    function Initialize-FmscRecoveryConnection { $script:connectionChecks++ }
    function Assert-FmscRecoveryDatabaseIdentity { $script:identityChecks++ }
    function Get-DatabaseVersion { return [string]$script:syntheticSchema }
    function Convert-ToDbVersion { param($Version) return [int]$Version }
    function Confirm-FmscRecoveryBackup {
        param([int]$CurrentSchema)
        if (-not $script:RecoveryJournal.Backup) { return }
        $script:backupChecks++
        if ($CurrentSchema -ne $script:RecoveryJournal.BeforeSchema -or
            (Get-FileHash -LiteralPath $script:RecoveryJournal.Backup -Algorithm SHA256).Hash -ne
            $script:RecoveryJournal.BackupSha256) {
            throw 'Invalid fixture backup.'
        }
    }
    $AppDir = $app; $AppRollbackDir = $rollback
    $script:connectionChecks = 0; $script:identityChecks = 0; $script:backupChecks = 0
    $script:RecoveryJournal.Phase = 'Migrating'
    Save-FmscRecoveryJournal $script:RecoveryRoot $script:RecoveryJournal
    $script:syntheticSchema = 6
    Refuse { Invoke-FmscSafeUpdateAbort }
    Require ((Read-FmscRecoveryJournal $script:RecoveryRoot).Phase -eq 'Migrating') 'Advanced schema was abandoned.'
    $script:syntheticSchema = 5
    $script:RecoveryJournal.BackupSha256 = '0' * 64
    Save-FmscRecoveryJournal $script:RecoveryRoot $script:RecoveryJournal
    Refuse { Invoke-FmscSafeUpdateAbort }
    $script:RecoveryJournal.BackupSha256 = (Get-FileHash -LiteralPath $script:RecoveryJournal.Backup -Algorithm SHA256).Hash
    Save-FmscRecoveryJournal $script:RecoveryRoot $script:RecoveryJournal
    Invoke-FmscSafeUpdateAbort
    Require ((Read-FmscRecoveryJournal $script:RecoveryRoot).Phase -eq 'Completed' -and
        $script:RecoveryJournal.Outcome -eq 'Aborted' -and
        $script:connectionChecks -eq 3 -and $script:identityChecks -eq 3 -and
        $script:backupChecks -eq 2) 'Safe abort did not gate SQL identity, schema and backup integrity.'
    # Inject a deterministic failure at the first SQL execution boundary after
    # the pre-migration backup has been verified and journaled. This injection
    # exists in TEST CODE ONLY, never in the production updater or published app.
    $migrationFunction = $ast.Find({
        param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $n.Name -eq 'Invoke-PendingMigrations'
    }, $true)
    if (-not $migrationFunction) { throw 'Production migration orchestrator is missing.' }
    Invoke-Expression $migrationFunction.Extent.Text
    $migrationFixture = Join-Path $script:fixture 'inert-migrations'
    $null = New-Item -ItemType Directory -Path $migrationFixture
    foreach ($migrationNumber in (6..$schema)) {
        Set-Content -LiteralPath (Join-Path $migrationFixture "$migrationNumber.sql") -Value '-- inert fixture; never executed'
    }
    $script:faultBackup = Join-Path $script:fixture 'inert-pre-migration-backup.bak'
    $script:faultEvents = [Collections.Generic.List[string]]::new()
    $script:syntheticSchema = 5
    $script:RecoveryJournal.Phase = 'Prepared'
    $script:RecoveryJournal.Backup = ''
    $script:RecoveryJournal.BackupSha256 = ''
    $script:RecoveryJournal.BeforeSchema = 0
    $script:RecoveryJournal.LastSchema = 0
    Save-FmscRecoveryJournal $script:RecoveryRoot $script:RecoveryJournal
    $previousAppHash = (Get-FileHash -LiteralPath (Join-Path $app 'FMSC_SMS.exe') -Algorithm SHA256).Hash
    function New-PreMigrationBackup {
        param($FromVersion, $ToVersion)
        if ("$FromVersion->$ToVersion" -ne "5->$schema") { throw 'Unexpected fixture backup version.' }
        $script:faultEvents.Add('backup-created')
        [IO.File]::WriteAllText($script:faultBackup, 'FAKE BACKUP ONLY -- TEST FILE, NOT SQL SERVER DATA')
        $hash = (Get-FileHash -LiteralPath $script:faultBackup -Algorithm SHA256).Hash
        if (-not $hash) { throw 'Fixture backup hash verification failed.' }
        $script:faultEvents.Add('backup-verified')
        return $script:faultBackup
    }
    function Invoke-SqlMigration {
        param($FilePath, $VersionBefore, $ExpectedVersion)
        if ($ExpectedVersion -ne 6 -or $VersionBefore -ne 5) {
            throw 'Fault injection was not reached at migration 6.'
        }
        $script:faultEvents.Add('fault-before-sql-6')
        throw 'INJECTED_FAILURE_BEFORE_SQL_MIGRATION_6'
    }
    $faultMessage = ''
    try { Invoke-PendingMigrations -Directory $migrationFixture -RequiredVersion $schema }
    catch { $faultMessage = $_.Exception.Message }
    Require ($faultMessage -like '*INJECTED_FAILURE_BEFORE_SQL_MIGRATION_6*') 'Expected test-only failure was not raised.'
    $failedRecord = Read-FmscRecoveryJournal $script:RecoveryRoot
    Require ($failedRecord.Phase -eq 'Migrating' -and $failedRecord.BeforeSchema -eq 5 -and
        $failedRecord.Schema -eq $schema -and $failedRecord.LastSchema -eq 0 -and
        $script:syntheticSchema -eq 5) 'Injected failure modified the simulated database or did not journal the baseline.'
    Require ((Get-FileHash -LiteralPath $failedRecord.Backup -Algorithm SHA256).Hash -eq
        $failedRecord.BackupSha256) 'Injected failure did not preserve the verified backup.'
    Require (($script:faultEvents -join ',') -eq 'backup-created,backup-verified,fault-before-sql-6') 'The failure was not injected at the required boundary.'
    $script:RecoveryJournal = $failedRecord
    Invoke-FmscSafeUpdateAbort
    Require ((Read-FmscRecoveryJournal $script:RecoveryRoot).Phase -eq 'Completed' -and
        $script:RecoveryJournal.Outcome -eq 'Aborted') 'Injected failure was not safely aborted.'
    Require ((Get-FileHash -LiteralPath (Join-Path $app 'FMSC_SMS.exe') -Algorithm SHA256).Hash -eq
        $previousAppHash -and $script:syntheticSchema -eq 5) 'Fault-recovery test changed the app or schema.'
    Refuse { Complete-FmscAbortedTransaction -ObservedSchema 5 -ApplicationDirectory $app -RollbackDirectory $rollback }
    Write-Output "PASS: fault injected AFTER verified backup and BEFORE migration 6; verified baseline 5 -> $schema recorded; no SQL applied, old app unchanged, recovery Aborted, no repeat."
    # An Aborted journal is terminal; a subsequent approved update may start.
    # Verify the actual v1.0.8 C# startup guard accepts the terminal journal.
    # Older FMSC rejects a novel Aborted phase but accepts Completed.
    $legacyGuard = Get-Content -LiteralPath (Join-Path $project 'Core/Updates/UpdateStartupGuard.cs') -Raw
    Require ($legacyGuard -match '"Completed"') 'A terminal phase must remain recognized by the old application.'
    $previousTransaction = $script:RecoveryJournal.Transaction
    $script:RecoveryJournal = Initialize-FmscRecoveryTransaction $prepared $script:RecoveryRoot $version $schema $sid
    Require ($script:RecoveryJournal.Transaction -ne $previousTransaction -and
        $script:RecoveryJournal.Phase -eq 'Prepared') 'Terminal abort incorrectly blocked the next update.'
    # Fail closed when protection is weakened, rather than elevating mutable scripts.
    & icacls.exe $script:RecoveryRoot /grant '*S-1-5-32-545:(M)' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Cannot weaken fixture ACL for refusal test.' }
    Refuse { Read-FmscRecoveryJournal $script:RecoveryRoot }
    Write-Output 'PASS: interrupted activation and cleanup, pre-migration fault injection, safe baseline abort, partial-schema/backup-integrity/rollback/missing-application refusal, no retry, next-update acceptance, identity and storage permission guards. No SQL or live installation.'
} finally {
    $absolute = [IO.Path]::GetFullPath($script:fixture)
    if (-not $absolute.StartsWith([IO.Path]::GetTempPath(),[StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Leaf $absolute) -notmatch '^fmsc-recovery-[a-f0-9]{32}$') { throw 'Unsafe fixture cleanup.' }
    Remove-Item -LiteralPath $absolute -Recurse -Force -ErrorAction SilentlyContinue
}
