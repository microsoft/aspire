# Analyze CI failures

The [`Analyze CI Failure`](../../.github/workflows/analyze-ci-failure.md)
workflow uses Copilot to classify failed `CI` workflow runs as transient,
pull-request-caused, or a repository break on `main`.

The workflow may post analysis on a pull request, rerun transient failures,
persist recurring causes, or create a `[Main CI Failure]` issue. These effects
are allowed only after deterministic validation against data collected from
GitHub.

## Supported runs

Automatic analysis runs only when a `CI` push to `main` fails on the attempt
immediately after the configured automatic-rerun source-attempt cap. If GitHub
rejects an otherwise eligible rerun request, the rerun workflow dispatches
fallback analysis for that early attempt instead of leaving the failure
unanalyzed. Manual dispatch can analyze a specific run, but an early attempt
can publish only when the dispatch identifies the failed rerun request. The
collector accepts `main` push runs and pull-request runs; other workflow
paths, events, and branches are rejected or skipped.

The collector pins the run attempt from the `workflow_run` event so a later
rerun cannot change the evidence being analyzed. Run ID, attempt, workflow
path, event, branch, SHA, and failed jobs come from GitHub rather than from
agent output.

The [CI auto-rerun workflow](auto-rerun-transient-ci-failures.md) handles
current-main reruns independently of this analysis queue. The shared
`defaultMaxRunAttempt` value in
[`auto-rerun-transient-ci-failures.js`](../../.github/workflows/auto-rerun-transient-ci-failures.js)
controls the last source attempt that may request a rerun; the analyzer derives
its final attempt as the next attempt. A successful retry needs no failure
analysis. If the retry request fails before that final attempt, the auto-rerun
workflow remains failed for visibility and dispatches the analyzer with the
failed CI run ID. The analyzer accepts that early attempt only while its failed
SHA is still current `main`, its attempt is unchanged, and no newer main CI run
supersedes it. The separate `ci_failure_tracker` in `ci.yml` can still report
the first failed push.

## Attribution

For a pull-request run, PR-directed effects require exactly one subject PR.
The workflow first uses the run's PR association, then bounded commit and fork
branch fallbacks. The fork fallback requires the failed run's exact head SHA.
Missing or ambiguous associations do not produce a guessed subject.

For a failed `main` run, the PR associated with the failed push is context, not
the presumed cause. The workflow considers every merge since the most recent
successful `main` run. Candidate attribution requires a complete GitHub
comparison whose relation is `ahead`; identical, behind, diverged, malformed,
or incomplete comparisons are non-attributable. A candidate commit must also
map to exactly one PR merged into `main`.

PR comments and pull-request reruns require an unambiguous subject PR that is
still open and unlocked immediately before the mutation. Automatic `main`
reruns do not require a subject PR or agent classification. Run-scoped
recurring-cause persistence can continue without an actionable PR, but its PR
occurrence context is recorded as unavailable when the subject cannot be
identified.

## Agent trust boundary

Logs, annotations, pull-request metadata, prior causes, and failed-test
evidence are collected before analysis. The agent receives bounded evidence
and proposes classifications, causes, a verdict, and rerun requests.
Job evidence includes every step conclusion, including skipped steps, and
preserves bounded GitHub CLI diagnostics when complete job-log retrieval
fails. Classification must use the current failed phase and diagnostic before
matching a recurring cause, so a failed prerequisite cannot be treated as a
test failure merely because it occurred in a test job.

Before any side effect, the
[`analyze-ci-failure-validation.sh`](../../.github/workflows/analyze-ci-failure-validation.sh)
boundary rebuilds trusted run, attempt, SHA, PR, failed-job, test, and cause
identity from collected artifacts. It rejects output that adds, omits, or
rebinds trusted records. Published diagnostics are reconstructed from trusted
evidence rather than copied from agent output.

The automatic `main` rerun is deterministic and does not consume agent output.
Immediately before the failed-job rerun request, it re-fetches the workflow
run, `refs/heads/main`, and the workflow's main run list. It fails closed unless
the trusted run is still the current `main` SHA, no newer main CI run has a
greater run number, the attempt is unchanged, and the source attempt does not
exceed the shared `defaultMaxRunAttempt` policy. Rerun decisions and skip
reasons appear in that workflow's logs and job summary; they are not stored on
the analysis memory branch. The analyzer calls
[`analyze-ci-failure-terminal.sh`](../../.github/workflows/analyze-ci-failure-terminal.sh)
before collecting evidence, before publication, and before updating each cause
issue. The helper derives the final attempt from `defaultMaxRunAttempt` and
checks that attempt is completed and failed for the current `main` SHA, or that
an earlier failed attempt was dispatched after its rerun request failed. The
fallback dispatch includes the trusted attempt and SHA, and collection fetches
that immutable attempt rather than the run's latest attempt. Both paths require
the live attempt to remain unchanged and not be superseded by a newer main CI
run. The helper returns 2 to skip stale or ineligible analysis; failed
verification stops the workflow. Stale runs are skipped rather than canceled
through the shared CI concurrency key, which would also cancel unrelated main
builds.

