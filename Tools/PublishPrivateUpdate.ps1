# Owner-triggered GitHub Actions orchestration. Reuses the existing packager,
# verifier, two-version snapshot builder and atomic Git publisher.
param([Parameter(Mandatory=$true)][string]$ApplicationDirectory,
      [Parameter(Mandatory=$true)][string]$WorkDirectory)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
. (Join-Path $PSScriptRoot 'ResolveFmscGitHubRepository.ps1')
Assert-FmscWorkflowIdentity -Repository Source -RequireOwnerActor
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:GITHUB_EVENT_NAME -ne 'workflow_dispatch' -or
    $env:GITHUB_REF -ne 'refs/heads/main' -or $env:GITHUB_REPOSITORY_ID -ne '1412760928') {
    throw 'Publication is permitted only from the owner-triggered source main workflow.'
}
if (-not $env:FMSC_UPDATES_PUBLISH_TOKEN) { throw 'Configure the owner-only FMSC_UPDATES_PUBLISH_TOKEN repository secret.' }
if ($env:GITHUB_ACTOR_ID -notmatch '^\d+$' -or $env:GITHUB_ACTOR -notmatch '^[A-Za-z0-9-]+$') {
    throw 'Missing verified GitHub actor identity.'
}
if (Test-Path -LiteralPath $WorkDirectory) { throw 'Publication work directory must be new.' }
New-Item -ItemType Directory -Path $WorkDirectory | Out-Null
$app = [IO.Path]::GetFullPath($ApplicationDirectory)
$work = [IO.Path]::GetFullPath($WorkDirectory)
$packagesProject = Join-Path $PSScriptRoot 'UpdatePackages/UpdatePackages.csproj'
$distributionProject = Join-Path $PSScriptRoot 'UpdateDistribution/UpdateDistribution.csproj'
function Invoke-Checked([string]$File, [string[]]$Arguments) {
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$File failed. The distribution was not deliberately advanced by this step." }
}
function Package([string[]]$Arguments) {
    Invoke-Checked 'dotnet' (@('run','--project',$packagesProject,'-c','Release','--') + $Arguments)
}
function Distribution([string[]]$Arguments) {
    Invoke-Checked 'dotnet' (@('run','--project',$distributionProject,'-c','Release','--') + $Arguments)
}
$encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('x-access-token:' + $env:FMSC_UPDATES_PUBLISH_TOKEN))
Write-Output "::add-mask::$encoded"
# No token in URLs, Git configuration files, package artifacts or the application.
$saved = @{}
foreach ($key in @('GIT_CONFIG_COUNT','GIT_CONFIG_KEY_0','GIT_CONFIG_VALUE_0','GIT_TERMINAL_PROMPT')) {
    $saved[$key] = [Environment]::GetEnvironmentVariable($key)
}
try {
    $env:GIT_CONFIG_COUNT = '1'
    $env:GIT_CONFIG_KEY_0 = 'http.https://github.com/.extraheader'
    $env:GIT_CONFIG_VALUE_0 = 'AUTHORIZATION: basic ' + $encoded
    $env:GIT_TERMINAL_PROMPT = '0'
    $remote = (Resolve-FmscGitHubRepository -Repository Updates -Token $env:FMSC_UPDATES_PUBLISH_TOKEN).HttpsRemote
    $previous = Join-Path $work 'previous'
    Invoke-Checked 'git' @('clone','--quiet','--branch','main','--single-branch',$remote,$previous)
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $app 'FMSC_SMS.dll')).ProductVersion.Split('+')[0]
    $installer = Get-Content -LiteralPath (Join-Path $app 'UpdateSupport/core.ps1') -Raw
    $schemaMatch = [regex]::Match($installer, '(?m)^\$DatabaseVersion = ''([0-9]+)''\r?$')
    if (-not $schemaMatch.Success) { throw 'Cannot determine required database schema from the actual installer.' }
    $schema = $schemaMatch.Groups[1].Value
    $baselineArgs = @()
    $previousSnapshotArgs = @()
    $baseline = $null
    $oldPointer = $null
    $pointerPath = Join-Path $previous 'channel/stable.json'
    if (Test-Path -LiteralPath $pointerPath) {
        $oldPointer = Get-Content -LiteralPath $pointerPath -Raw | ConvertFrom-Json
        if ($oldPointer.format -ne 'fmsc-git-updates/v1' -or
            $oldPointer.latestVersion -notmatch '^\d+\.\d+\.\d+(?:-[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*)?$') {
            throw 'Invalid currently published channel.'
        }
        $oldRelease = Join-Path $previous ('versions/' + $oldPointer.latestVersion)
        $oldManifest = Get-Content -LiteralPath (Join-Path $oldRelease 'update-manifest.json') -Raw | ConvertFrom-Json
        if ($oldManifest.version -ne $oldPointer.latestVersion) { throw 'Published baseline does not match the channel.' }
        if ($version -eq $oldManifest.version) { throw "Version $version is already published. Bump the project and installer version for a new release." }
        $previousSnapshotArgs = @('--previous-snapshot-dir',$previous)
        if ([int]$oldManifest.requiredDatabaseSchema -eq [int]$schema) {
            $extraction = Join-Path $work 'verified-baseline'
            Package @('--extract-release',$oldRelease,'--output-dir',$extraction)
            $baseline = Join-Path $extraction 'application'
            $baselineArgs = @('--previous-dir',$baseline,'--previous-version',$oldManifest.version)
        }
    }
    $package = Join-Path $work 'new-package'
    Package (@('--current-dir',$app,'--output-dir',$package,'--version',$version,'--schema',$schema) + $baselineArgs)
    $verifyArgs = @('--verify-package',$package,'--output-dir',(Join-Path $work 'verified-target'))
    if ($baseline) { $verifyArgs += @('--baseline-dir',$baseline) }
    Package $verifyArgs
    $snapshot = Join-Path $work 'snapshot'
    Distribution (@('--new-package-dir',$package,'--output-dir',$snapshot) + $previousSnapshotArgs)
    $publication = Join-Path $work 'publication'
    Distribution @('--prepare-publication',$snapshot,'--workspace',$publication)
    Invoke-Checked 'git' @('-C',$publication,'config','user.name',$env:GITHUB_ACTOR)
    Invoke-Checked 'git' @('-C',$publication,'config','user.email',($env:GITHUB_ACTOR_ID + '+' + $env:GITHUB_ACTOR + '@users.noreply.github.com'))
    $expected = (Get-Content -LiteralPath (Join-Path $publication '.git/fmsc-expected-parent') -Raw).Trim()
    $manifest = Get-Content -LiteralPath (Join-Path $package 'update-manifest.json') -Raw | ConvertFrom-Json
    $retired = if ($oldPointer -and $oldPointer.previousVersion) { $oldPointer.previousVersion } else { 'None' }
    $prior = if ($oldPointer) { $oldPointer.latestVersion } else { 'None' }
    $method = if ($manifest.incremental) { 'Full and incremental' } else { 'Full only' }
    $plan = @(
        '## Verified private update publication',
        "- New version: $version; required schema: $schema",
        "- Packages: $method",
        "- Full download: $($manifest.full.length) bytes",
        "- Incremental download: $(if ($manifest.incremental) { $manifest.incremental.length } else { 'unavailable' }) bytes",
        "- Previous version retained: $prior",
        "- Retired from current tree: $retired",
        "- Expected distribution parent: $expected",
        '- All chunks, ZIPs and target application files verified before publication.',
        '- Git history is preserved. No school database or credentials are included.'
    )
    $plan | Write-Output
    if ($env:GITHUB_STEP_SUMMARY) { $plan | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY }
    # This is the only write to distribution main: one non-forced Git ref update.
    Distribution @('--publish-reviewed',$publication,'--expected-parent',$expected)
    $published = (& git -C $publication rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read publication receipt.' }
    $remoteHead = & git -C $publication ls-remote origin refs/heads/main
    if ($LASTEXITCODE -ne 0 -or ($remoteHead -split '\s+')[0] -ne $published) {
        throw 'Push finished, but branch verification failed. Inspect the distribution before retrying.'
    }
    Write-Output "Published $version atomically: $published"
    if ($env:GITHUB_STEP_SUMMARY) { "- Published commit: $published" | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY }
}
finally {
    foreach ($key in $saved.Keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key]) }
    $encoded = $null
}
