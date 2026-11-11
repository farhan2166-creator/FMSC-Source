# Offline only; validate Workflow 04 wiring without publishing or invoking GitHub APIs.
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$workflow=Get-Content (Join-Path $root '.github/workflows/fmsc-windows-download.yml') -Raw
$prep=Get-Content (Join-Path $root 'Tools/PrepareWorkflow04Release.ps1') -Raw
$dispatch=Get-Content (Join-Path $root 'Tools/DispatchSandboxAcceptance.ps1') -Raw
$wait=Get-Content (Join-Path $root 'Tools/WaitForSandboxAttestation.ps1') -Raw
function Check([bool]$Pass,[string]$Failure) { if (-not $Pass) { throw "Workflow04 regression: $Failure" } }
foreach ($name in @('PrepareWorkflow04Release.ps1','AssertWorkflow04MigrationInventory.ps1','ResolveWorkflow04Version.ps1','ReadWorkflow04SourceVersions.ps1','DispatchSandboxAcceptance.ps1','WaitForSandboxAttestation.ps1','AssertSandboxPublicationGate.ps1','AssertWorkflow04Artifact.ps1')) {
    $tokens=$null;$errors=$null
    $null=[Management.Automation.Language.Parser]::ParseFile((Join-Path $root ('Tools/'+$name)),[ref]$tokens,[ref]$errors)
    Check (@($errors).Count -eq 0) "PowerShell parser errors in $name."
}
Check ($workflow -match '(?m)^\s+default: true\s*$') 'Owner publication is not the default.'
$packageMatch=[regex]::Match($workflow,'(?ms)^  package:\r?\n(.*?)(?=^  publish:\r?\n|\z)')
$publisherMatch=[regex]::Match($workflow,'(?ms)^  publish:\r?\n(.*)\z')
Check ($packageMatch.Success -and $publisherMatch.Success) 'Separate Windows verification and publication jobs are required.'
$package=$packageMatch.Value
$publisher=$publisherMatch.Value
Check ($package -match '(?m)^    needs: prepare\r?
Check ($workflow.Contains('ref: ') -and $workflow.Contains('needs.prepare.outputs.release_sha')) 'Release checkout must use the prepared SHA.'
Check ($workflow.Contains('DispatchSandboxAcceptance.ps1') -and $workflow.Contains('WaitForSandboxAttestation.ps1')) 'Private Sandbox dispatch/wait is absent.'
Check ($workflow.Contains('AssertSandboxPublicationGate.ps1 -SourceSha') -and $workflow.Contains('PublishPrivateUpdate.ps1')) 'No final trusted status recheck before publication.'
Check ($workflow.IndexOf('  prepare:') -gt 0 -and
    $workflow.IndexOf('  prepare:') -lt $workflow.IndexOf('  sandbox-release-gate:') -and
    $workflow.IndexOf('  sandbox-release-gate:') -lt $workflow.IndexOf('  package:') -and
    $workflow.IndexOf('  package:') -lt $workflow.IndexOf('  publish:')) 'Release job declaration order is unsafe.'
# Execute exactly the parser Workflow 04 uses, with synthetic installer
# values. A refactor of regex formatting or whitespace must not itself block
# releases; invalid, duplicated or noncanonical assignments still do.
. (Join-Path $root 'Tools/ReadWorkflow04SourceVersions.ps1')
$core=Get-Content -LiteralPath (Join-Path $root 'Installer/core.ps1') -Raw
$actual=Read-FmscWorkflow04SourceVersions -InstallerText $core
Check ($actual.AppVersion -match '^[0-9]+\.[0-9]+\.[0-9]+$') 'Current app version is invalid.'
Check ($actual.Schema -ge 7) 'Current schema is below the supported private upgrade baseline.'
$parserAstTokens=$null;$parserAstErrors=$null
$prepParserAst=[Management.Automation.Language.Parser]::ParseInput($prep,[ref]$parserAstTokens,[ref]$parserAstErrors)
$readers=@($prepParserAst.FindAll({
    param($node)
    $node -is [Management.Automation.Language.CommandAst] -and
    $node.GetCommandName() -ceq 'Read-FmscWorkflow04SourceVersions'
},$true))
Check ($readers.Count -eq 1) 'Release preparation must call the tested version parser.'
function Assert-VersionParse {
    param([string]$Case,[string]$Text,[bool]$Expected)
    $passed=$true
    try { $null=Read-FmscWorkflow04SourceVersions -InstallerText $Text }
    catch { $passed=$false }
    Check ($passed -eq $Expected) "Release version parser behavior: $Case."
}
$sample=@(
    '$AppVersion = ''1.2.37'''
    '$DatabaseVersion = ''42'''
) -join "`n"
Assert-VersionParse 'valid future source line' $sample $true
Assert-VersionParse 'whitespace-independent valid source' ($sample -replace ' = ','  =  ') $true
Assert-VersionParse 'duplicate source version rejected' ($sample+"`n"+'$AppVersion = ''1.2.38''') $false
Assert-VersionParse 'duplicate schema target rejected' ($sample+"`n"+'$DatabaseVersion = ''43''') $false
Assert-VersionParse 'missing version rejected' '$DatabaseVersion = ''42''' $false
Assert-VersionParse 'invalid version rejected' ($sample.Replace('1.2.37','1.02.37')) $false
Assert-VersionParse 'leading-zero schema rejected' ($sample.Replace("'42'","'042'")) $false
Assert-VersionParse 'zero schema rejected' ($sample.Replace("'42'","'0'")) $false
Assert-VersionParse 'overflow schema rejected' ($sample.Replace("'42'","'2147483648'")) $false
# Verify migration safety by executing the current inventory validator
# against throwaway file sets. Never demand yesterday's error-message wording,
# fixture count or schema number.
. (Join-Path $root 'Tools/AssertWorkflow04MigrationInventory.ps1')
$astTokens=$null;$astErrors=$null
$prepAst=[Management.Automation.Language.Parser]::ParseInput($prep,[ref]$astTokens,[ref]$astErrors)
$invocations=@($prepAst.FindAll({
    param($node)
    $node -is [Management.Automation.Language.CommandAst] -and
    $node.GetCommandName() -ceq 'Assert-FmscWorkflow04MigrationInventory'
},$true))
Check ($invocations.Count -eq 1) 'Release preparation must actually call the tested migration inventory policy.'
$scratch=Join-Path ([IO.Path]::GetTempPath()) ('fmsc-w04-migration-inventory-'+[guid]::NewGuid().ToString('N'))
$null=New-Item -Path $scratch -ItemType Directory -Force
function Assert-MigrationOutcome {
    param([string]$Case,[bool]$Expected,[int]$Schema,[string]$Directory)
    $passed=$true
    try { Assert-FmscWorkflow04MigrationInventory -TargetSchema $Schema -Directory $Directory }
    catch { $passed=$false }
    Check ($passed -eq $Expected) "Migration inventory behavior: $Case."
}
try {
    $base=Join-Path $scratch 'base'
    $null=New-Item -Path $base -ItemType Directory
    foreach($v in 2..7) {
        Set-Content -LiteralPath (Join-Path $base "$v.sql") -Value '-- inert migration fixture'
    }
    Assert-MigrationOutcome 'complete historical upgrade remains supported' $true 7 $base
    Assert-MigrationOutcome 'missing migration directory must refuse' $false 7 (Join-Path $scratch 'missing')
    Assert-MigrationOutcome 'retirement-only schema cannot release' $false 8 $base

    $newTarget=[Math]::Min(9999,[Math]::Max(9,($actual.Schema+3)))
    $forward=Join-Path $scratch 'future'
    $null=New-Item -Path $forward -ItemType Directory
    foreach($v in 2..$newTarget) {
        Set-Content -LiteralPath (Join-Path $forward "$v.sql") -Value '-- inert future migration'
    }
    Assert-MigrationOutcome 'complete future schema releases are accepted without pinned ceiling' $true $newTarget $forward
    Assert-MigrationOutcome 'unexpected future migration must refuse older target' $false ($newTarget-1) $forward
    Remove-Item -LiteralPath (Join-Path $forward '5.sql')
    Assert-MigrationOutcome 'missing intermediate migration must block' $false $newTarget $forward
    Set-Content -LiteralPath (Join-Path $forward '5.sql') -Value '-- inert restored migration'
    Set-Content -LiteralPath (Join-Path $forward '05.sql') -Value '-- malformed number'
    Assert-MigrationOutcome 'noncanonical migration filenames must block' $false $newTarget $forward
    Remove-Item -LiteralPath (Join-Path $forward '05.sql')
    Set-Content -LiteralPath (Join-Path $forward 'notes.sql') -Value '-- not a migration'
    Assert-MigrationOutcome 'unexpected SQL file must block' $false $newTarget $forward
    Remove-Item -LiteralPath (Join-Path $forward 'notes.sql')
    Assert-MigrationOutcome 'restored valid chain remains accepted' $true $newTarget $forward
    $actualSchema=$actual.Schema
    Assert-MigrationOutcome 'checked-in migration inventory matches current installer schema' $true $actualSchema (Join-Path $root 'Migrations')
}
finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}

