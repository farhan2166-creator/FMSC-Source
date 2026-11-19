# Purely disposable regression: no real FMSC path, SQL instance or school files are modified.
$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot '../Installer/Uninstall-FMSC.ps1'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($scriptPath,[ref]$tokens,[ref]$errors)
if ($errors.Count -ne 0) { throw "Standalone uninstaller PowerShell parse failed: $($errors[0])" }
foreach ($name in @('Test-FmscNoLinks','Test-FmscCloudRecoveryArtifact','Test-FmscPendingTransactionArtifact','Get-FmscPendingTransactionJournals','Assert-FmscNoPendingTransactions','Get-FmscProtectedFiles','Save-FmscProtectedFiles',
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
    $transactionProfile = Join-Path $testRoot 'Users\TestSchool\AppData\Local\FMSC_SMS'
    $DataDirectory = $school
    foreach ($folder in @($app,$updater,$school,$external,$cloud,$transactionProfile)) {
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
    $oauth = Join-Path $cloud 'oauth-dropbox.v1.dpapi'
    $schedule = Join-Path $cloud 'schedule.json'
    foreach ($file in @($package,$queuedKey,$journal,$recoveryKey,$oauth,$schedule)) {
        New-Item -ItemType Directory -Path (Split-Path $file -Parent) -Force | Out-Null
        [IO.File]::WriteAllText($file,'synthetic cloud material for '+$file)
    }
    $submissionFolder=Join-Path $profile ('Communication\Attempts\'+('A'*32)+'\'+('b'*32))
    New-Item -ItemType Directory -Path $submissionFolder -Force | Out-Null
    $submission=Join-Path $submissionFolder (('d'*32)+'.claim.dpapi')
    $submissionResult=Join-Path $submissionFolder (('d'*32)+'.result.dpapi')
    [IO.File]::WriteAllText($submission,'synthetic protected submission')
    [IO.File]::WriteAllText($submissionResult,'synthetic protected provider reference')
    # These journals are the only durable evidence of unknown SQL commit outcomes.
    # They must survive uninstall and be inspected before a transaction is retried.
    $pendingCollection = Join-Path $transactionProfile 'PendingCollection.json'
    $pendingRegistration = Join-Path $transactionProfile 'PendingQuickRegistration.json'
    $pendingMutation = Join-Path $transactionProfile 'PendingFeeAdjustment.json'
    $nonRecovery = Join-Path $transactionProfile 'preferences.json'
    $nestedNonRecovery = Join-Path $transactionProfile 'Nested\PendingCollection.json'
    foreach ($file in @($pendingCollection,$pendingRegistration,$pendingMutation,
        $nonRecovery,$nestedNonRecovery)) {
        New-Item -ItemType Directory -Path (Split-Path $file -Parent) -Force | Out-Null
        [IO.File]::WriteAllText($file,'synthetic pending operation '+$file)
    }
    $roots = @(
        [pscustomobject]@{Label='Application';Path=$app},
        [pscustomobject]@{Label='Updater';Path=$updater},
        [pscustomobject]@{Label='User-Synthetic';Path=$profile},
        [pscustomobject]@{Label='User-Transactions';Path=$transactionProfile}
    )
    # Must refuse uninstall without removing the unresolved original SQL intents.
    $discovered = @(Get-FmscPendingTransactionJournals -Roots $roots)
    if ($discovered.Count -ne 3) { throw 'Pending SQL journal discovery was incomplete.' }
    $blocked = $false
    try { Assert-FmscNoPendingTransactions -Roots $roots } catch {
        $blocked = $_.Exception.Message -like '*unresolved save recovery record*'
    }
    if (-not $blocked) { throw 'Uninstall did not refuse unknown SQL save outcomes.' }
    foreach ($file in @($pendingCollection,$pendingRegistration,$pendingMutation)) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw 'Blocked uninstall removed a journal.' }
    }
    $preserved = Join-Path $school ('UninstallPreserved\'+[guid]::NewGuid().ToString('N'))
    $original = Get-FileHash -LiteralPath $appMdf -Algorithm SHA256
    $originalBackup = Get-FileHash -LiteralPath $updateBak -Algorithm SHA256
    $records = @(Save-FmscProtectedFiles -Roots $roots -PreservationDirectory $preserved)
    if ($records.Count -ne 12) { throw "Expected SQL, cloud, communication and pending transaction recovery artifacts; found $($records.Count)." }
    foreach ($file in @($package,$queuedKey,$journal,$recoveryKey,$submission,$submissionResult)) {
        if (@($records | Where-Object Source -eq $file).Count -ne 1) {
            throw "Missing encrypted cloud recovery artifact: $file"
        }
    }
    if (@($records | Where-Object { $_.Source -eq $oauth -or $_.Source -eq $schedule }).Count -gt 0) {
        throw 'OAuth tokens or cloud settings must not be retained with school recovery data.'
    }
    foreach ($file in @($pendingCollection,$pendingRegistration,$pendingMutation)) {
        if (@($records | Where-Object Source -eq $file).Count -ne 1) {
            throw "Missing pending SQL transaction evidence: $file"
        }
    }
    if (@($records | Where-Object { $_.Source -eq $nonRecovery -or
        $_.Source -eq $nestedNonRecovery }).Count -gt 0) {
        throw 'Unrelated app settings or nested files must not be archived as pending SQL evidence.'
    }
    Assert-FmscProtectedCopies -Roots $roots -Records $records
    $lateKey = Join-Path $cloud 'queue\late.fmscbackup.dpapi'
    [IO.File]::WriteAllText($lateKey,'new protected cloud key')
    $rejected = $false
    try { Assert-FmscProtectedCopies -Roots $roots -Records $records } catch { $rejected = $true }
    if (-not $rejected) { throw 'New cloud recovery credential was not detected.' }
    Remove-Item -LiteralPath $lateKey -Force
    $lateTransaction = Join-Path $transactionProfile 'PendingStudentHold.json'
    [IO.File]::WriteAllText($lateTransaction,'new pending SQL intent')
    $rejected = $false
    try { Assert-FmscProtectedCopies -Roots $roots -Records $records } catch { $rejected = $true }
    if (-not $rejected) { throw 'Newly pending financial recovery journal was not detected.' }
    Remove-Item -LiteralPath $lateTransaction -Force
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

    # Synthetic operator reconciliation: the copies remain in the archive.
    foreach ($file in @($pendingCollection,$pendingRegistration,$pendingMutation)) {
        Remove-Item -LiteralPath $file -Force
    }
    Assert-FmscNoPendingTransactions -Roots $roots
    foreach ($root in $roots) {
        Assert-FmscNoPendingTransactions -Roots $roots
        Remove-Item -LiteralPath $root.Path -Recurse -Force
    }
    Assert-FmscNoPendingTransactions -Roots $roots
    Remove-FmscNonProtectedContents $school
    if (Test-Path -LiteralPath $app) { throw 'Synthetic application not removed.' }
    if (Test-Path -LiteralPath $updater) { throw 'Synthetic updater not removed.' }
    if (Test-Path -LiteralPath $profile) { throw 'Synthetic cloud cache and OAuth tokens were not removed.' }
    if (Test-Path -LiteralPath $transactionProfile) { throw 'Synthetic transaction settings were not removed.' }
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
    Write-Host 'PASS: unresolved SQL save journals block uninstall until reconciled.'
    Write-Host 'PASS: SQL, cloud and pending transaction recovery evidence survives; late or modified protected files block deletion.'
    Write-Host 'PASS: removable FMSC folders/logs can be cleared without deleting school data or unrelated files.'
} finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

& (Join-Path $PSScriptRoot 'TestUninstallTasks.ps1')
