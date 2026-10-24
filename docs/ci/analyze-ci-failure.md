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
successful `main` run. Candidate attribution requires a complete GitHub
comparison whose relation is `ahead`; identical, behind, diverged, malformed,
or incomplete comparisons are non-attributable. A candidate commit must also
map to exactly one PR merged into `main`.

PR comments and pull-request reruns require an unambiguous subject PR that is
still open and unlocked immediately before the mutation. Validated transient
`main` failures can be rerun without a subject PR. Run-scoped recurring-cause
persistence can continue without an actionable PR, but its PR occurrence
context is recorded as unavailable when the subject cannot be identified.

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
[`AnalyzeCiFailure`](../../tools/AnalyzeCiFailure/Program.cs)
command validates run and subject PR identity, failed jobs, tests, and causes
against collected artifacts. It rejects output that adds, omits, or
rebinds trusted records. Published diagnostics are reconstructed from trusted
evidence rather than copied from agent output.

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
The join uses the exact display test name and trusted job name, not a new
numeric test-observation identity. Distinct numeric jobs may share a name,
and more than one cause may cover the same flaky test/job pair.
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

- Only validated transient failures from the same run attempt and with available
  test evidence can request a rerun. Pull-request reruns additionally require
  the subject PR to remain open and unlocked.
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
`analyze-ci-failure-*.sh`; final output validation is the dependency-free
`tools/AnalyzeCiFailure` C# executable. The command reads existing
`ci-failure-data/` files relative to the working directory and the existing
agent proposal and cause files under `ANALYSIS_DIR`. It does not collect data,
change history identity, or add run-attempt/SHA checks.

Validation keeps the existing single-test cause identity and run-only history.
It checks run identity, exact job/test binding, compatible causes and ownership,
unchanged stored cause type/test name, verdict consistency, and the ten-cause
and 65,000-byte PR-comment budgets. Sanitization and comment rendering still
call the publisher's bash helpers, keeping redaction and rendered output
single-sourced. Required unavailable TRX evidence blocks publication; optional
Mocha collection behavior is unchanged.

Semantic rules live in
[`ValidationRules.cs`](../../tools/AnalyzeCiFailure/ValidationRules.cs), operating
on immutable records in
[`ValidationModels.cs`](../../tools/AnalyzeCiFailure/ValidationModels.cs).
[`Validator.cs`](../../tools/AnalyzeCiFailure/Validator.cs) sequences those checks
and owns file access, JSON projection, sanitization, trusted diagnostic rebinding,
and rendering. JSON decoding and compatibility policy stay at that boundary.

To add or change a rule, edit the plainly named method (for example,
`RequireExactTestCoverage`) and add accepted and rejected typed-input cases in
[`AnalyzeCiFailureRuleTests.cs`](../../tests/Infrastructure.Tests/WorkflowScripts/AnalyzeCiFailureRuleTests.cs).
These tests call production rules without JSON, shell, or file fixtures.
Keep command-level coverage for boundary behavior and error ordering; rules do
not need a registry, framework, or per-rule class.

Numeric matching preserves the shell validator's distinct checks: failed-job
summaries must match the collected IDs' jq-serialized forms, while cause references
use numeric equality. Cause-ID validation retains jq's floating-point `floor`
coercion rather than introducing a stricter integer-only JSON contract.

Raw collected JSON uses last-property-wins semantics, including decoded duplicate
names and replacement by null. Scope and verdict extraction preserves Bash's
NUL removal and trailing-LF stripping without changing exact JSON test/job joins.
Matching `causes/*.json` directories count toward the cause budget and are rejected
as invalid cause files; hidden entries remain ignored.

### Compatibility limits

The covered producer-generated artifacts do not establish universal numeric,
scalar-extraction, or parser parity with jq. Verified differences remain:

- **Valid JSON numeric spellings:** raw run-context and run-metadata IDs written
  as `1.23e2` or `123e0`, and numeric context `pr_numbers` such as `4.2e1`, can
  pass the shell validator but be rejected by C#. These are standard JSON forms,
  distinct from the ordinary producer representations and from invalid JSON
  spellings such as `0123` or `+123`. Job/cause numeric coverage does not prove
  equivalence for these raw identity fields.
- **Other shell-extracted scalars:** trailing LFs in `run_id`, `html_url`,
  context `pr_numbers`, test-evidence `state`, and stored prior-cause `type`
  can be stripped by Bash but retained by C#, causing controlled rejection.
  The scope/verdict normalization above is not applied globally.
- **Parser limits and nonstandard grammar:** nesting beyond the default depth
  of 64 and lone low-surrogate escapes can be accepted by jq but rejected by
  C#. jq's nonstandard `nan` grammar can also be accepted where C# rejects it;
  this is separate from the valid scientific-number forms above.
- **Encoding:** a UTF-16LE-BOM raw trusted context is accepted by C# but rejected
  by the baseline jq validator. Unlike the rejection-only differences above,
  this difference is not fail-closed. It is not a claim about all BOM encodings.

These residuals remain rollout caveats; they are not addressed by the existing
fixture passes or by the sparse-build smoke check.

### Trusted publication build

The publication job checks out the tool, `global.json`, and shell dependencies
from the trusted workflow ref, installs the pinned SDK, and runs:

```bash
ANALYSIS_DIR=/path/to/downloaded/ci-analysis-output \
  dotnet run --project tools/AnalyzeCiFailure/AnalyzeCiFailure.csproj \
  --configuration Release -- validate
```

Local `Directory.Build.props`, `Directory.Build.targets`, and
`Directory.Packages.props` stop root Arcade and central-package inheritance.
No packages are required in the sparse checkout. Never build the failed PR's
ref or agent-written sources in this write-token job.

Command-level validator fixtures invoke the application entry point as a real
command; direct rule tests complement them, and the remaining shell helpers
retain their behavioral tests. Boundary coverage lives in
[`AnalyzeCiFailureWorkflowTests`](../../tests/Infrastructure.Tests/WorkflowScripts/AnalyzeCiFailureWorkflowTests.cs).
When changing the workflow, helpers, or the job/artifact naming contract, keep
the source workflow, generated lock, scripts, tests, and this document aligned.

```bash
dotnet test --project tests/Infrastructure.Tests/Infrastructure.Tests.csproj \
  --no-launch-profile -- \
  --filter-class "*.AnalyzeCiFailureRuleTests" \
  --filter-class "*.AnalyzeCiFailureWorkflowTests" \
  --filter-class "*.AnalyzeCiFailureTextTests" \
  --filter-not-trait "quarantined=true" \
  --filter-not-trait "outerloop=true"

gh aw compile analyze-ci-failure --validate --actionlint --shellcheck --schedule-seed microsoft/aspire
```
