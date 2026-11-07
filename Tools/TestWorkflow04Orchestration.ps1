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
# Regression: a literal "$AppVersion" in core.ps1 must not become
# an unescaped regex end-of-line anchor. Check the actual current source.
$core=Get-Content -LiteralPath (Join-Path $root 'Installer/core.ps1') -Raw
$appPattern='(?m)^\$AppVersion = ''([0-9]+\.[0-9]+\.[0-9]+)''\r? -and $prep.Contains('Installer/core.ps1') -and
    $prep.Contains('refs/heads/main') -and $prep.Contains('Reusing pending')) 'Preparation must use feed version and preserve retryability.'
Check ($prep.Contains('FMSC_SANDBOX_DISPATCH_TOKEN') -and $prep.Contains('FMSC_UPDATES_PUBLISH_TOKEN')) 'Release secrets are not checked before version commit.'
Check ($dispatch.Contains('previous_status_id=') -and $dispatch.Contains("ref='main'") -and $dispatch.Contains('source_sha=$SourceSha')) 'Dispatch lacks pinned commit/status baseline.'
Check ($wait.Contains('$PreviousStatusId') -and $wait.Contains('fmsc/sandbox-mdf-ldf/v1') -and
    $wait.Contains('AssertSandboxPublicationGate.ps1')) 'Wait accepts old or untrusted private status.'
Check (-not $wait.Contains('FMSC_SANDBOX_DISPATCH_TOKEN') -and -not $wait.Contains('FMSC_UPDATES_PUBLISH_TOKEN')) 'Sensitive tokens leaked into polling job.'
Write-Output 'PASS: one-click exact-commit release preparation, private chaos acceptance and final atomic publication wiring.'

$schemaPattern='(?m)^\$DatabaseVersion = ''([0-9]+)''\r? -and $prep.Contains('Installer/core.ps1') -and
    $prep.Contains('refs/heads/main') -and $prep.Contains('Reusing pending')) 'Preparation must use feed version and preserve retryability.'
Check ($prep.Contains('FMSC_SANDBOX_DISPATCH_TOKEN') -and $prep.Contains('FMSC_UPDATES_PUBLISH_TOKEN')) 'Release secrets are not checked before version commit.'
Check ($dispatch.Contains('previous_status_id=') -and $dispatch.Contains("ref='main'") -and $dispatch.Contains('source_sha=$SourceSha')) 'Dispatch lacks pinned commit/status baseline.'
Check ($wait.Contains('$PreviousStatusId') -and $wait.Contains('fmsc/sandbox-mdf-ldf/v1') -and
    $wait.Contains('AssertSandboxPublicationGate.ps1')) 'Wait accepts old or untrusted private status.'
Check (-not $wait.Contains('FMSC_SANDBOX_DISPATCH_TOKEN') -and -not $wait.Contains('FMSC_UPDATES_PUBLISH_TOKEN')) 'Sensitive tokens leaked into polling job.'
Write-Output 'PASS: one-click exact-commit release preparation, private chaos acceptance and final atomic publication wiring.'

Check ($prep.Contains('[regex]::Match($core,''(?m)^\$AppVersion') -and
    $prep.Contains('[regex]::Match($core,''(?m)^\$DatabaseVersion')) 'Version parser must match literal dollar-prefixed installer variables.'
Check ([regex]::Matches($core,$appPattern).Count -eq 1 -and
    [regex]::Matches($core,$schemaPattern).Count -eq 1) 'Current Installer/core.ps1 version assignments are not uniquely detectable.'
Check ([regex]::Match($core,$appPattern).Groups[1].Value -match '^[0-9]+\.[0-9]+\.[0-9]+ -and $prep.Contains('Installer/core.ps1') -and
    $prep.Contains('refs/heads/main') -and $prep.Contains('Reusing pending')) 'Preparation must use feed version and preserve retryability.'
Check ($prep.Contains('FMSC_SANDBOX_DISPATCH_TOKEN') -and $prep.Contains('FMSC_UPDATES_PUBLISH_TOKEN')) 'Release secrets are not checked before version commit.'
Check ($dispatch.Contains('previous_status_id=') -and $dispatch.Contains("ref='main'") -and $dispatch.Contains('source_sha=$SourceSha')) 'Dispatch lacks pinned commit/status baseline.'
Check ($wait.Contains('$PreviousStatusId') -and $wait.Contains('fmsc/sandbox-mdf-ldf/v1') -and
    $wait.Contains('AssertSandboxPublicationGate.ps1')) 'Wait accepts old or untrusted private status.'
Check (-not $wait.Contains('FMSC_SANDBOX_DISPATCH_TOKEN') -and -not $wait.Contains('FMSC_UPDATES_PUBLISH_TOKEN')) 'Sensitive tokens leaked into polling job.'
Write-Output 'PASS: one-click exact-commit release preparation, private chaos acceptance and final atomic publication wiring.'
) 'Current app version is not valid semver.'
Check ([regex]::Match($core,$schemaPattern).Groups[1].Value -eq '7') 'Schema-7 chaos fixture no longer matches current production schema.'
Check ($prep.Contains('channel/stable.json') -and $prep.Contains('Installer/core.ps1') -and
    $prep.Contains('refs/heads/main') -and $prep.Contains('Reusing pending')) 'Preparation must use feed version and preserve retryability.'
Check ($prep.Contains('FMSC_SANDBOX_DISPATCH_TOKEN') -and $prep.Contains('FMSC_UPDATES_PUBLISH_TOKEN')) 'Release secrets are not checked before version commit.'
Check ($dispatch.Contains('previous_status_id=') -and $dispatch.Contains("ref='main'") -and $dispatch.Contains('source_sha=$SourceSha')) 'Dispatch lacks pinned commit/status baseline.'
Check ($wait.Contains('$PreviousStatusId') -and $wait.Contains('fmsc/sandbox-mdf-ldf/v1') -and
    $wait.Contains('AssertSandboxPublicationGate.ps1')) 'Wait accepts old or untrusted private status.'
Check (-not $wait.Contains('FMSC_SANDBOX_DISPATCH_TOKEN') -and -not $wait.Contains('FMSC_UPDATES_PUBLISH_TOKEN')) 'Sensitive tokens leaked into polling job.'
Write-Output 'PASS: one-click exact-commit release preparation, private chaos acceptance and final atomic publication wiring.'
