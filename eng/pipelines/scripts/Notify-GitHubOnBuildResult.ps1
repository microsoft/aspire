# Files or updates a GitHub issue on microsoft/aspire when the internal AzDO
# build fails on a publishing branch, and closes the issue when the next build
# of the same branch goes green.
#
# Invoked from the notify_failure and notify_success stages of azure-pipelines.yml.
# Both the failure path (`-Mode Failure`) and the success path (`-Mode Success`)
# live in this single script so the search-and-dedupe logic stays in one place.
#
# Auth: mints an aspire-repo-bot GitHub App installation access token via the
# shared Get-AspireBotInstallationToken.ps1 helper, exports it as GH_TOKEN for
# the gh CLI, and also marks it as a secret AzDO variable so any incidental
# log echo gets redacted.
#
# Dedupe strategy:
#   - Issues are identified by a hidden HTML-comment marker in the body:
#       <!-- aspire-internal-build-broken:<branch> -->
#   - One open issue per branch at a time.
#   - We use GET /repos/.../issues?labels=ci-broken&state=open (strongly
#     consistent) and filter locally on the marker. The /search/issues
#     endpoint is intentionally avoided because its 1-2 min eventual
#     consistency causes near-simultaneous failed builds to each see
#     "0 hits" and each file a duplicate.
#   - On create, we re-list to detect the rare race where two builds raced
#     past the consistency window simultaneously, and close ours as a
#     duplicate of the older one.
#
# Failures table:
#   - The issue body contains a managed markdown table inside a fenced
#     region delimited by <!-- ci-broken-failures:begin --> /
#     <!-- ci-broken-failures:end -->. Each subsequent failure on the same
#     branch appends a row (build, commit, failed stages, timestamp).
#     A follow-up comment is also posted so @-mentions still fire — body
#     edits don't generate notifications.
#   - Visible rows are capped (FailuresTableMaxRows); older rows are
#     summarized as "_N earlier failures omitted_" and remain accessible
#     via the per-failure comments.
#   - Only content between the markers is rewritten; any human-added
#     prose elsewhere in the body is preserved across updates.
#
# Safety: this script ALWAYS exits 0. A flaky notification path must not
# turn an otherwise-correct build red. All API errors are logged via
# Write-Warning and swallowed.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('Failure', 'Success')][string]$Mode,
    # Optional at binding time so dry-run can run without aspire-repo-bot
    # credentials. Live mode enforces presence in the main body — Mandatory
    # would run before the dry-run gate.
    [Parameter()][string]$AppId,
    [Parameter()][string]$PrivateKeyPem,
    [Parameter()][string]$Owner = 'microsoft',
    [Parameter()][string]$Repo = 'aspire',
    [Parameter(Mandatory = $true)][string]$Branch,
    [Parameter(Mandatory = $true)][string]$BuildId,
    [Parameter(Mandatory = $true)][string]$BuildNumber,
    [Parameter(Mandatory = $true)][string]$BuildUrl,
    [Parameter(Mandatory = $true)][string]$CommitSha,
    [Parameter()][string]$FailedStages = '',
    [Parameter()][switch]$DryRun
)

$ErrorActionPreference = 'Stop'

# Labels applied to every filed issue. ci-broken is the existing aspire-wide
# label whose description is literally "Internal ADO pipeline is failing".
$Script:IssueLabels = @('area-engineering-systems', 'ci-broken', 'blocking-clean-ci')

# Assignees notified on issue creation. Also @-mentioned in each new-failure
# comment because edits to issue bodies do not re-fire notifications, but
# comment @-mentions do.
$Script:Assignees = @('joperezr', 'radical')
$Script:MentionLine = 'cc @joperezr @radical'

# Managed region in the issue body. Update-FailuresTableInBody only touches
# content between these markers, so any human-added prose elsewhere in the
# body is preserved across updates.
$Script:FailuresTableBeginMarker = '<!-- ci-broken-failures:begin -->'
$Script:FailuresTableEndMarker = '<!-- ci-broken-failures:end -->'
$Script:FailuresTableHeader = "| # | When (UTC) | Build | Commit | Failed stages |`n|---|------------|-------|--------|---------------|"
# Cap visible rows in the issue body. Older rows are collapsed into a
# "_N earlier failures omitted_" line; full per-failure history is still
# preserved in the issue's comments.
$Script:FailuresTableMaxRows = 50