Check ($prep.Contains('channel/stable.json') -and $prep.Contains('Installer/core.ps1') -and
    $prep.Contains('refs/heads/main') -and $prep.Contains('Reusing pending')) 'Preparation must use feed version and preserve retryability.'

Check ($prep.Contains('Resolve-FmscWorkflow04Version') -and
    $prep.Contains('if ($decision.RequiresBump)') -and
    $prep.Contains('Reusing pending Source version')) 'Workflow 04 no longer uses the tested release version decision.'
. (Join-Path $root 'Tools/ResolveWorkflow04Version.ps1')
# No GitHub calls or version-file writes: these are policy decisions only.
$accepted = @(
    @{ Source='1.0.21'; Feed='1.0.21'; Release='1.0.22'; Bump=$true },
    @{ Source='1.0.22'; Feed='1.0.21'; Release='1.0.22'; Bump=$false },
    @{ Source='1.0.24'; Feed='1.0.21'; Release='1.0.24'; Bump=$false },
    @{ Source='1.0.99'; Feed='1.0.21'; Release='1.0.99'; Bump=$false },
    @{ Source='1.0.25'; Feed='1.0.24'; Release='1.0.25'; Bump=$false },
    # The exact owner-entered version makes the next minor/major transition safe.
    @{ Source='1.1.0'; Feed='1.0.99'; Release='1.1.0'; Bump=$false; Approved='1.1.0' },
    @{ Source='1.2.0'; Feed='1.1.27'; Release='1.2.0'; Bump=$false; Approved='1.2.0' },
    @{ Source='2.0.0'; Feed='1.9.25'; Release='2.0.0'; Bump=$false; Approved='2.0.0' }
)
foreach ($case in $accepted) {
    $approved=if ($case.ContainsKey('Approved')) { $case.Approved } else { '' }
    $decision=Resolve-FmscWorkflow04Version -SourceVersion $case.Source -PublishedVersion $case.Feed -ApprovedTransitionVersion $approved
    Check ($decision.ReleaseVersion -ceq $case.Release -and
        $decision.RequiresBump -eq $case.Bump) "Incorrect release preparation for $($case.Feed) -> $($case.Source)."
}
$refused = @(
    @{ Source='1.0.20'; Feed='1.0.21' }, # downgrade
    @{ Source='1.0.23'; Feed='1.0.24' }, # previously published source is now stale
    @{ Source='1.1.0'; Feed='1.0.21' }, # transition never implicitly authorized
    @{ Source='1.1.0'; Feed='1.0.21'; Approved='1.2.0' }, # wrong approval
    @{ Source='1.1.1'; Feed='1.0.21'; Approved='1.1.1' }, # new line must start at patch zero
    @{ Source='1.2.0'; Feed='1.0.21'; Approved='1.2.0' }, # skipped minor
    @{ Source='2.1.0'; Feed='1.9.25'; Approved='2.1.0' }, # major must start at minor zero
    @{ Source='3.0.0'; Feed='1.9.25'; Approved='3.0.0' }, # skipped major
    @{ Source='2.0.1'; Feed='1.0.21'; Approved='2.0.1' }, # nonzero major patch
    @{ Source='0.9.99'; Feed='1.0.21'; Approved='0.9.99' }, # older line
    @{ Source='1.0.22'; Feed='1.0.21'; Approved='1.0.22' }, # stale authorization for patch
    @{ Source='1.0.21'; Feed='1.0.21'; Approved='1.1.0' }, # approval cannot auto-bump a different line
    @{ Source='1.0.021'; Feed='1.0.21' }, # noncanonical
    @{ Source='1.0.bad'; Feed='1.0.21' }, # malformed source
    @{ Source='1.0.24'; Feed='1.0.bad' }, # malformed feed
    @{ Source='1.0.2147483647'; Feed='1.0.2147483647' } # patch overflow
)
foreach ($case in $refused) {
    $approved=if ($case.ContainsKey('Approved')) { $case.Approved } else { '' }
    $rejected=$false
    try {
        $null=Resolve-FmscWorkflow04Version -SourceVersion $case.Source -PublishedVersion $case.Feed -ApprovedTransitionVersion $approved
    } catch { $rejected=$true }
    Check $rejected "Unsafe version change accepted: $($case.Feed) -> $($case.Source) with approval '$approved'."
}

