# Analyze CI failures

The [`Analyze CI Failure`](../../.github/workflows/analyze-ci-failure.md)
workflow uses Copilot to classify failed `CI` workflow runs as transient,
pull-request-caused, or a repository break on `main`.

The workflow may post analysis on a pull request, rerun transient failures,
persist recurring causes, or create a `[Main CI Failure]` issue. These effects
are allowed only after deterministic validation against data collected from
GitHub.

## Supported runs

Automatic analysis currently runs for failed `CI` workflow pushes to `main`.
Manual dispatch can analyze a specific run. The collector accepts `main` push
runs and pull-request runs; other workflow paths, events, and branches are
rejected or skipped.

The collector pins the run attempt from the `workflow_run` event so a later
rerun cannot change the evidence being analyzed. Run ID, attempt, workflow
path, event, branch, SHA, and failed jobs come from GitHub rather than from
agent output.

## Attribution

For a pull-request run, PR-directed effects require exactly one subject PR.
The workflow first uses the run's PR association, then bounded commit and fork
branch fallbacks. The fork fallback requires the failed run's exact head SHA.
Missing or ambiguous associations do not produce a guessed subject.

For a failed `main` run, the PR associated with the failed push is context, not
the presumed cause. The workflow considers every merge since the most recent
successful `main` run. Candidate attribution is withheld when run history is
incomplete or when a candidate commit does not map to exactly one PR merged
into `main`.

PR comments, locks, and reruns require an unambiguous subject PR. Run-scoped
recurring-cause persistence can continue without one, but its PR occurrence
context is recorded as unavailable.

## Agent trust boundary

Logs, annotations, pull-request metadata, prior causes, and failed-test
evidence are collected before analysis. The agent receives bounded evidence
and proposes classifications, causes, a verdict, and rerun requests.

Before any side effect, the
[`analyze-ci-failure-validation.sh`](../../.github/workflows/analyze-ci-failure-validation.sh)
boundary rebuilds trusted run, attempt, SHA, PR, failed-job, test, and cause
identity from collected artifacts. It rejects output that adds, omits, or
rebinds trusted records. Published diagnostics are reconstructed from trusted
evidence rather than copied from agent output.

External and agent-supplied text is bounded and rendered inert before it is
used in workflow diagnostics, Markdown comments, or issue bodies.

## Failed-test provenance

Each failed test job's logs artifact is selected within the analyzed run and
attempt, downloaded by artifact ID, and extracted separately. TRX results from
that artifact are stamped with the corresponding GitHub Actions job name.

Reported failures must match the same trusted `{test, job}` record. Diagnostic
rebinding and flaky-cause validation use that exact pair, so a real test from
one job cannot be attributed to another failed job.

GitHub's artifact API does not expose a producer job ID. The selector therefore
uses the job and artifact naming contract in
[`run-tests.yml`](../../.github/workflows/run-tests.yml). Missing, oversized,
or ambiguous artifacts make test evidence unavailable rather than producing an
empty successful result.

## Side-effect gates

- Only validated transient failures from the same run attempt can request a
  rerun, and the subject PR must still be open.
- Failures attributed to one PR are reported on that PR.
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
`analyze-ci-failure-validation.sh`.

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
