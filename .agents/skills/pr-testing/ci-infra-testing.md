# Testing CI Infrastructure Changes

This is a supporting reference for the `pr-testing` skill. Use it when a PR
changes **CI infrastructure** instead of (or in addition to) product code. The
main `SKILL.md` flow — dogfood the CLI, `aspire new`/`run`, dashboard
screenshots — does **not** validate workflow, action, pipeline, or CI-script
changes.

The repo has **two CI systems, tested two different ways.** Read the track that
matches the diff (a PR can hit both):

- **Track A — GitHub Actions** (`.github/**`, plus the eng/ scripts & data those
  workflows consume). Most workflows do **not** run on the PR. → [Track A](#track-a--github-actions).
- **Track B — Azure DevOps pipelines** (`eng/pipelines/**`, `eng/common/**`, and
  the signing/packaging/publishing plumbing they call). **No** AzDO CI runs on a
  GitHub PR — a non-trivial change must be run on the `dnceng/internal` mirror.
  → [Track B](#track-b--azure-devops-pipelines).

> **Shell note (Windows).** Command examples use bash (the repo's CI runs on
> Linux). On Windows, run them in `pwsh` or Git Bash. `git`, `gh`, `az`, `node`,
> `python`, and `pwsh` are cross-platform; the Unix text tools are not. PowerShell
> equivalents for the ones used below:
>
> | bash | PowerShell |
> |------|------------|
> | `grep PATTERN` | `Select-String PATTERN` |
> | `grep -rn PATTERN` | `Get-ChildItem -Recurse \| Select-String PATTERN` |
> | `cat file` | `Get-Content file` |
> | `find . -maxdepth N` | `Get-ChildItem -Recurse -Depth N` |
> | `unzip -l a.zip` | `Expand-Archive a.zip -DestinationPath out` then inspect (or `[IO.Compression.ZipFile]::OpenRead('a.zip').Entries`) |
> | `diff a b` | `Compare-Object (Get-Content a) (Get-Content b)` |
> | `sort \| uniq` | `Sort-Object -Unique` |
> | `wc -l` | `Measure-Object -Line` |
> | `cmd >/dev/null 2>&1` | `cmd *> $null` |

## When this applies

Follow this file when the PR touches any of:

**GitHub Actions (Track A):**

- `.github/workflows/*.yml` — workflows (CI, tests, release, scheduled, etc.)
- `.github/workflows/*.js` — JS `github-script` helper modules (one of several
  helper forms; inline `run:` bash/pwsh blocks are far more common — see I-3.A)
- `.github/workflows/*.md` + `*.lock.yml` — gh-aw agentic workflows
- `.github/actions/**` — composite actions
- `.github/aw/**`, `.github/agents/**` — gh-aw config / agent docs
- `eng/scripts/*.ps1`, `eng/scripts/*.sh` — scripts invoked by workflows
- `eng/github-ci/ci-skip-entirely-patterns.txt` — the CI-skip pattern file
- `eng/test-retry-patterns.json` — auto-rerun retry patterns

**Azure DevOps (Track B):**

- `eng/pipelines/**` — internal / unofficial / public / release pipelines
- `eng/common/**` — Arcade-provided shared build/sign/publish scripts the
  pipelines call (changes here ripple into every AzDO build)
- Signing / packaging / publishing plumbing the pipelines invoke:
  `eng/AfterSigning.targets`, `eng/Publishing.props`, `eng/Signing.props`,
  version computation, asset-manifest emission, and any `eng/**/*.ps1` a pipeline
  step runs

CI behaviour is **not only** in `.github/` or `eng/pipelines/`. A PR can change
*how CI selects, builds, or runs* without touching a workflow or pipeline file.
Treat these as infra changes too, and validate them the same way:

- `tools/**` — C# tools that CI invokes (e.g. test-selection, runsheet, and
  summary generators). These usually have their own `Infrastructure.Tests`
  classes — run them.
- `eng/**/*.props`, `eng/**/*.targets` — MSBuild test plumbing that decides what
  gets built, packed, partitioned, or run, and on which OS.
- `eng/**/*.json` and similar config/data — test-selection rules, retry patterns,
  and matrix/partition config consumed by CI.

The test for "is this an infra change": *if it can alter which jobs run, which
tests are selected, or how artifacts are built in CI, it is* — categorize it here
and validate it, even when no `.github/` or `eng/pipelines/` file was touched.

**Not in scope for this skill:** changes under `.agents/skills/**` (these skill
docs themselves), `docs/**`, and other repo documentation. The `pr-testing`
skill does **not** test skill or doc edits — there is nothing to run for them.
Don't dogfood the CLI or trigger CI for a skills-only or docs-only PR.

If the PR *also* changes product code, run the normal `SKILL.md` scenarios for
that part and this playbook for the infra part. For an infra-only PR, **skip the
CLI dogfood install and template scenarios entirely** — they validate nothing
here.

## How to think about an infra PR

The steps below (I-1…I-5 for GitHub, Track B for AzDO) are the mechanics. What
generates them — and the fallback when a change matches no category or table row
— is seven questions. Answer all seven for every infra PR; each points to the
step that operationalizes it and to its line in the report's "CI Infrastructure
Validation" section:

1. **What's the blast radius?** CI is a graph — a changed script, action,
   reusable workflow, data file, job, or artifact affects every transitive
   consumer, often in a *different* workflow or pipeline. Build that set first;
   it, not the changed-file list, is what you validate. → I-3.E/I-3.G/I-4;
   Track B `dependsOn`. (Reports as *Dependency graph*.)
2. **What does PR CI already prove — and what does nothing prove?** Partition
   the blast radius: runs-and-gates on this PR (note it), script logic covered
   by `Infrastructure.Tests` (PR CI runs those for you — don't re-run), and
   nothing. The "nothing" set is this skill's job — and on AzDO it is the whole
   set, since no pipeline runs on a GitHub PR. → I-1/I-1b/I-2; Track B.
   (Reports as *What runs on this PR* + *Automated tests*.)
3. **What could have silently stopped running?** A removed or narrowed trigger,
   a deleted workflow, `continue-on-error`, or reshaped enumeration/matrix
   makes CI pass *by doing less* — and a broken non-PR workflow merges green
   and only fails later on `main`, a schedule, or a release. Prove coverage was
   conserved against a baseline. → I-1b(1)/I-5. (Reports as the *coverage-loss
   audit* evidence bullet.)
4. **Is anything here generated, or a duplicated list?** Lock files compile
   from `.md` sources; some `paths:` lists and YAML-contract tests must stay in
   sync with the workflows they mirror. Regenerate and compare — don't trust
   the hand edit. → I-2 (contract tests)/I-3.C; Failure modes §4/§6. (Reports
   as *gh-aw*.)
5. **Per behavioral change, what observable would a correct run produce?** A
   log marker, an artifact's shape, a binlog target, a skip *for the right
   reason*. Green only proves no step hard-failed; a change with no nameable
   observable is itself a finding. → I-5; Track B validation. (Reports as
   *Results validation*.)
6. **What's the least-privileged run that produces that observable?** Climb
   only as far as needed: static parse → unit test/fixture → `dry_run` → fork
   dispatch (proves wiring only) → upstream with explicit permission →
   maintainer-run (side-effecting workflows; AzDO def-1602 via
   `azdo-internal`). → I-4; Track B loop. (Reports as *Manual triggers*;
   AzDO *Run vs. baseline*.)
7. **Which runtime-context gotchas apply?** Unit tests catch script *logic*;
   what ships broken is *context* — trigger events, permissions/tokens,
   fork/bot actors, paths filters, runner OS, lock drift, artifact wiring.
   Scan the matching failure-mode tables. → Failure modes §1–§8; Track B's
   traps live in the `azdo-internal` skill. (Reports as *Failure-modes
   scan*.)

# Track A — GitHub Actions

## Step I-1: Determine what runs automatically on THIS PR

Work out, from the changed-file list, which workflows GitHub will actually
trigger on the PR, then confirm those runs go green — and, per Step I-5, that they
actually *validated the change*, not merely that they completed.

| Workflow | Runs on the PR when… |
|----------|----------------------|
| `ci.yml` | Always, **unless every** changed file matches a glob in `eng/github-ci/ci-skip-entirely-patterns.txt` (then build/test jobs skip). |
| `tests-quarantine.yml`, `tests-outerloop.yml` | **Only** if the PR touches one of: that file itself, `specialized-test-runner.yml`, `run-tests.yml`, `build-cli-e2e-image.yml`. Otherwise schedule/dispatch only. **The PR run is one project only** — a plumbing smoke test, not full coverage (see below). |
| `markdownlint.yml` | On every PR — `pull_request` with **no** `paths` filter. |
| `polyglot-validation.yml`, `typescript-api-compat.yml`, `typescript-sdk-tests.yml`, `extension-e2e-tests.yml` | **Reusable (`workflow_call`)** — not triggered directly. They run via `tests.yml` (itself called by `ci.yml`), so they execute on the PR when `ci.yml`/`tests.yml` run, subject to their own internal `if:` / path gating inside `tests.yml`. |
| `pr-docs-check` (gh-aw) | On PR **close/merge** (`pull_request: [closed]`, gated on `merged == true`) + `workflow_dispatch` — **not** on PR open. |
| `labeler-predict-pulls.yml` | On PR open — `pull_request_target` (`types: opened`), so it runs for fork PRs too. |
| Everything else (release, backmerge, milestone, auto-rerun, update-*, refresh-*, agentic `*.md`) | Not on PRs — `schedule` / `workflow_dispatch` / `workflow_run` / release events. **Must be triggered manually** (Step I-4). |

Concrete check for the CI-skip path:

```bash
# Will ci.yml skip build/test for this PR? Compare changed files to the skip globs.
git --no-pager diff --name-only origin/main...HEAD
cat eng/github-ci/ci-skip-entirely-patterns.txt
```

If you changed `tests-quarantine.yml` / `tests-outerloop.yml` / their shared
deps, the path-triggered run **will** appear on the PR — wait for it and verify
it passes before merge. These are easy to break precisely because they normally
run only on schedule.

### Quarantine / outerloop on a PR run only ONE project (plumbing smoke, not coverage)

When `tests-quarantine.yml` / `tests-outerloop.yml` (or their shared deps) are
path-triggered on a PR, the run is **deliberately one project, not the whole
suite.** `eng/AfterSolutionBuild.targets` filters the combined runsheet to the
first project (keeping its OS variants) for `pull_request` events on the
`QuarantinedTestRunsheetBuilder` / `OuterloopTestRunsheetBuilder` runners —
literally "picking a single test project as a sanity check." So a green PR run
proves the runner plumbing works for *one* project; it does **not** prove your
change is right for all of them. Decide which validation you actually need:

- **Changed the test *invocation* — the command line each project runs**
  (`extraTestArgs`, `extraRunSheetBuilderArgs`, the `dotnet test` / MTP args,
  retry or env setup in `run-tests.yml` / `specialized-test-runner.yml`): the
  one-project PR run is **not enough**. Manually dispatch the workflow so the
  full project set runs — the single-project filter is gated on
  `event == 'pull_request'`, so a `workflow_dispatch` run skips it and exercises
  the new command line against *every* project:

  ```bash
  gh workflow run tests-outerloop.yml --ref <branch>     # or tests-quarantine.yml
  gh run watch "$(gh run list --workflow tests-outerloop.yml -L1 --json databaseId \
    --jq '.[0].databaseId')" --exit-status
  ```

- **Changed test *enumeration / splitting*** — the runsheet builder, the
  `split-test-*.ps1` scripts, or the matrix: don't judge by a test run at all.
  **Inspect the emitted runsheet** — `combined_runsheet.json` and
  `runsheet.binlog` upload as the `logs-runsheet` artifact — and confirm the
  project list is complete and the shards/splits are partitioned as intended
  (every expected project present exactly once, no empty shard, OS variants
  correct). This is the Step I-5 "no fewer than baseline" check applied to the
  enumeration itself, before any test even runs.

## Step I-1b: What CI does NOT cover (this is pr-testing's job)

PR CI already runs the unit and e2e CLI tests, and the workflows wired to
`pull_request`. Re-running those is **not** the point of this exercise — assume CI
covers them. The value is the validation CI *cannot* do on the PR. Three things CI
will not catch on its own; always check them when the diff touches them:

**1. Coverage-loss / gating audit (CI can't notice it stopped gating itself).**
When a diff **removes or narrows** `on.pull_request` / `paths`, **deletes** a
workflow, adds job-level `continue-on-error: true`, or changes an aggregator's
`needs:` / required-check allowlist, CI on the PR will happily go green while
silently no longer protecting anything. Enumerate every workflow/job that no
longer gates the PR and require explicit proof the replacement covers it.

```bash
# Surface trigger/gating changes in this PR:
git --no-pager diff origin/main...HEAD -- .github/workflows \
  | grep -E '^[+-]' \
  | grep -E 'pull_request|paths:|continue-on-error|needs:|workflow_dispatch|schedule'
```

**2. Non-PR-event (push / main / release) behavior.** A PR's CI run only exercises
the `pull_request` path. If a change makes jobs *conditional* (skip on PR, run on
push), the PR run can't prove the push/main path still works. Confirm the `if:` /
matrix conditions keep the job on non-PR events, and that the aggregator `needs:` /
required-check list still gates push and `release/*`.

**3. Behavioral invariants of code CI never executes on the PR.** AzDO scripts,
`schedule`/`workflow_dispatch`-only workflows, side-effecting safe-output handlers,
and new helper scripts without a harness do not run on the PR. For each, name the
one **load-bearing invariant** (idempotency, false-positive/false-negative,
always-`exit 0`, "fail → run all") and check it without a real run using the
no-harness techniques in Step I-3.A (lint, dry-run, fixture). "No harness" is the
start of the analysis, not the end of it.

## Step I-2: Know what PR CI already covers (don't duplicate it)

The repo unit-tests its workflow helper scripts (JavaScript, PowerShell, and
bash) and asserts YAML contracts in `tests/Infrastructure.Tests/`. PR CI runs
that project **only when `ci.yml`'s test job isn't skipped** — and it *is*
skipped when every changed file matches a glob in
`eng/github-ci/ci-skip-entirely-patterns.txt`, which includes `eng/pipelines/**`
and `auto-rerun-transient-ci-failures.*` (see Step I-1). So for an infra-only PR
that touches just those paths, CI does **not** run `Infrastructure.Tests` for
you — run the matching class locally. When the test job does run, use the map
below to know which changed files CI already validates (so you don't re-run
them) and, more usefully, which have **no** coverage — those are the gaps to
call out and hunt in Step I-3. Re-run a class locally for fast pre-CI feedback,
when CI skipped it, or when CI isn't available (`node`/`pwsh` required on PATH).

```bash
# All workflow-script + pipeline tests (Linux-only project; ~minutes):
dotnet test --project tests/Infrastructure.Tests/Infrastructure.Tests.csproj \
  --no-launch-profile -- \
  --filter-not-trait "quarantined=true" --filter-not-trait "outerloop=true"

# Or target one class, e.g. the auto-rerun JS:
dotnet test --project tests/Infrastructure.Tests/Infrastructure.Tests.csproj \
  --no-launch-profile -- \
  --filter-class "*.AutoRerunTransientCiFailuresTests" \
  --filter-not-trait "quarantined=true" --filter-not-trait "outerloop=true"
```

| Changed file | Test class |
|--------------|-----------|
| `.github/workflows/auto-rerun-transient-ci-failures.js`, `eng/test-retry-patterns.json` | `AutoRerunTransientCiFailuresTests` |
| `.github/workflows/create-failing-test-issue.js`, `workflow-command-helpers.js` | `CreateFailingTestIssueWorkflowTests`, `CreateFailingTestIssueToolTests` |
| `eng/scripts/build-test-matrix.ps1` | `BuildTestMatrixTests` |
| `eng/scripts/split-test-projects-for-ci.ps1` | `SplitTestProjectsTests` |
| `eng/scripts/split-test-matrix-by-deps.ps1` | `SplitTestMatrixByDepsTests` |
| `eng/scripts/expand-test-matrix-github.ps1` | `ExpandTestMatrixGitHubTests` |
| `eng/scripts/stage-native-cli-tool-packages.ps1` | `StageNativeCliToolPackagesTests` |
| `eng/pipelines/**` release/npm | `ReleasePublishNugetPipelineTests`, `NpmCliPackageTests` |

A changed helper with a matching test class — whether `*.js`, `*.ps1`, or `*.sh`
— is **covered by CI when the test job runs** (note it and move on); if `ci.yml`
skipped the test job for this PR (above), run that class locally instead. The
valuable case is a
changed helper with **no** test: an inline `run:` block, a `*.sh` with no harness,
or e.g. the `check-changed-files` action's bash glob→regex logic (none). Say so in
the report — "no automated coverage" is a finding, a candidate for a new test, and
a prompt to validate that logic by hand here.

Some of these classes also include **YAML-contract assertions** — they read the
workflow `.yml` text and assert key trigger / safety-rail lines are present
(e.g. `WorkflowYamlKeepsDocumentedSafetyRails` in `AutoRerunTransientCiFailuresTests`).
If you changed a workflow's `on:`, `if:`, permissions, or job gating, a contract
test may need updating — a red contract test here is signal, not noise.

## Step I-3: Validate by category

### A. Workflow helper scripts (any language)

A "workflow helper" is **any script a workflow runs** — there's no mandate that
it be JavaScript. In this repo helpers take several forms, and a new one can be
written in whatever language fits:

- **`.github/workflows/*.js`** — `github-script` modules (only a few exist).
- **Inline `run:` blocks** in `shell: bash` or `shell: pwsh` — the **most common**
  form by far (dozens of each). These live inside the `.yml` and have no
  standalone unit test unless the logic was extracted.
- **Extracted `eng/scripts/*.ps1` / `*.sh`** — see Section B; these are the ones
  that usually *do* have an `Infrastructure.Tests` class.
- **Composite-action logic** (`.github/actions/**`) — see Section E.

To validate, regardless of language:

- If a matching `Infrastructure.Tests` class exists (Step I-2), run it — that's
  the JS *and* the PowerShell/bash extracted-script case.
- A JS module is `require()`d by a `*.harness.js` and driven from C#; you can also
  run it directly with `node` against a representative JSON fixture.
- For **inline** bash/pwsh that has no harness, you can't unit-test it in place:
  lint it (`bash -n`, or `pwsh -NoProfile -Command "[System.Management.Automation.Language.Parser]::ParseFile(...)"`),
  extract a representative fixture and run the snippet directly, or — the durable
  fix — move the logic into an `eng/scripts/*` file with its own test, then
  validate it as an extracted script (Section B).
- The YAML that *calls* the helper is not exercised by the unit test — eyeball
  the `on:`, `permissions:`, and `if:` of the workflow, and consult the Failure
  modes tables below.

### B. eng scripts — PowerShell & bash (`eng/scripts/*.ps1`, `*.sh`)

- Run the matching `*Tests` class (Step I-2); they invoke the script via `pwsh`
  (or `bash`).
- For matrix-generating scripts, run the script directly and sanity-check the
  emitted JSON (valid JSON, every expected project present exactly once, no empty
  shard).

### C. gh-aw agentic workflows (`.md` → `.lock.yml`)

**Only do this when the PR actually changes gh-aw files** — a
`.github/workflows/*.md` agentic source, a generated `*.lock.yml`, or anything
under `.github/aw/`. If no gh-aw files changed, skip this section entirely; don't
run `gh aw compile` for an unrelated PR.

The `.lock.yml` is **generated** from the `.md` source by `gh aw compile`. Never
hand-edit the `.lock.yml` body. The canonical guidance lives in
`.github/agents/agentic-workflows.agent.md` — read it; do not duplicate it.

The core invariant to verify on any gh-aw PR:

```bash
# Validate, then recompile and confirm the lock file matches the source.
gh aw compile --validate
gh aw compile <workflow-name>
git --no-pager diff -- .github/workflows/<workflow-name>.lock.yml
```

- A non-empty diff after recompile means the committed lock file was **stale**
  (the `.md` was edited without recompiling) — flag it.
- The `# gh-aw-metadata:` header line carries `compiler_version` and
  `frontmatter_hash`. Confirm the `compiler_version` matches what the rest of the
  repo's lock files use (run `gh aw compile --help` / `gh aw version` to confirm
  flag spelling and version if unsure — flags can change between gh-aw releases).
- For a **Dependabot** PR that edits a `*.lock.yml`'s action SHAs: do **not**
  merge directly — recompile via `gh aw compile --dependabot` from the `.md`
  source (see the agent doc, "Fix Dependabot PRs").
- The runtime lock-file traps (stale-check overrides, cross-repo `safe_outputs`
  checkouts, branch resolution) are in Failure modes §6.

### D. Regular workflows (`.github/workflows/*.yml`)

Static, fast, do these always:

```bash
# YAML parses (one example with python; any YAML parser is fine):
python3 -c 'import sys,yaml;[yaml.safe_load(open(f)) for f in sys.argv[1:]]' \
  .github/workflows/<changed>.yml
# Embedded bash blocks are syntactically valid (extract and `bash -n`), and:
git --no-pager diff --check        # trailing whitespace / conflict markers
```

Then walk the Failure modes tables — for a `.yml` PR the trigger/permission/fork
gotchas are where the real bugs hide. Then trigger it (Step I-4) if it doesn't
run on the PR.

### E. Reusable workflows & composite actions (validate through callers)

A reusable workflow (`on: workflow_call`, e.g. `run-tests.yml`, `tests.yml`) and a
composite action (`.github/actions/**`) have no standalone PR run — **don't try to
test them in isolation.** Instead find every caller and make sure the callers get
exercised:

```bash
# callers of a reusable workflow:
grep -rn "uses:.*<name>.yml" .github/workflows/
# callers of a composite action:
grep -rn "uses: ./.github/actions/<name>" .github/workflows/
```

- Validate **through** the callers: each caller's own PR CI run (where it runs on
  the PR) plus the relevant `Infrastructure.Tests` / path-triggered workflows.
- A change to a reusable workflow or action can affect **every** caller — check
  each one, not just the obvious path. If a caller doesn't run on this PR, that
  caller still has to be covered (its own CI, a manual dispatch, or a unit test).
- The `check-changed-files` glob→regex logic has **no** unit test; if it changed,
  reason through the conversion by hand and test representative globs locally.
- Watch the action's own `actions/checkout` / token / `fetch-depth` assumptions
  (see Failure modes: Reusable-workflow & composite-action wiring).

### F. Azure DevOps pipelines (`eng/pipelines/**`, `eng/common/**`)

AzDO pipelines do **not** run on a GitHub PR at all. They have their own track —
see [Track B](#track-b--azure-devops-pipelines). Don't try to validate a
pipeline change from the GitHub side beyond a static YAML parse; the real
validation is a run on the `dnceng/internal` mirror.

### G. Artifact & job dependencies (producer → consumer)

CI is a **graph**: jobs depend on each other via `needs:`, pass scalars via
`outputs`, and pass files by `actions/upload-artifact` (producer) →
`actions/download-artifact` (consumer). The consumer is frequently in a
**different workflow** — pulled via `workflow_run` or a cross-workflow download.
A change to *what a job produces* (artifact **name**, **contents**, directory
**layout**, or the producing job itself) breaks every consumer downstream, and
the producer's own run still goes green. So whenever the diff touches a producing
job or an artifact's shape, find and check the consumers:

```bash
# The producer side you changed (artifact name / the job that uploads it):
grep -rn "upload-artifact" -A4 .github/workflows/<changed>.yml | grep -iE 'name:|path:'
# Every consumer of that artifact name — often in OTHER workflows:
grep -rn "name: <artifact-name>" .github/workflows/        # both up- and download sites
grep -rln "workflow_run:" .github/workflows/               # cross-workflow consumers
# Same-run job graph: who needs: the job you changed (and reads its outputs)?
grep -rn "needs:\|\.outputs\." .github/workflows/<changed>.yml
```

- Real example: `built-nugets` (and `built-nugets-for-<rid>`) is produced by
  `build-packages.yml` and consumed by `extension-e2e-tests.yml`,
  `polyglot-validation.yml`, and others. Renaming it, changing its on-disk layout
  (e.g. `Debug/Shipping/*.nupkg` nesting), or dropping a file from it passes the
  producer's CI and breaks each consumer's download/extract step.
- An artifact whose consumer **doesn't run on this PR** (e.g. a nightly or
  dispatch-only workflow downloads it) is exactly the Step I-4 manual-trigger
  case — fold those consumers into the affected-workflow set and dispatch them.
- Validate the consumer in a real run (Step I-5): confirm its download step found
  the artifact and the files it expects are present at the path it reads. The
  runtime traps for this graph (soft-fail downloads, empty `outputs`,
  silently-skipped dependents) are in Failure modes §8.

## Step I-4: Trigger every affected workflow that doesn't run on the PR

First **enumerate the complete set of workflows this diff affects** — not just
the changed `.yml` files. A changed script, composite action, reusable workflow,
or data file affects *every workflow that consumes it*, and most of those don't
run on the PR. Build that set before triggering anything:

```bash
# Workflows that consume a changed script / action / reusable workflow / data file.
# Run for each changed eng/ script, .github/actions/<name>, reusable *.yml, or data file:
git --no-pager diff --name-only origin/main...HEAD
grep -rln "<changed-basename>" .github/workflows/        # e.g. build-test-matrix.ps1, run-tests.yml
grep -rln "uses: ./.github/actions/<name>" .github/workflows/
```

Add to the set every workflow that consumes an artifact a changed job produces —
the consumer set you already traced in Step I-3.G.

Then, for **each** workflow in that set, decide from Step I-1 whether it runs on
the PR. The ones that **don't** (the common case — `schedule` /
`workflow_dispatch` / `workflow_run` / release-gated) are yours to validate by
running them. Examples of frequently-affected dispatch-only workflows:
`deployment-tests.yml` (`workflow_dispatch` + nightly; takes a `pr_number`
input), `tests-daily-smoke.yml` (`workflow_dispatch` + daily; takes a `quality`
input), the agentic `*.md` workflows, and the release/backmerge family — but
treat these as illustrations, not a checklist. **Drive the list from the diff,
not from this paragraph.**

