# Purely disposable regression: no real FMSC path, SQL instance or school files are modified.
$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot '../Installer/Uninstall-FMSC.ps1'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($scriptPath,[ref]$tokens,[ref]$errors)
if ($errors.Count -ne 0) { throw "Standalone uninstaller PowerShell parse failed: $($errors[0])" }
foreach ($name in @('Test-FmscNoLinks','Test-FmscCloudRecoveryArtifact','Get-FmscProtectedFiles','Save-FmscProtectedFiles',
    'Assert-FmscProtectedCopies','Remove-FmscNonProtectedContents')) {
    $definition = $ast.Find({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    },$true)
    if (-not $definition) { throw "Missing uninstall safety function: $name" }
    . ([scriptblock]::Create($definition.Extent.Text))
}
# The completion acknowledgement must exit the uninstaller, but a stopped
# uninstall must leave its error on screen instead of silently closing.
$uninstallWindow = $ast.Find({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq 'Show-FmscUninstallWindow'
},$true)
if (-not $uninstallWindow) { throw 'Missing standalone uninstaller window.' }
$completed = @($uninstallWindow.Body.FindAll({
    param($node)
    $node -is [Management.Automation.Language.AssignmentStatementAst] -and
        $node.Left.Extent.Text -ceq '$uninstallSucceeded' -and
        $node.Right.Extent.Text -ceq '$true'
},$true))
$closeAfterSuccess = @($uninstallWindow.Body.FindAll({
    param($node)
    $node -is [Management.Automation.Language.IfStatementAst] -and
        $node.Clauses.Count -eq 1 -and
        $node.Clauses[0].Item1.Extent.Text.Trim() -ceq '$uninstallSucceeded' -and
        $node.Clauses[0].Item2.Extent.Text.Contains('$form.Close()')
},$true))
if ($completed.Count -ne 1 -or $closeAfterSuccess.Count -ne 1) {
    throw 'Uninstaller must close automatically only after success acknowledgement.'
}
$ProtectExtensions = @('.mdf','.ldf','.bak','.ndf','.bacpac','.dacpac','.fmscbackup')
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('FMSC-Uninstall-Disposable-'+[Guid]::NewGuid().ToString('N'))
try {
    $app = Join-Path $testRoot 'Application'
    $updater = Join-Path $testRoot 'Updater'
    $school = Join-Path $testRoot 'SchoolDatabase'
    $external = Join-Path $testRoot 'UnrelatedDocuments'
    $profile = Join-Path $testRoot 'Users\TestSchool\AppData\Local\FMSC'
    $cloud = Join-Path $profile 'CloudBackups'
    $DataDirectory = $school
    foreach ($folder in @($app,$updater,$school,$external,$cloud)) {
        New-Item -ItemType Directory -Path $folder -Force | Out-Null
    }
    $appMdf = Join-Path $app 'subdir\spare.mdf'
    $appLdf = Join-Path $app 'subdir\spare_log.ldf'
    $updateBak = Join-Path $updater 'transactions\recovery.BAK'
    foreach ($file in @($appMdf,$appLdf,$updateBak)) {
        New-Item -ItemType Directory -Path (Split-Path $file -Parent) -Force | Out-Null
        [IO.File]::WriteAllBytes($file,([Text.Encoding]::UTF8.GetBytes('synthetic-database-'+$file)))
    }
    [IO.File]::WriteAllText((Join-Path $app 'FMSC_SMS.exe'),'synthetic removable app')
    [IO.File]::WriteAllText((Join-Path $school 'FMSC_DB.mdf'),'synthetic live db')
    [IO.File]::WriteAllText((Join-Path $school 'FMSC_DB_log.ldf'),'synthetic live log')
    [IO.File]::WriteAllText((Join-Path $school 'Backups.bak'),'synthetic live backup')
    [IO.File]::WriteAllText((Join-Path $school 'Install.log'),'synthetic removable log')
    [IO.File]::WriteAllText((Join-Path $external 'not-FMSC.txt'),'external data')
    $package = Join-Path $cloud 'queue\abc.fmscbackup'
    $queuedKey = Join-Path $cloud 'queue\abc.fmscbackup.dpapi'
    $journal = Join-Path $cloud 'journal\abc.json'
    $recoveryKey = Join-Path $cloud 'recovery-key.v1.dpapi'
    $oauth = Join-Path $cloud 'oauth-googledrive.v1.dpapi'
    $schedule = Join-Path $cloud 'schedule.json'
    foreach ($file in @($package,$queuedKey,$journal,$recoveryKey,$oauth,$schedule)) {
        New-Item -ItemType Directory -Path (Split-Path $file -Parent) -Force | Out-Null
        [IO.File]::WriteAllText($file,'synthetic cloud material for '+$file)
    }
    $roots = @(
        [pscustomobject]@{Label='Application';Path=$app},
        [pscustomobject]@{Label='Updater';Path=$updater},
        [pscustomobject]@{Label='User-Synthetic';Path=$profile}
    )
    $preserved = Join-Path $school ('UninstallPreserved\'+[guid]::NewGuid().ToString('N'))
    $original = Get-FileHash -LiteralPath $appMdf -Algorithm SHA256
    $originalBackup = Get-FileHash -LiteralPath $updateBak -Algorithm SHA256
    $records = @(Save-FmscProtectedFiles -Roots $roots -PreservationDirectory $preserved)
    if ($records.Count -ne 7) { throw "Expected seven protected files including cloud recovery state; found $($records.Count)." }
    foreach ($file in @($package,$queuedKey,$journal,$recoveryKey)) {
        if (@($records | Where-Object Source -eq $file).Count -ne 1) {
            throw "Missing encrypted cloud recovery artifact: $file"
        }
    }
    if (@($records | Where-Object { $_.Source -eq $oauth -or $_.Source -eq $schedule }).Count -gt 0) {
        throw 'OAuth tokens or cloud settings must not be retained with school recovery data.'
    }
    Assert-FmscProtectedCopies -Roots $roots -Records $records
    $lateKey = Join-Path $cloud 'queue\late.fmscbackup.dpapi'
    [IO.File]::WriteAllText($lateKey,'new protected cloud key')
    $rejected = $false
    try { Assert-FmscProtectedCopies -Roots $roots -Records $records } catch { $rejected = $true }
    if (-not $rejected) { throw 'New cloud recovery credential was not detected.' }
    Remove-Item -LiteralPath $lateKey -Force
    [IO.File]::WriteAllText($journal,'changed cloud delivery receipt')
    $rejected = $false
    try { Assert-FmscProtectedCopies -Roots $roots -Records $records } catch { $rejected = $true }
    if (-not $rejected) { throw 'Changed cloud journal was not detected.' }
    [IO.File]::WriteAllText($journal,'synthetic cloud material for '+$journal)
    Assert-FmscProtectedCopies -Roots $roots -Records $records
    $new = Join-Path $updater 'just-arrived.bak'
    [IO.File]::WriteAllText($new,'new recovery backup')
    $rejected = $false
    try { Assert-FmscProtectedCopies -Roots $roots -Records $records } catch { $rejected = $true }
    if (-not $rejected) { throw 'A newly created backup was not detected before deletion.' }
    Remove-Item -LiteralPath $new -Force
    [IO.File]::WriteAllText($appMdf,'changed mdf after preservation')
    $rejected = $false
    try { Assert-FmscProtectedCopies -Roots $roots -Records $records } catch { $rejected = $true }
    if (-not $rejected) { throw 'Changed database contents were not detected before deletion.' }
    [IO.File]::WriteAllBytes($appMdf,[Text.Encoding]::UTF8.GetBytes('synthetic-database-'+$appMdf))
    Assert-FmscProtectedCopies -Roots $roots -Records $records

    foreach ($root in $roots) { Remove-Item -LiteralPath $root.Path -Recurse -Force }
    Remove-FmscNonProtectedContents $school
    if (Test-Path -LiteralPath $app) { throw 'Synthetic application not removed.' }
    if (Test-Path -LiteralPath $updater) { throw 'Synthetic updater not removed.' }
    if (Test-Path -LiteralPath $profile) { throw 'Synthetic cloud cache and OAuth tokens were not removed.' }
    if (Test-Path -LiteralPath (Join-Path $school 'Install.log')) { throw 'Synthetic logs not removed.' }
    foreach ($name in @('FMSC_DB.mdf','FMSC_DB_log.ldf','Backups.bak')) {
        if (-not (Test-Path -LiteralPath (Join-Path $school $name))) {
            throw "Existing school file disappeared: $name"
        }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $external 'not-FMSC.txt'))) {
        throw 'Unrelated data was modified.'
    }
    $savedMdf = $records | Where-Object Source -eq $appMdf
    $savedBak = $records | Where-Object Source -eq $updateBak
    if (-not $savedMdf -or -not $savedBak -or
        (Get-FileHash -LiteralPath $savedMdf.Target).Hash -cne $original.Hash -or
        (Get-FileHash -LiteralPath $savedBak.Target).Hash -cne $originalBackup.Hash) {
        throw 'Protected database and backup copies were not retained byte-for-byte.'
    }
    foreach ($record in $records) {
        if (-not (Test-Path -LiteralPath $record.Target -PathType Leaf) -or
            (Get-FileHash -LiteralPath $record.Target -Algorithm SHA256).Hash -cne $record.Sha256) {
            throw "Protected SQL or cloud recovery file lost during database folder cleanup: $($record.Source)"
        }
    }
    Write-Host 'PASS: successful uninstalls close the window after acknowledgement; failure remains visible.'
    Write-Host 'PASS: SQL and encrypted cloud recovery copies survive cleanup; new or changed artifacts block deletion.'
    Write-Host 'PASS: removable FMSC folders/logs can be cleared without deleting school data or unrelated files.'
} finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

& (Join-Path $PSScriptRoot 'TestUninstallTasks.ps1')
