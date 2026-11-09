# End-to-end prepared-update acceptance on the already owned synthetic install.
# Fault hooks and restart observation are inserted into a private fixture copy;
# production scripts and publish artifacts are never instrumented.
param([string]$SourceApplication)
$ErrorActionPreference='Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_OS -ne 'Windows' -or
    $env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or $env:FMSC_DISPOSABLE_INSTALL -ne 'approved-synthetic-only') {
    throw 'Prepared activation acceptance requires the approved hosted synthetic installation.'
}
$project=Split-Path $PSScriptRoot -Parent
. (Join-Path $project 'Installer/UpdateRecovery.ps1')
$root=Join-Path $env:RUNNER_TEMP ('fmsc-prepared-recovery-'+[Guid]::NewGuid().ToString('N'))
$prepared=Join-Path (Get-FmscDownloadParent) ('prepared-'+[Guid]::NewGuid().ToString('N'))
$state=Split-Path -Parent $prepared
$marker=Join-Path $state ('fixture-'+[Guid]::NewGuid().ToString('N')+'.marker')
$installed='C:\FMSC School Management'
$recovery=Get-FmscRecoveryRoot
$child=$null; $original=$null
function Require($Condition,$Message) { if (-not $Condition) { throw $Message } }
function Wait-File($Path,$Process,$Seconds) {
    $watch=[Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath $Path)) {
        if ($Process -and $Process.HasExited) { throw "Prepared fixture exited before checkpoint (code $($Process.ExitCode))." }
        if ($watch.Elapsed.TotalSeconds -gt $Seconds) { throw 'Prepared recovery checkpoint timed out.' }
        Start-Sleep -Milliseconds 100
    }
}
try {
    $null=New-Item -ItemType Directory -Path $root,(Join-Path $prepared 'metadata') -Force
    Set-Content -LiteralPath $marker 'inert enrollment-neighbor preservation marker'
    Copy-Item -LiteralPath $SourceApplication -Destination (Join-Path $prepared 'application') -Recurse
    $application=Join-Path $prepared 'application'
    $core=Join-Path $application 'UpdateSupport/core.ps1'
    $text=[IO.File]::ReadAllText($core)
    $version=[regex]::Match($text,'(?m)^\$AppVersion = ''([^'']+)''').Groups[1].Value
    $schema=[int][regex]::Match($text,'(?m)^\$DatabaseVersion = ''([0-9]+)''').Groups[1].Value
    $tokens=$null;$errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseInput($text,[ref]$tokens,[ref]$errors)
    Require (-not $errors.Count) 'Fixture source does not parse.'
    $activation=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Enable-StagedApplication'},$true)
    $moveTry=@($activation.Body.EndBlock.Statements | Where-Object {$_ -is [Management.Automation.Language.TryStatementAst]})[0]
    $restart=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Start-FmscAfterInstallation'},$true)
    $main=@($ast.EndBlock.Statements | Where-Object {$_ -is [Management.Automation.Language.TryStatementAst] -and $_.Body.Extent.Text.Contains('Open-UpdateWindow')})[0]
    Require ($moveTry -and $restart -and $main) 'Cannot locate the reviewed installer fault-injection boundaries.'
    $checkpoint=Join-Path $root 'old-moved';$completed=Join-Path $root 'completed'
    $previousAppHash=(Get-FileHash -LiteralPath (Join-Path $installed 'FMSC_SMS.exe') -Algorithm SHA256).Hash
    $hook="if (`$env:FMSC_RECOVERY_FIXTURE_STOP -eq '1') { [IO.File]::WriteAllText('$($checkpoint.Replace("'","''"))','moved'); while (`$true) { Start-Sleep -Milliseconds 200 } }`r`n"
    $replacement="function Start-FmscAfterInstallation { param([string]`$Application) if ((Read-FmscRecoveryJournal `$script:RecoveryRoot).Phase -ne 'Completed' -or `$script:InstallerLockHeld) { throw 'Restart before completion/lock release' }; [IO.File]::WriteAllText('$($completed.Replace("'","''"))','verified') }"
    $edits=@(
        @{Start=$moveTry.Extent.StartOffset;Length=0;Text=$hook},
        @{Start=$restart.Extent.StartOffset;Length=$restart.Extent.Text.Length;Text=$replacement},
        @{Start=$main.CatchClauses[0].Extent.StartOffset;Length=$main.CatchClauses[0].Extent.Text.Length;Text='catch { Write-Output (''FIXTURE_FAILED: ''+$_.Exception.Message); exit 1 }'}
    )
    foreach ($edit in $edits | Sort-Object Start -Descending) { $text=$text.Remove($edit.Start,$edit.Length).Insert($edit.Start,$edit.Text) }
    # Test-only replacement removes the modal completion dialog so a real
    # elevated launcher recovery can complete unattended on the hosted runner.
    $resumeStart="    if (`$ResumeUpdate -and `$script:RecoveryJournal.Phase -in @('Prepared','Migrating','Activating')) {"
    $resumeEnd="    if (`$ResumeUpdate -and `$script:RecoveryJournal.Phase -eq 'Verified') {"
    $from=$text.IndexOf($resumeStart,[StringComparison]::Ordinal)
    $to=$text.IndexOf($resumeEnd,$from,[StringComparison]::Ordinal)
    Require ($from -ge 0 -and $to -gt $from) 'Unable to instrument the reviewed rollback-resume boundary.'
    $resumeFixture=@"
    if (`$ResumeUpdate -and `$script:RecoveryJournal.Phase -in @('Prepared','Migrating','Activating')) {
        Invoke-FmscRollbackFailedUpdate
        [IO.File]::WriteAllText('$($completed.Replace("'","''"))','rolled-back')
        Release-FmscInstallerLock
        return
    }
"@
    $text=$text.Remove($from,$to-$from).Insert($from,$resumeFixture+[Environment]::NewLine)
    [IO.File]::WriteAllText($core,$text,(New-Object Text.UTF8Encoding($false)))
    $files=@(Get-ChildItem $application -Recurse -File | ForEach-Object {
        @{path=$_.FullName.Substring($application.Length+1).Replace('\','/');length=$_.Length;sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash}
    })
    @{format='fmsc-app-update/v1';version=$version;requiredDatabaseSchema=$schema;files=$files} |
        ConvertTo-Json -Depth 8 | Set-Content (Join-Path $prepared 'metadata/update-manifest.json') -Encoding UTF8
    $original=Start-Process powershell.exe -ArgumentList @('-NoProfile','-Command','Start-Sleep -Seconds 2') -WindowStyle Hidden -PassThru
    $started=$original.StartTime.ToUniversalTime().Ticks
    $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $psi=[Diagnostics.ProcessStartInfo]::new('powershell.exe')
    $psi.UseShellExecute=$false;$psi.CreateNoWindow=$true;$psi.RedirectStandardOutput=$true;$psi.RedirectStandardError=$true
    $psi.EnvironmentVariables['FMSC_RECOVERY_FIXTURE_STOP']='1'
    $psi.Arguments='-NoProfile -STA -ExecutionPolicy Bypass -File "'+$core+'" -PreparedRoot "'+$prepared+'" -OriginalProcessId '+$original.Id+' -OriginalStartTicks '+$started+' -OriginalUserSid '+$sid
    $child=[Diagnostics.Process]::Start($psi)
    $stdout=$child.StandardOutput.ReadToEndAsync();$stderr=$child.StandardError.ReadToEndAsync()
    try { Wait-File $checkpoint $child 240 }
    catch {
        $message=$_.Exception.Message
        if (-not $child.HasExited) { $child.Kill($true);$child.WaitForExit() }
        throw "Prepared installer checkpoint failed: $message $($stderr.GetAwaiter().GetResult()) $($stdout.GetAwaiter().GetResult())"
    }
    $child.Kill($true);$child.WaitForExit()
    Require (-not (Test-Path $installed) -and (Test-Path "$installed.rollback")) 'The missing-app activation gap was not reproduced.'
    Require ((Read-FmscRecoveryJournal $recovery).Phase -eq 'Activating') 'Activation intent was not committed by the actual installer.'
    $shortcut=(New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path ([Environment]::GetFolderPath('Desktop')) 'FMSC SMS.lnk'))
    Require ($shortcut.TargetPath -ieq (Join-Path $recovery 'FMSC.Launcher.exe')) 'Shortcut is not outside the missing application directory.'
    # Execute the real stable launcher, including its real elevated resume handoff.
    $launcher=[Diagnostics.ProcessStartInfo]::new($shortcut.TargetPath)
    $launcher.UseShellExecute=$false;$launcher.CreateNoWindow=$true;$launcher.Arguments=$shortcut.Arguments
    $launcherProcess=[Diagnostics.Process]::Start($launcher)
    try {
        if (-not $launcherProcess.WaitForExit(30000)) { $launcherProcess.Kill($true);throw 'Stable launcher did not return.' }
    } finally { $launcherProcess.Dispose() }
    Wait-File $completed $null 240
    $record=Read-FmscRecoveryJournal $recovery
    Require ($record.Phase -eq 'Completed' -and $record.Outcome -eq 'Aborted') 'Actual interrupted activation was not rolled back.'
    Require (Test-Path (Join-Path $installed 'FMSC_SMS.exe')) 'Rollback lost the installed application.'
    Require (-not (Test-Path "$installed.rollback")) 'Rollback left an unused previous-app directory.'
    Require (Test-Path $prepared) 'Rollback deleted the retained prepared package.'
    Require (Test-Path $marker) 'Rollback deleted unrelated user files.'
    Require ((Get-FileHash (Join-Path $installed 'FMSC_SMS.exe') -Algorithm SHA256).Hash -eq
        $previousAppHash) 'Interrupted activation did not restore the exact original executable.'
    Write-Output 'PASS: prepared update interrupted after moving original app; stable launcher elevated and rolled back the exact previous executable without resuming activation. Download cache and unrelated files preserved.'
} finally {
    if ($child) { if (-not $child.HasExited) { $child.Kill($true);$child.WaitForExit() };$child.Dispose() }
    if ($original) { if (-not $original.HasExited) { $original.Kill($true);$original.WaitForExit() };$original.Dispose() }
    if ([IO.Path]::GetFullPath($prepared).StartsWith([IO.Path]::GetFullPath((Get-FmscDownloadParent))+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $prepared -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $marker -Force -ErrorAction SilentlyContinue
    } else { throw 'Unsafe prepared fixture cleanup.' }
    if (-not [IO.Path]::GetFullPath($root).StartsWith([IO.Path]::GetFullPath($env:RUNNER_TEMP),[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe checkpoint cleanup.' }
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
