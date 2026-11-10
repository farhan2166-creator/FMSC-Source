param(
    [switch]$Execute,
    [int]$StagingProcessId = 0
)
# Independent uninstaller. Never call installer/core.ps1, detach SQL, or remove LocalDB.
$ErrorActionPreference = 'Stop'
$AppDirectory = 'C:\FMSC School Management'
$DataDirectory = 'C:\ProgramData\FMSC_Database'
$UpdaterDirectory = 'C:\ProgramData\FMSC_Updater'
$StageBase = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'FMSC_UninstallStage'
$ProtectExtensions = @('.mdf','.ldf','.bak','.ndf','.bacpac','.dacpac','.fmscbackup')
$UninstallRegistryPath = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\FMSC-SchoolManagement'

function Test-FmscAdministrator {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
function Test-FmscNoLinks {
    param([Parameter(Mandatory=$true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    while ($item) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Refusing to follow a linked or redirected uninstall path: $($item.FullName)"
        }
        $item = $item.Parent
    }
    if (Test-Path -LiteralPath $Path -PathType Container) {
        foreach ($entry in Get-ChildItem -LiteralPath $Path -Recurse -Force -ErrorAction Stop) {
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Refusing linked content in FMSC files: $($entry.FullName)"
            }
        }
    }
}
function Get-FmscProtectedFiles {
    param([Parameter(Mandatory=$true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return @() }
    Test-FmscNoLinks $Path
    return @(Get-ChildItem -LiteralPath $Path -Recurse -File -Force -ErrorAction Stop |
        Where-Object { $ProtectExtensions -contains $_.Extension.ToLowerInvariant() })
}
function Save-FmscProtectedFiles {
    param([object[]]$Roots,[string]$PreservationDirectory)
    $inventory = New-Object Collections.Generic.List[object]
    foreach ($root in $Roots) {
        $index = 0
        foreach ($file in @(Get-FmscProtectedFiles $root.Path)) {
            $relative = $file.FullName.Substring($root.Path.TrimEnd('\').Length).TrimStart('\')
            $inventory.Add([pscustomobject]@{
                Source = $file.FullName
                Target = (Join-Path (Join-Path $PreservationDirectory $root.Label) $relative)
                Sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
                Length = $file.Length
            })
            $index++
        }
    }
    if ($inventory.Count -eq 0) { return @() }
    if (Test-Path -LiteralPath $PreservationDirectory) {
        throw 'Preservation folder already exists; refusing to overwrite an earlier recovery copy.'
    }
    New-Item -ItemType Directory -Path $PreservationDirectory -ErrorAction Stop | Out-Null
    foreach ($record in $inventory) {
        $parent = Split-Path -Path $record.Target -Parent
        New-Item -ItemType Directory -Path $parent -Force -ErrorAction Stop | Out-Null
        Copy-Item -LiteralPath $record.Source -Destination $record.Target -ErrorAction Stop
        if ((Get-Item -LiteralPath $record.Target).Length -ne $record.Length -or
            (Get-FileHash -LiteralPath $record.Target -Algorithm SHA256).Hash -cne $record.Sha256) {
            throw "A school database or backup could not be verified after copying: $($record.Source)"
        }
    }
    return @($inventory.ToArray())
}
function Assert-FmscProtectedCopies {
    param([object[]]$Roots,[object[]]$Records)
    # Check again immediately before removing any source tree. This rejects new
    # backup files, changed files and incomplete copies instead of losing them.
    $original = @{}
    foreach ($record in $Records) { $original[$record.Source] = $record }
    $seen = @{}
    foreach ($root in $Roots) {
        foreach ($file in @(Get-FmscProtectedFiles $root.Path)) {
            if (-not $original.ContainsKey($file.FullName)) {
                throw "A new protected school file appeared; uninstall stopped: $($file.FullName)"
            }
            $record = $original[$file.FullName]
            if ($file.Length -ne $record.Length -or
                (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -cne $record.Sha256 -or
                -not (Test-Path -LiteralPath $record.Target -PathType Leaf) -or
                (Get-Item -LiteralPath $record.Target).Length -ne $record.Length -or
                (Get-FileHash -LiteralPath $record.Target -Algorithm SHA256).Hash -cne $record.Sha256) {
                throw "School data changed or its preserved copy is invalid: $($file.FullName)"
            }
            $seen[$file.FullName] = $true
        }
    }
    if ($seen.Count -ne $Records.Count) {
        throw 'A protected school file disappeared during the uninstall check.'
    }
}
function Remove-FmscNonProtectedContents {
    param([Parameter(Mandatory=$true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return }
    Test-FmscNoLinks $Path
    # Delete only files that are NOT database, log or backup material.
    foreach ($file in @(Get-ChildItem -LiteralPath $Path -Recurse -Force -File -ErrorAction Stop)) {
        if ($ProtectExtensions -notcontains $file.Extension.ToLowerInvariant()) {
            Remove-Item -LiteralPath $file.FullName -Force -ErrorAction Stop
        }
    }
    foreach ($folder in @(Get-ChildItem -LiteralPath $Path -Recurse -Directory -Force -ErrorAction Stop |
        Sort-Object { $_.FullName.Length } -Descending)) {
        if (@(Get-ChildItem -LiteralPath $folder.FullName -Force -ErrorAction Stop).Count -eq 0) {
            Remove-Item -LiteralPath $folder.FullName -Force -ErrorAction Stop
        }
    }
}
function Get-FmscDeletionRoots {
    # Deliberate allowlist: never scan or delete arbitrary drives or Documents.
    $entries = New-Object Collections.Generic.List[object]
    foreach ($pair in @(
        @('Application',$AppDirectory),
        @('Staging',"$AppDirectory.staging"),
        @('Rollback',"$AppDirectory.rollback"),
        @('Updater',$UpdaterDirectory)
    )) {
        if (Test-Path -LiteralPath $pair[1] -PathType Container) {
            $entries.Add([pscustomobject]@{Label=$pair[0];Path=$pair[1]})
        }
    }
    foreach ($archived in @(Get-ChildItem -LiteralPath 'C:\' -Directory -Force -ErrorAction Stop |
        Where-Object { $_.Name -match '^FMSC School Management\.(?:staging|rollback)\.archived-\d{8}-\d{6}$' })) {
        $entries.Add([pscustomobject]@{Label=('Archive-'+$archived.Name.Replace(' ','_'));Path=$archived.FullName})
    }
    $profiles = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList'
    foreach ($item in @(Get-ChildItem -LiteralPath $profiles -ErrorAction Stop)) {
        if ($item.PSChildName -notmatch '^S-1-5-21-(?:\d+-){3}\d+$') { continue }
        $property = Get-ItemProperty -LiteralPath $item.PSPath -Name ProfileImagePath -ErrorAction Stop
        $profile = [Environment]::ExpandEnvironmentVariables([string]$property.ProfileImagePath)
        # Only local user profiles on the FMSC system drive. Never remove
        # roaming/network profile shares, drive redirections or unexpected roots.
        if ([string]::IsNullOrWhiteSpace($profile) -or $profile.StartsWith('\\')) { continue }
        $profile = [IO.Path]::GetFullPath($profile)
        if (-not $profile.StartsWith('C:\Users\',[StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $profile -PathType Container)) { continue }
        foreach ($relative in @('AppData\Local\FMSC','AppData\Local\FMSC_SMS',
            'AppData\Roaming\FMSC','AppData\Roaming\FMSC_SMS')) {
            $path = Join-Path $profile $relative
            if (Test-Path -LiteralPath $path -PathType Container) {
                $entries.Add([pscustomobject]@{
                    Label=('User-'+$item.PSChildName+'-'+$relative.Replace('\','-'))
                    Path=$path
                })
            }
        }
    }
    return @($entries.ToArray())
}
function Assert-FmscUninstallReady {
    if (-not (Test-FmscAdministrator)) { throw 'Windows administrator permission is required.' }
    foreach ($process in @(Get-Process -Name 'FMSC_SMS','FMSC.Setup' -ErrorAction SilentlyContinue)) {
        throw "Close FMSC and its installer before uninstalling ($($process.ProcessName), PID $($process.Id))."
    }
    $journal = Join-Path $UpdaterDirectory 'recovery.json'
    if (Test-Path -LiteralPath $journal -PathType Leaf) {
        Test-FmscNoLinks $journal
        $record = Get-Content -LiteralPath $journal -Raw -ErrorAction Stop | ConvertFrom-Json
        if ([string]$record.Phase -notin @('Completed','Aborted')) {
            throw 'An unfinished FMSC update/recovery exists. Open FMSC to resolve it before uninstalling.'
        }
    }
}
function New-FmscStage {
    if (Test-Path -LiteralPath $StageBase) {
        Test-FmscNoLinks $StageBase
        $owner = (Get-Acl -LiteralPath $StageBase).GetOwner([Security.Principal.SecurityIdentifier]).Value
        if ($owner -notin @('S-1-5-18','S-1-5-32-544')) {
            throw 'Uninstall staging is not administrator-owned. Cleanup stopped.'
        }
        $acl = Get-Acl -LiteralPath $StageBase
        foreach ($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
            if ($rule.AccessControlType -eq 'Allow' -and
                $rule.IdentityReference.Value -notin @('S-1-5-18','S-1-5-32-544') -and
                ([int]$rule.FileSystemRights -band 0xD0156)) {
                throw 'Uninstall staging is writable by a non-administrator. Cleanup stopped.'
            }
        }
    } else {
        New-Item -ItemType Directory -Path $StageBase -ErrorAction Stop | Out-Null
        $acl = New-Object Security.AccessControl.DirectorySecurity
        $acl.SetAccessRuleProtection($true,$false)
        $acl.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))
        foreach ($sid in @('S-1-5-18','S-1-5-32-544')) {
            $rule = New-Object Security.AccessControl.FileSystemAccessRule(
                (New-Object Security.Principal.SecurityIdentifier($sid)),
                'FullControl','ContainerInherit,ObjectInherit','None','Allow')
            $acl.AddAccessRule($rule)
        }
        Set-Acl -LiteralPath $StageBase -AclObject $acl
    }
    $destination = Join-Path $StageBase ([guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $destination -ErrorAction Stop | Out-Null
    $source = $PSCommandPath
    $target = Join-Path $destination 'Uninstall-FMSC.ps1'
    Copy-Item -LiteralPath $source -Destination $target -ErrorAction Stop
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -cne
        (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash) {
        throw 'Uninstall engine staging integrity check failed.'
    }
    return $target
}
function Write-FmscUninstallStatus {
    param([string]$Message)
    if ($script:StatusText) { $script:StatusText.Text = $Message; [Windows.Forms.Application]::DoEvents() }
}
function Invoke-FmscUninstall {
    $mutex = New-Object Threading.Mutex($false,'Global\FMSCInstallerActivation')
    $held = $false
    try {
        try { $held = $mutex.WaitOne(0) }
        catch [Threading.AbandonedMutexException] { $held = $true }
        if (-not $held) { throw 'FMSC installation or update is running. Uninstall refused.' }
        Assert-FmscUninstallReady
        $roots = @(Get-FmscDeletionRoots)
        foreach ($root in $roots) { Test-FmscNoLinks $root.Path }
        Test-FmscNoLinks $DataDirectory
        Write-FmscUninstallStatus 'Checking protected school databases and backups...'
        $preserved = Join-Path $DataDirectory ('UninstallPreserved\' + (Get-Date -Format 'yyyyMMdd-HHmmss') +
            '-' + [guid]::NewGuid().ToString('N'))
        $protected = @(Save-FmscProtectedFiles -Roots $roots -PreservationDirectory $preserved)
        Assert-FmscProtectedCopies -Roots $roots -Records $protected
        Write-FmscUninstallStatus 'Removing application, updater and temporary files...'
        foreach ($root in $roots) {
            Test-FmscNoLinks $root.Path
            # Recheck the protected files in the current root before each deletion.
            Assert-FmscProtectedCopies -Roots @($root) -Records @($protected |
                Where-Object { $_.Source.StartsWith($root.Path.TrimEnd('\')+'\',
                    [StringComparison]::OrdinalIgnoreCase) })
            Remove-Item -LiteralPath $root.Path -Recurse -Force -ErrorAction Stop
        }
        Write-FmscUninstallStatus 'Removing FMSC logs and unneeded database-folder files...'
        Remove-FmscNonProtectedContents $DataDirectory
        Write-FmscUninstallStatus 'Removing FMSC Windows registration and shortcuts...'
        foreach ($desktop in @(
            [Environment]::GetFolderPath('Desktop'),
            [Environment]::GetFolderPath('CommonDesktopDirectory')
        )) {
            if (-not [string]::IsNullOrWhiteSpace($desktop)) {
                $shortcut = Join-Path $desktop 'FMSC SMS.lnk'
                if (Test-Path -LiteralPath $shortcut -PathType Leaf) {
                    # Shortcut is an owned FMSC shortcut only when it targets
                    # our own stable launcher. Never remove a re-pointed shortcut.
                    $shell = New-Object -ComObject WScript.Shell
                    if ($shell.CreateShortcut($shortcut).TargetPath -ieq
                        (Join-Path $UpdaterDirectory 'FMSC.Launcher.exe')) {
                        Remove-Item -LiteralPath $shortcut -Force -ErrorAction Stop
                    }
                }
            }
        }
        if (Test-Path -LiteralPath $UninstallRegistryPath) {
            Remove-Item -LiteralPath $UninstallRegistryPath -Recurse -Force -ErrorAction Stop
        }
        Write-FmscUninstallStatus ('Uninstall complete. School MDF/LDF/BAK files preserved in ' + $DataDirectory)
    } finally {
        if ($held) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
}
function Show-FmscUninstallWindow {
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    [Windows.Forms.Application]::EnableVisualStyles()
    $form = New-Object Windows.Forms.Form
    $form.Text = 'FMSC School Management - Uninstaller'
    $form.ClientSize = [Drawing.Size]::new(680,350)
    $form.StartPosition = 'CenterScreen'
    $form.FormBorderStyle = 'FixedDialog'
    $form.MaximizeBox = $false
    $form.Font = New-Object Drawing.Font('Segoe UI',10)
    try {
        $iconFile = Join-Path $AppDirectory 'FMSC.Uninstall.exe'
        if (Test-Path -LiteralPath $iconFile -PathType Leaf) {
            $form.Icon = [Drawing.Icon]::ExtractAssociatedIcon($iconFile)
        }
    } catch { } # Branding must never affect uninstall safety.
    $form.BackColor = [Drawing.Color]::FromArgb(246,248,251)
    $heading = New-Object Windows.Forms.Label
    $heading.SetBounds(26,24,620,42)
    $heading.Text = 'Uninstall FMSC School Management'
    $heading.Font = New-Object Drawing.Font('Segoe UI',17,[Drawing.FontStyle]::Bold)
    $explanation = New-Object Windows.Forms.Label
    $explanation.SetBounds(28,85,620,102)
    $explanation.Text = ('This removes the FMSC application, updater, temporary files, local settings and shortcuts.' +
        [Environment]::NewLine + [Environment]::NewLine +
        'Your school MDF, LDF and BAK files are preserved. SQL Server LocalDB is not uninstalled.')
    $explanation.ForeColor = [Drawing.Color]::FromArgb(50,60,75)
    $script:StatusText = New-Object Windows.Forms.Label
    $script:StatusText.SetBounds(28,212,625,55)
    $script:StatusText.Text = 'Ready. No files will be deleted until you confirm.'
    $script:StatusText.ForeColor = [Drawing.Color]::FromArgb(37,99,235)
    $start = New-Object Windows.Forms.Button
    $start.SetBounds(427,291,122,37)
    $start.Text = 'Uninstall'
    $start.BackColor = [Drawing.Color]::FromArgb(186,45,45)
    $start.ForeColor = [Drawing.Color]::White
    $start.FlatStyle = 'Flat'
    $close = New-Object Windows.Forms.Button
    $close.SetBounds(560,291,94,37)
    $close.Text = 'Cancel'
    $close.Add_Click({ $form.Close() })
    $start.Add_Click({
        $answer = [Windows.Forms.MessageBox]::Show($form,
            'Remove FMSC application and settings from this computer?' +
            [Environment]::NewLine + [Environment]::NewLine +
            'The school database and backup files will remain on disk.',
            'Confirm FMSC uninstall','YesNo','Warning')
        if ($answer -ne [Windows.Forms.DialogResult]::Yes) { return }
        $start.Enabled = $false
        $close.Enabled = $false
        $form.ControlBox = $false
        try {
            Invoke-FmscUninstall
            $script:StatusText.ForeColor = [Drawing.Color]::FromArgb(20,132,70)
            $close.Text = 'Close'
            [Windows.Forms.MessageBox]::Show($form,
                'FMSC has been uninstalled. Database files and backups were retained.' +
                [Environment]::NewLine + $DataDirectory,
                'FMSC Uninstall Complete','OK','Information') | Out-Null
        } catch {
            $script:StatusText.ForeColor = [Drawing.Color]::FromArgb(171,57,57)
            $script:StatusText.Text = 'Uninstall stopped: ' + $_.Exception.Message
            [Windows.Forms.MessageBox]::Show($form,
                $_.Exception.Message + [Environment]::NewLine +
                'Any database and backup files remain protected. Resolve the error before retrying.',
                'FMSC Uninstall Stopped','OK','Error') | Out-Null
            $close.Text = 'Close'
        } finally {
            $close.Enabled = $true
            $form.ControlBox = $true
        }
    })
    $form.Controls.AddRange(@($heading,$explanation,$script:StatusText,$start,$close))
    [Windows.Forms.Application]::Run($form)
    $form.Dispose()
}
try {
if (-not (Test-FmscAdministrator)) { throw 'Run FMSC.Uninstall.exe with administrator approval.' }
if (-not $Execute) {
    $staged = New-FmscStage
    $powerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $arguments = '-NoProfile -STA -WindowStyle Hidden -ExecutionPolicy Bypass -File "' +
        $staged + '" -Execute -StagingProcessId ' + $PID
    Start-Process -FilePath $powerShell -ArgumentList $arguments -WindowStyle Hidden -ErrorAction Stop
    return
}
$expectedStage = [IO.Path]::GetFullPath($StageBase).TrimEnd('\') + '\'
if (-not $PSScriptRoot.StartsWith($expectedStage,[StringComparison]::OrdinalIgnoreCase) -or
    (Split-Path $PSScriptRoot -Leaf) -notmatch '^[a-f0-9]{32}$' -or
    (Split-Path $PSCommandPath -Leaf) -cne 'Uninstall-FMSC.ps1') {
    throw 'The FMSC uninstall engine is not running from its protected staging directory.'
}
Test-FmscNoLinks $PSScriptRoot
if ($StagingProcessId -gt 0) {
    Wait-Process -Id $StagingProcessId -Timeout 30 -ErrorAction SilentlyContinue
}
try {
    Show-FmscUninstallWindow
} finally {
    # The script has been parsed by PowerShell; the staging copy is disposable.
    try { Remove-Item -LiteralPath $PSScriptRoot -Recurse -Force -ErrorAction Stop } catch { }
}
} catch {
    try {
        Add-Type -AssemblyName System.Windows.Forms
        [Windows.Forms.MessageBox]::Show($_.Exception.Message,
            'FMSC Uninstall Stopped','OK','Error') | Out-Null
    } catch { }
    exit 1
}
