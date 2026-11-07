# Workflow 04: request one new private acceptance for the exact prepared Source commit.
# Dispatch token is restricted to Actions: write on FMSC-Sandbox only.
param([Parameter(Mandatory)][string]$SourceSha)
$ErrorActionPreference='Stop'
if ($env:GITHUB_ACTIONS -cne 'true' -or
    $env:GITHUB_REPOSITORY -cne 'farhan2166-creator/FMSC-Source' -or
    $env:GITHUB_REF -cne 'refs/heads/main' -or
    $SourceSha -cnotmatch '^[a-f0-9]{40}$') {
    throw 'Private Sandbox dispatch only permits an exact Source/main SHA in its owner workflow.'
}
if (-not $env:GITHUB_TOKEN -or -not $env:FMSC_SANDBOX_DISPATCH_TOKEN -or
    -not $env:GITHUB_OUTPUT) { throw 'Missing GitHub status, dispatch credential or output location.' }
$sourceApi='https://api.github.com/repos/farhan2166-creator/FMSC-Source'
$headers=@{Authorization="Bearer $env:GITHUB_TOKEN";Accept='application/vnd.github+json';'X-GitHub-Api-Version'='2022-11-28'}
$main=(Invoke-RestMethod -Uri "$sourceApi/branches/main" -Headers $headers).commit.sha
if ($main -cne $SourceSha) { throw 'Source main moved before Sandbox dispatch. No release is authorized.' }
$statuses=@(Invoke-RestMethod -Uri "$sourceApi/commits/$SourceSha/statuses?per_page=100" -Headers $headers)
$prior=@($statuses | Where-Object { $_.context -ceq 'fmsc/sandbox-mdf-ldf/v1' } | Select-Object -First 1)
$baseline=if ($prior.Count) { [string]$prior[0].id } else { '0' }
if ($baseline -notmatch '^[0-9]+$') { throw 'Unexpected GitHub status identifier.' }
$dispatch=@{ref='main';inputs=@{source_sha=$SourceSha}} | ConvertTo-Json -Depth 5 -Compress
$privateHeaders=@{Authorization="Bearer $env:FMSC_SANDBOX_DISPATCH_TOKEN";Accept='application/vnd.github+json';'X-GitHub-Api-Version'='2022-11-28'}
# The endpoint returns 204 if dispatched, not a workflow-run ID.
Invoke-RestMethod -Uri 'https://api.github.com/repos/farhan2166-creator/FMSC-Sandbox/actions/workflows/private-release-acceptance.yml/dispatches' -Method Post -Headers $privateHeaders -ContentType 'application/json' -Body $dispatch | Out-Null
"previous_status_id=$baseline" | Add-Content -LiteralPath $env:GITHUB_OUTPUT
Write-Host "Dispatched private SQL, real installer and chaos acceptance for exact Source SHA $SourceSha."
Write-Host 'No private fixture, SQL contents, Sandbox read token or publication token was transferred.'