# See Exit-NotifyScript for the SucceededWithIssues rationale.
$Script:HasWarnings = $false

function Write-Step {
    param([string]$Message)
    Write-Host "[$Mode] $Message"
}

# Logs to console AND, when running inside an AzDO pipeline, emits the
# logissue logging command so the warning surfaces in the task's Issues
# tab + build summary. task.logissue alone does NOT change the task
# result — Exit-NotifyScript does that via task.complete at exit time.
# See https://learn.microsoft.com/azure/devops/pipelines/scripts/logging-commands#logissue-log-an-error-or-warning
function Write-NotifyWarning {
    param([string]$Message)
    Write-Warning $Message
    if ($env:TF_BUILD -eq 'True') {
        Write-Host "##vso[task.logissue type=warning]$Message"
    }
    $Script:HasWarnings = $true
}

# Single exit point. If anything called Write-NotifyWarning, emit
# task.complete to flip the AzDO task result to SucceededWithIssues so
# the silent-dead-feature class of bug (e.g., bot lost issues:write,
# label deleted, API shape changed) becomes visible in build status
# rather than buried in task logs no one reads on green builds.
function Exit-NotifyScript {
    if ($Script:HasWarnings -and $env:TF_BUILD -eq 'True') {
        Write-Host "##vso[task.complete result=SucceededWithIssues;]done"
    }
    exit 0
}

# Defense in depth: pipeline gate is the primary filter. Pipeline
# trigger's `main*` wildcard means we must match `main` exactly.
function Test-NotifiableBranch {
    param([string]$Name)
    return ($Name -eq 'main') -or ($Name -like 'release/*')
}

# Thin wrapper around the gh CLI. Throws on non-zero exit so the surrounding
# try/catch + always-exit-0 contract picks up errors. stderr is folded into the
# same stream so any failure message is visible in the thrown exception.
# Auth: gh reads GH_TOKEN from the process environment (set once in the main
# body after the bot token is minted); no token plumbing through call sites.
function Invoke-Gh {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string[]]$ArgList,
        [string]$StdinBody
    )
    if ($PSBoundParameters.ContainsKey('StdinBody')) {
        $output = $StdinBody | & gh @ArgList 2>&1
    }
    else {
        $output = & gh @ArgList 2>&1
    }
    if ($LASTEXITCODE -ne 0) {
        throw "gh $($ArgList -join ' ') failed (exit $LASTEXITCODE): $output"
    }
    return $output
}

# Lists open issues with the ci-broken label and filters locally on the
# branch-specific marker. Returns an array (possibly empty) sorted by issue
# number ascending — so [0] is always the oldest open issue for this branch.
# `gh issue list` returns issues only (excludes PRs) and handles pagination
# internally up to --limit; realistic ci-broken issue count is < 10.
function Get-OpenBrokenIssuesForBranch {
    param([string]$Marker)

    $json = Invoke-Gh -ArgList @(
        'issue', 'list',
        '--repo', "$Owner/$Repo",
        '--label', 'ci-broken',
        '--state', 'open',
        '--limit', '1000',
        '--json', 'number,body,url'
    )

    $all = @($json | ConvertFrom-Json)
    $matched = @($all | Where-Object { $_.body -and $_.body.Contains($Marker) })
    return @($matched | Sort-Object -Property number)
}

function New-FailureTableRow {
    param([int]$Index)
    $shortSha = if ($CommitSha.Length -ge 7) { $CommitSha.Substring(0, 7) } else { $CommitSha }
    $commitLink = "[``$shortSha``](https://github.com/$Owner/$Repo/commit/$CommitSha)"
    # Escape pipes in user-supplied content so they don't break the markdown
    # table. FailedStages currently comes from a fixed enumeration in the
    # pipeline, but a future caller could pass arbitrary text.
    $stages = if ([string]::IsNullOrWhiteSpace($FailedStages)) { '—' } else { $FailedStages -replace '\|', '\|' }
    $when = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm')
    return "| $Index | $when | [$BuildNumber]($BuildUrl) | $commitLink | $stages |"
}

