# Workflow 04 trusted bootstrap. Run ONLY on owner-dispatched Source/main.
# The sole Source mutation is a one-patch application version bump. No schema
# change, database asset, migration or private feed is modified here.
$ErrorActionPreference='Stop'
$PSNativeCommandUseErrorActionPreference=$false
. (Join-Path $PSScriptRoot 'ResolveFmscGitHubRepository.ps1')
Assert-FmscWorkflowIdentity -Repository Source -RequireOwnerActor
function Require([bool]$Ok,[string]$Reason) { if (-not $Ok) { throw "Workflow 04 refused: $Reason" } }
Require ($env:GITHUB_ACTIONS -ceq 'true' -and
    $env:GITHUB_EVENT_NAME -ceq 'workflow_dispatch' -and
    $env:GITHUB_REF -ceq 'refs/heads/main' -and
    $env:GITHUB_REPOSITORY_ID -ceq '1412760928' -and
    $env:GITHUB_REPOSITORY_OWNER_ID -ceq '316891609' -and
    $env:GITHUB_ACTOR_ID -ceq '316891609') 'Only the owner may manually run Source/main release publication.'
Require ($env:GITHUB_SHA -cmatch '^[0-9a-f]{40}$' -and
    -not [string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) 'Original Source commit or output file is missing.'
Require ($env:FMSC_WORKFLOW04_PUBLISH -cin @('true','false')) 'Invalid workflow publication input.'
Require (-not [string]::IsNullOrWhiteSpace($env:GH_SOURCE_TOKEN)) 'Source contents-write token is missing.'
$publish=$env:FMSC_WORKFLOW04_PUBLISH -ceq 'true'
$sourceRepo=Resolve-FmscGitHubRepository -Repository Source -Token $env:GH_SOURCE_TOKEN
$sourceApi=$sourceRepo.Api
$headers=@{ Authorization="Bearer $env:GH_SOURCE_TOKEN"; Accept='application/vnd.github+json'; 'X-GitHub-Api-Version'='2022-11-28' }
$main=(Invoke-RestMethod -Uri "$sourceApi/branches/main" -Headers $headers).commit.sha
Require ($main -ceq $env:GITHUB_SHA) 'Source/main changed after the workflow was launched.'
$checkout=(& git rev-parse HEAD).Trim()
Require ($LASTEXITCODE -eq 0 -and $checkout -ceq $main) 'Checkout does not match the verified original main commit.'
Require ((@(& git status --porcelain) | Measure-Object).Count -eq 0) 'Source checkout is not clean.'
$origin=(& git remote get-url origin).Trim()
Require ($LASTEXITCODE -eq 0 -and $origin -in @(
    $sourceRepo.HttpsRemote.Substring(0, $sourceRepo.HttpsRemote.Length - 4),
    $sourceRepo.HttpsRemote)) 'Source origin is not the expected GitHub repository.'

$corePath=Join-Path (Get-Location).Path 'Installer/core.ps1'
$core=[IO.File]::ReadAllText($corePath)
$appMatch=[regex]::Match($core,'(?m)^\$AppVersion = ''([0-9]+\.[0-9]+\.[0-9]+)''\r?$')
$dbMatch=[regex]::Match($core,'(?m)^\$DatabaseVersion = ''([0-9]+)''\r?$')
Require ($appMatch.Success -and $dbMatch.Success) 'Current application/schema versions could not be resolved from Installer/core.ps1.'
# Frozen schema-3/7 private fixtures remain unchanged. Sandbox uses copies
# and upgrades them through the declared production migration target.
$targetSchema=[int]$dbMatch.Groups[1].Value
Require ($targetSchema -ge 7 -and $targetSchema -le 9999) 'Schema target is outside the reviewed frozen-fixture migration range.'
Require ($targetSchema -ne 8) 'Schema 8 is retirement-only. Finish and accept schema 9+ before publishing.'
$migrationsDir=Join-Path (Get-Location).Path 'Migrations'
Require (Test-Path -LiteralPath $migrationsDir -PathType Container) 'Source migration directory missing.'
$scriptVersions=@{}
foreach ($file in @(Get-ChildItem -LiteralPath $migrationsDir -Filter '*.sql' -File)) {
    Require ($file.BaseName -cmatch '^[1-9][0-9]*$') "Invalid numbered migration filename: $($file.Name)."
    $v=[int]$file.BaseName
    Require ($v -ge 2 -and $v -le $targetSchema) "Migration $v.sql lies outside the declared target schema $targetSchema."
    Require (-not $scriptVersions.ContainsKey($v)) "Duplicate migration $v.sql."
    $scriptVersions[$v]=$true
}
for ($v=2;$v -le $targetSchema;$v++) {
    Require ($scriptVersions.ContainsKey($v)) "Missing consecutive migration $v.sql for schema $targetSchema."
}
$current=$appMatch.Groups[1].Value
$release=$current
$releaseSha=$checkout
$bumped=$false
if ($publish) {
    Require (-not [string]::IsNullOrWhiteSpace($env:FMSC_UPDATES_PUBLISH_TOKEN) -and
        -not [string]::IsNullOrWhiteSpace($env:FMSC_SANDBOX_DISPATCH_TOKEN)) 'Configure the separate Updates publication and Sandbox Actions-dispatch secrets before bumping the version.'
    $dispatchHeaders=@{ Authorization="Bearer $env:FMSC_SANDBOX_DISPATCH_TOKEN"; Accept='application/vnd.github+json'; 'X-GitHub-Api-Version'='2022-11-28' }
    $sandboxRepo=Resolve-FmscGitHubRepository -Repository Sandbox -Token $env:FMSC_SANDBOX_DISPATCH_TOKEN
    $workflow=Invoke-RestMethod -Headers $dispatchHeaders -Uri ($sandboxRepo.Api + '/actions/workflows/private-release-acceptance.yml')
    Require ($workflow.state -ceq 'active') 'Sandbox acceptance workflow is not accessible and active with the configured dispatch credential.'
    $feedHeaders=@{ Authorization="Bearer $env:FMSC_UPDATES_PUBLISH_TOKEN"; Accept='application/vnd.github+json'; 'X-GitHub-Api-Version'='2022-11-28' }
    $updatesRepo=Resolve-FmscGitHubRepository -Repository Updates -Token $env:FMSC_UPDATES_PUBLISH_TOKEN
    $stable=Invoke-RestMethod -Uri ($updatesRepo.Api + '/contents/channel/stable.json') -Headers $feedHeaders
    Require ($stable.encoding -ceq 'base64') 'Unexpected private update-feed encoding.'
    $channel=([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String(($stable.content -replace '\s','')))) | ConvertFrom-Json
    Require ($channel.format -ceq 'fmsc-git-updates/v1' -and
        $channel.latestVersion -cmatch '^[0-9]+\.[0-9]+\.[0-9]+$') 'Published channel is not the expected stable semantic version.'
    $latest=[version]::Parse($channel.latestVersion)
    Require ($latest.Major -ge 0 -and $latest.Minor -ge 0 -and $latest.Build -ge 0) 'Invalid current feed version.'
    $nextVersion=('{0}.{1}.{2}' -f $latest.Major,$latest.Minor,($latest.Build+1))
    if ($current -ceq $channel.latestVersion) {
        # Preserve CRLF/other source lines and change only one exact assignment.
        Require ([regex]::Matches($core,'(?m)^\$AppVersion = ''([0-9]+\.[0-9]+\.[0-9]+)''\r?$').Count -eq 1) 'Ambiguous installer application-version assignment.'
        $revised=$core.Remove($appMatch.Groups[1].Index,$appMatch.Groups[1].Length).Insert($appMatch.Groups[1].Index,$nextVersion)
        [IO.File]::WriteAllText($corePath,$revised,[Text.UTF8Encoding]::new($false))
        & git add -- Installer/core.ps1
        Require ($LASTEXITCODE -eq 0) 'Could not stage the application version bump.'
        $changes=@(& git diff --cached --name-only)
        Require ($changes.Count -eq 1 -and $changes[0] -ceq 'Installer/core.ps1') 'Version-bump commit includes unexpected files.'
        & git -c 'user.name=github-actions[bot]' -c 'user.email=41898282+github-actions[bot]@users.noreply.github.com' commit -m "release: prepare FMSC $nextVersion (schema $targetSchema)"
        Require ($LASTEXITCODE -eq 0) 'Failed to prepare the new release commit.'
        $releaseSha=(& git rev-parse HEAD).Trim()
        Require ($LASTEXITCODE -eq 0 -and $releaseSha -cmatch '^[0-9a-f]{40}$') 'No valid release-commit SHA.'
        $encoded=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("x-access-token:$env:GH_SOURCE_TOKEN"))
        Write-Host "::add-mask::$encoded"
        & git -c "http.https://github.com/.extraheader=AUTHORIZATION: basic $encoded" push origin HEAD:refs/heads/main
        Require ($LASTEXITCODE -eq 0) 'Version commit could not fast-forward Source/main; feed was not touched. Check branch permissions.'
        $bumped=$true
        $release=$nextVersion
    } elseif ($current -ceq $nextVersion) {
        # Failed release run may leave its reviewed Source version commit.
        # Reuse it without bumping again. Publication still requires fresh chaos
        # and real-installer acceptance for this exact Source commit.
        $release=$current
        Write-Host "Reusing pending $release after an earlier unpublished attempt."
    } else {
        throw "Source version $current is neither published version $($channel.latestVersion) nor next patch $nextVersion. Refusing to guess."
    }
}
# A newer source branch or race invalidates the acceptance target, regardless
# of whether this job created the release commit or reused one.
$verified=$false
for ($attempt=1;$attempt -le 12;$attempt++) {
    $currentMain=(Invoke-RestMethod -Uri "$sourceApi/branches/main" -Headers $headers).commit.sha
    if ($currentMain -ceq $releaseSha) { $verified=$true;break }
    Start-Sleep -Seconds 3
}
Require $verified 'Main no longer points at the prepared release SHA; publication refused.'
@(
    "release_sha=$releaseSha",
    "release_version=$release",
    "release_schema=$targetSchema",
    "version_bumped=$($bumped.ToString().ToLowerInvariant())"
) | Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8
Write-Host "Prepared FMSC $release, schema $targetSchema, exact Source commit $releaseSha. Publish=$publish."
if ($env:GITHUB_STEP_SUMMARY) {
    @(
        '## Workflow 04 release preparation',
        "- Application: $release",
        "- SQL schema (migration target): $targetSchema",
        "- Exact Source SHA: $releaseSha",
        "- New version commit: $bumped",
        "- Publication requested: $publish"
    ) | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY
}