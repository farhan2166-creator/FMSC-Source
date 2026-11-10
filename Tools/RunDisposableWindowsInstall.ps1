# Real installation acceptance on a GitHub-hosted disposable Windows runner.
# Runs the production installer against a pinned schema-only, record-free test fixture.
param([string]$ProjectDirectory = (Split-Path $PSScriptRoot -Parent), [switch]$Level3, [string]$FixtureRoot = '', [int]$FixtureSchema = 0)
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_OS -ne 'Windows' -or
    $env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or $env:FMSC_DISPOSABLE_INSTALL -ne 'approved-synthetic-only') {
    throw 'Refusing to run outside a designated disposable Windows GitHub Actions job.'
}
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
$principal=[Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Runner lacks elevation needed for the real installer.'
}
$project=[IO.Path]::GetFullPath($ProjectDirectory)
. (Join-Path $project 'Installer/MaintenanceTools.ps1')
$expectedSchema=[regex]::Match((Get-Content -LiteralPath (Join-Path $project 'Installer/core.ps1') -Raw), '\$DatabaseVersion\s*=\s*''([0-9]+)''').Groups[1].Value
if (-not $expectedSchema) { throw 'Cannot determine the installer schema contract.' }
$root=Join-Path $(if ($FixtureRoot) { $env:LOCALAPPDATA } else { $env:RUNNER_TEMP }) 'fmsc-disposable-installer'
$evidence=Join-Path $env:RUNNER_TEMP 'fmsc-install-evidence'
$release=Join-Path $root 'release'
$app=Join-Path $release 'FMSC_SMS'
$tools=Join-Path $release 'Tools'
$database=Join-Path $release 'Database'
$localMsi=Join-Path $tools 'SqlLocalDB.msi'
$vcRedist=Join-Path $tools 'vc_redist.x64.exe'
$sqlcmd=Join-Path $tools 'sqlcmd.exe'
$instance='(localdb)\MSSQLLocalDB'
$installed='C:\FMSC School Management'
$live='C:\ProgramData\FMSC_Database'
$shortcut=Join-Path ([Environment]::GetFolderPath('Desktop')) 'FMSC SMS.lnk'
$recoveryStorage=Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'FMSC_Updater'
$ownedDatabase=$false
$ownedInstallation=$false
$ownedRoot=$false
$fixtureFiles=@()
$fixtureProbeName=''
$cleanupResult='NOT_REQUIRED'
$stage='Runner isolation'
$checks=[Collections.Generic.List[string]]::new()
$status='BLOCKED'
$msiCoverage='NOT_TESTED'
$level3Owner=[guid]::NewGuid().ToString()
$themeFile=Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'FMSC_SMS/theme.txt'
$ownedTheme=$false
New-Item -ItemType Directory -Path $evidence -Force | Out-Null

function Stage([string]$Name) { $script:stage=$Name; Write-Host "ACCEPTANCE STAGE: $Name" }
function NativeOk([string]$Name) { if ($LASTEXITCODE -ne 0) { throw "$Name exited with code $LASTEXITCODE" } }
function Sql([string]$Db, [string]$Query) {
    $result=@(& $script:sqlcmd -S $script:instance -E -d $Db -b -l 30 -t 60 -h -1 -W -Q $Query 2>&1)
    if ($LASTEXITCODE -ne 0) {
        # Only SQL error messages from approved synthetic fixture jobs. Exclude
        # arbitrary query output that might contain SQL rows or application data.
        $errors=@($result | ForEach-Object { $_.ToString() } |
            Where-Object { $_ -match '(?i)^(sqlcmd:|msg [0-9]+|level [0-9]+|error |fatal |.*error:)' } |
            Select-Object -Last 5)
        throw "SQL verification on $Db exited with code $LASTEXITCODE. $($errors -join ' | ')"
    }
    return $result
}
function Assert-EmbeddedDatabaseContract([string]$Db) {
    # Execute the same SQL structural contract embedded in the application, not
    # a weak check of version + a handful of table names.
    Add-Type -AssemblyName System.Data
    $source=Get-Content -LiteralPath (Join-Path $project 'Core/DatabaseCompatibility.cs') -Raw
    $match=[regex]::Match($source,'(?s)using var command = new SqlCommand\("""\s*(.*?)\s*""", connection\)')
    if (-not $match.Success) { throw 'Cannot locate production structural SQL compatibility query.' }
    $requirements=@(
        (Get-Content -LiteralPath (Join-Path $project 'Core/DatabaseCompatibilityRequirements.json') -Raw)
        (Get-Content -LiteralPath (Join-Path $project 'Core/DatabaseCompatibilitySchema10Requirements.json') -Raw)
        (Get-Content -LiteralPath (Join-Path $project 'Core/DatabaseCompatibilitySchema12Requirements.json') -Raw)
    )
    $cn=[System.Data.SqlClient.SqlConnection]::new("Data Source=$script:instance;Initial Catalog=$Db;Integrated Security=True;Encrypt=False")
    try {
        $cn.Open()
        $cmd=$cn.CreateCommand()
        $cmd.CommandText='SELECT compatibility_level FROM sys.databases WHERE database_id=DB_ID();'
        if ([int]$cmd.ExecuteScalar() -lt 130) { throw 'SQL compatibility level below production minimum 130.' }
        $version=@(Sql $Db 'SET NOCOUNT ON; SELECT TOP(1) CONVERT(nvarchar(50),VersionNumber) FROM dbo.DatabaseVersion ORDER BY VersionId DESC;')
        if (($version -join '').Trim() -ne $expectedSchema) {
            throw 'Installed schema version differs from required application version.'
        }
        $cmd.CommandText=$match.Groups[1].Value
        $cmd.CommandTimeout=120
        [void]$cmd.Parameters.Add('@Requirements',[System.Data.SqlDbType]::NVarChar,-1)
        foreach ($requiredJson in $requirements) {
            $cmd.Parameters['@Requirements'].Value=$requiredJson
            $result=[System.Data.DataSet]::new()
            $adapter=[System.Data.SqlClient.SqlDataAdapter]::new($cmd)
            [void]$adapter.Fill($result)
            if ($result.Tables.Count -lt 2) { throw 'Structural compatibility SQL returned incomplete results.' }
            $count=[int]$result.Tables[0].Rows[0][0]
            if ($count -ne 0) {
                $sample=@($result.Tables[1].Rows | ForEach-Object { $_[0].ToString() } | Select-Object -First 12)
                throw "Production SQL compatibility contract rejects $count requirements: $($sample -join '; ')"
            }
        }
        $checks.Add("Verified all embedded SQL compatibility requirements after schema-$FixtureSchema installation")
    } finally { $cn.Dispose() }
}

