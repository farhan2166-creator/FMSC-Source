# Offline regression tests for the *real* Workflow 04 publication gate.
# GitHub REST is mocked locally; no token, database, repository status or update feed is touched.
$ErrorActionPreference = 'Stop'
$gate = Join-Path $PSScriptRoot 'AssertSandboxPublicationGate.ps1'
if (-not (Test-Path -LiteralPath $gate)) { throw 'Publication gate script is missing.' }
$sha = '0123456789abcdef0123456789abcdef01234567'
$otherSha = 'fedcba9876543210fedcba9876543210fedcba98'
$installer=Get-Content -LiteralPath (Join-Path $PSScriptRoot '../Installer/core.ps1') -Raw
$schemaMatch=[regex]::Match($installer,'(?m)^\$DatabaseVersion = ''([0-9]+)''\r?$')
if (-not $schemaMatch.Success) { throw 'Cannot resolve schema for gate regression.' }
$targetSchema=[int]$schemaMatch.Groups[1].Value
$expectedDescription="MDF/LDF v3+v7 -> v$targetSchema SQL+installer PASS (fixture 5a8c5130)"
$original = @{}
foreach ($key in @('GITHUB_REPOSITORY','GITHUB_REF','GITHUB_TOKEN')) {
    $original[$key] = [Environment]::GetEnvironmentVariable($key)
}
$global:__FmscGateMockMain = $sha
$global:__FmscGateMockStatuses = @()
function Invoke-RestMethod {
    param([string]$Uri, [hashtable]$Headers, [string]$Method)
    if ($Uri -eq 'https://api.github.com/repos/farhan2166-creator/FMSC-Source/branches/main') {
        return [pscustomobject]@{commit=[pscustomobject]@{sha=$global:__FmscGateMockMain}}
    }
    if ($Uri -eq "https://api.github.com/repos/farhan2166-creator/FMSC-Source/commits/$sha/statuses?per_page=100") {
        return $global:__FmscGateMockStatuses
    }
    throw "Unsafe unexpected network request: $Uri"
}
function New-MockStatus {
    param(
        [string]$State='success',
        [string]$Creator='farhan2166-creator',
        [string]$Description=$script:expectedDescription,
        [string]$Url='https://github.com/farhan2166-creator/FMSC-Sandbox/actions/runs/42368731562'
    )
    return [pscustomobject]@{
        context='fmsc/sandbox-mdf-ldf/v2'
        state=$State
        creator=[pscustomobject]@{login=$Creator}
        description=$Description
        target_url=$Url
    }
}
function Assert-Outcome {
    param([string]$Case,[bool]$ShouldPass)
    $passed=$false
    $reason=''
    try { & $gate -SourceSha $sha | Out-Null; $passed=$true } catch { $reason=$_.Exception.Message }
    if ($passed -ne $ShouldPass) {
        throw "Publication gate regression: $Case (actual pass=$passed, expected=$ShouldPass; reason=$reason)"
    }
    Write-Host "PASS: $Case"
}
try {
    $env:GITHUB_REPOSITORY='farhan2166-creator/FMSC-Source'
    $env:GITHUB_REF='refs/heads/main'
    $env:GITHUB_TOKEN='synthetic-token-never-used-for-network'
    $global:__FmscGateMockStatuses=@()
    Assert-Outcome 'missing attestation blocks' $false

    $global:__FmscGateMockStatuses=@((New-MockStatus -State 'failure'),(New-MockStatus))
    Assert-Outcome 'newer failure cannot be bypassed by older success' $false

    $global:__FmscGateMockStatuses=@(New-MockStatus -Creator 'untrusted-actor')
    Assert-Outcome 'untrusted status author blocks' $false

    $global:__FmscGateMockStatuses=@(New-MockStatus -Description 'not accepted')
    Assert-Outcome 'incorrect acceptance description blocks' $false

    $global:__FmscGateMockStatuses=@(New-MockStatus -Description "MDF/LDF v3+v7 -> v$($targetSchema+1) SQL+installer PASS (fixture 5a8c5130)")
    Assert-Outcome 'success for different target schema blocks' $false

    $global:__FmscGateMockStatuses=@(New-MockStatus)
    $global:__FmscGateMockStatuses[0].context='fmsc/sandbox-mdf-ldf/v1'
    Assert-Outcome 'legacy schema-unaware attestation blocks' $false

    $global:__FmscGateMockStatuses=@(New-MockStatus -Url 'https://example.invalid/forged')
    Assert-Outcome 'untrusted run target blocks' $false

    $global:__FmscGateMockStatuses=@(New-MockStatus)
    $global:__FmscGateMockMain=$otherSha
    Assert-Outcome 'stale main revision blocks' $false

    $global:__FmscGateMockMain=$sha
    $env:GITHUB_REF='refs/heads/feature/untrusted'
    Assert-Outcome 'non-main ref blocks' $false
    $env:GITHUB_REF='refs/heads/main'

    $env:GITHUB_TOKEN=''
    Assert-Outcome 'missing token blocks' $false
    $env:GITHUB_TOKEN='synthetic-token-never-used-for-network'

    $global:__FmscGateMockStatuses=@((New-MockStatus),(New-MockStatus -State 'failure'))
    Assert-Outcome 'newest valid trusted status allows build-only gate' $true
} finally {
    foreach ($key in $original.Keys) {
        [Environment]::SetEnvironmentVariable($key,$original[$key])
    }
    Remove-Variable -Name __FmscGateMockMain,__FmscGateMockStatuses -Scope Global -ErrorAction SilentlyContinue
}
