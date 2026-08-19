# Red-main CI failure issues

The `CI` workflow ([`ci.yml`](../../.github/workflows/ci.yml)) runs on every push
to a protected branch (`main`, `release/**`). When such a push fails CI the branch
is "red", and — unlike a PR failure, which the author sees in their own checks —
nobody necessarily owns it. This mechanism files a single deduplicated GitHub
issue per branch when a push is red, and **closes it automatically** when a later
push to the same branch is green.

It is a consumer of the shared, repo-agnostic tracking-issue engine
([`tracking-issue.js`](../../.github/workflows/tracking-issue.js)), alongside the
[scheduled-workflow scanner](monitor-scheduled-workflows.md), the
[specialized-test failure reporter](specialized-test-failure-issues.md), and the
[nightly-pipeline failure reporter](pipeline-failure-issues.md).

## Push only — PR failures are excluded

The reporter runs only on `push` events. A failing **pull request** is the
author's to fix and already surfaces in the PR's checks, so filing an issue for it
would be noise. Per-test tracking issues are still created on demand via the
[`/create-issue` command](../../.github/workflows/create-failing-test-issue.yml).

## Per-branch keying

A red `main` and a red `release/13.3` are different problems, so the dedup marker
embeds the ref and each branch gets its own issue:

```text
<!-- ci-failure:ci.yml:push:main -->
<!-- ci-failure:ci.yml:push:release/13.3 -->
```

## What gets filed

When a push to a protected branch fails CI:

- **Title:** `CI failing on <ref>`, with the ref in backticks — for example, a
  red `main` produces the title ``CI failing on `main` ``.
- **Label:** `automation-broken`
- **Body marker:** the per-branch marker above, on the first line.
- **Close stamp:** a hidden `<!-- autoclose:true -->` stamp (see below).

The body is a fixed description written once at filing; each failed run is recorded
as a **comment** carrying the run link and commit. The comment intentionally does
**not** assert a failing-test list — a push can fail for non-test reasons (setup,
build, a non-TRX job), so it links the run rather than claiming specific tests.
Each comment embeds a hidden `<!-- run:<id> -->` marker used to dedup re-runs of
the same run.

## Self-closing on green

`ci.yml` runs on every push, so a green push to the branch is the most timely
signal that it is no longer red — there is no need to wait for an external poll.
The same `ci_failure_tracker` job that files the issue also closes the branch's
open issue (with a "CI is green again" comment) when a later push to the same
branch passes CI.

The `autoClose:true` body stamp records this policy on the issue itself, so the
scheduled [watchdog](monitor-scheduled-workflows.md) can also close it as a
backstop. Test-failure issues that should **not** auto-close (e.g. the specialized
reporter's, where a single green run does not prove a flaky test fixed) carry no
such stamp.

### Why explicit `needs.*.result` checks

A single job (`ci_failure_tracker`) handles both filing and closing: it runs on
every push (`if: always()`) and the script opens or closes based on the aggregate
CI result the workflow passes in env:

- `CI_RED` = `contains(needs.*.result, 'failure')` — file/update the issue.
- `CI_GREEN` = `prepare_for_ci`, `tests`, and `stabilization_check` all `success`
  — close the issue.

These are derived from explicit `needs.*.result` checks rather than
`failure()`/`success()` because `tests`/`stabilization_check` are **skipped** on
the no-relevant-changes path (`skip_workflow`). A skipped run is neither red nor
green, so the script leaves any open issue untouched — requiring all-`success` for
`CI_GREEN` avoids closing a still-red issue on a docs-only push, and the failure
check avoids filing on a skip.

## Logic and tests

The reusable issue mechanics (marker dedup, the comment-recording loop with
per-run dedup, octokit primitives) live in the generic engine
[`tracking-issue.js`](../../.github/workflows/tracking-issue.js), unit-tested by
[`TrackingIssueTests`](../../tests/Infrastructure.Tests/WorkflowScripts/TrackingIssueTests.cs).

The reporter logic — the pure helpers (marker, title, body, comment) and the
`reportFailure()` / `resolveSuccess()` orchestrators (dispatched by `track()` from
the workflow) — lives in
[`report-ci-failure.js`](../../.github/workflows/report-ci-failure.js). The pure
helpers are unit-tested by
[`ReportCiFailureTests`](../../tests/Infrastructure.Tests/WorkflowScripts/ReportCiFailureTests.cs);
the orchestrators are driven against an in-memory octokit fake by
[`ReportCiFailureIntegrationTests`](../../tests/Infrastructure.Tests/WorkflowScripts/ReportCiFailureIntegrationTests.cs)
via the Node harness
[`report-ci-failure.integration.harness.js`](../../tests/Infrastructure.Tests/WorkflowScripts/report-ci-failure.integration.harness.js).

When changing the workflow job/step names or the module's exported contract, keep
the workflow YAML, the `.js` modules, the harnesses, the tests, and this doc
aligned.