Check ($workflow.Contains('approved_version_transition:') -and
    $workflow.Contains('inputs.approved_version_transition') -and
    $prep.Contains('-ApprovedTransitionVersion $env:FMSC_WORKFLOW04_APPROVED_VERSION')) 'Owner approved transition is not forwarded to the tested version policy.'
Check ($prep.Contains('FMSC_SANDBOX_DISPATCH_TOKEN') -and $prep.Contains('FMSC_UPDATES_PUBLISH_TOKEN')) 'Release secrets are not checked before version commit.'
Check ($dispatch.Contains('fmsc/sandbox-mdf-ldf/v2') -and $dispatch.Contains('previous_status_id=') -and $dispatch.Contains("ref='main'") -and $dispatch.Contains('source_sha=$SourceSha')) 'Dispatch lacks pinned commit/status baseline.'
$gate=Get-Content (Join-Path $root 'Tools/AssertSandboxPublicationGate.ps1') -Raw
Check ($gate.Contains('fmsc/sandbox-mdf-ldf/v2') -and
    $gate.Contains('Installer/core.ps1') -and
    $gate.Contains('$targetSchema') -and
    $gate.Contains('commits/$SourceSha/statuses') -and
    -not $gate.Contains('expectedDescription')) 'Publication gate must validate the checked-out schema and exact commit, not fixed fixture descriptions.'
