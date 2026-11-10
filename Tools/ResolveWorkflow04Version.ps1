# Pure, offline-testable release version policy. No network or repository mutation.
# A source patch may skip unpublished patch numbers; the publisher still
# verifies every numbered migration and the exact Source/Sandbox release SHA.
function Resolve-FmscWorkflow04Version {
    param(
        [Parameter(Mandatory=$true)][string]$SourceVersion,
        [Parameter(Mandatory=$true)][string]$PublishedVersion
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
    if ($source.Major -ne $published.Major -or $source.Minor -ne $published.Minor) {
        throw "Source version $SourceVersion must retain the published major.minor $($published.Major).$($published.Minor) for a patch release."
    }
    $comparison = $source.CompareTo($published)
    if ($comparison -lt 0) {
        throw "Source version $SourceVersion is older than published version $PublishedVersion."
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
