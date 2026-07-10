# Nightly-pipeline failure issues (deployment E2E & daily smoke)

The scheduled `Deployment E2E Tests` (`deployment-tests.yml`) and
`Daily CLI Smoke Tests` (`tests-daily-smoke.yml`) workflows run unattended and
otherwise fail silently — GitHub only emails whoever last edited the workflow
file. Each workflow files a GitHub issue when a **scheduled** run fails.

This is a consumer of the shared, repo-agnostic tracking-issue engine
([`tracking-issue.js`](../../.github/workflows/tracking-issue.js)), alongside the
[scheduled-workflow scanner](monitor-scheduled-workflows.md), the
[specialized-test failure reporter](specialized-test-failure-issues.md), and the
[red-main CI reporter](ci-failure-issues.md). Like the
specialized reporter it runs *inside* the pipeline (so it has the run context),
but unlike it, a failure here is **not** classified into test-vs-infra: a nightly
deployment/smoke failure can be either, and these pipelines do not inspect results
to tell them apart. The issue just records "the scheduled run failed" with a link
to the run.

## What gets filed

When a scheduled run fails:

- **Title:** `Nightly run failing: <display name>`
- **Labels:** the workflow's existing labels **plus** `automation-broken`:
  - deployment: `automation-broken`, `area-testing`, `deployment-e2e`
  - smoke: `automation-broken`, `area-cli`, `failing-test`
- **Body marker:** a hidden HTML comment `<!-- ci-failure:<workflow-file>:scheduled -->`
  on the first line, used for dedup.

The body is a fixed description written once at filing (the hidden marker plus
prose); each failed run is recorded as a **comment** carrying the run link and
commit. Each comment embeds a hidden `<!-- run:<id> -->` marker used to dedup
re-runs of the same run.

## How a run is reported

Each workflow's reporter job runs only on
`failure() && github.event_name == 'schedule' && github.repository_owner == 'microsoft'`
(so PR/manual runs never file issues and forks stay quiet). Because the job is
gated on `failure()`, it cannot run for a `startup_failure` (no job runs) or a
`timed_out` run (cancelled-class); the scheduled-workflow scanner **backstops**
those two conclusions — see
[monitor-scheduled-workflows.md](monitor-scheduled-workflows.md). It checks out
`main` to load the local `.js` modules and calls
[`report-pipeline-failure.js#report()`](../../.github/workflows/report-pipeline-failure.js),
which:

1. Ensures the `automation-broken` label exists.
2. Finds the open issue carrying the per-workflow marker.
3. **No open issue** → creates one (static body with the marker) and posts the
   failure comment.
4. **Open issue exists** → posts the failure comment, unless this run's comment is
   already present (dedup), in which case it is a no-op.

The **comment** is what fires notifications. The first filing notifies a team via
a `/cc` `@mention` in the body (deployment cc's `@microsoft/aspire-team`);
subsequent failures notify via comment.

### Per-run detail on the comment

The smoke reporter parses the per-route Aspire CLI versions from its uploaded
artifact and passes them as `commentDetail`, so they appear on each run's
failure comment rather than being baked into the body. The artifact-parsing
helpers stay inline in `tests-daily-smoke.yml` — they are smoke-specific and not
part of the reusable engine.

## Dedup and lifecycle

One open issue is kept per workflow — subsequent failed runs add a comment rather
than filing a new issue (replacing the previous *per-day* issue behaviour). Issues
are **closed by a human** once the underlying problem is fixed; there is no
auto-close-on-green (a dev mid-triage should not have the issue closed out from
under them by a transient green run).

Issue lookup uses `GET /issues?labels=automation-broken&state=open` (strongly
consistent) plus a local marker filter. The query is a superset — it also returns
the scanner's and specialized reporter's `automation-broken` issues — but the
per-workflow marker (`ci-failure:<file>:scheduled`) selects only this pipeline's
issue, so the three mechanisms never manage each other's issues. Pull requests
returned by the list API are excluded.

## Logic and tests

The reusable issue mechanics (marker dedup, the comment-recording loop with
per-run dedup, octokit primitives) live in the generic engine
[`tracking-issue.js`](../../.github/workflows/tracking-issue.js), unit-tested by
[`TrackingIssueTests`](../../tests/Infrastructure.Tests/WorkflowScripts/TrackingIssueTests.cs).

Reporter logic — the pure helpers (marker, title, body/comment formatting) and the
`report()` orchestrator — lives in
[`report-pipeline-failure.js`](../../.github/workflows/report-pipeline-failure.js).
The pure helpers are unit-tested by
[`ReportPipelineFailureTests`](../../tests/Infrastructure.Tests/WorkflowScripts/ReportPipelineFailureTests.cs);
`report()` is driven against an in-memory octokit fake by
[`ReportPipelineFailureIntegrationTests`](../../tests/Infrastructure.Tests/WorkflowScripts/ReportPipelineFailureIntegrationTests.cs)
via the Node harness
[`report-pipeline-failure.integration.harness.js`](../../tests/Infrastructure.Tests/WorkflowScripts/report-pipeline-failure.integration.harness.js).

When changing the workflow job/step names or the module's exported contract, keep
the workflow YAML, the `.js` modules, the harnesses, the tests, and this doc
aligned.