function LocalDbExe {
    $exe=Get-Command sqllocaldb.exe -ErrorAction SilentlyContinue
    if ($exe) { return $exe.Source }
    $known=Join-Path $env:ProgramFiles 'Microsoft SQL Server\160\Tools\Binn\SqlLocalDB.exe'
    if (Test-Path -LiteralPath $known -PathType Leaf) { return $known }
    return $null
}
function Invoke-ActualInstaller {
    $installer=Join-Path $release 'core.ps1'
    $psi=[Diagnostics.ProcessStartInfo]::new('powershell.exe')
    foreach ($a in @('-NoProfile','-ExecutionPolicy','Bypass','-File',$installer)) { [void]$psi.ArgumentList.Add($a) }
    $psi.UseShellExecute=$false
    $psi.RedirectStandardInput=$true
    $psi.RedirectStandardOutput=$true
    $psi.RedirectStandardError=$true
    $script:ownedInstallation=$true
    $p=[Diagnostics.Process]::Start($psi)
    if (-not $p) { throw 'Failed to start installer process.' }
    $stdout=$p.StandardOutput.ReadToEndAsync()
    $stderr=$p.StandardError.ReadToEndAsync()
    $p.StandardInput.WriteLine('N')  # Do not start interactive WinForms UI.
    $p.StandardInput.Close()
    if (-not $p.WaitForExit(720000)) {
        try { $p.Kill($true) } catch { }
        throw 'Installer timed out; never claim success.'
    }
    $installerOutput=$stdout.GetAwaiter().GetResult()
    $installerError=$stderr.GetAwaiter().GetResult()
    # Retain synthetic diagnostics on the disposable runner only, never as artifacts.
    @($installerOutput,$installerError) | Set-Content -LiteralPath (Join-Path $root 'installer-private.log')
    if ($p.ExitCode -ne 0) {
        # The private fixture repo has no real records or source-repository
        # publishing credentials. Only there, expose a bounded diagnostic tail
        # during the one-time integration proof; never print this in public CI.
        if ($env:GITHUB_REPOSITORY -eq 'farhan2166-creator/FMSC-Sandbox' -and
            $env:FMSC_DISPOSABLE_INSTALL -eq 'approved-synthetic-only') {
            Write-Host 'PRIVATE SANDBOX INSTALLER DIAGNOSTIC (last 35 lines):'
            $details=@(($installerOutput+"`n"+$installerError) -split '\r?\n' | Select-Object -Last 35)
            foreach ($line in $details) { Write-Host $line.Substring(0,[Math]::Min(500,$line.Length)) }
        }
        # Only fixed installer phase names and known error categories enter public
        # logs. Raw diagnostics can contain SQL/configuration and remain private.
        $phases=[regex]::Matches($installerOutput,'STEP [1-8] OF 8  -  [A-Z ]+')
        if ($phases.Count) { Write-Host ('Installer stopped during '+$phases[$phases.Count-1].Value.Trim()) }
        foreach ($reason in @('Invalid version number','Invalid database schema version','FMSC_SMS.exe was not found','Installation stopped to prevent an application downgrade')) {
            if (($installerOutput+$installerError).Contains($reason)) { Write-Host ('Installer failure category: '+$reason) }
        }
        # Public CI reports ONLY allowlisted, non-secret SQL error identifiers
        # and fixed semantic classifications, never raw installer/SQL text.
        # Raw details stay in the disposable runner's private log.
        $sourceLog=Join-Path $live 'Install.log'
        $sourceText=$installerOutput+"\n"+$installerError
        if (Test-Path -LiteralPath $sourceLog -PathType Leaf) {
            $sourceText+="\n"+(Get-Content -LiteralPath $sourceLog -Raw -ErrorAction SilentlyContinue)
        }
        $codes=@([regex]::Matches($sourceText,'(?i)\b(?:Msg\s+|SQL error\s*:?\s*)(?<code>[0-9]{4,6})\b') |
            ForEach-Object { $_.Groups['code'].Value } | Sort-Object -Unique)
        if ($codes.Count) {
            Write-Host ('Installer SQL diagnostics: error codes '+($codes -join ', '))
        }
        foreach ($item in @(
            @{ Pattern='(?i)Migration\s+16\s+failed'; Label='Migration 16 rejected' },
            @{ Pattern='(?i)Migration\s+15\s+failed'; Label='Migration 15 rejected' },
            @{ Pattern='(?i)Migration\s+[0-9]+\s+failed'; Label='SQL migration rejected' },
            @{ Pattern='(?i)verified backup.*failed|backup.*VERIFYONLY'; Label='Recovery-backup precondition rejected' },
            @{ Pattern='(?i)The reviewed admission transport predicate has changed'; Label='Admission policy source drift (51008)' },
            @{ Pattern='(?i)Unexpected admission procedure header'; Label='Admission procedure-header source drift (51009)' },
            @{ Pattern='(?i)Required reference catalog bootstrap is incomplete'; Label='First-run reference bootstrap incomplete (51005)' },
            @{ Pattern='(?i)No such file or directory|Migrations folder'; Label='Missing staged migration input' }
        )) {
            if ($sourceText -match $item.Pattern) {
                Write-Host ('Installer sanitized category: '+$item.Label)
            }
        }
    }
    return $p.ExitCode
}
function Assert-Level3Database {
    $marker=@(Sql 'FMSC_DB' "SET NOCOUNT ON; SELECT CONVERT(nvarchar(36),value) FROM sys.extended_properties WHERE class=0 AND name=N'FMSC_Level3InstallOwner';")
    if (($marker -join '').Trim() -ne $script:level3Owner) { throw 'Synthetic installation ownership changed; operation refused.' }
    Sql 'FMSC_DB' "IF (SELECT COUNT(*) FROM sys.database_files WHERE physical_name IN (N'C:\ProgramData\FMSC_Database\FMSC_DB.mdf',N'C:\ProgramData\FMSC_Database\FMSC_DB_log.ldf'))<>2 THROW 51005,'Unexpected synthetic file identity',1;" | Out-Null
}
function Install-Snapshot {
    Assert-Level3Database
    return ((@(Sql 'FMSC_DB' "SET NOCOUNT ON; SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),(SELECT (SELECT * FROM dbo.SchoolProfile FOR JSON PATH) School,(SELECT * FROM dbo.Users ORDER BY UserId FOR JSON PATH) Users,(SELECT * FROM dbo.Roles ORDER BY RoleId FOR JSON PATH) Roles,(SELECT * FROM dbo.DatabaseVersion ORDER BY VersionId FOR JSON PATH) Versions FOR JSON PATH))),2); SELECT CONVERT(nvarchar(36),database_guid) FROM sys.database_recovery_status WHERE database_id=DB_ID();")) -join '').Trim()
}