External and agent-supplied text is bounded and rendered inert before it is
used in workflow diagnostics, Markdown comments, or issue bodies.
`[Main CI Failure]` issue titles and diagnostic text are publisher-owned and
derived from trusted run and SHA context. Agent-proposed main-breakage titles
and patterns remain matching metadata and are not published as attribution.
Existing matching issues are migrated to the trusted rendering while retaining
their occurrence history and operator notes appended after the generated
details. Unsupported legacy body shapes are left intact rather than blocking
other publication work, but their titles are still migrated.

## Failed-test provenance

Each failed test job's logs artifact is selected within the analyzed run and
attempt, downloaded by artifact ID, and extracted separately. TRX results from
the reusable test workflow and `mocha.json` results from VS Code extension E2E
shards are stamped with the corresponding GitHub Actions job name.

Complete evidence requires the agent to report exactly the same unique
`{test, job}` records. Diagnostic rebinding and flaky-cause validation use that
exact pair, so a real test from one job cannot be attributed to another failed
job. Every flaky `{test, job}` pair must also be covered by a matching cause.
The ten-cause budget fails closed rather than silently dropping distinct flaky
test identities.

Published flaky-test diagnostics use the bounded error, stack trace, standard
output, and standard error read from trusted test artifacts. Agent-provided
copies of those fields are replaced before comments or persistent records are
rendered. Extension E2E diagnostics provide the Mocha error and stack trace;
their reporter does not capture standard output or standard error per test.

GitHub's artifact API does not expose a producer job ID. The selector therefore
uses the job and artifact naming contract in
[`run-tests.yml`](../../.github/workflows/run-tests.yml) and
[`extension-e2e-tests.yml`](../../.github/workflows/extension-e2e-tests.yml).
Missing, oversized, ambiguous, or malformed required TRX artifacts make test
evidence unavailable rather than producing an empty successful result.
Extension E2E diagnostics are optional because setup can fail before the test
harness creates them, and recordings can exceed the analyzer's bounded download
budget. Missing, oversized, or unavailable extension diagnostics, and valid
diagnostics without `mocha.json`, contribute no trusted failed-test records; the
job remains classifiable from its trusted step conclusions and sanitized logs.
Malformed or ambiguous extension results still fail closed.

## Side-effect gates

- Agent-requested reruns require validated transient failures from the same run
  attempt and available test evidence. Pull-request reruns additionally require
  the subject PR to remain open and unlocked.
- Automatic `main` reruns are independent of the verdict and require all
  current-SHA, supersession, attempt, and cap checks to pass immediately before
  the write.
- Failures attributed to one PR are reported on that PR only while it remains
  open and unlocked.
- Deterministic `main` failures are reported through `[Main CI Failure]`
  issues.
- Shared recurring-cause and issue publication is serialized. Cause counts,
  artifact sizes, extracted test data, comments, and issue bodies have explicit
  budgets.

## Implementation and validation

The source workflow is
[`analyze-ci-failure.md`](../../.github/workflows/analyze-ci-failure.md). Its
generated executable workflow is
[`analyze-ci-failure.lock.yml`](../../.github/workflows/analyze-ci-failure.lock.yml).
Collection and persistence helpers live beside the workflow as
`analyze-ci-failure-*.sh`; final output validation is in
`analyze-ci-failure-validation.sh`. The
[`analyze-ci-failure-terminal.sh`](../../.github/workflows/analyze-ci-failure-terminal.sh)
helper guards final-attempt and failed-rerun fallback `main` analysis before
collection and publication.

Focused coverage lives in
[`AnalyzeCiFailureWorkflowTests`](../../tests/Infrastructure.Tests/WorkflowScripts/AnalyzeCiFailureWorkflowTests.cs).
When changing the workflow, helpers, or the job/artifact naming contract, keep
the source workflow, generated lock, scripts, tests, and this document aligned.

```bash
dotnet test --project tests/Infrastructure.Tests/Infrastructure.Tests.csproj \
  --no-launch-profile -- \
  --filter-class "*.AnalyzeCiFailureWorkflowTests" \
  --filter-not-trait "quarantined=true" \
  --filter-not-trait "outerloop=true"

gh aw compile analyze-ci-failure --validate --actionlint --shellcheck
```
