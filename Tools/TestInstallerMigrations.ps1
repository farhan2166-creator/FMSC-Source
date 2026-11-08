# Inert migration/verification tests: never connects to SQL or launches the app.
param([string]$InstallerPath = (Join-Path $PSScriptRoot '../Installer/core.ps1'))
$ErrorActionPreference = 'Stop'
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput((Get-Content -LiteralPath $InstallerPath -Raw),[ref]$tokens,[ref]$errors)
if ($errors.Count) { throw 'Installer PowerShell does not parse.' }
foreach ($name in @('Convert-ToVersion','Compare-ApplicationVersion','Convert-ToDbVersion','Invoke-PendingMigrations','Invoke-SqlMigration','Invoke-InstallationDatabaseCheck','Get-ApplicationFileManifest','Test-StagedApplication')) {
    $functions = @($ast.FindAll({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name },$true))
    if ($functions.Count -ne 1) { throw "Expected one $name helper." }
    Invoke-Expression $functions[0].Extent.Text
}
$sqlRunner = ${function:Invoke-SqlMigration}
$activations = @($ast.FindAll({param($n) $n -is [Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Enable-StagedApplication'},$true))
if ($activations.Count -ne 1) { throw 'Review all application activation paths.' }
$flow = $activations[0].Parent
while ($null -ne $flow.Parent -and $flow.Parent -isnot [Management.Automation.Language.StatementBlockAst]) { $flow=$flow.Parent }
$prior = @($flow.Parent.Statements | Where-Object { $_.Extent.StartOffset -lt $flow.Extent.StartOffset })
if (-not ($prior | Where-Object { $_.Extent.Text -match '^Invoke-PendingMigrations ' }) -or
    -not ($prior | Where-Object { $_.Extent.Text -match 'Invoke-InstallationDatabaseCheck' })) { throw 'Migrations and compatibility verification must precede activation.' }
function Write-Log { param($Message) }
function Write-Step { param($Message) }
function Get-DatabaseVersion { $script:reads++; return "$script:current" }
function New-PreMigrationBackup { param($FromVersion,$ToVersion) $script:events.Add("backup:$FromVersion->$ToVersion"); if($script:backupFails){throw 'backup failed'}; return 'verified.bak' }
function Invoke-SqlMigration { param($FilePath,$VersionBefore,$ExpectedVersion)
    if($VersionBefore -ne $script:current){throw 'Wrong version precondition'}
    $script:events.Add("apply:$ExpectedVersion")
    if($script:sqlFails){throw 'sql failed'}
    if(-not $script:wrongVersion){$script:current=$ExpectedVersion}
}
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('FMSC migration checks ' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
$checks=0
function Scenario($Files,$Current,$Required,$Expected,$ErrorText='',$Missing=$false) {
    $directory=Join-Path $scratch ([Guid]::NewGuid().ToString('N'))
    if(-not $Missing){New-Item -ItemType Directory -Path $directory | Out-Null}
    foreach($file in $Files){Set-Content -LiteralPath (Join-Path $directory $file) -Value '-- inert fixture'}
    $script:current=$Current; $script:reads=0; $script:events=[Collections.Generic.List[string]]::new()
    $failure=''
    try{Invoke-PendingMigrations -Directory $directory -RequiredVersion $Required}catch{$failure=$_.Exception.Message}
    if($ErrorText){if(-not $failure.Contains($ErrorText)){throw "Expected '$ErrorText', got '$failure'"}}elseif($failure){throw $failure}
    if(($script:events -join ',') -ne $Expected){throw "Wrong migration order: $($script:events -join ',')"}
    if(($Missing -or @($Files | Where-Object {$_ -like '*.sql'}).Count -eq 0) -and $script:reads -ne 0){throw 'Empty migration folder must not read a migration version.'}
    $script:checks++
}
try {
    foreach ($case in @(
        @('1.0.0','1.1.0-rc.1',-1), @('1.1.0-rc.1','1.1.0-rc.1',0),
        @('1.1.0-rc.1','1.1.0',-1), @('1.1.0','1.1.0-rc.1',1),
        @('1.1.0-rc.2','1.1.0-rc.10',-1), @('1.1.0-rc.1','1.1.0-rc.1.1',-1),
        @('1.1.0-alpha','1.1.0-beta',-1), @('1.1.0-1','1.1.0-alpha',-1),
        @('1.1.0+build.1','1.1.0+build.2',0), @('1.1.0.0','1.1.0',0),
        @('1.1.0.1','1.1.0',1), @('1.2.0-rc.1','1.1.0',1)
    )) {
        if ([Math]::Sign((Compare-ApplicationVersion $case[0] $case[1])) -ne $case[2]) {
            throw "Incorrect application version precedence: $($case[0]) / $($case[1])"
        }
    }
    foreach ($invalid in @('1.1.0-','1.1.0+','unversioned')) {
        $refused=$false
        try { $null=Convert-ToVersion $invalid } catch { $refused=$true }
        if (-not $refused) { throw 'Invalid application version was accepted.' }
    }
    Scenario @() 6 6 '' '' $true
    Scenario @() 6 6 ''
    Scenario @('readme.txt') 6 6 ''
    Scenario @('5.sql','6.sql') 6 6 ''
    Scenario @('7.sql','8.sql') 6 8 'backup:6->8,apply:7,apply:8'
    Scenario @('4.sql','2.sql','3.sql','1.sql') 1 4 'backup:1->4,apply:2,apply:3,apply:4'
    Scenario @('1.sql','2.sql') 10 10 ''
    Scenario @('7.SQL','8.sql') 6 8 'backup:6->8,apply:7,apply:8'
    Scenario @('notes.sql') 6 6 '' 'Invalid migration filename'
    Scenario @('08.sql') 6 8 '' 'Invalid migration filename'
    Scenario @('9.sql') 6 8 '' 'supported schema version'
    Scenario @('8.sql') 6 9 '' 'Available migrations stop'
    # Gap refusals must happen before New-PreMigrationBackup is called.
    Scenario @('8.sql') 6 8 '' 'Missing required migration 7.sql'
    Scenario @('7.sql','9.sql') 6 9 '' 'Missing required migration 8.sql'
    Scenario @('66.sql','68.sql','69.sql') 65 69 '' 'Missing required migration 67.sql'
    Scenario @('66.sql','67.sql','68.sql','69.sql') 65 69 'backup:65->69,apply:66,apply:67,apply:68,apply:69'
    $script:backupFails=$true
    Scenario @('7.sql') 6 7 'backup:6->7' 'backup failed'
    $script:backupFails=$false; $script:sqlFails=$true
    Scenario @('7.sql','8.sql','9.sql') 6 9 'backup:6->9,apply:7' 'sql failed'
    $script:sqlFails=$false; $script:wrongVersion=$true
    Scenario @('7.sql','8.sql','9.sql') 6 9 'backup:6->9,apply:7' 'expected database version'
    $script:wrongVersion=$false
    # Inspect the real runner's same-connection wrapper through an inert sqlcmd replacement.
    # The fixture has GO batches and a path with spaces; the runner must inline its SQL.
    $fixturePath = Join-Path $scratch '8.sql'
    $fixtureSql = "PRINT N'FMSC_INLINE_BATCH_ONE';`r`nGO`r`nPRINT N'FMSC_INLINE_BATCH_TWO';"
    [IO.File]::WriteAllText($fixturePath, $fixtureSql, [Text.UTF8Encoding]::new($false))
    function Invoke-FakeSqlcmd { param($S,[switch]$E,$d,[switch]$C,[switch]$b,$i)
        $script:wrapperPath=$i; $script:wrapper=Get-Content -LiteralPath $i -Raw
        $global:LASTEXITCODE=$script:sqlExit
    }
    $script:SqlCmdPath='Invoke-FakeSqlcmd'; $SqlInstance='inert'; $DatabaseName='inert'
    $script:sqlExit=0
    & $sqlRunner -FilePath $fixturePath -VersionBefore 6 -ExpectedVersion 8
    $firstBatch = $script:wrapper.IndexOf("PRINT N'FMSC_INLINE_BATCH_ONE';", [StringComparison]::Ordinal)
    $secondBatch = $script:wrapper.IndexOf("PRINT N'FMSC_INLINE_BATCH_TWO';", [StringComparison]::Ordinal)
    if($script:wrapper -notmatch 'sp_getapplock' -or $script:wrapper -notmatch 'BEGIN TRANSACTION;' -or
       $firstBatch -lt 0 -or $secondBatch -le $firstBatch -or
       $script:wrapper -match '(?m)^\s*:r\s' -or
       $script:wrapper -notmatch '@After<>8' -or $script:wrapper -notmatch 'COMMIT TRANSACTION;') {
        throw 'Migration transaction/version wrapper or inline SQL is incomplete.'
    }
    if(Test-Path -LiteralPath $script:wrapperPath){throw 'Wrapper was not removed.'}
    $script:sqlExit=1; $failure=''
    try{& $sqlRunner -FilePath $fixturePath -VersionBefore 6 -ExpectedVersion 8}catch{$failure=$_.Exception.Message}
    if(-not $failure.Contains('Migration 8 failed') -or (Test-Path -LiteralPath $script:wrapperPath)){throw 'SQL failure/cleanup must be reported.'}
    # Audit 02: exercise the production staging verifier against owned scratch files.
    # A size-only comparison would incorrectly accept the equal-byte corruption.
    $published = Join-Path $scratch 'published'
    $staged = Join-Path $scratch 'staged'
    $live = Join-Path $scratch 'existing-app'
    foreach ($folder in @($published,$staged,$live)) {
        New-Item -ItemType Directory -Path (Join-Path $folder 'assets') -Force | Out-Null
    }
    $publishedExe = Join-Path $published 'FMSC_SMS.exe'
    $stagedExe = Join-Path $staged 'FMSC_SMS.exe'
    $publishedAsset = Join-Path $published 'assets\theme.ini'
    $stagedAsset = Join-Path $staged 'assets\theme.ini'
    $liveExe = Join-Path $live 'FMSC_SMS.exe'
    [IO.File]::WriteAllBytes($publishedExe,[Text.Encoding]::ASCII.GetBytes('GOOD'))
    [IO.File]::WriteAllBytes($publishedAsset,[Text.Encoding]::ASCII.GetBytes('SAFE'))
    [IO.File]::WriteAllBytes($liveExe,[Text.Encoding]::ASCII.GetBytes('LIVE'))
    Copy-Item -LiteralPath $publishedExe -Destination $stagedExe
    Copy-Item -LiteralPath $publishedAsset -Destination $stagedAsset
    $originalLiveHash=(Get-FileHash -LiteralPath $liveExe -Algorithm SHA256).Hash
    $verified=Test-StagedApplication -Source $published -Staging $staged
    if ($verified.Files -ne 2 -or $verified.Bytes -ne 8) {
        throw 'Identical published and staged files failed to verify or changed the existing count/size report.'
    }
    $integrityChecks=1

    function Assert-StagingRejected([string]$ExpectedProblem) {
        $message=''
        try { $null=Test-StagedApplication -Source $published -Staging $staged }
        catch { $message=$_.Exception.Message }
        if (-not $message.Contains($ExpectedProblem) -or
            -not $message.Contains('Nothing has been changed')) {
            throw "Staged application did not fail closed for $ExpectedProblem; got '$message'."
        }
        if ((Get-FileHash -LiteralPath $liveExe -Algorithm SHA256).Hash -ne $originalLiveHash) {
            throw 'Failed staging verification modified the installed application.'
        }
        $script:integrityChecks++
    }
    [IO.File]::WriteAllBytes($stagedExe,[Text.Encoding]::ASCII.GetBytes('EVIL'))
    if ((Get-Item -LiteralPath $stagedExe).Length -ne (Get-Item -LiteralPath $publishedExe).Length) {
        throw 'Equal-length corruption fixture invalid.'
    }
    Assert-StagingRejected 'SHA-256 differs: FMSC_SMS.exe'
    Copy-Item -LiteralPath $publishedExe -Destination $stagedExe -Force
    [IO.File]::WriteAllBytes($stagedAsset,[Text.Encoding]::ASCII.GetBytes('FAIL'))
    Assert-StagingRejected 'SHA-256 differs: assets\theme.ini'
    Copy-Item -LiteralPath $publishedAsset -Destination $stagedAsset -Force
    [IO.File]::WriteAllBytes($stagedExe,[Text.Encoding]::ASCII.GetBytes('BAD'))
    Assert-StagingRejected 'size differs: FMSC_SMS.exe'
    Copy-Item -LiteralPath $publishedExe -Destination $stagedExe -Force
    Remove-Item -LiteralPath $stagedAsset
    Assert-StagingRejected 'missing: assets\theme.ini'
    Copy-Item -LiteralPath $publishedAsset -Destination $stagedAsset -Force
    [IO.File]::WriteAllBytes((Join-Path $staged 'extra.txt'),[Text.Encoding]::ASCII.GetBytes('EXTRA'))
    Assert-StagingRejected 'unexpected: extra.txt'
    if ((Get-FileHash -LiteralPath $liveExe -Algorithm SHA256).Hash -ne $originalLiveHash) {
        throw 'Integrity checks altered the live-install sentinel.'
    }
    Write-Output "PASS: $integrityChecks staged-application integrity checks: identical, equal-sized executable and nested-asset corruption, truncated, missing, extra and unchanged live application."
    Write-Output "PASS: 15 application-version cases; $checks folder/version/backup scenarios plus SQL wrapper, failure cleanup and activation-order checks."
} finally {
    $resolved=[IO.Path]::GetFullPath($scratch)
    if(-not $resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()),[StringComparison]::OrdinalIgnoreCase)){throw 'Refusing cleanup outside test temp directory.'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
# Audit 04: the owner-side probe is review-only, parseable PowerShell and must
# retain its independently pinned published EXE hash and SELECT-only SQL read.
$localProbePath = Join-Path $PSScriptRoot 'VerifyInstalledFmscRelease.ps1'
if (-not (Test-Path -LiteralPath $localProbePath -PathType Leaf)) {
    throw 'Read-only local release acceptance helper is missing.'
}
$probeTokens=$null; $probeErrors=$null
$probeAst=[Management.Automation.Language.Parser]::ParseFile($localProbePath,[ref]$probeTokens,[ref]$probeErrors)
if ($probeErrors.Count) { throw 'Read-only local release acceptance helper does not parse.' }
$probeSource=Get-Content -LiteralPath $localProbePath -Raw
foreach ($mustHave in @('4c5b5201851b202e2b7ff33c26cabc20bcd00b9c4486be5e99df22de819c8990',
    'Get-FileHash','--verify-installation','SELECT TOP(1)','dbo.DatabaseVersion','Source/main at audit')) {
    if (-not $probeSource.Contains($mustHave)) { throw "Audit 04 verifier missing review safeguard $mustHave." }
}
if ($probeSource -match '(?i)\b(INSERT\s+INTO|UPDATE\s+dbo[.]|DELETE\s+FROM|DROP\s+TABLE|ALTER\s+TABLE)\b') {
    throw 'Audit 04 verifier must never edit school SQL records.'
}
# Audit 05: execute only the isolated acceptance predicate with synthetic
# evidence; never launch FMSC, inspect real SQL, or alter an installed file.
$acceptanceHelpers = @($probeAst.FindAll({
    param($n)
    $n -is [Management.Automation.Language.FunctionDefinitionAst] -and
    $n.Name -eq 'Test-ReleaseProbeAcceptance'
},$true))
if ($acceptanceHelpers.Count -ne 1) {
    throw 'The release probe needs exactly one testable acceptance decision.'
}
Invoke-Expression $acceptanceHelpers[0].Extent.Text
$probeCase = @{
    BinaryMatchesPublication = $true
    BuildInfoStatus = 'MATCHES PUBLISHED BUILD_INFO'
    Schema = 10
    RequiredSchema = 10
    CompatibilityStatus = 'PASS: published application structural compatibility'
    RunningSelection = 'MATCH'
}
if (-not (Test-ReleaseProbeAcceptance @probeCase)) {
    throw 'Matching publication evidence should pass the release probe.'
}
$probeCase.BuildInfoStatus = 'DIFFERS FROM PUBLISHED BUILD_INFO'
if (Test-ReleaseProbeAcceptance @probeCase) {
    throw 'Different BUILD_INFO bytes must refuse release-probe acceptance.'
}
$probeCase.BuildInfoStatus = 'NOT PRESENT'
if (-not (Test-ReleaseProbeAcceptance @probeCase)) {
    throw 'The existing optional BUILD_INFO policy must be preserved.'
}
$probeCase.BinaryMatchesPublication = $false
if (Test-ReleaseProbeAcceptance @probeCase) { throw 'Untrusted executable bytes must fail.' }
$probeCase.BinaryMatchesPublication = $true
$probeCase.RunningSelection = 'DIFFERENT RUNNING EXECUTABLE OR MULTIPLE PROCESSES'
if (Test-ReleaseProbeAcceptance @probeCase) { throw 'Wrong running executable must fail.' }
$probeCase.RunningSelection = 'MATCH'
$probeCase.Schema = 9
if (Test-ReleaseProbeAcceptance @probeCase) { throw 'Wrong SQL schema must fail.' }
$probeCase.Schema = 10
$probeCase.CompatibilityStatus = 'FAILED: verifier exit 2'
if (Test-ReleaseProbeAcceptance @probeCase) { throw 'Incompatible SQL must fail.' }
Write-Output 'PASS: 7 isolated publication acceptance cases, including mismatched and optional BUILD_INFO, without SQL or executable launch.'
Write-Output 'PASS: read-only installed-release helper parses and retains pinned provenance, SQL read and verified executable guard.'
$global:LASTEXITCODE = 0
