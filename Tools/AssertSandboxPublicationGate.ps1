# This gate runs only in trusted FMSC-Source workflows. Never use private
# Sandbox credentials here. It reads a least-privilege commit status written
# by Sandbox only after BOTH real SQL / Windows installer matrices succeed.
param([string]$SourceSha=$env:GITHUB_SHA)
$ErrorActionPreference='Stop'
if ($env:GITHUB_REPOSITORY -cne 'farhan2166-creator/FMSC-Source' -or
    $env:GITHUB_REF -cne 'refs/heads/main' -or
    $SourceSha -cnotmatch '^[a-f0-9]{40}$') {
    throw 'Sandbox publication attestation is only valid for an exact FMSC-Source main revision.'
}
if (-not $env:GITHUB_TOKEN) { throw 'Required GitHub commit-status read credential is missing.' }
$api='https://api.github.com/repos/farhan2166-creator/FMSC-Source'
$headers=@{
  Authorization="Bearer $env:GITHUB_TOKEN"
  Accept='application/vnd.github+json'
  'X-GitHub-Api-Version'='2022-11-28'
}
$main=Invoke-RestMethod -Uri "$api/branches/main" -Headers $headers -Method Get
if ($main.commit.sha -cne $SourceSha) {
    throw 'main moved since this run began. An attestation for a stale revision cannot publish.'
}
$contexts=@(Invoke-RestMethod -Uri "$api/commits/$SourceSha/statuses?per_page=100" -Headers $headers -Method Get)
# Bind release authorization to the declared schema at this exact Source checkout.
# An old success for a different schema must never authorize publication.
$installer=Get-Content -LiteralPath (Join-Path $PSScriptRoot '../Installer/core.ps1') -Raw
$helper=Get-Content -LiteralPath (Join-Path $PSScriptRoot '../DatabaseHelper.cs') -Raw
$schemaMatch=[regex]::Match($installer,'(?m)^\$DatabaseVersion = ''([0-9]+)''\r?$')
$helperSchema=[regex]::Match($helper,'RequiredDatabaseSchemaVersion\s*=\s*"([0-9]+)"')
if (-not $schemaMatch.Success -or -not $helperSchema.Success -or
    $schemaMatch.Groups[1].Value -cne $helperSchema.Groups[1].Value) {
    throw 'Source installer and application schema must agree before validating Sandbox status.'
}
$targetSchema=[int]$schemaMatch.Groups[1].Value
if ($targetSchema -lt 7 -or $targetSchema -gt 9999) { throw 'Unsupported private release schema target.' }
$expectedDescription="MDF/LDF v3+v7 -> v$targetSchema SQL+installer PASS (fixture 5a8c5130)"
$context='fmsc/sandbox-mdf-ldf/v2'
# GitHub returns newest first. Do not skip a newer failure to find an older
# successful status, even when it was posted by the legitimate attestor.
$latest=@($contexts | Where-Object { $_.context -ceq $context } | Select-Object -First 1)
if ($latest.Count -ne 1) { throw 'Missing private Sandbox real-installer acceptance attestation.' }
$record=$latest[0]
if ($record.state -cne 'success' -or
    $record.creator.login -cne 'farhan2166-creator' -or
    $record.description -cne $expectedDescription -or
    $record.target_url -cnotmatch '^https://github[.]com/farhan2166-creator/FMSC-Sandbox/actions/runs/[0-9]+$') {
    throw 'The latest Sandbox acceptance status was unsuccessful, unauthorized or invalid.'
}
Write-Host "Verified trusted Sandbox SQL + installer acceptance for exact Source SHA $SourceSha"
