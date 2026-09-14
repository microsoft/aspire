# CI failure analysis and automatic reruns

This document explains how failed pull-request CI runs are analyzed and when an automatic rerun is requested.

## How it works

When a `CI` pull-request run fails, the [Analyze CI Failure](../../.github/workflows/analyze-ci-failure.md) agentic workflow collects the failed jobs, annotations, focused logs, test-result artifacts, PR metadata, changed files, and known transient patterns from [`eng/test-retry-patterns.json`](../../eng/test-retry-patterns.json). Runner startup failures and failed jobs without a failed step are retained; aggregate result jobs are excluded because they contain no independent diagnostic evidence.

The same analysis produces:

- a classification for every failed job and extracted test;
- an overall verdict;
- an explicit `rerun.eligible` decision and reason;
- the PR comment describing the evidence and decision.

There is no separate PR CI rerun classifier. The analysis result is the sole source of the rerun decision.

```text
Failed PR CI run
       |
       v
Analyze CI Failure collects revision-bound evidence
       |
       v
Agent classifies every failure and records rerun.eligible
       |
       +--> publish validated PR comment
       |
       +--> rerun failed jobs when eligible and still current
```

Scheduled `Outerloop Tests` runs use a separate unconditional workflow because they have no associated PR or analysis result. See [Auto-rerun outerloop failures](auto-rerun-outerloop-failures.md).

## Verdicts and reruns

| Verdict | Meaning | Automatic rerun |
|---------|---------|-----------------|
| `transient-infra` | Every failure is caused by transient infrastructure | Yes for open PR runs on attempts 1-3 |
| `flaky-test` | Every failure is transient and at least one is a likely flaky test | Yes for open PR runs on attempts 1-3 |
| `code-issue` | PR changes caused a build, API, lint, or configuration failure | No |
| `pr-test-failure` | PR changes caused a deterministic test regression | No |
| `mixed` | Multiple known categories coexist | Yes for open PR runs on attempts 1-3 |
| `unknown` | Evidence is insufficient or contradictory | Yes for open PR runs on attempts 1-3 |

A partial evidence collection does not automatically force `unknown`, but missing evidence needed for a confident classification does. Unknown results are rerun within the normal attempt limit to give CI another chance to produce conclusive evidence. Mixed results are also rerun so their transient failures get another attempt, even though GitHub reruns every failed job and the non-transient failures may remain. A wholly non-transient `code-issue` or `pr-test-failure` result is not rerun.

## Safety checks

The rerun safe-output job treats the analysis as a decision, not as trusted execution context. The JavaScript helper owns deterministic validation of the analysis, collector-owned context, evidence metadata, and safe-output request. The workflow retains orchestration, live GitHub state checks, and side effects. Before calling GitHub's rerun API, it independently verifies:

- the safe-output request, analysis JSON, and collector-owned context identify the same run and PR;
- the verdict is `transient-infra`, `flaky-test`, `mixed`, or `unknown` and `rerun.eligible` is `true`;
- the source is a failed `CI` pull-request run;
- the source attempt is 1, 2, or 3, allowing at most three automatic reruns;
- the run attempt and analyzed commit SHA still match the collected evidence;
- the PR is still open and still points at the analyzed commit.

If any check fails, no rerun is requested. Publication applies the same verdict-to-decision consistency rule before posting the result.

The aggregate `All-TestResults` artifact is selected by exact name, newest creation time, and artifact ID. Collection rejects artifacts larger than 100 MB, processes at most 200 TRX files, skips TRX files larger than 50 MB, ignores links or paths outside the extraction root, and applies time limits to archive extraction and TRX parsing. Skipped evidence is recorded as an evidence gap for the analysis.

## Known transient patterns

[`eng/test-retry-patterns.json`](../../eng/test-retry-patterns.json) supplies reviewed test and job failure patterns as evidence to the analysis. A match supports a transient classification, but the analysis must still consider all failed jobs, test output, evidence gaps, and PR changes before making the run-level decision.

Patterns may use a case-insensitive string or a JavaScript-style regular expression object. Keep patterns specific and provide a concise `reason` describing why the failure is transient.

## Manual analysis

The workflow supports `workflow_dispatch` with a failed `CI` run ID. Manual and automatic invocations use the same collection, classification, validation, publication, and rerun rules. Set the documented `dry_run` input to `true` to collect, analyze, and publish the result while suppressing the final rerun API call. The workflow summary records when an otherwise eligible rerun was suppressed.

## Files

| File | Role |
|------|------|
| [`.github/workflows/analyze-ci-failure.md`](../../.github/workflows/analyze-ci-failure.md) | Agentic workflow source, classification instructions, publication, and rerun execution |
| [`.github/workflows/analyze-ci-failure.lock.yml`](../../.github/workflows/analyze-ci-failure.lock.yml) | Generated executable workflow |
| [`.github/workflows/analyze-ci-failure.js`](../../.github/workflows/analyze-ci-failure.js) | Deterministic publication and rerun validation, evidence redaction, test-result extraction, and comment rendering |
| [`eng/test-retry-patterns.json`](../../eng/test-retry-patterns.json) | Reviewed transient-pattern evidence |
| [`tests/Infrastructure.Tests/WorkflowScripts/AnalyzeCiFailureWorkflowTests.cs`](../../tests/Infrastructure.Tests/WorkflowScripts/AnalyzeCiFailureWorkflowTests.cs) | Workflow contract and helper behavior tests |

## Validation

Compile the agentic workflow and run its focused tests:

```bash
gh aw compile .github/workflows/analyze-ci-failure.md --strict

dotnet test --project tests/Infrastructure.Tests/Infrastructure.Tests.csproj --no-launch-profile -- \
  --filter-class "*.AnalyzeCiFailureWorkflowTests" \
  --filter-not-trait "quarantined=true" \
  --filter-not-trait "outerloop=true"
```
