# This gate runs only in trusted FMSC-Source workflows. Never use private
# Sandbox credentials here. It reads a least-privilege commit status written
# by Sandbox only after BOTH real SQL / Windows installer matrices succeed.
param([string]$SourceSha=$env:GITHUB_SHA)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'ResolveFmscGitHubRepository.ps1')
Assert-FmscWorkflowIdentity -Repository Source
if ($env:GITHUB_REPOSITORY_ID -cne '1412760928' -or
    $env:GITHUB_REF -cne 'refs/heads/main' -or
    $SourceSha -cnotmatch '^[a-f0-9]{40}$') {
    throw 'Sandbox publication attestation is only valid for an exact FMSC-Source main revision.'
}
if (-not $env:GITHUB_TOKEN) { throw 'Required GitHub commit-status read credential is missing.' }
$sourceRepo=Resolve-FmscGitHubRepository -Repository Source -Token $env:GITHUB_TOKEN
$api=$sourceRepo.Api
$headers=@{
  Authorization="Bearer $env:GITHUB_TOKEN"
  Accept='application/vnd.github+json'
  'X-GitHub-Api-Version'='2022-11-28'
}
$main=Invoke-RestMethod -Uri "$api/branches/main" -Headers $headers -Method Get
if ($main.commit.sha -cne $SourceSha) {
    throw 'main moved since this run began. An attestation for a stale revision cannot publish.'
}
. (Join-Path $PSScriptRoot 'GetGitHubCommitStatuses.ps1')
$contexts=@(Get-FmscGitHubCommitStatuses -Uri "$api/commits/$SourceSha/statuses?per_page=100" -Headers $headers)
# Bind release authorization to the declared schema at this exact Source checkout.
# An old success for a different schema must never authorize publication.
$installer=Get-Content -LiteralPath (Join-Path $PSScriptRoot '../Installer/core.ps1') -Raw
$schemaMatch=[regex]::Match($installer,'(?m)^\$DatabaseVersion = ''([0-9]+)''\r?$')
if (-not $schemaMatch.Success) {
    throw 'Required database schema could not be resolved from Installer/core.ps1.'
}
$targetSchema=[int]$schemaMatch.Groups[1].Value
if ($targetSchema -lt 7 -or $targetSchema -gt 9999) { throw 'Unsupported private release schema target.' }
if ($targetSchema -eq 8) { throw 'Schema 8 is retirement-only. No intermediate Communication Center-free release.' }
$expectedDescription="MDF/LDF v3+v7 -> v$targetSchema SQL+installer PASS (fixture 5a8c5130)"
$context='fmsc/sandbox-mdf-ldf/v2'
# GitHub returns newest first. Do not skip a newer failure to find an older
# successful status, even when it was posted by the legitimate attestor.
$latest=@($contexts | Where-Object { $_.context -ceq $context } | Select-Object -First 1)
if ($latest.Count -ne 1) { throw 'Missing private Sandbox real-installer acceptance attestation.' }
$record=$latest[0]
# A status is trusted only from the permanent account ID and a run URL on
# its current verified owner namespace; never follow a legacy URL redirect.
$trustedRunPrefix='https://github.com/' + $sourceRepo.FullName.Split('/')[0] + '/FMSC-Sandbox/actions/runs/'
$trustedRunPattern='^' + [regex]::Escape($trustedRunPrefix) + '[0-9]+$'
if ($record.state -cne 'success' -or
    [string]$record.creator.id -cne '316891609' -or
    $record.description -cne $expectedDescription -or
    $record.target_url -cnotmatch $trustedRunPattern) {
    throw 'The latest Sandbox acceptance status was unsuccessful, unauthorized or invalid.'
}
Write-Host "Verified trusted Sandbox SQL + installer acceptance for exact Source SHA $SourceSha"
