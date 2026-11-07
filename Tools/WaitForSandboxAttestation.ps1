# Workflow 04: wait for a NEW trusted Sandbox attestation before any packaging.
# No Sandbox credential or private Updates publication token is in this job.
param(
    [Parameter(Mandatory)][string]$SourceSha,
    [Parameter(Mandatory)][string]$PreviousStatusId,
    [int]$MaxMinutes=105
)
$ErrorActionPreference='Stop'
if ($env:GITHUB_ACTIONS -cne 'true' -or
    $env:GITHUB_REPOSITORY -cne 'farhan2166-creator/FMSC-Source' -or
    $env:GITHUB_REF -cne 'refs/heads/main' -or
    $SourceSha -cnotmatch '^[a-f0-9]{40}$' -or
    $PreviousStatusId -notmatch '^[0-9]+$' -or
    $MaxMinutes -lt 1 -or $MaxMinutes -gt 110 -or
    -not $env:GITHUB_TOKEN) {
    throw 'Private Sandbox acceptance wait requires exact trusted Source main, token and bounded timeout.'
}
$headers=@{Authorization="Bearer $env:GITHUB_TOKEN";Accept='application/vnd.github+json';'X-GitHub-Api-Version'='2022-11-28'}
$api='https://api.github.com/repos/farhan2166-creator/FMSC-Source'
$deadline=(Get-Date).AddMinutes($MaxMinutes)
$attempt=0
do {
    $attempt++
    $head=(Invoke-RestMethod -Uri "$api/branches/main" -Headers $headers).commit.sha
    if ($head -cne $SourceSha) { throw 'Source/main moved during SQL/installer chaos acceptance; release refused.' }
    $records=@(Invoke-RestMethod -Uri "$api/commits/$SourceSha/statuses?per_page=100" -Headers $headers)
    $matching=@($records | Where-Object { $_.context -ceq 'fmsc/sandbox-mdf-ldf/v1' } | Select-Object -First 1)
    if ($matching.Count -eq 1 -and [string]$matching[0].id -cne $PreviousStatusId) {
        $attestation=$matching[0]
        if ($attestation.state -cne 'success') {
            if ($attestation.state -in @('failure','error')) {
                throw "New private SQL/installer/chaos acceptance returned $($attestation.state); publishing blocked."
            }
        } else {
            # The existing strict gate verifies creator, exact description, Source
            # HEAD, trusted private Sandbox run URL and newest context semantics.
            # PowerShell script failures throw under ErrorActionPreference=Stop.
            # LASTEXITCODE applies to native programs, not this script.
            & (Join-Path $PSScriptRoot 'AssertSandboxPublicationGate.ps1') -SourceSha $SourceSha
            Write-Host "A NEW trusted private acceptance approved $SourceSha."
            if ($env:GITHUB_STEP_SUMMARY) {
                @(
                    '## Private SQL and chaos acceptance',
                    "- Accepted Source SHA: $SourceSha",
                    "- Run: $($attestation.target_url)",
                    '- Schema-3 + schema-7 SQL and real installers, plus historical chaos and recovery, required.'
                ) | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY
            }
            return
        }
    }
    if (($attempt % 10) -eq 1) {
        Write-Host "Awaiting fresh Sandbox attest for $SourceSha; no publication until successful."
    }
    Start-Sleep -Seconds 30
} while ((Get-Date) -lt $deadline)
throw 'Timed out waiting for a NEW exact-commit Sandbox attestation. Private update feed unchanged; rerun Workflow 04 after investigating Sandbox.'
