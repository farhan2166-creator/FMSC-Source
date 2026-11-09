param(
    [string]$PreparedRoot = '',
    [int]$OriginalProcessId = 0,
    [long]$OriginalStartTicks = 0,
    [string]$OriginalUserSid = '',
    [switch]$ResumeUpdate,
    [switch]$ShowInstallerWindow
)
$ErrorActionPreference = 'Stop'
. ([IO.Path]::Combine($PSScriptRoot,'UpdateRecovery.ps1'))
. ([IO.Path]::Combine($PSScriptRoot,'MaintenanceTools.ps1'))
$script:RecoveryRoot = Get-FmscRecoveryRoot
$script:RecoveryJournal = $null
if ($ResumeUpdate) { $PreparedRoot = 'pending-recovery' }
function Wait-OriginalFmscExit {
    param([int]$ProcessId, [long]$StartTicks, [string]$UserSid, [int]$TimeoutMilliseconds = 120000)
    if ($ProcessId -le 0 -or $StartTicks -le 0 -or
        $UserSid -ne [Security.Principal.WindowsIdentity]::GetCurrent().User.Value) {
        throw 'Update must run as the original Windows user, with a valid application process identity.'
    }
    $original = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if ($original) {
        if ($original.StartTime.ToUniversalTime().Ticks -ne $StartTicks) {
            throw 'The original process identity changed. Update stopped.'
        }
        if (-not $original.WaitForExit($TimeoutMilliseconds)) { throw 'FMSC did not exit. No update was installed.' }
    }
}
$AppName = 'FMSC School Management System'
$AppVersion = '1.0.21'
$DatabaseVersion = '12'
$ReleaseRoot = $PSScriptRoot
$AppSource      = Join-Path $ReleaseRoot 'FMSC_SMS'
$DatabaseSource = Join-Path $ReleaseRoot 'Database'
$MigrationsDir  = Join-Path $ReleaseRoot 'Migrations'
$AppDir = 'C:\FMSC School Management'
$DatabaseDir = 'C:\ProgramData\FMSC_Database'
# The new application is copied to a staging folder beside the live install and only renamed into place once it has been verified.
$AppStagingDir  = "$AppDir.staging"
$AppRollbackDir = "$AppDir.rollback"
$LiveMdf = Join-Path $DatabaseDir 'FMSC_DB.mdf'
$LiveLdf = Join-Path $DatabaseDir 'FMSC_DB_log.ldf'
$TemplateMdf = Join-Path $DatabaseSource 'FMSC_DB.mdf'
$TemplateLdf = Join-Path $DatabaseSource 'FMSC_DB_log.ldf'
if ($PreparedRoot) {
    # Update mode is existing-installation only. Never seed a missing database.
    $AppSource = Join-Path $PreparedRoot 'application'
    $MigrationsDir = Join-Path $AppSource 'UpdateSupport/Migrations'
    $DatabaseSource = Join-Path $PreparedRoot 'NO_DATABASE_TEMPLATES_ALLOWED'
    $TemplateMdf = Join-Path $DatabaseSource 'FMSC_DB.mdf'
    $TemplateLdf = Join-Path $DatabaseSource 'FMSC_DB_log.ldf'
}
$LogFile = Join-Path $DatabaseDir 'Install.log'
$SqlInstance = '(localdb)\MSSQLLocalDB'
$DatabaseName = 'FMSC_DB'
$ColorTitle   = 'Cyan'
$ColorInfo    = 'White'
$ColorStep    = 'Yellow'
$ColorSuccess = 'Green'
$ColorWarning = 'Yellow'
$ColorError   = 'Red'
$ColorDim     = 'DarkGray'
$ColorAccent  = 'Magenta'
try {
    $Host.UI.RawUI.WindowTitle =
        "$AppName - Installer"
    $WindowSize =
        $Host.UI.RawUI.WindowSize
    if ($WindowSize.Width -lt 110) {
        $WindowSize.Width = 110
    }
    if ($WindowSize.Height -lt 35) {
        $WindowSize.Height = 35
    }
    $Host.UI.RawUI.WindowSize = $WindowSize
}
catch {
}
function Show-Banner {
    Clear-Host
    Write-Host ''
    Write-Host '  ==============================================================' -ForegroundColor $ColorTitle
    Write-Host '                                                                ' -ForegroundColor $ColorTitle
    Write-Host '              FMSC SCHOOL MANAGEMENT SYSTEM                   ' -ForegroundColor $ColorTitle
    Write-Host '                    INSTALLER / UPDATER                        ' -ForegroundColor $ColorAccent
    Write-Host '                                                                ' -ForegroundColor $ColorTitle
    Write-Host '  ==============================================================' -ForegroundColor $ColorTitle
    Write-Host ''
    Write-Host "  Application Release : $AppVersion" -ForegroundColor $ColorInfo
    Write-Host "  Required DB Schema  : $DatabaseVersion" -ForegroundColor $ColorInfo
    Write-Host ''
}
function Write-Section {
    param(
        [string]$Title
    )
    Set-UpdateWindowStage $Title
    Set-UpdateWindowStatus $Title
    Write-Host ''
    Write-Host '  --------------------------------------------------------------' -ForegroundColor $ColorDim
    Write-Host "  $Title" -ForegroundColor $ColorStep
    Write-Host '  --------------------------------------------------------------' -ForegroundColor $ColorDim
    Write-Host ''
}
function Write-Step {
    param(
        [string]$Message
    )
    Set-UpdateWindowStatus $Message
    Write-Host "  > $Message" -ForegroundColor $ColorInfo
}
function Write-Success {
    param(
        [string]$Message
    )
    Write-Host "  [OK] $Message" -ForegroundColor $ColorSuccess
}
function Write-WarningMessage {
    param(
        [string]$Message
    )
    Write-Host "  [!] $Message" -ForegroundColor $ColorWarning
}
function Write-Failure {
    param(
        [string]$Message
    )
    Write-Host "  [X] $Message" -ForegroundColor $ColorError
}
function Pause-Step {
    if ($PreparedRoot -or $ShowInstallerWindow) { return }
    Start-Sleep -Milliseconds 700
}
function Pause-Long {
    if ($PreparedRoot -or $ShowInstallerWindow) { return }
    Start-Sleep -Milliseconds 1200
}
function Open-UpdateWindow {
    if (-not $PreparedRoot -and -not $ShowInstallerWindow) { return }
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    [Windows.Forms.Application]::EnableVisualStyles()
    $script:UpdateWindow = New-Object Windows.Forms.Form
    # A complete installer can also be run over an existing application or
    # school database. Only an empty destination is a first-time installation.
    $installedExeExists = -not [string]::IsNullOrWhiteSpace([string]$AppDir) -and
        (Test-Path -LiteralPath (Join-Path $AppDir 'FMSC_SMS.exe') -PathType Leaf)
    $existingDataExists = -not [string]::IsNullOrWhiteSpace([string]$LiveMdf) -and
        (Test-Path -LiteralPath $LiveMdf -PathType Leaf)
    $existingInstallation = $installedExeExists -or $existingDataExists
    # The installer UI runs in PowerShell, but its window/taskbar should carry
    # FMSC's own icon. Prefer the packaged GUI setup binary, then the working
    # installation, then the candidate payload (first-time setup).
    # Icon loading is cosmetic and must never interfere with recovery.
    $brandIcon = $null
    foreach ($candidate in @(
        @{ Root = $PSScriptRoot; File = 'FMSC.Setup.exe' },
        @{ Root = $AppDir; File = 'FMSC_SMS.exe' },
        @{ Root = $AppSource; File = 'FMSC_SMS.exe' }
    )) {
        # The same UI is exercised as a standalone function by offline tests,
        # where the script root or installation path may legitimately be empty.
        # Only resolve paths inside the cosmetic, best-effort guard.
        if ([string]::IsNullOrWhiteSpace([string]$candidate.Root)) { continue }
        try {
            $iconExe = Join-Path -Path $candidate.Root -ChildPath $candidate.File
            if (-not (Test-Path -LiteralPath $iconExe -PathType Leaf)) { continue }
            $icon = [Drawing.Icon]::ExtractAssociatedIcon($iconExe)
            if ($icon) {
                $script:UpdateWindow.Icon = $icon
                $brandIcon = $icon
                break
            }
        } catch {
            # Missing or invalid branding must never stop setup or safe recovery.
        }
    }
    $script:UpdateWindow.AutoScaleMode = 'None' # Avoid a second WinForms font-based size pass.
    $script:UpdateWindow.Text = if ($PreparedRoot) { 'FMSC - Installing update' }
        elseif ($existingInstallation) { 'FMSC - Existing installation' }
        else { 'FMSC - First-time setup' }
    # Shared first-install and prepared-update window: match the FMSC login dialog
    # baseline at 100% DPI. Both installer modes use this same UI and geometry.
    $clientWidth = 760
    $clientHeight = 500
    $script:UpdateWindow.ClientSize = [Drawing.Size]::new($clientWidth, $clientHeight)
    $script:UpdateWindow.StartPosition = 'CenterScreen'
    $script:UpdateWindow.FormBorderStyle = 'FixedDialog'
    $script:UpdateWindow.ControlBox = $false
    $script:UpdateWindow.MaximizeBox = $false
    $script:UpdateWindow.MinimizeBox = $false
    $script:UpdateWindow.Font = New-Object Drawing.Font('Segoe UI',10)
    $script:UpdateWindow.BackColor = [Drawing.Color]::FromArgb(246,248,251)

    $header = New-Object Windows.Forms.Panel
    $header.SetBounds(0,0,$clientWidth,138)
    $header.BackColor = [Drawing.Color]::White
    # The circular FMSC icon is already embedded in FMSC.Setup.exe. Use it
    # directly rather than a separate asset or placeholder letter.
    $mark = New-Object Windows.Forms.PictureBox
    $mark.SetBounds(30,38,54,54)
    $mark.SizeMode = [Windows.Forms.PictureBoxSizeMode]::Zoom
    $mark.BackColor = [Drawing.Color]::White
    # The packaged circular ICO has higher-resolution sizes than some EXE
    # icon extractors return. Fall back to the verified launcher's icon.
    foreach ($logoRoot in @($AppSource, $AppDir)) {
        if ([string]::IsNullOrWhiteSpace([string]$logoRoot)) { continue }
        try {
            $logoPath = Join-Path $logoRoot 'Assets\FMSC_Logo_Circular.ico'
            if (-not (Test-Path -LiteralPath $logoPath -PathType Leaf)) { continue }
            $logoIcon = [Drawing.Icon]::new($logoPath, [Drawing.Size]::new(64,64))
            try { $mark.Image = $logoIcon.ToBitmap() }
            finally { $logoIcon.Dispose() }
            break
        } catch {
            # Branding is optional during recovery; never block installation.
        }
    }
    if (-not $mark.Image -and $brandIcon) { $mark.Image = $brandIcon.ToBitmap() }
    $name = New-Object Windows.Forms.Label
    $name.SetBounds(98,36,600,36)
    $name.Text = 'FMSC School Management'
    $name.ForeColor = [Drawing.Color]::FromArgb(17,24,39)
    $name.Font = New-Object Drawing.Font('Segoe UI',16,[Drawing.FontStyle]::Bold)
    $mode = New-Object Windows.Forms.Label
    $mode.SetBounds(100,77,480,20)
    $mode.Text = if ($PreparedRoot) { 'SECURE APPLICATION UPDATE' }
        elseif ($existingInstallation) { 'EXISTING FMSC INSTALLATION' }
        else { 'FIRST-TIME INSTALLATION' }
    $mode.ForeColor = [Drawing.Color]::FromArgb(37,99,235)
    $mode.Font = New-Object Drawing.Font('Segoe UI',9,[Drawing.FontStyle]::Bold)
    $version = New-Object Windows.Forms.Label
    $version.SetBounds(495,105,235,22)
    $version.Text = "App $AppVersion   |   Database $DatabaseVersion"
    $version.TextAlign = 'MiddleRight'
    $version.ForeColor = [Drawing.Color]::FromArgb(107,114,128)
    $version.Font = New-Object Drawing.Font('Segoe UI',8.5)
    $header.Controls.AddRange(@($mark,$name,$mode,$version))
    $accent = New-Object Windows.Forms.Panel
    $accent.SetBounds(0,138,$clientWidth,3)
    $accent.BackColor = [Drawing.Color]::FromArgb(37,99,235)

    $script:UpdatePhase = New-Object Windows.Forms.Label
    $script:UpdatePhase.SetBounds(30,165,700,30)
    $script:UpdatePhase.Text = 'PREPARING A SAFE INSTALLATION'
    $script:UpdatePhase.ForeColor = [Drawing.Color]::FromArgb(37,99,235)
    $script:UpdatePhase.Font = New-Object Drawing.Font('Segoe UI',9,[Drawing.FontStyle]::Bold)

    $script:UpdateStatus = New-Object Windows.Forms.Label
    $script:UpdateStatus.SetBounds(30,204,700,90)
    $script:UpdateStatus.TextAlign = 'MiddleLeft'
    $script:UpdateStatus.ForeColor = [Drawing.Color]::FromArgb(31,41,55)
    $script:UpdateStatus.Font = New-Object Drawing.Font('Segoe UI',11.5,[Drawing.FontStyle]::Bold)

    $script:UpdateStepBars = @()
    foreach ($i in 0..7) {
        $segment = New-Object Windows.Forms.Panel
        $segment.SetBounds((30 + 88 * $i),314,82,7)
        $segment.BackColor = [Drawing.Color]::FromArgb(226,232,240)
        $script:UpdateWindow.Controls.Add($segment)
        $script:UpdateStepBars += $segment
    }
    $script:UpdateBar = New-Object Windows.Forms.ProgressBar
    $script:UpdateBar.SetBounds(30,342,700,14)
    $script:UpdateBar.Style = 'Marquee'
    $script:UpdateBar.MarqueeAnimationSpeed = 28

    $foot = New-Object Windows.Forms.Label
    $foot.SetBounds(30,369,700,24)
    $foot.Text = 'Please keep this computer on. Database safety checks remain active.'
    $foot.ForeColor = [Drawing.Color]::FromArgb(107,114,128)
    $foot.Font = New-Object Drawing.Font('Segoe UI',8.5)
    # A stable, integrated action footer. Buttons cannot be used during
    # installation or verification; they activate only after successful setup.
    $separator = New-Object Windows.Forms.Panel
    $separator.SetBounds(0,424,$clientWidth,1)
    $separator.BackColor = [Drawing.Color]::FromArgb(226,232,240)
    $actions = New-Object Windows.Forms.Panel
    $actions.SetBounds(0,425,$clientWidth,75)
    $actions.BackColor = [Drawing.Color]::White
    $script:UpdateFinishPrompt = New-Object Windows.Forms.Label
    $script:UpdateFinishPrompt.SetBounds(30,20,365,34)
    $script:UpdateFinishPrompt.Text = 'Open FMSC when setup finishes?'
    $script:UpdateFinishPrompt.TextAlign = 'MiddleLeft'
    $script:UpdateFinishPrompt.ForeColor = [Drawing.Color]::FromArgb(107,114,128)
    $script:UpdateFinishPrompt.Font = New-Object Drawing.Font('Segoe UI',9)
    $script:UpdateYes = New-Object Windows.Forms.Button
    $script:UpdateYes.SetBounds(420,18,170,38)
    $script:UpdateYes.Text = 'Yes, open FMSC'
    $script:UpdateYes.FlatStyle = 'Flat'
    $script:UpdateYes.FlatAppearance.BorderSize = 0
    $script:UpdateYes.BackColor = [Drawing.Color]::FromArgb(37,99,235)
    $script:UpdateYes.ForeColor = [Drawing.Color]::White
    $script:UpdateYes.Font = New-Object Drawing.Font('Segoe UI',9,[Drawing.FontStyle]::Bold)
    $script:UpdateYes.Cursor = [Windows.Forms.Cursors]::Hand
    $script:UpdateYes.Enabled = $false
    $script:UpdateNo = New-Object Windows.Forms.Button
    $script:UpdateNo.SetBounds(602,18,128,38)
    $script:UpdateNo.Text = 'No, close'
    $script:UpdateNo.FlatStyle = 'Flat'
    $script:UpdateNo.FlatAppearance.BorderSize = 1
    $script:UpdateNo.FlatAppearance.BorderColor = [Drawing.Color]::FromArgb(226,232,240)
    $script:UpdateNo.BackColor = [Drawing.Color]::FromArgb(250,251,253)
    $script:UpdateNo.ForeColor = [Drawing.Color]::FromArgb(31,41,55)
    $script:UpdateNo.Font = New-Object Drawing.Font('Segoe UI',9)
    $script:UpdateNo.Cursor = [Windows.Forms.Cursors]::Hand
    $script:UpdateNo.Enabled = $false
    $script:UpdateDecision = $null
    $script:UpdateYes.Add_Click({ $script:UpdateDecision = 'Yes' })
    $script:UpdateNo.Add_Click({ $script:UpdateDecision = 'No' })
    $actions.Controls.AddRange(@($script:UpdateFinishPrompt,$script:UpdateYes,$script:UpdateNo))
    $script:UpdateWindow.Controls.AddRange(@($header,$accent,$script:UpdatePhase,$script:UpdateStatus,$script:UpdateBar,$foot,$separator,$actions))
    $script:UpdateWindow.Show()
    if ($PreparedRoot) {
        Set-UpdateWindowStatus 'Waiting for FMSC to close safely...'
    } else {
        Set-UpdateWindowStatus 'Preparing the installation and checking requirements...'
    }
}

