# Offline only; validate Workflow 04 wiring without publishing or invoking GitHub APIs.
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$workflow=Get-Content (Join-Path $root '.github/workflows/fmsc-windows-download.yml') -Raw
$prep=Get-Content (Join-Path $root 'Tools/PrepareWorkflow04Release.ps1') -Raw
$dispatch=Get-Content (Join-Path $root 'Tools/DispatchSandboxAcceptance.ps1') -Raw
$wait=Get-Content (Join-Path $root 'Tools/WaitForSandboxAttestation.ps1') -Raw
function Check([bool]$Pass,[string]$Failure) { if (-not $Pass) { throw "Workflow04 regression: $Failure" } }
foreach ($name in @('PrepareWorkflow04Release.ps1','DispatchSandboxAcceptance.ps1','WaitForSandboxAttestation.ps1','AssertSandboxPublicationGate.ps1')) {
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
Check ($prep.Contains('FMSC_SANDBOX_DISPATCH_TOKEN') -and $prep.Contains('FMSC_UPDATES_PUBLISH_TOKEN')) 'Release secrets are not checked before version commit.'
Check ($dispatch.Contains('fmsc/sandbox-mdf-ldf/v2') -and $dispatch.Contains('previous_status_id=') -and $dispatch.Contains("ref='main'") -and $dispatch.Contains('source_sha=$SourceSha')) 'Dispatch lacks pinned commit/status baseline.'
$gate=Get-Content (Join-Path $root 'Tools/AssertSandboxPublicationGate.ps1') -Raw
Check ($gate.Contains('fmsc/sandbox-mdf-ldf/v2') -and $gate.Contains('expectedDescription') -and
    $gate.Contains('targetSchema')) 'Publication gate is not bound to the declared migration target.'
Check ($prep.Contains('$targetSchema -ne 8') -and $gate.Contains('$targetSchema -eq 8')) 'Retirement-only schema 8 must be blocked by both release preparation and the final gate.'
Check ($wait.Contains('$PreviousStatusId') -and $wait.Contains('fmsc/sandbox-mdf-ldf/v2') -and
    $wait.Contains('AssertSandboxPublicationGate.ps1')) 'Wait accepts old or untrusted private status.'
Check (-not $wait.Contains('FMSC_SANDBOX_DISPATCH_TOKEN') -and -not $wait.Contains('FMSC_UPDATES_PUBLISH_TOKEN')) 'Sensitive tokens leaked into polling job.'
Write-Output 'PASS: one-click exact-commit release preparation, private chaos acceptance and final atomic publication wiring.'
