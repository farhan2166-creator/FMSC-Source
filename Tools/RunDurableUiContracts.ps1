# Opt-in, focused WinForms behavior checks for test-contract maintenance.
# Run on a disposable Windows test profile. No school database, installer,
# private Sandbox or Workflow 04 publication is contacted by this script.
[CmdletBinding()]
param(
    [switch]$IncludePdfReports,
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Native WinForms checks require Windows.'
}
$project = 'Tests/ThemeSmokeChecks/ThemeSmokeChecks.csproj'
$checks = @(
    '--action-shortcuts',
    '--admission-fee-layout',
    '--admission-header',
    '--dialog-size-tiers',
    '--billing-layout',
    '--billing-design',
    '--class-sheet-buttons',
    '--collection-counter-layout',
    '--grid-alignment',
    '--report-ordering',
    '--new-reports',
    '--family-confirmation'
)
if ($IncludePdfReports) {
    $checks += @(
        '--family-advance-layout',
        '--bill-roll-heights',
        '--compact-management-reports',
        '--report-captions'
    )
}
Push-Location (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
try {
    if (-not $NoBuild) {
        & dotnet build $project -c Release
        if ($LASTEXITCODE -ne 0) { throw 'WinForms focused-test compilation failed.' }
    }
    $failed = @()
    foreach ($mode in $checks) {
        Write-Host "Running focused UI contract: $mode"
        & dotnet run --project $project -c Release --no-build -- $mode
        if ($LASTEXITCODE -ne 0) {
            $failed += "$mode (exit $LASTEXITCODE)"
            Write-Warning "Standalone contract failed: $mode"
        }
    }
    if ($failed.Count -gt 0) {
        throw "$($failed.Count) of $($checks.Count) standalone UI modes failed: $($failed -join ', ')"
    }
    Write-Host "PASS: $($checks.Count) focused native UI contract modes."
}
finally {
    Pop-Location
}
