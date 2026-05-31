# Dispatches a GitHub Actions workflow on a target repository as the
# aspire-repo-bot GitHub App. With -NoWait, dispatches and returns
# immediately. Without -NoWait, polls the resulting run until it completes
# and exits 0 only if conclusion=success.
#
# Auth (mint a GitHub App installation token) is delegated to
# Get-AspireBotInstallationToken.ps1 so the flow can be reused by other
# release pipeline scripts.
#
# workflow_dispatch returns 204 with no run id, so wait-mode polls
# /repos/.../actions/runs filtered by workflow + branch + created>=dispatch
# time to find the run we just queued (the documented workaround):
#   https://docs.github.com/en/rest/actions/workflow-runs#list-workflow-runs-for-a-workflow

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$AppId,
    [Parameter(Mandatory = $true)][string]$PrivateKeyPem,
    [Parameter(Mandatory = $true)][string]$Owner,
    [Parameter(Mandatory = $true)][string]$Repo,
    [Parameter(Mandatory = $true)][string]$WorkflowFile,
    [Parameter(Mandatory = $true)][string]$Ref,
    [Parameter(Mandatory = $true)][hashtable]$Inputs,
    [Parameter()][int]$PollIntervalSeconds = 30,
    [Parameter()][int]$PollTimeoutMinutes = 60,
    # Dispatch and exit immediately without resolving or polling the run. Use
    # when the dispatching pipeline should treat the GH workflow as
    # informational signal rather than a gate.
    [Parameter()][switch]$NoWait
)

$ErrorActionPreference = 'Stop'

function Invoke-GitHubApi {
    param(
        [string]$Method,
        [string]$Uri,
        [string]$Token,
        [object]$Body
    )

    $headers = @{
        Authorization          = "Bearer $Token"
        Accept                 = 'application/vnd.github+json'
        'X-GitHub-Api-Version' = '2022-11-28'
        'User-Agent'           = 'aspire-release-pipeline'
    }

    $params = @{
        Method  = $Method
        Uri     = $Uri
        Headers = $headers
    }

    if ($null -ne $Body) {
        $params['Body'] = ($Body | ConvertTo-Json -Depth 8 -Compress)
        $params['ContentType'] = 'application/json'
    }

    return Invoke-RestMethod @params
}

Write-Host "=== Dispatch Release GitHub Tasks ==="
Write-Host "Target: $Owner/$Repo workflow=$WorkflowFile ref=$Ref"
Write-Host "Inputs:"
foreach ($key in $Inputs.Keys) {
    Write-Host "  $key = $($Inputs[$key])"
}

# Mint an installation access token via the shared helper (handles JWT mint,
# installation id lookup, and token exchange).
$tokenScript = Join-Path $PSScriptRoot 'Get-AspireBotInstallationToken.ps1'
$installationToken = & $tokenScript -AppId $AppId -PrivateKeyPem $PrivateKeyPem -Owner $Owner -Repo $Repo
if ([string]::IsNullOrWhiteSpace($installationToken)) {
    Write-Error "Failed to acquire installation access token from Get-AspireBotInstallationToken.ps1"
    exit 1
}

# Record the time *before* dispatching so we can find the resulting run reliably.
# GitHub's workflow_dispatch endpoint returns 204 with no body — there is no run id
# in the response. The standard workaround is to filter actions/runs by event,
# workflow, branch, and a created>=<dispatch time> timestamp.
$dispatchedAt = [DateTimeOffset]::UtcNow

