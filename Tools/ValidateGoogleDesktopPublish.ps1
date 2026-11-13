param([Parameter(Mandatory=$true)][string]$ApplicationDirectory)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'AssertGoogleDesktopRegistration.ps1')
$providers = Get-Content -LiteralPath (Join-Path $ApplicationDirectory 'CloudBackupProviders.json') -Raw | ConvertFrom-Json
$path = Join-Path $ApplicationDirectory 'CloudBackupGoogleDesktop.json'
$json = ''
if (Test-Path -LiteralPath $path -PathType Leaf) {
    if ((Get-Item -LiteralPath $path).Length -gt 16384) { throw 'Google Desktop registration exceeds its size limit.' }
    $json = [IO.File]::ReadAllText($path)
}
Assert-FmscGoogleDesktopRegistration -ClientId $providers.GoogleClientId -Json $json
Write-Output 'Google Desktop registration is valid for private publishing.'