# Preparation's schema-8 rule is covered by the executed synthetic scenarios above.
# Final publication independently retains its reviewed retirement-only guard.
Check ($gate.Contains('$targetSchema -eq 8')) 'Final publication still blocks retirement-only schema 8.'
Check ($wait.Contains('$PreviousStatusId') -and $wait.Contains('fmsc/sandbox-mdf-ldf/v2') -and
    $wait.Contains('AssertSandboxPublicationGate.ps1')) 'Wait accepts old or untrusted private status.'
Check (-not $wait.Contains('FMSC_SANDBOX_DISPATCH_TOKEN') -and -not $wait.Contains('FMSC_UPDATES_PUBLISH_TOKEN')) 'Sensitive tokens leaked into polling job.'

# Behavioral regression: REST JSON arrays must be individual status records,
# preserving newest-first API order. A prior failure must not mask a new
# success, nor can an older success override a newer failure.
. (Join-Path $root 'Tools/GetGitHubCommitStatuses.ps1')
$script:mockStatusRecords=@(
    [pscustomobject]@{ id=303; context='fmsc/sandbox-mdf-ldf/v2'; state='success' },
    [pscustomobject]@{ id=302; context='fmsc/sandbox-mdf-ldf/v2'; state='failure' },
    [pscustomobject]@{ id=301; context='unrelated/ci'; state='success' }
)
function Invoke-RestMethod {
    param($Uri,$Headers,$Method)
    # Emulate Invoke-RestMethod emitting one object[] instead of three objects.
    Write-Output -NoEnumerate $script:mockStatusRecords
}
try {
    $statuses=@(Get-FmscGitHubCommitStatuses -Uri 'https://example.invalid/statuses' -Headers @{})
    Check ($statuses.Count -eq 3 -and $statuses[0].id -eq 303 -and $statuses[1].id -eq 302) 'REST status array was not enumerated in newest-first order.'
    $latest=@($statuses | Where-Object { $_.context -ceq 'fmsc/sandbox-mdf-ldf/v2' } | Select-Object -First 1)
    Check ($latest.Count -eq 1 -and $latest[0].state -ceq 'success') 'Fresh success was masked by an older failure.'
    $script:mockStatusRecords=@(
        [pscustomobject]@{ id=304; context='fmsc/sandbox-mdf-ldf/v2'; state='failure' },
        [pscustomobject]@{ id=303; context='fmsc/sandbox-mdf-ldf/v2'; state='success' }
    )
    $statuses=@(Get-FmscGitHubCommitStatuses -Uri 'https://example.invalid/statuses' -Headers @{})
    $latest=@($statuses | Where-Object { $_.context -ceq 'fmsc/sandbox-mdf-ldf/v2' } | Select-Object -First 1)
    Check ($latest.Count -eq 1 -and $latest[0].state -ceq 'failure') 'Newer failure must take precedence over older success.'
} finally {
    Remove-Item Function:Invoke-RestMethod -ErrorAction SilentlyContinue
    Remove-Variable mockStatusRecords -Scope Script -ErrorAction SilentlyContinue
}

