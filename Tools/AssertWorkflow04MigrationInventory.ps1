# Offline Workflow 04 release-migration inventory check. No migration is executed.
# The only authoritative target is the schema declared in the candidate source.
function Assert-FmscWorkflow04MigrationInventory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)][int]$TargetSchema,
        [Parameter(Mandatory=$true)][string]$Directory
    )

    if ($TargetSchema -lt 7 -or $TargetSchema -gt 9999) {
        throw "Unsupported release schema target $TargetSchema."
    }
    if ($TargetSchema -eq 8) {
        throw 'Schema 8 is a retirement-only transition, not a standalone release.'
    }
    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) {
        throw "Source migration directory missing: $Directory."
    }

    $present=@{}
    foreach ($file in @(Get-ChildItem -LiteralPath $Directory -File |
        Where-Object { $_.Extension -ieq '.sql' })) {
        if ($file.BaseName -cnotmatch '^[1-9][0-9]*$') {
            throw "Invalid numbered migration filename: $($file.Name)."
        }
        $version=0
        if (-not [int]::TryParse($file.BaseName,[ref]$version) -or
            $version -lt 2 -or $version -gt $TargetSchema) {
            throw "Migration $($file.Name) lies outside the declared target schema $TargetSchema."
        }
        if ($present.ContainsKey($version)) {
            throw "Duplicate migration $version.sql."
        }
        $present[$version]=$true
    }
    for ($version=2; $version -le $TargetSchema; $version++) {
        if (-not $present.ContainsKey($version)) {
            throw "Missing consecutive migration $version.sql for schema $TargetSchema."
        }
    }
}
