# Extract helpers only. No elevation, application activation, real restart or SQL.
$ErrorActionPreference = 'Stop'
$source = Get-Content (Join-Path $PSScriptRoot '../Installer/core.ps1') -Raw
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput($source,[ref]$tokens,[ref]$errors)
if ($errors.Count) { throw 'Installer does not parse.' }
foreach ($name in @('Pause-Step','Pause-Long','Start-UpdatedFmsc','Start-FmscAfterInstallation','Open-UpdateWindow','Set-UpdateWindowStage','Set-UpdateWindowStatus','Wait-FmscSetupChoice')) {
    $definition = $ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name},$true)
    Invoke-Expression $definition.Extent.Text
}
function Read-Host { param($Prompt) throw 'Prepared update must not prompt.' }
function Start-Sleep { throw 'Prepared update must skip cosmetic pauses.' }
function Start-Process { throw 'Prepared update must not directly launch an elevated app.' }
function Write-Log { param($Message) }
$script:launches = 0
$desktop = [pscustomobject]@{}
$desktop | Add-Member ScriptMethod ShellExecute {
    param($Application,$Arguments,$Directory,$Verb,$Display)
    if ($Application -ne 'C:\fixture\FMSC_SMS.exe' -or $Arguments -ne '' -or
        $Directory -ne 'C:\fixture' -or $Verb -ne 'open' -or $Display -ne 1) { throw 'Invalid shell restart request.' }
    $script:launches++
}
function Get-FmscDesktopShell { return $desktop }
$AppVersion = '1.0.0'; $DatabaseVersion = '1'
# Disposable paths exercise the installer header without touching installed
# applications or school MDF/LDF files.
$setupFixture = Join-Path ([IO.Path]::GetTempPath()) ('fmsc-setup-display-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($setupFixture)
$AppDir = Join-Path $setupFixture 'application'
$LiveMdf = Join-Path $setupFixture 'FMSC_DB.mdf'
$AppSource = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
function Assert-SetupHeader([string]$ExpectedText) {
    $items = @($script:UpdateWindow.Controls | ForEach-Object { $_.Controls })
    $mode = @($items | Where-Object { $_ -is [Windows.Forms.Label] -and $_.Text -eq $ExpectedText })
    $pictures = @($items | Where-Object { $_ -is [Windows.Forms.PictureBox] })
    if ($mode.Count -ne 1 -or $pictures.Count -ne 1 -or
        -not $pictures[0].Image -or
        $pictures[0].SizeMode -ne [Windows.Forms.PictureBoxSizeMode]::Zoom) {
        throw "Installer header must show $ExpectedText and the existing circular FMSC logo."
    }
}
$script:TestDecision = $null
$script:launches = 0
# The production selector waits for a real button click. These isolated tests
# supply a choice without hanging; the actual button click events are verified.
function Wait-FmscSetupChoice {
    if (-not $script:UpdateYes.Enabled -or -not $script:UpdateNo.Enabled) {
        throw 'Finish choices activated before verified completion.'
    }
    return $script:TestDecision -eq 'Yes'
}
$PreparedRoot = 'fixture'; $ShowInstallerWindow = $false
Pause-Step; Pause-Long
try {
    Open-UpdateWindow
    Assert-SetupHeader 'SECURE APPLICATION UPDATE'
    if (-not $script:UpdateWindow.Visible -or $script:UpdateWindow.ControlBox -or
        $script:UpdateWindow.ClientSize.Height -lt 400 -or
        $script:UpdateWindow.BackColor.ToArgb() -ne [Drawing.Color]::FromArgb(246,248,251).ToArgb() -or
        $script:UpdateYes.Enabled -or $script:UpdateNo.Enabled -or
        $script:UpdateYes.Bottom -gt $script:UpdateYes.Parent.ClientSize.Height -or
        $script:UpdateNo.Bottom -gt $script:UpdateNo.Parent.ClientSize.Height) {
        throw 'OceanBlue updater window must be tall and disable launch decisions during installation.'
    }
    $script:UpdateYes.PerformClick()
    if ($script:UpdateDecision) { throw 'A disabled finish button must not start FMSC.' }
    Set-UpdateWindowStatus 'Verifying the installed application...'
    $script:TestDecision = 'Yes'
    Start-FmscAfterInstallation 'C:\fixture\FMSC_SMS.exe'
    if ($script:launches -ne 1 -or -not $script:UpdateYes.Enabled -or -not $script:UpdateNo.Enabled -or
        $script:UpdateBar.Style -ne 'Continuous' -or $script:UpdateBar.Value -ne 100) {
        throw 'A verified update must offer active finish actions and launch once only after Yes.'
    }
    $script:UpdateYes.PerformClick()
    if ($script:UpdateDecision -ne 'Yes') { throw 'Yes button did not record the launch choice.' }
    $script:UpdateNo.PerformClick()
    if ($script:UpdateDecision -ne 'No') { throw 'No button did not record the close choice.' }
} finally { if ($script:UpdateWindow) { $script:UpdateWindow.Dispose(); $script:UpdateWindow = $null } }

$PreparedRoot = ''; $ShowInstallerWindow = $true
Pause-Step; Pause-Long
try {
    Open-UpdateWindow
    Assert-SetupHeader 'FIRST-TIME INSTALLATION'
    Set-UpdateWindowStage 'STEP 4 OF 8  -  DATABASE ENGINE AND TOOLS'
    Set-UpdateWindowStatus 'Checking SQL Server LocalDB...'
    if (-not $script:UpdateWindow.Visible -or $script:UpdateWindow.ControlBox -or
        $script:UpdateWindow.Text -notlike '*First-time setup*' -or
        $script:UpdateStepBars.Count -ne 8 -or
        $script:UpdatePhase.Text -notmatch 'STEP 4 OF 8' -or
        $script:UpdateYes.Enabled -or $script:UpdateNo.Enabled) {
        throw 'Fresh installation must use the same gated eight-stage OceanBlue GUI.'
    }
    $script:TestDecision = 'No'
    Start-FmscAfterInstallation 'C:\fixture\FMSC_SMS.exe'
    if ($script:launches -ne 1 -or -not $script:UpdateYes.Enabled -or -not $script:UpdateNo.Enabled) {
        throw 'No must close the verified installer without launching FMSC.'
    }
} finally {
    if ($script:UpdateWindow) { $script:UpdateWindow.Dispose(); $script:UpdateWindow = $null }
}
# An ordinary full setup must not claim to be the first installation if an
# application or school database already exists. This is display-only:
# synthetic files are never opened as a database or installed.
try {
    [void][IO.Directory]::CreateDirectory($AppDir)
    Set-Content -LiteralPath (Join-Path $AppDir 'FMSC_SMS.exe') -Value 'synthetic executable'
    Open-UpdateWindow
    Assert-SetupHeader 'EXISTING FMSC INSTALLATION'
    if ($script:UpdateWindow.Text -notlike '*Existing installation*') {
        throw 'Existing application must be identified in installer window title.'
    }
    $script:UpdateWindow.Dispose(); $script:UpdateWindow = $null
    Remove-Item -LiteralPath (Join-Path $AppDir 'FMSC_SMS.exe') -Force
    Set-Content -LiteralPath $LiveMdf -Value 'synthetic MDF marker, no SQL'
    Open-UpdateWindow
    Assert-SetupHeader 'EXISTING FMSC INSTALLATION'
} finally {
    if ($script:UpdateWindow) { $script:UpdateWindow.Dispose(); $script:UpdateWindow = $null }
    Remove-Item -LiteralPath $setupFixture -Recurse -Force
}
$ShowInstallerWindow = $false
# The real selector must pump UI events; never auto-launch a prepared update.
$selector = $ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Wait-FmscSetupChoice'}, $true)
if (-not $selector.Extent.Text.Contains('[Windows.Forms.Application]::DoEvents()') -or
    -not $selector.Extent.Text.Contains('$script:UpdateDecision')) {
    throw 'The real finish choice must wait for the user click.'
}
$installerEntry = Get-Content (Join-Path $PSScriptRoot '../Installer/Install FMSC_SMS.bat') -Raw
if ($installerEntry -notmatch 'FMSC[.]Setup[.]exe' -or
    $installerEntry -match 'powershell[.]exe') {
    throw 'The compatibility batch file must delegate to the graphical EXE only.'
}
$launcherSource = Get-Content (Join-Path $PSScriptRoot '../Installer/FMSC.Setup.cs') -Raw
if (-not $launcherSource.Contains('-ShowInstallerWindow') -or
    -not $launcherSource.Contains('-PreparedRoot') -or
    -not $launcherSource.Contains('Verb = "runas"') -or
    -not $launcherSource.Contains('-STA -WindowStyle Hidden') -or
    -not $launcherSource.Contains('Path.GetFullPath') -or
    -not $launcherSource.Contains('FMSC update process identity is invalid.')) {
    throw 'Shared GUI setup executable must retain the guarded first-install/update modes.'
}
$finish = $source.IndexOf('    Start-FmscAfterInstallation -Application $InstalledExe')
$verified = $source.IndexOf('    Complete-VerifiedApplicationUpdate -ApplicationDirectory $AppDir -Source $AppSource')
$catch = $source.IndexOf("'                    INSTALLATION FAILED'")
if ($finish -le $verified -or $finish -ge $catch) { throw 'Restart must occur only on the verified success path.' }
$handoff = Get-Content (Join-Path $PSScriptRoot '../Core/Updates/UpdateInstallerHandoff.cs') -Raw
if (-not $handoff.Contains('FMSC.Setup.exe') -or -not $handoff.Contains('--update') -or
    -not $handoff.Contains('UseShellExecute = true, Verb = "runas"')) {
    throw 'Prepared updates must request UAC through the verified shared GUI executable.'
}
Write-Output 'PASS: FMSC circular logo and accurate fresh/existing/update headers; eight-stage install/update finish gating, user choice, shared native launcher and UAC retained.'
