# Stable desktop entry point. This script and its helpers are installed in
# Administrators/SYSTEM-writable storage outside the replaceable app directory.
param([switch]$ResumeUpdate)

$ErrorActionPreference = 'Stop'
try {
    . ([IO.Path]::Combine($PSScriptRoot,'UpdateRecovery.ps1'))
    $root = Get-FmscRecoveryRoot
    if ([IO.Path]::GetFullPath($PSScriptRoot) -ine [IO.Path]::GetFullPath($root)) { throw 'Invalid FMSC launcher location.' }
    $record = Read-FmscRecoveryJournal $root
    $active = $null
    try {
        if ([Threading.Mutex]::TryOpenExisting('Global\FMSCInstallerActivation',[ref]$active)) {
            $held = $false
            try { $held = $active.WaitOne(0) } catch [Threading.AbandonedMutexException] { $held = $true }
            if (-not $held) { throw 'FMSC is being updated. Wait for installation to finish.' }
            $active.ReleaseMutex()
        }
    } finally { if ($active) { $active.Dispose() } }
    if ($record -and $record.Phase -notin @('Completed','Aborted')) {
        if ($record.UserSid -ne [Security.Principal.WindowsIdentity]::GetCurrent().User.Value) {
            throw 'Sign in as the Windows user who started this update to recover it.'
        }
        $recoveryScript = Join-Path $root 'Recovery.ps1'
        if ((Get-FileHash $recoveryScript -Algorithm SHA256).Hash -ne $record.ScriptSha256 -or
            (Get-FileHash (Join-Path $root 'UpdateRecovery.ps1') -Algorithm SHA256).Hash -ne $record.HelperSha256) {
            throw 'Recovery files failed verification. Contact support; school data has not been restored or replaced.'
        }
        if ($ResumeUpdate) {
            $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
            if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
                throw 'Administrator approval is required to recover the interrupted update.'
            }
            & $recoveryScript -ResumeUpdate
            exit $LASTEXITCODE
        }
        # Elevate the GUI launcher so PowerShell starts without allocating a console.
        $start = New-Object Diagnostics.ProcessStartInfo
        $start.FileName = Join-Path $root 'FMSC.Launcher.exe'
        $start.WorkingDirectory = $root
        $start.Arguments = '--recover'
        $start.UseShellExecute = $true
        $start.Verb = 'runas'
        $process = [Diagnostics.Process]::Start($start)
        if ($process) { $process.Dispose() }
    } elseif (-not $ResumeUpdate) {
        Start-Process -FilePath 'C:\FMSC School Management\FMSC_SMS.exe' -WorkingDirectory 'C:\FMSC School Management'
    }
} catch {
    Add-Type -AssemblyName System.Windows.Forms
    [Windows.Forms.MessageBox]::Show($_.Exception.Message, 'FMSC - Update Recovery', 'OK', 'Warning') | Out-Null
}
