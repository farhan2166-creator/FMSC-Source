# Offline only; validate Workflow 04 wiring without publishing or invoking GitHub APIs.
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$workflow=Get-Content (Join-Path $root '.github/workflows/fmsc-windows-download.yml') -Raw
$prep=Get-Content (Join-Path $root 'Tools/PrepareWorkflow04Release.ps1') -Raw
$dispatch=Get-Content (Join-Path $root 'Tools/DispatchSandboxAcceptance.ps1') -Raw
$wait=Get-Content (Join-Path $root 'Tools/WaitForSandboxAttestation.ps1') -Raw
function Check([bool]$Pass,[string]$Failure) { if (-not $Pass) { throw "Workflow04 regression: $Failure" } }
foreach ($name in @('PrepareWorkflow04Release.ps1','ResolveWorkflow04Version.ps1','DispatchSandboxAcceptance.ps1','WaitForSandboxAttestation.ps1','AssertSandboxPublicationGate.ps1')) {
    $tokens=$null;$errors=$null
    $null=[Management.Automation.Language.Parser]::ParseFile((Join-Path $root ('Tools/'+$name)),[ref]$tokens,[ref]$errors)
    Check (@($errors).Count -eq 0) "PowerShell parser errors in $name."
}
Check ($workflow -match '(?m)^\s+default: true\s*$') 'Owner publication is not the default.'
Check ($workflow.Contains('needs: [prepare, sandbox-release-gate]')) 'Package can bypass preparation or private SQL acceptance.'
Check ($workflow.Contains('ref: ') -and $workflow.Contains('needs.prepare.outputs.release_sha')) 'Release checkout must use the prepared SHA.'
Check ($workflow.Contains('DispatchSandboxAcceptance.ps1') -and $workflow.Contains('WaitForSandboxAttestation.ps1')) 'Private Sandbox dispatch/wait is absent.'
Check ($workflow.Contains('AssertSandboxPublicationGate.ps1 -SourceSha') -and $workflow.Contains('PublishPrivateUpdate.ps1')) 'No final trusted status recheck before publication.'
Check ($workflow.IndexOf('  prepare:') -gt 0 -and
    $workflow.IndexOf('  prepare:') -lt $workflow.IndexOf('  sandbox-release-gate:') -and
    $workflow.IndexOf('  sandbox-release-gate:') -lt $workflow.IndexOf('  package:')) 'Release job dependency order is unsafe.'
# Release version parsing must match the literal "$AppVersion" and
# "$DatabaseVersion" variables in the existing installer.
$core=Get-Content -LiteralPath (Join-Path $root 'Installer/core.ps1') -Raw
$appPattern='(?m)^\$AppVersion = ''([0-9]+\.[0-9]+\.[0-9]+)''\r?$'
$schemaPattern='(?m)^\$DatabaseVersion = ''([0-9]+)''\r?$'
$appParserMarker='[regex]::Match($core,'+[char]39+'(?m)^\'+[char]36+'AppVersion'
$schemaParserMarker='[regex]::Match($core,'+[char]39+'(?m)^\'+[char]36+'DatabaseVersion'
Check ($prep.Contains($appParserMarker) -and $prep.Contains($schemaParserMarker)) 'Parser must use literal-dollar version matching.'
Check ([regex]::Matches($core,$appPattern).Count -eq 1 -and
    [regex]::Matches($core,$schemaPattern).Count -eq 1) 'Current installer version assignments are not uniquely detectable.'
Check ([regex]::Match($core,$appPattern).Groups[1].Value -match '^[0-9]+\.[0-9]+\.[0-9]+$') 'Current app version is invalid.'
Check ([int][regex]::Match($core,$schemaPattern).Groups[1].Value -ge 7) 'Schema is below frozen private schema-7 baseline.'
Check ($prep.Contains('Missing consecutive migration') -and $prep.Contains('outside the declared target schema')) 'Migration inventory fail-closed preflight missing.'
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
    @{ Source='1.0.25'; Feed='1.0.24'; Release='1.0.25'; Bump=$false }
)
foreach ($case in $accepted) {
    $decision=Resolve-FmscWorkflow04Version -SourceVersion $case.Source -PublishedVersion $case.Feed
    Check ($decision.ReleaseVersion -ceq $case.Release -and
        $decision.RequiresBump -eq $case.Bump) "Incorrect release preparation for $($case.Feed) -> $($case.Source)."
}
$refused = @(
    @{ Source='1.0.20'; Feed='1.0.21' }, # downgrade
    @{ Source='1.0.23'; Feed='1.0.24' }, # previously published source is now stale
    @{ Source='1.1.0'; Feed='1.0.21' }, # different minor
    @{ Source='2.0.1'; Feed='1.0.21' }, # different major
    @{ Source='0.9.99'; Feed='1.0.21' }, # older release line
    @{ Source='1.0.021'; Feed='1.0.21' }, # noncanonical
    @{ Source='1.0.bad'; Feed='1.0.21' }, # malformed source
    @{ Source='1.0.24'; Feed='1.0.bad' }, # malformed feed
    @{ Source='1.0.2147483647'; Feed='1.0.2147483647' } # patch overflow
)
foreach ($case in $refused) {
    $rejected=$false
    try {
        $null=Resolve-FmscWorkflow04Version -SourceVersion $case.Source -PublishedVersion $case.Feed
    } catch { $rejected=$true }
    Check $rejected "Unsafe version change accepted: $($case.Feed) -> $($case.Source)."
}

Check ($prep.Contains('FMSC_SANDBOX_DISPATCH_TOKEN') -and $prep.Contains('FMSC_UPDATES_PUBLISH_TOKEN')) 'Release secrets are not checked before version commit.'
Check ($dispatch.Contains('fmsc/sandbox-mdf-ldf/v2') -and $dispatch.Contains('previous_status_id=') -and $dispatch.Contains("ref='main'") -and $dispatch.Contains('source_sha=$SourceSha')) 'Dispatch lacks pinned commit/status baseline.'
$gate=Get-Content (Join-Path $root 'Tools/AssertSandboxPublicationGate.ps1') -Raw
Check ($gate.Contains('fmsc/sandbox-mdf-ldf/v2') -and $gate.Contains('expectedDescription') -and
    $gate.Contains('targetSchema')) 'Publication gate is not bound to the declared migration target.'
Check ($prep.Contains('$targetSchema -ne 8') -and $gate.Contains('$targetSchema -eq 8')) 'Retirement-only schema 8 must be blocked by both release preparation and the final gate.'
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