function WriteEvidence {
    # GitHub SHA belongs to the private Sandbox workflow, not the public Source checkout.
    $testedSha=if ($env:FMSC_TESTED_SOURCE_SHA -match '^[0-9a-f]{40}$') {
        $env:FMSC_TESTED_SOURCE_SHA
    } else { $env:GITHUB_SHA }
    $report=[ordered]@{
        sourceCommit=$testedSha
        result=$script:status
        lastStage=$script:stage
        localDbMsiCoverage=$script:msiCoverage
        level3=[bool]$Level3
        cleanup=$script:cleanupResult
        checksPassed=@($script:checks.ToArray())
        limitations=@(
          'GitHub-hosted Windows 2022 is disposable but may include preinstalled LocalDB.',
          'When LocalDB was preinstalled this run does not cover a pristine MSI installation or its interactive fallback.',
          'Only vetted record-free sandbox SQL fixtures or the separate first-production schema model are used; no school data is used.',
          'Graphical login, printing, real production upgrades and Windows reboot scenarios remain manual.',
          'A passing run is not authorization to install on live school computers.'
        )
    }
    $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $script:evidence 'report.json') -Encoding utf8
    $summary=@('# FMSC Disposable Windows Installation', '', "Commit: $testedSha", "Result: **$script:status**",
        "Last stage: $script:stage", "LocalDB MSI: $script:msiCoverage", "Cleanup: $script:cleanupResult", '', '## Completed checks')
    $summary+=@($script:checks | ForEach-Object { "- $_" })
    $summary+=@('','## Coverage limits')
    $summary+=@($report.limitations | ForEach-Object { "- $_" })
    $summary | Set-Content -LiteralPath (Join-Path $script:evidence 'summary.md') -Encoding utf8
    if ($env:GITHUB_STEP_SUMMARY) { $summary | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding utf8 }
}

