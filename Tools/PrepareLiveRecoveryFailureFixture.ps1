# DEVELOPMENT-ONLY: prepare a deliberately failing FMSC update payload for a
# controlled Windows recovery rehearsal. NEVER run on school installations.
# Does not install, launch FMSC, modify live SQL or touch ProgramData.
param(
    [Parameter(Mandatory=$true)][string]$PreparedSource,
    [string]$OutputDirectory = 'C:\FMSC-Recovery-Failure-Fixture',
    [string]$DevAssembly = 'C:\FMSC-Recovery-Dev\bin\Release\net8.0-windows\FMSC_SMS.dll'
)
$ErrorActionPreference = 'Stop'

# The source is an untouched update prepared by FMSC in the current user's cache.
$source = [IO.Path]::GetFullPath($PreparedSource).TrimEnd('\')
$expectedParent = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'FMSC\Updates')).TrimEnd('\')
if ((Split-Path -Parent $source) -ine $expectedParent -or
    (Split-Path -Leaf $source) -notmatch '^prepared-[a-f0-9]{32}$') {
    throw 'Source must be an original prepared-* folder inside the current user FMSC update cache.'
}
if (-not (Test-Path -LiteralPath (Join-Path $source 'application\FMSC_SMS.dll') -PathType Leaf)) {
    throw 'Prepared source is missing application binaries.'
}
$manifestPath = Join-Path $source 'metadata\update-manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.format -ne 'fmsc-app-update/v1' -or
    $manifest.version -ne '1.0.10' -or $manifest.requiredDatabaseSchema -ne 7) {
    throw 'The original prepared package must be application 1.0.10 / schema 7.'
}
$destination = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
if ($destination -ine 'C:\FMSC-Recovery-Failure-Fixture') {
    throw 'Refusing output outside the dedicated developer test location.'
}
if (Test-Path -LiteralPath $destination) {
    throw 'Developer test fixture already exists. Nothing overwritten.'
}
if (-not (Test-Path -LiteralPath $DevAssembly -PathType Leaf)) {
    throw 'Missing locally built dev FMSC_SMS.dll. Run dotnet build -c Release first.'
}
if ((Get-Item -LiteralPath $DevAssembly).VersionInfo.ProductVersion -notmatch '^1\.0\.10(?:\+|$)') {
    throw 'The developer DLL must identify as application version 1.0.10.'
}
$links = @(Get-ChildItem -LiteralPath $source -Recurse -Force | Where-Object {
    $_.Attributes -band [IO.FileAttributes]::ReparsePoint
})
if ($links.Count) { throw 'Prepared package contains linked files. Refusing to copy.' }

# Source control commit with compatibility fix for prior installed app 1.0.8:
# the journal is terminal Completed + Outcome Aborted, not a new phase that
# the old app startup guard would reject.
$recoveryCommit = 'af7229c91f2634d4c873f4c0c0b4fc48fed5a210'
. (Join-Path $PSScriptRoot 'ResolveFmscGitHubRepository.ps1')
$sourceRepo=Resolve-FmscGitHubRepository -Repository Source
$downloadBase = "https://raw.githubusercontent.com/$($sourceRepo.FullName)/$recoveryCommit/Installer"