& (Join-Path $root 'Tools/TestWorkflow04Artifact.ps1')
Write-Output 'PASS: parallel Windows/Sandbox verification, exact-commit artifact integrity and atomic publication wiring.'
) 'Windows verification must depend on the prepared exact Source SHA.'
Check ($package -notmatch 'PublishPrivateUpdate\.ps1|FMSC_UPDATES_PUBLISH_TOKEN') 'Windows build must not be capable of publishing.'
Check ($publisher.Contains('needs: [prepare, sandbox-release-gate, package]')) 'Publisher can bypass Windows verification or fresh private SQL acceptance.'
Check ($publisher.Contains('inputs.publish_update') -and
    $publisher.Contains('actions/download-artifact@v7') -and
    $publisher.Contains('needs.package.outputs.inventory_sha256') -and
    $publisher.Contains('AssertWorkflow04Artifact.ps1')) 'Publisher must verify the producing job artifact and exact release identity.'
Check ($publisher.IndexOf('AssertWorkflow04Artifact.ps1') -lt $publisher.IndexOf('PublishPrivateUpdate.ps1') -and
    $publisher.IndexOf('AssertSandboxPublicationGate.ps1') -lt $publisher.IndexOf('PublishPrivateUpdate.ps1')) 'Publisher must independently reverify artifact and private Sandbox attestation before publication.'
Check ($workflow.Contains('ref: ') -and $workflow.Contains('needs.prepare.outputs.release_sha')) 'Release checkout must use the prepared SHA.'
Check ($workflow.Contains('DispatchSandboxAcceptance.ps1') -and $workflow.Contains('WaitForSandboxAttestation.ps1')) 'Private Sandbox dispatch/wait is absent.'
Check ($workflow.Contains('AssertSandboxPublicationGate.ps1 -SourceSha') -and $workflow.Contains('PublishPrivateUpdate.ps1')) 'No final trusted status recheck before publication.'
Check ($workflow.IndexOf('  prepare:') -gt 0 -and
    $workflow.IndexOf('  prepare:') -lt $workflow.IndexOf('  sandbox-release-gate:') -and
    $workflow.IndexOf('  sandbox-release-gate:') -lt $workflow.IndexOf('  package:')) 'Release job dependency order is unsafe.'