For anything that isn't wired to `pull_request`, the only real validation is to
run it. Prefer the least-privileged path that still exercises the change:
`dry_run` if the workflow exposes it, then your **fork**, then — only with the
user's explicit permission — the upstream repo.

```bash
# If the workflow has workflow_dispatch (many do), trigger and watch it:
gh workflow run <workflow>.yml -f dry_run=true        # if it exposes dry_run
# e.g. a PR-build deployment run: gh workflow run deployment-tests.yml -f pr_number=<n>
gh run watch "$(gh run list --workflow <workflow>.yml -L1 --json databaseId \
  --jq '.[0].databaseId')" --exit-status
```

- `workflow_dispatch` runs the workflow file from the **branch you dispatch**, so
  you can test your change before merge by pushing the branch and dispatching it
  there. Exception: `workflow_run`-triggered workflows always run the **default
  branch** version of the file — your change only takes effect after it lands on
  `main`.
- **Fork dispatch often proves only wiring, not behavior.** Many workflows guard
  the real work with `if: github.repository_owner == 'microsoft'` or need repo
  secrets that forks don't get. Dispatched on a fork, the job body **skips** —
  you've confirmed the trigger fires and nothing more.
- **To actually exercise an org-gated workflow you must run it on the upstream
  `microsoft/aspire` repo, which means pushing your PR branch to the upstream
  remote. That is privileged and visible — ASK THE USER for explicit permission
  first, and never push to the upstream remote unprompted.** With permission
  (here `<upstream>` is the `microsoft/aspire` remote, `<branch>` your PR branch):

  ```bash
  git push <upstream> HEAD:<branch>
  gh workflow run <workflow>.yml --repo microsoft/aspire --ref <branch> [-f dry_run=true]
  gh run watch <run-id> --repo microsoft/aspire --exit-status
  git push <upstream> --delete <branch>   # clean up the temporary upstream branch
  ```

