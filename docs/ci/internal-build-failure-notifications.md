# Internal build failure notifications

The internal Azure DevOps pipeline (`microsoft-aspire`, definition 1602,
defined in [`eng/pipelines/azure-pipelines.yml`](../../eng/pipelines/azure-pipelines.yml))
files a GitHub issue on [microsoft/aspire](https://github.com/microsoft/aspire/issues)
when it breaks on a publishing branch, and closes that issue when the next
build of the same branch goes green.

This document describes the contract so future maintainers can reason about
the behavior without re-reading the pipeline YAML.

## What gets notified

Two stages run at the end of every non-PR internal build, after every
other stage:

- `notify_failure` — files or updates a GitHub issue when at least one
  build stage ends with `Failed`. It depends on **every** non-notify
  stage — `build_sign_native`, `build_extension`, `build`,
  `template_tests`, `assemble`, and `prepare_installers` — so a break
  anywhere (including the publish stage `assemble`) files an issue.
- `notify_success` — closes any open `ci-broken` issue for the branch
  when all of those stages end with `Succeeded` or `SucceededWithIssues`
  (`prepare_installers` may also end with `Skipped`; see below).

A stage whose dependency failed is reported as `Skipped` (not `Failed`)
by Azure Pipelines, so each stage must be watched directly — a single
downstream stage cannot be relied on to roll failures up. This is why
both conditions enumerate the full stage set rather than gating on one
terminal stage.

`prepare_installers` is allowed to end with `Skipped` in the success
condition only as a defensive measure. On a notifiable branch
(`main` / `release/*`) it runs whenever `build_sign_native` succeeded, so
a `Skipped` there implies `build_sign_native` did not succeed — a failure
path already covered by `notify_failure`. (Its own condition only skips on
the *stable* channel for **non**-notifiable branches.)

Both stages gate on the branch being either:

- `refs/heads/main` (exact — the trigger uses the wildcard `main*` so an
  exact match here is load-bearing to avoid sweeping in branches like
  `main-something`), or
- `refs/heads/release/*`.

`internal/release/*` is deliberately excluded so internal branch names
don't leak into the public issue tracker. Pull-request builds are also
excluded.

The two stages must be at the stage level (not as two jobs in a single
stage) because cross-stage dependency results can only be referenced
from a stage condition via `dependencies.<stage>.result`; from a job
condition the only available form is
`stageDependencies.<stage>.<job>.result`, which has no stage-aggregate
equivalent.

## What gets filed

When the `notify_failure` stage fires, it creates (or appends a comment to)
a single GitHub issue per affected branch:

- **Title:** `Internal build broken on <branch>`
- **Labels:** `area-engineering-systems`, `ci-broken`, `blocking-clean-ci`
- **Assignees:** `joperezr`, `radical`
- **Body marker:** the first line is a hidden HTML comment
  `<!-- aspire-internal-build-broken:<branch> -->` used for dedup.

Only one open issue per branch exists at a time.

The body records the first failure's build link, commit SHA, and the
comma-separated list of failed stages (any of `build_sign_native`,
`build_extension`, `build`, `template_tests`, `assemble`,
`prepare_installers`). It is written once at creation and is **not**
rewritten afterwards.

On each subsequent failure the script **posts a follow-up comment** with
that build's link, commit SHA, failed stages, and `cc @joperezr @radical`.
The comments are the per-failure history — and the comment is what fires
notifications, since editing the issue body would not.

`Canceled` stage results (operator cancellation, 1ES timeouts) intentionally
do not file an issue — the stage condition uses explicit `in(..., 'Failed')`
checks which exclude `Canceled`.

## What gets closed

The `notify_success` stage lists open `ci-broken` issues, filters by the
branch marker, and for each match posts a "build is green again" comment
and closes the issue with `state_reason: completed`.

### Mixed results that neither file nor close

A build can finish with **no** stage `Failed` but one or more watched stages
`Canceled` (a 1ES timeout or operator cancellation) or `Skipped`. This is the
*limbo* case: `notify_failure` does not fire (nothing `Failed`) and
`notify_success` does not fire (not every stage is `Succeeded` /
`SucceededWithIssues`). This is intentional, not a coverage gap:

- No issue is filed — a cancellation is infrastructure noise, not a code break.
- An existing open `ci-broken` issue is **left open** — the build produced no
  fully-green signal, and we cannot assert the break is fixed when a stage
  never completed. Auto-closing here would be a false all-clear.

The consequence is that an open issue can persist across successive limbo
builds until a fully-green build closes it. If you have confirmed the
cancellation was spurious (e.g. a transient 1ES timeout on an otherwise-healthy
build), close the issue manually.

Widening the success condition to tolerate `Canceled` was considered and
rejected: a canceled stage never verified its work, so tolerating it risks
closing the issue while the tree is still broken.

## Dedup and race handling

Issue lookup uses `GET /repos/microsoft/aspire/issues?labels=ci-broken&state=open`
(strongly consistent) plus a local body-marker filter. The Search API is
intentionally avoided because its 1–2 minute eventual-consistency window
would cause near-simultaneous failed builds to each see "0 hits" and file
duplicate issues.

Two builds of the same branch failing within that window can still briefly
create two issues. Because builds are rolling this is rare, and the cost of
auto-deduping it (an extra `gh issue list` round-trip on every first-failure)
isn't worth it — the duplicate is left for a human to close.

## Auth

The script mints an installation access token for the **aspire-repo-bot**
GitHub App via [`Get-AspireBotInstallationToken.ps1`](../../eng/pipelines/scripts/Get-AspireBotInstallationToken.ps1)
(the same helper used by the release pipeline's
`dispatch-release-github-tasks.ps1`). The token is immediately registered
as a secret with the agent via `##vso[task.setsecret]` so any incidental
log echo is redacted; it is consumed by `gh` through the `GH_TOKEN` process
environment variable and is not persisted as a pipeline variable.

The App's `aspire-bot-app-id` and `aspire-bot-private-key` secrets come
from the `Aspire-Release-Secrets` variable group, imported at pipeline
scope in `eng/pipelines/azure-pipelines.yml` and gated on non-PR builds
of `refs/heads/main` or `refs/heads/release/*` — the same condition the
notify stages use. Manual runs on feature branches and PR builds skip
the import entirely.

**Prerequisite**: the aspire-repo-bot install on microsoft/aspire must have
`issues:write` permission. If missing, the script will 403 on every call
(but never break the build — see below).

## Disabling for a single run

Queue the pipeline manually and set `Notify on failure: dry-run` to true.
In dry-run mode, both stages log the `gh` CLI commands they *would* run
without mutating anything on GitHub. This applies to both the failure
and success paths — a green-build dry-run will not accidentally close
real open issues.

Dry-run mode is fully decoupled from the aspire-repo-bot credentials:
the wrapper omits the `ASPIRE_BOT_APP_ID` / `ASPIRE_BOT_PRIVATE_KEY` env
block and the script's `-AppId` / `-PrivateKeyPem` parameters are
non-mandatory, so a dry-run validation works without Aspire-Release-Secrets
variable group access and never mints a token.

## Why this never breaks the build

[`Notify-GitHubOnBuildResult.ps1`](../../eng/pipelines/scripts/Notify-GitHubOnBuildResult.ps1)
wraps the entire body in `try`/`catch` and always exits 0. Any GitHub API
error, network blip, or 401/403 from a missing App permission produces a
`Write-Warning` in the job log but leaves the build result unchanged. A
flaky notification path must never turn an otherwise-correct build red.

However, a silently-skipped notification is its own failure mode — operators
need to see when the notification path itself broke (e.g., revoked App
permission, GitHub API shape change, deleted label). The catch block emits
AzDO logging commands so failures are visible without breaking the build:

- `##vso[task.logissue type=warning]` surfaces the warning in the build
  summary, in 1ES dashboards, and on the badge.
- `##vso[task.complete result=SucceededWithIssues;]` bumps the job result
  to `SucceededWithIssues`, which renders as a yellow badge instead of
  green. Notifications and dashboards can filter on this.

A build that finishes "green-but-yellow" means the upstream build itself
succeeded, but the notify stage's call to GitHub failed for some reason —
worth investigating, but does not block anything that depends on the build.

## Manually filing or closing

If you need to file or close a `ci-broken` issue by hand (e.g. during
recovery), use the existing label and add the marker `<!-- aspire-internal-build-broken:<branch> -->`
as the first line of the body. The marker must start a line — the script
matches it anchored to the start of a line so the text can't accidentally
match if pasted mid-prose into an unrelated issue. The script's next run
will treat the issue as the canonical open one and append/close accordingly.