# Execute exactly the parser Workflow 04 uses, with synthetic installer
# values. A refactor of regex formatting or whitespace must not itself block
# releases; invalid, duplicated or noncanonical assignments still do.
. (Join-Path $root 'Tools/ReadWorkflow04SourceVersions.ps1')
$core=Get-Content -LiteralPath (Join-Path $root 'Installer/core.ps1') -Raw
$actual=Read-FmscWorkflow04SourceVersions -InstallerText $core
Check ($actual.AppVersion -match '^[0-9]+\.[0-9]+\.[0-9]+$') 'Current app version is invalid.'
Check ($actual.Schema -ge 7) 'Current schema is below the supported private upgrade baseline.'
$parserAstTokens=$null;$parserAstErrors=$null
$prepParserAst=[Management.Automation.Language.Parser]::ParseInput($prep,[ref]$parserAstTokens,[ref]$parserAstErrors)
$readers=@($prepParserAst.FindAll({
    param($node)
    $node -is [Management.Automation.Language.CommandAst] -and
    $node.GetCommandName() -ceq 'Read-FmscWorkflow04SourceVersions'
},$true))
Check ($readers.Count -eq 1) 'Release preparation must call the tested version parser.'
function Assert-VersionParse {
    param([string]$Case,[string]$Text,[bool]$Expected)
    $passed=$true
    try { $null=Read-FmscWorkflow04SourceVersions -InstallerText $Text }
    catch { $passed=$false }
    Check ($passed -eq $Expected) "Release version parser behavior: $Case."
}
$sample=@(
    '$AppVersion = ''1.2.37'''
    '$DatabaseVersion = ''42'''
) -join "`n"
Assert-VersionParse 'valid future source line' $sample $true
Assert-VersionParse 'whitespace-independent valid source' ($sample -replace ' = ','  =  ') $true
Assert-VersionParse 'duplicate source version rejected' ($sample+"`n"+'$AppVersion = ''1.2.38''') $false
Assert-VersionParse 'duplicate schema target rejected' ($sample+"`n"+'$DatabaseVersion = ''43''') $false
Assert-VersionParse 'missing version rejected' '$DatabaseVersion = ''42''' $false
Assert-VersionParse 'invalid version rejected' ($sample.Replace('1.2.37','1.02.37')) $false
Assert-VersionParse 'leading-zero schema rejected' ($sample.Replace("'42'","'042'")) $false
Assert-VersionParse 'zero schema rejected' ($sample.Replace("'42'","'0'")) $false
Assert-VersionParse 'overflow schema rejected' ($sample.Replace("'42'","'2147483648'")) $false
# Verify migration safety by executing the current inventory validator
# against throwaway file sets. Never demand yesterday's error-message wording,
# fixture count or schema number.
. (Join-Path $root 'Tools/AssertWorkflow04MigrationInventory.ps1')
$astTokens=$null;$astErrors=$null
$prepAst=[Management.Automation.Language.Parser]::ParseInput($prep,[ref]$astTokens,[ref]$astErrors)
$invocations=@($prepAst.FindAll({
    param($node)
    $node -is [Management.Automation.Language.CommandAst] -and
    $node.GetCommandName() -ceq 'Assert-FmscWorkflow04MigrationInventory'
},$true))
Check ($invocations.Count -eq 1) 'Release preparation must actually call the tested migration inventory policy.'
$scratch=Join-Path ([IO.Path]::GetTempPath()) ('fmsc-w04-migration-inventory-'+[guid]::NewGuid().ToString('N'))
$null=New-Item -Path $scratch -ItemType Directory -Force
function Assert-MigrationOutcome {
    param([string]$Case,[bool]$Expected,[int]$Schema,[string]$Directory)
    $passed=$true
    try { Assert-FmscWorkflow04MigrationInventory -TargetSchema $Schema -Directory $Directory }
    catch { $passed=$false }
    Check ($passed -eq $Expected) "Migration inventory behavior: $Case."
}
try {
    $base=Join-Path $scratch 'base'
    $null=New-Item -Path $base -ItemType Directory
    foreach($v in 2..7) {
        Set-Content -LiteralPath (Join-Path $base "$v.sql") -Value '-- inert migration fixture'
    }
    Assert-MigrationOutcome 'complete historical upgrade remains supported' $true 7 $base
    Assert-MigrationOutcome 'missing migration directory must refuse' $false 7 (Join-Path $scratch 'missing')
    Assert-MigrationOutcome 'retirement-only schema cannot release' $false 8 $base

    $newTarget=[Math]::Min(9999,[Math]::Max(9,($actual.Schema+3)))
    $forward=Join-Path $scratch 'future'
    $null=New-Item -Path $forward -ItemType Directory
    foreach($v in 2..$newTarget) {
        Set-Content -LiteralPath (Join-Path $forward "$v.sql") -Value '-- inert future migration'
    }
    Assert-MigrationOutcome 'complete future schema releases are accepted without pinned ceiling' $true $newTarget $forward
    Assert-MigrationOutcome 'unexpected future migration must refuse older target' $false ($newTarget-1) $forward
    Remove-Item -LiteralPath (Join-Path $forward '5.sql')
    Assert-MigrationOutcome 'missing intermediate migration must block' $false $newTarget $forward
    Set-Content -LiteralPath (Join-Path $forward '5.sql') -Value '-- inert restored migration'
    Set-Content -LiteralPath (Join-Path $forward '05.sql') -Value '-- malformed number'
    Assert-MigrationOutcome 'noncanonical migration filenames must block' $false $newTarget $forward
    Remove-Item -LiteralPath (Join-Path $forward '05.sql')
    Set-Content -LiteralPath (Join-Path $forward 'notes.sql') -Value '-- not a migration'
    Assert-MigrationOutcome 'unexpected SQL file must block' $false $newTarget $forward
    Remove-Item -LiteralPath (Join-Path $forward 'notes.sql')
    Assert-MigrationOutcome 'restored valid chain remains accepted' $true $newTarget $forward
    $actualSchema=$actual.Schema
    Assert-MigrationOutcome 'checked-in migration inventory matches current installer schema' $true $actualSchema (Join-Path $root 'Migrations')
}
finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}

