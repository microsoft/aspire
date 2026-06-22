# Test trigger selector — design

A design for a tool that takes a PR's changed files and emits the set of test
projects and CI jobs to run, so PR CI runs a relevant subset instead of the full
matrix.

Companion documents:

- [`test-trigger-map.md`](./test-trigger-map.md) — the descriptive path → target map.
- [`eng/github-ci/test-trigger-map.yml`](../../eng/github-ci/test-trigger-map.yml) — its machine-readable form.

**Status: audit.** `tests.yml`'s `setup_for_tests` runs the `select-tests` action
*before* `enumerate-tests`. When its `enforce: 'true'` and the selection is
not ALL, the selector writes an `OverrideProjectToBuild` props file so
`enumerate-tests` builds and enumerates only the selected projects; in audit mode
(`enforce: 'false'`) it writes no props and `enumerate-tests` produces
the full matrix unchanged while the summary still reports what enforcing would
have skipped.

Audit mode does not soften Layer 1 failures. If the affected-projects graph
cannot be computed, `SelectTests` fails the step because under-selecting would
silently skip real tests.

## Goal

Input: the list of files changed in a PR. Output:

- the subset of `test:<Project>` entries to run, and
- which non-.NET jobs to trigger (`job:polyglot`, `job:extension-e2e`, …).

Select *before* enumeration: pick the affected test projects, then have
`enumerate-tests` build/shard only those — do **not** enumerate the full matrix
and filter it after.

## Why not just consume `test-trigger-map.yml`?

The map mixes two kinds of rule. The large graph-derived edges (a leaf
integration → its own test, the core fan-out, and foreign linked-file consumers)
are mechanically derivable from the `.csproj` graph and go stale the moment a
project or a `ProjectReference` changes.

So the design splits by **who can know the dependency**:

- **Layer 1 — derived (zero maintenance):** changed file → owning project
  → reverse-dependency closure → affected test projects. Computed from the live
  MSBuild graph every run, so it can never drift.
- **Layer 2 — curated:** only what the MSBuild graph cannot see — the non-.NET
  jobs, runtime/loose-file reads, and convention backstops.

## Layer 1 — derived, in process

Layer 1 is implemented by
[`tools/SelectTests/GraphAffectedProjects.cs`](../../tools/SelectTests/GraphAffectedProjects.cs).
It builds an MSBuild `ProjectGraph` from `Aspire.slnx` at the PR head.

The graph is **HEAD-only**. It never evaluates from-commit project content; the
diff is used only to identify changed paths.

### Changed paths

When `--from` / `--to` are supplied, Layer 1 reads changed files with:

```text
git diff --name-status -M <from> <to>
```

Deletes are included. Renames include both the old path and the new path, so a
cross-project move marks both the project that lost the file and the project
that gained it.

`--changed-files` is a path-only input for local/debug runs. It does not carry
rename/delete status, so each line is treated as a present changed path.

### File → project attribution

Layer 1 indexes evaluated `ProjectInstance` inputs for every graph node:

- project files themselves;
- `ProjectInstance.ImportPaths`, including repo hook files imported through
  SDK/Arcade targets that live in the NuGet cache, such as `eng/Versions.props`
  and `Directory.Build.props`;
- evaluated items resolved through their `FullPath` metadata.

The indexed item types include `Compile`, `Content`, `None`,
`EmbeddedResource`, `AdditionalFiles`, and other registered types from each
project's `AvailableItemName` items, such as `Protobuf`.

Using each item's resolved `FullPath` matters for linked/shared files. A source
file linked into multiple projects maps to every project that consumes it, not
just to the directory where the file physically lives.

Files not found in that index fall back to longest-prefix project directory
containment. This covers deleted files, the old side of a cross-project rename,
and project-owned files that are not modeled as one of the indexed item types.

### Reverse closure and output

After direct attribution, Layer 1 walks the reverse dependency edges
transitively (BFS), producing every downstream project that can be broken by
the change.

The edges come from each project's **declared** `<ProjectReference>` items, not
from `ProjectGraphNode.ReferencingProjects` / `ProjectReferences`. A
solution-based `ProjectGraph` flattens those collections into the *transitive*
closure (e.g. `AppTests → Mid → Core` surfaces as `AppTests` referencing both
`Mid` and `Core` directly), which would lose the intermediate hops. The
declared items are the real one-hop edges, so BFS over them yields both the
correct affected set *and* the genuine shortest hop chain to each project.

