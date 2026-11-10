# Local publishing is offline by default. CI opts into approved downloads.
param(
    [string]$ToolsDirectory=(Join-Path $PSScriptRoot '../../Tools'),
    [string]$GitDirectory='', [string]$SqlCmdPath='', [string]$LocalDbMsiPath='', [string]$VcRedistPath='',
    [switch]$DownloadMissing
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../Installer/MaintenanceTools.ps1')
$ToolsDirectory=[IO.Path]::GetFullPath($ToolsDirectory)
if(-not $GitDirectory){$GitDirectory=Join-Path $ToolsDirectory 'MinGit'}
if(-not $SqlCmdPath){$SqlCmdPath=Join-Path $ToolsDirectory 'sqlcmd.exe'}
if(-not $LocalDbMsiPath){$LocalDbMsiPath=Join-Path $ToolsDirectory 'SqlLocalDB.msi'}
if(-not $VcRedistPath){$VcRedistPath=Join-Path $ToolsDirectory 'vc_redist.x64.exe'}
foreach($name in @('Git','SqlCmd','LocalDb','VcRedist')){
    $path=switch($name){Git {$GitDirectory} SqlCmd {$SqlCmdPath} LocalDb {$LocalDbMsiPath} VcRedist {$VcRedistPath}}
    if($name -eq 'Git'){$bundle=$path}else{$bundle=Join-Path (Split-Path $path -Parent) $name}
    if(Test-FmscTool $bundle $name){
        if($name -ne 'Git'){
            $verified=Join-Path $bundle (Get-FmscToolSpec $name).required[0]
            if(-not(Test-Path -LiteralPath $path) -or (Get-FileHash $path -Algorithm SHA256).Hash -ne (Get-FileHash $verified -Algorithm SHA256).Hash){Copy-Item -LiteralPath $verified -Destination $path -Force}
        }
        continue
    }
    # A valid manifest that now fails checks means corruption: do not bless it
    # with new hashes. The owner replaces it, or CI downloads a verified copy.
    $manifest=Join-Path $bundle 'FMSC.ToolIntegrity.json'
    $existing=Test-Path -LiteralPath $path
    if($existing -and -not(Test-Path -LiteralPath $manifest)){
        try {
            if($name -in @('LocalDb','VcRedist')){Assert-FmscMicrosoftPackage $path}
            else {
                $exe=if($name -eq 'Git'){Join-Path $path 'cmd/git.exe'}else{$path}
                $reported=(& $exe --version 2>&1 | Out-String).Replace('.windows.','.')
                $expected=[regex]::Escape((Get-FmscToolSpec $name).version)
                if($LASTEXITCODE -ne 0 -or $reported -notmatch "\b$expected\b"){throw "$name in your Tools folder is not the approved version."}
                if($name -eq 'Git'){
                    Assert-FmscTransportRuntime $path
                }
            }
            if($name -ne 'Git'){
                $null=New-Item -ItemType Directory -Path $bundle -Force
                Copy-Item -LiteralPath $path -Destination (Join-Path $bundle (Get-FmscToolSpec $name).required[0]) -Force
            }
            # The owner's local Tools folder is an explicit build input. Record
            # it once; subsequent publishes verify without rewriting hashes.
            Write-FmscToolManifest $bundle $name
            if(Test-FmscTool $bundle $name){continue}
        } catch {
            if(-not $DownloadMissing){throw "$name could not be verified in $path. $($_.Exception.Message) Replace it or run PrepareMaintenanceTools.ps1 -DownloadMissing once."}
        }
    }
    if(-not $DownloadMissing){throw "$name is missing, changed or corrupt in $path. Replace it in your Tools folder, or run PrepareMaintenanceTools.ps1 -DownloadMissing once."}
    $temporary=Join-Path $ToolsDirectory ('prepare-'+[Guid]::NewGuid().ToString('N'))
    $null=New-Item -ItemType Directory -Path $ToolsDirectory -Force
    try {
        Receive-FmscTool $name $temporary
        if(Test-Path -LiteralPath $bundle){Remove-Item -LiteralPath $bundle -Recurse -Force}
        Move-Item -LiteralPath $temporary -Destination $bundle
        if($name -ne 'Git'){Copy-Item -LiteralPath (Join-Path $bundle (Get-FmscToolSpec $name).required[0]) -Destination $path -Force}
    } finally {if(Test-Path -LiteralPath $temporary){Remove-Item -LiteralPath $temporary -Recurse -Force}}
}
Write-Host 'Maintenance tools verified: local publishing reuses this folder; no per-publish download.'
