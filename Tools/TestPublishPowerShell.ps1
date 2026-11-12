# Exercise the full PortableRelease publish from PowerShell 7 in a disposable
# directory. Its prerequisite is a readability fixture, not a real MSI; this
# output is never uploaded/installed. No SQL connection or school data is used.
param(
    [string]$ProjectDirectory = (Split-Path $PSScriptRoot -Parent),
    # Optional, never used for real publication. Default stays production TFM.
    [string]$ProbeTargetFramework = ''
)
if ($ProbeTargetFramework -and $ProbeTargetFramework -cne 'net10.0-windows') {
    throw 'The optional compatibility probe only supports net10.0-windows.'
}
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if (-not $IsWindows -or $PSVersionTable.PSVersion.Major -lt 7) {
    throw 'Run this regression with PowerShell 7 on Windows.'
}
$ProjectDirectory = [IO.Path]::GetFullPath($ProjectDirectory)
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('FMSC publish check ' + [Guid]::NewGuid().ToString('N'))
$modulePathBefore = $env:PSModulePath
try {
    New-Item -ItemType Directory -Path $scratch -Force | Out-Null
    $database = Join-Path $scratch 'empty database'
    New-Item -ItemType Directory -Path $database | Out-Null
    # Publish test uses synthetic Git/SSH files only; the real release pipeline
    # downloads and checksum-verifies the entire upstream MinGit distribution.
    $transport = Join-Path $scratch 'synthetic MinGit'
    foreach ($relative in @('cmd/git.exe','usr/bin/ssh.exe','COPYING-GIT.txt','THIRD_PARTY_NOTICES.txt')) {
        $target = Join-Path $transport $relative
        New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
        Set-Content -LiteralPath $target -Value 'Synthetic publish-only fixture; not executable.'
    }
    # These fixture files are never installed or uploaded; release workflows
    # separately verify the real Microsoft-signed MSI and pinned sqlcmd hash.
    $sqlCmd = Join-Path $scratch 'synthetic-sqlcmd.exe'
    Set-Content -LiteralPath $sqlCmd -Value 'Synthetic offline SQL tool; not executable.'
    $prerequisite = Join-Path $scratch 'readability fixture.txt'
    Set-Content -LiteralPath $prerequisite -Value 'Test fixture; not an installer.'
    $redist = Join-Path $scratch 'synthetic VC runtime.exe'
    Set-Content -LiteralPath $redist -Value 'Synthetic Visual C++ runtime; not executable.'

    foreach($pair in @(@('SqlCmd','sqlcmd.exe',$sqlCmd),@('LocalDb','SqlLocalDB.msi',$prerequisite),@('VcRedist','vc_redist.x64.exe',$redist))){
        $folder=Join-Path $scratch $pair[0]
        $null=New-Item -ItemType Directory -Path $folder
        Copy-Item -LiteralPath $pair[2] -Destination (Join-Path $folder $pair[1])
    }
    # Deliberately omit Windows PowerShell modules from the parent environment.
    # The real MSBuild Exec tasks must supply the correct path to both children.
    $env:PSModulePath = Join-Path $PSHOME 'Modules'
    $release = Join-Path $scratch 'release output'
    $application = Join-Path $release 'FMSC_SMS'
    # Reuse an old output directory deliberately. Retired SQL must disappear
    # from both installer and application support without deleting other files.
    foreach ($folder in @((Join-Path $release 'Migrations'), (Join-Path $application 'UpdateSupport/Migrations'))) {
        New-Item -ItemType Directory -Path $folder -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $folder '999999.sql') -Value '-- retired output fixture'
        Set-Content -LiteralPath (Join-Path $folder 'retained.txt') -Value 'preserve unrelated output'
    }
    # The opt-in compatibility probe tracks WFO1000 design-time metadata
    # separately. Never alter production's default compiler diagnostics.
    $tfmOverride = if ($ProbeTargetFramework) {
        @("-p:TargetFramework=$ProbeTargetFramework",'-p:NoWarn=WFO1000')
    } else { @() }
    $output = & dotnet publish (Join-Path $ProjectDirectory 'FMSC_SMS.csproj') `
        -c Release -r win-x64 --self-contained true -p:PublishProfile=PortableRelease `
        "-p:PublishDir=$application/" "-p:ReleaseRoot=$release/" `
        "-p:LocalDbMsiPath=$prerequisite" "-p:VcRedistPath=$redist" "-p:DeploymentDatabaseDirectory=$database" `
        "-p:BundledGitDirectory=$transport" "-p:BundledSqlCmdPath=$sqlCmd" `
        -p:FmscSyntheticPublishFixture=true -p:PublishSingleFile=false -p:PublishTrimmed=false @tfmOverride 2>&1
    $code = $LASTEXITCODE
    if ($code -ne 0) {
        throw "Full PortableRelease publish failed from PowerShell 7:`n$($output -join "`n")"
    }
    $text = $output -join "`n"
    if ($text -notmatch 'LocalDB prerequisite is present, nonempty and readable\.') {
        throw "Installer asset validation must execute:`n$text"
    }
    if ($env:PSModulePath -ne (Join-Path $PSHOME 'Modules')) {
        throw 'Validation changed the parent PowerShell module path.'
    }
    if ($ProbeTargetFramework) {
        $runtimeOptions = (Get-Content -LiteralPath (Join-Path $application 'FMSC_SMS.runtimeconfig.json') -Raw | ConvertFrom-Json).runtimeOptions
        $expectedRuntime = $ProbeTargetFramework.Replace('-windows','')
        if ($runtimeOptions.tfm -cne $expectedRuntime) {
            throw "Synthetic self-contained publish targets $($runtimeOptions.tfm), not $expectedRuntime."
        }
    }
    foreach ($relative in @('FMSC_SMS/FMSC_SMS.exe', 'FMSC_SMS/FMSC_SMS.dll',
        'FMSC_SMS/FMSC_SMS.deps.json', 'FMSC_SMS/FMSC_SMS.runtimeconfig.json',
        'FMSC_SMS/UpdateSupport/core.ps1', 'FMSC_SMS/UpdateSupport/UpdateRecovery.ps1',
        'FMSC_SMS/UpdateSupport/Launch-FMSC.ps1', 'FMSC_SMS/UpdateSupport/FMSC.Launcher.exe',
        'FMSC_SMS/UpdateSupport/FMSC.Setup.exe',
        'FMSC_SMS/FMSC.Uninstall.exe', 'FMSC_SMS/UpdateSupport/Uninstall-FMSC.ps1',
        'FMSC_SMS/UpdateTransport/cmd/git.exe',
        'FMSC_SMS/UpdateTransport/usr/bin/ssh.exe',
        'FMSC_SMS/UpdateTransport/COPYING-GIT.txt',
        'FMSC_SMS/UpdateTransport/THIRD_PARTY_NOTICES.txt',
        'FMSC_SMS/UpdateSupport/Tools/SqlCmd/sqlcmd.exe',
        'FMSC_SMS/UpdateSupport/Tools/LocalDb/SqlLocalDB.msi',
        'FMSC_SMS/UpdateSupport/Tools/VcRedist/vc_redist.x64.exe',
        'FMSC_SMS/UpdateSupport/MaintenanceTools.ps1',
        'FMSC_SMS/UpdateSupport/ToolCatalog.json',
        'MaintenanceTools.ps1', 'ToolCatalog.json',
        'UpdateRecovery.ps1', 'core.ps1', 'Install FMSC_SMS.bat', 'FMSC.Setup.exe',
        'FMSC.Uninstall.exe', 'Uninstall-FMSC.ps1', 'Tools/SqlLocalDB.msi','Tools/vc_redist.x64.exe')) {
        $file = Join-Path $release $relative
        if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -eq 0) {
            throw "Full publish did not copy its required output: $relative"
        }
    }
    foreach ($pair in @(
        ,@('Installer/core.ps1', 'core.ps1')
        ,@('Installer/core.ps1', 'FMSC_SMS/UpdateSupport/core.ps1')
        ,@('Installer/UpdateRecovery.ps1', 'UpdateRecovery.ps1')
        ,@('Installer/UpdateRecovery.ps1', 'FMSC_SMS/UpdateSupport/UpdateRecovery.ps1')
        ,@('Installer/MaintenanceTools.ps1', 'MaintenanceTools.ps1')
        ,@('Installer/MaintenanceTools.ps1', 'FMSC_SMS/UpdateSupport/MaintenanceTools.ps1')
        ,@('Installer/ToolCatalog.json', 'ToolCatalog.json')
        ,@('Installer/ToolCatalog.json', 'FMSC_SMS/UpdateSupport/ToolCatalog.json')
        ,@('Installer/Launch-FMSC.ps1', 'FMSC_SMS/UpdateSupport/Launch-FMSC.ps1')
        ,@('Installer/Uninstall-FMSC.ps1', 'Uninstall-FMSC.ps1')
        ,@('Installer/Uninstall-FMSC.ps1', 'FMSC_SMS/UpdateSupport/Uninstall-FMSC.ps1')
    )) {
        $expected = (Get-FileHash -LiteralPath (Join-Path $ProjectDirectory $pair[0]) -Algorithm SHA256).Hash
        $actual = (Get-FileHash -LiteralPath (Join-Path $release $pair[1]) -Algorithm SHA256).Hash
        if ($actual -ne $expected) { throw "Published installer asset differs from checked-out source: $($pair[1])" }
    }
    foreach ($pair in @(
        ,@($sqlCmd, (Join-Path $application 'UpdateSupport/Tools/SqlCmd/sqlcmd.exe'))
        ,@($prerequisite, (Join-Path $application 'UpdateSupport/Tools/LocalDb/SqlLocalDB.msi'))
        ,@($redist, (Join-Path $application 'UpdateSupport/Tools/VcRedist/vc_redist.x64.exe'))
    )) {
        if ((Get-FileHash -LiteralPath $pair[0] -Algorithm SHA256).Hash -ne
            (Get-FileHash -LiteralPath $pair[1] -Algorithm SHA256).Hash) {
            throw "Published offline SQL fixture differs from its input: $($pair[1])"
        }
    }
    & (Join-Path $ProjectDirectory 'Tools/TestOfflineSqlPrerequisites.ps1')
    & (Join-Path $ProjectDirectory 'Tools/TestStandaloneUninstall.ps1')
    & (Join-Path $ProjectDirectory 'Tools/TestWindowlessLauncher.ps1') -LauncherPath (Join-Path $application 'UpdateSupport/FMSC.Launcher.exe')
    # GUI setup is one native executable copied to both first install and
    # private update support. Inspect PE subsystem without running UAC or SQL.
    $setupRelease = Join-Path $release 'FMSC.Setup.exe'
    $setupUpdate = Join-Path $application 'UpdateSupport/FMSC.Setup.exe'
    if ((Get-FileHash -LiteralPath $setupRelease -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath $setupUpdate -Algorithm SHA256).Hash) {
        throw 'Full installer and private update must package the same FMSC.Setup.exe.'
    }
    $setupBytes = [IO.File]::ReadAllBytes($setupRelease)
    $pe = [BitConverter]::ToInt32($setupBytes, 0x3c)
    if ($pe -lt 64 -or $pe + 96 -ge $setupBytes.Length -or
        [BitConverter]::ToUInt16($setupBytes, $pe + 24 + 68) -ne 2) {
        throw 'FMSC.Setup.exe must be a Windows GUI executable, not a console application.'
    }
    $uninstallRelease = Join-Path $release 'FMSC.Uninstall.exe'
    $uninstallApplication = Join-Path $application 'FMSC.Uninstall.exe'
    if ((Get-FileHash -LiteralPath $uninstallRelease -Algorithm SHA256).Hash -cne
        (Get-FileHash -LiteralPath $uninstallApplication -Algorithm SHA256).Hash) {
        throw 'The full installer and installed application must package the same uninstaller.'
    }
    $uninstallBytes = [IO.File]::ReadAllBytes($uninstallRelease)
    $uninstallPe = [BitConverter]::ToInt32($uninstallBytes,0x3c)
    if ($uninstallPe -lt 64 -or $uninstallPe+96 -ge $uninstallBytes.Length -or
        [BitConverter]::ToUInt16($uninstallBytes,$uninstallPe+24+68) -ne 2) {
        throw 'FMSC.Uninstall.exe must be a Windows GUI executable.'
    }
    & (Join-Path $ProjectDirectory 'Tools/TestUpdateAutomation.ps1')
    if (-not (Test-Path -LiteralPath (Join-Path $release 'Migrations') -PathType Container)) { throw 'Package must include its migrations folder even when empty.' }
    $sourceSql = @(Get-ChildItem -LiteralPath (Join-Path $ProjectDirectory 'Migrations') -Filter '*.sql' -File)
    $copiedSql = @(Get-ChildItem -LiteralPath (Join-Path $release 'Migrations') -Filter '*.sql' -File)
    if ($sourceSql.Count -ne $copiedSql.Count) { throw 'Packaged migration inventory differs from source.' }
    $updateMigrations = Join-Path $application 'UpdateSupport/Migrations'
    $updateSql = if (Test-Path -LiteralPath $updateMigrations) { @(Get-ChildItem -LiteralPath $updateMigrations -Filter '*.sql' -File) } else { @() }
    if ($sourceSql.Count -ne $updateSql.Count) { throw 'Update support migration inventory differs from source.' }
    foreach ($folder in @((Join-Path $release 'Migrations'), $updateMigrations)) {
        if (-not (Test-Path -LiteralPath (Join-Path $folder 'retained.txt'))) { throw 'Migration cleanup removed an unrelated file.' }
    }
    foreach ($file in $sourceSql) {
        if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne
            (Get-FileHash -LiteralPath (Join-Path $application ('UpdateSupport/Migrations/' + $file.Name))).Hash) {
            throw "Update migration content differs: $($file.Name)"
        }
    }
    # Exercise the actual copied helper, not merely its source-tree counterpart.
    # Use a child scope so the test's mocked process/console commands stay local.
    & (Join-Path $ProjectDirectory 'Tools/TestInstallerLocalDb.ps1') -InstallerPath (Join-Path $release 'core.ps1')
    & (Join-Path $ProjectDirectory 'Tools/TestInstallerVcRuntime.ps1') -InstallerPath (Join-Path $release 'core.ps1')
    & (Join-Path $ProjectDirectory 'Tools/TestInstallerMigrations.ps1') -InstallerPath (Join-Path $release 'core.ps1')
    # Exercise the real published WinExe's headless path. These targets must be
    # refused before any network/SQL connection or interactive startup occurs.
    foreach ($arguments in @(
        @('--verify-installation', 'remote.invalid', 'FMSC_DB'),
        @('--verify-installation', '(localdb)\MSSQLLocalDB', 'master'),
        @('--verify-installation'),
        @('--install-communication', 'remote.invalid', 'FMSC_DB', 'missing.sql')
    )) {
        $process = Start-Process -FilePath (Join-Path $application 'FMSC_SMS.exe') -ArgumentList $arguments -PassThru
        if (-not $process.WaitForExit(10000)) { $process.Kill(); throw 'Installer verification unexpectedly opened interactive startup.' }
        if ($process.ExitCode -ne 23) { throw "Installer verification did not refuse an unsafe target: $($process.ExitCode)" }
    }
    Write-Host 'PASS: published installer CLI refuses remote, wrong-catalog and incomplete targets without interactive startup.'
    Write-Host 'PASS: published installer matches the checked-out revision and migration inventory.'
    Write-Host 'PASS: full PortableRelease publish, asset validation and installer/migration copies with a PowerShell-7-only parent module path.'

} finally {
    $env:PSModulePath = $modulePathBefore
    if (Test-Path -LiteralPath $scratch) {
        Remove-Item -LiteralPath $scratch -Recurse -Force
    }
}
# Keep the native exit code from deliberately refused test invocations from failing the workflow.
$global:LASTEXITCODE = 0
