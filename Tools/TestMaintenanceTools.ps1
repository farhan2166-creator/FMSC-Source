# Isolated synthetic files only. No executable, MSI, network or school SQL runs.
$ErrorActionPreference='Stop'
$project=Split-Path $PSScriptRoot -Parent
foreach($file in @('Installer/MaintenanceTools.ps1','Installer/core.ps1','Installer/UpdateRecovery.ps1','Tools/PrepareMaintenanceTools.ps1')){
    $tokens=$null;$errors=$null
    $null=[Management.Automation.Language.Parser]::ParseFile((Join-Path $project $file),[ref]$tokens,[ref]$errors)
    if($errors.Count){throw "PowerShell syntax errors in ${file}: $($errors -join '; ')"}
}
. (Join-Path $project 'Installer/MaintenanceTools.ps1')
$productionReceive=${function:Receive-FmscTool}
. (Join-Path $project 'Installer/UpdateRecovery.ps1')
$script:checks=0
function Check([bool]$Value,[string]$Reason){if(-not $Value){throw $Reason};$script:checks++}
# Cover the production ZIP-entry guard without network, tools or extraction.
foreach($unsafe in @('/absolute','\absolute','C:/evil.exe','../escape.exe','..\escape.exe',
    'bin/../escape.exe','bin\..\escape.exe','bin/..\escape.exe','bin\../escape.exe')) {
    Check (-not(Test-FmscSafeZipEntryName $unsafe)) "Unsafe ZIP entry accepted: $unsafe"
}
foreach($safe in @('cmd/git.exe','nested/tool.dll','mingit\cmd\git.exe')) {
    Check (Test-FmscSafeZipEntryName $safe) "Safe ZIP entry rejected: $safe"
}
# Signature fixture substitutes only the OS trust provider. Production uses
# Authenticode; real signed MSI verification remains a Windows package gate.
function Get-AuthenticodeSignature([string]$LiteralPath){
    if([IO.File]::ReadAllText($LiteralPath) -notin @('synthetic Microsoft MSI','synthetic Microsoft EXE')){
        return [pscustomobject]@{Status='NotSigned';SignerCertificate=$null}
    }
    return [pscustomobject]@{Status='Valid';SignerCertificate=[pscustomobject]@{Subject='CN=Test,O=Microsoft Corporation'}}
}
if([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT){
    # Portable execution checks file lifecycle, not Windows ACL acceptance.
    function Initialize-FmscRecoveryStorage([string]$Root){$null=New-Item -ItemType Directory -Path $Root -Force}
    function Assert-FmscRecoveryStorage([string]$Root){Assert-FmscToolPaths $Root}
}
$work=Join-Path ([IO.Path]::GetTempPath()) ('fmsc-tool-check-'+[Guid]::NewGuid().ToString('N'))
$script:toolRoot=Join-Path $work 'retained/tools'
function Get-FmscMaintenanceRoot{return $script:toolRoot}
$script:downloads=0
function Seed([string]$Name,[string]$Directory){
    $null=New-Item -ItemType Directory -Path $Directory -Force
    foreach($file in (Get-FmscToolSpec $Name).required){
        $path=Join-Path $Directory $file
        $null=New-Item -ItemType Directory -Path (Split-Path $path -Parent) -Force
        $value=if($Name -eq 'LocalDb'){'synthetic Microsoft MSI'}elseif($Name -eq 'VcRedist'){'synthetic Microsoft EXE'}else{'synthetic verified tool '+$file}
        [IO.File]::WriteAllText($path,$value)
    }
    if($Name -eq 'Git'){[IO.File]::WriteAllText((Join-Path $Directory 'support.dll'),'synthetic supporting DLL')}
    Write-FmscToolManifest $Directory $Name
}
# Simulate a verified HTTPS bootstrap without running or downloading a tool.
function Receive-FmscTool([string]$Name,[string]$Destination){$script:downloads++;Seed $Name $Destination}
try {
    $app=Join-Path $work 'application'
    foreach($name in @('Git','SqlCmd','LocalDb','VcRedist')){
        $spec=Get-FmscToolSpec $name
        $source=Join-Path $app $spec.folder
        if($name -ne 'Git'){$source=Join-Path $source $name}
        Seed $name $source
        Check (Test-FmscTool $source $name) "$name fixture integrity"
        $retained=Install-FmscMaintenanceTool $name $app
        Check ($retained.StartsWith($script:toolRoot)) "$name must live outside the application"
        Check (Test-FmscTool $retained $name) "$name retained integrity"
    }
    Check ($script:downloads -eq 0) 'First installation must copy good bundles without downloading'
    Remove-Item -LiteralPath $app -Recurse -Force
    foreach($name in @('Git','SqlCmd','LocalDb','VcRedist')){
        Check ([bool](Find-FmscMaintenanceTool $name)) "$name must survive deletion of the source application/setup"
        $same=Find-FmscMaintenanceTool $name
        Check ((Install-FmscMaintenanceTool $name $app) -eq $same) 'Unchanged tools must be reused'
    }
    Check ($script:downloads -eq 0) 'Healthy update/repair must not download tools'
    # Three same-version GUID copies can be created by repair/reinstall. After
    # success, prune only redundant ones, never the selected verified bundles
    # or other catalog versions.
    $duplicates=@()
    foreach($name in @('Git','LocalDb','SqlCmd')){
        $selected=Find-FmscMaintenanceTool $name
        Check ([bool]$selected) "Missing selected $name tool before cleanup."
        $spec=Get-FmscToolSpec $name
        $copy=Join-Path $script:toolRoot ($name+'-'+$spec.version+'-'+[Guid]::NewGuid().ToString('N'))
        Copy-Item -LiteralPath $selected -Destination $copy -Recurse -Force
        if([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT){
            foreach($entry in (@((Get-Item -LiteralPath $copy)) + @(Get-ChildItem -LiteralPath $copy -Recurse -Force))){
                $acl=Get-Acl -LiteralPath $entry.FullName
                $acl.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))
                Set-Acl -LiteralPath $entry.FullName -AclObject $acl
            }
        }
        Check (Test-FmscTool $copy $name) "Old $name GUID copy should validate."
        $duplicates+=,$copy
    }
    $unrelatedVersion=Join-Path $script:toolRoot ('Git-prior-version-'+[Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $unrelatedVersion | Out-Null
    if([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT){
        $acl=Get-Acl -LiteralPath $unrelatedVersion
        $acl.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))
        Set-Acl -LiteralPath $unrelatedVersion -AclObject $acl
    }
    $selectedBefore=@{}
    foreach($name in @('Git','LocalDb','SqlCmd')) {
        $selectedBefore[$name]=Find-FmscMaintenanceTool $name
    }
    Remove-FmscSupersededMaintenanceTools
    foreach($name in @('Git','LocalDb','SqlCmd')){
        $spec=Get-FmscToolSpec $name
        $remaining=@(Get-ChildItem $script:toolRoot -Directory -Filter "$name-$($spec.version)-*")
        Check ($remaining.Count -eq 1 -and $remaining[0].FullName -eq $selectedBefore[$name] -and
            (Test-FmscTool $remaining[0].FullName $name)) "Current $name cache cleanup removed selected bundle or kept redundant copies."
    }
    Check (Test-Path $unrelatedVersion) 'Pruning unexpectedly removed a different tool version.'
    Remove-FmscSupersededMaintenanceTools
    Check (@(Get-ChildItem $script:toolRoot -Directory -Filter 'Git-*').Count -eq 2) 'Tool pruning is not idempotent or removed prior versions.'
    $old=Find-FmscMaintenanceTool 'Git'
    [IO.File]::WriteAllText((Join-Path $old 'support.dll'),'corrupt DLL')
    Check (-not(Test-FmscTool $old 'Git')) 'Corrupt supporting DLL must be rejected'
    $fixed=Install-FmscMaintenanceTool 'Git' $app
    Check ($fixed -ne $old -and (Test-FmscTool $fixed 'Git')) 'Corruption must create a verified replacement without editing the old version'
    Check ($script:downloads -eq 1) 'Broken Git must bootstrap independently with one HTTPS repair'
    $oldVersion=$script:FmscToolCatalog.Git.version
    $script:FmscToolCatalog.Git.version='next-test-version'
    $next=Install-FmscMaintenanceTool 'Git' $app
    Check ($next -ne $fixed -and (Test-FmscTool $next 'Git')) 'A deliberately changed tool version gets its own bundle'
    $script:FmscToolCatalog.Git.version=$oldVersion
    Check ((Find-FmscMaintenanceTool 'Git') -eq $fixed) 'Rollback can still resolve the previously verified tool version'
    [IO.File]::WriteAllText((Join-Path $fixed 'extra.dll'),'unlisted library')
    Check (-not(Test-FmscTool $fixed 'Git')) 'Unlisted DLLs cannot enter a valid bundle'
    Remove-Item -LiteralPath (Join-Path $fixed 'extra.dll')
    $manifest=Join-Path $fixed 'FMSC.ToolIntegrity.json'
    $saved=[IO.File]::ReadAllText($manifest)
    $m=$saved|ConvertFrom-Json;$m.files[0].path='../outside.exe'
    $m|ConvertTo-Json -Depth 6|Set-Content $manifest
    Check (-not(Test-FmscTool $fixed 'Git')) 'Traversal inside an integrity manifest must fail'
    [IO.File]::WriteAllText($manifest,$saved)
    Check (Test-FmscTool $fixed 'Git') 'Restored manifest remains valid'
    $msi=Find-FmscMaintenanceTool 'LocalDb'
    [IO.File]::WriteAllText((Join-Path $msi 'SqlLocalDB.msi'),'not signed')
    Write-FmscToolManifest $msi 'LocalDb'
    Check (-not(Test-FmscTool $msi 'LocalDb')) 'A matching hash cannot bless an unsigned MSI'
    $refusedPrune=$false
    try { Remove-FmscSupersededMaintenanceTools } catch { $refusedPrune=$_.Exception.Message -like '*cannot be verified*' }
    Check $refusedPrune 'Pruning must refuse to run when a current maintenance tool is corrupt.'
    $local=Join-Path $work 'owner-Tools'
    Seed 'Git' (Join-Path $local 'MinGit')
    Seed 'SqlCmd' (Join-Path $local 'SqlCmd')
    Seed 'LocalDb' (Join-Path $local 'LocalDb')
    Seed 'VcRedist' (Join-Path $local 'VcRedist')
    Copy-Item (Join-Path $local 'SqlCmd/sqlcmd.exe') (Join-Path $local 'sqlcmd.exe')
    Copy-Item (Join-Path $local 'LocalDb/SqlLocalDB.msi') (Join-Path $local 'SqlLocalDB.msi')
    Copy-Item (Join-Path $local 'VcRedist/vc_redist.x64.exe') (Join-Path $local 'vc_redist.x64.exe')
    function Invoke-WebRequest {throw 'A cached local publish attempted a download.'}
    $before=(Get-FileHash (Join-Path $local 'MinGit/FMSC.ToolIntegrity.json')).Hash
    & (Join-Path $project 'Tools/PrepareMaintenanceTools.ps1') -ToolsDirectory $local
    Check ((Get-FileHash (Join-Path $local 'MinGit/FMSC.ToolIntegrity.json')).Hash -eq $before) 'Local publish reuses verified owner tools without rewriting hashes'
    Remove-Item (Join-Path $local 'sqlcmd.exe')
    & (Join-Path $project 'Tools/PrepareMaintenanceTools.ps1') -ToolsDirectory $local
    Check (Test-Path (Join-Path $local 'sqlcmd.exe')) 'Local preparation restores a flat build input from its verified bundle offline'
    # Exercise the real packaging target after AssignTargetPaths has already
    # run, as on a first publish after Build. Substitute only tool preparation
    # with an inert copy; no synthetic tool is executed or published.
    $publishTools=Join-Path $work 'first-publish-tools'
    $prepareFixture=Join-Path $work 'prepare-fixture.ps1'
    'param([string]$Source,[string]$Destination) Copy-Item -LiteralPath $Source -Destination $Destination -Recurse' |
        Set-Content -LiteralPath $prepareFixture
    [xml]$sourceProject=Get-Content -LiteralPath (Join-Path $project 'FMSC_SMS.csproj') -Raw
    [xml]$harness='<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>'
    foreach($pair in @(@('BundledGitDirectory',(Join-Path $publishTools 'MinGit')),
        @('BundledSqlCmdPath',(Join-Path $publishTools 'sqlcmd.exe')),
        @('LocalDbMsiPath',(Join-Path $publishTools 'SqlLocalDB.msi')),
        @('VcRedistPath',(Join-Path $publishTools 'vc_redist.x64.exe')))){
        $property=$harness.CreateElement($pair[0]);$property.InnerText=$pair[1]
        $null=$harness.Project.PropertyGroup.AppendChild($property)
    }
    $target=$harness.ImportNode($sourceProject.SelectSingleNode("/Project/Target[@Name='PrepareMaintenanceInputs']"),$true)
    $engine=(Get-Process -Id $PID).Path
    $target.Exec.SetAttribute('Command',('"'+$engine+'" -NoProfile -File "'+$prepareFixture+'" -Source "'+$local+'" -Destination "'+$publishTools+'"'))
    $target.Exec.RemoveAttribute('EnvironmentVariables')
    $null=$harness.Project.AppendChild($target)
    $harnessPath=Join-Path $work 'packaging.csproj';$harness.Save($harnessPath)
    $result=& dotnet msbuild $harnessPath -nologo '-t:AssignTargetPaths;PrepareMaintenanceInputs' -getItem:ContentWithTargetPath 2>&1
    if($LASTEXITCODE -ne 0){throw "Maintenance packaging target failed: $($result -join '`n')"}
    $items=($result -join "`n" | ConvertFrom-Json).Items.ContentWithTargetPath
    $paths=@($items | ForEach-Object {$_.TargetPath.Replace('\','/')})
    foreach($expected in @('UpdateTransport/cmd/git.exe','UpdateTransport/support.dll','UpdateTransport/FMSC.ToolIntegrity.json',
        'UpdateSupport/Tools/SqlCmd/sqlcmd.exe','UpdateSupport/Tools/SqlCmd/FMSC.ToolIntegrity.json',
        'UpdateSupport/Tools/LocalDb/SqlLocalDB.msi','UpdateSupport/Tools/LocalDb/FMSC.ToolIntegrity.json',
        'UpdateSupport/Tools/VcRedist/vc_redist.x64.exe','UpdateSupport/Tools/VcRedist/FMSC.ToolIntegrity.json')){
        Check ($expected -in $paths) "First publish must include freshly prepared $expected (found: $($paths -join ', '))"
    }
    [IO.File]::WriteAllText((Join-Path $local 'SqlCmd/sqlcmd.exe'),'corrupt')
    $refused=$false
    try {& (Join-Path $project 'Tools/PrepareMaintenanceTools.ps1') -ToolsDirectory $local}catch{$refused=$_.Exception.Message -like '*missing, changed or corrupt*'}
    Check $refused 'Local publish refuses corrupted tools instead of silently downloading or blessing new hashes'
    # Exercise production download verification with invalid fixture bytes.
    # Only HTTPS I/O is substituted; extraction/execution must never be reached.
    function Invoke-WebRequest {
        param([switch]$UseBasicParsing,[string]$Uri,[string]$OutFile,[int]$MaximumRedirection,[int]$TimeoutSec)
        [IO.File]::WriteAllText($OutFile,'untrusted download fixture')
    }
    foreach($name in @('Git','SqlCmd')){
        $refused=$false
        try {& $productionReceive $name (Join-Path $work ('bad-'+$name))}catch{$refused=$_.Exception.Message -like '*checksum mismatch*'}
        Check $refused "Downloaded $name with an incorrect archive hash must be refused"
    }
    Write-Host "PASS: $script:checks maintenance lifecycle checks; no SQL, tool executables or external downloads."
} finally {if(Test-Path $work){Remove-Item -LiteralPath $work -Recurse -Force}}
