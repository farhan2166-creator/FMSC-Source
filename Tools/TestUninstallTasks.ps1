# Execute the actual task-removal helper against an inert Scheduler facade.
# No Windows task, process, registry or application file is modified.
$ErrorActionPreference='Stop'
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '../Installer/Uninstall-FMSC.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Uninstaller does not parse.'}
$definition=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Remove-FmscCloudBackupTasks'},$true)
. ([scriptblock]::Create($definition.Extent.Text))
$AppDirectory=Join-Path ([IO.Path]::GetTempPath()) 'synthetic-FMSC-app'
$script:deleted=[Collections.Generic.List[string]]::new()
$script:tasks=@()
$folder=[pscustomobject]@{}
$folder | Add-Member ScriptMethod GetTasks {param($flags) return $script:tasks}
$folder | Add-Member ScriptMethod DeleteTask {param($name,$flags) $script:deleted.Add($name)}
$scheduler=[pscustomobject]@{}
$scheduler | Add-Member ScriptMethod Connect {}
$scheduler | Add-Member ScriptMethod GetFolder {param($path) return $folder}
function New-Object {param([string]$ComObject)
    if($ComObject -ne 'Schedule.Service'){throw 'Unexpected COM request.'}
    return $scheduler
}
function TaskFixture([string]$name,[string]$path,[string]$arguments){
    return [pscustomobject]@{Name=$name;Definition=[pscustomobject]@{
        Actions=@([pscustomobject]@{Type=0;Path=$path;Arguments=$arguments})}}
}
$owned='FMSC Cloud Backup S-1-5-21-1-2-3-1001'
$script:tasks=@(
    (TaskFixture $owned (Join-Path $AppDirectory 'FMSC_SMS.exe') '--cloud-backup-scheduled'),
    (TaskFixture 'Other school backup' (Join-Path $AppDirectory 'FMSC_SMS.exe') '--cloud-backup-scheduled'),
    (TaskFixture 'FMSC Cloud Backup unrelated' (Join-Path $AppDirectory 'FMSC_SMS.exe') '--cloud-backup-scheduled')
)
Remove-FmscCloudBackupTasks
if($script:deleted.Count -ne 1 -or $script:deleted[0] -ne $owned){throw 'Owned task cleanup removed an unrelated task or missed its own task.'}
$script:tasks=@();Remove-FmscCloudBackupTasks
foreach($case in @(
    @((Join-Path $AppDirectory 'Other.exe'),'--cloud-backup-scheduled'),
    @((Join-Path $AppDirectory 'FMSC_SMS.exe'),'--other-operation')
)){
    $script:tasks=@((TaskFixture $owned $case[0] $case[1]))
    $before=$script:deleted.Count;$refused=$false
    try{Remove-FmscCloudBackupTasks}catch{$refused=$_.Exception.Message -like '*unexpected action*'}
    if(-not $refused -or $script:deleted.Count -ne $before){throw 'Unexpected task action was not preserved for review.'}
}
Write-Output 'PASS: uninstall removes only exact FMSC cloud backup tasks, handles missing tasks and refuses unexpected actions.'
