# Issue-driven flaky-test investigations

A maintainer can enroll an open Aspire issue by URL alone. Fresh, bounded
agent phases follow the `fix-flaky-test` skill, discover the test from source,
reproduce it, diagnose matching failures, and verify a source-scoped candidate.
The result is a clean **draft** fix PR or finite, evidence-backed findings.
Nothing merges, closes the issue, or removes quarantine.

## Start, resume, cancel

After the workflows are registered on the default branch:

```shell
gh workflow run flaky-test-investigation.yml --repo microsoft/aspire \
  -f issue_url=https://github.com/microsoft/aspire/issues/NUMBER \
  -f operation=start
```

Use `operation=resume` to reconcile an existing investigation, or
`operation=cancel` to cancel only its exact owned run. Enrollment and cancellation
require maintainer permission; the issue author need not be a maintainer.
Repeated enrollment does not reset state or budgets.

The controller advances one phase and exits; no model waits for CI. A scheduled
controller reconciles checkpoints every 15 minutes. Manual resume can advance
completed work before the next scheduled check. Progress does not depend on
completion events from token-dispatched workflows. Scheduled recovery can also
resume a completed worker after an interrupted controller.

```text
issue URL → discover → reproduce → diagnose → candidate → verify → source review
                             ↑                    │                 │
                             └── fresh phase ─────┘           draft PR/findings
```

## Authority and isolation

The trusted implementation is `.github/scripts/flaky_investigation/`.
`contracts.py` validates discovery, repair scope and budgets; `github.py` owns
REST and immutable Git checkpoints; `evidence.py` accounts for original test
records; `worker.py` validates fixed-workflow activation; `runner.py` retains
original test iterations; `controller.py` advances the state machine and publishes.

The checkpoint is `state.json` on
`ankj/flaky-test-<issue>-state`. Each transition creates a new commit and updates
the ref without force. Concurrent writers cannot replace one another's state.
The checkpoint records immutable source/control revisions, exact owned run IDs
and attempts, proposals, counts, candidate identity, and terminal outcome.
Model repo-memory is proposal transport, **not** orchestration authority.

The controller records a dispatch intent before calling the API and captures
`return_run_details.workflow_run_id` from the response. It never finds a run by
looking up the latest run. If a dispatch succeeded but its receipt/checkpoint
was lost, recovery stops with an explicit blocker instead of dispatching twice.
Rerun attempts and foreign workflow/source/repository identities are rejected.

Issue text, comments, source, logs and model output are untrusted. They cannot
authorize arbitrary shell, refs, workflows, feeds or credentials. Discovery
must resolve one ordinary test declaration, class/namespace, existing test
project, and literal issue error. Ambiguous/generated/nested cases stop for a
human. Repair paths are existing regular C# files in the selected project's
explicit project-reference graph; dynamic references do not grant broader scope.
Scheduling, data, and quarantine attributes cannot change.

Candidates run through the fixed `reproduce-flaky-tests.yml`. Its optional
`investigation_issue_url` and `investigation_revision` inputs select an immutable
worker-intent checkpoint, not arbitrary settings or code. When neither input
is supplied, the existing branch-configured manual defaults remain unchanged.

Scoped `ankj/flaky-test-<issue>-run-<round>` refs point at the already published
trusted worker revision. The controller never creates workflow-containing trees.
Trusted setup validates the checkpoint and owned run, derives its matrix and
source revision, and checks the candidate checkout before building. The trusted
workflow/runner stay in the root checkout; candidate source is checked out
separately under `candidate/`. Neither checkout persists credentials, and the
job has read-only repository permissions. No candidate process runs in a
publication job or receives write credentials.

The agent job is read-only. Compiler-generated proposal-memory persistence has
`contents: write` to its scoped proposal branch; the generated conclusion job
also requests `issues: write`. Those jobs do not execute candidate code.
Automatic issue-creation/error-report handlers are disabled. The trusted
controller alone publishes findings and source PRs.

A reusable caller must allow `contents: write`, `actions: read`, `issues: write`,
and `copilot-requests: write` for the compiled workflow. These are the combined
job requirements, not the permissions of the model job.

## Evidence and finite limits

Local repetition is attempted when the agent environment supports the relevant
OS and prerequisites. Otherwise it records the actual environment and explicit
skip reason. Local results never replace the hosted gate.

Baseline rounds use three runners × three iterations per selected OS. At most
three rounds are permitted; the third can escalate to all quarantined tests in
the project. A flaky baseline requires both target passes and target failures
matching the issue's error. Unrelated failures, build/runner errors, missing or
skipped tests, and missing TRX cannot authorize a candidate.