# Initial failures table section embedded in a freshly-filed issue body.
# Contains the header plus the first failure row (index 1).
function New-InitialFailuresTableSection {
    $row = New-FailureTableRow -Index 1
    return @"
$($Script:FailuresTableBeginMarker)
$($Script:FailuresTableHeader)
$row
$($Script:FailuresTableEndMarker)
"@
}

# Splices a new failure row into the managed region of an existing issue body.
# - Locates the begin/end markers; if missing (issue body was hand-edited or
#   pre-dates this feature), returns the body unchanged with a warning.
# - Counts existing visible rows + any "N earlier omitted" tally to determine
#   the next failure index.
# - When the visible-row count exceeds FailuresTableMaxRows, drops oldest
#   rows and rolls them into the omitted tally.
function Update-FailuresTableInBody {
    param([string]$Body)

    $beginIdx = $Body.IndexOf($Script:FailuresTableBeginMarker)
    $endIdx = $Body.IndexOf($Script:FailuresTableEndMarker)
    if ($beginIdx -lt 0 -or $endIdx -lt 0 -or $endIdx -lt $beginIdx) {
        Write-NotifyWarning "Could not locate failures-table markers in issue body; skipping body update."
        return $Body
    }

    $contentStart = $beginIdx + $Script:FailuresTableBeginMarker.Length
    $managedContent = $Body.Substring($contentStart, $endIdx - $contentStart)

    # Data rows look like:  | 3 | 2026-06-04 22:34 | [...](...) | ... | ... |
    $lines = $managedContent -split "`r?`n"
    $dataRows = @($lines | Where-Object { $_ -match '^\|\s*\d+\s*\|' })

    $omittedCount = 0
    foreach ($line in $lines) {
        if ($line -match '^_(\d+) earlier failures omitted') {
            $omittedCount = [int]$Matches[1]
            break
        }
    }

    $nextIndex = $dataRows.Count + $omittedCount + 1
    $newRow = New-FailureTableRow -Index $nextIndex
    $allRows = @($dataRows) + @($newRow)

    if ($allRows.Count -gt $Script:FailuresTableMaxRows) {
        $extraToDrop = $allRows.Count - $Script:FailuresTableMaxRows
        $omittedCount += $extraToDrop
        $allRows = $allRows | Select-Object -Skip $extraToDrop
    }

    $omittedLine = ''
    if ($omittedCount -gt 0) {
        $omittedLine = "_$omittedCount earlier failures omitted; see issue comments._`n`n"
    }

    $newContent = "`n" + $omittedLine + $Script:FailuresTableHeader + "`n" + ($allRows -join "`n") + "`n"

    return $Body.Substring(0, $contentStart) + $newContent + $Body.Substring($endIdx)
}

