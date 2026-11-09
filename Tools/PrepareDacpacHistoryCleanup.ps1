#Requires -Version 5.1
<#
.SYNOPSIS
    PREVIEW ONLY: remove the root fmsc.dacpac from a separate mirror clone.
.DESCRIPTION
    Does not push, delete remote refs, or modify an existing local checkout.
    Excludes only the exact Git path fmsc.dacpac. Preserves empty commits
    to avoid unnecessarily dropping the surrounding development record.
    Uses only installed Git and PowerShell; no Python or added tools.
    Verifies every commit's other files, message, identities, dates and parents.
    All changed commit IDs and GitHub PR caches must be handled separately.
.EXAMPLE
    .\Tools\PrepareDacpacHistoryCleanup.ps1 -OutputDirectory C:\Temp\fmsc-dacpac-review.git
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,
    [ValidateNotNullOrEmpty()]
    [string]$Repository = ''
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $Repository) {
    . (Join-Path $PSScriptRoot 'ResolveFmscGitHubRepository.ps1')
    $Repository=(Resolve-FmscGitHubRepository -Repository Source).HttpsRemote
}
$pathToRemove = 'fmsc.dacpac'
# Reviewed against GitHub on 30 October 2026: main is the only branch;
# there are no tags. Stop again if that inventory changes.
$expectedBranches = @('refs/heads/main')

$destination = [System.IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $destination) {
    throw "Output path already exists; will not overwrite: $destination"
}
& git --version
if ($LASTEXITCODE -ne 0) { throw 'Git is not available.' }

# GitHub is never modified by this script.
& git clone --mirror -- $Repository $destination
if ($LASTEXITCODE -ne 0) { throw 'Mirror clone failed. Nothing was pushed.' }