The output is the affected project base names: the `.csproj` filename without
extension. `TestSelector.Select(...)` intersects test-project names with the
matrix and matches production-project names against `affected_project_rules`.

#### Decision paths (traceability)

The BFS records each affected project's predecessor (set on first, shortest
reach) plus the changed file that seeded its chain. `AffectedResult.Paths` thus
carries, per affected project, an ordered chain
`changed file → directly-changed project → … → affected test`. The selector
attaches this to each Layer 1 cause (`Cause.Path`), and the renderers surface
it: the step summary shows the full chain
(`src/Core/Core.cs → Core → Core.Tests`), while the PR comment groups every test
reached from a seed file under that file's heading (in a "via the project graph"
bucket). Only a single representative shortest path per project
is tracked — alternate longer paths are not enumerated.

### Why a HEAD-only graph

The selector deliberately avoids `dotnet-affected` for Layer 1.

`dotnet-affected` reads from-commit blobs through a libgit2-backed MSBuild
virtual filesystem to diff packages. That has two CI-breaking constraints:

- it crashes whenever the diff touches `Directory.Packages.props`, because it
  eager-loads `global.json` as MSBuild XML
  (`leonardochaia/dotnet-affected#155`);
- it cannot run inside a git worktree.

A HEAD-only graph never evaluates from-commit content, so both constraints
disappear. Two-commit central-package diffing is intentionally not reproduced:
Layer 2 routes `Directory.Packages.props` to `ALL`.

### Why no `Microsoft.Build.Prediction`

Layer 1 does not use `Microsoft.Build.Prediction`.

The evaluated-item index (`FullPath`) plus `ImportPaths` and
`AvailableItemName`-registered item types reaches every file class that matters
for this selector. It was measured equal-or-superset of a prediction-based index
for cross-project linked `.cs`, `.proto`, linked `.json`, and `.resx` changes.

The evaluated-item index was strictly better for deleted files under a project
directory, because the containment fallback can still attribute the removed
path. Prediction's only diff-relevant unique catch was `global.json`, which
Layer 2 already routes to `ALL`.

Avoiding prediction keeps Layer 1 self-owned and avoids another third-party
dependency.

### Why root at `Aspire.slnx`

`ProjectGraph` follows `ProjectReference` edges. An Arcade `Build.proj`-style
root expresses its build set as `ProjectToBuild` items, so using that shape as
the graph root does not produce the repository project graph.

Evaluating `eng/Build.props`'s `ProjectToBuild` items would also make selection
depend on build flags and the current RID. Flags such as `SkipNativeBuild`,
`BuildBundleDepsOnly`, and `SkipTestProjects` differ across CI jobs.

That project set is also a net loss for test selection:

- it adds test-less leaves, such as RID-specific `eng/dcppack`,
  `eng/dashboardpack`, and `eng/clipack` packaging projects, plus
  `playground/**` sample apps;
- it drops `tools/**` projects that `Aspire.slnx` includes and that affect real
  tests through `Infrastructure.Tests`.

`Aspire.slnx` is deterministic, RID/flag-independent, and test-complete.
`ProjectGraph` auto-expands `ProjectReference`s, so every project reachable to a
test is in the graph even if it is not directly listed as a solution entry.

### MSBuild loading

`SelectTests` references:

- `Microsoft.Build` `18.3.3` with `ExcludeAssets=runtime`;
- `Microsoft.Build.Framework` `18.3.3` with `ExcludeAssets=runtime`;
- `Microsoft.Build.Locator` `1.9.1`.

The MSBuild engine assemblies are loaded from the repo-local SDK via
`MSBuildLocator`. The packages are available on the approved dnceng feeds, and
no external tool restore is required for Layer 1.

## Layer 2 — curated

Layer 2 is the hand-owned part in `test-trigger-map.yml`. It contains what the
MSBuild graph cannot infer:

- non-.NET jobs;
- runtime and loose-file reads;
- convention backstops for files that are not modeled as MSBuild inputs;
- conservative `ALL` routes for broad infrastructure changes.

Only five selector matchers exist (`conventions`, `ignore`, `path_rules`,
`affected_project_rules`, `derived_targets`); `groups` are reusable target
bundles. The per-section reference — what each matches, with examples and how to
edit — lives in [`test-trigger-map.md`](./test-trigger-map.md). Two facts are
load-bearing at the design level:

- **`prefilter`** is the only matcher that drops a changed file from **Layer 1's**
  input, not just Layer 2's. Its pattern list is **read at runtime** from
  `eng/github-ci/ci-skip-entirely-patterns.txt` — the same file the top-level
  `ci.yml` skip gate uses — so the selector and the gate can never drift (its glob
  syntax is the `check-changed-files` action's, ported in `ChangedFileFilter`).
  This is why `prefilter`, not `ignore`, is what stops a packed `README.md` from
  being attributed by the graph and fanned out: `ignore` only suppresses the
  Layer 2 run-all fallback, while Layer 1 still attributes an `ignore`d file.
- **`affected_project_rules`** matches Layer 1's affected **production** project
  names only; affected matrix *test* projects are filtered out first, so a
  test-only change cannot fire production jobs (`ats-diffs`, `extension-e2e`, …)
  through a glob like `Aspire.Hosting*`.

## The tool (`tools/SelectTests`)

`SelectTests` is a small C# console tool, run *before* `enumerate-tests`. It
decides which test projects are affected; `enumerate-tests` then builds and
shards only those.

Main options:

- `--repo-root`: repository root, defaulting to the current directory.
- `--map`: curated map path, defaulting to `eng/github-ci/test-trigger-map.yml`.
- `--slnx`: path to the solution that defines the project universe, defaulting to
  `<repo-root>/Aspire.slnx`.
- `--from` / `--to`: git refs for the PR diff.
- `--changed-files`: newline-delimited changed file list, instead of
  `--from` / `--to`.
- `--skip-layer1`: skip the graph closure for explicit diagnostics.
- `--force-all`: kill switch; force ALL.
- `--enforce`: write the restriction props for a non-ALL selection. Without this
  (audit), no props are written and `enumerate-tests` runs the full matrix.
- `--before-build-props`: path for the `OverrideProjectToBuild` props file
  (consumed by `enumerate-tests` via `BeforeBuildPropsPath`).

The test-project universe (the set an `ALL` selection expands to, and the
existence guard) is the `tests/<Name>/<Name>.csproj` projects ending in `.Tests`
in `Aspire.slnx` — derived directly from the slnx because the selector runs
before any matrix exists.

Flow:

1. Resolve changed files, then drop any matched by the `prefilter` (the CI
   skip-gate patterns file, read at runtime, minus `keep_routed`) — applied to
   **both** the Layer 2 path list and Layer 1's git diff, so an excluded file
   (e.g. a packed `README.md`) influences neither layer.
2. Compute Layer 1 affected project names (and the set of changed paths it
   attributed) unless `--force-all` or `--skip-layer1` is set.
3. Apply Layer 2 `conventions`, `ignore`, and `path_rules` for each changed
   file. A changed file that Layer 1 attributed (its attributed-paths set) is
   treated as Layer-1-owned, so a link-compiled `src/Shared`/`tests/Shared`
   file does not trip the run-all fallback even though it is under no project
   directory.
4. Apply `affected_project_rules` to Layer 1 **production**-project names only
   (affected matrix test projects are filtered out first, so a test-only change
   does not fire production jobs through a production-name glob).
5. Apply `derived_targets` to a cycle-safe fixpoint.
6. Escalate to `ALL` for a kill switch, an `ALL` path rule, or any changed file
   that survived the prefilter but is not Layer-1-owned (neither under a project
   directory in `Aspire.slnx` nor in Layer 1's attributed-paths set), not
   `ignore`d, and matched by no rule. The fallback is location-independent (not
   `src/**`-only): a missed test is a silent regression, so any unmapped change
   fails safe to the full matrix. Files that genuinely need no CI are dropped by
   the prefilter in step 1, so they never reach this fallback.
7. Emit the per-job booleans (as one `selection` JSON object), and — in enforce
   mode for a non-ALL selection — the `OverrideProjectToBuild` props restricting
   the downstream build.

Selection only decides *which* projects survive. OS expansion, timeouts,
`requiresNugets` / `requiresCliArchive` flags, and the matrix split stay owned
by the existing scripts (downstream of `enumerate-tests`).

## Pipeline integration

The flow in `tests.yml`'s `setup_for_tests` job:

```text
checkout
  -> select-tests (action: minimal SDK bootstrap; curated map + Layer 1 in process)
       -> selection JSON (run_<job> booleans) + summary
       -> (enforce && !ALL) project_override_props: BeforeBuildProps.props (OverrideProjectToBuild)
  -> enumerate-tests (action; checkout reused; own restore; beforeBuildPropsPath)
       -> all_tests JSON {"include":[...]} (only the selected projects in enforce)
  -> split-test-matrix-by-deps.ps1
  -> run-tests.yml (per-dependency matrices)
```

The `select-tests` action runs first; `enumerate-tests` reuses the job's checkout
(`checkout: 'false'`) so the props file survives — a fresh checkout's `git clean`
would otherwise remove it. Selection only needs a minimal SDK (the action's
`./dotnet.sh --version`), not a full repo restore; `enumerate-tests` does its own
restore (`restore: 'true'`). The split, per-OS/per-dependency bucketing, and
`run-tests.yml` are unchanged.

The `select-tests` action emits the per-job gates as a single generic `selection`
step output — a JSON object keyed `run_<job>` — so neither the action nor the
SelectTests tool enumerates the concrete jobs. `tests.yml` is where the per-job
names belong, so `setup_for_tests` unpacks that object into one boolean job
output per job, e.g.
`run_polyglot: ${{ fromJSON(steps.select_tests.outputs.selection).run_polyglot }}`.
Each non-.NET job then gates on plain `needs.setup_for_tests.outputs.run_<job> ==
'true'` (no `fromJSON` at the call site, and usable in a job-level `if:`, where
the `env` context is unavailable). Adding a trigger-map job means adding its
unpack line + its own `if:` — no change to the action or the tool.

The .NET test jobs need no `run_<job>` gate: they are already gated by their
matrix bucket being empty once `enumerate-tests` produces only the selected
projects. Base builds stay ungated because they are upstream `needs:` that run
whenever a dependent runs.

When a gated job is a `needs:` dependency of another gated job, its gate must be
the **or** of its own condition and its dependents', so need-propagation cannot
skip a downstream job whose dependency was gated off. This is a property of the
`needs:` graph, not of any particular job.

**Audit vs. enforce is a single knob: the `select-tests` action's `enforce`
input.** Audit (`'false'`, no `--enforce`) writes no restriction
props, so `enumerate-tests` builds the full matrix and every `run_<job>` output
is true, with the advisory summary showing what enforcing would select.

Flipping `enforce` to `'true'` makes the same selector return the
selective matrix and selective `run_<job>` outputs. The downstream gates do not
need to change.

The kill switch is wired in the same step: the `run-full-ci` PR label passes
`--force-all`. The label is read from the workflow event payload
(`contains(github.event.pull_request.labels.*.name, 'run-full-ci')`), a snapshot —
so adding the label takes effect on the next push (or reopen), not on a plain
re-run of an existing run. Non-PR events (no base SHA at all,
e.g. a push to `main`) also force the full set. A PR *with* a base SHA that
cannot be fetched in the shallow checkout **fails the step** instead of forcing
run-all: `base.sha` is always reachable on origin, so a fetch failure is a real
problem, and masking it with run-all would teach the audit nothing.

## Failure policy

Layer 1 is safety-critical. Any failure to compute the affected-projects graph
is fatal in audit and enforce modes.

The selector may still choose run-all intentionally for known-safe reasons:

- `--force-all`;
- a non-PR event with no diff base at all (e.g. a push to `main`);
- a changed path that matches an `ALL` rule;
- any changed file that survived the prefilter but is not Layer-1-owned, not
  `ignore`d, and matched by no rule (the run-all fallback — location-independent,
  not `src/**`-only).

Those are explicit selections of the full matrix. They are not fallbacks for a
crashed selector or a failed graph computation. (A PR whose base SHA cannot be
fetched is *not* one of them — that fails the step; see above.)

## Debuggability and traceability

Because a crash fails the step (never silently under-selects), the selector
makes failures and decisions easy to inspect after the fact.

**Crash diagnostics.** `Selection.Run` threads a `SelectionTrace` (current stage
+ current item) through the run. On any unhandled exception it appends a
`## SelectTests FAILED` block to the job step summary (and stderr) naming the
failing stage, the concrete item in hand, the exception, and the exact inputs
needed to re-run locally (change source, repo root, slnx, map, the mode flags),
then rethrows so the step still fails. Set `SELECTTESTS_TRACE=1` to also echo
every stage/item to stderr as it happens — a full ordered trail for the cases
where the last breadcrumb alone isn't enough.

**Per-item causes.** Every selected test project and job records *why* it was
selected — the changed file (convention / path rule), the affected production
project, the Layer 1 graph closure (with its full decision path), or the
selected test that pulled it in via `derived_targets`. These render in both the
step summary (verbose, with the full graph chain and the rule's reason text) and
the PR comment (terse).

**Selection record (`select-tests-selection.json`).** The selector writes a
machine-readable record — mode, the reproduce inputs, the changed / excluded /
unattributed files, the Layer 1 affected set, and every selected/skipped test
and job with its per-item causes (including decision paths) — to
`SELECT_TESTS_JSON_FILE`. The `select-tests` action uploads it as the
`select-tests-selection-<os>` artifact, so "why did this run?" can be answered
weeks later without re-running CI, and acceptance tests can assert against it.

## Measured selectivity

The graph has two structural "god edges" that make any hosting-integration or
data-component change fan out to roughly the hosting test cluster:

- `tests/Aspire.Hosting.Tests` `ProjectReference`s several integrations and is
  itself referenced by many hosting test projects.
- `tests/testproject` (`TestProject.AppHost`, `IntegrationServiceA`) references
  a broad component set, bridging data-component changes into the hosting
  cluster.

**This fan-out is accepted, not a defect to fix.** Running the affected hosting
cluster for a hosting-integration change is still far cheaper than the full
matrix, and pruning those edges would change what "affected" means for the test
owners.

The clean wins remain large and safe:

- CLI-only, Dashboard-only, extension-only, TypeScript-only, and polyglot-only
  changes stay tightly scoped.
- Component ↔ component isolation holds: an `Aspire.Npgsql` change does not pull
  unrelated Redis / RabbitMQ / MongoDB / Milvus component tests.

## Audit mode

Audit mode computes the subset and writes a `$GITHUB_STEP_SUMMARY`, but CI still
runs the full matrix and all jobs. The summary shows:

- the invocation mode and change source;
- selected test projects and triggered jobs, each annotated with **why** it was
  selected — the changed file, affected project, graph edge, or selected test
  that pulled it in, plus the curated rule's `reason` text;
- the would-have-been-skipped list;
- any `ALL` or kill-switch escalation and why;
- unattributed changed files that may need curated rules.

The PR comment carries the same selection in a more scannable form. It leads
with **what runs** — the flat list of selected test projects and the flat list
of selected jobs (test projects first, since they are the primary review
signal) — so a reviewer sees the full impact at a glance even when many files
changed. It then explains **how** the selection was reached in a collapsed
`<details>` (the heading is the `<summary>`, so the rationale stays out of the
way until expanded), grouping the selected projects under each trigger (changed
file, affected project, or derived test) that pulled them in: a changed file
and its graph fan-out appear under one heading, so a single edit's whole
closure is stated once rather than repeated per project, and large fan-outs
collapse into a nested `<details>`. Every cause is still shown — a project
selected by several triggers appears under each — and a per-job table names
what triggered each job. The comment is posted **one per pushed
commit** and links the head commit it was computed for: a re-run of the same
commit updates that commit's comment in place (no duplicate — re-runs are
common), a new commit posts a fresh comment at the bottom, and comments from
superseded commits are collapsed (minimized, never deleted) so the latest
selection surfaces at the bottom while the per-push history is preserved. In
audit mode the comment is advisory — the
full matrix and all jobs still run — so it is labelled "(audit mode)" and states
that the lists are what selective CI **would** run under enforcement.

Any audit run where a would-be-skipped test would have failed is a map bug,
fixed before enforcing. Once audit data shows the skip set is consistently safe,
flip to enforcing and keep the `run-full-ci` kill switch.

## Verifier test

`Infrastructure.Tests` keeps the curated layer honest:

- **Referential integrity:** every curated `test:` / `job:` target, including
  `affected_project_rules` and `derived_targets`, names a real test project or
  known job; every path glob is valid; every `affected_project_rules`
  project-name glob matches at least one project in `Aspire.slnx`.
- **Coverage:** every test project and every `src` project is reachable by some
  rule or by `Aspire.slnx`, so a newly added, unmapped project fails loudly
  instead of silently never running.

A convention-miss dir with no same-named test is intentionally not asserted.
Its MSBuild files are owned by Layer 1, and a non-MSBuild change there safely
hits a curated rule, the convention backstop, or the run-all fallback.

## Rollout

1. Run `SelectTests` in audit mode.
2. Watch the audit summaries and fix unsafe skips in the curated layer.
3. Flip to enforcing. Keep the kill switch and hard-fail Layer 1 policy.

## Future refinement

Refinement is about the curated layer staying accurate, not about changing the
graph:

- new non-.NET jobs or runtime file dependencies get curated rules;
- the verifier catches new unmapped projects;
- audit data flags any rule that under- or over-selects.

God-edge pruning is explicitly out of scope.