$null = New-Item -ItemType Directory -Path $destination
try {
    Copy-Item -LiteralPath (Join-Path $source 'application') -Destination $destination -Recurse -ErrorAction Stop
    Copy-Item -LiteralPath (Join-Path $source 'metadata') -Destination $destination -Recurse -ErrorAction Stop
    $app = Join-Path $destination 'application'
    $support = Join-Path $app 'UpdateSupport'
    Copy-Item -LiteralPath $DevAssembly -Destination (Join-Path $app 'FMSC_SMS.dll') -Force
    foreach ($name in @('core.ps1','UpdateRecovery.ps1','Launch-FMSC.ps1')) {
        Invoke-WebRequest -UseBasicParsing -Uri "$downloadBase/$name" -OutFile (Join-Path $support $name)
    }
    $corePath = Join-Path $support 'core.ps1'
    $core = [IO.File]::ReadAllText($corePath,[Text.Encoding]::UTF8)

    # Production core.ps1 contains no fault injection. Only this copied file is
    # altered. At the migration executor boundary we always throw, therefore
    # this test package cannot execute any SQL migration.
    $migrationCall = '        Invoke-SqlMigration -FilePath $migration.FilePath -VersionBefore $current -ExpectedVersion $migration.Version'
    if ($core.Split(@($migrationCall),[StringSplitOptions]::None).Count -ne 2) {
        throw 'Migration invocation changed. Developer fixture was not prepared.'
    }
    $fault = @'
        # DEVELOPMENT-ONLY FAULT; THIS COPY MUST NOT BE PUBLISHED.
        if (-not $script:RecoveryJournal -or
            $current -ne 5 -or $migration.Version -ne 6 -or
            $script:RecoveryJournal.Phase -ne 'Migrating' -or
            $script:RecoveryJournal.BeforeSchema -ne 5 -or
            $script:RecoveryJournal.Schema -ne 7 -or
            -not $script:RecoveryJournal.Backup -or
            (Get-FileHash -LiteralPath $script:RecoveryJournal.Backup -Algorithm SHA256).Hash -ne
                $script:RecoveryJournal.BackupSha256) {
            throw 'FMSC DEV TEST GUARD: unexpected state; no SQL migration was run.'
        }
        throw 'FMSC DEV TEST: failure AFTER verified backup and BEFORE migration 6.'
'@
    $core = $core.Replace($migrationCall, $fault + [Environment]::NewLine + $migrationCall)

    # Fail closed before even calling the migration orchestrator if this isn't
    # the exact schema baseline we intentionally prepared for.
    $orchestratorCall = '    Invoke-PendingMigrations -Directory $MigrationsDir -RequiredVersion (Convert-ToDbVersion $DatabaseVersion)'
    if ($core.Split(@($orchestratorCall),[StringSplitOptions]::None).Count -ne 2) {
        throw 'Migration orchestration changed. Developer fixture was not prepared.'
    }
    $guard = @'
    # DEVELOPMENT-ONLY: refuse any baseline other than schema v5.
    if ((Convert-ToDbVersion (Get-DatabaseVersion)) -ne 5 -or
        -not (Test-Path -LiteralPath (Join-Path $MigrationsDir '6.sql') -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $MigrationsDir '7.sql') -PathType Leaf)) {
        throw 'FMSC DEV TEST GUARD: schema 5 and migrations 6/7 required; no migration was run.'
    }
'@
    $core = $core.Replace($orchestratorCall, $guard + [Environment]::NewLine + $orchestratorCall)
    [IO.File]::WriteAllText($corePath, $core, [Text.UTF8Encoding]::new($false))
    if ($core -notmatch 'FMSC DEV TEST: failure AFTER verified backup') {
        throw 'Test-only failure marker is missing.'
    }
    [IO.File]::WriteAllText(
        (Join-Path $app 'DEV_TEST_ONLY.txt'),
        'DEVELOPMENT RECOVERY TEST. Intentionally fails after database backup; never deploy to schools.',
        [Text.Encoding]::UTF8
    )

    # Build a fresh complete manifest for this intentionally authored test
    # package; the production installer still validates every file and digest.
    $files = @(Get-ChildItem -LiteralPath $app -File -Recurse | Sort-Object FullName | ForEach-Object {
        [ordered]@{
            path = $_.FullName.Substring($app.Length + 1).Replace('\','/')
            length = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    })
    $testManifest = [ordered]@{
        format='fmsc-app-update/v1'
        version='1.0.10'
        requiredDatabaseSchema=7
        files=$files
    }
    [IO.File]::WriteAllText(
        (Join-Path $destination 'metadata\update-manifest.json'),
        ($testManifest | ConvertTo-Json -Depth 10),
        [Text.UTF8Encoding]::new($false)
    )
    Write-Host 'DEV-ONLY PACKAGE PREPARED:' $destination
    Write-Host 'Files in manifest:' $files.Count
    Write-Host 'Fault injection: before migration 6 (after real backup)'
    Write-Host 'Published package and live FMSC: unchanged'
} catch {
    Write-Warning "Package preparation failed; incomplete fixture remains at $destination. Do not run it."
    throw
}