Check ($prep.Contains('channel/stable.json') -and $prep.Contains('Installer/core.ps1') -and
    $prep.Contains('refs/heads/main') -and $prep.Contains('Reusing pending')) 'Preparation must use feed version and preserve retryability.'

Check ($prep.Contains('Resolve-FmscWorkflow04Version') -and
    $prep.Contains('if ($decision.RequiresBump)') -and
    $prep.Contains('Reusing pending Source version')) 'Workflow 04 no longer uses the tested release version decision.'
. (Join-Path $root 'Tools/ResolveWorkflow04Version.ps1')
# No GitHub calls or version-file writes: these are policy decisions only.
$accepted = @(
    @{ Source='1.0.21'; Feed='1.0.21'; Release='1.0.22'; Bump=$true },
    @{ Source='1.0.22'; Feed='1.0.21'; Release='1.0.22'; Bump=$false },
    @{ Source='1.0.24'; Feed='1.0.21'; Release='1.0.24'; Bump=$false },
    @{ Source='1.0.99'; Feed='1.0.21'; Release='1.0.99'; Bump=$false },
    @{ Source='1.0.25'; Feed='1.0.24'; Release='1.0.25'; Bump=$false },
    # The exact owner-entered version makes the next minor/major transition safe.
    @{ Source='1.1.0'; Feed='1.0.99'; Release='1.1.0'; Bump=$false; Approved='1.1.0' },
    @{ Source='1.2.0'; Feed='1.1.27'; Release='1.2.0'; Bump=$false; Approved='1.2.0' },
    @{ Source='2.0.0'; Feed='1.9.25'; Release='2.0.0'; Bump=$false; Approved='2.0.0' }
)
foreach ($case in $accepted) {
    $approved=if ($case.ContainsKey('Approved')) { $case.Approved } else { '' }
    $decision=Resolve-FmscWorkflow04Version -SourceVersion $case.Source -PublishedVersion $case.Feed -ApprovedTransitionVersion $approved
    Check ($decision.ReleaseVersion -ceq $case.Release -and
        $decision.RequiresBump -eq $case.Bump) "Incorrect release preparation for $($case.Feed) -> $($case.Source)."
}
$refused = @(
    @{ Source='1.0.20'; Feed='1.0.21' }, # downgrade
    @{ Source='1.0.23'; Feed='1.0.24' }, # previously published source is now stale
    @{ Source='1.1.0'; Feed='1.0.21' }, # transition never implicitly authorized
    @{ Source='1.1.0'; Feed='1.0.21'; Approved='1.2.0' }, # wrong approval
    @{ Source='1.1.1'; Feed='1.0.21'; Approved='1.1.1' }, # new line must start at patch zero
    @{ Source='1.2.0'; Feed='1.0.21'; Approved='1.2.0' }, # skipped minor
    @{ Source='2.1.0'; Feed='1.9.25'; Approved='2.1.0' }, # major must start at minor zero
    @{ Source='3.0.0'; Feed='1.9.25'; Approved='3.0.0' }, # skipped major
    @{ Source='2.0.1'; Feed='1.0.21'; Approved='2.0.1' }, # nonzero major patch
    @{ Source='0.9.99'; Feed='1.0.21'; Approved='0.9.99' }, # older line
    @{ Source='1.0.22'; Feed='1.0.21'; Approved='1.0.22' }, # stale authorization for patch
    @{ Source='1.0.21'; Feed='1.0.21'; Approved='1.1.0' }, # approval cannot auto-bump a different line
    @{ Source='1.0.021'; Feed='1.0.21' }, # noncanonical
    @{ Source='1.0.bad'; Feed='1.0.21' }, # malformed source
    @{ Source='1.0.24'; Feed='1.0.bad' }, # malformed feed
    @{ Source='1.0.2147483647'; Feed='1.0.2147483647' } # patch overflow
)
foreach ($case in $refused) {
    $approved=if ($case.ContainsKey('Approved')) { $case.Approved } else { '' }
    $rejected=$false
    try {
        $null=Resolve-FmscWorkflow04Version -SourceVersion $case.Source -PublishedVersion $case.Feed -ApprovedTransitionVersion $approved
    } catch { $rejected=$true }
    Check $rejected "Unsafe version change accepted: $($case.Feed) -> $($case.Source) with approval '$approved'."
}