- **Real side effects, no `dry_run`:** some workflows create PRs, file/close
  issues, push branches, or post comments (release, backmerge, pr-docs-check, the
  AzDO build-notify pipeline, …). Running them — even upstream — performs those
  actions for real. Get explicit maintainer approval, prefer a throwaway
  validation branch, and otherwise validate statically and lean on the PR's own
  required checks. Never trigger one against `microsoft/aspire` just to "see if it
  works."
- `auto-rerun-transient-ci-failures.yml` has a `dry_run` input that produces the
  full analysis summary without requesting a rerun — use it to validate matcher
  changes against a real failed CI run id.

A green `--exit-status` from a dispatched run is necessary but **not** sufficient —
always follow it with Step I-5 against that run.

## Step I-5: Validate the run did what the change intended (green ≠ validated)

A workflow finishing green only proves no step hard-failed. It does **not** prove
the change did what it was for. Once a relevant run exists — the PR's own CI, a
path-triggered run, or one you dispatched (Step I-4) — confirm the change's intent in
the run's **outputs**, not its status. For each behavioral change in the diff, name the
**observable** it should produce in a real run and go check that observable.

**Added or changed a validation/check step → prove it actually ran and evaluated the
thing.** A check that silently didn't run (false `if:`, wrong `steps.<id>` ref, early
`exit 0`, a glob that matched nothing) is worse than no check — it reports success while
protecting nothing. Pull the job log and find the step's own output:

