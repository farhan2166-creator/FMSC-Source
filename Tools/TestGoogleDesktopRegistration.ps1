$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'AssertGoogleDesktopRegistration.ps1')
$id = 'fictional-desktop.apps.googleusercontent.com'
$good = @{installed=@{client_id=$id;client_secret='fictional-native-value'}} | ConvertTo-Json -Compress
Assert-FmscGoogleDesktopRegistration -ClientId $id -Json $good
Assert-FmscGoogleDesktopRegistration -ClientId '' -Json ''
foreach ($bad in @('', '{}', 'null', '{',
    (@{web=@{client_id=$id;client_secret='fictional-native-value'}} | ConvertTo-Json -Compress),
    (@{installed=@{client_id='wrong-id';client_secret='fictional-native-value'}} | ConvertTo-Json -Compress),
    (@{installed=@{client_id=$id;client_secret=12}} | ConvertTo-Json -Compress),
    (' ' * 16385))) {
    $refused = $false
    try { Assert-FmscGoogleDesktopRegistration -ClientId $id -Json $bad }
    catch {
        if ($_.Exception.Message.Contains('fictional-native-value')) { throw 'Registration data leaked in a diagnostic.' }
        $refused = $true
    }
    if (-not $refused) { throw 'Invalid or missing Google registration was accepted for publishing.' }
}
Write-Output 'PASS: configured Google publishing requires matching Desktop metadata; empty, malformed, web and mismatched credentials fail without disclosure.'

$scratch = Join-Path ([IO.Path]::GetTempPath()) ('fmsc-local-registration-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
try {
    @{GoogleClientId=$id} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $scratch 'CloudBackupProviders.json')
    $refused = $false
    try { & (Join-Path $PSScriptRoot 'ValidateGoogleDesktopPublish.ps1') -ApplicationDirectory $scratch }
    catch { $refused = $true }
    if (-not $refused) { throw 'Local publishing accepted missing configured Google metadata.' }
    [IO.File]::WriteAllText((Join-Path $scratch 'CloudBackupGoogleDesktop.json'), $good)
    & (Join-Path $PSScriptRoot 'ValidateGoogleDesktopPublish.ps1') -ApplicationDirectory $scratch
    Write-Output 'PASS: local source/output publishing validation requires the matching registration.'
}
finally { Remove-Item -LiteralPath $scratch -Recurse -Force }