function New-IssueBody {
    param([string]$Marker)

    $failuresTable = New-InitialFailuresTableSection

    return @"
$Marker

The internal Azure DevOps build for ``microsoft-aspire`` (definition 1602)
is failing on ``$Branch``.

$failuresTable

This issue is updated with a new row in the table above on each subsequent
failure of the same branch, and closed automatically when the next build of
``$Branch`` succeeds. See [docs/ci/internal-build-failure-notifications.md](https://github.com/$Owner/$Repo/blob/main/docs/ci/internal-build-failure-notifications.md).

$Script:MentionLine
"@
}

function New-FailureFollowupCommentBody {
    return @"
Another failure on ``$Branch`` — see the failures table in the issue body for full history.

- **Build:** [$BuildNumber]($BuildUrl)
- **Commit:** ``$CommitSha``

$Script:MentionLine
"@
}

function New-SuccessCommentBody {
    return @"
✅ Build is green again on ``$Branch``:

- **Build:** [$BuildNumber]($BuildUrl)
- **Commit:** ``$CommitSha``

Closing.
"@
}

function Invoke-FailureMode {
    param([string]$Marker)

    if ($DryRun) {
        Write-Step "DRY-RUN: would run 'gh issue list --repo $Owner/$Repo --label ci-broken --state open' and filter by marker '$Marker'"
        Write-Step "DRY-RUN: assuming no existing issue; would run: gh issue create --repo $Owner/$Repo --title `"Internal build broken on $Branch`" --label $($Script:IssueLabels -join ',') --assignee $($Script:Assignees -join ',')"
        Write-Step "DRY-RUN: issue body would contain:"
        $body = New-IssueBody -Marker $Marker
        $body -split "`n" | ForEach-Object { Write-Step "DRY-RUN:   | $_" }
        Write-Step "DRY-RUN: would then re-list and close as duplicate if not oldest (race handler)"
        Write-Step "DRY-RUN: if an existing issue had been found, would run 'gh issue edit' to update the failures table and 'gh issue comment' for the follow-up with @-mentions"
        return
    }

    $existing = Get-OpenBrokenIssuesForBranch -Marker $Marker

    if ($existing.Count -eq 0) {
        Write-Step "No existing open issue for branch '$Branch'. Creating one."

        $issueBody = New-IssueBody -Marker $Marker
        # `gh issue create` accepts --label and --assignee multiple times.
        # Build flag pairs rather than relying on comma-separation, which is
        # more brittle if a label/assignee ever contains a comma.
        $labelFlags = @($Script:IssueLabels | ForEach-Object { '--label'; $_ })
        $assigneeFlags = @($Script:Assignees | ForEach-Object { '--assignee'; $_ })

        $createArgs = @(
            'issue', 'create',
            '--repo', "$Owner/$Repo",
            '--title', "Internal build broken on $Branch",
            '--body-file', '-'
        ) + $labelFlags + $assigneeFlags

        # gh issue create prints the new issue's URL as the last non-empty
        # line of stdout. Parse the number out for the race-handler compare.
        $createOutput = Invoke-Gh -ArgList $createArgs -StdinBody $issueBody
        $createdUrl = (@($createOutput) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Last 1).ToString().Trim()
        if ($createdUrl -notmatch '/issues/(\d+)$') {
            throw "Could not parse issue number from gh output: '$createdUrl'"
        }
        $createdNumber = [int]$Matches[1]
        Write-Step "Created issue #${createdNumber}: $createdUrl"

        # Post-create race handler: re-list and close as duplicate if we
        # aren't the oldest. See file header "Dedupe strategy".
        $recheck = Get-OpenBrokenIssuesForBranch -Marker $Marker
        $oldest = $recheck | Where-Object { $_.number -ne $createdNumber } | Select-Object -First 1
        if ($null -ne $oldest -and $oldest.number -lt $createdNumber) {
            Write-Step "Race detected: older open issue #$($oldest.number) found. Closing our just-created #${createdNumber} as duplicate."
            $dupBody = "Duplicate of #$($oldest.number). Two near-simultaneous failed builds raced past the dedupe window; auto-closing this one."
            Invoke-Gh -ArgList @('issue', 'comment', "$createdNumber", '--repo', "$Owner/$Repo", '--body-file', '-') -StdinBody $dupBody | Out-Null
            # `gh issue close --reason` accepts "completed" or "not planned"
            # (with a space) — gh maps the latter to the API's not_planned.
            Invoke-Gh -ArgList @('issue', 'close', "$createdNumber", '--repo', "$Owner/$Repo", '--reason', 'not planned') | Out-Null
        }
        return
    }

    $target = $existing[0]
    if ($existing.Count -gt 1) {
        Write-NotifyWarning "Found $($existing.Count) open ci-broken issues for branch '$Branch' (numbers: $($existing.number -join ', ')). Updating the oldest (#$($target.number)) and leaving the rest for human cleanup."
    }
    else {
        Write-Step "Found existing open issue #$($target.number) for branch '$Branch'. Updating failures table and appending comment."
    }

    # Append a row to the failures table in the issue body. Race: two
    # near-simultaneous failures doing list->modify->edit may drop one row;
    # accepted because the follow-up comment still fires and per-failure
    # history is preserved in the issue comments. The table is the at-a-glance
    # summary, not the system of record.
    $currentBody = $target.body
    if ($null -ne $currentBody) {
        $newBody = Update-FailuresTableInBody -Body $currentBody
        if ($newBody -ne $currentBody) {
            Invoke-Gh -ArgList @('issue', 'edit', "$($target.number)", '--repo', "$Owner/$Repo", '--body-file', '-') -StdinBody $newBody | Out-Null
            Write-Step "Updated failures table in #$($target.number)."
        }
    }

    $commentBody = New-FailureFollowupCommentBody
    Invoke-Gh -ArgList @('issue', 'comment', "$($target.number)", '--repo', "$Owner/$Repo", '--body-file', '-') -StdinBody $commentBody | Out-Null
    Write-Step "Appended failure comment to #$($target.number): $($target.url)"
}

function Invoke-SuccessMode {
    param([string]$Marker)

    if ($DryRun) {
        Write-Step "DRY-RUN: would run 'gh issue list --repo $Owner/$Repo --label ci-broken --state open' and filter by marker '$Marker'"
        Write-Step "DRY-RUN: for each matching open issue, would run 'gh issue comment' with green-build body and 'gh issue close --reason completed'"
        return
    }

    $existing = Get-OpenBrokenIssuesForBranch -Marker $Marker
    if ($existing.Count -eq 0) {
        Write-Step "No open ci-broken issue for branch '$Branch'. Nothing to close."
        return
    }

    foreach ($issue in $existing) {
        Write-Step "Closing issue #$($issue.number) ($($issue.url)) with green-build comment."

        $commentBody = New-SuccessCommentBody
        Invoke-Gh -ArgList @('issue', 'comment', "$($issue.number)", '--repo', "$Owner/$Repo", '--body-file', '-') -StdinBody $commentBody | Out-Null
        Invoke-Gh -ArgList @('issue', 'close', "$($issue.number)", '--repo', "$Owner/$Repo", '--reason', 'completed') | Out-Null
        Write-Step "Closed #$($issue.number)."
    }
}

# === main ===

try {
    Write-Step "Starting. Branch='$Branch' BuildId=$BuildId BuildNumber=$BuildNumber DryRun=$DryRun"

    if (-not (Test-NotifiableBranch -Name $Branch)) {
        Write-NotifyWarning "Branch '$Branch' is not in the notifiable set (main, release/*). No action taken."
        Exit-NotifyScript
    }

    $marker = "<!-- aspire-internal-build-broken:$Branch -->"

    if (-not $DryRun) {
        # Live mode enforces -AppId/-PrivateKeyPem presence here (see param comment).
        if ([string]::IsNullOrWhiteSpace($AppId) -or [string]::IsNullOrWhiteSpace($PrivateKeyPem)) {
            Write-NotifyWarning "Live mode requires -AppId and -PrivateKeyPem; aborting."
            Exit-NotifyScript
        }
        $tokenScript = Join-Path $PSScriptRoot 'Get-AspireBotInstallationToken.ps1'
        $token = & $tokenScript -AppId $AppId -PrivateKeyPem $PrivateKeyPem -Owner $Owner -Repo $Repo
        if ([string]::IsNullOrWhiteSpace($token)) {
            Write-NotifyWarning "Failed to mint installation token. Skipping notification."
            Exit-NotifyScript
        }
        # Register the token with AzDO so any incidental log echo in this
        # script or downstream tasks gets redacted. Using task.setsecret
        # rather than task.setvariable;issecret=true so the token isn't
        # also persisted as a job-scoped variable that other tasks could
        # accidentally reference via $(__notifyGhToken) — we only need
        # the log-masking effect. The token is consumed by `gh` through
        # the GH_TOKEN process env var set below.
        # https://learn.microsoft.com/azure/devops/pipelines/scripts/logging-commands#setsecret-register-a-value-as-a-secret
        Write-Host "##vso[task.setsecret]$token"
        # gh reads its auth token from GH_TOKEN (process env var, not
        # persisted to gh's config file). Set here so every subsequent
        # `gh` call in this process authenticates as aspire-repo-bot.
        $env:GH_TOKEN = $token
    }

    switch ($Mode) {
        'Failure' { Invoke-FailureMode -Marker $marker }
        'Success' { Invoke-SuccessMode -Marker $marker }
    }

    Write-Step "Done."
    Exit-NotifyScript
}
catch {
    # Never break the build. (See Exit-NotifyScript.)
    Write-NotifyWarning "Notification failed: $($_.Exception.Message)"
    Write-Warning $_.ScriptStackTrace
    Exit-NotifyScript
}
