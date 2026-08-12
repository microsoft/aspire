# Specialized-test failure issues (outerloop & quarantine)

The scheduled `Outerloop Tests` (`tests-outerloop.yml`) and `Quarantined Tests`
(`tests-quarantine.yml`) workflows run unattended and otherwise fail silently —
GitHub only emails whoever last edited the workflow file. Each workflow files a
GitHub issue when a **scheduled** run fails.

This is the test-workflow counterpart to the scheduled-workflow scanner
([monitor-scheduled-workflows.md](monitor-scheduled-workflows.md)). It lives
*inside* the test pipelines because, unlike the scanner, it needs the run's test
results to tell a test failure from an infrastructure break. The scanner does,
however, **backstop** these workflows for `startup_failure` and `timed_out` — the
two conclusions where no job (and so no in-pipeline reporter) runs.

## Two failure kinds

| Kind | When | Issue label | Title |
|------|------|-------------|-------|
| `test-failures` | outerloop tests failed | `failing-test` | `Test failures: Outerloop Tests` |
| `infra` | the run broke before/around test execution (build/setup, no TRX) | `automation-broken` | `CI infrastructure failing: <name>` |

### Why quarantine is infra-only

`tests-quarantine.yml` passes `ignoreTestFailures: true`. In `run-tests.yml` that
appends `|| true` to the test step and then only checks that `.trx` files with a
nonzero test count were produced. So **failing quarantined tests never red the
run** — they are flaky by definition. A *failed* quarantine run therefore always
means infrastructure broke, and only an `infra` issue is filed.

`tests-outerloop.yml` does not ignore test failures, so a failed outerloop run is
classified by inspecting the produced `.trx`:

- failed test names present → `test-failures`
- clean extraction, zero failed → `infra` (the run broke before/around test
  execution; no test produced a failed result)
- results could not be extracted (download/tool flake on a genuinely-red run) →
  `test-failures`, with a "could not enumerate — see artifacts" comment. A red run
  with unknown results is never downgraded to `infra`, so the failing-test signal
  is not lost.

The classification logic is `classifyFailure()` in
[`report-specialized-test-failures.js`](../../.github/workflows/report-specialized-test-failures.js).

## How a run is reported

The reporter job runs only on `failure() && github.event_name == 'schedule'` (so
PR-triggered runs from the `paths:` filter never file issues), and only for
`microsoft/aspire`.

For outerloop it downloads the runner's `logs-*` artifacts (which contain the
`.trx` files) and runs:

```bash
GenerateTestSummary <all-logs> --failed-tests-json <out>
```

which writes `{ "failedTests": [...], "count": N, "extractionFailed": bool }`
(outcomes `Failed`, `Error`, `Timeout`, `Aborted`). `extractionFailed` is `true`
when at least one `.trx` was unreadable and no failures were collected, so a red run
with entirely-corrupt results is reported as a test failure rather than infra. The
orchestrator also synthesizes `extractionFailed: true` if the tool itself errors,
the artifact download fails, or the extract step crashes before producing a results
path. Quarantine skips this step entirely.

An `actions/github-script` step then calls the shared orchestrator
[`specialized-test-failure-runner.js`](../../.github/workflows/specialized-test-failure-runner.js),
which:

1. Classifies the failure and picks the label.
2. Finds the open issue carrying the per-(workflow, kind) marker
   `<!-- ci-failure:<workflow-file>:<kind> -->`.
3. **No open issue** → creates one (static body with the marker) and posts the
   failure comment.
4. **Open issue exists** → posts the failure comment, unless this run's comment is
   already present (dedup), in which case it is a no-op.

The **comment** is what fires notifications; for `test-failures` it lists the
failing tests (capped at 50, the rest are in the run artifacts). The issue body is
a fixed description; each failed run is recorded as a comment carrying a hidden
`<!-- run:<id> -->` marker used to dedup re-runs.

## Dedup and lifecycle

One open issue is kept per `(workflow, kind)` — subsequent failed runs add a
comment rather than filing a new issue. Issues are **closed by a human** once the
underlying problem is fixed; there is no auto-close-on-green (a dev mid-triage
should not have the issue closed out from under them by a transient green run).

Issue lookup uses `GET /issues?labels=<label>&state=open` (strongly consistent)
plus a local marker filter, mirroring the scanner. The markers use a distinct
`ci-failure:` prefix so the scanner and this reporter never manage each other's
issues.

## Why `failing-test` for outerloop test failures

Outerloop test-failure issues use the existing `failing-test` label so they show
up alongside other failing-test issues and tooling. Infra issues reuse
`automation-broken` (shared with the scanner) because an infra break in a
scheduled pipeline is the same class of problem the scanner reports.

## Logic and tests

The reusable issue mechanics (marker dedup, the comment-recording loop with
per-run dedup, octokit primitives) live in the generic, repo-agnostic engine
[`tracking-issue.js`](../../.github/workflows/tracking-issue.js), shared with the
[scheduled-workflow scanner](monitor-scheduled-workflows.md), the
[nightly-pipeline failure reporter](pipeline-failure-issues.md), and the
[red-main CI reporter](ci-failure-issues.md), and unit-tested by
[`TrackingIssueTests`](../../tests/Infrastructure.Tests/WorkflowScripts/TrackingIssueTests.cs).

Reporter-specific pure logic (markers, titles, classification, body/comment
formatting) is in
[`report-specialized-test-failures.js`](../../.github/workflows/report-specialized-test-failures.js)
and unit-tested by
[`ReportSpecializedTestFailuresTests`](../../tests/Infrastructure.Tests/WorkflowScripts/ReportSpecializedTestFailuresTests.cs).
The `--failed-tests-json` extraction is covered by
[`GenerateTestSummaryToolTests`](../../tests/Infrastructure.Tests/GenerateTestSummary/GenerateTestSummaryToolTests.cs).
The network orchestrator (`specialized-test-failure-runner.js`) is a thin wiring
layer over the engine. Its results-read/classify preamble and the
find-or-create + comment-dedup branching it delegates to the engine are covered
by integration tests in
[`SpecializedTestFailureRunnerTests`](../../tests/Infrastructure.Tests/WorkflowScripts/SpecializedTestFailureRunnerTests.cs),
driven against an in-memory octokit fake via
`specialized-test-failure-runner.harness.js`.

When changing the workflow job/step names or the module's exported contract, keep
the workflow YAML, the `.js` modules, the tests, and this doc aligned.
