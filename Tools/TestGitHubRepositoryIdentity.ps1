# Offline ID resolver contract. No real network, token, update feed or repository write.
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'ResolveFmscGitHubRepository.ps1')
$script:owner='renamed-fmsc-owner'
$script:ownerId=316891609
$script:repoOverride=$null
$script:privateOverride=$null
$script:expectedToken='synthetic-token'
$script:called=@()
function Invoke-RestMethod {
    param([string]$Uri,[hashtable]$Headers,[string]$Method)
    $script:called+= $Uri
    if ($Uri -ceq 'https://api.github.com/user/316891609') {
        if ($Headers.ContainsKey('Authorization')) { throw 'Public account lookup leaked a token.' }
        return [pscustomobject]@{id=$script:ownerId;login=$script:owner;type='User'}
    }
    $match=[regex]::Match($Uri,
        '^https://api[.]github[.]com/repos/renamed-fmsc-owner/(FMSC-Source|FMSC-Sandbox|FMSC-Updates)$')
    if (-not $match.Success) { throw "Unexpected or old-login repository lookup: $Uri" }
    $name=$match.Groups[1].Value
    $id=switch ($name) {
        'FMSC-Source' {1412760928}
        'FMSC-Sandbox' {1449162585}
        'FMSC-Updates' {1445675367}
    }
    if ($name -ne 'FMSC-Source' -and $Headers.Authorization -cne "Bearer $script:expectedToken") {
        throw 'Private metadata lookup omitted its scoped authentication.'
    }
    return [pscustomobject]@{
        id=$(if ($null -ne $script:repoOverride) { $script:repoOverride } else { $id })
        name=$name;full_name="$($script:owner)/$name"
        owner=[pscustomobject]@{id=$script:ownerId}
        private=$(if ($null -ne $script:privateOverride) { $script:privateOverride } else { $name -ne 'FMSC-Source' })
    }
}
function Reject([scriptblock]$Action,[string]$Scenario) {
    $refused=$false
    try { & $Action | Out-Null } catch { $refused=$true }
    if (-not $refused) { throw "Invalid repository accepted: $Scenario" }
    Write-Host "PASS: refuse $Scenario"
}
$source=Resolve-FmscGitHubRepository -Repository Source
$sandbox=Resolve-FmscGitHubRepository -Repository Sandbox -Token $script:expectedToken
$updates=Resolve-FmscGitHubRepository -Repository Updates -Token $script:expectedToken
if ($source.FullName -cne 'renamed-fmsc-owner/FMSC-Source' -or
    $source.Id -ne 1412760928 -or
    $sandbox.Id -ne 1449162585 -or
    $updates.Id -ne 1445675367 -or
    $updates.SshRemote -cne 'git@github.com:renamed-fmsc-owner/FMSC-Updates.git' -or
    $script:called.Count -ne 6) {
    throw 'Simulated owner rename did not resolve all verified private/public repositories.'
}
Write-Host 'PASS: simulated account rename resolves Source, Sandbox and private Updates without redirect.'
$script:ownerId=1
Reject { Resolve-FmscGitHubRepository -Repository Source } 'owner account ID'
$script:ownerId=316891609
$script:repoOverride=1
Reject { Resolve-FmscGitHubRepository -Repository Updates -Token $script:expectedToken } 'repository substitution'
$script:repoOverride=$null
$script:privateOverride=$false
Reject { Resolve-FmscGitHubRepository -Repository Updates -Token $script:expectedToken } 'private distribution becoming public'
$script:privateOverride=$null
$script:owner='invalid/owner'
Reject { Resolve-FmscGitHubRepository -Repository Source } 'unsafe login'
Write-Host 'PASS: immutable ID and private distribution resolver regression.'
