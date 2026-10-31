# Analyze CI failures

The CI analyzer collects bounded evidence, asks Copilot for classification
decisions, and independently validates those decisions before publishing.
**The agent matches mechanisms; the publisher owns durable history and issue
destinations.** A proposal or file claiming to be validated grants no authority.

## Components and trust boundaries

```text
Trusted collection → evidence.json + diagnosis context
                           ↓
                       agent → proposal.json
                           ↓
Trusted publisher → pure validation → fresh history/destination checks → effects
```

The implementation is in `tools/AnalyzeCiFailure/`:

- `Models/` groups related data types without changing their `AnalyzeCiFailure`
  namespace. `TransientContracts.cs` defines strict versioned evidence and
  proposal transfers; `CollectionModels.cs` contains evidence-collection requests
  and results, shared analysis progress and invocation outcomes;
  `EvidenceContracts.cs` contains read-only upstream views
  and compatible publication projections. `PriorContextContracts.cs` keeps
  tolerant historical discovery views, while `RunSummaryContracts.cs` defines
  persisted run summaries. `ValidationModels.cs` holds internal accepted values,
  `EvidencePaths.cs` holds local paths, and `AnalysisVocabulary.cs` preserves
  explicit enum wire names. These roles remain distinct despite sharing a
  directory.
- `Infrastructure/` contains the analyzer's GitHub metadata/error adapter,
  exact upstream ID decoding in `GitHubJson`, Git commands,
  and `AnalyzerCommandExecutor`, which binds command context, checks cancellation
  at command boundaries, and reports sanitized analyzer errors. Process execution
  itself is reused from `Aspire.TestTools.ProcessRunner` for Git.
  GitHub API requests use the source-linked `Aspire.TestTools.GitHubApiClient`.
  Cause-issue discovery, PR
  actionability, and analysis-comment matching remain analysis policy in
  `AnalysisPublicationQueries`, alongside the publisher rather than transport.
- `AnalysisCommands` registers commands, resolves input paths and owns short
  invocation lifetime boundaries. `EvidencePreparation` sequences collection
  and replay, while `EvidencePackageStore` reads validated inputs and writes or
  invalidates generated evidence views. Neither calls back into CLI registration.
- `GitHubActionsIntegration` writes output batches and optional step summaries,
  and interprets completion job results. `AnalysisRerunner` owns fresh-state
  rerun checks. Invocation directory announcements and inline error annotations
  remain at their existing lifetime/error boundaries.
- `WorkflowRunEvidenceCollector` acquires external data and parses selected test
  artifacts. It is single-use, including after failure or cancellation.
  Caller-owned `AnalysisProgress` and `InvocationCapture` retain established
  facts and partial captures across failures.
- `CauseHistoryStore` owns the history branch, fresh checkout, record layout,
  and commit/push receipts. The publisher owns occurrence and merge policy:
  occurrences are persisted before issue effects and resolved links afterward.
- `PriorCauseCatalogue` indexes historical records without I/O.
  `TestResultArtifacts` selects artifact expectations and allocates budgets;
  `TestResultArchiveExtractor` owns bounded ZIP validation and extraction.
- `TestFailureReader` parses TRX/Mocha data and deduplicates copied executions.
  Raw diagnostics are compared before sanitization or truncation.
- `WorkflowFailureDiagnosisContextBuilder.Build` projects captured in-memory
  facts into the agent summary and complete historical catalogue. It performs
  no filesystem, network or clock reads. Diagnostic JSON is parsed during
  acquisition; full historical attachments retain their original bytes.
- `AnalysisValidator.Validate(evidence, proposal, expectedRepository)` binds
  decisions to immutable evidence and derives the uniquely permitted verdict.
- `AnalysisPublisher` checks fresh history and destinations, records
  occurrences, publishes issues, then records issue links. PR comments use
  accepted facts and fresh PR state.
- `PublicationEvidenceProjection` maps collected tests to publication-compatible
  evidence for validation. `MarkdownFormatting` handles code spans and
  indentation; it does not redact secrets.