```bash
gh run view <run-id> --repo microsoft/aspire --log | grep -n "<marker the new step prints>"
# A skipped step emits no log lines and shows "skipped" in the job graph — confirm the
# assertion actually evaluated; don't settle for "job completed".
```

If the change is meant to **fail** on bad input, a green run on good input proves nothing
— exercise the failure path (dispatch with a crafted bad input / fixture) and confirm it
goes red for the right reason.

**Changed artifacts, artifact layout, archives, or packaging → download the output and
inspect its shape.** The build step running is not proof the artifact is correct. Pull the
run's artifacts and verify names, directory layout, presence/absence of files, archive
contents, and counts against the intended structure:

```bash
gh run download <run-id> --repo microsoft/aspire -D ./_artifacts
# then: unzip -l <archive>; find ./_artifacts -maxdepth 3; diff the layout against intent.
```

Where a verify script already exists, run it against the downloaded artifact instead of
eyeballing — e.g. `eng/scripts/verify-cli-tool-nupkg.ps1`, `verify-cli-archive.ps1`,
`verify-cli-npm-package.ps1`, `verify-aspire-skills-bundle.ps1`. If a packaging change has
no such check, that absence is itself a finding.

**Build behavior changed → read the binlog, not just the console.** CI build jobs emit
MSBuild binlogs via `/bl:` — e.g. `BuildAndArchive.binlog`, `BuildCli.binlog`,
`BundlePayload.binlog` under `artifacts/log/...` (`run-tests.yml`,
`build-cli-native-archives.yml`) and `SendToHelix.binlog` on the AzDO Helix path. Download
the binlog and confirm the change took effect: the target/task ran, the property/condition
evaluated as intended, and the right items were (or were not) included.

