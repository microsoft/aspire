# Auto-rerun transient CI failures

This document explains how the automatic CI rerun system works and how to configure it.

## How it works at a glance

When a `CI` pull request run fails on GitHub Actions, a companion workflow can analyze the failure, determine whether it was caused by transient infrastructure or test issues, and — if safe — request GitHub to rerun the failed jobs. That broad analysis remains available, but the pull request and manual-dispatch paths currently run in temporary force mode: they skip classification and rerun all failed jobs when an associated pull request is open and the attempt cap allows it. Immediately before the request, the workflow verifies that the selected run is still the same completed failed attempt. The workflow posts a comment on the PR explaining what it did.

A failed `push` run for the current `main` SHA uses a separate, narrow infrastructure allowlist. Source attempt 1 may request one retry only when every non-aggregate failed job matches that allowlist; attempt 2 remains failed if it does not pass. Before the request, the workflow re-fetches the run, `refs/heads/main`, and the workflow's main run list. It skips the write unless the run is still the same completed failed attempt, the failed SHA is still current, no newer main CI run supersedes it, and the attempt cap is not exceeded. The GitHub API has no atomic expected-attempt precondition, so the final run revalidation minimizes but cannot eliminate the check-to-write race.

**Scheduled `Outerloop Tests` runs use a separate, simpler workflow.** Outerloop runs have no associated PR, so they are rerun unconditionally (no analysis, no PR comment) with the same attempt cadence. See [Auto-rerun outerloop failures](auto-rerun-outerloop-failures.md).

