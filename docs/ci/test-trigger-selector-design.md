# Test trigger selector — design

A design for a tool that takes a PR's changed files and emits the set of test
projects and CI jobs to run, so PR CI runs a relevant subset instead of the full
matrix.

Companion documents:

- [`test-trigger-map.md`](./test-trigger-map.md) — the descriptive path → target map.
- [`eng/test-trigger-map.yml`](../../eng/test-trigger-map.yml) — its machine-readable form.

**Status: enforcing.** `tests.yml`'s `setup_for_tests` runs `SelectTests`
*before* `enumerate-tests`. When `ENFORCE_SELECTION: 'true'` and the selection is
not ALL, the selector writes an `OverrideProjectToBuild` props file so
`enumerate-tests` builds and enumerates only the selected projects; in audit mode
(`ENFORCE_SELECTION: 'false'`) it writes no props and `enumerate-tests` produces
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

After direct attribution, Layer 1 walks `ProjectGraphNode.ReferencingProjects`
transitively. This produces every downstream project that can be broken by the
change.

The output is the affected project base names: the `.csproj` filename without
extension. `TestSelector.Select(...)` intersects test-project names with the
matrix and matches production-project names against `affected_project_rules`.

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

Only five selector matchers exist; `groups` are reusable target bundles:

- **`conventions`**: `<name>`-capture pattern → target template, additive and
  existence-guarded.
- **`ignore`**: globs Layer 2 accounts for with no target, so they do not trip
  the run-all fallback.
- **`path_rules`**: the general path-glob → targets matcher (`test:` / `job:` /
  group / `ALL`).
- **`affected_project_rules`**: an affected production project name glob
  → targets.
- **`derived_targets`**: if any selected test matches, add more targets to a
  fixpoint.

These are sourced from the corresponding sections of `test-trigger-map.yml`.

## The tool (`tools/SelectTests`)

`SelectTests` is a small C# console tool, run *before* `enumerate-tests`. It
decides which test projects are affected; `enumerate-tests` then builds and
shards only those.

Main options:

- `--repo-root`: repository root, defaulting to the current directory.
- `--map`: curated map path, defaulting to `eng/test-trigger-map.yml`.
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

1. Resolve changed files for Layer 2 path matching.
2. Compute Layer 1 affected project names unless `--force-all` or
   `--skip-layer1` is set.
3. Apply Layer 2 `conventions`, `ignore`, and `path_rules` for each changed
   file.
4. Apply `affected_project_rules` to Layer 1 production-project names.
5. Apply `derived_targets` to a cycle-safe fixpoint.
6. Escalate to `ALL` for a kill switch, an `ALL` path rule, or an unattributed
   `src/**` file that is not under a project directory in `Aspire.slnx`.
7. Emit the per-job booleans, and — in enforce mode for a non-ALL selection —
   the `OverrideProjectToBuild` props restricting the downstream build.

Selection only decides *which* projects survive. OS expansion, timeouts,
`requiresNugets` / `requiresCliArchive` flags, and the matrix split stay owned
by the existing scripts (downstream of `enumerate-tests`).

## Pipeline integration

The flow in `tests.yml`'s `setup_for_tests` job:

```text
checkout -> restore
  -> SelectTests (--from base --to head; curated map + Layer 1 in process)
       -> run_* outputs + summary
       -> (enforce && !ALL) BeforeBuildProps.props (OverrideProjectToBuild)
  -> enumerate-tests (action; checkout/restore reused; beforeBuildPropsPath)
       -> all_tests JSON {"include":[...]} (only the selected projects in enforce)
  -> split-test-matrix-by-deps.ps1
  -> run-tests.yml (per-dependency matrices)
```

`SelectTests` runs first; `enumerate-tests` reuses the job's checkout+restore
(`checkout: 'false'`, `restore: 'false'`) so the props file survives — a fresh
checkout's `git clean` would otherwise remove it. The split, per-OS/per-dependency
bucketing, and `run-tests.yml` are unchanged.

The `run_*` step outputs become `setup_for_tests` job outputs that gate every
non-.NET job, such as `polyglot_validation`, `typescript_sdk_tests`,
`typescript_api_compat`, extension jobs, `cli_starter_validation_windows`, and
the WinGet/Homebrew installer-prepare jobs.

The .NET test jobs need no `run_*` gate: they are already gated by their matrix
bucket being empty once `enumerate-tests` produces only the selected projects.
Base builds stay ungated because they are upstream `needs:` that run whenever a
dependent runs.

The extension-unit jobs (`extension_tests_win` / `extension_bootstrap_linux`)
gate on `run_extension_unit` **or** `run_extension_e2e`, because
`extension_e2e_tests` needs them. Gating them off while e2e runs would skip e2e
via need-propagation.

**Audit vs. enforce is a single knob in the `select_tests` step:
`ENFORCE_SELECTION`.** Audit (`'false'`, no `--enforce`) writes no restriction
props, so `enumerate-tests` builds the full matrix and `run_*` are all true, with
the advisory summary showing what enforcing would select.

Flipping `ENFORCE_SELECTION` to `'true'` makes the same selector return the
selective matrix and selective `run_*` outputs. The downstream gates do not need
to change.

The kill switch is wired in the same step: a `[full ci]` token in the PR body or
a `run-all-tests` label passes `--force-all`. Non-PR events (no base SHA at all,
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
- an unattributed `src/**` path.

Those are explicit selections of the full matrix. They are not fallbacks for a
crashed selector or a failed graph computation. (A PR whose base SHA cannot be
fetched is *not* one of them — that fails the step; see above.)

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

The sticky PR comment carries the same selection in a terser form: each test
project and job is listed with its single most-direct cause (e.g. a job pulled
in only because a test runs reads `via test <Name>`), with a `(+N more)` tail
when an item has several causes. The full per-item cause list, including the
rule `reason`, stays in the step summary.

Any audit run where a would-be-skipped test would have failed is a map bug,
fixed before enforcing. Once audit data shows the skip set is consistently safe,
flip to enforcing and keep the `[full ci]` kill switch.

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
