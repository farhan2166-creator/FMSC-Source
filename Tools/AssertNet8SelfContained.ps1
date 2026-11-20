# Fail closed if a production FMSC package is not a complete .NET 8 Windows x64 self-contained publish.
# This check inspects only disposable/release application files; no SQL, network or school data.
param([Parameter(Mandatory=$true)][string]$ApplicationDirectory)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($ApplicationDirectory)
if (-not (Test-Path -LiteralPath $root -PathType Container)) {
    throw '.NET 8 application directory does not exist.'
}
foreach ($name in @('FMSC_SMS.exe','FMSC_SMS.dll','FMSC_SMS.runtimeconfig.json',
                   'FMSC_SMS.deps.json','coreclr.dll','hostpolicy.dll','System.Private.CoreLib.dll')) {
    $file = Join-Path $root $name
    if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -eq 0) {
        throw "Self-contained .NET 8 Windows x64 package is missing $name."
    }
}
$runtime = Get-Content -LiteralPath (Join-Path $root 'FMSC_SMS.runtimeconfig.json') -Raw | ConvertFrom-Json
if ([string]$runtime.runtimeOptions.tfm -cne 'net8.0') {
    throw 'Published FMSC runtimeconfig does not target .NET 8.'
}
$frameworks = @($runtime.runtimeOptions.includedFrameworks)
foreach ($name in @('Microsoft.NETCore.App','Microsoft.WindowsDesktop.App')) {
    $matched = @($frameworks | Where-Object { $_.name -ceq $name -and [string]$_.version -match '^8\.' })
    if ($matched.Count -ne 1) {
        throw "Published application is not self-contained with .NET 8 $name."
    }
}
foreach ($framework in $frameworks) {
    if ([string]$framework.version -notmatch '^8\.') {
        throw 'Published application unexpectedly embeds a non-.NET-8 framework.'
    }
}
$deps = Get-Content -LiteralPath (Join-Path $root 'FMSC_SMS.deps.json') -Raw | ConvertFrom-Json
if ([string]$deps.runtimeTarget.name -notmatch '\.NETCoreApp,Version=v8\.0/win-x64$') {
    throw 'Published application dependencies do not target .NET 8 win-x64.'
}
Write-Output 'PASS: FMSC is a self-contained .NET 8 Windows x64 package, including Windows Desktop runtime.'
