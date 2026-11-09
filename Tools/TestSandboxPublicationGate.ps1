# Offline regression tests for the real Workflow 04 exact-commit publication gate.
# REST calls are deterministic mocks. No real token, status, fixture or feed is touched.
$ErrorActionPreference='Stop'
$gate=Join-Path $PSScriptRoot 'AssertSandboxPublicationGate.ps1'
$sha='0123456789abcdef0123456789abcdef01234567'
$otherSha='fedcba9876543210fedcba9876543210fedcba98'
$installer=Get-Content (Join-Path $PSScriptRoot '../Installer/core.ps1') -Raw
$match=[regex]::Match($installer,'(?m)^\$DatabaseVersion = ''([0-9]+)''\r?$')
if (-not $match.Success) { throw 'No schema to test acceptance contract.' }
$targetSchema=[int]$match.Groups[1].Value
$expectedDescription="MDF/LDF v3+v7 -> v$targetSchema SQL+installer PASS (fixture 5a8c5130)"
$global:__FmscMockLogin='future-renamed-owner'
$owner=$global:__FmscMockLogin
$original=@{}
foreach ($key in @('GITHUB_ACTIONS','GITHUB_REPOSITORY','GITHUB_REPOSITORY_ID',
    'GITHUB_REPOSITORY_OWNER','GITHUB_REPOSITORY_OWNER_ID','GITHUB_REF','GITHUB_TOKEN')) {
    $original[$key]=[Environment]::GetEnvironmentVariable($key)
}
$global:__FmscMockMain=$sha
$global:__FmscMockOwnerId='316891609'
$global:__FmscMockRepoId='1412760928'
$global:__FmscMockStatuses=@()
function Invoke-RestMethod {
    param([string]$Uri,[hashtable]$Headers,[string]$Method)
    if ($Uri -ceq 'https://api.github.com/user/316891609') {
        return [pscustomobject]@{id=$global:__FmscMockOwnerId;type='User';login=$global:__FmscMockLogin}
    }
    if ($Uri -ceq ('https://api.github.com/repos/'+$global:__FmscMockLogin+'/FMSC-Source')) {
        return [pscustomobject]@{
            id=$global:__FmscMockRepoId;name='FMSC-Source';
            full_name=$global:__FmscMockLogin+'/FMSC-Source';private=$false;
            owner=[pscustomobject]@{id=$global:__FmscMockOwnerId}
        }
    }
    if ($Uri -ceq ('https://api.github.com/repos/'+$global:__FmscMockLogin+'/FMSC-Source/branches/main')) {
        return [pscustomobject]@{commit=[pscustomobject]@{sha=$global:__FmscMockMain}}
    }
    if ($Uri -ceq ('https://api.github.com/repos/'+$global:__FmscMockLogin+"/FMSC-Source/commits/$sha/statuses?per_page=100")) {
        return $global:__FmscMockStatuses
    }
    throw "Unexpected or obsolete GitHub URL in gate: $Uri"
}
function New-Status {
    param(
        [string]$State='success',
        [string]$CreatorId='316891609',
        [string]$Description=$script:expectedDescription,
        [string]$Url=('https://github.com/'+$global:__FmscMockLogin+'/FMSC-Sandbox/actions/runs/42368731562')
    )
    return [pscustomobject]@{
        context='fmsc/sandbox-mdf-ldf/v2';state=$State
        creator=[pscustomobject]@{id=$CreatorId;login=$global:__FmscMockLogin}
        description=$Description;target_url=$Url
    }
}
function Assert-Outcome([string]$Case,[bool]$Expected) {
    $passed=$false;$reason=''
    try { & $gate -SourceSha $sha | Out-Null;$passed=$true }
    catch { $reason=$_.Exception.Message }
    if ($passed -ne $Expected) {
        throw "Sandbox publication gate test failed: $Case (pass=$passed; reason=$reason)"
    }
    Write-Host "PASS: $Case"
}
try {
    $env:GITHUB_ACTIONS='true'
    $env:GITHUB_REPOSITORY=$owner+'/FMSC-Source'
    $env:GITHUB_REPOSITORY_ID='1412760928'
    $env:GITHUB_REPOSITORY_OWNER=$owner
    $env:GITHUB_REPOSITORY_OWNER_ID='316891609'
    $env:GITHUB_REF='refs/heads/main'
    $env:GITHUB_TOKEN='offline-synthetic-only'

    Assert-Outcome 'missing acceptance blocks' $false
    $global:__FmscMockStatuses=@((New-Status -State 'failure'),(New-Status))
    Assert-Outcome 'newest failure defeats older success' $false
    $global:__FmscMockStatuses=@(New-Status -CreatorId '1')
    Assert-Outcome 'untrusted creator numeric ID blocks' $false
    $global:__FmscMockStatuses=@(New-Status -Description 'forged acceptance')
    Assert-Outcome 'incorrect exact acceptance description blocks' $false
    $global:__FmscMockStatuses=@(New-Status -Description "MDF/LDF v3+v7 -> v$($targetSchema+1) SQL+installer PASS (fixture 5a8c5130)")
    Assert-Outcome 'old schema acceptance blocks' $false
    $global:__FmscMockStatuses=@(New-Status)
    $global:__FmscMockStatuses[0].context='fmsc/sandbox-mdf-ldf/v1'
    Assert-Outcome 'legacy context blocks' $false
    $global:__FmscMockStatuses=@(New-Status -Url 'https://github.com/previous-login/FMSC-Sandbox/actions/runs/42368731562')
    Assert-Outcome 'old-owner run URL does not silently redirect' $false
    $global:__FmscMockStatuses=@(New-Status -Url 'https://example.invalid/forged')
    Assert-Outcome 'foreign run URL blocks' $false

    $global:__FmscMockStatuses=@(New-Status)
    $global:__FmscMockMain=$otherSha
    Assert-Outcome 'Source main moved blocks' $false
    $global:__FmscMockMain=$sha
    $env:GITHUB_REPOSITORY_ID='1'
    Assert-Outcome 'wrong Source repository ID blocks' $false
    $env:GITHUB_REPOSITORY_ID='1412760928'
    $env:GITHUB_REPOSITORY_OWNER_ID='1'
    Assert-Outcome 'wrong owner ID blocks' $false
    $env:GITHUB_REPOSITORY_OWNER_ID='316891609'
    $global:__FmscMockOwnerId='1'
    Assert-Outcome 'account lookup substituted blocks' $false
    $global:__FmscMockOwnerId='316891609'
    $global:__FmscMockRepoId='1'
    Assert-Outcome 'same-name substitute repository ID blocks' $false
    $global:__FmscMockRepoId='1412760928'
    $env:GITHUB_REF='refs/heads/untrusted'
    Assert-Outcome 'non-main branch blocks' $false
    $env:GITHUB_REF='refs/heads/main'
    $env:GITHUB_TOKEN=''
    Assert-Outcome 'missing token blocks' $false
    $env:GITHUB_TOKEN='offline-synthetic-only'
    $global:__FmscMockStatuses=@((New-Status),(New-Status -State 'failure'))
    Assert-Outcome 'renamed owner and exact current acceptance work without old login' ($targetSchema -ne 8)
}
finally {
    foreach ($key in $original.Keys) { [Environment]::SetEnvironmentVariable($key,$original[$key]) }
    Remove-Variable -Name __FmscMockLogin,__FmscMockMain,__FmscMockOwnerId,__FmscMockRepoId,__FmscMockStatuses -Scope Global -ErrorAction SilentlyContinue
}