function Set-UpdateWindowStage {
    param([string]$Title)
    if (-not $script:UpdateWindow -or $script:UpdateWindow.IsDisposed) { return }
    if ($Title -match '^STEP ([1-8]) OF 8\s*-\s*(.+)$') {
        $number = [int]$Matches[1]
        $script:UpdatePhase.Text = "STEP $number OF 8  |  $($Matches[2].ToUpperInvariant())"
        for ($i = 0; $i -lt $script:UpdateStepBars.Count; $i++) {
            $script:UpdateStepBars[$i].BackColor = if ($i -lt ($number - 1)) {
                [Drawing.Color]::FromArgb(37,99,235)
            } elseif ($i -eq ($number - 1)) {
                [Drawing.Color]::FromArgb(59,130,246)
            } else {
                [Drawing.Color]::FromArgb(226,232,240)
            }
        }
    }
    [Windows.Forms.Application]::DoEvents()
}
function Set-UpdateWindowStatus {
    param([string]$Message)
    if ($script:UpdateWindow -and -not $script:UpdateWindow.IsDisposed) {
        $prefix = if ($PreparedRoot) { 'Installing update' } else { 'Installing FMSC' }
        $script:UpdateStatus.Text = $prefix + [Environment]::NewLine + $Message
        [Windows.Forms.Application]::DoEvents()
    }
}
function Get-FmscDesktopShell {
    # Delegate restart to the existing desktop shell; never launch the school app
    # directly from this elevated process or under another Windows user's LocalDB.
    $shell = New-Object -ComObject Shell.Application
    $handle = 0
    $desktop = $shell.Windows().FindWindowSW(0,0,8,[ref]$handle,1)
    if (-not $desktop) { throw 'The Windows desktop is unavailable. Open FMSC from its shortcut.' }
    return $desktop.Document.Application
}
function Start-UpdatedFmsc {
    param([string]$Application)
    $desktop = Get-FmscDesktopShell
    $desktop.ShellExecute($Application, '', (Split-Path -Parent $Application), 'open', 1)
}
function Wait-FmscSetupChoice {
    if (-not $script:UpdateWindow -or $script:UpdateWindow.IsDisposed) {
        throw 'The FMSC setup window is unavailable.'
    }
    # The user may decide whether to launch FMSC only after all eight stages.
    while (-not $script:UpdateDecision) {
        [Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 50
    }
    return $script:UpdateDecision -eq 'Yes'
}
function Start-FmscAfterInstallation {
    param([string]$Application)
    if ($PreparedRoot -or $ShowInstallerWindow) {
        if (-not $script:UpdateWindow -or $script:UpdateWindow.IsDisposed) {
            throw 'The FMSC setup window closed unexpectedly after verification.'
        }
        $script:UpdateWindow.Text = 'FMSC - Installation complete'
        $script:UpdatePhase.Text = 'ALL 8 STAGES COMPLETE'
        $script:UpdatePhase.ForeColor = [Drawing.Color]::FromArgb(22,163,74)
        $script:UpdateStatus.Text = 'Installation complete' + [Environment]::NewLine +
            'FMSC has been verified and is ready to use.'
        foreach ($segment in $script:UpdateStepBars) {
            $segment.BackColor = [Drawing.Color]::FromArgb(37,99,235)
        }
        $script:UpdateBar.MarqueeAnimationSpeed = 0
        $script:UpdateBar.Style = 'Continuous'
        $script:UpdateBar.Value = 100
        $script:UpdateFinishPrompt.Text = 'Installation successful. Open FMSC?'
        $script:UpdateFinishPrompt.ForeColor = [Drawing.Color]::FromArgb(31,41,55)
        $script:UpdateYes.Enabled = $true
        $script:UpdateNo.Enabled = $true
        [Windows.Forms.Application]::DoEvents()
        if (-not (Wait-FmscSetupChoice)) { return }
        try { Start-UpdatedFmsc -Application $Application }
        catch {
            Write-Log "Installation completed; automatic launch failed: $($_.Exception.Message)"
            [Windows.Forms.MessageBox]::Show($script:UpdateWindow,
                'FMSC is installed. Please open it from its desktop shortcut.',
                'FMSC - Setup Complete', 'OK', 'Information') | Out-Null
        }
        return
    }
    $choice = Read-Host '  Start FMSC SMS now? (Y/N)'
    if ($choice -match '^[Yy]$') { Start-Process -FilePath $Application }
}
function Release-FmscInstallerLock {
    if ($script:InstallerLockHeld) {
        $script:InstallerMutex.ReleaseMutex()
        $script:InstallerLockHeld = $false
    }
}
function Write-Log {
    param(
        [string]$Message
    )
    $Time =
        Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    $Line =
        "[$Time] $Message"
    Write-Host "  $Line" -ForegroundColor $ColorDim
    try {
        if (-not (Test-Path -LiteralPath $DatabaseDir)) {
            New-Item `
                -ItemType Directory `
                -Path $DatabaseDir `
                -Force |
                Out-Null
        }
        Add-Content `
            -Path $LogFile `
            -Value $Line `
            -ErrorAction SilentlyContinue
    }
    catch {
    }
}
function Convert-ToVersion {
    param(
        [string]$Version
    )
    try {
        if ($Version -notmatch '^(?<Core>\d+\.\d+\.\d+(?:\.\d+)?)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$') {
            throw 'Invalid application version format.'
        }
        $Core = $Matches.Core
        if (($Core -split '\.').Count -eq 3) { $Core += '.0' }
        return [System.Version]::Parse($Core)
    }
    catch {
        throw "Invalid version number: $Version"
    }
}
function Compare-ApplicationVersion {
    param([string]$Left, [string]$Right)
    $Comparison = (Convert-ToVersion $Left).CompareTo((Convert-ToVersion $Right))
    if ($Comparison -ne 0) { return $Comparison }
    # Build metadata does not determine precedence. A final release is newer
    # than its prereleases; numeric prerelease identifiers compare numerically.
    $LeftPre = ($Left -split '\+',2)[0] -split '-',2
    $RightPre = ($Right -split '\+',2)[0] -split '-',2
    if ($LeftPre.Count -eq 1 -and $RightPre.Count -eq 1) { return 0 }
    if ($LeftPre.Count -eq 1) { return 1 }
    if ($RightPre.Count -eq 1) { return -1 }
    $LeftParts = $LeftPre[1] -split '\.'
    $RightParts = $RightPre[1] -split '\.'
    for ($Index=0; $Index -lt [Math]::Min($LeftParts.Count,$RightParts.Count); $Index++) {
        $L = $LeftParts[$Index]; $R = $RightParts[$Index]
        $LNumeric = $L -match '^\d+$'; $RNumeric = $R -match '^\d+$'
        if ($LNumeric -and $RNumeric) {
            $L = $L.TrimStart('0'); $R = $R.TrimStart('0')
            $Comparison = $L.Length.CompareTo($R.Length)
            if ($Comparison -eq 0) { $Comparison = [String]::CompareOrdinal($L,$R) }
        }
        elseif ($LNumeric) { $Comparison = -1 }
        elseif ($RNumeric) { $Comparison = 1 }
        else { $Comparison = [String]::CompareOrdinal($L,$R) }
        if ($Comparison -ne 0) { return $Comparison }
    }
    return $LeftParts.Count.CompareTo($RightParts.Count)
}
function Convert-ToDbVersion {
    param(
        [string]$Version
    )
    if ($Version -notmatch '^\d+$') {
        throw "Invalid database schema version: $Version (expected a plain integer, e.g. '1', '2', '3')"
    }
    return [int]$Version
}
function Invoke-SqlMaster {
    param(
        [string]$Query
    )
    $Output =
        & $script:SqlCmdPath `
            -S $SqlInstance `
            -E `
            -d master `
            -b `
            -Q $Query `
            2>&1
    if ($LASTEXITCODE -ne 0) {
        $Message =
            $Output -join "`r`n"
        throw (
            "SQL command failed:`r`n$Message"
        )
    }
    return $Output
}
function Invoke-SqlDatabase {
    param(
        [string]$Query
    )
    $Output =
        & $script:SqlCmdPath `
            -S $SqlInstance `
            -E `
            -d $DatabaseName `
            -b `
            -Q $Query `
            2>&1
    if ($LASTEXITCODE -ne 0) {
        $Message =
            $Output -join "`r`n"
        throw (
            "FMSC_DB SQL command failed:`r`n$Message"
        )
    }
    return $Output
}
function New-PreMigrationBackup {
    param(
        [string]$FromVersion,
        [string]$ToVersion
    )
    # A recovery point taken automatically, immediately before the first migration runs.
    $BackupFolder =
        Join-Path `
            $DatabaseDir `
            'Backups'
    New-Item `
        -ItemType Directory `
        -Path $BackupFolder `
        -Force |
        Out-Null
    $BackupBaseName =
        "FMSC_DB_BeforeMigration_v${FromVersion}_to_v${ToVersion}_" +
        (Get-Date -Format 'yyyyMMdd_HHmmss')
    $BackupPath =
        Join-Path `
            $BackupFolder `
            "$BackupBaseName.bak"
    # Never overwrite an existing recovery point: a second upgrade attempt in the same second must not destroy the first attempt's backup.
    $CollisionCounter = 1
    while (Test-Path -LiteralPath $BackupPath) {
        $BackupPath =
            Join-Path `
                $BackupFolder `
                "$BackupBaseName`_$CollisionCounter.bak"
        $CollisionCounter++
    }
    # A single quote is the only character that can break the literal, and a T-SQL string literal escapes it by doubling.
    $SqlPath =
        $BackupPath.Replace("'", "''")
    Write-Step `
        'Creating verified recovery backup...'
    Write-Log `
        "Creating pre-migration recovery backup: $BackupPath"
    try {
        Invoke-SqlMaster `
            -Query (
                "BACKUP DATABASE [$DatabaseName] " +
                "TO DISK = N'$SqlPath' " +
                "WITH INIT, CHECKSUM, STATS = 10"
            ) |
            Out-Null
    }
    catch {
        throw (
            "The pre-migration recovery backup could not be created, so no " +
            "migration was run.`r`n`r`n" +
            "Backup file : $BackupPath`r`n" +
            "Database    : $DatabaseName`r`n`r`n" +
            "The database has NOT been changed.`r`n`r`n" +
            "Underlying error: $($_.Exception.Message)"
        )
    }
    Write-Step `
        'Verifying recovery backup...'
    try {
        Invoke-SqlMaster `
            -Query (
                "RESTORE VERIFYONLY FROM DISK = N'$SqlPath' WITH CHECKSUM"
            ) |
            Out-Null
    }
    catch {
        throw (
            "The pre-migration recovery backup did not pass RESTORE " +
            "VERIFYONLY, so no migration was run.`r`n`r`n" +
            "Backup file : $BackupPath`r`n`r`n" +
            "A backup that cannot be verified is not a recovery point. The " +
            "database has NOT been changed.`r`n`r`n" +
            "Underlying error: $($_.Exception.Message)"
        )
    }
    if (-not (Test-Path -LiteralPath $BackupPath)) {
        throw (
            "SQL Server reported that the pre-migration backup succeeded, but " +
            "the backup file does not exist, so no migration was run.`r`n`r`n" +
            "Backup file : $BackupPath"
        )
    }
    $BackupSize =
        (Get-Item -LiteralPath $BackupPath).Length
    if ($BackupSize -le 0) {
        throw (
            "The pre-migration backup file was created but is empty, so no " +
            "migration was run.`r`n`r`n" +
            "Backup file : $BackupPath"
        )
    }
    $BackupSizeMb =
        [math]::Round(
            $BackupSize / 1MB,
            2
        )
    $RecordPath =
        Join-Path `
            $BackupFolder `
            'LastPreMigrationBackup.txt'
    Set-Content `
        -LiteralPath $RecordPath `
        -Value @(
            "Backup file       : $BackupPath"
            "Backup size (MB)  : $BackupSizeMb"
            "Created (UTC)     : $((Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss'))"
            "Database          : $DatabaseName"
            "Schema version    : $FromVersion -> $ToVersion"
            "Installer version : $AppVersion"
            "Verified with     : RESTORE VERIFYONLY WITH CHECKSUM"
        ) `
        -Encoding UTF8
    Write-Success `
        "Recovery backup created and verified ($BackupSizeMb MB)."
    Write-Host `
        "    $BackupPath" `
        -ForegroundColor $ColorDim
    Write-Log `
        "Recovery backup verified: $BackupPath ($BackupSizeMb MB)"
    Write-Log `
        "Recovery backup record: $RecordPath"
    Pause-Long
    return $BackupPath
}
function Get-InstalledApplicationVersion {
    $InstalledExe =
        Join-Path $AppDir 'FMSC_SMS.exe'
    if (-not (Test-Path -LiteralPath $InstalledExe)) {
        return $null
    }
    try {
        $Version =
            (Get-Item $InstalledExe).VersionInfo.ProductVersion
        if ([string]::IsNullOrWhiteSpace($Version)) {
            return $null
        }
        $Version =
            $Version.Split('+')[0].Trim()
        $null = Convert-ToVersion $Version
        return $Version
    }
    catch {
        return $null
    }
}
function Get-DatabaseVersion {
    Write-Step `
        'Reading database schema version...'
    $Query =
        "SET NOCOUNT ON; " +
        "SELECT TOP 1 VersionNumber " +
        "FROM dbo.DatabaseVersion " +
        "ORDER BY VersionId DESC;"
    $Result =
        Invoke-SqlDatabase `
            -Query $Query
    $DetectedVersion = $null
    foreach ($Line in $Result) {
        $Text =
            $Line.ToString().Trim()
        if (
            -not [string]::IsNullOrWhiteSpace($Text) -and
            $Text -match '^\d+$'
        ) {
            $DetectedVersion = $Text
        }
    }
    if ([string]::IsNullOrWhiteSpace($DetectedVersion)) {
        throw `
            'Unable to read a valid database schema version from dbo.DatabaseVersion. Expected a plain integer (e.g. 1, 2, 3).'
    }
    return $DetectedVersion
}
function Test-IsAdministrator {
    $Identity =
        [Security.Principal.WindowsIdentity]::GetCurrent()
    $Principal =
        New-Object Security.Principal.WindowsPrincipal($Identity)
    return $Principal.IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator
    )
}
function Resolve-SqlCmd {
    $root=Install-FmscMaintenanceTool 'SqlCmd' $AppSource
    return Join-Path $root 'sqlcmd.exe'
}

function Copy-ApplicationFiles {
    param(
        [string]$Source,
        [string]$Destination
    )
    # Copies the published application into a destination folder that is not the live installation. The live folder is only ever replaced by Enable-StagedApplication, never written into file by file.
    New-Item `
        -ItemType Directory `
        -Path $Destination `
        -Force |
        Out-Null
    foreach (
        $Item in
        (Get-ChildItem -LiteralPath $Source -Force)
    ) {
        $Target =
            Join-Path `
                $Destination `
                $Item.Name
        if ($Item.PSIsContainer) {
            Copy-Item `
                -LiteralPath $Item.FullName `
                -Destination $Target `
                -Recurse `
                -Force
        }
        else {
            Copy-Item `
                -LiteralPath $Item.FullName `
                -Destination $Target `
                -Force
        }
    }
}
function Get-ApplicationFileManifest {
    param(
        [string]$Root
    )
    # A recursive inventory of an application folder: every directory and file below the root, keyed by relative path.
    $Manifest = @{}
    # The root is normalised through Get-Item because Get-ChildItem builds the paths of its children in the provider's normalised (long) form.
    $Prefix =
        (Get-Item -LiteralPath $Root).FullName.TrimEnd('\') + '\'
    foreach (
        $Directory in
        (Get-ChildItem -LiteralPath $Root -Recurse -Directory -Force)
    ) {
        $Manifest[
            $Directory.FullName.Substring($Prefix.Length).TrimStart('\')
        ] = -1
    }
    foreach (
        $File in
        (Get-ChildItem -LiteralPath $Root -Recurse -File -Force)
    ) {
        $Manifest[
            $File.FullName.Substring($Prefix.Length).TrimStart('\')
        ] = $File.Length
    }
    return $Manifest
}
function Test-StagedApplication {
    param(
        [string]$Source,
        [string]$Staging
    )
    # A completed copy must have the same tree, file sizes and SHA-256 contents as the publication. Throws before activation on any mismatch; retains existing count/size log results.
    if (-not (Test-Path -LiteralPath $Staging)) {
        throw `
            "The staged application folder was not created: $Staging"
    }
    $SourceManifest =
        Get-ApplicationFileManifest -Root $Source
    $StagingManifest =
        Get-ApplicationFileManifest -Root $Staging
    $Problems = @()
    foreach ($RelativePath in $SourceManifest.Keys) {
        if (-not $StagingManifest.ContainsKey($RelativePath)) {
            $Problems += "missing: $RelativePath"
            continue
        }
        if ($StagingManifest[$RelativePath] -ne $SourceManifest[$RelativePath]) {
            $Problems += (
                "size differs: $RelativePath " +
                "(published $($SourceManifest[$RelativePath]) bytes, " +
                "staged $($StagingManifest[$RelativePath]) bytes)"
            )
            continue
        }
        # Directories are represented by -1. Even when the lengths match,
        # compare the actual published and staged file bytes before activation.
        if ($SourceManifest[$RelativePath] -ne -1) {
            $PublishedHash = (
                Get-FileHash -LiteralPath (Join-Path $Source $RelativePath) -Algorithm SHA256 -ErrorAction Stop
            ).Hash
            $StagedHash = (
                Get-FileHash -LiteralPath (Join-Path $Staging $RelativePath) -Algorithm SHA256 -ErrorAction Stop
            ).Hash
            if ($StagedHash -ne $PublishedHash) {
                $Problems += "SHA-256 differs: $RelativePath"
            }
        }
    }
    foreach ($RelativePath in $StagingManifest.Keys) {
        if (-not $SourceManifest.ContainsKey($RelativePath)) {
            $Problems += "unexpected: $RelativePath"
        }
    }
    if ($Problems.Count -gt 0) {
        $Shown =
            $Problems |
            Select-Object -First 20
        $Remainder = ''
        if ($Problems.Count -gt $Shown.Count) {
            $Remainder =
                "`r`n... and $($Problems.Count - $Shown.Count) more difference(s)."
        }
        throw (
            "The staged application does not match the published application.`r`n`r`n" +
            "Staged    : $Staging`r`n" +
            "Published : $Source`r`n`r`n" +
            ($Shown -join "`r`n") +
            $Remainder + "`r`n`r`n" +
            "Nothing has been changed: the installed application and the " +
            "database are both untouched."
        )
    }
    $FileCount = 0
    $TotalBytes = 0
    foreach ($RelativePath in $SourceManifest.Keys) {
        if ($SourceManifest[$RelativePath] -ne -1) {
            $FileCount++
            $TotalBytes +=
                $SourceManifest[$RelativePath]
        }
    }
    return [PSCustomObject]@{
        Files = $FileCount
        Bytes = $TotalBytes
    }
}
function Enable-StagedApplication {
    param(
        [string]$Staging,
        [string]$AppDir,
        [string]$RollbackDir
    )
    # activation is a pair of folder renames on the same volume, not a file-by-file overwrite of the live installation.
    if (-not (Test-Path -LiteralPath $Staging)) {
        throw `
            "There is no staged application to activate: $Staging"
    }
    if (-not (Test-Path -LiteralPath (Join-Path $Staging 'FMSC_SMS.exe'))) {
        throw `
            "The staged application does not contain FMSC_SMS.exe: $Staging"
    }
    $HadPreviousInstall =
        Test-Path -LiteralPath $AppDir
    $HadRecoveryRollback = $script:RecoveryJournal -and (Test-Path -LiteralPath $RollbackDir)
    if ($script:RecoveryJournal) {
        # Activating is written BEFORE the first rename. On recovery, any
        # existing rollback is this transaction's baseline and must be preserved.
        if ($script:RecoveryJournal.Phase -ne 'Activating') {
            if (Test-Path -LiteralPath $RollbackDir) {
                Remove-VerifiedUpdateDirectory $RollbackDir "$AppDir.rollback"
            }
            Set-FmscRecoveryPhase 'Activating'
        } elseif ((Test-Path -LiteralPath $RollbackDir) -and $HadPreviousInstall) {
            Remove-VerifiedUpdateDirectory $AppDir $AppDir
            $HadPreviousInstall = $false
        }
    }
    if ($HadPreviousInstall) {
        if (Test-Path -LiteralPath $RollbackDir) {
            Write-Log `
                "Removing the previous rollback copy: $RollbackDir"
            Remove-VerifiedUpdateDirectory $RollbackDir "$AppDir.rollback"
        }
        Write-Log `
            "Setting the installed application aside: $AppDir -> $RollbackDir"
        Move-Item `
            -LiteralPath $AppDir `
            -Destination $RollbackDir `
            -Force
    }
    try {
        Move-Item `
            -LiteralPath $Staging `
            -Destination $AppDir `
            -Force
    }
    catch {
        $ActivationError =
            $_.Exception.Message
        $Restored = $false
        # A directory move that fails part-way can leave a partially populated destination behind. Any $AppDir present at this point is such an artefact: the previous application was already moved aside into $RollbackDir.
        if (Test-Path -LiteralPath $AppDir) {
            try {
                Remove-Item `
                    -LiteralPath $AppDir `
                    -Recurse `
                    -Force
                Write-Log `
                    "Removed the partially activated application folder: $AppDir"
            }
            catch {
                Write-Log `
                    "The partially activated application folder could not be removed: $($_.Exception.Message)"
            }
        }
        if (
            ($HadPreviousInstall -or $HadRecoveryRollback) -and
            (Test-Path -LiteralPath $RollbackDir) -and
            (-not (Test-Path -LiteralPath $AppDir))
        ) {
            try {
                Move-Item `
                    -LiteralPath $RollbackDir `
                    -Destination $AppDir `
                    -Force
                $Restored = $true
            }
            catch {
                Write-Log `
                    "The previous application could not be restored automatically: $($_.Exception.Message)"
            }
        }
        if ($Restored) {
            $Recovery =
                "The previous application was put back in place at $AppDir."
        }
        elseif (Test-Path -LiteralPath $RollbackDir) {
            $Recovery =
                "The previous application is intact at $RollbackDir and must " +
                "be moved back to $AppDir by hand."
        }
        else {
            $Recovery =
                "No previous application was installed."
        }
        throw (
            "The staged application could not be activated.`r`n`r`n" +
            "Staged             : $Staging`r`n" +
            "Application folder : $AppDir`r`n`r`n" +
            $Recovery + "`r`n`r`n" +
            "Any completed database migrations remain applied; keep the recorded recovery backup.`r`n`r`n" +
            "Underlying error: $ActivationError"
        )
    }
    if (-not (Test-Path -LiteralPath (Join-Path $AppDir 'FMSC_SMS.exe'))) {
        throw `
            "The application was activated, but FMSC_SMS.exe is not present in $AppDir."
    }
    Write-Log `
        "Application activated: $AppDir"
}
# Remove only application/update artifacts after successful activation.
function Remove-VerifiedUpdateDirectory {
    param([string]$Path, [string]$ExpectedPath)
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $expected = [IO.Path]::GetFullPath($ExpectedPath).TrimEnd('\')
    if ($full -ine $expected -or $full -eq [IO.Path]::GetPathRoot($full).TrimEnd('\')) {
        throw 'Refusing cleanup outside the expected update directory.'
    }
    if (-not (Test-Path -LiteralPath $full)) { return }
    $ancestor = Get-Item -LiteralPath $full -Force
    while ($null -ne $ancestor) {
        if ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing cleanup through a link.' }
        $ancestor = $ancestor.Parent
    }
    foreach ($entry in Get-ChildItem -LiteralPath $full -Recurse -Force -ErrorAction Stop) {
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing cleanup of a directory containing links.' }
    }
    Remove-Item -LiteralPath $full -Recurse -Force -ErrorAction Stop
}
function Remove-OldPreMigrationBackups {
    if (-not $DatabaseDir) { return }
    $folder = Join-Path $DatabaseDir 'Backups'
    if (-not (Test-Path -LiteralPath $folder -PathType Container)) { return }
    for ($item = Get-Item -LiteralPath $folder -Force; $null -ne $item; $item = $item.Parent) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw 'Linked backup directory refused.'
        }
    }
    $backups = @(Get-ChildItem -LiteralPath $folder -File -Force | ForEach-Object {
        if (-not ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -and
            $_.Name -match '^FMSC_DB_BeforeMigration_v[0-9]+_to_v[0-9]+_(?<stamp>[0-9]{8}_[0-9]{6})(?:_(?<sequence>[0-9]+))?\.bak$') {
            [pscustomobject]@{ Path = $_.FullName; Stamp = $Matches.stamp; Sequence = [long]$Matches.sequence }
        }
    } | Sort-Object -Property Stamp, Sequence -Descending)
    if ($backups.Count -le 2) { return }

    # Verify both retained recovery points before removing any older file.
    foreach ($backup in ($backups | Select-Object -First 2)) {
        $sqlPath = $backup.Path.Replace("'", "''")
        Invoke-SqlMaster -Query "RESTORE VERIFYONLY FROM DISK = N'$sqlPath' WITH CHECKSUM" | Out-Null
    }
    foreach ($backup in ($backups | Select-Object -Skip 2)) {
        Remove-Item -LiteralPath $backup.Path -ErrorAction Stop
        Write-Log "Removed older pre-migration backup: $($backup.Path)"
    }
}

# Retain the most recently installed verified prepared package for offline
# inspection/retry; prune only older GUID-named prepared folders and redundant
# chunk-cache hash folders after the activated application has passed verification.
# Never remove enrollment/DPAPI keys, failed-update evidence or arbitrary files.
function Remove-SupersededPreparedUpdates {
    param([string]$PreservePrepared)
    $state = [IO.Path]::GetFullPath((Get-FmscDownloadParent)).TrimEnd('\')
    $keep = [IO.Path]::GetFullPath($PreservePrepared).TrimEnd('\')
    if ((Split-Path -Parent $keep) -ine $state -or
        (Split-Path -Leaf $keep) -notmatch '^prepared-[a-f0-9]{32}$' -or
        -not (Test-Path -LiteralPath $keep -PathType Container)) {
        throw 'The verified package must be an existing prepared folder in this user update cache.'
    }
    if ($script:RecoveryJournal -and
        ($script:RecoveryJournal.Phase -notin @('Verified','Completed') -or
         $script:RecoveryJournal.DownloadRoot -ine $keep)) {
        throw 'Recovery has not verified this prepared package; cache cleanup refused.'
    }
    # Ensure we are not traversing a junction/symlink for the cache root or
    # retained package, before deleting anything else.
    foreach ($path in @($state, $keep)) {
        $ancestor = Get-Item -LiteralPath $path -Force -ErrorAction Stop
        while ($null -ne $ancestor) {
            if ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw 'Linked update storage is not eligible for automatic cleanup.'
            }
            $ancestor = $ancestor.Parent
        }
    }
    foreach ($entry in Get-ChildItem -LiteralPath $state -Directory -Force) {
        if ($entry.Name -notmatch '^prepared-[a-f0-9]{32}$' -or
            $entry.FullName -ieq $keep) { continue }
        try {
            Remove-VerifiedUpdateDirectory -Path $entry.FullName -ExpectedPath (Join-Path $state $entry.Name)
            Write-Log "Removed superseded prepared package: $($entry.FullName)"
        } catch {
            Write-WarningMessage "Could not remove old prepared package $($entry.Name): $($_.Exception.Message)"
        }
    }
    $cache = Join-Path $state 'download-cache'
    if (-not (Test-Path -LiteralPath $cache)) { return }
    $ancestor = Get-Item -LiteralPath $cache -Force
    while ($null -ne $ancestor) {
        if ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw 'Linked chunk-cache storage refused.'
        }
        $ancestor = $ancestor.Parent
    }
    foreach ($entry in Get-ChildItem -LiteralPath $cache -Directory -Force) {
        if ($entry.Name -notmatch '^[a-f0-9]{64}$') { continue }
        try {
            Remove-VerifiedUpdateDirectory -Path $entry.FullName -ExpectedPath (Join-Path $cache $entry.Name)
            Write-Log "Removed redundant downloaded chunks: $($entry.FullName)"
        } catch {
            Write-WarningMessage "Could not remove old chunks $($entry.Name): $($_.Exception.Message)"
        }
    }
}

function Complete-VerifiedApplicationUpdate {
    param([string]$ApplicationDirectory, [string]$Source, [string]$Prepared = '')
    # Verification failures keep rollback and downloads for recovery.
    $null = Test-StagedApplication -Source $Source -Staging $ApplicationDirectory
    if ($Prepared) {
        Test-PreparedUpdate -Root $Prepared -Version $AppVersion -Schema ([int]$DatabaseVersion) -Application $ApplicationDirectory
    }
    $result = Invoke-InstallationDatabaseCheck -Application (Join-Path $ApplicationDirectory 'FMSC_SMS.exe')
    if ($result -ne 0) { throw "Activated application verification failed (code $result). Recovery files have been kept." }
    if ($script:RecoveryJournal) { Complete-FmscRecoveryTransaction }
    # Prune superseded admin-owned tool bundles only after successful app/SQL
    # verification. Retain the currently integrity-verified copies and all
    # other catalog versions; a failed update must never delete maintenance.
    try { Remove-FmscSupersededMaintenanceTools }
    catch { Write-WarningMessage "Update succeeded; redundant maintenance-tool cleanup is pending: $($_.Exception.Message)" }
    try { Remove-OldPreMigrationBackups }
    catch { Write-WarningMessage "Update succeeded; pre-migration backup cleanup is pending: $($_.Exception.Message)" }
    $rollback = "$ApplicationDirectory.rollback"
    try {
        Remove-VerifiedUpdateDirectory -Path $rollback -ExpectedPath $rollback
        Write-Log 'Activated application verified; previous application copy removed.'
    } catch { Write-WarningMessage "Update succeeded; previous application cleanup is pending: $($_.Exception.Message)" }
    if (-not $Prepared) { return }
    if ($script:RecoveryJournal) { $Prepared = $script:RecoveryJournal.DownloadRoot }
    try {
        Remove-SupersededPreparedUpdates -PreservePrepared $Prepared
        Write-Log 'Successful update: latest verified package retained, older prepared packages and redundant chunks pruned.'
    } catch { Write-WarningMessage "Update succeeded; downloaded-file cleanup is pending: $($_.Exception.Message)" }

}
function Invoke-InstallationDatabaseCheck {
    param([string]$Application)
    $arguments = @('--verify-installation', ('"' + $SqlInstance + '"'), ('"' + $DatabaseName + '"'))
    $process = Start-Process -FilePath $Application -ArgumentList $arguments -PassThru -WindowStyle Hidden
    $null = $process.Handle
    if (-not $process.WaitForExit(180000)) {
        try { $process.Kill() } catch { }
        throw 'Database verification timed out. The staged application was not activated.'
    }
    return $process.ExitCode
}
function Invoke-SqlMigration {
    param([string]$FilePath, [int]$VersionBefore, [int]$ExpectedVersion)
    $fullPath = [IO.Path]::GetFullPath($FilePath)
    $migrationSql = [IO.File]::ReadAllText($fullPath, [Text.Encoding]::UTF8)
    $wrapperPath = Join-Path ([IO.Path]::GetTempPath()) ('FMSC_migration_' + [Guid]::NewGuid().ToString('N') + '.sql')
    # One sqlcmd connection owns the lock and transaction across every GO batch.
    # Scripts may use a balanced nested transaction and must record their filename version.
    $wrapper = @"
:on error exit
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;
SET NUMERIC_ROUNDABORT OFF;
DECLARE @Lock int;
EXEC @Lock=sys.sp_getapplock @Resource=N'FMSC_SchemaMigration',@LockMode=N'Exclusive',@LockOwner=N'Session',@LockTimeout=0;
IF @Lock<0 THROW 51000,'Another database migration is running.',1;
DECLARE @Before int=(SELECT TOP(1) TRY_CONVERT(int,VersionNumber) FROM dbo.DatabaseVersion ORDER BY VersionId DESC);
IF @Before IS NULL OR @Before<>$VersionBefore THROW 51000,'Database version changed since migration planning; rerun the installer.',1;
BEGIN TRANSACTION;
GO
$migrationSql
GO
IF @@TRANCOUNT<>1 THROW 51000,'Migration did not preserve the installer transaction.',1;
DECLARE @After int=(SELECT TOP(1) TRY_CONVERT(int,VersionNumber) FROM dbo.DatabaseVersion ORDER BY VersionId DESC);
IF @After IS NULL OR @After<>$ExpectedVersion THROW 51000,'Migration did not record its expected database version.',1;
COMMIT TRANSACTION;
"@
    try {
        [IO.File]::WriteAllText($wrapperPath, $wrapper, [Text.UTF8Encoding]::new($false))
        # Pinned Microsoft go-sqlcmd reads UTF-8 input natively; it does not
        # support the ODBC sqlcmd-only -f code-page switch.
        $output = & $script:SqlCmdPath -S $SqlInstance -E -d $DatabaseName -C -b -i $wrapperPath 2>&1
        foreach ($line in $output) { Write-Log $line.ToString() }
        if ($LASTEXITCODE -ne 0) {
            throw "Migration $ExpectedVersion failed. The staged application was not activated. Keep the verified recovery backup and inspect the database before retrying."
        }
    } finally {
        if (Test-Path -LiteralPath $wrapperPath) { Remove-Item -LiteralPath $wrapperPath -Force }
    }
}
function Invoke-PendingMigrations {
    param([string]$Directory, [int]$RequiredVersion)
    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) {
        Write-Log 'No migrations folder; skipping database migrations.'
        return
    }
    $files = @(Get-ChildItem -LiteralPath $Directory -Filter '*.sql' -File)
    if ($files.Count -eq 0) {
        Write-Log 'Migrations folder has no SQL files; skipping database migrations.'
        return
    }
    $versions = @{}
    $migrations = @(foreach ($file in $files) {
        if ($file.BaseName -notmatch '^[1-9][0-9]*$') {
            throw "Invalid migration filename: $($file.Name). Use a positive integer such as 8.sql. No migrations were run."
        }
        $version = Convert-ToDbVersion $file.BaseName
        if ($versions.ContainsKey($version)) { throw "Duplicate migration version $version. No migrations were run." }
        $versions[$version] = $true
        [pscustomobject]@{ FilePath = $file.FullName; Version = $version }
    })
    $current = Convert-ToDbVersion (Get-DatabaseVersion)
    if ($script:RecoveryJournal) { Confirm-FmscRecoveryBackup -CurrentSchema $current }
    $pending = @($migrations | Where-Object Version -gt $current | Sort-Object Version)
    if ($pending.Count -eq 0) {
        Write-Log "No migrations newer than database version $current; skipping database migrations."
        return
    }
    if ($current -gt $RequiredVersion -or ($pending | Where-Object Version -gt $RequiredVersion)) {
        throw "Pending SQL exceeds this release's supported schema version $RequiredVersion. No migrations were run. Use the matching application release."
    }
    $target = $pending[-1].Version
    if ($target -ne $RequiredVersion) {
        throw "Available migrations stop at $target, but this release requires $RequiredVersion. No migrations were run."
    }
    # Refuse an incomplete upgrade BEFORE taking a backup or modifying SQL.
    # A target-version file alone cannot prove intermediate migrations were supplied.
    $expected = $current + 1
    foreach ($migration in $pending) {
        if ($migration.Version -ne $expected) {
            throw "Missing required migration $expected.sql (database schema $current, target $RequiredVersion). No migrations were run."
        }
        $expected++
    }
    if ($script:RecoveryJournal -and $script:RecoveryJournal.Backup) {
        $script:RecoveryBackupPath = $script:RecoveryJournal.Backup
    } else {
        $script:RecoveryBackupPath = New-PreMigrationBackup -FromVersion "$current" -ToVersion "$target"
        if ($script:RecoveryJournal) {
            $script:RecoveryJournal.Backup = $script:RecoveryBackupPath
            $script:RecoveryJournal.BackupSha256 = (Get-FileHash -LiteralPath $script:RecoveryBackupPath -Algorithm SHA256).Hash
            $script:RecoveryJournal.BeforeSchema = $current
            if ($script:RecoveryJournal.Phase -eq 'Prepared') { $script:RecoveryJournal.Phase = 'Migrating' }
            Save-FmscRecoveryJournal $script:RecoveryRoot $script:RecoveryJournal
        }
    }
    foreach ($migration in $pending) {
        if ($script:RecoveryJournal -and (Get-Process -Name 'FMSC_SMS' -ErrorAction SilentlyContinue)) {
            throw 'FMSC was reopened during installation. Close it and use the recovery shortcut; no further migration was run.'
        }
        Write-Step "Applying database migration $($migration.Version)..."
        Invoke-SqlMigration -FilePath $migration.FilePath -VersionBefore $current -ExpectedVersion $migration.Version
        $actual = Convert-ToDbVersion (Get-DatabaseVersion)
        if ($actual -ne $migration.Version) { throw "Migration $($migration.Version) did not leave the expected database version. Application activation stopped." }
        $current = $actual
        if ($script:RecoveryJournal) {
            $script:RecoveryJournal.LastSchema = $actual
            Save-FmscRecoveryJournal $script:RecoveryRoot $script:RecoveryJournal
        }
    }
}