# Dispatch the workflow.
Write-Host "Dispatching workflow $WorkflowFile on ref=$Ref..."
$dispatchBody = @{
    ref    = $Ref
    inputs = $Inputs
}
Invoke-GitHubApi -Method POST `
    -Uri "https://api.github.com/repos/$Owner/$Repo/actions/workflows/$WorkflowFile/dispatches" `
    -Token $installationToken `
    -Body $dispatchBody | Out-Null
Write-Host "✓ Workflow dispatch accepted."

if ($NoWait) {
    # Fire-and-forget: don't try to resolve a run id or poll. Surface a link to the
    # workflow runs page so operators can find the dispatched run manually.
    $runsListUrl = "https://github.com/$Owner/$Repo/actions/workflows/$WorkflowFile"
    Write-Host "##[section]Dispatched (no wait). See recent runs: $runsListUrl"
    Write-Host "##vso[task.setvariable variable=DispatchedRunsUrl]$runsListUrl"
    exit 0
}

# Resolve the run id. The dispatched run is not always queryable instantly,
# so retry for up to 2 minutes. Filter by created>=dispatchedAt-30s to allow for
# clock skew between this runner and GitHub.
$createdFilter = $dispatchedAt.AddSeconds(-30).ToString('yyyy-MM-ddTHH:mm:ssZ')
$runId = $null
$runHtmlUrl = $null
$resolveDeadline = [DateTime]::UtcNow.AddMinutes(2)

while ([DateTime]::UtcNow -lt $resolveDeadline -and -not $runId) {
    Start-Sleep -Seconds 5
    $runsUri = "https://api.github.com/repos/$Owner/$Repo/actions/workflows/$WorkflowFile/runs?event=workflow_dispatch&branch=$([Uri]::EscapeDataString($Ref))&created=%3E%3D$createdFilter&per_page=10"
    try {
        $runs = Invoke-GitHubApi -Method GET -Uri $runsUri -Token $installationToken
    }
    catch {
        Write-Host "  (transient) Could not list runs yet: $($_.Exception.Message)"
        continue
    }

    if ($runs.workflow_runs -and $runs.workflow_runs.Count -gt 0) {
        # List endpoint returns newest first. Picking the newest is correct
        # when this branch+workflow isn't being dispatched concurrently. Two
        # simultaneous dispatches against the same workflow+ref can't be
        # disambiguated (dispatch returns 204 with no id, list doesn't echo
        # input values) and would mis-attribute one of the two pollers — warn
        # so the operator sees it.
        $candidates = $runs.workflow_runs | Sort-Object -Property created_at -Descending
        if ($candidates.Count -gt 1) {
            Write-Host "##vso[task.logissue type=warning]Multiple candidate runs ($($candidates.Count)) matched the dispatch filter (workflow=$WorkflowFile branch=$Ref created>=$createdFilter). Selecting newest; concurrent dispatch may have mis-attributed this poller."
        }
        $candidate = $candidates | Select-Object -First 1
        $runId = $candidate.id
        $runHtmlUrl = $candidate.html_url
        Write-Host "✓ Resolved dispatched run: $runHtmlUrl (id=$runId)"
        break
    }

    Write-Host "  Waiting for dispatched run to appear..."
}

if (-not $runId) {
    Write-Error "Could not resolve the dispatched workflow run within 2 minutes. Check the workflow run history manually."
    exit 1
}

# Surface the run URL in the AzDO job summary regardless of outcome.
Write-Host "##vso[task.setvariable variable=DispatchedRunUrl]$runHtmlUrl"
Write-Host "##[section]Dispatched run: $runHtmlUrl"

# Poll the run until it reaches a terminal state.
$pollDeadline = [DateTime]::UtcNow.AddMinutes($PollTimeoutMinutes)
$status = $null
$conclusion = $null

while ([DateTime]::UtcNow -lt $pollDeadline) {
    Start-Sleep -Seconds $PollIntervalSeconds
    try {
        $run = Invoke-GitHubApi -Method GET -Uri "https://api.github.com/repos/$Owner/$Repo/actions/runs/$runId" -Token $installationToken
    }
    catch {
        Write-Host "  (transient) Poll failed: $($_.Exception.Message). Retrying."
        continue
    }

    $status = $run.status
    $conclusion = $run.conclusion
    Write-Host "  status=$status conclusion=$conclusion"

    if ($status -eq 'completed') {
        break
    }
}

if ($status -ne 'completed') {
    Write-Error "Dispatched workflow did not complete within $PollTimeoutMinutes minutes. Last status: $status. See $runHtmlUrl"
    exit 1
}

if ($conclusion -ne 'success') {
    Write-Error "Dispatched workflow finished with conclusion '$conclusion'. See $runHtmlUrl"
    exit 1
}

Write-Host "✓ Dispatched workflow completed successfully: $runHtmlUrl"
exit 0
