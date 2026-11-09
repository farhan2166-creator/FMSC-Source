# Offline authorization/trigger contracts. Never provide a token or reach Git.
$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot 'PublishPrivateUpdate.ps1'
$tokens = $null; $errors = $null
[void][Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Publication orchestrator does not parse.' }
$workflowLines = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../.github/workflows/fmsc-windows-download.yml')
$blocks = 0
for ($line = 0; $line -lt $workflowLines.Count; $line++) {
    if ($workflowLines[$line] -notmatch '^        run: \|\s*$') { continue }
    $scriptLines = @()
    for ($next = $line + 1; $next -lt $workflowLines.Count; $next++) {
        if ($workflowLines[$next] -match '^          (.*)$') { $scriptLines += $Matches[1] }
        elseif ([string]::IsNullOrWhiteSpace($workflowLines[$next])) { $scriptLines += '' }
        else { break }
    }
    $tokens = $null; $errors = $null
    [void][Management.Automation.Language.Parser]::ParseInput(($scriptLines -join "`n"), [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw "Workflow PowerShell block at line $($line+1) does not parse: $($errors[0].Message)" }
    $blocks++
}
if ($blocks -lt 5) { throw 'Expected publication workflow PowerShell contracts are missing.' }
$keys = @('GITHUB_ACTIONS','GITHUB_EVENT_NAME','GITHUB_REF','GITHUB_REPOSITORY','GITHUB_REPOSITORY_ID','GITHUB_REPOSITORY_OWNER',
    'GITHUB_REPOSITORY_OWNER_ID','GITHUB_ACTOR_ID','FMSC_UPDATES_PUBLISH_TOKEN')
$saved = @{}
foreach ($key in $keys) { $saved[$key] = [Environment]::GetEnvironmentVariable($key) }
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('fmsc-publication-guard-' + [Guid]::NewGuid().ToString('N'))
try {
    $env:FMSC_UPDATES_PUBLISH_TOKEN = $null
    foreach ($scenario in @('local','push','branch','fork','missing-token')) {
        $env:GITHUB_ACTIONS = 'true'; $env:GITHUB_EVENT_NAME = 'workflow_dispatch'
        $env:GITHUB_REF = 'refs/heads/main'; $env:GITHUB_REPOSITORY = 'future-renamed-owner/FMSC-Source'
        $env:GITHUB_REPOSITORY_ID = '1412760928'; $env:GITHUB_REPOSITORY_OWNER = 'future-renamed-owner'
        $env:GITHUB_REPOSITORY_OWNER_ID = '316891609'; $env:GITHUB_ACTOR_ID = '316891609'
        switch ($scenario) {
            local { $env:GITHUB_ACTIONS = 'false' }
            push { $env:GITHUB_EVENT_NAME = 'push' }
            branch { $env:GITHUB_REF = 'refs/heads/feature/test' }
            fork { $env:GITHUB_REPOSITORY_ID = '1' }
        }
        $refused = $false
        try { & $scriptPath -ApplicationDirectory 'unused' -WorkDirectory $scratch }
        catch {
            $expected = switch ($scenario) {
                'missing-token' { 'FMSC_UPDATES_PUBLISH_TOKEN' }
                'local' { 'Untrusted GitHub' }
                'fork' { 'Untrusted GitHub' }
                default { 'owner-triggered' }
            }
            if (-not $_.Exception.Message.Contains($expected)) { throw }
            $refused = $true
        }
        if (-not $refused -or (Test-Path -LiteralPath $scratch)) { throw "Unsafe publication trigger accepted: $scenario" }
    }
    Write-Output 'PASS: local, push, non-main, fork and missing-credential publication refused before file/network operations.'
}
finally { foreach ($key in $saved.Keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key]) } }