Check ($workflow.Contains('approved_version_transition:') -and
    $workflow.Contains('inputs.approved_version_transition') -and
    $prep.Contains('-ApprovedTransitionVersion $env:FMSC_WORKFLOW04_APPROVED_VERSION')) 'Owner approved transition is not forwarded to the tested version policy.'
Check ($prep.Contains('FMSC_SANDBOX_DISPATCH_TOKEN') -and $prep.Contains('FMSC_UPDATES_PUBLISH_TOKEN')) 'Release secrets are not checked before version commit.'
Check ($dispatch.Contains('fmsc/sandbox-mdf-ldf/v2') -and $dispatch.Contains('previous_status_id=') -and $dispatch.Contains("ref='main'") -and $dispatch.Contains('source_sha=$SourceSha')) 'Dispatch lacks pinned commit/status baseline.'
$gate=Get-Content (Join-Path $root 'Tools/AssertSandboxPublicationGate.ps1') -Raw
Check ($gate.Contains('fmsc/sandbox-mdf-ldf/v2') -and
    $gate.Contains('Installer/core.ps1') -and
    $gate.Contains('$targetSchema') -and
    $gate.Contains('commits/$SourceSha/statuses') -and
    -not $gate.Contains('expectedDescription')) 'Publication gate must validate the checked-out schema and exact commit, not fixed fixture descriptions.'
# Preparation's schema-8 rule is covered by the executed synthetic scenarios above.
# Final publication independently retains its reviewed retirement-only guard.
Check ($gate.Contains('$targetSchema -eq 8')) 'Final publication still blocks retirement-only schema 8.'
Check ($wait.Contains('$PreviousStatusId') -and $wait.Contains('fmsc/sandbox-mdf-ldf/v2') -and
    $wait.Contains('AssertSandboxPublicationGate.ps1')) 'Wait accepts old or untrusted private status.'
Check (-not $wait.Contains('FMSC_SANDBOX_DISPATCH_TOKEN') -and -not $wait.Contains('FMSC_UPDATES_PUBLISH_TOKEN')) 'Sensitive tokens leaked into polling job.'

# Behavioral regression: REST JSON arrays must be individual status records,
# preserving newest-first API order. A prior failure must not mask a new
# success, nor can an older success override a newer failure.
. (Join-Path $root 'Tools/GetGitHubCommitStatuses.ps1')
$script:mockStatusRecords=@(
    [pscustomobject]@{ id=303; context='fmsc/sandbox-mdf-ldf/v2'; state='success' },
    [pscustomobject]@{ id=302; context='fmsc/sandbox-mdf-ldf/v2'; state='failure' },
    [pscustomobject]@{ id=301; context='unrelated/ci'; state='success' }
)
function Invoke-RestMethod {
    param($Uri,$Headers,$Method)
    # Emulate Invoke-RestMethod emitting one object[] instead of three objects.
    Write-Output -NoEnumerate $script:mockStatusRecords
}
try {
    $statuses=@(Get-FmscGitHubCommitStatuses -Uri 'https://example.invalid/statuses' -Headers @{})
    Check ($statuses.Count -eq 3 -and $statuses[0].id -eq 303 -and $statuses[1].id -eq 302) 'REST status array was not enumerated in newest-first order.'
    $latest=@($statuses | Where-Object { $_.context -ceq 'fmsc/sandbox-mdf-ldf/v2' } | Select-Object -First 1)
    Check ($latest.Count -eq 1 -and $latest[0].state -ceq 'success') 'Fresh success was masked by an older failure.'
    $script:mockStatusRecords=@(
        [pscustomobject]@{ id=304; context='fmsc/sandbox-mdf-ldf/v2'; state='failure' },
        [pscustomobject]@{ id=303; context='fmsc/sandbox-mdf-ldf/v2'; state='success' }
    )
    $statuses=@(Get-FmscGitHubCommitStatuses -Uri 'https://example.invalid/statuses' -Headers @{})
    $latest=@($statuses | Where-Object { $_.context -ceq 'fmsc/sandbox-mdf-ldf/v2' } | Select-Object -First 1)
    Check ($latest.Count -eq 1 -and $latest[0].state -ceq 'failure') 'Newer failure must take precedence over older success.'
} finally {
    Remove-Item Function:Invoke-RestMethod -ErrorAction SilentlyContinue
    Remove-Variable mockStatusRecords -Scope Script -ErrorAction SilentlyContinue
}

Write-Output 'PASS: one-click exact-commit release preparation, private chaos acceptance and final atomic publication wiring.'
