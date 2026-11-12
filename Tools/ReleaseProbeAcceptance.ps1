# Pure, release-independent acceptance policy for an independently verified
# published executable. This helper performs NO filesystem, database, network,
# installation or process actions. Callers obtain expected hashes from trusted
# publication evidence and perform the checks before invoking this predicate.
function Test-ReleaseProbeAcceptance {
    param(
        [bool]$BinaryMatchesPublication,
        [bool]$BuildInfoPresent,
        [bool]$BuildInfoMatchesPublication,
        [int]$Schema,
        [int]$RequiredSchema,
        [bool]$CompatibilityVerified,
        [bool]$RunningSelectionValid
    )
    return (
        $BinaryMatchesPublication -and
        (-not $BuildInfoPresent -or $BuildInfoMatchesPublication) -and
        $RequiredSchema -gt 0 -and
        $Schema -eq $RequiredSchema -and
        $CompatibilityVerified -and
        $RunningSelectionValid
    )
}