All three privileged checkouts use trusted analyzer sources, never failed-PR
or agent-written code. The collection artifact is downloaded independently of
the proposal artifact. Publication validates again in its own process.
Paginated GitHub records must be objects; malformed rows fail explicitly rather
than becoming successful "no work" or "no matching issue" results.

### GitHub transport and credentials

The analyzer uses one shared HTTP client per invocation rather than launching
`gh api`, `gh issue`, or `gh pr comment` for each request. The existing static
`Aspire.TestTools.GitHubCli` and `GitHubActionsApi` facades remain CLI-backed for
their other consumers; this migration does not change those tools.

Authentication is lazy: `GH_TOKEN` takes precedence over `GITHUB_TOKEN`. When
neither is present, the client can retrieve the current github.com credential
once through `gh auth token`. Tokens remain in memory and never enter command
user-visible output, diagnostics or artifacts. An explicitly supplied token that fails
authentication does not fall back to another identity. Replay, validation and
help perform no credential lookup. The default client explicitly supports
github.com only; a different configured host is rejected rather than silently
receiving a github.com credential.

JSON reads follow same-origin `Link` pagination, retaining comparison metadata
and accepting a short page with a next link. The aggregate response is bounded
to 16 MiB and 1,000 pages. Signed log/artifact redirects require HTTPS and never
receive API credentials outside the API origin. Operation deadlines cover
response bodies as well as headers. Transient GET responses have at most three
attempts; a server-requested wait over five seconds fails instead of retrying
earlier than GitHub permits.

Log acquisition retains at most 10 MiB per job and records bounded partial
content on stream failure or truncation. HTTP errors expose the method, API
path and status, not response bodies, authorization headers or signed queries.
Artifact limits apply to actual received bytes, including partial files from
failed downloads, in addition to metadata and extraction checks.

Issue/comment creation validates returned JSON IDs and `html_url` values.
Label additions use the additive endpoint so operator labels survive updates.
Mutations are never automatically retried: a lost or invalid response records
an unknown receipt and preserves the existing publication recovery behavior.
Internal progress uses the nullable `HasRelevantFailures` flag; its external
name remains `has_work` (`HAS_WORK` / `has-work` at Actions boundaries).
Unknown means admission and failed-job discovery have not completed; false
means no relevant failures; true means relevant failures were discovered even
if a later phase fails. Readiness is a separate fact. Replay copies decoded run
facts and sets this flag before validating the bundle, so true in a failed
replay does not authorize its input.

Internal names do not change JSON fields, CLI arguments, artifact filenames
or status meanings. `NormalizedJsonNumber` represents mathematical JSON-number
equality in tolerant historical comparisons, not validated GitHub identity.
Upstream IDs still use separate exact-integer decoding. An `AnalysisOutcome` of `ready`
means evidence is ready, not that publication happened.

Git history still uses `git` and the existing `gh auth git-credential` helper.
The HTTP migration removes process-per-API-call overhead, not the separate Git
authentication dependency.

## Workflow-specific conventions

`Workflows/IGitHubWorkflowConventions` is the code-level extension point for a
GitHub workflow. `AspireCiWorkflowConventions` owns the current CI workflow's
path, aggregation-job recognition, step names, test-job naming patterns,
artifact-name construction and interrupted-execution rules. The collector
calls `DescribeTestArtifacts` once per actionable job. It returns one
`TestArtifactExpectation` per expected artifact, so a job can produce several explicitly
named artifacts. Each archive can also contain multiple TRX files, including
different target frameworks. Selection and availability checks use every
job/artifact expectation; finding one artifact cannot satisfy another.

Evidence policy is separate from the result format:

| Policy | Download resource limits | Missing or unreadable results |
|---|---|---|
| `Required` | Reserved first | Test evidence unavailable |
| `OptionalDiagnostics` | Remaining capacity | Absence or acquisition failure is diagnostic-only |
| `RequiredForInterruptedExecution` | Remaining capacity | Test evidence unavailable, including omission due to limits |

