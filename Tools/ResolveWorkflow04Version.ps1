# Pure, offline-testable release version policy. No network or repository mutation.
# A source patch may skip unpublished patch numbers; the publisher still
# verifies every numbered migration and the exact Source/Sandbox release SHA.
function Resolve-FmscWorkflow04Version {
    param(
        [Parameter(Mandatory=$true)][string]$SourceVersion,
        [Parameter(Mandatory=$true)][string]$PublishedVersion,
        [string]$ApprovedTransitionVersion = ''
    )
    if ($SourceVersion -cnotmatch '^[0-9]+\.[0-9]+\.[0-9]+$' -or
        $PublishedVersion -cnotmatch '^[0-9]+\.[0-9]+\.[0-9]+$') {
        throw 'Source and published versions must be three-part numeric versions.'
    }
    try {
        $source = [version]::Parse($SourceVersion)
        $published = [version]::Parse($PublishedVersion)
    } catch {
        throw 'Source or published version cannot be parsed safely.'
    }
    if ($source.ToString(3) -cne $SourceVersion -or
        $published.ToString(3) -cne $PublishedVersion) {
        throw 'Source or published version is not in canonical version format.'
    }
    $comparison = $source.CompareTo($published)
    if ($comparison -lt 0) {
        throw "Source version $SourceVersion is older than published version $PublishedVersion."
    }
    $changingReleaseLine = $source.Major -ne $published.Major -or
        $source.Minor -ne $published.Minor
    if ($changingReleaseLine) {
        # A new release line is NEVER inferred from a higher source version.
        # The owner must enter the exact new X.Y.0 version when dispatching 04.
        if ([string]::IsNullOrWhiteSpace($ApprovedTransitionVersion) -or
            $ApprovedTransitionVersion -cne $SourceVersion) {
            throw "Version-line transition to $SourceVersion requires explicit exact owner approval."
        }
        if ($source.Build -ne 0) {
            throw 'A new major/minor release line must begin at patch zero.'
        }
        $nextMinor = $source.Major -eq $published.Major -and
            $source.Minor -eq ($published.Minor + 1)
        $nextMajor = $source.Major -eq ($published.Major + 1) -and
            $source.Minor -eq 0
        if (-not ($nextMinor -or $nextMajor)) {
            throw "Unreviewed major/minor release-line jump from $PublishedVersion to $SourceVersion."
        }
        return [pscustomobject]@{ RequiresBump=$false; ReleaseVersion=$SourceVersion }
    }
    if (-not [string]::IsNullOrWhiteSpace($ApprovedTransitionVersion)) {
        throw 'Transition approval was supplied for a patch-only release.'
    }
    if ($comparison -gt 0) {
        # A failed attempt or several unreleased changes may already have
        # advanced Source/main. Publish the exact version and SHA, never bump it.
        return [pscustomobject]@{ RequiresBump=$false; ReleaseVersion=$SourceVersion }
    }
    if ($published.Build -ge [int]::MaxValue) {
        throw 'Cannot advance the published patch version safely.'
    }
    $next = '{0}.{1}.{2}' -f $published.Major,$published.Minor,($published.Build + 1)
    return [pscustomobject]@{ RequiresBump=$true; ReleaseVersion=$next }
}