> **Currently in force mode.** The pull request and manual-dispatch paths are temporarily configured to skip the analysis below and rerun the failed jobs on **any** failed run with an open PR. See [Force-rerun all failures](#force-rerun-all-failures-force_rerun_all). The rest of this section describes the normal behavior used when force mode is disabled.

```text
CI run fails on PR
       │
       ▼
┌──────────────────────────────────┐
│  Analyze failed jobs             │
│  1. Infrastructure matchers      │  ← hardcoded in JS
│  2. Infrastructure log override  │  ← hardcoded in JS
│  3. Job log pattern matching     │  ← eng/test-retry-patterns.json (jobFailurePatterns)
│  4. TRX test output matching     │  ← eng/test-retry-patterns.json (testFailurePatterns)
└──────────────┬───────────────────┘
               │
               ▼
┌──────────────────────────────────┐
│  Safety rails                    │
│  • configured automatic rerun cap│
│  • ≤ 5 retryable jobs (default)  │
│  • PR must still be open         │
└──────────────┬───────────────────┘
               │
               ▼
   Rerun all failed jobs for the attempt
   + Post PR comment with matched jobs and reasons
```

The full analysis runs in four passes:

1. **Infrastructure annotation check** — Hardcoded patterns match runner failures, action download failures, and other known infrastructure errors from job annotations.
2. **Infrastructure log override** — For non-test-execution failures, job logs are checked against a hardcoded list of high-confidence network failure patterns (NuGet feed timeouts, GitHub API errors, etc.).
3. **Job log pattern matching** — For test execution failures (`Run tests*` step), the job log is matched against configurable `jobFailurePatterns` from [`eng/test-retry-patterns.json`](../../eng/test-retry-patterns.json).
4. **TRX test output matching** — If any test execution failures remain unmatched, the workflow downloads the `All-TestResults` artifact, parses the `.trx` files, and matches individual failed test output against configurable `testFailurePatterns` from the same config file.

Passes 1–2 are hardcoded because they target well-known infrastructure signatures that rarely change. Passes 3–4 are configurable because transient test failure patterns evolve as integrations are added or CI environments change.

## When does it trigger?

| Trigger | Behavior |
|---------|----------|
| **Automatic PR rerun** (`workflow_run` on `CI` completion) | Currently reruns all failed jobs without classification when the run has an open associated PR and is within the three-attempt cap. |
| **Automatic current-main rerun** (`workflow_run` on `CI` pushes to `main`) | Selectively reruns failed jobs once from source attempt 1 when every real failed job matches the narrow current-main infrastructure allowlist and the failed run is still the latest run for the current `main` SHA. |
| **Manual** (`workflow_dispatch`) | Enter a completed, failed PR `CI` run ID. It currently uses the same force-mode policy as automatic PR reruns; `dry_run` reports eligibility without requesting a rerun. |

The manual and automatic PR paths use the broad transient-failure analysis when force mode is disabled. Current-main reruns always use the separate policy below and do not consult the configurable PR job-log or TRX retry patterns.

The pull request attempt cap is defined in
[`auto-rerun/common.js`](../../.github/workflows/auto-rerun/common.js), and the
current-main cap is defined in [`auto-rerun/rerun-main.js`](../../.github/workflows/auto-rerun/rerun-main.js).
`defaultMaxRunAttempt` remains 3 for pull request runs. `mainMaxRunAttempt` is 1,
so only the first failed rolling `main` attempt can request a retry.

## Current-main selective retry policy

Current-main reruns are intentionally narrower than pull request reruns. After ignoring `Final Results` and `Tests / Final Test Results`, the run must have at least one failed job, no more than the configured retryable-job cap, and **every** remaining failed job must match one of these conditions:

1. A check-run annotation contains GitHub's hosted-runner-loss message: `The hosted runner lost communication with the server.` This does not apply to Hosting-1 or Hosting-5.
2. The job diagnostics contain `netaspireci.azurecr.io` within 500 characters of either `connect: connection refused` or `Connection reset by peer`, in either order.
3. The job diagnostics contain `mcr.microsoft.com` within 500 characters of HTTP 503 or `ServiceUnavailable`, in either order. `CONTAINER1014` without both the registry endpoint and a nearby service-unavailable status is not enough.
4. The job diagnostics contain Windows process initialization exit `-1073741502` or `0xC0000142`, at least one `Run tests*` or `Run nuget dependent tests*` step succeeded, and every failed step is a post-test reporting or cleanup step. A failed, skipped, or absent test execution step prevents this override.

Hosting-1 and Hosting-5 are always excluded from automatic current-main reruns because runner-loss signals in those shards can mask DCP or process-lifecycle failures. Generic messages such as `Operation timed out`, generic container failures, and the broader pull request/TRX patterns do not make a current-main run eligible.

The all-failures requirement is strict because GitHub's `rerun-failed-jobs` endpoint reruns the failed set for the attempt rather than accepting the matched job IDs as an atomic subset. One unmatched or excluded failure vetoes the whole run.

## Configuring test failure retry patterns

The file [`eng/test-retry-patterns.json`](../../eng/test-retry-patterns.json) defines patterns for identifying transient test failures that should trigger a rerun. Changes to this file go through normal PR review — the patterns are not user-supplied at runtime.

### File structure

```json
{
  "version": 1,
  "testFailurePatterns": [ ... ],
  "jobFailurePatterns": [ ... ]
}
```

- **`version`**: Schema version. Currently `1`. Reserved for future schema migrations.
- **`testFailurePatterns`**: Rules matched against individual failed test output from TRX files (pass 4).
- **`jobFailurePatterns`**: Rules matched against the full job log text for test execution failure jobs (pass 3).

### Adding a new pattern

> **Note**: The snippets below show only the relevant pattern entry. Add the pattern to the corresponding array (`testFailurePatterns` or `jobFailurePatterns`) in the full config file shown above.

**Example**: Tests in the Redis integration project occasionally fail with `ECONNRESET` due to container startup races. To automatically retry these:

```json
{
  "testFailurePatterns": [
    {
      "output": "ECONNRESET",
      "reason": "Transient network connection reset"
    }
  ]
}
```

If the pattern should only apply to a specific test project or test name:

```json
{
  "testFailurePatterns": [
    {
      "testProject": "Aspire.Hosting.Redis.Tests",
      "output": "ECONNRESET",
      "reason": "Redis container transient connection reset"
    }
  ]
}
```

For job-level log matching (e.g., a Windows-specific process init failure):

```json
{
  "jobFailurePatterns": [
    {
      "jobName": { "regex": ".*windows.*" },
      "output": "0xC0000142",
      "reason": "Windows process initialization failure"
    }
  ]
}
```

### Rule fields

#### Common to both rule types

| Field | Required | Type | Description |
|-------|----------|------|-------------|
| `reason` | **Yes** | string | Human-readable explanation shown in PR comments and workflow summary |
| `output` | No | string or `{"regex": "..."}` | Matched against the relevant text (test output or job log) |
| `enabled` | No | boolean | Defaults to `true`. Set `false` to temporarily disable a rule without deleting it |

#### `testFailurePatterns` fields

| Field | Type | Matched against |
|-------|------|-----------------|
| `testName` | string or `{"regex": "..."}` | Fully qualified test name from TRX (e.g., `Aspire.Hosting.Redis.Tests.RedisFunctionalTests.TestMethod`) |
| `testProject` | string or `{"regex": "..."}` | Test project name derived from TRX filename (e.g., `Aspire.Hosting.Redis.Tests`) |
| `output` | string or `{"regex": "..."}` | Concatenation of ErrorMessage + StackTrace + StdOut from the TRX test result (capped at 10KB per test) |

#### `jobFailurePatterns` fields

| Field | Type | Matched against |
|-------|------|-----------------|
| `jobName` | string or `{"regex": "..."}` | GitHub Actions job name (e.g., `Tests / Run ubuntu-latest Aspire.Hosting.Redis.Tests`) |
| `output` | string or `{"regex": "..."}` | Full job log text (capped at 256KB) |

### Matching semantics

- **Plain string**: Case-insensitive substring match (e.g., `"ECONNRESET"` matches `"Error: socket hang up: ECONNRESET"`).
- **`{"regex": "..."}`**: JavaScript (V8) regular expression, case-insensitive. Regex patterns are precompiled when the config is loaded; invalid patterns log a warning and the rule is disabled.
- **Within a rule**: All specified fields must match (**AND** logic). A rule with `testProject` + `output` requires both to match.
- **Across rules**: Any matching rule is sufficient (**OR** logic). A test that matches rule 1 or rule 2 is considered matched.
- **Deduplication**: If the same test (by fully qualified name) matches multiple rules, it appears once in the results with the first matching reason.

### What happens when a pattern matches

**Job log patterns (pass 3)**: The matched job is moved from "skipped" to "retryable" immediately during job classification.

**Test output patterns (pass 4)**: After TRX analysis, if *any* test matches a `testFailurePatterns` rule, *all* skipped test execution failure jobs are promoted to retryable. This is intentional — TRX files are a shared artifact that doesn't map 1:1 to individual jobs, and the existing `maxRetryableJobs` cap prevents runaway retries.

### Tips for writing good patterns

1. **Start specific, broaden if needed.** A pattern with `testProject` + `output` is safer than `output` alone. If the pattern is too broad, it may retry deterministic failures.
2. **Use `reason` to document the known transient failure.** The reason text appears in PR comments, so make it descriptive enough that a reviewer can understand *why* this pattern is retry-worthy.
3. **Prefer plain strings over regex.** Substring matching is simpler, easier to review, and less prone to surprising matches. Use regex only when you need anchoring, alternation, or wildcards.
4. **Temporarily disable before deleting.** Set `"enabled": false` rather than removing a rule. This preserves the pattern for future reference if the same transient issue recurs.
5. **Test your patterns locally.** The test suite validates config structure and regex compilation. Run the tests after modifying the config:
   ```bash
   dotnet test --project tests/Infrastructure.Tests/Infrastructure.Tests.csproj --no-launch-profile -- \
     --filter-class "*.AutoRerunTransientCiFailuresTests" \
     --filter-not-trait "quarantined=true" --filter-not-trait "outerloop=true"
   ```

### Verifying with dry run

To test how the workflow would analyze a specific failed CI run without triggering a rerun:

1. Go to **Actions** → **Auto-rerun transient CI failures** → **Run workflow**
2. Enter the failed CI run ID
3. Check **dry_run**
4. Inspect the workflow summary for matched jobs, matched tests, and whether a rerun would have been requested

## Safety rails

The policies deliberately apply different rails:

| Rail | Scope | Detail |
|------|-------|--------|
| **Attempt limit** | All paths | Pull request source attempts use `defaultMaxRunAttempt` (3), defined in [`auto-rerun/common.js`](../../.github/workflows/auto-rerun/common.js). Current-`main` source attempts use `mainMaxRunAttempt` (1), defined in [`auto-rerun/rerun-main.js`](../../.github/workflows/auto-rerun/rerun-main.js), allowing exactly one retry. |
| **Open PR** | Pull request and manual paths | At least one associated pull request must still be open during analysis, including manual dry runs and force mode. Execution rechecks that an associated PR remains open immediately before requesting a rerun. |
| **Live source attempt** | Pull request and manual execution | Immediately before the write, the selected run must still be a completed failure at the source attempt analyzed earlier. |
| **Retryable job cap** | Normal PR analysis and current-main | At least 1 but no more than 5 retryable jobs (default). Pull request attempts after the first use the existing stricter count rule. Current-main only considers source attempt 1 and requires every real failed job to match. Force mode bypasses this cap because it does not enumerate jobs. |
| **Non-aggregator** | Normal PR analysis and current-main | Aggregator jobs (`Final Results`, `Tests / Final Test Results`) are excluded from analysis. Force mode bypasses analysis and lets GitHub rerun the failed set. |
| **Mixed-failure veto** | Normal PR analysis | A job with both a test execution failure (`Run tests*`) and unrelated transient post-step noise is *not* retried on infrastructure grounds alone — the test execution failure must match a pattern (pass 3 or 4) to qualify. |

When a rerun is requested, GitHub reruns **all** failed jobs for that attempt — not just the matched ones. This is a GitHub API constraint (there is no API for atomically rerunning a subset of failed jobs). The matched-job count and safety rails are the eligibility gate; once eligible, the rerun covers the full failed set.

Current-main reruns have additional fail-closed rails:

- every non-aggregate failed job must match the narrow current-main allowlist
- Hosting-1 and Hosting-5 are never eligible for automatic current-main reruns
- the live run must still be a `push` of `.github/workflows/ci.yml` on `main` for the trusted SHA
- the live run must still be completed with a failure conclusion
- the live attempt, run number, and workflow identity must equal the values validated before polling
- `refs/heads/main` must still point to the failed SHA
- no main run for the same workflow may have a greater run number
- the source attempt must not exceed the `mainMaxRunAttempt` policy

Rerun decisions and skip reasons are reported in the workflow logs and job summary. If GitHub rejects the rerun request, the workflow fails directly so the request failure remains visible.

## Force-rerun all failures (`FORCE_RERUN_ALL`)

> **For-now behavior:** the workflow reruns the failed CI jobs on any failed run with an open PR, without analyzing the failure. This stays in place until CI auto-rerun patterns are improved (e.g. agents curating the transient-failure rules). Disable it by flipping the flag (see below); the classification rules are kept intact behind it.

The pull request and manual-dispatch paths currently run in **force mode**, enabled by the `FORCE_RERUN_ALL: 'true'` environment variable on the analysis job in [`auto-rerun-transient-ci-failures.yml`](../../.github/workflows/auto-rerun-transient-ci-failures.yml). The analyzed policy carries this flag into execution, so it has one source of truth. Force mode is a **short-circuit**: as soon as the run is eligible (failed run, attempt within the configured cap, open PR), it requests a rerun of the failed jobs and stops. It does not look at individual jobs at all. The current-main path does not use force mode.

**Force mode bypasses:**

1. **All job analysis** — the workflow does not enumerate jobs, fetch annotations, download logs, parse TRX files, or run any of the four classification passes. The annotation-allowlist, infrastructure/network-log-override, job-log-pattern, and TRX test-pattern analysis are all skipped. No per-job decision is made.
2. **The retryable-job-count cap** — the `≤ 5 retryable jobs` rail is not applied (there is no job list to count).

Because the rerun uses GitHub's `rerun-failed-jobs` API — which reruns **all** non-successful jobs for the attempt regardless of any job list — the short-circuit reruns exactly what the normal path would have, without doing the analysis to get there.

**Force mode keeps:**

- **The open-PR requirement** — a rerun only fires for a run that has a currently-open associated PR. Runs with no associated PR, or where every associated PR is closed/merged, are still skipped. There is no value in spending CI on an inactive PR, so force mode does not bypass this.
- **The attempt cap** — pull request reruns still stop after the `defaultMaxRunAttempt` policy is exceeded.
- **Failed-run-only triggering** — automatic execution only fires on `workflow_run.conclusion == 'failure'`, and manual execution validates that the selected run is completed with conclusion `failure`. A `cancelled` run (which is what you get when a run is cancelled, or when fail-fast cancels siblings) has conclusion `cancelled`, not `failure`, so it never triggers a rerun.

The classification rules and [`eng/test-retry-patterns.json`](../../eng/test-retry-patterns.json) config are left fully intact; force mode is gated behind an optional `forceRerunAll` flag (default `false`), so the normal behavior is preserved when it is off.

**To disable:** set `FORCE_RERUN_ALL: 'true'` to `'false'` (or remove the env var) on the analysis job in the YAML.

### PR association

The workflow identifies the associated PR from the `workflow_run` event payload. When GitHub's payload omits `pull_requests` (which can happen for fork-based PRs), the workflow falls back to matching by `head_repository.owner.login`, `head_branch`, and optionally `head_sha`. The fallback requires exactly one matching PR to proceed — ambiguous matches are skipped.

## Architecture and file layout

| File | Role |
|------|------|
| [`.github/workflows/auto-rerun-transient-ci-failures.yml`](../../.github/workflows/auto-rerun-transient-ci-failures.yml) | Two thin callers: read-only analysis and write-capable execution |
| [`.github/workflows/auto-rerun-transient-ci-failures.js`](../../.github/workflows/auto-rerun-transient-ci-failures.js) | Dispatcher: validates source context and selects the PR or current-main policy for each phase |
| [`.github/workflows/auto-rerun/rerun-pull-request.js`](../../.github/workflows/auto-rerun/rerun-pull-request.js) | PR/manual policy: force mode, broad classification, configurable patterns, TRX artifact analysis, and rerun execution |
| [`.github/workflows/auto-rerun/rerun-main.js`](../../.github/workflows/auto-rerun/rerun-main.js) | Current-main policy: narrow allowlist, one retry, final freshness checks, and rerun execution |
| [`.github/workflows/auto-rerun/common.js`](../../.github/workflows/auto-rerun/common.js) | Shared diagnostics, eligibility primitives, failed-job rerun request, and reporting |
| [`.github/workflows/auto-rerun/github.js`](../../.github/workflows/auto-rerun/github.js) | Shared attempt-scoped job, annotation, and log access |
| [`eng/test-retry-patterns.json`](../../eng/test-retry-patterns.json) | Configuration: test failure and job failure patterns |
| [`tests/.../auto-rerun-transient-ci-failures.harness.js`](../../tests/Infrastructure.Tests/WorkflowScripts/auto-rerun-transient-ci-failures.harness.js) | Node.js test harness: bridges C# xUnit tests to the JS module functions |
| [`tests/.../AutoRerunTransientCiFailuresTests.cs`](../../tests/Infrastructure.Tests/WorkflowScripts/AutoRerunTransientCiFailuresTests.cs) | C# test class: behavior-focused tests covering all matcher logic |

The YAML passes GitHub context, the manual run ID, dry-run and force-mode flags,
and the phase to the same JavaScript dispatcher. The dispatcher serializes the
PR or main policy's analysis object into workflow outputs. Each policy module
owns its eligibility gates, live-run lookups, validation, and request error
handling; `common.js` contains shared rerun-request and reporting operations,
while `github.js` provides shared attempt-scoped job, annotation, and log access.

The two jobs are separate to keep the analysis job's `GITHUB_TOKEN` read-only.
GitHub Actions permissions are fixed per job: permission levels cannot use
expressions, and a token cannot be escalated between steps. Only the execution
job gets rerun and PR-comment write permissions. See
[GitHub's job permissions reference](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax#jobsjob_idpermissions).

Analysis emits a serialized decision. Execution selects the same policy from
that decision, checks eligibility, and invokes its handler. The PR handler
rechecks open PRs and the exact source attempt; the main handler revalidates the
live run, current `main` SHA, and run-list ordering immediately before the shared
failed-job rerun request. GitHub's API has no atomic expected-attempt
precondition, so these final reads minimize but cannot eliminate the
check-to-write window.

## Tests

The automated tests live in `tests/Infrastructure.Tests/WorkflowScripts/AutoRerunTransientCiFailuresTests.cs`.

They are intentionally behavior-focused rather than regex-focused:

- they use representative fixtures for each supported behavior
- they keep representative job and step fixtures anchored to the current CI workflow names so matcher coverage does not drift from the implementation
- they cover the mixed-failure veto and ignored-step override explicitly
- they invoke the dispatcher and policy modules through the Node.js harness, including manual selection, attempt caps, execution freshness checks, dry-run handling, and rerun-request outcomes
- they validate the `eng/test-retry-patterns.json` config structure and regex compilation in Node.js (V8)
- they test pattern matching functions (substring, regex, AND/OR logic, disabled rules)
- they test TRX parsing, output capping, XML entity decoding, and the `analyzeTrxFiles` deduplication
- they test the `promoteTestExecutionFailureJobs` promotion logic and `selectTestResultsArtifact` selection
- they test the `analyzeFailedJobs` integration with `retryPatternsConfig` for job log pattern matching
- they exercise dispatcher routing, manual dry-run handling, PR force-mode job-read bypass, and main freshness checks across both phases

### Running the tests

```bash
dotnet test --project tests/Infrastructure.Tests/Infrastructure.Tests.csproj --no-launch-profile -- \
  --filter-class "*.AutoRerunTransientCiFailuresTests" \
  --filter-not-trait "quarantined=true" --filter-not-trait "outerloop=true"
```

These tests require Node.js to be installed (the harness invokes `node` to run the JS module).
