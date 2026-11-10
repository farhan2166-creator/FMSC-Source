# Shared install/update/repair resolver. Never touches the school database or credentials.
param(
    [ValidateSet('Library','Resolve','Install')][string]$Action='Library',
    [ValidateSet('Git','SqlCmd','LocalDb','VcRedist')][string]$Tool='Git',
    [string]$ApplicationDirectory=''
)
$ErrorActionPreference='Stop'
if([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT -and $PSVersionTable.PSVersion.Major -le 5){
    # A parent PowerShell 7 session must not change Windows PowerShell's modules.
    $env:PSModulePath=Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell/v1.0/Modules'
}
$script:FmscToolCatalog=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ToolCatalog.json') -Raw | ConvertFrom-Json

function Get-FmscToolSpec([string]$Name) {
    if($Name -notin @('Git','SqlCmd','LocalDb','VcRedist')){throw 'Unknown maintenance tool.'}
    return $script:FmscToolCatalog.$Name
}
function Assert-FmscToolPaths([string]$Root) {
    $item=Get-Item -LiteralPath $Root -Force
    while($null -ne $item){
        if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Linked maintenance paths are forbidden.'}
        $item=$item.Parent
    }
    foreach($item in Get-ChildItem -LiteralPath $Root -Recurse -Force){
        if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Linked tool files are forbidden.'}
    }
}
function Assert-FmscMicrosoftPackage([string]$Path) {
    $sig=Get-AuthenticodeSignature -LiteralPath $Path
    if($sig.Status -ne 'Valid' -or $null -eq $sig.SignerCertificate -or
        $sig.SignerCertificate.Subject -notmatch '(^|, *)O=Microsoft Corporation(,|$)'){
        throw 'The prerequisite is not a valid Microsoft-signed package.'
    }
}
function Assert-FmscTransportRuntime([string]$Root) {
    foreach($entry in @(@('cmd/git.exe','--version'),@('usr/bin/ssh.exe','-V'))){
        $start=New-Object Diagnostics.ProcessStartInfo
        $start.FileName=Join-Path $Root $entry[0]
        $start.Arguments=$entry[1]
        $start.UseShellExecute=$false;$start.CreateNoWindow=$true
        # OpenSSH writes its version to stderr; capture it without treating it
        # as a terminating NativeCommandError under Windows PowerShell 5.1.
        $start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
        $process=[Diagnostics.Process]::Start($start)
        try {
            $output=$process.StandardOutput.ReadToEndAsync();$errors=$process.StandardError.ReadToEndAsync()
            if(-not $process.WaitForExit(30000)){$process.Kill();throw 'Verified update transport startup timed out.'}
            $null=$output.GetAwaiter().GetResult();$null=$errors.GetAwaiter().GetResult()
            if($process.ExitCode -ne 0){throw "Verified update transport could not start: $($entry[0])"}
        } finally {$process.Dispose()}
    }
}
function Write-FmscToolManifest([string]$Root,[string]$Name) {
    Assert-FmscToolPaths $Root
    $spec=Get-FmscToolSpec $Name
    foreach($relative in $spec.required){
        $file=Join-Path $Root $relative
        if(-not(Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -eq 0){throw "Incomplete $Name bundle: $relative"}
    }
    $files=@(Get-ChildItem -LiteralPath $Root -Recurse -File -Force | Where-Object {$_.Name -ne 'FMSC.ToolIntegrity.json'} | ForEach-Object {
        @{path=$_.FullName.Substring($Root.TrimEnd('\','/').Length+1).Replace('\','/');sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
    })
    @{format='fmsc-tool-integrity/v1';tool=$Name;version=$spec.version;archiveSha256=$spec.sha256;files=$files} |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Root 'FMSC.ToolIntegrity.json') -Encoding UTF8
}
function Test-FmscTool([string]$Root,[string]$Name) {
    try {
        if(-not(Test-Path -LiteralPath $Root -PathType Container)){return $false}
        Assert-FmscToolPaths $Root
        $manifest=Join-Path $Root 'FMSC.ToolIntegrity.json'
        if(-not(Test-Path -LiteralPath $manifest) -or (Get-Item $manifest).Length -gt 4194304){return $false}
        $m=Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
        $spec=Get-FmscToolSpec $Name
        if($m.format -ne 'fmsc-tool-integrity/v1' -or $m.tool -ne $Name -or $m.version -ne $spec.version -or @($m.files).Count -eq 0){return $false}
        if($spec.sha256 -and $m.archiveSha256 -ne $spec.sha256){return $false}
        $seen=@{}
        foreach($file in $m.files){
            $relative=[string]$file.path
            if($relative -match '(^/|\|:|(^|/)\.\.?(/|$))' -or $relative.Length -eq 0 -or $seen.ContainsKey($relative) -or $file.sha256 -notmatch '^[a-fA-F0-9]{64}$'){return $false}
            $seen[$relative]=$true
            $path=Join-Path $Root $relative
            if(-not(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256){return $false}
        }
        foreach($required in $spec.required){if(-not $seen.ContainsKey($required)){return $false}}
        # Extra DLLs/executables are corruption too; do not load an unlisted file.
        $actual=@(Get-ChildItem -LiteralPath $Root -Recurse -File -Force | Where-Object {$_.Name -ne 'FMSC.ToolIntegrity.json'})
        if($actual.Count -ne $seen.Count){return $false}
        if($Name -in @('LocalDb','VcRedist')){
            $package=if($Name -eq 'LocalDb'){'SqlLocalDB.msi'}else{'vc_redist.x64.exe'}
            Assert-FmscMicrosoftPackage (Join-Path $Root $package)
        }
        return $true
    } catch {return $false}
}
function Test-FmscSafeZipEntryName([string]$EntryName) {
    if([string]::IsNullOrEmpty($EntryName)){return $false}
    return $EntryName -notmatch '(^[/\\]|:|(^|[/\\])\.\.([/\\]|$))'
}
function Invoke-FmscPinnedDownload([string]$Uri,[string]$OutFile,[int]$TimeoutSec=300) {
    # Retry transient transport/server faults only; callers still validate
    # pinned archive SHA-256 and Microsoft Authenticode signatures.
    for($attempt=1; $attempt -le 3; $attempt++) {
        Remove-Item -LiteralPath $OutFile -Force -ErrorAction SilentlyContinue
        try {
            Invoke-WebRequest -UseBasicParsing -Uri $Uri -OutFile $OutFile -MaximumRedirection 5 -TimeoutSec $TimeoutSec
            if(-not(Test-Path -LiteralPath $OutFile -PathType Leaf) -or
                (Get-Item -LiteralPath $OutFile).Length -le 0) {
                throw 'Pinned tooling download was empty.'
            }
            return
        } catch {
            $exception=$_.Exception
            $httpCode=0
            if($exception.Response -and $exception.Response.StatusCode) {
                $httpCode=[int]$exception.Response.StatusCode
            }
            $retryable=$httpCode -in @(429,500,502,503,504)
            if($httpCode -eq 0 -and $exception -is [Net.WebException]) {
                $retryable=$exception.Status -in @(
                    [Net.WebExceptionStatus]::Timeout,
                    [Net.WebExceptionStatus]::ConnectionClosed,
                    [Net.WebExceptionStatus]::ConnectFailure,
                    [Net.WebExceptionStatus]::ReceiveFailure,
                    [Net.WebExceptionStatus]::SendFailure
                )
            }
            if(-not $retryable -or $attempt -eq 3) { throw }
            Write-Warning ("Pinned tooling endpoint unavailable (attempt {0}/3); retrying without relaxing integrity checks." -f $attempt)
            Start-Sleep -Seconds (2 * $attempt)
        }
    }
}

function Receive-FmscTool([string]$Name,[string]$Destination) {
    $spec=Get-FmscToolSpec $Name
    $ProgressPreference='SilentlyContinue'
    [Net.ServicePointManager]::SecurityProtocol=[Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $parent=if($script:FmscToolTemporaryRoot){$script:FmscToolTemporaryRoot}else{[IO.Path]::GetTempPath()}
    $temporary=Join-Path $parent ('fmsc-tool-download-'+[Guid]::NewGuid().ToString('N'))
    $null=New-Item -ItemType Directory -Path $temporary
    try {
        $archiveName=switch($Name){LocalDb {'SqlLocalDB.msi'} VcRedist {'vc_redist.x64.exe'} default {'download'}}
        $archive=Join-Path $temporary $archiveName
        Invoke-FmscPinnedDownload -Uri $spec.url -OutFile $archive -TimeoutSec 300
        if($Name -in @('LocalDb','VcRedist')){
            Assert-FmscMicrosoftPackage $archive
            $null=New-Item -ItemType Directory -Path $Destination
            Copy-Item -LiteralPath $archive -Destination (Join-Path $Destination $archiveName)
        } else {
            if((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $spec.sha256){throw "Pinned $Name archive checksum mismatch."}
            $zip=$archive+'.zip';Move-Item -LiteralPath $archive -Destination $zip
            # Reject traversal before expanding a verified upstream ZIP.
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            $zipReader=[IO.Compression.ZipFile]::OpenRead($zip)
            try {foreach($entry in $zipReader.Entries){if(-not(Test-FmscSafeZipEntryName $entry.FullName)){throw 'Unsafe archive path.'}}} finally {$zipReader.Dispose()}
            $expanded=Join-Path $temporary 'expanded'
            Expand-Archive -LiteralPath $zip -DestinationPath $expanded
            if($Name -eq 'SqlCmd'){
                $exe=@(Get-ChildItem -LiteralPath $expanded -Filter sqlcmd.exe -File -Recurse)
                if($exe.Count -ne 1){throw 'Expected one verified sqlcmd executable.'}
                $null=New-Item -ItemType Directory -Path $Destination
                Copy-Item -LiteralPath $exe[0].FullName -Destination (Join-Path $Destination 'sqlcmd.exe')
            } else {
                Move-Item -LiteralPath $expanded -Destination $Destination
                Invoke-FmscPinnedDownload -Uri $spec.licenseUrl -OutFile (Join-Path $Destination 'COPYING-GIT.txt') -TimeoutSec 120
                if(-not(Select-String -LiteralPath (Join-Path $Destination 'COPYING-GIT.txt') -Pattern 'GNU GENERAL PUBLIC LICENSE' -Quiet)){throw 'Git license is missing.'}
                @("MinGit $($spec.version) x64; upstream archive unchanged.","Distribution: $($spec.url)","Archive SHA256: $($spec.sha256)",'Git GPLv2: COPYING-GIT.txt. OpenSSH and other components retain their upstream licenses.') |
                    Set-Content -LiteralPath (Join-Path $Destination 'THIRD_PARTY_NOTICES.txt') -Encoding UTF8
            }
        }
        if($Name -eq 'Git' -and [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT){
            Assert-FmscTransportRuntime $Destination
        }
        Write-FmscToolManifest $Destination $Name
        if(-not(Test-FmscTool $Destination $Name)){throw "Downloaded $Name did not validate."}
    } finally {Remove-Item -LiteralPath $temporary -Recurse -Force}
}
function Get-FmscMaintenanceRoot {
    return Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'FMSC_Updater/MaintenanceTools'
}
function Find-FmscMaintenanceTool([string]$Name) {
    $root=Get-FmscMaintenanceRoot
    if(-not(Test-Path -LiteralPath $root)){return $null}
    # Permanent machine tools are admin-owned and read-only to ordinary users.
    Assert-FmscRecoveryStorage $root
    $spec=Get-FmscToolSpec $Name
    foreach($candidate in @(Get-ChildItem -LiteralPath $root -Directory -Filter "$Name-$($spec.version)-*" | Sort-Object CreationTimeUtc -Descending)){
        if(Test-FmscTool $candidate.FullName $Name){return $candidate.FullName}
    }
    return $null
}

# Only after successful app and SQL verification: retain one integrity-verified
# current-version bundle per tool. Keep other catalog versions for recovery.
function Remove-FmscSupersededMaintenanceTools {
    $root=Get-FmscMaintenanceRoot
    if(-not(Test-Path -LiteralPath $root -PathType Container)){return}
    Assert-FmscRecoveryStorage $root
    $selected=@{}
    foreach($name in @('Git','SqlCmd','LocalDb','VcRedist')){
        $current=Find-FmscMaintenanceTool $name
        if(-not $current -or -not(Test-FmscTool $current $name)){
            throw "Current $name maintenance tool cannot be verified; pruning refused."
        }
        $selected[$name]=[IO.Path]::GetFullPath($current).TrimEnd('\')
    }
    foreach($name in @('Git','SqlCmd','LocalDb','VcRedist')){
        $spec=Get-FmscToolSpec $name
        $pattern='^'+[regex]::Escape($name+'-'+$spec.version+'-')+'[a-f0-9]{32}$'
        foreach($candidate in @(Get-ChildItem -LiteralPath $root -Directory -Force)){
            if($candidate.Name -cnotmatch $pattern -or
                $candidate.FullName -ieq $selected[$name]){continue}
            try {
                Assert-FmscToolPaths $candidate.FullName
                $expected=Join-Path $root $candidate.Name
                if([IO.Path]::GetFullPath($expected).TrimEnd('\') -ine
                    [IO.Path]::GetFullPath($candidate.FullName).TrimEnd('\')){
                    throw 'Tool cache path changed; deletion refused.'
                }
                Remove-Item -LiteralPath $candidate.FullName -Recurse -Force -ErrorAction Stop
            } catch {
                Write-Warning "Retained old $name tool cache $($candidate.Name): $($_.Exception.Message)"
            }
        }
    }
}

function Install-FmscMaintenanceToolInternal([string]$Name,[string]$Application) {
    $current=Find-FmscMaintenanceTool $Name
    if($current){return $current}
    $root=Get-FmscMaintenanceRoot
    Initialize-FmscRecoveryStorage (Split-Path $root -Parent)
    Initialize-FmscRecoveryStorage $root
    $script:FmscToolTemporaryRoot=$root
    $spec=Get-FmscToolSpec $Name
    $stage=Join-Path $root ('stage-'+[Guid]::NewGuid().ToString('N'))
    try {
        $source=Join-Path (Join-Path $Application $spec.folder) $Name
        # Git has its own tree; SQL tools have isolated integrity subdirectories.
        if($Name -eq 'Git'){$source=Join-Path $Application $spec.folder}
        if(Test-FmscTool $source $Name){
            $null=New-Item -ItemType Directory -Path $stage
            Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $stage -Recurse -Force
        } else {
            # Direct HTTPS bootstrap works even when Git itself is broken.
            Receive-FmscTool $Name $stage
        }
        if(-not(Test-FmscTool $stage $Name)){throw "Staged $Name is invalid."}
        $destination=Join-Path $root ("$Name-$($spec.version)-"+[Guid]::NewGuid().ToString('N'))
        # Normalize ownership before publishing; no unprivileged writer may
        # modify a DLL between integrity verification and elevated SQL use.
        if([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT){
            foreach($item in (@((Get-Item $stage))+@(Get-ChildItem $stage -Recurse -Force))){
                $acl=Get-Acl -LiteralPath $item.FullName
                $acl.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))
                Set-Acl -LiteralPath $item.FullName -AclObject $acl
            }
        }
        Move-Item -LiteralPath $stage -Destination $destination
        Assert-FmscRecoveryStorage $destination
        return $destination
    } finally {if(Test-Path -LiteralPath $stage){Remove-Item -LiteralPath $stage -Recurse -Force}}
}

function Install-FmscMaintenanceTool([string]$Name,[string]$Application) {
    $gate=New-Object Threading.Mutex($false,'Global\FMSC.MaintenanceTools.Install')
    $held=$false
    try {
        try {$held=$gate.WaitOne(60000)}catch [Threading.AbandonedMutexException]{$held=$true}
        if(-not $held){throw 'Another FMSC tool repair is running. Wait for it to finish.'}
        return Install-FmscMaintenanceToolInternal $Name $Application
    } finally {if($held){$gate.ReleaseMutex()};$gate.Dispose()}
}

if($Action -ne 'Library'){
    . (Join-Path $PSScriptRoot 'UpdateRecovery.ps1')
    $found=Find-FmscMaintenanceTool $Tool
    if(-not $found -and $Action -eq 'Resolve'){exit 20}
    if(-not $found){$found=Install-FmscMaintenanceTool $Tool $ApplicationDirectory}
    @{root=$found;file=(Join-Path $found (Get-FmscToolSpec $Tool).required[0])} | ConvertTo-Json -Compress
}