**Meant to skip something → prove it was skipped, not that it ran anyway (or that
everything skipped).** For a `paths:` filter, a CI-skip pattern, test-selection, or a job
`if:`, confirm in the run that the intended job/step/test shows **skipped for the right
reason** — and that *unrelated* jobs were **not** also skipped. A too-broad skip that drops
real coverage looks identical to success on the status line (the Step I-1b coverage-loss
trap, caught in the run instead of the diff).

**Changed test enumeration, the matrix, or job structure → prove the run executes no
*fewer* tests/projects than the baseline.** Reshaping how tests are discovered, partitioned,
sharded, or fanned out can silently *drop* projects or whole shards while every job still
goes green — the run passes precisely because it ran less. Capture a baseline from a recent
`main` run and compare counts, don't just eyeball green:

```bash
# Baseline (recent main run) vs. this PR's run — compare the discovered/emitted set, not status.
gh run view <baseline-run-id> --json jobs --jq '.jobs[].name' | sort > /tmp/jobs.base
gh run view <pr-run-id>       --json jobs --jq '.jobs[].name' | sort > /tmp/jobs.pr
diff /tmp/jobs.base /tmp/jobs.pr        # jobs/shards that disappeared are the red flag
# For matrix-generating scripts, diff the emitted project list directly:
pwsh eng/scripts/<matrix-script>.ps1 ... | jq -r '.[].project' | sort | uniq | wc -l
```

