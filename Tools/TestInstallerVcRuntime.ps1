# Synthetic regression: no installer execution, network, Windows changes or database access.
param([string]$InstallerPath=(Join-Path $PSScriptRoot '../Installer/core.ps1'))
$ErrorActionPreference='Stop'
$source=Get-Content -LiteralPath $InstallerPath -Raw
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseInput($source,[ref]$tokens,[ref]$errors)
if($errors.Count){throw "Installer parser errors: $($errors -join '; ')"}
$functions=@($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -in @('Test-FmscVcRuntime','Invoke-FmscVcRuntimeInstallation')
},$true))
if($functions.Count -ne 2){throw 'Expected VC++ registry check and installation helper.'}
$vcCall=$source.IndexOf('Invoke-FmscVcRuntimeInstallation -Path (Join-Path $VcRedistRoot')
$sqlCall=$source.IndexOf('Invoke-LocalDbMsiInstallation -MsiPath $MsiPath')
$localDbGuard=$source.IndexOf('if (-not $SqlLocalDb) {', $vcCall)
if($vcCall -lt 0 -or $sqlCall -lt 0 -or $vcCall -ge $sqlCall -or
    $localDbGuard -lt 0 -or $vcCall -ge $localDbGuard){
    throw 'VC++ must be checked for both updates and fresh setups before the LocalDB MSI.'
}
foreach($fn in $functions){Invoke-Expression $fn.Extent.Text}
function Assert([bool]$Condition,[string]$Message){if(-not $Condition){throw $Message}}
$script:runtime=$null;$script:codes=@();$script:launches=@()
function Get-ItemProperty {
    param([string]$Path,[object]$ErrorAction)
    return $script:runtime
}
function Write-Log{param([string]$Message)}
function Write-Step{param([string]$Message)}
function Write-Success{param([string]$Message)}
function Assert-FmscMicrosoftPackage {
    param([string]$Path)
    Assert ($Path -eq 'C:\synthetic\vc_redist.x64.exe') 'Unexpected runtime executable path.'
}
function Start-Process {
    param([string]$FilePath,[object[]]$ArgumentList,[switch]$Wait,[switch]$PassThru)
    Assert ($FilePath -eq 'C:\synthetic\vc_redist.x64.exe') 'Unexpected process.'
    $script:launches+=,($ArgumentList -join ' ')
    if(-not $script:codes.Count){throw 'Unexpected installer attempt.'}
    $result=$script:codes[0]
    $script:codes=@($script:codes | Select-Object -Skip 1)
    if($result -eq 0){$script:runtime=[pscustomobject]@{Installed=1;Major=14;Minor=50}}
    return [pscustomobject]@{ExitCode=$result}
}
$script:runtime=[pscustomobject]@{Installed=1;Major=14;Minor=29}
Assert (-not(Test-FmscVcRuntime)) 'An outdated v14 runtime must not bypass SQL 2022 prerequisite.'
$script:runtime=[pscustomobject]@{Installed=1;Major=14;Minor=50}
Assert (Test-FmscVcRuntime) 'Supported VC++ runtime must be recognized.'
$script:runtime=[pscustomobject]@{Installed=0;Major=14;Minor=50}
Assert (-not(Test-FmscVcRuntime)) 'An uninstalled runtime key must be ignored.'
$script:runtime=[pscustomobject]@{Installed=1;Major=14;Minor=50}
$script:codes=@()
Invoke-FmscVcRuntimeInstallation -Path 'C:\synthetic\vc_redist.x64.exe' -LogDirectory $env:TEMP
Assert ($script:launches.Count -eq 0) 'Already installed runtime must be skipped.'
$script:runtime=$null;$script:codes=@(0)
Invoke-FmscVcRuntimeInstallation -Path 'C:\synthetic\vc_redist.x64.exe' -LogDirectory $env:TEMP
Assert ($script:launches.Count -eq 1) 'Missing runtime must run exactly one installer.'
Assert ($script:launches[0] -match '/install.*?/quiet.*?/norestart.*?/log') 'Install must be logged, quiet and non-restarting.'
foreach($code in @(3010,1641,1603)){
    $script:runtime=$null;$script:codes=@($code)
    $failure=$null
    try {
        Invoke-FmscVcRuntimeInstallation -Path 'C:\synthetic\vc_redist.x64.exe' -LogDirectory $env:TEMP
    }catch{$failure=$_.Exception.Message}
    Assert ([bool]$failure) "Runtime MSI exit code $code should block LocalDB."
    if($code -in @(3010,1641)){Assert ($failure -match 'restart') 'Missing restart guidance.'}
    else{Assert ($failure -match 'failed') 'Missing failure guidance.'}
}
Write-Output 'PASS: VC++ prerequisite registry, signed-package call, silent install, skip and restart/error guards.'
