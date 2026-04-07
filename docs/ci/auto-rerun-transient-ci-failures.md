# Auto-rerun transient CI failures

This document describes the current behavior contract for `.github/workflows/auto-rerun-transient-ci-failures.yml`.

## Overview

The workflow analyzes failed `CI` pull request runs, identifies retry-safe transient infrastructure failures, uses those matched jobs as the eligibility signal, requests GitHub to rerun all failed jobs for the failed attempt through the run-level failed-job rerun API, and comments on the open pull request with the rerun details.

GitHub does not provide an API for atomically rerunning multiple arbitrary failed jobs within the same workflow run. Once the matcher finds one or more retry-safe jobs and all safety rails pass, the workflow sends a single run-level `rerun-failed-jobs` request for the source run. That rerun attempt includes all failed jobs for that attempt, not only the matched jobs.

When GitHub's `workflow_run` payload omits `pull_requests` for a failed PR run, the workflow falls back to resolving the source pull request from the CI run's `head_repository.owner.login` and `head_branch`. The fallback proceeds only when that lookup yields exactly one matching pull request, so ambiguous branch reuse still results in a skip. When the `workflow_run` payload also includes a `head_sha`, the fallback further filters candidate pull requests to those whose head SHA matches that value, which can also cause the lookup to skip if the branch still matches but the pull request head has advanced since the analyzed run.

It is intentionally conservative:

- it does not treat every failed run as rerun-eligible; a rerun is requested only when at least one matched job satisfies the retry-safe rules and the safety rails pass
- it treats mixed deterministic failures plus transient post-step noise as non-retryable by default
- it keeps `workflow_dispatch` behind the same matcher and safety rails as automatic execution, with an optional dry-run mode for inspection-only runs

## Matcher behavior

- Retry jobs with no failed steps only when their annotations contain an explicit job-level infrastructure signature.
- Retry jobs whose failed step is on the retry-safe allowlist only when their annotations also contain a transient infrastructure signature.
- Ignore aggregator jobs such as `Final Results` and `Tests / Final Test Results`.
- Skip jobs whose failed steps are outside the retry-safe allowlist, even if their annotations contain generic failure text.
- Keep the mixed-failure veto: if an ignored step such as `Run tests*` failed, do not rerun the job based only on unrelated transient post-step noise.
- Allow a narrow override when an ignored failed step is paired with a high-confidence job-level infrastructure annotation such as runner loss or action-download failure.
- Allow a narrow override for Windows jobs whose failures are limited to post-test cleanup or upload steps when the annotations report process initialization failure `-1073741502` (`0xC0000142`).
- Allow a narrow log-based override for non-test-execution failures when the job log shows high-confidence infrastructure network failures against approved `dnceng` public feeds, `builds.dotnet.microsoft.com`, `api.github.com`, or `github.com`.

## Safety rails

- `workflow_dispatch` can inspect any `CI` workflow run by ID and request reruns when the same retry-safety rules are satisfied.
- `workflow_dispatch` also exposes an optional `dry_run` input so manual runs can produce the analysis summary without sending rerun requests.
- Dry-run summaries still report whether the analyzed run would be eligible to rerun if dry run were disabled; the execution gate remains suppressed separately.
- Automatic rerun triggers only when the run attempt is 3 or fewer (`run_attempt <= 3`), allowing up to 2 automatic reruns (3 total attempts) per PR run.
- Automatic rerun requires at least one retryable job.
- Automatic rerun is suppressed when matched jobs exceed the configured cap (default: 5).
- For attempts after the first (`run_attempt > 1`), a stricter cap applies: rerun is suppressed unless the matched job count is strictly less than the configured cap (for example, fewer than 5 jobs by default). Aggregator jobs such as `Final Results` and `Tests / Final Test Results` are excluded from this count.
- Before issuing reruns, the workflow confirms that at least one associated pull request is still open.
- When a rerun is requested, the workflow sends one run-level failed-job rerun request for the analyzed source run rather than one request per matched job.
- The matched-job count limits remain an eligibility gate: once the run is eligible, GitHub reruns all failed jobs for that attempt.
- The workflow summary clearly states whether reruns were skipped, are eligible, or were requested, and links to the analyzed workflow run.
- When reruns are requested, the rerun summary also links to both the failed attempt and the rerun attempt, plus any posted pull request comments.
- After successful rerun requests, the workflow comments on the open associated pull request that the failed jobs in the CI workflow are being rerun, with links to the failed attempt, the rerun attempt, and the matched failed-attempt jobs and retry reasons that made the run eligible.

## Tests

The automated tests for this workflow live in `tests/Infrastructure.Tests/WorkflowScripts/AutoRerunTransientCiFailuresTests.cs`.

Those tests are intentionally behavior-focused rather than regex-focused:

- they use representative fixtures for each supported behavior
- they keep representative job and step fixtures anchored to the current CI workflow names so matcher coverage does not drift from the implementation
- they cover the mixed-failure veto and ignored-step override explicitly
- they keep only a minimal set of YAML contract checks for safety rails such as the optional manual `dry_run` override, up-to-three-attempt automatic reruns, enabling manual reruns through `workflow_dispatch`, and gating the rerun job on `rerun_execution_eligible`
