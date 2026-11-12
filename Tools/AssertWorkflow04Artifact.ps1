# Verify the exact Workflow 04 build artifact before a separate publication job.
# This is deliberately offline: only a digest supplied by the producing job
# authorizes the inventory. There are no network calls or school data here.
param(
    [Parameter(Mandatory=$true)][string]$ApplicationDirectory,
    [Parameter(Mandatory=$true)][string]$InventorySha256,
    [string]$ExpectedSourceSha = '',
    [string]$ExpectedVersion = '',
    [int]$ExpectedSchema = 0,
    [switch]$InventoryOnly
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $ApplicationDirectory -PathType Container)) {
    throw 'Workflow 04 application artifact is missing.'
}
$root = (Resolve-Path -LiteralPath $ApplicationDirectory).Path
$inventory = Join-Path $root 'SHA256SUMS.txt'
if ($InventorySha256 -cnotmatch '^[0-9a-f]{64}$' -or
    -not (Test-Path -LiteralPath $inventory -PathType Leaf)) {
    throw 'Workflow 04 inventory digest or file is missing.'
}
$digest = (Get-FileHash -LiteralPath $inventory -Algorithm SHA256).Hash.ToLowerInvariant()
if ($digest -cne $InventorySha256) { throw 'Workflow 04 artifact inventory digest differs from the producing job.' }
$lines = @(Get-Content -LiteralPath $inventory -Encoding UTF8)
if ($lines.Count -eq 0) { throw 'Workflow 04 artifact inventory is empty.' }
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$prefix = $root.TrimEnd([char]'\', [char]'/') + [IO.Path]::DirectorySeparatorChar
foreach ($line in $lines) {
    if ($line -cnotmatch '^([0-9a-f]{64})  (.+)$') { throw 'Malformed Workflow 04 artifact inventory entry.' }
    $wantedHash = $Matches[1]
    $relative = $Matches[2]
    if ($relative -ceq 'SHA256SUMS.txt' -or
        $relative -match '(^/|\\|:|//|(^|/)\.{1,2}(/|$))' -or
        -not $seen.Add($relative)) {
        throw 'Unsafe or duplicate Workflow 04 artifact inventory path.'
    }
    $file = [IO.Path]::GetFullPath((Join-Path $root $relative.Replace('/', [string][IO.Path]::DirectorySeparatorChar)))
    if (-not $file.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $file -PathType Leaf)) {
        throw 'Missing or out-of-root Workflow 04 artifact file.'
    }
    $actual = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -cne $wantedHash) { throw "Workflow 04 artifact digest differs for $relative." }
}
$files = @(Get-ChildItem -LiteralPath $root -Recurse -Force -File)
foreach ($file in $files) {
    if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Workflow 04 artifact contains a linked file.'
    }
    $relative = [IO.Path]::GetRelativePath($root, $file.FullName).Replace('\', '/')
    if ($relative -cne 'SHA256SUMS.txt' -and -not $seen.Contains($relative)) {
        throw "Unlisted Workflow 04 artifact file: $relative."
    }
}
$linkedDirectories = @(Get-ChildItem -LiteralPath $root -Recurse -Force -Directory |
    Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 })
if ($linkedDirectories.Count) { throw 'Workflow 04 artifact contains a linked directory.' }
if ($files.Count -ne ($seen.Count + 1)) { throw 'Workflow 04 artifact inventory is incomplete.' }
if ($InventoryOnly) {
    Write-Output 'PASS: Workflow 04 downloaded artifact matches the producing-job inventory.'
    return
}
if ($ExpectedSourceSha -cnotmatch '^[0-9a-f]{40}$' -or
    $ExpectedVersion -cnotmatch '^[0-9]+\.[0-9]+\.[0-9]+$' -or
    $ExpectedSchema -lt 1) {
    throw 'Missing exact Workflow 04 release identity.'
}
$dll = Join-Path $root 'FMSC_SMS.dll'
$corePath = Join-Path $root 'UpdateSupport/core.ps1'
$infoPath = Join-Path $root 'BUILD_INFO.txt'
foreach ($file in @($dll, $corePath, $infoPath)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        throw 'Workflow 04 artifact is missing its release identity evidence.'
    }
}
$productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($dll).ProductVersion
if (-not $productVersion -or $productVersion.Split('+')[0] -cne $ExpectedVersion) {
    throw 'Downloaded executable does not match the exact approved application version.'
}
$core = Get-Content -LiteralPath $corePath -Raw
if ($core -cnotmatch ('(?m)^\$AppVersion = ''' + [regex]::Escape($ExpectedVersion) + '''\r?$') -or
    $core -cnotmatch ('(?m)^\$DatabaseVersion = ''' + $ExpectedSchema + '''\r?$')) {
    throw 'Downloaded installer application or database version disagrees with approval.'
}
$info = Get-Content -LiteralPath $infoPath -Raw
if ($info -cnotmatch ('(?m)^Source commit: ' + [regex]::Escape($ExpectedSourceSha) + '\r?$') -or
    $info -cnotmatch ('(?m)^Application version: ' + [regex]::Escape($ExpectedVersion) + '\r?$')) {
    throw 'Downloaded build manifest does not match the exact approved Source SHA and version.'
}
Write-Output "Verified Workflow 04 artifact for $ExpectedVersion, schema $ExpectedSchema, SHA $ExpectedSourceSha."