function Invoke-LocalDbMsiInstallation {
    param([Parameter(Mandatory=$true)][string]$MsiPath,
          [Parameter(Mandatory=$true)][string]$LogDirectory)
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $silentLog = Join-Path $LogDirectory ("SqlLocalDB-Silent-$stamp.log")
    $wizardLog = Join-Path $LogDirectory ("SqlLocalDB-Wizard-$stamp.log")
    Write-Log "LocalDB silent MSI diagnostic log: $silentLog"
    $silent = Start-Process -FilePath 'msiexec.exe' -ArgumentList @(
        '/i', "`"$MsiPath`"", '/qn', '/norestart', '/L*V', "`"$silentLog`""
    ) -Wait -PassThru
    if ($silent.ExitCode -in @(3010, 1641)) {
        throw "LocalDB setup requires a Windows restart (MSI code $($silent.ExitCode)). Restart Windows and rerun FMSC. Log: $silentLog"
    }
    if ($silent.ExitCode -eq 0) {
        Write-Success 'SQL Server LocalDB installed silently.'
        return
    }
    $silentCode = $silent.ExitCode
    Write-WarningMessage "Silent LocalDB installation failed (MSI code $silentCode)."
    Write-WarningMessage "Detailed Microsoft Installer log: $silentLog"
    if ($PreparedRoot) {
        throw "LocalDB repair requires support. Update stopped without opening an installation wizard. Log: $silentLog"
    }
    Write-Host '  You can complete LocalDB setup using the official Microsoft installer wizard.' -ForegroundColor $ColorInfo
    if ($ShowInstallerWindow -and $script:UpdateWindow) {
        $answer = [Windows.Forms.MessageBox]::Show($script:UpdateWindow,
            "Microsoft LocalDB could not be installed silently (code $silentCode)." +
            [Environment]::NewLine + [Environment]::NewLine +
            'Would you like to open the official Microsoft installer wizard?' +
            [Environment]::NewLine + "Diagnostic log: $silentLog",
            'FMSC - LocalDB Setup', [Windows.Forms.MessageBoxButtons]::YesNo,
            [Windows.Forms.MessageBoxIcon]::Warning)
        $choice = if ($answer -eq [Windows.Forms.DialogResult]::Yes) { 'Y' } else { 'N' }
    } else {
        $choice = Read-Host 'Open Microsoft LocalDB installation wizard now? (Y/N)'
    }
    if ($choice -notmatch '^[Yy]$') {
        throw "LocalDB not installed. Silent MSI code: $silentCode. Check $silentLog and rerun the installer."
    }
    Write-Step 'Starting the official Microsoft LocalDB installer wizard...'
    Write-Log "LocalDB wizard MSI diagnostic log: $wizardLog"
    $wizard = Start-Process -FilePath 'msiexec.exe' -ArgumentList @(
        '/i', "`"$MsiPath`"", '/norestart', '/L*V', "`"$wizardLog`""
    ) -Wait -PassThru
    if ($wizard.ExitCode -in @(3010, 1641)) {
        throw "LocalDB setup requires a Windows restart (MSI code $($wizard.ExitCode)). Restart Windows and rerun FMSC. Log: $wizardLog"
    }
    if ($wizard.ExitCode -ne 0) {
        throw "LocalDB setup failed. Silent MSI code: $silentCode; wizard MSI code: $($wizard.ExitCode). Logs: $silentLog and $wizardLog"
    }
    Write-Success 'SQL Server LocalDB installed through the Microsoft setup wizard.'
}

