# Offline tampering and identity-gate tests for cross-job Workflow 04 artifacts.
$ErrorActionPreference = 'Stop'
$verifier = Join-Path $PSScriptRoot 'AssertWorkflow04Artifact.ps1'
$root = Join-Path ([IO.Path]::GetTempPath()) ('fmsc-workflow04-artifact-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $root
function Snapshot {
    $paths = @(Get-ChildItem -LiteralPath $root -File -Recurse -Force |
        Where-Object { $_.Name -cne 'SHA256SUMS.txt' } | Sort-Object FullName)
    $entries = @($paths | ForEach-Object {
        $rel = [IO.Path]::GetRelativePath($root, $_.FullName).Replace('\', '/')
        '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $rel
    })
    $entries | Set-Content -LiteralPath (Join-Path $root 'SHA256SUMS.txt') -Encoding utf8
    return (Get-FileHash -LiteralPath (Join-Path $root 'SHA256SUMS.txt') -Algorithm SHA256).Hash.ToLowerInvariant()
}
function MustReject([string]$Case,[string]$Digest,[bool]$InventoryOnly=$true) {
    $refused = $false
    try {
        if ($InventoryOnly) {
            $null = & $verifier -ApplicationDirectory $root -InventorySha256 $Digest -InventoryOnly
        } else {
            $null = & $verifier -ApplicationDirectory $root -InventorySha256 $Digest
        }
    } catch { $refused = $true }
    if (-not $refused) { throw "Workflow 04 artifact test failed: $Case" }
}
try {
    Set-Content -LiteralPath (Join-Path $root 'fixture.txt') -Value 'verified content'
    $digest = Snapshot
    $null = & $verifier -ApplicationDirectory $root -InventorySha256 $digest -InventoryOnly
    MustReject 'full release identity is required' $digest $false

    Set-Content -LiteralPath (Join-Path $root 'fixture.txt') -Value 'changed content'
    MustReject 'modified payload' $digest
    Set-Content -LiteralPath (Join-Path $root 'fixture.txt') -Value 'verified content'
    $null = & $verifier -ApplicationDirectory $root -InventorySha256 $digest -InventoryOnly

    Set-Content -LiteralPath (Join-Path $root 'extra.txt') -Value 'not inventoried'
    MustReject 'unexpected file' $digest
    Remove-Item -LiteralPath (Join-Path $root 'extra.txt')

    Remove-Item -LiteralPath (Join-Path $root 'fixture.txt')
    MustReject 'missing file' $digest
    Set-Content -LiteralPath (Join-Path $root 'fixture.txt') -Value 'verified content'

    Add-Content -LiteralPath (Join-Path $root 'SHA256SUMS.txt') -Value 'tampered'
    MustReject 'changed inventory' $digest
    $digest = Snapshot
    Add-Content -LiteralPath (Join-Path $root 'SHA256SUMS.txt') -Value ((Get-FileHash -LiteralPath (Join-Path $root 'fixture.txt') -Algorithm SHA256).Hash.ToLowerInvariant() + '  ../outside.txt')
    $unsafe = (Get-FileHash -LiteralPath (Join-Path $root 'SHA256SUMS.txt') -Algorithm SHA256).Hash.ToLowerInvariant()
    MustReject 'traversal path even with recomputed inventory digest' $unsafe

    $digest = Snapshot
    Add-Content -LiteralPath (Join-Path $root 'SHA256SUMS.txt') -Value ((Get-FileHash -LiteralPath (Join-Path $root 'fixture.txt') -Algorithm SHA256).Hash.ToLowerInvariant() + '  fixture.txt')
    $duplicate = (Get-FileHash -LiteralPath (Join-Path $root 'SHA256SUMS.txt') -Algorithm SHA256).Hash.ToLowerInvariant()
    MustReject 'duplicate inventory path' $duplicate
    Write-Output 'PASS: Workflow 04 verified artifact refuses modified, missing, extra, traversal, duplicate and wrong-identity files.'
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