Any project/shard present on `main` but absent here must be explained (intentionally
removed) or fixed — a smaller test set is a regression unless that *is* the change.

If a change produces no observable you can read back from a run, say so — that itself is a
gap.

## Failure modes to hunt for

These are the bugs that pass every unit test and still break in production. For a
given PR, scan the tables that match what it touches; for each relevant row,
either confirm the change is safe or call it out. **Spend your budget on the gaps you
can't infer from the diff alone** — platform / org-policy / runner quirks (empty-input
bypass, masked-secret outputs, `released` vs `published`, two-dot diffs), not what a
careful read of the change already reveals; and don't re-run what PR CI covers (Step I-2).
Cited PRs are real prior breakages (illustrative, not exhaustive).

### 1. Triggers & events

| Gotcha | What to check |
|--------|---------------|
| `release: [released]` never fires for programmatic releases (GitHub emits `published`). | Grep `on:` for `types: [released]`; for pipeline-created releases it should be `[published]`. Update both the `.md` and `.lock.yml`. (#17271, #17322) |
| A workflow that creates a release/PR/push with the default `GITHUB_TOKEN` does **not** trigger any downstream `workflow_run`/`release`/`push` workflow — those events are intentionally not cascaded. | If the step is meant to chain-trigger another workflow, confirm it uses an **App token**, not `github.token`/`secrets.GITHUB_TOKEN`. (release-github-tasks.yml) |
| `workflow_run` workflows run on the **default-branch** file and in base context; `workflow_run.pull_requests` is **empty** for push-triggered CI. | Confirm PR-number handling tolerates an empty `pull_requests` array; gate on `workflow_run.event == 'pull_request'` if it should only act on PR runs. (auto-rerun) |
| `types: [closed]` fires for PRs closed **without** merging. | Confirm the first job's `if:` includes `github.event.pull_request.merged == true`. (#16167) |
| A diff that **removes/narrows** `on.pull_request`/`paths`, deletes a workflow, or adds job-level `continue-on-error` silently stops gating the PR — CI still goes green. | Run the Step I-1b coverage-loss audit: enumerate every workflow/job that no longer gates this PR and require proof the replacement covers it. (#17996) |
| A job made **conditional** (skip on PR, run on push) is only proven on the PR path by CI. | Confirm the `if:`/matrix keeps it on push/`main`/`release/*` and the aggregator `needs:`/required-check allowlist still gates those events. (#17973) |
| Editing a workflow's `on:`/`if:`/permissions can desync a YAML-contract test. | Re-run the relevant `Infrastructure.Tests` class; update the contract assertion if the change is intentional. |

### 2. Auth, permissions & tokens

| Gotcha | What to check |
|--------|---------------|
| Missing permission scope → 403/422 at runtime, invisible to tests. Mutating a PR via the Issues API needs **`pull-requests: write`** alongside `issues: write` (PRs are issues). | Diff the `permissions:` block; cross-check every GitHub API the workflow calls has a matching scope. (#16174) |
| `create-github-app-token@v3` with explicit `permission-*` inputs 422s if **any** listed scope isn't granted to the App installation (`@v2` requested all-granted and never 422'd). | Enumerate each `permission-*` input and confirm the App installation grants it; e.g. `administration: read` is often **not** granted. (#17765) |
| Microsoft org policy blocks `GITHUB_TOKEN` from **creating PRs**. | Any `gh pr create` / `pulls.create` must use an App token, minted **after** any actor-permission gate. (backport.yml) |
| `git push` over a `bearer` `http.extraHeader` fails non-interactively; and `actions/checkout` leaves a persisted `GITHUB_TOKEN` extraheader that wins over a later App-token push (suppressing the `synchronize` event so CI never re-runs). | Use the `https://x-access-token:<token>@host/repo.git` remote form + `GIT_TERMINAL_PROMPT=0`; unset `http.https://github.com/.extraheader` before pushing with a different token. (#17737, #17109) |
| GitHub Actions **strips masked secrets from job outputs** — an App token passed via `needs.<job>.outputs.<token>` arrives empty downstream. | Tokens must be minted in the **same job** that uses them; grep for `needs.*.outputs.*token*` used as a `token:`. (#15929) |

### 3. Fork / bot / actor context

| Gotcha | What to check |
|--------|---------------|
| `pull_request` (not `pull_request_target`) workflows can't access secrets on **fork** PRs; secret-dependent steps silently skip. | If the workflow needs secrets and must handle fork PRs, it needs `pull_request_target` (and must not execute untrusted PR head code), or document the fork exclusion + a `workflow_dispatch` fallback. (pr-docs-check) |
| gh-aw activation gate sees a **bot/App** actor's repo permission as `none` and silently skips all jobs. | For workflows triggered by bot label/push/release actions, confirm `bots: [<app-slug>]` is in the `.md` frontmatter and `GH_AW_ALLOWED_BOTS` is in the compiled `.lock.yml`. (#17869) |
| Downstream workflows on `pull_request: [labeled]` fire **once** per label; a re-run needs the label removed and re-added. | If a creating workflow may re-attach an already-present trigger label, confirm it removes+re-adds (with an App token, per org policy). (#17737) |

### 4. Paths filters & trigger scoping

| Gotcha | What to check |
|--------|---------------|
| Too-**broad** `paths:` on a build-heavy reusable workflow re-triggers it on every eng/CI change and exhausts runner disk. | New `pull_request` reusable-workflow triggers must scope `paths:` to just the orchestrating YAMLs, not `src/`/`**`. Simulate which files match before merge. (#12143, #15921) |
| `tests-quarantine.yml` and `tests-outerloop.yml` carry **independent** shared `paths:` lists that must stay in sync. | Editing one → edit the other identically (each also lists itself). The only acceptable diff between their lists is the self-entry. (COPILOT INSTRUCTIONS comment at top of both files) |
| Two-dot `git diff base..head` reports files changed on **base** since branch-point as PR changes; three-dot `base...head` is merge-base correct. | Grep workflows/scripts for `git diff --name-only` — prefer three-dot for "files this PR changed". (#17220) |
| Editing `eng/github-ci/ci-skip-entirely-patterns.txt` itself is a skippable change, so a typo there **won't** be caught by CI. | Validate new globs by hand against the action's conversion rules; consider temporarily removing the file's self-skip to force one validating CI run. |

### 5. OS / shell / runner portability

| Gotcha | What to check |
|--------|---------------|
| BSD `sed`/`grep` on macOS don't support `\s`, `\d`, `\w`, `\b`; scripts that work on Linux silently mismatch or error on `macos-latest`. | Replace with POSIX classes (`[[:space:]]`, `[[:digit:]]`, `[[:alnum:]_]`); smoke-test new shell on a macOS shell, not just Linux. |
| Windows defaults write **UTF-16** log/output files (MSBuild `WriteLinesToFile`, `cmd echo >`, PS5.1 `Set-Content`) — binary garbage to Linux steps. | Force UTF-8: `Encoding="UTF-8"`, `-Encoding utf8`, `[Console]::OutputEncoding=UTF8`, `chcp 65001` on Helix/Windows pre-commands. (#15772) |
| 2-core `windows-latest` runners hang under CPU contention (VBCSCompiler resident + heavy test parallelism + frequent PowerShell heartbeat). | New Windows test jobs: `UseSharedCompilation=false`, `dotnet build-server shutdown` before tests, longer heartbeat interval, reduce parallelism. (#15834) |
| Switching critical-path / high-fanout jobs to a different runner **label or size** (e.g. `N-core-ubuntu-latest`) is a **capacity** change: one PR run looks fine, but at full `main`/merge volume the pool can throttle or exhaust and CI grinds to a halt. | Unprovable on the PR — confirm the label is provisioned with adequate org quota, check the PR's own jobs didn't sit queued, and prefer a canary / staged rollout. The regression only appears post-merge at fleet scale. (#13916 → reverted #13948) |
| macOS cert/TLS tests fail if the login keychain is locked. | Test jobs on `macos-latest` needing dev-certs must call `./.github/actions/unlock-macos-keychain` first (no-op elsewhere). |
| SHA-pinned actions at old releases run deprecated Node (16/20) and will fail when GitHub enforces deprecation. | New `uses:` blocks must copy a current blessed SHA already used elsewhere in the repo, never a stale tag/SHA. (#15544, #15829, #17269) |

### 6. gh-aw lock files

| Gotcha | What to check |
|--------|---------------|
| `.md` edited without `gh aw compile` → stale `.lock.yml`; the runtime uses the lock, so the change has no effect. | Recompile and assert **no diff** (Step I-3.C). |
| Cross-repo `safe_outputs` checkout at the workspace root breaks the PR-creation handler ("repository not found in workspace"). | Each non-primary repo checkout in `safe_outputs` needs an explicit `path:`. (#15960) |
| Compiler can strip manual lock-file overrides on recompile unless `stale-check: false` is set. | If the workflow relies on lock overrides, confirm `stale-check: false` in the `.md` and that overrides survive a recompile. (#16019) |
| Agent re-deriving a cross-repo target branch is non-deterministic / wrong when branch topologies differ. | Branch resolution must be a deterministic `pre-agent-steps:` shell block writing a file the agent reads verbatim. (#16950) |

### 7. Reusable-workflow & composite-action wiring

| Gotcha | What to check |
|--------|---------------|
| GitHub Actions passes `""` (not null) for unset `workflow_call` inputs, **bypassing** the input-level `default:`. | Inputs consumed as shell args need an `${{ inputs.X \|\| 'fallback' }}` guard in `env:`, not just a `default:`. (#15921, `run-tests.yml`) |
| A step output reference (`steps.<id>.outputs.x`) that names a non-existent step id silently resolves empty — passes YAML lint, wrong at runtime. | Confirm every `steps.<id>.outputs.*` `<id>` matches a real step in the same job; confirm `jq` paths match the actual JSON shape (e.g. `.properties.flag` vs `.flag`). (`specialized-test-runner.yml`) |
| `check-changed-files` uses a three-dot diff needing the PR **base SHA**; a shallow checkout (`fetch-depth: 1`) makes the diff fail → empty change list → CI silently **skipped**. | Any caller of `check-changed-files` must `actions/checkout` with `fetch-depth: 0`. |
| `with:`/`secrets:` passed to a reusable workflow that the callee doesn't declare in `on.workflow_call` are dropped. | Cross-check every `with:`/`secrets:` key at the call site exists in the callee's `workflow_call.inputs`/`secrets`. |

### 8. Artifact & job dependencies (producer → consumer)

The tracing mechanics — find every consumer of a changed artifact or job,
including consumers that don't run on this PR — are Step I-3.G. These are the
runtime traps that survive a correct-looking trace:

| Gotcha | What to check |
|--------|---------------|
| `download-artifact` for a missing/renamed artifact can **soft-fail** (empty dir, `if-no-files-found` not set), so the consumer continues with nothing and "succeeds". | Confirm the consumer fails loudly on a missing artifact, and verify in the run that the download actually pulled files. |
| A scalar passed via `needs.<job>.outputs.<x>` is empty if the producing job didn't set it (masked secrets are stripped from outputs entirely). | Confirm the producer `echo`s the output and the consumer's `needs:` lists that job; never route a token through `outputs` (§2). |
| Removing/renaming a job others `needs:` makes the dependents **skip** (not error), silently dropping them from the graph. | Grep `needs:.*<job>` across the workflow; confirm no dependent silently drops, and that the aggregator still gates on the renamed job. |

# Track B — Azure DevOps pipelines

**No AzDO pipeline runs on a GitHub PR.** A change to `eng/pipelines/**`,
`eng/common/**`, or the signing/packaging/publishing plumbing those pipelines
call can pass every GitHub check and still break the internal build, signing,
packaging, or release. For anything non-trivial, the only real validation is a
run on the `dnceng/internal` mirror.

**Use the `azdo-internal` skill** for the mechanics — it owns preflight
access, discovering the mirror remote, pushing, triggering, monitoring,
branch rules, dry-run validation of publish/release-only changes, and the
reduce-to-one-job technique. Load it and follow it for those. The
**validation process** for an infra change — capture a baseline, iterate
cheaply, watch for contributor-branch skips, and prove no regression — is
the loop below; the skill doesn't duplicate it. This section is the routing
and the must-not-skip validation bar.

## Which AzDO pipeline?

| Pipeline | Definition / trigger | When a change here needs a run |
|----------|----------------------|--------------------------------|
| **Internal build** `eng/pipelines/azure-pipelines.yml` | def **1602**, `dnceng/internal` mirror | Most `eng/pipelines/**`, `eng/common/**`, signing/packaging/version/asset-manifest changes. **This is the default target** — run it via the `azdo-internal` skill. |
| **Unofficial** `azure-pipelines-unofficial.yml` | dev/unofficial build | Changes scoped to that pipeline. |
| **Release** `release-publish-nuget.yml` | separate definition; consumes def-1602 artifacts | Publish / NuGet / installer-promotion logic. Side-effecting — read the skill's dry-run caveats. |
| **Public / Helix** `azure-pipelines-public.yml` | weekly + `/azp run aspire-tests` | Helix test routing / test execution. Different pipeline, different breakage patterns — see `docs/ci/azdo-public-pipeline.md`; out of scope for the internal skill. |

## The loop (handed off to `azdo-internal`)

1. **Preflight access** to `dnceng/internal`. No access → hand the run to a
   maintainer, or fall back to offline validation (unit-test the changed scripts
   under `tests/Infrastructure.Tests`). Never block on a run you can't do.
2. **Discover the mirror remote by URL** (`…/dnceng/internal/_git/microsoft-aspire`)
   — the local remote name is whatever you chose; don't assume one. Add it if
   missing.
3. **Capture a baseline** from a recent good def-1602 run on `main`: `buildId`,
   result, stages/jobs that ran, and the artifact set produced.
4. **Push the branch to the mirror** under a personal `<alias>/` branch (never
   `main` / `release/*` / `internal/release/*`).
5. **Trigger + monitor** def 1602 on that branch (mind auto-trigger-on-push and
   the `az pipelines run` HTTP-timeout caveat — the build usually queued anyway).
6. **Validate the change took effect** — see the bar below.
7. **Prove no regression** vs. the baseline, and record both `buildId`s in the PR.

## Validate the run did what the change intended (green ≠ validated)

Apply the **Step I-5** discipline to the AzDO run — per behavioral change, name
the observable and read it back from the run, not just the status: the step's
own log marker, the artifact's shape (`az pipelines runs artifact list` /
`download`, then `eng/scripts/verify-cli-*.ps1` where one exists), or the
MSBuild binlog (e.g. `SendToHelix.binlog` on the Helix path). A perf/refactor
change must produce the **same artifact set** as the baseline. What is
*different* on AzDO:

- **Read the timeline, not the headline result.** The headline `result` hides
  per-task outcomes; query the build *timeline* for failed records
  (`az devops invoke --area build --resource Timeline --route-parameters project=internal buildId=<BUILD_ID> --org https://dev.azure.com/dnceng`,
  then filter `result=='failed'`). An empty
  list means green-enough even when the header says `partiallySucceeded`
  (SDL/Component-Detection noise). Pull the specific task log to find your
  change's marker.
- **Dependencies chain via `dependsOn` and across pipelines.** Artifacts flow
  `PublishBuildArtifacts`/`PublishPipelineArtifact` (producer) →
  `DownloadPipelineArtifact`/`download` (consumer) — including **across
  pipelines**: the release pipeline (`release-publish-nuget.yml`) consumes
  def-1602's build via a `resources.pipelines:` (`aspire-build`) input. If your
  change alters what a stage produces (artifact name, contents, layout) or
  reorders/removes a stage, confirm every downstream `dependsOn`/`download` still
  resolves — and that the **release** pipeline, which you can't run from a feature
  branch, still finds the artifacts it expects (validate its `download` inputs and
  the produced names statically, and flag for a maintainer run).
- **Distinguish a real break from an expected contributor-branch skip.** Publish /
  BAR / release stages are gated on `main`/`release` and **will** skip on an
  `<alias>/` branch — that's by design, not a regression. Branch-gated variable
  groups can inject a 1ES Branch-control gate that blocks stages before any work
  runs. Confirm such skips against the skill's branch rules before calling a run
  broken.

# Evidence to capture & report

Fill in the **CI Infrastructure Validation** section of the `SKILL.md` report
(Step 10) and PR comment (Step 11) — that template enumerates the per-track
evidence (what runs on the PR, automated tests, manual triggers, results
validation, dependency graph, gh-aw, failure-modes scan, AzDO baseline vs.
validation run). Two notes on filling it in, plus what the template doesn't ask
for:

- In the failure-modes line, include gotchas you confirmed are *not* a problem,
  so the reviewer sees they were considered.
- In the AzDO line, call out expected contributor-branch skips (branch-gated
  stages) so a reviewer doesn't read them as breakage.
- **Changed-file categorization** (not in the template): per category, what was
  validated.
- **CI coverage vs. the gap** (not in the template): which changed files PR CI
  already tests (note, don't re-run), and which have **no** coverage — the gaps
  you validated by hand.
- **Coverage-loss audit** (not in the template): for any removed/narrowed
  trigger, deleted workflow, or `continue-on-error` added, what no longer gates
  the PR and how the replacement was confirmed (or "n/a — no gating change").
