# Scheduled-workflow failure notifications

Several scheduled GitHub Actions workflows in this repo run unattended
(generate diffs, refresh manifests/SDKs, update models/dependencies, clean up
deployments, retrain the labeler, backmerge releases). When one of them fails,
GitHub only emails the last person who edited the workflow file — so a broken
scheduled job can sit unnoticed for days.

[`monitor-scheduled-workflows.yml`](../../.github/workflows/monitor-scheduled-workflows.yml)
watches those workflows and files, updates, or closes a single deduplicated
GitHub issue per workflow.

This is the GitHub Actions counterpart to the internal AzDO build notifier.
It reuses the same file → update → close-on-green contract, keyed per *workflow*
instead of per branch.

## Schedule

Runs every 2 hours (`cron: '0 */2 * * *'`) and on `workflow_dispatch`. The
manual dispatch accepts a `dry_run` boolean that logs the issue actions it
*would* take without mutating anything on GitHub.

Each run fetches recent completed scheduled runs and processes the runs updated
in a three-hour polling window, oldest to newest. That prevents an hourly
workflow failure from being hidden by a later success before the next watchdog
tick; the per-run comment marker still deduplicates repeat observations.

## What it watches

The watch-list lives in
[`monitor-scheduled-workflows.config.json`](../../.github/workflows/monitor-scheduled-workflows.config.json)
(not in the workflow script), as an array of
`{ file, name, enabled, selfReports?, labels? }` entries.

**Full-watch entries** — workflows whose `failure` conclusion *unambiguously*
means "broken". The watchdog files an issue on any failure conclusion:

- `generate-api-diffs`, `generate-ats-diffs`
- `refresh-manifests`
- `update-dependencies`, `update-ai-foundry-models`,
  `update-azure-vm-sizes`, `update-aspire-skills-bundle`
- `deployment-cleanup`
- `labeler-cache-retention`
- `warm-cli-e2e-image-cache`
- `locker`
- `backmerge-release`

**Backstop entries (`selfReports: true`)** — workflows that file their *own*
normal failures in-pipeline via an `if: failure()` reporter. For these the
watchdog records **only** `startup_failure` and `timed_out` — the two
conclusions that in-pipeline reporter cannot catch (see below) — and **never**
plain `failure` (which the reporter owns; recording it would double-file under a
second marker):

- `tests-outerloop`, `tests-quarantine` (see
  [specialized-test-failure-issues.md](specialized-test-failure-issues.md))
- `tests-daily-smoke`, `deployment-tests` (see
  [pipeline-failure-issues.md](pipeline-failure-issues.md))

- **To add a full-watch workflow:** add a `{ "file", "name" }` entry (entries
  are watched by default).
- **To add a backstop for a self-reporting workflow:** add
  `"selfReports": true`.
- **To add labels** to the filed issue (beyond `automation-broken`): add a
  `"labels": [ ... ]` array.
- **To stop watching one** (without deleting it): set `"enabled": false`.
  Disabled entries are skipped and logged.

### Why backstop entries record only `startup_failure` / `timed_out`

An in-pipeline reporter is a job gated on `if: failure()`. Two conclusions slip
past it:

- **`startup_failure`** — the run never started a job (bad YAML, an unresolvable
  `uses:`, a dead runner), so the reporter job itself never runs.
