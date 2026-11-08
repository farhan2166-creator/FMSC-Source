# Emit one GitHub REST commit-status record per pipeline object, in API order.
# Invoke-RestMethod can return a JSON array as a single pipeline object;
# explicitly enumerate it before selecting the newest status for a context.
function Get-FmscGitHubCommitStatuses {
    param(
        [Parameter(Mandatory)][string]$Uri,
        [Parameter(Mandatory)][hashtable]$Headers
    )
    $response=Invoke-RestMethod -Uri $Uri -Headers $Headers -Method Get
    foreach ($record in $response) {
        Write-Output $record
    }
}