try {
    foreach ($path in @($installed,"$installed.staging","$installed.rollback",$live,$shortcut)) {
        if (Test-Path -LiteralPath $path) { throw 'Pre-existing FMSC installation assets: refusing to touch them.' }
    }
    if (Test-Path -LiteralPath $root) { throw 'Disposable test workspace already exists.' }
    if (Test-Path -LiteralPath $recoveryStorage) { throw 'Recovery storage already exists; refusing to alter an existing installation.' }
    New-Item -ItemType Directory -Path $root,$release,$app,$tools,$database,(Join-Path $root 'empty-source') -Force | Out-Null
    $ownedRoot=$true
    $checks.Add('No existing FMSC application or target database found')

    Stage 'Approved SQL fixture integrity'
    if ($FixtureRoot) {
        if ($FixtureSchema -notin @(3,7)) { throw 'Private fixture must explicitly select supported schema 3 or 7.' }
        $sandbox=[IO.Path]::GetFullPath($FixtureRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
        $workspace=[IO.Path]::GetFullPath($env:GITHUB_WORKSPACE).TrimEnd([IO.Path]::DirectorySeparatorChar)
        $expectedRoot=Join-Path $workspace 'fmsc-sandbox'
        if ($sandbox -ine $expectedRoot -or -not (Test-Path -LiteralPath $sandbox -PathType Container)) {
            throw 'Fixture location must be the trusted private sandbox checkout under the runner workspace.'
        }
        $manifestPath=Join-Path $sandbox 'Tests/manifest.json'
        if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'Private fixture manifest is missing.' }
        $manifest=Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        if ($manifest.format -ne 'fmsc-sandbox-fixtures/v1') { throw 'Unrecognized SQL fixture manifest.' }
        $selected=@($manifest.fixtures | Where-Object { $_.schema -eq $FixtureSchema })
        if ($selected.Count -ne 1) { throw 'Exactly one approved fixture entry is required for this schema.' }
        $record=$selected[0]
        if ($record.status -ne 'extracted-schema-only-pending-sql-validation' -and $record.status -ne 'validated-schema-only') {
            throw 'Fixture is not in an accepted data-free review state.'
        }
        if ($record.mdf -cne "Database/v$FixtureSchema/FMSC_DB.mdf" -or
            $record.ldf -cne "Database/v$FixtureSchema/FMSC_DB_log.ldf") {
            throw 'Fixture manifest contains an unexpected path.'
        }
        foreach ($item in @(
            @{path=$record.mdf;sha=$record.mdfSha256;bytes=8388608},
            @{path=$record.ldf;sha=$record.ldfSha256;bytes=75497472}
        )) {
            $source=Join-Path $sandbox ($item.path.Replace('/','\'))
            $file=Get-Item -LiteralPath $source -ErrorAction Stop
            if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
                $file.Length -ne $item.bytes -or
                $item.sha -notmatch '^[a-fA-F0-9]{64}$' -or
                (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ine $item.sha) {
                throw 'Private fixture failed size, LFS-smudge or SHA-256 verification.'
            }
        }
        $checks.Add("Private schema-$FixtureSchema MDF/LDF are exact Git LFS bytes checked against the sandbox manifest")
    } else {
    $fixture=Join-Path $project 'Tests/LifecycleSqlChecks/Baseline/schema.dacpac'
    if (-not (Test-Path -LiteralPath $fixture -PathType Leaf)) { throw 'Disposable SQL test fixture is missing.' }
    Add-Type -AssemblyName System.IO.Compression
    $zip=[IO.Compression.ZipFile]::OpenRead($fixture)
    try {
        $names=@($zip.Entries | ForEach-Object { $_.FullName } | Sort-Object)
        $allowed=@('[Content_Types].xml','_rels/.rels','DacMetadata.xml','model.xml','Origin.xml' | Sort-Object)
        if (($names -join '|') -ne ($allowed -join '|') -or $names.Count -ne 5) {
            throw 'Unexpected SQL test fixture entries: exported data/scripts are forbidden.'
        }
        function Read-SqlFixtureEntry($archive, [string]$name) {
            $entry=$archive.GetEntry($name)
            if ($null -eq $entry) { throw "Missing SQL fixture entry: $name" }
            $reader=[IO.StreamReader]::new($entry.Open())
            try { return $reader.ReadToEnd() }
            finally { $reader.Dispose() }
        }
        [xml]$origin=(Read-SqlFixtureEntry $zip 'Origin.xml')
        if ($origin.SelectNodes("//*[local-name()='ExportStatistics' or local-name()='Server' or local-name()='DataPhaseTables']").Count -gt 0) {
            throw 'SQL test fixture contains export/source metadata.'
        }
        foreach ($flag in $origin.SelectNodes("//*[local-name()='ContainsExportedData']")) {
            if ($flag.InnerText.Trim().ToLowerInvariant() -ne 'false') { throw 'SQL test fixture contains exported-data marker.' }
        }
        [xml]$relationships=(Read-SqlFixtureEntry $zip '_rels/.rels')
        if ($relationships.SelectNodes('//*[@Target]').Count -gt 0) {
            throw 'SQL test fixture contains a prohibited package relationship.'
        }
        [xml]$model=(Read-SqlFixtureEntry $zip 'model.xml')
        if ($model.SelectNodes("//*[local-name()='Model']/*[local-name()='Element' and @Type='SqlProcedure']").Count -eq 0) {
            throw 'SQL test fixture has no deployable stored procedures.'
        }
    } finally { $zip.Dispose() }
    $checks.Add('Data-free SQL fixture contains schema-only entries and no export relationships')
    }

    Stage 'Real Microsoft SQL Server LocalDB prerequisite'
    $existing=LocalDbExe
    $downloadedMsi=Join-Path $root 'SqlLocalDB.msi'
    Invoke-FmscPinnedDownload -Uri (Get-FmscToolSpec 'LocalDb').url -OutFile $downloadedMsi -TimeoutSec 300
    if ((Get-Item -LiteralPath $downloadedMsi).Length -lt 1000000) { throw 'LocalDB MSI is incomplete.' }
    Assert-FmscMicrosoftPackage $downloadedMsi
    Copy-Item -LiteralPath $downloadedMsi -Destination $localMsi
    $downloadedVc=Join-Path $root 'vc_redist.x64.exe'
    Invoke-FmscPinnedDownload -Uri (Get-FmscToolSpec 'VcRedist').url -OutFile $downloadedVc -TimeoutSec 300
    if ((Get-Item -LiteralPath $downloadedVc).Length -lt 1000000) { throw 'Visual C++ x64 redistributable is incomplete.' }
    Assert-FmscMicrosoftPackage $downloadedVc
    Copy-Item -LiteralPath $downloadedVc -Destination $vcRedist
    if ($existing) {
        $msiCoverage='PREINSTALLED_NOT_A_CLEAN_MSI_TEST'
        Write-Host 'LocalDB already present in GitHub runner image; MSI installation was NOT exercised.'
    } else {
        $msiCoverage='REAL_SILENT_MSI_ATTEMPTED'
        $log=Join-Path $root 'localdb-msi-private.log'
        $p=Start-Process -FilePath 'msiexec.exe' -ArgumentList @(
            '/i', ('"' + $downloadedMsi + '"'), '/qn', '/norestart', 'IACCEPTSQLLOCALDBLICENSETERMS=YES', '/L*V', ('"' + $log + '"')
        ) -Wait -PassThru
        if ($p.ExitCode -in @(3010,1641)) { throw 'LocalDB requires a Windows restart; acceptance is blocked until reboot.' }
        if ($p.ExitCode -ne 0) { throw "Real silent LocalDB MSI failed with code $($p.ExitCode); interactive recovery needs an operator." }
        $msiCoverage='REAL_SILENT_MSI_SUCCEEDED'
    }
    $localExe=LocalDbExe
    if (-not $localExe) { throw 'LocalDB executable missing after setup.' }
    $null=& $localExe info MSSQLLocalDB 2>&1
    if ($LASTEXITCODE -ne 0) {
        & $localExe create MSSQLLocalDB 16.0
        NativeOk 'Create isolated-runner SQL Server 2022 LocalDB instance'
    }
    # Hosted Windows may transiently fail automatic LocalDB startup. Retry
    # only on this disposable runner; never delete/reset an existing instance.
    $healthyLocalDb=$false
    for ($attempt=1; $attempt -le 3; $attempt++) {
        $startOutput=@(& $localExe start MSSQLLocalDB 2>&1)
        $startCode=$LASTEXITCODE
        $info=@(& $localExe info MSSQLLocalDB 2>&1)
        $infoCode=$LASTEXITCODE
        $pipe=[regex]::Match(($info -join [Environment]::NewLine), '(?:np:)?(\\\\\.\\pipe\\LOCALDB#[A-Za-z0-9]+\\tsql\\query)')
        if ($startCode -eq 0 -and $infoCode -eq 0 -and $pipe.Success) {
            $healthyLocalDb=$true
            break
        }
        Write-Warning ("Disposable runner LocalDB not ready (attempt {0}/3; startExit={1}; infoExit={2}; namedPipe={3})." -f $attempt,$startCode,$infoCode,$pipe.Success)
        if ($attempt -lt 3) { Start-Sleep -Seconds (5 * $attempt) }
    }
    if (-not $healthyLocalDb) {
        throw 'Disposable runner LocalDB did not start with a healthy SQL endpoint after three attempts; no database operations permitted.'
    }
    $instance='np:'+$pipe.Groups[1].Value
    $checks.Add('Actual LocalDB instance started using Microsoft-signed prerequisites')

    Stage 'Pinned SQL command and full installer publishing'
    & (Join-Path $project 'Tools/PrepareBundledSqlCmd.ps1') -DestinationFile $sqlcmd
    $transport = Join-Path $root 'MinGit'
    & (Join-Path $project 'Tools/PrepareBundledGit.ps1') -DestinationDirectory $transport
    & dotnet restore (Join-Path $project 'FMSC_SMS.sln')
    NativeOk 'dotnet restore'
    $arguments=@('publish',(Join-Path $project 'FMSC_SMS.csproj'),'-c','Release','-r','win-x64',
        '--self-contained','true','-p:PublishProfile=PortableRelease',
        "-p:PublishDir=$app/","-p:ReleaseRoot=$release/",
        "-p:LocalDbMsiPath=$localMsi","-p:VcRedistPath=$vcRedist","-p:BundledGitDirectory=$transport",
        "-p:BundledSqlCmdPath=$sqlcmd",
        "-p:DeploymentDatabaseDirectory=$(Join-Path $root 'empty-source')",
        '-p:PublishSingleFile=false','-p:PublishTrimmed=false')
    & dotnet @arguments
    NativeOk 'Full PortableRelease publish'
    foreach ($file in @('FMSC_SMS/FMSC_SMS.exe','FMSC_SMS/UpdateSupport/FMSC.Setup.exe',
        'FMSC_SMS/UpdateTransport/cmd/git.exe','FMSC_SMS/UpdateTransport/usr/bin/ssh.exe',
        'FMSC_SMS/UpdateTransport/COPYING-GIT.txt',
        'FMSC_SMS/UpdateSupport/Tools/SqlCmd/sqlcmd.exe','FMSC_SMS/UpdateSupport/Tools/LocalDb/SqlLocalDB.msi',
        'FMSC_SMS/UpdateSupport/Tools/VcRedist/vc_redist.x64.exe','core.ps1','Install FMSC_SMS.bat','FMSC.Setup.exe',
        'Tools/SqlLocalDB.msi','Tools/vc_redist.x64.exe','Tools/sqlcmd.exe')) {
        if (-not (Test-Path -LiteralPath (Join-Path $release $file) -PathType Leaf)) {
            throw "Real installer asset missing: $file"
        }
    }
    $checks.Add('Actual validated PortableRelease published with MSI, sqlcmd and migrations')

    Stage 'Provision owned synthetic SQL installation source'
    $prior=@(Sql 'master' "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.databases WHERE name=N'FMSC_DB';")
    if (($prior -join '').Trim() -ne '0') { throw 'FMSC_DB already exists; refusing to overwrite it.' }
    $ownedDatabase=$true
    if ($FixtureRoot) {
        $sourceRoot=Join-Path $sandbox "Database/v$FixtureSchema"
        foreach ($name in @('FMSC_DB.mdf','FMSC_DB_log.ldf')) {
            $source=Join-Path $sourceRoot $name
            $destination=Join-Path $database $name
            Copy-Item -LiteralPath $source -Destination $destination -ErrorAction Stop
            # Only disposable copies may become writable. Never change the LFS checkout.
            $copied=Get-Item -LiteralPath $destination
            if ($copied.IsReadOnly) { $copied.IsReadOnly=$false }
            if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne
                (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash) {
                throw 'Copied installation fixture hash mismatch.'
            }
        }

        # Inspect another independent copy before the real installer touches its
        # installation template. Never attach the master LFS checkout itself.
        $probeRoot=Join-Path $root 'fixture-probe'
        New-Item -ItemType Directory -Path $probeRoot -Force | Out-Null
        $probeMdf=Join-Path $probeRoot 'fixture.mdf'
        $probeLdf=Join-Path $probeRoot 'fixture_log.ldf'
        Copy-Item -LiteralPath (Join-Path $database 'FMSC_DB.mdf') -Destination $probeMdf
        Copy-Item -LiteralPath (Join-Path $database 'FMSC_DB_log.ldf') -Destination $probeLdf
        foreach ($probeFile in @($probeMdf,$probeLdf)) {
            $probeItem=Get-Item -LiteralPath $probeFile
            if ($probeItem.IsReadOnly) { $probeItem.IsReadOnly=$false }
        }
        $fixtureProbeName="FMSC_FixtureProbe_$FixtureSchema"
        $escapedMdf=$probeMdf.Replace("'","''")
        $escapedLdf=$probeLdf.Replace("'","''")
        try {
            Sql 'master' "CREATE DATABASE [$fixtureProbeName] ON (FILENAME=N'$escapedMdf'),(FILENAME=N'$escapedLdf') FOR ATTACH;" | Out-Null
            $actual=@(Sql $fixtureProbeName 'SET NOCOUNT ON; SELECT TOP(1) TRY_CONVERT(int,VersionNumber) FROM dbo.DatabaseVersion ORDER BY VersionId DESC;')
            if (($actual -join '').Trim() -ne [string]$FixtureSchema) { throw 'Private SQL file schema version does not match its manifest.' }
            $readonly=@(Sql 'master' "SET NOCOUNT ON; SELECT is_read_only FROM sys.databases WHERE name=N'$fixtureProbeName';")
            if (($readonly -join '').Trim() -ne '0') { throw 'Disposable SQL fixture copy unexpectedly attached read-only.' }
            Sql $fixtureProbeName @'
SET NOCOUNT ON;
IF EXISTS(SELECT 1 FROM dbo.Students) OR EXISTS(SELECT 1 FROM dbo.Users)
    THROW 51008,'Sandbox fixture must be empty of personal school records',1;
IF NOT EXISTS(SELECT 1 FROM sys.extended_properties WHERE class=0 AND name=N'FMSC_DisposableFixture')
    THROW 51009,'Sandbox fixture is missing its dedicated disposable owner marker',1;
DBCC CHECKDB WITH NO_INFOMSGS;
'@ | Out-Null
            $checks.Add("Attached copied sandbox schema-$FixtureSchema MDF/LDF, verified declared version and passed DBCC CHECKDB")
        } finally {
            if ($fixtureProbeName) {
                Sql 'master' "IF DB_ID(N'$fixtureProbeName') IS NOT NULL BEGIN ALTER DATABASE [$fixtureProbeName] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; EXEC sys.sp_detach_db @dbname=N'$fixtureProbeName'; END;" | Out-Null
                $fixtureProbeName=''
            }
        }
        # DACPAC publication preserves SQL schema but does not seed operational
        # database-level GUID state. Create that state only on a disposable
        # installer input copy. Never modify FMSC-Sandbox's master binaries.
        $templateDb="FMSC_FixtureTemplate_$FixtureSchema"
        $templateMdf=(Join-Path $database 'FMSC_DB.mdf').Replace("'","''")
        $templateLdf=(Join-Path $database 'FMSC_DB_log.ldf').Replace("'","''")
        try {
            Sql 'master' "CREATE DATABASE [$templateDb] ON (FILENAME=N'$templateMdf'),(FILENAME=N'$templateLdf') FOR ATTACH;" | Out-Null
            Sql $templateDb @'
SET NOCOUNT ON;
DECLARE @scope uniqueidentifier=NEWID();
IF EXISTS(SELECT 1 FROM sys.extended_properties WHERE class=0 AND name=N'FMSC_QuickRegistrationScope')
    EXEC sys.sp_updateextendedproperty @name=N'FMSC_QuickRegistrationScope',@value=@scope;
ELSE
    EXEC sys.sp_addextendedproperty @name=N'FMSC_QuickRegistrationScope',@value=@scope;
IF NOT EXISTS(SELECT 1 FROM sys.extended_properties WHERE class=0 AND name=N'FMSC_QuickRegistrationScope'
    AND SQL_VARIANT_PROPERTY(value,'BaseType')='uniqueidentifier'
    AND TRY_CONVERT(uniqueidentifier,value)<>'00000000-0000-0000-0000-000000000000')
    THROW 51012,'Disposable fixture lacks required quick-registration identity',1;
'@ | Out-Null
        } finally {
            Sql 'master' "IF DB_ID(N'$templateDb') IS NOT NULL BEGIN ALTER DATABASE [$templateDb] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; EXEC sys.sp_detach_db @dbname=N'$templateDb'; END;" | Out-Null
        }
        $checks.Add("Prepared schema-$FixtureSchema installer COPY with native quick-registration recovery GUID")
    } else {
    Push-Location (Join-Path $project 'Tests/LifecycleSqlChecks')
    try {
        & dotnet tool restore
        NativeOk 'Pinned SQLPackage tool restore'
        $conn="Server=$instance;Database=FMSC_DB;Integrated Security=True;Encrypt=False;TrustServerCertificate=True"
        & dotnet tool run sqlpackage -- '/Action:Publish' "/SourceFile:$fixture" "/TargetConnectionString:$conn" '/p:CreateNewDatabase=True'
        NativeOk 'Publish record-free SQL test fixture'
    } finally { Pop-Location }
    $seed=@'
SET NOCOUNT ON;
IF EXISTS(SELECT 1 FROM dbo.DatabaseVersion) THROW 51000, 'Nonempty version fixture', 1;
IF EXISTS(SELECT 1 FROM dbo.Students) THROW 51001, 'Unexpected student rows', 1;
IF EXISTS(SELECT 1 FROM dbo.Users) THROW 51002, 'Unexpected user rows', 1;
EXEC sys.sp_addextendedproperty @name=N'FMSC_DisposableFixture',@value=N'approved-synthetic-only';
'@
    Sql 'FMSC_DB' $seed | Out-Null
    Sql 'FMSC_DB' (Get-Content (Join-Path $project 'Tests/LifecycleSqlChecks/Baseline/Initialize.sql') -Raw) | Out-Null
    $paths=@(& $sqlcmd -S $instance -E -d 'FMSC_DB' -b -h -1 -W -Q 'SET NOCOUNT ON; SELECT physical_name FROM sys.database_files;' 2>&1)
    NativeOk 'Find fixture MDF and LDF'
    $mdf=$paths | ForEach-Object { $_.ToString().Trim() } | Where-Object { $_ -match '^[A-Za-z]:\\.+\.mdf$' } | Select-Object -First 1
    $ldf=$paths | ForEach-Object { $_.ToString().Trim() } | Where-Object { $_ -match '^[A-Za-z]:\\.+\.ldf$' } | Select-Object -First 1
    if (-not $mdf -or -not $ldf) { throw 'Synthetic LocalDB MDF/LDF paths unavailable.' }
    $fixtureFiles=@($mdf,$ldf)
    Sql 'master' "ALTER DATABASE [FMSC_DB] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; EXEC sys.sp_detach_db @dbname=N'FMSC_DB';" | Out-Null
    Copy-Item -LiteralPath $mdf -Destination (Join-Path $database 'FMSC_DB.mdf')
    Copy-Item -LiteralPath $ldf -Destination (Join-Path $database 'FMSC_DB_log.ldf')
    $checks.Add('Generated a record-free first-release baseline-1 MDF/LDF pair from the reviewed SQL model')
    }

    Stage 'Actual FMSC Windows installer execution'
    $exit=Invoke-ActualInstaller
    if ($exit -ne 0) { throw "Actual installer failed with exit code $exit; inspect private runner diagnostics." }
    $installLog=Join-Path $live 'Install.log'
    if (-not (Test-Path -LiteralPath $installLog) -or
        -not (Select-String -LiteralPath $installLog -Pattern 'FMSC SMS installation completed successfully\.' -Quiet)) {
        throw 'Installer did not log its success marker.'
    }
    if (-not (Test-Path -LiteralPath (Join-Path $installed 'FMSC_SMS.exe')) -or
        -not (Test-Path -LiteralPath (Join-Path $live 'FMSC_DB.mdf')) -or
        -not (Test-Path -LiteralPath (Join-Path $live 'FMSC_DB_log.ldf'))) {
        throw 'Installer did not activate application and attach the synthetic database.'
    }
    $checks.Add('Ran actual core.ps1; application and LocalDB database installed successfully')
    if ($FixtureRoot) {
        Assert-EmbeddedDatabaseContract 'FMSC_DB'
        $checks.Add("Actual installer processed source schema-$FixtureSchema through the release migration path to schema $expectedSchema")
    }
    . (Join-Path $project 'Installer/UpdateRecovery.ps1')
    Assert-FmscRecoveryStorage $recoveryStorage
    $installedShortcut=(New-Object -ComObject WScript.Shell).CreateShortcut($shortcut)
    if ($installedShortcut.TargetPath -ine (Join-Path $recoveryStorage 'FMSC.Launcher.exe') -or $installedShortcut.Arguments) {
        throw 'Installed shortcut does not use the stable external launcher.'
    }
    foreach ($asset in @('MaintenanceTools.ps1','ToolCatalog.json','UpdateRecovery.ps1','Launch-FMSC.ps1','FMSC.Launcher.exe')) {
        if ((Get-FileHash (Join-Path $recoveryStorage $asset) -Algorithm SHA256).Hash -ne
            (Get-FileHash (Join-Path $app "UpdateSupport/$asset") -Algorithm SHA256).Hash) { throw 'Installed recovery launcher asset differs from source package.' }
    }
    $checks.Add('Actual installer created protected stable launcher and desktop shortcut outside the application folder')

    Stage 'Installed app schema verification'
    $version=@(Sql 'FMSC_DB' 'SET NOCOUNT ON; SELECT TOP(1) VersionNumber FROM dbo.DatabaseVersion ORDER BY VersionId DESC;')
    if (($version -join '').Trim() -ne $expectedSchema) { throw 'Wrong schema version installed.' }
    $exe=Join-Path $installed 'FMSC_SMS.exe'
    $verify=Start-Process -FilePath $exe -ArgumentList @('--verify-installation',('"'+$instance+'"'),'FMSC_DB') -PassThru
    $null=$verify.Handle
    if (-not $verify.WaitForExit(180000)) { $verify.Kill(); throw 'Installed verification timed out.' }
    # Installer verification checks the required core schema only. The retired
    # optional communication module must never influence this exit code.
    if ($verify.ExitCode -ne 0) {
        throw "Installed FMSC CLI core schema verification failed (expected 0; got $($verify.ExitCode))."
    }
    Sql 'FMSC_DB' @'
IF EXISTS(SELECT 1 FROM dbo.Students) OR EXISTS(SELECT 1 FROM dbo.Users)
    THROW 51003,'Installer unexpectedly created student/user data',1;
IF (SELECT COUNT(*) FROM sys.database_files WHERE physical_name IN
    (N'C:\ProgramData\FMSC_Database\FMSC_DB.mdf',N'C:\ProgramData\FMSC_Database\FMSC_DB_log.ldf'))<>2
    THROW 51004,'Installed database does not use the expected copied file pair',1;
'@ | Out-Null
    $checks.Add('Installed application launched in verification mode and accepted base SQL schema')
    if ($Level3) {
        Stage 'Level 3 installation ownership and fictional preservation records'
        Sql 'FMSC_DB' "DECLARE @Owner uniqueidentifier='$level3Owner'; EXEC sys.sp_addextendedproperty @name=N'FMSC_Level3InstallOwner',@value=@Owner;" | Out-Null
        Assert-Level3Database
        Sql 'FMSC_DB' @'
SET NOCOUNT ON;
-- Schema 16 must have initialized immutable reference data. Use the
-- same first-administrator stored procedure as the shipped setup wizard,
-- rather than inserting conflicting reference roles/user memberships.
IF (SELECT COUNT(*) FROM dbo.Roles WHERE RoleName=N'Administrator')<>1
 OR (SELECT COUNT(*) FROM dbo.Permissions)<>31
 OR (SELECT COUNT(*) FROM dbo.BillingAutomationState WHERE StateId=1)<>1
    THROW 51013,'First-run reference data was not prepared by migrations.',1;
DECLARE @Hash varbinary(64)=CONVERT(varbinary(64),REPLICATE('H',64)),
        @Salt varbinary(32)=CONVERT(varbinary(32),REPLICATE('S',32));
EXEC dbo.BootstrapFirstAdministrator
    @Username=N'level3_install_fixture', @PasswordHash=@Hash, @PasswordSalt=@Salt;
DECLARE @User int=(SELECT UserId FROM dbo.Users WHERE Username=N'level3_install_fixture');
IF @User IS NULL
 OR NOT EXISTS(SELECT 1 FROM dbo.UserRoles U JOIN dbo.Roles R ON R.RoleId=U.RoleId
               WHERE U.UserId=@User AND R.RoleName=N'Administrator')
    THROW 51014,'First-run administrator bootstrap did not grant the required role.',1;
INSERT dbo.SchoolProfile(SchoolProfileId,SchoolName,CreatedAt,UpdatedAt,CreatedByUserId,UpdatedByUserId)
VALUES(1,N'Fictional Level 3 Install School',SYSUTCDATETIME(),SYSUTCDATETIME(),@User,@User);
'@ | Out-Null
        if (Test-Path -LiteralPath $themeFile) { throw 'Theme settings already exist; refusing to overwrite them.' }
        New-Item -ItemType Directory -Path (Split-Path $themeFile -Parent) -Force | Out-Null
        '0' | Set-Content -LiteralPath $themeFile
        $ownedTheme=$true
        $themeHash=(Get-FileHash -LiteralPath $themeFile).Hash
        $before=Install-Snapshot
        # The installed pair is a copy; only the owned release template is removed.
        Remove-Item -LiteralPath (Join-Path $database 'FMSC_DB.mdf'),(Join-Path $database 'FMSC_DB_log.ldf') -Force
        foreach ($id in @('L3-INS-02','L3-INS-03')) {
            Stage "$id actual update/reinstallation without a database template"
            Assert-Level3Database
            $exit=Invoke-ActualInstaller
            if ($exit -ne 0 -or (Install-Snapshot) -ne $before -or (Get-FileHash -LiteralPath $themeFile).Hash -ne $themeHash) {
                throw "$id did not preserve fictional SQL records, database identity and user theme settings."
            }
            $checks.Add("$id | Actual installer preserves owned SQL records, database GUID and theme bytes without packaged MDF/LDF")
        }
        Stage 'L3-INS-04 missing published application prerequisite'
        Assert-Level3Database
        $sourceExe=Join-Path $app 'FMSC_SMS.exe'
        Move-Item -LiteralPath $sourceExe -Destination "$sourceExe.held"
        try {
            if ((Invoke-ActualInstaller) -eq 0) { throw 'Missing source executable was accepted.' }
            if ((Install-Snapshot) -ne $before -or -not (Test-Path -LiteralPath (Join-Path $installed 'FMSC_SMS.exe'))) { throw 'Prerequisite refusal damaged the installed app/database.' }
        } finally { Move-Item -LiteralPath "$sourceExe.held" -Destination $sourceExe }
        $checks.Add('L3-INS-04 cloud subset | Missing application prerequisite refused without replacing live app/data; MSI refusal is separately unit tested')
        Stage 'L3-INS-05 simulated partial activation failure and actual filesystem rollback'
        Assert-Level3Database
        # This independent rollback fault seam owns only its temporary files.
        # SQL MDF/LDF copies live in LOCALAPPDATA for LocalDB access; this
        # unrelated test must remain under the separately approved RUNNER_TEMP.
        & (Join-Path $project 'Tests/Level3Checks/TestActivation.ps1') -ProjectDirectory $project -ScratchRoot $env:RUNNER_TEMP
        if ((Install-Snapshot) -ne $before) { throw 'Activation fault test changed the installed SQL snapshot.' }
        $checks.Add('L3-INS-05 | Test-only injected partial directory move exercises production rollback; exact previous bytes restored and partial output removed; not a power-loss test')
        Stage 'Level 3 complete installer ZIP integrity, no database files'
        # Reuse real package assembly; redirect only its output root to the owned fixture.
        $workflow=Get-Content -LiteralPath (Join-Path $project '.github/workflows/fmsc-complete-installer.yml') -Raw
        $assembly=[regex]::Match($workflow,'(?ms)^      - name: Validate complete installer layout and create database instructions\r?\n        shell: pwsh\r?\n        run: \|\r?\n(?<body>.*?)(?=\r?\n      - name:|\z)')
        if (-not $assembly.Success) { throw 'Cannot locate the reviewed complete-installer assembly step.' }
        $body=[regex]::Replace($assembly.Groups['body'].Value,'(?m)^          ','')
        $originalRoot='$root = Join-Path $env:GITHUB_WORKSPACE ''out/complete-installer'''
        if (-not $body.Contains($originalRoot)) { throw 'Installer assembly root changed; review the acceptance adapter.' }
        $body=$body.Replace($originalRoot,('$root = '''+$release.Replace("'","''")+''''))
        & ([scriptblock]::Create($body))
        $zipPath=Join-Path $root 'installer-synthetic.zip'
        Compress-Archive -Path (Join-Path $release '*') -DestinationPath $zipPath
        $archive=[IO.Compression.ZipFile]::OpenRead($zipPath)
        try {
            $entries=@($archive.Entries | ForEach-Object { $_.FullName.Replace('\','/') })
            foreach ($required in @('FMSC_SMS/FMSC_SMS.exe','FMSC_SMS/UpdateSupport/FMSC.Setup.exe','core.ps1','Install FMSC_SMS.bat','FMSC.Setup.exe','Tools/SqlLocalDB.msi','Tools/vc_redist.x64.exe','Tools/sqlcmd.exe','Database/PUT_MDF_AND_LDF_HERE.txt','READ_ME_FIRST.txt','SHA256SUMS.txt')) {
                if ($required -notin $entries) { throw "ZIP is missing $required" }
            }
            if (@($entries | Where-Object { $_ -match '(?i)\.(mdf|ldf|ndf|bak)$' }).Count) { throw 'Database file entered the installer ZIP.' }
            $manifest=@{}
            foreach ($line in Get-Content -LiteralPath (Join-Path $release 'SHA256SUMS.txt')) {
                if ($line -notmatch '^([0-9a-fA-F]{64})  (.+)$') { throw 'Invalid installer checksum manifest row.' }
                $manifest[$Matches[2]]=$Matches[1].ToUpperInvariant()
            }
            foreach ($entry in $archive.Entries) {
                if ($entry.FullName.EndsWith('/') -or $entry.FullName.EndsWith('\')) { continue }
                $name=$entry.FullName.Replace('\','/')
                if ($name -eq 'SHA256SUMS.txt') { continue }
                if (-not $manifest.ContainsKey($name)) { throw 'ZIP asset is missing from checksum manifest.' }
                $stream=$entry.Open(); $sha=[Security.Cryptography.SHA256]::Create()
                try {
                    if ([Convert]::ToHexString($sha.ComputeHash($stream)) -ne $manifest[$name]) { throw 'ZIP asset checksum differs from the assembled installer.' }
                } finally { $stream.Dispose(); $sha.Dispose() }
            }
        } finally { $archive.Dispose() }
        $checks.Add('L3 ZIP | All required non-database assets present and readable; no MDF/LDF/NDF/BAK files; ZIP remains private and is deleted')
        Stage 'Actual prepared updater interrupted between activation renames and resumed by its stable launcher'
        Assert-Level3Database
        & (Join-Path $project 'Tools/TestPreparedUpdateRecovery.ps1') -SourceApplication $app
        Assert-Level3Database
        if ((Install-Snapshot) -ne $before -or (Get-FileHash -LiteralPath $themeFile).Hash -ne $themeHash) {
            throw 'Actual prepared recovery changed fictional school records, database identity or user settings.'
        }
        $checks.Add('Actual prepared installer and stable launcher | Forced activation interruption resumed with unchanged fictional school data/GUID/theme; verified cleanup and inert restart observation')
    }
    $status='PASS'
    Write-Host 'PASS: Disposable real Windows installer and synthetic LocalDB acceptance.'
}
catch {
    if ($stage -notin @('Runner isolation','Data-free SQL test fixture safety')) { $status='FAIL' }
    Write-Host "Installation acceptance stopped at '$stage': $($_.Exception.Message)"
    throw
}
finally {
    try {
        if ($fixtureProbeName) {
            Sql 'master' "IF DB_ID(N'$fixtureProbeName') IS NOT NULL BEGIN ALTER DATABASE [$fixtureProbeName] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; EXEC sys.sp_detach_db @dbname=N'$fixtureProbeName'; END;" | Out-Null
        }
        if ($ownedDatabase) {
            if ($Level3 -and $ownedTheme) { Assert-Level3Database }
            Sql 'master' "IF DB_ID(N'FMSC_DB') IS NOT NULL BEGIN ALTER DATABASE [FMSC_DB] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [FMSC_DB]; END;" | Out-Null
            if (((@(Sql 'master' "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.databases WHERE name=N'FMSC_DB';")) -join '').Trim() -ne '0') {
                throw 'Owned disposable database still exists after cleanup.'
            }
        }
        foreach ($path in $fixtureFiles) {
            if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
        }
        if ($ownedInstallation) {
            foreach ($path in @($installed,"$installed.staging","$installed.rollback",$live,$shortcut,$recoveryStorage)) {
                if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
            }
        }
        if ($ownedTheme -and (Test-Path -LiteralPath $themeFile)) { Remove-Item -LiteralPath $themeFile -Force }
        if ($ownedRoot) { Remove-Item -LiteralPath $root -Recurse -Force }
        $cleanupResult='PASS'
    }
    catch { $cleanupResult='FAIL'; $status='FAIL'; throw }
    finally { WriteEvidence }
}
