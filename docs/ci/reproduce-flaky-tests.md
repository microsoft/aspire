# Reproduction worker

[`reproduce-flaky-tests.yml`](../../.github/workflows/reproduce-flaky-tests.yml)
runs a test selection repeatedly on independent hosted runners. It supports
manual branch configuration and a bounded dispatch contract. It does not repair
tests, publish candidates, open pull requests, or call another workflow.

## Manual configuration

The existing workflow-level `TEST_PROJECT`, `TEST_FILTER`, `TARGET_OSES`,
`RUNNERS_PER_OS`, and `ITERATIONS_PER_RUNNER` settings remain the defaults.
Edit them on an investigation branch and dispatch that branch as before.
The placeholder method must be replaced; it is not a runnable default.
Author-controlled `TEST_FILTER` still supports class and multiple-method filters.

Project shortnames resolve to `tests/{name}.Tests/{name}.Tests.csproj`, then
`tests/Aspire.{name}.Tests/Aspire.{name}.Tests.csproj`. The workflow builds with
quarantined tests enabled and disables default quarantine/outerloop exclusions
when reproducing.

## Dispatch contract

All eight inputs are optional strings. Empty inputs use the manual configuration
unless otherwise noted. Validation runs before the candidate checkout or build,
in [`eng/scripts/reproduction-worker.ps1`](../../eng/scripts/reproduction-worker.ps1).

| Input | Accepted values |
| --- | --- |
| `test_project` | Dot-separated project shortname, not a path |
| `test_method` | Exact fully qualified method, without wildcard, arguments, or shell syntax; selects `single-test` if mode is empty |
| `mode` | `single-test` requires `test_method`; `quarantine-project` selects all quarantined tests in the project and rejects a method; empty mode without a method retains manual `TEST_FILTER` |
| `os` | Comma-separated distinct `ubuntu-latest`, `windows-latest`, `macos-latest` |
| `repetitions` | 1-50 iterations per runner |
| `runner_count` | 1-10 runners per OS |
| `candidate_commit` | Full 40-character commit SHA accessible in this repository; empty checks out the dispatch ref's SHA |
| `correlation_id` | Optional 1-100 characters: letters, digits, `.`, `_`, `-`; starts with a letter or digit |

The dispatch overrides generate at most 30 runner jobs. Author-controlled manual
counts remain positive integers, with the GitHub matrix limit of 256 jobs.
There is no dispatch input for arbitrary commands or test-filter arguments.
Partial overrides are supported.

For a trusted workflow checking an exact candidate, dispatch the workflow on
`main`, not on the candidate branch:

```bash
gh workflow run reproduce-flaky-tests.yml --repo microsoft/aspire --ref main \
  -f test_project=Hosting \
  -f test_method=Aspire.Hosting.Tests.ExampleTests.ExampleMethod \
  -f os=ubuntu-latest -f runner_count=3 -f repetitions=5 \
  -f candidate_commit=<full-candidate-sha> -f correlation_id=repair-42-verify
```

Replace the example method and candidate SHA with real values. For project-wide
contention, omit `test_method` and use `-f mode=quarantine-project`.
The workflow is repository-gated to `microsoft/aspire`; a fork-only candidate
must first become accessible there. This contract does not authorize publication
to upstream branches or change fork-first publication policy.

GitHub discovers dispatched workflows on the default branch and runs the
requested ref; see the
[`workflow_dispatch` reference](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#workflow_dispatch).

## Execution evidence

Every runner uploads `reproduction-{os}-{index}`, including successful runs:

```text
receipt.json
iteration-1/test-output.log
iteration-1/<test reports and diagnostics, including TRX>
iteration-2/...
```

Failure runs also retain the existing `failures-{os}-{index}` artifact name.
Both artifact names contain the same evidence directory. Output is UTF-8.

The version-1 JSON receipt records the repository, run ID and attempt, workflow
ref and SHA, actual checked-out source SHA, runner OS/index, resolved
configuration (including correlation ID), and one entry per recorded iteration.
Each entry includes process and effective exit codes, status, executed/passed/
failed counts, and relative output/TRX paths. A requested candidate SHA is
compared with `git rev-parse HEAD` before building. Workflow identity uses
[`github.workflow_ref` and `github.workflow_sha`](https://docs.github.com/en/actions/reference/workflows-and-actions/contexts#github-context),
not the candidate identity.

Iterations explicitly request
[`--report-trx`](https://learn.microsoft.com/en-us/dotnet/core/testing/microsoft-testing-platform-test-reports).
Counts come from TRX counters, not console-text matching. MTP merged reports are
retained but excluded from counts to avoid double-counting original reports.
A zero process exit is insufficient: missing TRX, zero executed tests, failed
tests, or incomplete pass counts fail the iteration. Malformed reports fail
receipt processing explicitly. Raw output and reports remain available.

A consumer must check the workflow/job conclusion, expected OS/runner coverage,
requested iteration count, per-iteration status, correlation ID, source SHA,
and workflow SHA together. A build failure, cancellation, malformed report, or
timeout can leave an initial receipt or fewer recorded iterations. An incomplete
receipt is not a successful verification.

## Trust boundary and limitations

Candidate jobs have read-only `contents` permission, checkouts disable persisted
credentials, and no secrets are passed. The receipt script is checked out at the
workflow revision, independently of the candidate revision. The shared CLI E2E
image producer runs at the dispatch revision; candidate builds use those images.
Changes to candidate image definitions are therefore not verified by this path.

Candidate build/test code still executes on the runner and can modify local
files, including evidence. Receipts are execution diagnostics, not tamper-proof
attestations or permission to merge. Only dispatch reviewed candidates from
trusted workflow refs. Native Windows/Linux hosted execution and artifact upload
must be validated in Actions before relying on this contract operationally.
