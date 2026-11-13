# Publisher preflight only. Do not print OAuth registration or provider replies.
function Assert-FmscGoogleDesktopRegistration {
    param([AllowEmptyString()][string]$ClientId, [AllowEmptyString()][string]$Json)
    if ([string]::IsNullOrWhiteSpace($ClientId) -and [string]::IsNullOrWhiteSpace($Json)) { return }
    if ([string]::IsNullOrWhiteSpace($Json)) {
        throw 'Google Drive is configured. Supply CloudBackupGoogleDesktop.json for local publishing, or FMSC_GOOGLE_DESKTOP_JSON in the publishing repository Actions secrets. The release cannot omit Desktop registration.'
    }
    if ([Text.Encoding]::UTF8.GetByteCount($Json) -gt 16384) { throw 'Google Desktop registration exceeds its size limit.' }
    try { $registration = ConvertFrom-Json -InputObject $Json -ErrorAction Stop }
    catch { throw 'Google Desktop registration is not valid JSON.' }
    if (-not $registration -or $registration.PSObject.Properties['web'] -or
        -not $registration.PSObject.Properties['installed'] -or
        $registration.installed.client_id -isnot [string] -or
        $registration.installed.client_secret -isnot [string] -or
        $registration.installed.client_id -cne $ClientId -or
        $ClientId.Length -lt 3 -or $ClientId.Length -gt 250 -or
        [string]::IsNullOrWhiteSpace($registration.installed.client_secret) -or
        $registration.installed.client_secret.Length -gt 250) {
        throw 'Google Desktop registration must be an installed/Desktop client matching the pinned Source GoogleClientId. Web credentials are not supported.'
    }
}