Every runner retains each iteration's original TRX, output, process exit and
checkout receipt, including passing iterations. The controller validates exact
run/attempt/source/filter identity and counts original result records, not
summary counters or merged reports. Findings report **requested
runner-iterations** separately from **actual tests**: project-mode iterations
can execute many tests and are not an aggregate test-count cap.

Each issue has eight model phases, five worker rounds, two repair attempts,
180 runner-iterations, and twelve hours. Each model has 15 minutes; the existing
reproduction runner has a 60-minute job timeout. Repairs are limited to five
existing files and 800 changed lines. Final verification requires at least nine
target passes, no failures, and the mode that reproduced the failure. The final
fresh agent phase must review the exact verified candidate SHA and its files.
Nine passes are finite evidence, not proof that a low-frequency flake is gone.

CLI E2E investigations currently stop with an actionable blocker because their
prebuilt runtime images are not candidate-aware. Existing manual CLI E2E
reproduction is unchanged. Other tests that need unsupported runtime artifacts
or unavailable infrastructure can likewise finish blocked rather than claim
verification against the wrong environment.

Terminal outcomes are `verified`, `not-flaky`, `ambiguous`, `infrastructure`,
`not-reproduced`, `budget-exhausted`, `blocked`, and `cancelled`.
Findings use an automation prefix and a marker tied to issue/base SHA; duplicate
suppression also requires the actual `github-actions[bot]` author. PRs preserve
the repository template and contain only the source candidate, not investigation
workflow changes. Human code walkthrough and security review remain required.

If publication was interrupted, recovery checks for the owned bot marker. If
the remote outcome cannot be established, it records a terminal
`publication_uncertain` blocker and emits an error rather than retrying a
possibly completed comment/PR write. Inspect the checkpoint and remote objects
before any manual remediation. Investigation refs and artifacts are retained
for audit; artifact expiry can make an old investigation unrecoverable.

## Validation and deployment

Behavioral tests invoke the production state machine, transport boundaries,
evidence reader and runner. `FlakyInvestigationTests` runs them from
`Infrastructure.Tests` on Windows and Unix. No tests assert workflow/source text.

```shell
python3 -m unittest discover -s .github/scripts/flaky_investigation -p 'test_*.py' -v
dotnet test --project tests/Infrastructure.Tests/Infrastructure.Tests.csproj \
  --no-launch-profile -- \
  --filter-class "*.FlakyInvestigationTests" \
  --filter-not-trait "quarantined=true" --filter-not-trait "outerloop=true"
gh aw compile flaky-test-investigator --schedule-seed microsoft/aspire --validate
bash .github/workflows/lint-handwritten-workflows.sh
```

Use the repository-pinned compiler, currently v0.89.17. Regenerate the lock;
never edit it manually. Compilation and behavioral tests do not establish
hosted model authorization, built-in-token bot activation, action allow-list
acceptance, or successful live reproduction. Hosted validation must also prove
that checkpoint/source/ref publication and the fixed reproduction adapter work
with the built-in job token. No runtime step requires workflow-file mutation.

New dispatch filenames and schedules require default-branch registration.
Premerge validation can use a separately reviewed, temporary existing-workflow
bootstrap that calls the generated reusable agent. The trusted wrapper may set
`FLAKY_AGENT_WORKFLOW=reproduce-flaky-tests.yml` and `FLAKY_AGENT_SHA` to the
immutable agent-wrapper revision, plus `FLAKY_WORKER_SHA` to the immutable
production revision containing the fixed reproduction adapter. These are
trusted deployment settings, never issue/model inputs. Their identities are
frozen in the checkpoint. Production defaults use the canonical agent filename
and the control revision for both agent and worker code.

For premerge validation, publish the exact trusted worker revision to an
upstream ref before the job token creates run refs. In the observed
[before](https://github.com/microsoft/aspire/actions/runs/39673661846) /
[after](https://github.com/microsoft/aspire/actions/runs/39673902818) probe,
readable ancestor access returned HTTP 403 until that registration. This is an
observed activation prerequisite, not a universal GitHub policy guarantee.
Only separately authorized bootstrap setup uses a workflow-capable credential;
runtime jobs still use their job tokens and do not modify workflow files.

An agent wrapper forwards `issue_url`, `issue_number`, and `state_revision`.
The controller derives the number from the URL; preparation checks they agree
before model work. Proposal branches stay in the per-issue namespace:
`ankj/flaky-test-<issue>-proposals-<run-id>`.

Validate the real issue-only flow, exact original artifacts, fresh model
phases, token-dispatched bot activation, recovery and terminal output before
claiming deployment complete. Upstream policy/settings failures are blockers,
not permission to change main or introduce another credential.