function Test-PreparedUpdate {
    param([string]$Root, [string]$Version, [int]$Schema, [string]$Application = '')
    if (-not $Application) { $Application = Join-Path $Root 'application' }
    $application = [IO.Path]::GetFullPath($Application)
    $manifest = Get-Content -LiteralPath (Join-Path $Root 'metadata/update-manifest.json') -Raw | ConvertFrom-Json
    if ($manifest.format -ne 'fmsc-app-update/v1' -or $manifest.version -ne $Version -or
        $manifest.requiredDatabaseSchema -ne $Schema -or @($manifest.files).Count -eq 0) {
        throw 'Prepared update does not match this installer version/schema.'
    }
    $entries = @(Get-ChildItem -LiteralPath $application -Force -Recurse)
    if (@($entries | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) {
        throw 'Links are forbidden in a prepared update.'
    }
    $seen = @{}
    foreach ($file in $manifest.files) {
        $name = [string]$file.path
        if (-not $name -or $name.Contains('\') -or $name.Contains(':') -or
            $name.StartsWith('/') -or @($name.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -or
            $name -match '(?i)\.(mdf|ldf)$' -or $seen.ContainsKey($name)) { throw 'Unsafe update file path.' }
        $seen[$name] = $true
        $path = [IO.Path]::GetFullPath((Join-Path $application $name))
        if (-not $path.StartsWith($application + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Prepared update path escapes application directory.'
        }
        $info = Get-Item -LiteralPath $path -ErrorAction Stop
        if ($info.PSIsContainer -or $info.Length -ne $file.length -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) {
            throw "Prepared update file verification failed: $name"
        }
    }
    if (@($entries | Where-Object { -not $_.PSIsContainer }).Count -ne $seen.Count -or
        -not $seen.ContainsKey('FMSC_SMS.exe') -or -not $seen.ContainsKey('UpdateSupport/core.ps1')) {
        throw 'Prepared application inventory is incomplete or contains unlisted files.'
    }
}

# Abort only when nothing from the attempted update has been activated and
# the live SQL schema still matches the journal's recorded pre-migration state.
# A partial migration or missing proof must remain pending for supervised recovery.
# A prepared update is not committed until the new application and database
# pass final verification. Existing per-file SQL transactions remain unchanged.
# Restore a verified pre-migration snapshot if an earlier file committed, then
# return the protected original application directory to its original name.
function Invoke-FmscRestoreMigrationBaseline {
    param([int]$ObservedSchema)
    $record = $script:RecoveryJournal
    if (-not $record.Backup -or $record.BeforeSchema -le 0 -or
        $ObservedSchema -lt $record.BeforeSchema -or $ObservedSchema -gt $record.Schema) {
        throw 'Database version is outside the recorded update transaction. Manual recovery is required.'
    }
    Confirm-FmscRecoveryBackup -CurrentSchema $ObservedSchema
    # A journal showing a committed migration must restore the exact backup,
    # even if someone independently reset the schema marker to the old number.
    if ($ObservedSchema -eq $record.BeforeSchema -and $record.LastSchema -le $record.BeforeSchema) { return }
    # Refuse concurrent SQL clients rather than forcibly disconnecting them.
    $active = @((Invoke-SqlMaster -Query "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE database_id=DB_ID(N'$DatabaseName');") |
        ForEach-Object { $_.ToString().Trim() } | Where-Object { $_ -match '^[0-9]+$' })
    if ($active.Count -ne 1 -or [int]$active[0] -ne 0) {
        throw 'Another SQL session is connected. Close it before retrying recovery; no restore was performed.'
    }
    $registered = (Invoke-SqlMaster -Query "SET NOCOUNT ON; SELECT physical_name FROM sys.master_files WHERE database_id=DB_ID(N'$DatabaseName') ORDER BY type;") -join [Environment]::NewLine
    if ($registered -notmatch [regex]::Escape($LiveMdf) -or
        $registered -notmatch [regex]::Escape($LiveLdf)) {
        throw 'The active school database files differ from the recorded installation. No restore was performed.'
    }
    # Preserve the failed partial state as an independent SQL backup before
    # replacing it. If that backup cannot be verified, refuse to roll back.
    $safetyFile = Join-Path (Join-Path $DatabaseDir 'Backups') (
        'FMSC_DB_FailedUpdateBeforeRollback_' + (Get-Date -Format 'yyyyMMdd_HHmmss') +
        '_' + [Guid]::NewGuid().ToString('N') + '.bak')
    $escapedSafety = $safetyFile.Replace("'","''")
    Write-Step 'Preserving failed database state before safe rollback...'
    Invoke-SqlMaster -Query "BACKUP DATABASE [$DatabaseName] TO DISK=N'$escapedSafety' WITH INIT,CHECKSUM;" | Out-Null
    Invoke-SqlMaster -Query "RESTORE VERIFYONLY FROM DISK=N'$escapedSafety' WITH CHECKSUM;" | Out-Null
    if (-not (Test-Path -LiteralPath $safetyFile -PathType Leaf) -or
        (Get-Item -LiteralPath $safetyFile).Length -le 0) {
        throw 'The partial-state recovery backup was not verified. Original database was not restored.'
    }
    Write-Log "Failed-update SQL safety backup retained: $safetyFile"
    $escapedOriginal = $record.Backup.Replace("'","''")
    # NO_WAIT refuses other connections. Restore and change back to MULTI_USER
    # from the same master sqlcmd session. Interrupted SQL restores are NOT
    # considered successful; the journal remains pending for recovery.
    Write-Step "Restoring database schema $($record.BeforeSchema) from verified backup..."
    Invoke-SqlMaster -Query (
        "ALTER DATABASE [$DatabaseName] SET SINGLE_USER WITH NO_WAIT;" +
        "RESTORE DATABASE [$DatabaseName] FROM DISK=N'$escapedOriginal' WITH REPLACE,CHECKSUM,RECOVERY;" +
        "ALTER DATABASE [$DatabaseName] SET MULTI_USER;"
    ) | Out-Null
    $restored = Convert-ToDbVersion (Get-DatabaseVersion)
    if ($restored -ne $record.BeforeSchema) {
        throw "Restoration finished at unexpected schema $restored. Recovery remains pending."
    }
    Assert-FmscRecoveryDatabaseIdentity
    Write-Log "Database restored to original schema $restored from recorded recovery backup."
}
function Invoke-FmscRollbackFailedUpdate {
    $record = $script:RecoveryJournal
    if (-not $record -or $record.Phase -notin @('Prepared','Migrating','Activating')) {
        throw 'Only an unverified update can be automatically rolled back.'
    }
    if (Get-Process -Name 'FMSC_SMS' -ErrorAction SilentlyContinue) {
        throw 'FMSC was reopened. Close all application windows before recovery.'
    }
    if ($record.Phase -eq 'Prepared') {
        Invoke-FmscSafeUpdateAbort
        if (Test-Path -LiteralPath $AppStagingDir) {
            try { Remove-VerifiedUpdateDirectory $AppStagingDir $AppStagingDir }
            catch { Write-WarningMessage "Rollback complete; staging cleanup pending: $($_.Exception.Message)" }
        }
        return
    }
    # The original application must be recoverable before modifying SQL.
    # The digest is recorded at transaction creation, before any folder swap.
    $baselineExe = Join-Path $AppDir 'FMSC_SMS.exe'
    $rollbackExe = Join-Path $AppRollbackDir 'FMSC_SMS.exe'
    $hasRollback = Test-Path -LiteralPath $AppRollbackDir -PathType Container
    $candidate = if ($hasRollback) { $rollbackExe } else { $baselineExe }
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw 'The previous application is missing. Database restore was not attempted.'
    }
    $baselineHash = [string]$record.BeforeAppSha256
    if ($baselineHash) {
        if ((Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash -ne $baselineHash) {
            throw 'The previous application digest does not match the recorded baseline. Recovery stopped.'
        }
    } elseif ($hasRollback -or $record.Phase -eq 'Activating') {
        throw 'Original application digest is missing. Supervised recovery is required.'
    } else {
        # Legacy recovery journals have no baseline digest, and may only
        # abandon pre-activation updates with a proven older app version.
        $installedVersion = Get-InstalledApplicationVersion
        if (-not $installedVersion -or
            (Compare-ApplicationVersion $installedVersion $record.Version) -ge 0) {
            throw 'Cannot verify the previous executable version. Recovery stopped.'
        }
    }
    if ($record.BeforeSchema -le 0 -or -not $record.DatabaseIdentity) {
        throw 'Original database schema or identity was not recorded. Recovery stopped.'
    }
    $record | Add-Member -NotePropertyName RollbackRequested -NotePropertyValue $true -Force
    Save-FmscRecoveryJournal $script:RecoveryRoot $record
    Initialize-FmscRecoveryConnection
    Assert-FmscRecoveryDatabaseIdentity
    $observed = Convert-ToDbVersion (Get-DatabaseVersion)
    if ($record.Backup) {
        Invoke-FmscRestoreMigrationBaseline -ObservedSchema $observed
    } elseif ($record.Phase -ne 'Activating' -or $observed -ne $record.BeforeSchema) {
        throw 'An advanced database has no verified restore point. Recovery stopped.'
    }
    if ((Convert-ToDbVersion (Get-DatabaseVersion)) -ne $record.BeforeSchema) {
        throw 'Original database schema not verified; previous application will not be activated.'
    }
    # A SQL restore can finish but leave SINGLE_USER enabled if the final
    # ALTER fails. Never declare the old app usable in that state.
    $dbState = (Invoke-SqlMaster -Query "SET NOCOUNT ON; SELECT state_desc + '/' + user_access_desc FROM sys.databases WHERE name=N'$DatabaseName';") -join ' '
    if ($dbState -notmatch 'ONLINE/MULTI_USER') {
        throw "Database is not ONLINE/MULTI_USER after rollback ($dbState). Recovery remains pending."
    }
    $record.LastSchema = [int]$record.BeforeSchema
    Save-FmscRecoveryJournal $script:RecoveryRoot $record
    if ($hasRollback) {
        if (Test-Path -LiteralPath $AppDir) {
            Remove-VerifiedUpdateDirectory $AppDir $AppDir
        }
        Move-Item -LiteralPath $AppRollbackDir -Destination $AppDir
    }
    $verifiedHash = (Get-FileHash -LiteralPath $baselineExe -Algorithm SHA256).Hash
    if ($baselineHash -and $verifiedHash -ne $baselineHash) {
        throw 'Restored application digest does not match the original. Recovery remains pending.'
    }
    Complete-FmscAbortedTransaction -ObservedSchema $record.BeforeSchema -ApplicationDirectory $AppDir -RollbackDirectory $AppRollbackDir
    if (Test-Path -LiteralPath $AppStagingDir) {
        try { Remove-VerifiedUpdateDirectory $AppStagingDir $AppStagingDir }
        catch { Write-WarningMessage "Rollback complete; staged application cleanup pending: $($_.Exception.Message)" }
    }
    Write-Log "Update rollback verified: original application and database schema $($record.BeforeSchema)."
}

function Invoke-FmscSafeUpdateAbort {
    if (-not $script:RecoveryJournal -or $script:RecoveryJournal.Phase -notin @('Prepared','Migrating')) {
        throw 'This recovery phase cannot be automatically abandoned.'
    }
    if (Get-Process -Name 'FMSC_SMS' -ErrorAction SilentlyContinue) {
        throw 'Close FMSC before verifying and abandoning the failed update.'
    }
    $installedVersion = Get-InstalledApplicationVersion
    $comparison = if ($installedVersion) { Compare-ApplicationVersion $installedVersion $script:RecoveryJournal.Version } else { 1 }
    $installedExe = Join-Path $AppDir 'FMSC_SMS.exe'
    $previousHash = [string]$script:RecoveryJournal.BeforeAppSha256
    if (-not $installedVersion -or $comparison -gt 0 -or
        ($comparison -eq 0 -and -not $previousHash) -or
        ($previousHash -and ((Get-FileHash -LiteralPath $installedExe -Algorithm SHA256).Hash -ne $previousHash))) {
        throw 'The previous application executable cannot be verified. Recovery stopped.'
    }
    $observedSchema = 0
    if ($script:RecoveryJournal.Phase -eq 'Migrating') {
        if (-not $script:RecoveryJournal.Backup) {
            throw 'The migration recovery backup is missing from the journal.'
        }
        Initialize-FmscRecoveryConnection
        Assert-FmscRecoveryDatabaseIdentity
        $observedSchema = Convert-ToDbVersion (Get-DatabaseVersion)
        if ($observedSchema -ne $script:RecoveryJournal.BeforeSchema) {
            throw 'The database schema changed during the failed update. Automatic recovery stopped to protect school data.'
        }
        Confirm-FmscRecoveryBackup -CurrentSchema $observedSchema
    }
    Complete-FmscAbortedTransaction -ObservedSchema $observedSchema -ApplicationDirectory $AppDir -RollbackDirectory $AppRollbackDir
    Write-Log "Safe update abort verified: previous application $installedVersion, database schema $observedSchema, failed release $($script:RecoveryJournal.Version)."
}

try {
    Open-UpdateWindow
    $existingRecovery = Read-FmscRecoveryJournal $script:RecoveryRoot
    if ($ResumeUpdate) {
        if (-not $existingRecovery -or $existingRecovery.Phase -in @('Completed','Aborted')) {
            throw 'No unfinished update was found. Open FMSC from its shortcut.'
        }
        Assert-FmscResumeIdentity $existingRecovery $script:RecoveryRoot $AppVersion ([int]$DatabaseVersion) $PSCommandPath
        $script:RecoveryJournal = $existingRecovery
        $PreparedRoot = Join-Path $script:RecoveryRoot ('transactions/' + $existingRecovery.Transaction)
        $AppSource = Join-Path $PreparedRoot 'application'
        $MigrationsDir = Join-Path $AppSource 'UpdateSupport/Migrations'
        $ReleaseRoot = $script:RecoveryRoot
        $script:RecoveryBackupPath = $existingRecovery.Backup
    } elseif ($existingRecovery -and $existingRecovery.Phase -notin @('Completed','Aborted')) {
        throw 'An unfinished update exists. Use the FMSC desktop shortcut to recover it first.'
    }
    if ($PreparedRoot) {
        if (-not $ResumeUpdate) {
            Wait-OriginalFmscExit -ProcessId $OriginalProcessId -StartTicks $OriginalStartTicks -UserSid $OriginalUserSid
        }
        if (-not (Test-Path -LiteralPath $LiveMdf -PathType Leaf) -or
            -not (Test-Path -LiteralPath $LiveLdf -PathType Leaf)) {
            throw 'An existing complete school database is required. Update will never copy template database files.'
        }
    }
    $script:InstallerMutex = New-Object Threading.Mutex($false, 'Global\FMSCInstallerActivation')
    $script:InstallerLockHeld = $false
    try { $script:InstallerLockHeld = $script:InstallerMutex.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $script:InstallerLockHeld = $true }
    if (-not $script:InstallerLockHeld) { throw 'Another FMSC installer is active. No update was installed.' }
    if ($ResumeUpdate -and (Get-Process -Name 'FMSC_SMS' -ErrorAction SilentlyContinue)) {
        throw 'Close all FMSC windows before recovering the update.'
    }
    if ($ResumeUpdate -and $script:RecoveryJournal.Phase -in @('Prepared','Migrating','Activating')) {
        Invoke-FmscRollbackFailedUpdate
        Write-Success 'The interrupted update was rolled back. The previous application and database are available.'
        [Windows.Forms.MessageBox]::Show(
            'The interrupted update was rolled back after verifying the previous application and database.' +
            [Environment]::NewLine + [Environment]::NewLine +
            'Open FMSC from its desktop shortcut. The downloaded update may be retained for inspection.',
            'FMSC - Previous Installation Restored', 'OK', 'Information') | Out-Null
        Release-FmscInstallerLock
        return
    }
    if ($ResumeUpdate -and $script:RecoveryJournal.Phase -eq 'Verified') {
        Initialize-FmscRecoveryConnection
        Assert-FmscRecoveryDatabaseIdentity
        Confirm-FmscRecoveryBackup -CurrentSchema (Convert-ToDbVersion (Get-DatabaseVersion))
        Complete-VerifiedApplicationUpdate -ApplicationDirectory $AppDir -Source $AppDir -Prepared (Join-Path $script:RecoveryRoot 'verification')
        Remove-FmscCompletedSnapshot
        Release-FmscInstallerLock
        Start-FmscAfterInstallation -Application (Join-Path $AppDir 'FMSC_SMS.exe')
        return
    }
    if ($PreparedRoot) { Test-PreparedUpdate -Root $PreparedRoot -Version $AppVersion -Schema ([int]$DatabaseVersion) }
    Show-Banner
    Write-Section 'Installing - preparing and activating the application update'
    Write-Log '================================================'
    Write-Log 'FMSC SMS installation started.'
    Write-Log "Application version: $AppVersion"
    Write-Log "Required database version: $DatabaseVersion"
    Write-Log "Release root: $ReleaseRoot"
    Write-Log "Application directory: $AppDir"
    Write-Log "Database directory: $DatabaseDir"
    Write-Log "Migrations directory: $MigrationsDir"
    Write-Log '================================================'
    Pause-Step
    Write-Section `
        'STEP 1 OF 8  -  INSTALLATION PREPARATION'
    Write-Step `
        'Checking administrator privileges...'
    if (-not (Test-IsAdministrator)) {
        Write-Failure `
            'Administrator privileges are required.'
        Write-Host ''
        Write-Host `
            '  Please right-click Install.bat and select:' `
            -ForegroundColor $ColorWarning
        Write-Host `
            '  Run as administrator' `
            -ForegroundColor $ColorWarning
        Write-Host ''
        throw 'Administrator privileges are required. No update was installed.'
    }
    Write-Success `
        'Administrator privileges confirmed.'
    Write-Step `
        'Checking release files...'
    if (-not (Test-Path -LiteralPath $AppSource)) {
        throw `
            "Published application folder was not found: $AppSource"
    }
    $SourceExe =
        Join-Path $AppSource 'FMSC_SMS.exe'
    if (-not (Test-Path -LiteralPath $SourceExe)) {
        throw `
            "FMSC_SMS.exe was not found: $SourceExe"
    }
    Write-Success `
        'Release files verified.'
    if (-not (Test-Path -LiteralPath $DatabaseDir)) {
        New-Item `
            -ItemType Directory `
            -Path $DatabaseDir `
            -Force |
            Out-Null
    }
    Pause-Long
    Write-Section `
        'STEP 2 OF 8  -  APPLICATION VERSION CHECK'
    $null = Convert-ToVersion $AppVersion
    $InstalledAppVersion =
        Get-InstalledApplicationVersion
    if ($InstalledAppVersion) {
        Write-Step `
            "Installed application version : $InstalledAppVersion"
        Write-Step `
            "Release application version   : $AppVersion"
        $VersionComparison = Compare-ApplicationVersion $InstalledAppVersion $AppVersion
        if ($VersionComparison -gt 0) {
            throw (
                "The installed application is newer than this release.`r`n`r`n" +
                "Installed application: $InstalledAppVersion`r`n" +
                "This release: $AppVersion`r`n`r`n" +
                "Installation stopped to prevent an application downgrade."
            )
        }
        if ($VersionComparison -eq 0) {
            Write-Success `
                'Same application version detected.'
            Write-Host `
                '  Existing application files may be refreshed.' `
                -ForegroundColor $ColorDim
        }
        else {
            Write-Success `
                'A newer application release is available.'
        }
    }
    else {
        Write-Success `
            'No existing FMSC SMS application detected.'
        Write-Host `
            '  This will be treated as a new application installation.' `
            -ForegroundColor $ColorDim
    }
    Pause-Long
    Write-Section `
        'STEP 3 OF 8  -  APPLICATION SAFETY CHECK'
    Write-Step `
        'Checking whether FMSC SMS is currently running...'
    $RunningApp =
        Get-Process `
            -Name 'FMSC_SMS' `
            -ErrorAction SilentlyContinue
    if ($RunningApp) {
        throw (
            "FMSC SMS is currently running.`r`n`r`n" +
            "Please close the application and run Install.bat again.`r`n`r`n" +
            "The installer will not replace application files while FMSC SMS is running."
        )
    }
    Write-Success `
        'Application is not running.'
    if ($PreparedRoot -and -not $ResumeUpdate) {
        $script:RecoveryJournal = Initialize-FmscRecoveryTransaction -Prepared $PreparedRoot -Root $script:RecoveryRoot -Version $AppVersion -Schema ([int]$DatabaseVersion) -UserSid $OriginalUserSid
        $PreparedRoot = Join-Path $script:RecoveryRoot ('transactions/' + $script:RecoveryJournal.Transaction)
        $AppSource = Join-Path $PreparedRoot 'application'
        $MigrationsDir = Join-Path $AppSource 'UpdateSupport/Migrations'
        $ReleaseRoot = $script:RecoveryRoot
    }
    Pause-Step
    Write-Section `
        'STEP 4 OF 8  -  DATABASE ENGINE AND TOOLS'
    Write-Step `
        'Checking SQL Server LocalDB...'
    $SqlLocalDb =
        Get-Command `
            'sqllocaldb.exe' `
            -ErrorAction SilentlyContinue
    # Do not reinstall an existing LocalDB just because its directory is absent from PATH.
    if (-not $SqlLocalDb -and $env:ProgramFiles) {
        $localExe = Join-Path $env:ProgramFiles 'Microsoft SQL Server\160\Tools\Binn\SqlLocalDB.exe'
        if (Test-Path -LiteralPath $localExe -PathType Leaf) {
            $env:Path += ';' + (Split-Path $localExe -Parent)
            $SqlLocalDb = Get-Command 'sqllocaldb.exe' -ErrorAction SilentlyContinue
        }
    }
    Write-Step 'Retaining verified maintenance tools on this PC...'
    foreach($ToolName in @('Git','SqlCmd','LocalDb')){
        $null=Install-FmscMaintenanceTool $ToolName $AppSource
    }
    if (-not $SqlLocalDb) {
        Write-WarningMessage `
            'SQL Server LocalDB was not found.'
        # The self-contained win-x64 application includes its .NET Desktop Runtime. Tools carries only database prerequisites: sqlcmd.exe and SqlLocalDB.msi.
        $MsiRoot=Install-FmscMaintenanceTool 'LocalDb' $AppSource
        $MsiPath=Join-Path $MsiRoot 'SqlLocalDB.msi'
        Invoke-LocalDbMsiInstallation -MsiPath $MsiPath -LogDirectory $DatabaseDir
        $MachinePath =
            [Environment]::GetEnvironmentVariable(
                'Path',
                'Machine'
            )
        $UserPath =
            [Environment]::GetEnvironmentVariable(
                'Path',
                'User'
            )
        $env:Path =
            "$MachinePath;$UserPath"
        # The MSI can succeed before its PATH updates reach this process.
        if (-not (Get-Command 'sqllocaldb.exe' -ErrorAction SilentlyContinue) -and $env:ProgramFiles) {
            $localExe = Join-Path $env:ProgramFiles 'Microsoft SQL Server\160\Tools\Binn\SqlLocalDB.exe'
            if (Test-Path -LiteralPath $localExe -PathType Leaf) {
                $env:Path += ';' + (Split-Path $localExe -Parent)
            }
        }
        if (-not (Get-Command 'sqllocaldb.exe' -ErrorAction SilentlyContinue)) {
            throw 'LocalDB setup completed but sqllocaldb.exe was not found. Restart Windows and rerun the installer. Check the LocalDB diagnostic log if necessary.'
        }
    }
    else {
        Write-Success `
            'SQL Server LocalDB is already installed.'
    }
    Write-Step `
        'Checking sqlcmd...'
    # SQL always runs from verified, protected machine storage.
    $script:SqlCmdPath =
        Resolve-SqlCmd
    Write-Step `
        'Starting MSSQLLocalDB...'
    $LocalDbOutput =
        & sqllocaldb.exe start MSSQLLocalDB 2>&1
    if ($LocalDbOutput) {
        foreach ($Line in $LocalDbOutput) {
            Write-Log $Line.ToString()
        }
    }
    if ($LASTEXITCODE -ne 0) {
        throw `
            'Unable to start the MSSQLLocalDB instance.'
    }
    Write-Success `
        'MSSQLLocalDB is running.'
    # Use the engine's current pipe rather than a stale LocalDB name resolution.
    $EndpointInfo = & sqllocaldb.exe info MSSQLLocalDB 2>&1
    $EndpointPattern = '(?:np:)?(' + [regex]::Escape('\\.\pipe\LOCALDB#') + '[A-Za-z0-9]+' + [regex]::Escape('\tsql\query') + ')'
    $EndpointMatch = [regex]::Match(($EndpointInfo -join "`n"), $EndpointPattern)
    if ($EndpointMatch.Success) { $script:SqlInstance = 'np:' + $EndpointMatch.Groups[1].Value }

    Pause-Long
    Write-Section `
        'STEP 5 OF 8  -  DATABASE PREPARATION'
    if (Test-Path -LiteralPath $LiveMdf) {
        Write-Step `
            'Existing database MDF found.'
        Write-Success `
            'Existing database WILL NOT be replaced.'
        Write-Host `
            '  Existing user data WILL be preserved.' `
            -ForegroundColor $ColorInfo
        if (-not (Test-Path -LiteralPath $LiveLdf)) {
            throw (
                "FMSC_DB.mdf exists but FMSC_DB.ldf is missing.`r`n`r`n" +
                "MDF: $LiveMdf`r`n" +
                "LDF: $LiveLdf`r`n`r`n" +
                "Installation stopped to protect the existing database."
            )
        }
    }
    else {
        Write-Step `
            'No existing database found.'
        Write-Step `
            'Installing the initial database template...'
        if (-not (Test-Path -LiteralPath $DatabaseSource)) {
            throw (
                "Database folder was not found: $DatabaseSource`r`n`r`n" +
                "This is a fresh install (no existing FMSC_DB was found at $LiveMdf), " +
                "which needs the database template. If this machine should already " +
                "have FMSC_DB installed, check that path before re-running - this " +
                "release does not include a Database folder, which is fine for an " +
                "app-only update but not for a first-time install."
            )
        }
        if (-not (Test-Path -LiteralPath $TemplateMdf)) {
            throw `
                "Database template MDF was not found: $TemplateMdf"
        }
        if (-not (Test-Path -LiteralPath $TemplateLdf)) {
            throw `
                "Database template LDF was not found: $TemplateLdf"
        }
        Copy-Item `
            -LiteralPath $TemplateMdf `
            -Destination $LiveMdf `
            -Force
        Copy-Item `
            -LiteralPath $TemplateLdf `
            -Destination $LiveLdf `
            -Force
        Write-Success `
            'Initial database files copied successfully.'
    }
    Write-Step `
        'Applying database folder permissions.'
    $CurrentUser =
        [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    & icacls.exe `
        $DatabaseDir `
        /inheritance:r |
        Out-Null
    & icacls.exe `
        $DatabaseDir `
        /grant:r `
        'SYSTEM:(OI)(CI)(F)' `
        'Administrators:(OI)(CI)(F)' `
        "${CurrentUser}:(OI)(CI)(F)" |
        Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw `
            'Failed to configure database folder permissions.'
    }
    Write-Success `
        'Database folder permissions configured.'
    Pause-Long
    Write-Step `
        'Checking FMSC_DB registration...'
    $DatabaseQuery =
        "SET NOCOUNT ON; " +
        "SELECT DB_NAME(database_id) AS DatabaseName, " +
        "type_desc, physical_name " +
        "FROM sys.master_files " +
        "WHERE database_id = DB_ID(N'$DatabaseName');"
    $DatabaseInfo =
        Invoke-SqlMaster `
            -Query $DatabaseQuery
    $DatabaseRegistered = $false
    $MdfRegisteredCorrectly = $false
    $LdfRegisteredCorrectly = $false
    foreach ($Line in $DatabaseInfo) {
        $Text =
            $Line.ToString().Trim()
        if ($Text -match 'FMSC_DB') {
            $DatabaseRegistered = $true
        }
        if ($Text -match [regex]::Escape($LiveMdf)) {
            $MdfRegisteredCorrectly = $true
        }
        if ($Text -match [regex]::Escape($LiveLdf)) {
            $LdfRegisteredCorrectly = $true
        }
    }
    if (-not $DatabaseRegistered) {
        Write-Step `
            'FMSC_DB is not registered.'
        Write-Step `
            'Registering FMSC_DB...'
        $AttachQuery =
            "CREATE DATABASE [$DatabaseName] " +
            "ON " +
            "(FILENAME = N'$LiveMdf'), " +
            "(FILENAME = N'$LiveLdf') " +
            "FOR ATTACH;"
        Invoke-SqlMaster `
            -Query $AttachQuery
        Write-Success `
            'FMSC_DB registered successfully.'
    }
    else {
        Write-Success `
            'FMSC_DB is already registered.'
        if (
            (-not $MdfRegisteredCorrectly) -or
            (-not $LdfRegisteredCorrectly)
        ) {
            throw (
                "FMSC_DB is registered, but it does not point to the " +
                "authoritative database files.`r`n`r`n" +
                "Expected MDF: $LiveMdf`r`n" +
                "Expected LDF: $LiveLdf"
            )
        }
        Write-Success `
            'FMSC_DB registration is correct.'
    }
    Write-Step `
        'Verifying database file locations...'
    $VerifyQuery =
        "SET NOCOUNT ON; " +
        "SELECT DB_NAME(database_id) AS DatabaseName, " +
        "type_desc, physical_name " +
        "FROM sys.master_files " +
        "WHERE database_id = DB_ID(N'$DatabaseName');"
    $VerifyResult =
        Invoke-SqlMaster `
            -Query $VerifyQuery
    $VerifyText =
        $VerifyResult -join "`r`n"
    Write-Log $VerifyText
    if ($VerifyText -notmatch [regex]::Escape($LiveMdf)) {
        throw `
            'FMSC_DB verification failed. MDF path is incorrect.'
    }
    if ($VerifyText -notmatch [regex]::Escape($LiveLdf)) {
        throw `
            'FMSC_DB verification failed. LDF path is incorrect.'
    }
    Write-Success `
        'Database file locations verified.'
    Write-Step `
        'Checking database status...'
    $StateQuery =
        "SET NOCOUNT ON; " +
        "SELECT state_desc " +
        "FROM sys.databases " +
        "WHERE name = N'$DatabaseName';"
    $StateResult =
        Invoke-SqlMaster `
            -Query $StateQuery
    $StateText =
        $StateResult -join "`r`n"
    if ($StateText -notmatch 'ONLINE') {
        throw `
            "FMSC_DB is not ONLINE. Current state: $StateText"
    }
    Write-Success `
        'FMSC_DB is ONLINE.'
    Pause-Long
    Write-Section `
        'STEP 6 OF 8  -  APPLICATION STAGING'
    # Stage and verify application files before pending migrations; activate only after full database verification.
    Write-Host `
        '  The new application is copied and verified first.' `
        -ForegroundColor $ColorInfo
    Write-Host `
        '  It is activated only after pending database updates' `
        -ForegroundColor $ColorInfo
    Write-Host `
        '  and database compatibility checks have succeeded.' `
        -ForegroundColor $ColorInfo
    Write-Host ''
    $InstalledExe =
        Join-Path `
            $AppDir `
            'FMSC_SMS.exe'
    Write-Step `
        'Staging the new application...'
    if (Test-Path -LiteralPath $AppStagingDir) {
        Write-Log `
            "Removing the staging folder left by a previous run: $AppStagingDir"
        Remove-Item `
            -LiteralPath $AppStagingDir `
            -Recurse `
            -Force
    }
    Copy-ApplicationFiles `
        -Source $AppSource `
        -Destination $AppStagingDir
    Write-Step `
        'Verifying the staged application...'
    $StagedApplication =
        Test-StagedApplication `
            -Source $AppSource `
            -Staging $AppStagingDir
    $StagedSizeMb =
        [math]::Round(
            $StagedApplication.Bytes / 1MB,
            2
        )
    Write-Success `
        "Application staged and verified ($($StagedApplication.Files) files, $StagedSizeMb MB)."
    Write-Log `
        "Application staged: $AppStagingDir ($($StagedApplication.Files) files, $StagedSizeMb MB)"
    Pause-Long
    Write-Section `
        'STEP 7 OF 8  -  APPLICATION ACTIVATION AND DATABASE UPDATE'
    if ($PreparedRoot) {
        Test-PreparedUpdate -Root $PreparedRoot -Version $AppVersion -Schema ([int]$DatabaseVersion) -Application $AppStagingDir
    }
    if ($script:RecoveryJournal) { Assert-FmscRecoveryDatabaseIdentity }
    Invoke-PendingMigrations -Directory $MigrationsDir -RequiredVersion (Convert-ToDbVersion $DatabaseVersion)
    $verification = Invoke-InstallationDatabaseCheck -Application (Join-Path $AppStagingDir 'FMSC_SMS.exe')
    if ($verification -notin @(0,10)) {
        throw "Database compatibility verification failed (code $verification). The staged application was not activated."
    }
    $RequiredVersionObject = Convert-ToDbVersion $DatabaseVersion
    # Reject databases lacking reviewed baseline features; do not require procedures absent from the approved schema.
    $NeedsLatestUpdates = Invoke-SqlMaster -Query "SET NOCOUNT ON; SELECT CASE WHEN COL_LENGTH('FMSC_DB.dbo.Students','AdmissionNumber') IS NULL OR OBJECT_ID('FMSC_DB.dbo.SaveStudentDetailsWithFirstEnrollment','P') IS NULL OR OBJECT_ID('FMSC_DB.dbo.AdmitHistoricalStudent','P') IS NULL THEN 1 ELSE 0 END;"
    if (($NeedsLatestUpdates -join "`n") -match '(?m)^\s*1\s*$') {
        throw 'This database needs school-data updates that are not included in this application copy. Restore a backup made after the latest updates, or contact support to upgrade the older backup.'
    }
    Write-Step `
        'Performing final database compatibility check...'
    $FinalDatabaseVersion =
        Get-DatabaseVersion
    $FinalVersionObject =
        Convert-ToDbVersion $FinalDatabaseVersion
    if ($FinalVersionObject -lt $RequiredVersionObject) {
        throw (
            "Database is still below the required version.`r`n`r`n" +
            "Current: $FinalDatabaseVersion`r`n" +
            "Required: $DatabaseVersion"
        )
    }
    if ($FinalVersionObject -gt $RequiredVersionObject) {
        throw (
            "Database is newer than the version supported by this release.`r`n`r`n" +
            "Current: $FinalDatabaseVersion`r`n" +
            "Required: $DatabaseVersion"
        )
    }
    # Even an app-only update must journal its original database version.
    if ($script:RecoveryJournal -and $script:RecoveryJournal.BeforeSchema -eq 0) {
        $script:RecoveryJournal.BeforeSchema = [int]$FinalDatabaseVersion
        Save-FmscRecoveryJournal $script:RecoveryRoot $script:RecoveryJournal
    }
    Write-Step 'Activating the staged application...'
    if ($script:RecoveryJournal -and (Get-Process -Name 'FMSC_SMS' -ErrorAction SilentlyContinue)) {
        throw 'FMSC was reopened during installation. Close it before recovering application activation.'
    }
    Enable-StagedApplication -Staging $AppStagingDir -AppDir $AppDir -RollbackDir $AppRollbackDir
    Write-Success `
        "Final database schema version: $FinalDatabaseVersion"
    Write-Host ''
    Write-Host `
        '  [OK] DATABASE IS COMPATIBLE WITH THIS APPLICATION' `
        -ForegroundColor $ColorSuccess
    Pause-Long
    Write-Host `
        '  Database compatibility confirmed.' `
        -ForegroundColor $ColorSuccess
    Write-Host `
        '  The application has been activated.' `
        -ForegroundColor $ColorInfo
    Write-Host ''
    Pause-Long
    Write-Step `
        'Creating desktop shortcut...'
    $DesktopPath =
        [Environment]::GetFolderPath('Desktop')
    $ShortcutPath =
        Join-Path `
            $DesktopPath `
            'FMSC SMS.lnk'
    try {
        Install-FmscStableLauncher -Source $AppSource -Root $script:RecoveryRoot -ShortcutPath $ShortcutPath -Application $InstalledExe
        Write-Success `
            "Desktop shortcut created: $ShortcutPath"
    }
    catch {
        Write-WarningMessage `
            "Could not create desktop shortcut: $($_.Exception.Message)"
    }
    Pause-Long
    Complete-VerifiedApplicationUpdate -ApplicationDirectory $AppDir -Source $AppSource -Prepared $PreparedRoot
    if ($script:RecoveryJournal) { Remove-FmscCompletedSnapshot }
    Write-Section `
        'STEP 8 OF 8  -  COMPLETED'
    Write-Host ''
    Write-Host `
        '  ============================================================' `
        -ForegroundColor $ColorSuccess
    Write-Host `
        '                FMSC SMS UPDATE COMPLETED' `
        -ForegroundColor $ColorSuccess
    Write-Host `
        '  ============================================================' `
        -ForegroundColor $ColorSuccess
    Write-Host ''
    Write-Host `
        "  Application version : $AppVersion" `
        -ForegroundColor $ColorInfo
    Write-Host `
        "  Database version    : $FinalDatabaseVersion" `
        -ForegroundColor $ColorInfo
    Write-Host `
        "  Application folder  : $AppDir" `
        -ForegroundColor $ColorInfo
    Write-Host `
        "  Database folder     : $DatabaseDir" `
        -ForegroundColor $ColorInfo
    Write-Host `
        "  SQL Server          : $SqlInstance" `
        -ForegroundColor $ColorInfo
    Write-Host `
        "  Database            : $DatabaseName" `
        -ForegroundColor $ColorInfo
    Write-Host `
        "  Desktop shortcut    : $ShortcutPath" `
        -ForegroundColor $ColorInfo
    # The recovery point is reported on success too, not only in the failure message. This is the file to restore if the new version has to be taken back off.
    if ($RecoveryBackupPath) {
        Write-Host `
            '  Pre-migration backup:' `
            -ForegroundColor $ColorInfo
        Write-Host `
            "    $RecoveryBackupPath" `
            -ForegroundColor $ColorDim
    }
    Write-Host ''
    Write-Log `
        '================================================'
    Write-Log `
        'FMSC SMS installation completed successfully.'
    Write-Log `
        '================================================'
    Pause-Long
    Write-Host ''
    Write-Host `
        '  ------------------------------------------------------------' `
        -ForegroundColor $ColorDim
    Release-FmscInstallerLock
    Start-FmscAfterInstallation -Application $InstalledExe
    Write-Host ''
    Write-Host `
        '  Thank you for using FMSC School Management System.' `
        -ForegroundColor $ColorTitle
    Write-Host ''
}
catch {
    $recoveryOutcome = ''
    try {
        Write-Log `
            "ERROR: $($_.Exception.Message)"
    }
    catch {
    }
    if ($script:RecoveryJournal -and $script:RecoveryJournal.Phase -in @('Prepared','Migrating','Activating')) {
        try {
            Invoke-FmscRollbackFailedUpdate
            $recoveryOutcome = 'Rollback verified: previous application and original database schema restored.'
        }
        catch {
            $recoveryOutcome = "Automatic rollback stopped for safety: $($_.Exception.Message). Do not retry or open FMSC until recovery is verified; backups and old application files were retained."
        }
        try { Write-Log $recoveryOutcome } catch { }
    } elseif ($script:RecoveryJournal -and $script:RecoveryJournal.Phase -eq 'Verified') {
        $recoveryOutcome = 'New application and database were already verified; only post-commit completion remains. Use the FMSC recovery shortcut.'
    }
    Write-Host ''
    Write-Host `
        '  ============================================================' `
        -ForegroundColor $ColorError
    Write-Host `
        '                    INSTALLATION FAILED' `
        -ForegroundColor $ColorError
    Write-Host `
        '  ============================================================' `
        -ForegroundColor $ColorError
    Write-Host ''
    Write-Host `
        "  $($_.Exception.Message)" `
        -ForegroundColor $ColorError
    Write-Host ''
    if ($recoveryOutcome) {
        Write-Host "  $recoveryOutcome" -ForegroundColor $ColorWarning
        Write-Host ''
    }
    # A failure is exactly when these two locations matter.
    if (Test-Path -LiteralPath $AppRollbackDir) {
        Write-Host `
            '  Previous application kept at:' `
            -ForegroundColor $ColorWarning
        Write-Host `
            "  $AppRollbackDir" `
            -ForegroundColor $ColorInfo
        Write-Host ''
    }
    if ($RecoveryBackupPath) {
        Write-Host `
            '  Verified pre-migration database backup:' `
            -ForegroundColor $ColorWarning
        Write-Host `
            "  $RecoveryBackupPath" `
            -ForegroundColor $ColorInfo
        Write-Host ''
    }
    Write-Host `
        '  Installation log:' `
        -ForegroundColor $ColorWarning
    Write-Host `
        "  $LogFile" `
        -ForegroundColor $ColorInfo
    Write-Host ''
    if ($script:UpdateWindow -and -not $script:UpdateWindow.IsDisposed) {
        $script:UpdateWindow.Text = 'FMSC - Installation stopped'
        $script:UpdatePhase.Text = 'INSTALLATION NEEDS ATTENTION'
        $script:UpdatePhase.ForeColor = [Drawing.Color]::FromArgb(171,57,57)
        $script:UpdateStatus.Text = 'Installation stopped' + [Environment]::NewLine +
            'Review the error below. Detailed diagnostics are saved to the installer log.'
        $script:UpdateBar.MarqueeAnimationSpeed = 0
        $script:UpdateBar.Style = 'Continuous'
        $script:UpdateBar.Value = 0
        [Windows.Forms.Application]::DoEvents()
    }
    $previousApplicationLocation = if (Test-Path -LiteralPath (Join-Path $AppDir 'FMSC_SMS.exe') -PathType Leaf) {
        $AppDir
    } elseif (Test-Path -LiteralPath (Join-Path $AppRollbackDir 'FMSC_SMS.exe') -PathType Leaf) {
        $AppRollbackDir
    } else { 'Not verified - recovery required' }
    if ($PreparedRoot) {
        [Windows.Forms.MessageBox]::Show(
            "Update could not be completed. FMSC has not been restarted." + [Environment]::NewLine +
            [Environment]::NewLine + $_.Exception.Message + [Environment]::NewLine +
            [Environment]::NewLine + $recoveryOutcome + [Environment]::NewLine +
            [Environment]::NewLine + "Log: $LogFile" + [Environment]::NewLine +
            "Previous application location: $previousApplicationLocation" + [Environment]::NewLine +
            "Database backup: $RecoveryBackupPath",
            'FMSC - Update Needs Attention', 'OK', 'Error') | Out-Null
    } elseif ($ShowInstallerWindow) {
        [Windows.Forms.MessageBox]::Show($script:UpdateWindow,
            'Installation could not be completed.' + [Environment]::NewLine +
            [Environment]::NewLine + $_.Exception.Message + [Environment]::NewLine +
            [Environment]::NewLine + $recoveryOutcome + [Environment]::NewLine +
            [Environment]::NewLine + "Installer log: $LogFile" + [Environment]::NewLine +
            "Previous application location: $previousApplicationLocation" + [Environment]::NewLine +
            "Database backup: $RecoveryBackupPath",
            'FMSC - Setup Needs Attention', 'OK', 'Error') | Out-Null
    } else { Read-Host '  Press ENTER to close' }
    exit 1
}
finally {
    if ($script:UpdateWindow) { $script:UpdateWindow.Dispose() }
    if ($script:InstallerLockHeld) { $script:InstallerMutex.ReleaseMutex() }
    if ($script:InstallerMutex) { $script:InstallerMutex.Dispose() }
}