- **`timed_out`** — a job-level timeout is *cancelled-class*, so
  [`failure()` is false](https://docs.github.com/en/actions/reference/workflows-and-actions/expressions#failure)
  and the reporter job does not run.

Plain `failure` (including step-level timeouts, which surface as `failure`) *does*
trigger the reporter, so the watchdog leaves those to it.

### Deliberately excluded

- **`backmerge-release` infra** is full-watched here; it also files a "merge
  conflicts" issue on a *green* run (handled in its own workflow).
- **`workflow_call` building blocks** (e.g. `tests.yml`, `run-tests.yml`) —
  their failures surface in the caller, which is where the signal belongs.
- **`ci.yml`** — red-main push failures are filed (and self-closed) by ci.yml
  itself; see [ci-failure-issues.md](ci-failure-issues.md).
- **Agentic `*.lock.yml` workflows** — `gh-aw` has its own error reporting.

## What gets filed

When a watched workflow has a completed scheduled run on `main` in the watchdog
polling window that concluded `failure`, `timed_out`, or `startup_failure`:

- **Title:** `Scheduled workflow failing: <display name>`
- **Label:** `automation-broken` (created idempotently by the workflow)
- **Body marker:** the first line is a hidden HTML comment
  `<!-- automation-broken:<workflow-file> -->`, used for dedup.

Only one open issue per workflow exists at a time. The body is a fixed
description written once at filing (the hidden marker plus prose); each failed
run is recorded as a **comment** carrying the run link, commit, and conclusion.
The comment is what fires notifications.

Comments are deduplicated by run: the scanner can observe the same failed run on
multiple ticks. Each comment embeds a hidden `<!-- run:<id> -->` marker; a run
whose comment already exists is a no-op (no duplicate comment) until a newer run
completes.

`cancelled` and `skipped` conclusions are intentionally ignored — operator
cancellation and skipped runs are not workflow defects. A `timed_out` run, by
contrast, is treated as a failure and files an issue. For **backstop entries**
(`selfReports: true`) a plain `failure` is also ignored (its in-pipeline reporter
owns it); only `startup_failure` and `timed_out` file an issue, and the body says
so to distinguish it from the in-pipeline issue. Issues carry `automation-broken`
plus any per-entry `labels`, and are stamped `autoClose:true`.

## What gets closed

When the newest completed scheduled run in the polling window concludes
`success`, any open `automation-broken` issue for that workflow gets a "latest
run succeeded" comment and is closed with `state_reason: completed`.

## Dedup

Issue lookup uses `GET /issues?labels=automation-broken&state=open` (strongly
consistent) plus a local body-marker filter — the Search API is avoided because
its eventual-consistency window could let near-simultaneous runs each see
"0 hits". If two open issues ever carry the same marker, the oldest (lowest
number) is treated as canonical.

## Permissions and auth

The job uses the default `GITHUB_TOKEN` with `actions: read` (list runs) and
`issues: write` (file/comment/close). No app token is needed — issues are
created in the same repo and the watchdog deliberately does *not* trigger
downstream automation.

## Why it never fails noisily

Per-workflow run lookups are wrapped so a single unreadable workflow logs a
warning and is skipped rather than failing the whole watchdog run.

## Logic and tests

The reusable issue mechanics (marker dedup, the comment-recording loop with
per-run dedup, octokit primitives) live in the generic, repo-agnostic engine
[`tracking-issue.js`](../../.github/workflows/tracking-issue.js), shared with the
[specialized-test failure reporter](specialized-test-failure-issues.md), the
[nightly-pipeline failure reporter](pipeline-failure-issues.md), the
[red-main CI reporter](ci-failure-issues.md), and
unit-tested by
[`TrackingIssueTests`](../../tests/Infrastructure.Tests/WorkflowScripts/TrackingIssueTests.cs).

The watchdog-specific content (markers, titles, body/comment formatting, the
record/close/noop decision) and the `run()` orchestrator that reads the config,
loops, and drives the engine live in
[`monitor-scheduled-workflows.js`](../../.github/workflows/monitor-scheduled-workflows.js),
invoked from the workflow's `github-script` step. The pure helpers are unit-tested by
[`MonitorScheduledWorkflowsTests`](../../tests/Infrastructure.Tests/WorkflowScripts/MonitorScheduledWorkflowsTests.cs)
via the Node harness
[`monitor-scheduled-workflows.harness.js`](../../tests/Infrastructure.Tests/WorkflowScripts/monitor-scheduled-workflows.harness.js).

When changing the workflow's job/step names or the module's exported contract,
keep the workflow YAML, the `.js` module, the harness, the test, and this doc
aligned.
