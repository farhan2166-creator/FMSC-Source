# Fail-closed GitHub repository discovery for Source-owned release tooling.
# No mutable username is an authorization boundary. Private repositories require
# their existing, scope-limited fine-grained credential for metadata resolution.
$script:FmscOwnerId = 316891609
$script:FmscRepoIds = @{ Source = 1412760928; Sandbox = 1449162585; Updates = 1445675367 }
$script:FmscRepoNames = @{ Source = 'FMSC-Source'; Sandbox = 'FMSC-Sandbox'; Updates = 'FMSC-Updates' }

function Assert-FmscWorkflowIdentity {
    param(
        [Parameter(Mandatory)][ValidateSet('Source','Sandbox','Updates')][string]$Repository,
        [switch]$RequireOwnerActor
    )
    if ($env:GITHUB_ACTIONS -cne 'true' -or
        $env:GITHUB_REPOSITORY_ID -cne [string]$script:FmscRepoIds[$Repository] -or
        $env:GITHUB_REPOSITORY_OWNER_ID -cne [string]$script:FmscOwnerId -or
        $env:GITHUB_REPOSITORY -cne ($env:GITHUB_REPOSITORY_OWNER + '/' + $script:FmscRepoNames[$Repository]) -or
        ($RequireOwnerActor -and $env:GITHUB_ACTOR_ID -cne [string]$script:FmscOwnerId)) {
        throw 'Untrusted GitHub repository, account or workflow actor ID.'
    }
}

function Resolve-FmscGitHubRepository {
    param(
        [Parameter(Mandatory)][ValidateSet('Source','Sandbox','Updates')][string]$Repository,
        [string]$Token
    )
    $headers = @{ Accept='application/vnd.github+json'; 'X-GitHub-Api-Version'='2022-11-28' }
    # The immutable user-ID endpoint is public. No secret is needed here.
    $owner = Invoke-RestMethod -Method Get -Uri "https://api.github.com/user/$script:FmscOwnerId" -Headers $headers
    if ([string]$owner.id -cne [string]$script:FmscOwnerId -or
        $owner.type -cne 'User' -or
        $owner.login -cnotmatch '^[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?$') {
        throw 'GitHub account lookup failed permanent-owner verification.'
    }
    $name = $script:FmscRepoNames[$Repository]
    $full = $owner.login + '/' + $name
    if ($Token) { $headers.Authorization = "Bearer $Token" }
    $repo = Invoke-RestMethod -Method Get -Uri "https://api.github.com/repos/$full" -Headers $headers
    if ([string]$repo.id -cne [string]$script:FmscRepoIds[$Repository] -or
        [string]$repo.owner.id -cne [string]$script:FmscOwnerId -or
        $repo.name -cne $name -or
        $repo.full_name -cne $full -or
        (($Repository -ne 'Source') -and ($repo.private -cne $true))) {
        throw 'GitHub repository lookup failed permanent-ID, owner or visibility verification.'
    }
    return [pscustomobject]@{
        Name=$name
        FullName=$full
        Id=$script:FmscRepoIds[$Repository]
        Api="https://api.github.com/repos/$full"
        HttpsRemote="https://github.com/$full.git"
        SshRemote="git@github.com:$full.git"
        Browser="https://github.com/$full"
    }
}
