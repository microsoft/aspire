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
# the gh CLI, and registers it as a secret with the agent (task.setsecret) so
# any incidental log echo gets redacted.
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
#   - Two builds of the same branch failing within the same window can still
#     briefly create two issues. Builds are rolling so this is rare; the
#     duplicate is left for a human to close rather than auto-deduped, which
#     avoids an extra `gh issue list` round-trip on every first-failure.
#
# Per-failure history:
#   - The issue body is written once at creation (build, commit, and the
#     failed stages for the first failure). Each subsequent failure on the
#     same branch posts a comment with that build's details and @-mentions —
#     editing the body would not re-fire notifications, but comments do.
#   - The comments are the per-failure history; the body is not rewritten
#     after creation.
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
#
# IMPORTANT: the notifiable-branch policy is defined in THREE places that
# must stay in sync if the policy ever changes:
#   1. _IsNotificationBranch in eng/pipelines/common-variables.yml
#   2. the notify_failure / notify_success stage `condition:` blocks in
#      eng/pipelines/azure-pipelines.yml
#   3. this function
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

# Probe for the gh CLI up front. If it's missing from the image, every
# Invoke-Gh call would throw into the top-level catch and we'd silently stop
# filing/closing issues (silent-dead-feature). The release pipeline
# pin-installs gh for the same reason. Returns $true when `gh --version` runs
# and exits 0.
function Test-GhAvailable {
    try {
        & gh --version *> $null
        return ($LASTEXITCODE -eq 0)
    }
    catch {
        return $false
    }
}

# Matches the dedupe marker only at the START of a line. Every issue body this
# script files puts the marker on its own first line, so anchoring avoids a
# false match if the marker text is ever pasted mid-prose into an unrelated
# issue (which success-mode would otherwise comment on and close).
function Test-IssueBodyMatchesMarker {
    param([string]$Body, [string]$Marker)
    if ([string]::IsNullOrEmpty($Body)) {
        return $false
    }
    return [regex]::IsMatch($Body, '(?m)^' + [regex]::Escape($Marker))
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
    $matched = @($all | Where-Object { Test-IssueBodyMatchesMarker -Body $_.body -Marker $Marker })
    return @($matched | Sort-Object -Property number)
}

# Renders the failed-stages list for the issue body / comment. Em-dash when
# empty. The pipeline passes a comma-separated list of stage names (no pipe
# characters), so no markdown escaping is needed.
function Format-FailedStages {
    if ([string]::IsNullOrWhiteSpace($FailedStages)) {
        return '—'
    }
    return $FailedStages
}

function New-IssueBody {
    param([string]$Marker)

    $stages = Format-FailedStages

    return @"
$Marker

The internal Azure DevOps build for ``microsoft-aspire`` (definition 1602)
is failing on ``$Branch``.

- **Build:** [$BuildNumber]($BuildUrl)
- **Commit:** ``$CommitSha``
- **Failed stages:** $stages

Each subsequent failure on the same branch adds a comment with that build's
details; the comments are the per-failure history. This issue is closed
automatically when the next build of ``$Branch`` succeeds. See [docs/ci/internal-build-failure-notifications.md](https://github.com/$Owner/$Repo/blob/main/docs/ci/internal-build-failure-notifications.md).

$Script:MentionLine
"@
}

function New-FailureFollowupCommentBody {
    $stages = Format-FailedStages
    return @"
Another failure on ``$Branch``.

- **Build:** [$BuildNumber]($BuildUrl)
- **Commit:** ``$CommitSha``
- **Failed stages:** $stages

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
        Write-Step "DRY-RUN: if an existing issue had been found, would run 'gh issue comment' to append a follow-up failure comment with @-mentions"
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
        # line of stdout.
        $createOutput = Invoke-Gh -ArgList $createArgs -StdinBody $issueBody
        $createdUrl = (@($createOutput) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Last 1).ToString().Trim()
        Write-Step "Created issue: $createdUrl"
        return
    }

    $target = $existing[0]
    if ($existing.Count -gt 1) {
        Write-NotifyWarning "Found $($existing.Count) open ci-broken issues for branch '$Branch' (numbers: $($existing.number -join ', ')). Updating the oldest (#$($target.number)) and leaving the rest for human cleanup."
    }
    else {
        Write-Step "Found existing open issue #$($target.number) for branch '$Branch'. Appending failure comment."
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
        # Bail loudly if gh is missing rather than minting a token we can't use
        # and then failing every API call into the catch (see Test-GhAvailable).
        if (-not (Test-GhAvailable)) {
            Write-NotifyWarning "gh CLI is not available on this image; cannot file/update/close issues. Skipping notification."
            Exit-NotifyScript
        }
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