Push-Location -LiteralPath $destination
try {
    if ((& git rev-parse --is-bare-repository).Trim() -ne 'true' -or $LASTEXITCODE -ne 0) {
        throw 'The output is not the newly created bare mirror; stopping.'
    }
    $branches = @(& git for-each-ref --format='%(refname)' refs/heads)
    if ($LASTEXITCODE -ne 0) { throw 'Could not enumerate branches.' }
    $difference = @(Compare-Object -ReferenceObject $expectedBranches -DifferenceObject $branches)
    if ($difference.Count -ne 0) {
        throw 'Unexpected branch inventory. Review branches before cleanup; no remote changes.'
    }
    $tags = @(& git for-each-ref --format='%(refname)' refs/tags)
    if ($LASTEXITCODE -ne 0) { throw 'Could not enumerate tags.' }
    if ($tags.Count -ne 0) {
        throw 'Tags were found and need separate review before rewriting; no remote changes.'
    }

    $allRefs = @(& git for-each-ref --format='%(refname)')
    if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate mirror refs.' }
    foreach ($ref in $allRefs) {
        if ($ref -cne 'refs/heads/main' -and $ref -cnotmatch '^refs/pull/[0-9]+/(head|merge)$') {
            throw "Unexpected ref requires separate review: $ref. No remote changes."
        }
    }
    if (Test-Path -LiteralPath (Join-Path $destination 'info/grafts')) {
        throw 'Grafts require separate review. No remote changes.'
    }
    $originalCommits = @(& git rev-list --reverse --topo-order --all)
    if ($LASTEXITCODE -ne 0 -or $originalCommits.Count -eq 0) { throw 'Cannot enumerate original commits.' }
    $originalRefTips = @{}
    foreach ($ref in $allRefs) {
        $originalRefTips[$ref] = (& git rev-parse $ref).Trim()
        if ($LASTEXITCODE -ne 0) { throw "Cannot resolve $ref" }
    }

    $originalTips = @{}
    $otherFiles = @{}
    foreach ($ref in $expectedBranches) {
        $originalTips[$ref] = (& git rev-parse $ref).Trim()
        if ($LASTEXITCODE -ne 0) { throw "Cannot resolve $ref" }
        # ls-tree includes the mode and blob SHA, so this detects any changes
        # to other tracked file contents, names, and file modes at branch tips.
        $otherFiles[$ref] = @(
            & git ls-tree -r --full-tree $ref |
                Where-Object { $_ -cnotmatch '\tfmsc\.dacpac$' } |
                Sort-Object
        )
        if ($LASTEXITCODE -ne 0) { throw "Cannot list $ref" }
    }

    # Fixed ASCII index filter: no checkout, filename rewriting or shell-built paths.
    # Do not prune empty commits. The temporary state ref gives us a complete
    # old:new map for independent verification before removing local backup refs.
    $stateRef = 'refs/dacpac-cleanup-state'
    & git -c 'user.name=FMSC Cleanup Preview' -c 'user.email=cleanup-preview@invalid' filter-branch --state-branch $stateRef --index-filter 'git rm --cached --ignore-unmatch -- fmsc.dacpac' -- --all
    if ($LASTEXITCODE -ne 0) { throw 'Local rewrite failed; nothing was pushed.' }

    $mapLines = @(& git show ($stateRef + ':filter.map'))
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read the commit map; nothing was pushed.' }
    $commitMap = @{}
    foreach ($line in $mapLines) {
        if ($line -cnotmatch '^([0-9a-f]{40,64}):([0-9a-f]{40,64})$' -or $commitMap.ContainsKey($Matches[1])) {
            throw 'Invalid or duplicate commit mapping; nothing was pushed.'
        }
        $commitMap[$Matches[1]] = $Matches[2]
    }
    if ($commitMap.Count -ne $originalCommits.Count -or @($commitMap.Values | Sort-Object -Unique).Count -ne $originalCommits.Count) {
        throw 'Commit count changed unexpectedly; nothing was pushed.'
    }
    $firstChanged = @()
    $metadataFormat = '%an%x00%ae%x00%at%x00%ai%x00%cn%x00%ce%x00%ct%x00%ci%x00%B'
    foreach ($old in $originalCommits) {
        if (-not $commitMap.ContainsKey($old)) { throw "Missing commit mapping for $old" }
        $new = $commitMap[$old]
        $remainingPath = @(& git ls-tree $new -- $pathToRemove)
        if ($LASTEXITCODE -ne 0 -or $remainingPath.Count -ne 0) { throw "Root DACPAC remains in rewritten commit $new; nothing was pushed." }
        & git diff-tree --quiet -r $old $new -- . ':(top,exclude,literal)fmsc.dacpac'
        if ($LASTEXITCODE -ne 0) { throw "Unrelated file changed in commit $old; nothing was pushed." }
        $oldMetadata = @(& git show -s ("--format=" + $metadataFormat) $old)
        if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect original commit metadata.' }
        $newMetadata = @(& git show -s ("--format=" + $metadataFormat) $new)
        if ($LASTEXITCODE -ne 0 -or ($oldMetadata -join "`n") -cne ($newMetadata -join "`n")) {
            throw "Commit message, identity or date changed for $old; nothing was pushed."
        }
        $parentsText = ([string](& git show -s --format=%P $old)).Trim()
        if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect original parents.' }
        $parents = @($parentsText -split ' ' | Where-Object { $_ })
        $mappedParents = @()
        foreach ($parent in $parents) {
            if (-not $commitMap.ContainsKey($parent)) { throw 'Missing parent mapping.' }
            $mappedParents += $commitMap[$parent]
        }
        $newParents = ([string](& git show -s --format=%P $new)).Trim()
        if ($LASTEXITCODE -ne 0 -or $newParents -cne ($mappedParents -join ' ')) {
            throw "Merge/parent structure changed for $old; nothing was pushed."
        }
        if ($old -cne $new -and @($parents | Where-Object { $commitMap[$_] -cne $_ }).Count -eq 0) { $firstChanged += $old }
    }
    foreach ($ref in $allRefs) {
        $newTip = (& git rev-parse $ref).Trim()
        if ($LASTEXITCODE -ne 0 -or $newTip -cne $commitMap[$originalRefTips[$ref]]) {
            throw "Unexpected rewritten ref: $ref; nothing was pushed."
        }
    }
    $mapLines | Set-Content -LiteralPath (Join-Path $destination 'DACPAC_COMMIT_MAP.txt') -Encoding UTF8
    $affectedPullRefs = @($allRefs | Where-Object { $_ -cmatch '^refs/pull/' -and $originalRefTips[$_] -cne $commitMap[$originalRefTips[$_]] })
    $affectedPullRefs | Set-Content -LiteralPath (Join-Path $destination 'DACPAC_AFFECTED_PULL_REFS.txt') -Encoding UTF8

    # Remove only filter-branch's backup refs in this verified new mirror.
    # The original working repository is never selected or changed.
    $backupRefs = @(& git for-each-ref --format='%(refname)' refs/original/)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect local backup refs.' }
    foreach ($ref in @($backupRefs) + @($stateRef)) {
        if ($ref -cne $stateRef -and -not $ref.StartsWith('refs/original/',[StringComparison]::Ordinal)) {
            throw 'Unexpected backup ref; stopping.'
        }
        & git update-ref -d $ref
        if ($LASTEXITCODE -ne 0) { throw "Cannot remove local helper ref $ref" }
    }

    $remaining = @(& git log --all --format='%H' -- $pathToRemove)
    if ($LASTEXITCODE -ne 0 -or $remaining.Count -ne 0) {
        throw 'FAIL: historical root DACPAC is still reachable; nothing was pushed.'
    }
    foreach ($ref in $expectedBranches) {
        $newFiles = @(
            & git ls-tree -r --full-tree $ref |
                Where-Object { $_ -cnotmatch '\tfmsc\.dacpac$' } |
                Sort-Object
        )
        if ($LASTEXITCODE -ne 0) { throw "Cannot inspect rewritten $ref" }
        if (@(Compare-Object -ReferenceObject $otherFiles[$ref] -DifferenceObject $newFiles -CaseSensitive).Count -ne 0) {
            throw "FAIL: unrelated tracked file changed on $ref; nothing was pushed."
        }
    }

    $report = @(
        'LOCAL REVIEW ONLY - NO REMOTE CHANGES'
        'Repository: ' + $Repository
        'Removed historical path: ' + $pathToRemove
        ''
        'Verified: the root DACPAC does not exist in reachable local Git history.'
        'Verified: other tracked file paths/modes/blob IDs match at each reviewed branch tip.'
        ('Verified: all ' + $originalCommits.Count + ' commit trees preserve every other file.')
        'Verified: commit messages, author/committer identities/dates and merge parents are preserved.'
        ('Affected local pull refs: ' + $affectedPullRefs.Count)
        ('First changed original commits: ' + ($firstChanged -join ', '))
        'NOT verified: privacy clearance of earlier releases, logs, PRs, forks or clones.'
        'NOT verified: fresh replacement DACPAC or application regression tests.'
        ''
        'IMPORTANT: The root fmsc.dacpac is also absent from rewritten main.'
        'DO NOT push until a fresh, validated schema-only file is restored.'
        'DO NOT push until you have backup, PR-ref cleanup and collaborator plan.'
        'Never blindly push --mirror; GitHub pull request refs are server managed.'
        'Commit IDs change. Original commit signatures cannot remain valid after rewriting.'
        'Unreachable original objects may remain locally. Keep this preview private; do not upload it as an artifact.'
        ''
        'Original branch tips (needed for force-with-lease checks):'
    )
    foreach ($ref in $expectedBranches) {
        $report += ($ref + '  ' + $originalTips[$ref])
    }
    $reportPath = Join-Path $destination 'DACPAC_CLEANUP_REVIEW.txt'
    $report | Set-Content -LiteralPath $reportPath -Encoding UTF8
    Write-Host "PASS - local DACPAC history filtering and commit verification."
    Write-Host "Review: $reportPath"
    Write-Host "No remote GitHub refs were changed."
}
finally {
    Pop-Location
}