These are inherited safety limits, not financial or analysis quotas: at most
20 selected artifacts, 100 MiB per archive, 1 GiB cumulative downloaded archives,
10,000 ZIP entries per archive, and 1 GiB cumulative expanded result data.
Required evidence is selected first. In the remaining capacity, results needed
to establish an interrupted execution's outcome take precedence over optional
diagnostics, followed by deterministic job-name/artifact-ID ordering.
Exceeding a limit cannot turn incomplete required evidence into an empty
successful result.

Artifact selection, acquisition and report parsing proceed independently.
Missing or malformed required results leave the inventory explicitly unavailable
and block publication, but successfully captured observations remain in the
bundle and summary. A malformed file does not discard other files in its archive,
and conflicting copies exclude that execution rather than unrelated failures.
Failed downloads and failed extractions consume the same shared budgets as
successful acquisitions.
TRX reports are processed across all selected archives; copied executions are
deduplicated only within the same producing job and execution ID, with matching
raw diagnostics. Distinct target-framework executions remain distinct.
Mocha failures are matched individually against completed tests: a hook or
blocking browser-session failure does not discard other completed assertions
in the same report. Excluded hook/harness entries remain bounded, redacted
diagnostic context in `diagnostics/job-<id>-mocha.log`; they do not become failed
test references or turn passing tests into failures. Their job-level publication
policy is tracked separately in [#21468](https://github.com/microsoft/aspire/issues/21468).
Each job retains complete diagnostic entries up to 65,536 Unicode code points,
plus one notice counting omitted entries. Retained entries are redacted once;
after the buffer fills, additional entries are counted but not redacted or retained.
Completed test failures are still collected after the diagnostic limit is reached.
Format alone does not determine whether evidence is required.

Setting-key redaction uses non-backtracking matching to keep processing linear
even for long prefixes. Secret values are redacted before display truncation,
without imposing a maximum length on recognized setting keys.

Artifact collection and parsing diagnostics include the canonical run URL,
attempt, workflow path, processing stage, and relevant job/artifact names.
Parsing errors additionally identify artifact ID and result file; conflicting
execution errors identify the job, execution and source artifacts. Text stays
bounded, redacted and inert in workflow logs, and the same failures are captured
in the collection problems. A setup or unexplained acquisition failure leaves evidence unavailable, never
complete merely because no reports were read.
Result-file names in diagnostics are generated safe extraction names, not
original ZIP-entry paths; the artifact ID identifies the archive to inspect.

To adapt collection to another GitHub workflow:

1. Implement `IGitHubWorkflowConventions` with its exact workflow path,
   aggregation rules and artifact plans. Return an empty list only for jobs
   without supported structured evidence; reject malformed recognized jobs with a
   useful bounded diagnostic. Preserve the collected job name in every plan.
2. Select that trusted implementation explicitly in `EvidencePreparation`;
   the collector accepts one conventions implementation per
   invocation. There is no runtime plugin discovery or agent-controlled loading.
3. If automatic execution is wanted, update the analyzer workflow trigger and
   regenerate its lock. Neither is changed merely by adding a class.
4. Review baseline attribution and publication for that workflow. Current scopes
   remain main-branch pushes and PR events. Main history searches the failed
   run's workflow ID for successful main pushes; scheduled, release-branch and
   dispatch events need a separate scope/history design. All workflows would
   currently share the repository's cause-history branch.

Run/attempt/repository verification, artifact run/SHA checks, resource limits, safe ZIP
extraction, supported parsers, strict validation and effect gates stay in common
code. Workflow conventions cannot disable those checks. Artifact-to-job
association still relies on producer naming contracts; this seam does not add
a producing job ID to GitHub artifact metadata.

## Transient contracts

### Storage ownership and job handoffs

Only the Git branch `memory/ci-failure-analysis` is durable application history:
`causes/<id>.json` and `runs/<run-id>.json`. Collection and publication run on
different hosted runners and read the history independently.

Each invocation produces its own outputs:

```text
evidence/   Collected facts, agent summary, historical causes and diagnostics
replay/     Regenerated views of saved evidence
recovery/   Diagnostics retained when collection or publication has problems
diagnostics/ Decision summary for every outcome, including no-work and success
scratch/    Temporary working files; removed when the command finishes
```

Evidence, replay, recovery and diagnostic outputs remain available after the command exits.
Local callers can save them for investigation and remove them when no longer
needed. Each invocation leaves previous outputs unchanged.

Collection uploads `ci-failure-evidence`. The agent downloads it and uploads
`ci-analysis-output` containing `proposal.json`. Publication and rerun jobs
download the evidence independently of the proposal. Recovery is never an
input to publication or rerun authorization.

The separate diagnostic artifact explains collection and publication decisions,
including job selection, PR association, artifact acquisition, evidence completeness
and log truncation. It includes stage durations and publication receipts identifying
confirmed history pushes and issue/comment operations. An unknown receipt means
the tool could not confirm the remote result; it is not proof that no effect occurred.
Diagnostics never authorize publication or retries.

Running progress is written to stderr and flushed to `diagnostics/progress.log`
before the final `summary.json` exists. Stdout retains its existing path outputs.
The trace names each phase, records stage durations and activity outcomes, and
reports sanitized problems without dumping captured payloads, subprocess arguments
or environment variables. Collection errors also name the failed operation:

```text
[time] [jobs] started: Listing workflow jobs
[time] [jobs] Listing workflow jobs failed: gh failed (1): gh: Server Error (HTTP 502)
[time] [jobs] failed: Listing workflow jobs duration_ms=15327
::error::Listing workflow jobs failed: gh failed (1): gh: Server Error (HTTP 502)
```

This is a job-list acquisition failure, not a test-result parsing failure.
Test collection and parsing have their own explicitly named phase. Redirect
stderr to a file to keep the running trace locally. The retained progress file
is capped at 2 MiB; after an explicit truncation notice, progress continues on
stderr. A progress-file write failure emits a warning and diagnostic record
without replacing the original acquisition error. Existing diagnostic-directory
uploads include the progress file alongside the final summary.

Operational failure issues and follow-up comments include the source CI run as
well as the analyzer run. A trusted conclusion pre-step prepares private copies
of gh-aw's agent-failure, failed-job, missing-data and missing-tool templates
and selects them through the supported `GH_AW_PROMPTS_DIR` override. The pinned
upstream templates and their analyzer-run links remain unchanged.

Automatic reports use the triggering workflow run ID. Dispatch reports label
the input as a **requested** CI run because collection may fail before verifying
it. IDs must be canonical positive 64-bit decimal text; invalid dispatch input
produces an explicit unavailable-context note, never an input-derived URL.
No source URL comes from the model. Report preparation runs from the workflow's
own commit, even if collection or the agent failed, and adds no GitHub write
operations beyond the existing reporters. Missing pinned templates fail
preparation explicitly rather than selecting partial overrides.

CI uploads diagnostic summaries for successful, no-work and failed invocations
and retains them for seven days. They are redacted and bounded, with omission
counts when detail is dropped. Full upstream responses and subprocess streams
are not diagnostic summaries.

Recovery captures available intermediate data, test reports and error details
for debugging failed, degraded or partially collected analyses. These captures
are redacted and size-limited, so they may be incomplete or unsuitable for
parsing. The recovery manifest identifies retained and omitted files. CI keeps
recovery artifacts for seven days; abrupt runner termination may prevent capture.

Replay (`replay --evidence-dir SAVED_EVIDENCE`) reads a complete evidence package
and regenerates views without modifying the original input. Saved evidence also
supports offline validation. Replay does not resume interrupted collection or
reparse raw test reports; recovery captures are diagnostic material, not
substitutes for a complete evidence package.

| File | Producer | Consumer |
|---|---|---|
| `ci-failure-analysis/evidence/evidence.json` | Collection boundary | Agent, validator, publisher |
| `ci-failure-analysis/evidence/collection-outcome.json` | Preparation and workflow boundary | Diagnostic outcome, not handoff authority |
| `ci-failure-analysis/evidence/analysis-summary.md` | Pure context projection and boundary writer | Agent |
| `ci-failure-analysis/evidence/history/cause-index.json`, `history/causes/*.json` | Complete historical discovery | Agent |
| `proposal.json` in `ci-analysis-output` | Agent | Validator and publisher |

Other captured upstream payloads and focused logs are diagnostic attachments,
not publication authority. Full historical attachments retain their original
contents; the focused preview is never the inventory.
Job-log focusing recognizes Node assertion headers such as
`AssertionError [ERR_ASSERTION]: web entered FailedToStart`, retaining the
failure message before the later generic exit-code annotation. Excerpts remain
bounded and do not independently establish a cause from that assertion.
Explicit failure anchors are selected before generic keyword contexts. Recognized
passing Mocha result lines are not failure anchors, even when their names contain
"failed". Selected context is rendered in source order within the 150-line focused
budget.

The versioned contracts use canonical positive 64-bit decimal **strings** for
GitHub IDs. Attempts are positive 32-bit JSON integers. Decoding rejects unknown
or duplicate decoded properties, missing required members, null collections,
invalid enums, malformed Unicode and noncanonical IDs. This is intentionally a
transient compatibility break: old multi-file proposals, scalar cause proposals,
shell scalar coercion and jq number-spelling compatibility are not supported.
Durable historical records retain their existing tolerant readers.
New versioned files require valid UTF-8; this does not change historical
record decoding or BOM handling.

### Evidence

The bundle captures repository/run/attempt, event-derived scope, SHA, run URL,
selected failed/interrupted jobs and their steps, structured test availability,
exact test names, opaque local refs, bounded diagnostics, ordered provenance,
prior reusable cause identities, and optional PR/main context.

An optional `runner_failure` job field captures the explicit hosted-runner-loss
annotation only when upstream step metadata also records an interrupted
`in_progress` step. A workflow-emitted matching annotation on a normally
completed job is diagnostic-only, not authority to excuse missing results.
Only this known interruption explains the absence of that job's
results; missing artifacts or unfinished steps alone do not. Existing malformed
or unreadable results are still acquisition problems, including expired or
ambiguous artifacts from that attempt. Ambiguous producing job names cannot
borrow another job's runner-loss annotation. Available observations
from interrupted jobs are retained. A confirmed runner-loss job without observed
test failures requires infrastructure classification and a covering cause.

Agent context presents per-job observation counts and groups identical diagnostic
records with all member refs. The complete inventory is unchanged, and different
groups remain separate evidence even when analysis finds a common mechanism.
Neither group size nor missing results establish infrastructure or flakiness.

PR identity is separate from nullable descriptive metadata: a metadata lookup
failure cannot erase a known subject number. Main comparison history retains
its available/incomplete/unavailable state. Incomplete history does not authorize
attribution and projects to null persistent candidate arrays.

Only an execution ID in the same producing job establishes a copied execution.
Copies retain all distinct sources. Identical failures without execution IDs
remain distinct. Stable `(name, job)` ordering precedes local ref assignment.
Publication selects the first source, preserves display-name presentation and
assigns durable `observation-{n}` only to ambiguous failures, using the
one-based index in the entire ordered failure list. Transient refs, artifact
IDs and all-source lists are never persisted.
Raw execution IDs remain unchanged for evidence deduplication. Their publication
projection is separately redacted, stripped of controls and bounded to 500
characters before entering durable run and cause-occurrence records.

### Proposal

The agent supplies schema version, repository/run/attempt identity, one decision
per job and test ref, and every distinct supported cause. Each cause is either:

- `existing`: a catalogue ID, claimed job IDs, test refs and reason;
- `new`: a definition containing ID/type/title/error pattern, claimed job IDs,
  test refs and reason.

There are no copied diagnostics, names, scope/PR/URL metadata, timestamps,
overall verdict, redundant cause-ID array or per-cause files. Reasons are
sanitized into new accepted values and truncated to 500 characters. New cause
title/pattern limits remain 238/500 characters after sanitization. Inputs are
not rewritten. Cause-level rationale is accepted diagnostic context, not a new
durable or rendered field.

Validation requires exact coverage, compatible cause/job/test relationships
and complete recurring-cause coverage. Compatible overlapping causes remain
allowed. Pure infrastructure has verdict `transient-infra`; compatible flaky
failures, optionally with infrastructure jobs, have `flaky-test`. PR
deterministic-only failures have `code-issue`, main deterministic-only failures
have `main-repository-breakage`, and deterministic plus transient evidence is
`mixed`, including mixed observations within one deterministic-classified job.
Unavailable structured evidence cannot authorize publication or reruns.

## Preparation outcomes and failure handling

- **ready**: exit 0, known work, usable bundle and required views written.
- **no-work**: exit 0, known run, no relevant failed jobs.
- **degraded**: exit 0, known work and usable job-level context, but supported
  required test-evidence acquisition/parsing failed; successfully observed
  failures remain available with an explicitly incomplete inventory.
- **failed**: nonzero exit, bounded problems and all facts established so far.
  `has_work` stays true after work discovery even if a later phase fails.

Optional PR metadata, changed-file, annotation and main-comparison failures
retain nonfatal fallbacks. History transport, projection, evidence/view/outcome
writes and cancellation are fatal, never degraded. Failed outcome finalization
is best-effort and must not hide the original error.

The history reader accepts a target branch and defaults to `main` for both
successful-run queries and merged-PR filtering. The analysis workflow remains
main-scoped; this does not add a CLI branch selector.

`collect --github-output FILE` reports known run identity and work status through
GitHub Actions step outputs, including when collection fails. It also emits
`analysis_ready` and `collection_status`. Ready, no-work and usable degraded
preparation return exit 0; fatal collection, output failure and cancellation
return 1.
Local collection and replay use the same exit contract. Exit 0 means diagnostic
preparation completed, not that publication is authorized. Unavailable test
results remain explicit in the bundle and block validation, publication and reruns.
Test-inventory validation errors report expected and proposed decision counts
without copying untrusted test names into the diagnostic.

When test results are unavailable, collection writes `limited-analysis.md` to the
evidence package and, when provided by GitHub Actions, the step summary. This
deterministic report uses validated run identity and fixed problem categories,
not agent-written explanations or upstream diagnostic text. Detailed sanitized
annotations and acquisition errors remain in the evidence/diagnostic artifacts.
The report includes retained structured-failure and confirmed runner-loss counts.
The workflow does not request an AI diagnosis for this limited-evidence outcome.
No causal history, issues, PR analysis comments, or reruns are authorized.

Known failure facts remain available to the downstream publication gate.
Secondary output/outcome errors never hide the original collection failure.
`workflow check-completion` checks that known relevant failures reached an
accepted terminal outcome. `workflow require-publication` is an alias using
the same handler. Both interpret upstream job results rather than independently
verifying remote publication effects:

- Invalid `HAS_WORK` text fails.
- True plus `degraded` and successful collection passes regardless of the
  publication result.
- Otherwise true plus successful publication passes regardless of the
  collection result.
- Remaining true cases fail with collection status, collection job result and
  publication job result in the diagnostic; false or absent flags pass.

A failed/cancelled collection or report write does not qualify for the
limited-report exception. The separate publication-success case still applies.
Its checkout, SDK setup and invocation run only when
work was collected, avoiding restore overhead and false alarms on no-work runs.
There are no custom Bash/jq preparation or publication-gate scripts.

## Publication and durable compatibility

The memory branch remains `memory/ci-failure-analysis`, with unchanged
`causes/<id>.json` and `runs/<run-id>.json` paths and record shapes. New durable
IDs remain JSON numbers. Existing numeric-token comparison, legacy opaque
labels, unknown-field preservation, issue markers, labels, managed sections and
operator notes retain their established behavior.

Selected existing IDs must still be readable with matching identity/type in
fresh history. A new-ID collision rejects before writes, except a same-run
occurrence with matching identity/type is a replay: retain the stored definition
and first occurrence, then perform normal destination checks. Unselected
malformed historical records remain discoverable without becoming a global
publication gate.

All causes and destinations are preflighted before history pushes or issue
mutations. The two ordinary history pushes still surround issue effects.
An issue failure can therefore leave durable occurrences for a retry; there is
no publication journal or rollback. First-writer-per-cause/run behavior remains,
and current run summaries are still overwritten on publication.

PR deterministic-only failures produce a PR report but no recurring-history
writes. Main breaks use cause issues, not comments on a triggering merge PR.
PR-directed effects check fresh open/unlocked state. Rendering retains the
65,000-byte comment budget, issue-body budgets, Markdown inertness, Unicode
handling and secret redaction.

Structured fixture failures caused by runner infrastructure remain in the exact
test inventory with classification `transient-infra`; they are not omitted or
mislabelled flaky. Each must belong to an infrastructure-classified job covered
by an infrastructure cause. Job-level infrastructure causes cover these test
observations without introducing flaky-test identities into cause history.

### Aggregate publication above ten causes

Ten or fewer independent causes retain individual issue creation and updates.
Above ten, all validated causes remain in history, but publication creates or
updates one run-level issue instead of mutating individual cause issues. The
aggregate lists every cause, affected jobs, observation counts, representative
diagnostics and known existing cause issue links. Many tests sharing one cause
do not trigger aggregate mode.

The source-run marker `<!-- ci-failure-overflow:<run-id> -->` identifies the
aggregate independently of its title. The run record stores `overflow_issue_url`;
individual cause `issue_url` values never point to an aggregate. Fresh destination
checks reject ambiguous, locked, malformed or non-automation-owned aggregate
issues before history or issue writes. Reprocessing preserves operator notes
outside managed sections and updates the same issue. Replayed causes render
their first-stored occurrence attribution rather than replacing it with a later
proposal's job membership.

Large reports use numbered, automation-owned managed continuation comments on
that issue rather than omitting causes. Retries discover already-created pages;
obsolete managed pages are marked superseded while retaining operator notes.
Validation and publication use compact aggregate-mode PR comments so an oversized
ordinary comment cannot reject overflow before publication.

## Reruns and identifier precision

Reruns remain disabled. `workflow rerun` reads the raw engine output and freshly
validates the independent evidence/proposal in the same process. Exactly one
rerun request must match accepted run/PR identities and a pure-infrastructure
verdict. Engine-added fields carry no authority. Malformed output items reject;
well-formed unrelated kinds produce an explicit no-op. No acceptance-file
protocol or JavaScript rerun helper is used.

Only case-insensitive `true` enables effects. Enabled reruns check fresh PR
open/unlocked state, returned run identity and a positive current attempt.
A later attempt blocks a stale rerun, not historical publication. Unknown PR
state skips with a diagnostic; malformed run metadata fails before mutation.
IDs enter HTTP requests as canonical endpoint text, without JavaScript conversion.
Manual workflow inputs and custom safe-output schemas use strings.
Repository-controlled high-ID tests cannot prove the fidelity of GitHub-emitted
event expressions or hosted gh-aw runtime decoding.

## Commands and validation

```bash
dotnet run --project tools/AnalyzeCiFailure/AnalyzeCiFailure.csproj -- collect \
  --repo microsoft/aspire --run-id 12345 --run-attempt 1
dotnet run --project tools/AnalyzeCiFailure/AnalyzeCiFailure.csproj -- validate \
  --repo microsoft/aspire --evidence-dir ./downloaded-evidence \
  --analysis-dir ./downloaded-proposal
dotnet run --project tools/AnalyzeCiFailure/AnalyzeCiFailure.csproj -- publish \
  --repo microsoft/aspire --evidence-dir ./downloaded-evidence \
  --analysis-dir ./downloaded-proposal
dotnet run --project tools/AnalyzeCiFailure/AnalyzeCiFailure.csproj -- replay \
  --evidence-dir ./downloaded-evidence
```

`--working-dir`, `--history-dir` and `--observed-at` support local fixtures.
`--working-dir` selects the checkout used by subprocesses.
`--history-dir` optionally selects a new publication history checkout.
`replay --evidence-dir` regenerates views from an existing bundle into a fresh
invocation's `replay/`, without modifying the saved evidence package.
`collect` requires `--repo` and `--run-id`; an omitted `--run-attempt` selects the
latest attempt. `replay` requires `--evidence-dir` and does not accept repository
or run-selection options. Missing required options fail before acquisition.
`validate` remains a local, no-effect validator. Obsolete `--scripts-dir`,
`--result-file` and the old transient layout are not supported.

Commands that produce outputs print their paths as `workspace_dir`,
`evidence_dir`, `replay_dir`, `recovery_dir` and `diagnostics_dir`. Pass the saved or downloaded
evidence directory explicitly with `--evidence-dir` for replay, validation,
publication and rerun. `--github-output FILE` writes step outputs for scripted
invocations; it does not select an evidence output directory.

Local commands create invocation-owned temporary directories; they do not upload
GitHub artifacts or write a `ci-failure-data` directory beside the source.
The workflow uploads the declared evidence path after collection and fails if
that required path contains no files. Recovery and diagnostic uploads use
`always()` and optional-file handling so collection/publication errors retain
available diagnostics without hiding the original failure. Scratch archives and
Git metadata are not part of those uploads.

The workflow invokes C# directly:

```bash
dotnet run --project tools/AnalyzeCiFailure/AnalyzeCiFailure.csproj -- collect \
  --repo "$GITHUB_REPOSITORY" --run-id "$WORKFLOW_RUN_ID" \
  --run-attempt "$WORKFLOW_RUN_ATTEMPT" --github-output "$GITHUB_OUTPUT"
dotnet run --project tools/AnalyzeCiFailure/AnalyzeCiFailure.csproj -- workflow check-completion
dotnet run --project tools/AnalyzeCiFailure/AnalyzeCiFailure.csproj -- workflow rerun \
  --repo "$GITHUB_REPOSITORY" --evidence-dir "$EVIDENCE_DIR" \
  --analysis-dir "$ANALYSIS_DIR"
```

The workflow maps event inputs to explicit command options. Automatic collection
pins the triggering run attempt; manual collection omits the attempt to select
the latest. The same `collect` command serves local and workflow callers without
reading ambient event/run environment variables.

```bash
dotnet build tools/AnalyzeCiFailure/AnalyzeCiFailure.csproj --configuration Release
dotnet test --project tests/AnalyzeCiFailure.Tests/AnalyzeCiFailure.Tests.csproj \
  --no-launch-profile -- \
  --filter-not-trait "quarantined=true" --filter-not-trait "outerloop=true"
```

Analyzer-owned tests and snapshots live in `tests/AnalyzeCiFailure.Tests/`.
Generated Markdown snapshots preserve issue/comment layout and arbitrary
operator notes, so documentation lint excludes those verified outputs.
Generic process/report-reader and selector tests remain in `Infrastructure.Tests`.
Loose workflow inputs select the analyzer test project through
`eng/github-ci/test-trigger-map.yml`.
Compile changes with the repository-pinned gh-aw compiler without changing
action pins. Local tests and compiler output do not validate hosted artifact
transfer, permissions, source provenance or actual publication. Those require
separately authorized workflow execution in an isolated destination.
