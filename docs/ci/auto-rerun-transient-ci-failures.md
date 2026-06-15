# Auto-rerun transient CI failures

This document explains how the automatic CI rerun system works and how to configure it.

## How it works at a glance

When a `CI` pull request run fails on GitHub Actions, a companion workflow automatically analyzes the failure, determines whether it was caused by transient infrastructure or test issues, and — if safe — requests GitHub to rerun the failed jobs. It also posts a comment on the PR explaining what it did and why.

> **Currently in force mode.** The workflow is temporarily configured to skip the analysis below and rerun the failed jobs on **any** failed run with an open PR. See [Force-rerun all failures](#force-rerun-all-failures-force_rerun_all). The rest of this section describes the normal (analysis) behavior that force mode bypasses.

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
│  • attempts 1–3 → up to 3 reruns │
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
| **Automatic** (`workflow_run` on `CI` completion) | Runs whenever a `CI` pull request workflow concludes with failure. No manual action needed. |
| **Manual** (`workflow_dispatch`) | Enter a `CI` run ID to analyze. Supports a `dry_run` option that produces the analysis summary without actually requesting a rerun. |

Both trigger paths use the same analysis and safety rails. The only difference is how the source run is identified.

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

The workflow is intentionally conservative. All of these conditions must be met for a rerun to be requested:

| Rail | Detail |
|------|--------|
| **Attempt limit** | The source run must be on attempt ≤ 3. Reruns are triggered from source attempts 1, 2, and 3, so a run gets up to 3 automatic reruns (4 total attempts) before the cap stops further reruns. |
| **Retryable job cap** | At least 1 but no more than 5 retryable jobs (default). On attempts > 1, the cap is stricter: the count must be *strictly less than* 5. |
| **Open PR** | At least one associated pull request must still be open. |
| **Non-aggregator** | Aggregator jobs (`Final Results`, `Tests / Final Test Results`) are excluded from analysis. |
| **Mixed-failure veto** | A job with both a test execution failure (`Run tests*`) and unrelated transient post-step noise is *not* retried on infrastructure grounds alone — the test execution failure must match a pattern (pass 3 or 4) to qualify. |

When a rerun is requested, GitHub reruns **all** failed jobs for that attempt — not just the matched ones. This is a GitHub API constraint (there is no API for atomically rerunning a subset of failed jobs). The matched-job count and safety rails are the eligibility gate; once eligible, the rerun covers the full failed set.

## Force-rerun all failures (`FORCE_RERUN_ALL`)

> **For-now behavior:** the workflow reruns the failed CI jobs on any failed run with an open PR, without analyzing the failure. This stays in place until CI auto-rerun patterns are improved (e.g. agents curating the transient-failure rules). Disable it by flipping the flag (see below); the classification rules are kept intact behind it.

The workflow currently runs in **force mode**, enabled by the `FORCE_RERUN_ALL: 'true'` environment variable set on both jobs in [`auto-rerun-transient-ci-failures.yml`](../../.github/workflows/auto-rerun-transient-ci-failures.yml). Force mode is a **short-circuit**: as soon as the run is eligible (failed run, attempt ≤ 3, open PR), it requests a rerun of the failed jobs and stops. It does not look at individual jobs at all.

**Force mode bypasses:**

1. **All job analysis** — the workflow does not enumerate jobs, fetch annotations, download logs, parse TRX files, or run any of the four classification passes. The annotation-allowlist, infrastructure/network-log-override, job-log-pattern, and TRX test-pattern analysis are all skipped. No per-job decision is made.
2. **The retryable-job-count cap** — the `≤ 5 retryable jobs` rail is not applied (there is no job list to count).

Because the rerun uses GitHub's `rerun-failed-jobs` API — which reruns **all** non-successful jobs for the attempt regardless of any job list — the short-circuit reruns exactly what the normal path would have, without doing the analysis to get there.

**Force mode keeps:**

- **The open-PR requirement** — a rerun only fires for a run that has a currently-open associated PR. Runs with no associated PR, or where every associated PR is closed/merged, are still skipped. There is no value in spending CI on an inactive PR, so force mode does not bypass this.
- **The attempt cap** — reruns still only fire from source attempts 1–3 (up to 3 automatic reruns / 4 total attempts). This is unchanged.
- **Failed-run-only triggering** — the workflow only fires on `workflow_run.conclusion == 'failure'`. A `cancelled` run (which is what you get when a run is cancelled, or when fail-fast cancels siblings) has conclusion `cancelled`, not `failure`, so it never triggers a rerun. Cancellation is excluded for free by the trigger; force mode adds nothing here.

The classification rules and [`eng/test-retry-patterns.json`](../../eng/test-retry-patterns.json) config are left fully intact; force mode is gated behind an optional `forceRerunAll` flag (default `false`), so the normal behavior is preserved when it is off.

**To disable:** set `FORCE_RERUN_ALL: 'true'` to `'false'` (or remove the env var) on both jobs in the YAML.

### PR association

The workflow identifies the associated PR from the `workflow_run` event payload. When GitHub's payload omits `pull_requests` (which can happen for fork-based PRs), the workflow falls back to matching by `head_repository.owner.login`, `head_branch`, and optionally `head_sha`. The fallback requires exactly one matching PR to proceed — ambiguous matches are skipped.

## Architecture and file layout

| File | Role |
|------|------|
| [`.github/workflows/auto-rerun-transient-ci-failures.yml`](../../.github/workflows/auto-rerun-transient-ci-failures.yml) | YAML workflow: orchestration, GitHub API calls, artifact download, TRX file I/O |
| [`.github/workflows/auto-rerun-transient-ci-failures.js`](../../.github/workflows/auto-rerun-transient-ci-failures.js) | JavaScript module: all testable logic — pattern matching, job classification, TRX parsing, promotion, summary formatting |
| [`eng/test-retry-patterns.json`](../../eng/test-retry-patterns.json) | Configuration: test failure and job failure patterns |
| [`tests/.../auto-rerun-transient-ci-failures.harness.js`](../../tests/Infrastructure.Tests/WorkflowScripts/auto-rerun-transient-ci-failures.harness.js) | Node.js test harness: bridges C# xUnit tests to the JS module functions |
| [`tests/.../AutoRerunTransientCiFailuresTests.cs`](../../tests/Infrastructure.Tests/WorkflowScripts/AutoRerunTransientCiFailuresTests.cs) | C# test class: behavior-focused tests covering all matcher logic |

The JS module is intentionally separated from the YAML workflow so that all classification, matching, and formatting logic can be tested via the Node.js harness without mocking GitHub APIs. The YAML workflow only handles orchestration: fetching jobs, downloading artifacts, and calling the GitHub rerun API.

## Tests

The automated tests live in `tests/Infrastructure.Tests/WorkflowScripts/AutoRerunTransientCiFailuresTests.cs`.

They are intentionally behavior-focused rather than regex-focused:

- they use representative fixtures for each supported behavior
- they keep representative job and step fixtures anchored to the current CI workflow names so matcher coverage does not drift from the implementation
- they cover the mixed-failure veto and ignored-step override explicitly
- they keep only a minimal set of YAML contract checks for safety rails such as the optional manual `dry_run` override, up-to-three-attempt automatic reruns, enabling manual reruns through `workflow_dispatch`, and gating the rerun job on `rerun_execution_eligible`
- they validate the `eng/test-retry-patterns.json` config structure and regex compilation in Node.js (V8)
- they test pattern matching functions (substring, regex, AND/OR logic, disabled rules)
- they test TRX parsing, output capping, XML entity decoding, and the `analyzeTrxFiles` deduplication
- they test the `promoteTestExecutionFailureJobs` promotion logic and `selectTestResultsArtifact` selection
- they test the `analyzeFailedJobs` integration with `retryPatternsConfig` for job log pattern matching

### Running the tests

```bash
dotnet test --project tests/Infrastructure.Tests/Infrastructure.Tests.csproj --no-launch-profile -- \
  --filter-class "*.AutoRerunTransientCiFailuresTests" \
  --filter-not-trait "quarantined=true" --filter-not-trait "outerloop=true"
```

These tests require Node.js to be installed (the harness invokes `node` to run the JS module).
