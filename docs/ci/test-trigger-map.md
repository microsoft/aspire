# Test trigger map

A map of **repo path → CI targets that must run** when a matching file changes,
covering the .NET test projects and the validation/polyglot jobs in
[`tests.yml`](../../.github/workflows/tests.yml).

The machine-readable form lives next to this doc:
[`test-trigger-map.yml`](./test-trigger-map.yml). The tool that consumes it and
the rollout plan are in
[`test-trigger-selector-design.md`](./test-trigger-selector-design.md).

## Two layers

Selective CI is split by **who can know a dependency**:

- **Layer 1 — derived (zero maintenance).** The MSBuild project-graph closure —
  `ProjectReference` edges, Central Package Management, `Directory.Build.*`, and
  file-level `<Compile Include>` of another project's source — is computed at
  runtime by [`dotnet-affected`](https://github.com/leonardochaia/dotnet-affected)
  over `Aspire.slnx`. It can never drift, so the map does **not** enumerate it.
  This is why there is no `leaf_source` / `core_source` / `test_hubs` section: a
  leaf integration → its own test, the core hosting fan-out, and the test-hub
  fan-out are all reproduced live by the graph tool.

- **Layer 2 — curated (this file).** Only what the project graph provably cannot
  see. The selector unions Layer 1's result with the rules here.

`dotnet-affected` is scoped to `Aspire.slnx`, so projects **not** in the solution
(template placeholders that crash discovery, `playground/**`, build tooling,
`eng/*.proj`) are Layer-1 blind spots — and therefore also Layer 2's
responsibility. That is a second reason the curated layer exists, beyond the
non-.NET jobs and loose-file reads.

## What stays curated here

Only four matchers exist — a section is its own key only when the selector treats
it differently. Everything that is "a glob set → a target set" lives in one
section (`path_rules`); the groupings inside it are comments.

| Section | What it is |
|---------|------------|
| `conventions` | `<name>`-capture pattern → target template, emitted only if the derived test exists (existence guard). Additive. Covers a test's own folder (`tests/<name>/**`) and the Hosting/Components integration dirs (a backstop for non-MSBuild files the graph can't attribute). |
| `ignore` | globs Layer 2 accounts for with **no** target (Layer 1 covers them, or inert), so they don't trip the run-all fallback |
| `path_rules` | a glob set → a target set (`test:` / `job:` / a group / `ALL`). The single general matcher: catch-all-to-`ALL`, convention misses, non-.NET jobs, and loose-file reads all live here under comment headers |
| `derived_targets` | "if any of these tests is selected, also run these jobs/tests" — a *test-set* relationship, not a file edge |
| `groups` | named, reusable bundles of `test:`/`job:` targets (expand recursively) |

The former `run_all_globs`, `test_self`, `convention_overrides`, `curated_jobs`,
`loose_file_deps`, `shared_source`, `shared_compiled_source`, and `gaps` sections
are **gone**: `run_all_globs` is a `path_rules` entry whose target is `ALL`;
`test_self` is a convention; the override/job/loose buckets are all `path_rules`
(they had no distinct selector behavior); and `dotnet-affected` owns the
link-compiled / `Components/Common` edges at runtime, so those are not curated.

## Target vocabulary

| Target | Maps to |
|--------|---------|
| `test:<Name>` | a .NET test project `tests/<Name>` (a `run-tests.yml` matrix entry) |
| `job:polyglot` | `tests.yml` → [`polyglot-validation.yml`](../../.github/workflows/polyglot-validation.yml) (py/go/java/rust/ts) |
| `job:typescript-sdk` | `tests.yml` → [`typescript-sdk-tests.yml`](../../.github/workflows/typescript-sdk-tests.yml) |
| `job:typescript-api-compat` | `tests.yml` → [`typescript-api-compat.yml`](../../.github/workflows/typescript-api-compat.yml) |
| `job:extension-unit` | `tests.yml` `extension_tests_win` + `extension_bootstrap_linux` |
| `job:extension-e2e` | `tests.yml` → [`extension-e2e-tests.yml`](../../.github/workflows/extension-e2e-tests.yml) (gated by `extension_e2e_changes`) |
| `job:cli-starter` | `tests.yml` `cli_starter_validation_windows` |
| `job:winget-installer` | `tests.yml` `prepare_winget_installer_artifacts` |
| `job:homebrew-installer` | `tests.yml` `prepare_homebrew_installer_artifacts` |
| `job:api-diffs` | [`generate-api-diffs.yml`](../../.github/workflows/generate-api-diffs.yml) — *schedule-only today* |
| `job:ats-diffs` | [`generate-ats-diffs.yml`](../../.github/workflows/generate-ats-diffs.yml) — *schedule-only today* |
| `job:deployment-e2e` | [`deployment-tests.yml`](../../.github/workflows/deployment-tests.yml) — *schedule/dispatch-only today* |
| `ALL` | full test matrix + all jobs |
| `<GROUP_NAME>` | a named group (see `groups:`) expanding **recursively** to its `test:`/`job:` members |

Base builds (packages, CLI native archives, installer artifacts, the CLI E2E
image) are not modelled as targets: they are upstream `needs:` of the targets
above and run whenever any dependent target runs. Their **workflow files** are in
the catch-all `path_rules` entry (target `ALL`) because a change to *how* they
build can affect every consumer.

## Rule categories

Rules are **additive**: a changed file activates the union of targets from every
rule whose glob it matches, plus the Layer 1 projects. A `path_rules` entry whose
target is `ALL` selects the whole matrix. Group names in a rule's targets expand
recursively to their members.

### Named groups (`groups`)

Reusable bundles of `test:` and/or `job:` targets, so a rule can map to a named
set instead of repeating it. Example:

```yaml
groups:
  CLI_BUNDLE: [test:Aspire.Cli.EndToEnd.Tests, job:cli-starter, job:extension-e2e]
```

Group members may themselves be group names; expansion is recursive and
cycle-safe.

### Conventions (`conventions`)

A `<name>` capture (one path segment) → a target template, emitted only when the
derived test exists in the matrix (existence guard), and additive. This includes
a test project's own folder and the integration/component backstop:

```
tests/<name>/**               -> test:<name>
src/Aspire.Hosting.<name>/**  -> test:Aspire.Hosting.<name>.Tests
src/Components/<name>/**       -> test:<name>.Tests
```

`tests/Shared/**`, `src/Aspire.Hosting.Azure.CosmosDB/**`, `Orleans`, etc. have no
same-named test, so the guard drops them and they fall through to `path_rules` /
Layer 1.

### Catch-all → `ALL`

A single `path_rules` entry whose target is `ALL`. Build infrastructure and
broadly shared code re-run everything. Examples:

```
global.json, NuGet.config, .config/dotnet-tools.json
Directory.Build.*, Directory.Packages.props, Aspire.slnx
eng/*.props, eng/*.targets, eng/common/**, eng/OuterPreBuild.proj
src/Shared/**, tests/Shared/**
.github/workflows/tests.yml, run-tests.yml, build-packages.yml, ...
```

Note `eng/OuterPreBuild.proj` (build-wide project-name validation) is here, but
`eng/Bundle.proj` is **not** — it assembles only the CLI bundle, so it maps to
`CLI_BUNDLE`, not `ALL`.

### Ignore (`ignore`)

Files Layer 2 deliberately accounts for with **no** target, so they don't trip
the run-all fallback. Each is either covered precisely by Layer 1 (the graph sees
the foreign `<Compile Include>` consumers) or is inert:

```
src/Components/Common/**                          # link-compiled into many components; Layer 1 covers (verified: 71 tests)
src/Vendoring/OpenTelemetry.Instrumentation.*/**  # glob-compiled into Redis/Kafka components; Layer 1 covers
src/Vendoring/OpenTelemetry.Shared/**             # compiled by nothing (each instrumentation dir has its own Shared/); inert
```

### Path rules (`path_rules`)

The one general glob→targets matcher. `targets` may be `test:` / `job:` / a group
name / `ALL`. Comment headers in the YAML group the entries by intent; the
selector treats them all identically. Highlights:

- **convention misses** — `src/Aspire.Hosting.Azure.*/** → test:Aspire.Hosting.Azure.Tests`
  (the Azure hosting integrations have no dedicated test; the aggregate test covers them),
  `src/Aspire.Hosting.Integration.Analyzers/** → test:Aspire.Hosting.Analyzers.Tests`.
- **non-.NET jobs** — `job:polyglot` ← `src/Aspire.TypeSystem/**`, the `CodeGeneration.*`
  generators, `src/Aspire.Cli/**`, …; `job:extension-unit` / `job:extension-e2e` ← `extension/**`;
  `job:typescript-api-compat` / `job:ats-diffs` ← `src/Aspire.Hosting*/**` (so a hosting integration
  change triggers these, and **every** Hosting file is "matched" by Layer 2 — a non-MSBuild Hosting
  change never falls to the run-all fallback).
- **loose-file deps** — `eng/clipack/**` (read by `Aspire.Cli.Tests`), `eng/winget/**` /
  `eng/homebrew/**` (installer manifests → `Aspire.Acquisition.Tests` + the installer jobs),
  `src/Aspire.ProjectTemplates/**` (template hive install), `playground/**`, `.github/workflows/**`
  (asserted by `Infrastructure.Tests`), `eng/Bundle.proj` → `CLI_BUNDLE`.

### Derived targets (`derived_targets`)

"If **any** of these test projects is selected (by either layer), also run these
targets." Applied to the union of Layer 1 and Layer 2 selected tests, to a
fixpoint (a `test → test` edge whose target has its own rule is followed; cycles
terminate). This is how a job fires based on *which tests run*, not on which file
changed:

```yaml
- tests: [test:Aspire.Cli.Tests, test:Aspire.Cli.EndToEnd.Tests]
  targets: [job:cli-starter]
- tests: [test:Aspire.Acquisition.Tests]
  targets: [job:cli-starter, job:winget-installer, job:homebrew-installer]
```

## Maintenance

The map is hand-curated; there is no generator. The verifier tests
(`Infrastructure.Tests/TestTriggerMap/TestTriggerMapTests.cs`) keep it honest and
tell you exactly what to fix when the repo changes.

Steady state:

1. Make the change. The graph closure (Layer 1) tracks itself — you do **not**
   edit the map for a new `ProjectReference`, a new `src` project in the solution,
   or a new foreign `<Compile Include>` edge (the graph reports those live).
2. Run the verifier. Each failure names the offending path/project/target:
   - new `src` project neither in `Aspire.slnx` nor matched by a rule → add a
     `path_rules` entry (or add it to the solution);
   - a `src/Aspire.Hosting.Azure.<service>` (or other convention-miss) dir whose
     non-MSBuild changes should run a specific test → add a `path_rules` entry;
   - renamed/removed test project, job, or path → fix the name/glob.
3. The selector's audit summary lists **unattributed changed files** — files no
   Layer 2 rule matched, not ignored, and not owned by a slnx project. A new
   non-.NET job or runtime file read shows up there, prompting a `path_rules`
   addition.

The hand-owned knowledge (`conventions`, `ignore`, `path_rules`, `derived_targets`)
encodes dependencies a fresh codebase read cannot recover, so carry it forward;
never silently regenerate it.

## Caveats

- **Convention is a backstop, not the primary signal.** Normal compiled `.cs`
  changes are attributed by Layer 1; the convention exists for the non-MSBuild
  files the graph can't see, and to keep the common case selective instead of
  falling to the run-all fallback.
- **Run-all fallback.** A changed `src/**` file that no Layer 2 rule matched, that
  isn't ignored, and that isn't under a project in `Aspire.slnx` forces the full
  matrix. A missed test is a silent regression; an extra run is just slower.
  Non-`src` leftovers are audit-only.
- **Safety vs. selectivity.** The catch-all `ALL` rule, the run-all fallback, and
  the kill switch err toward `ALL`; otherwise the selector relies on Layer 1 for
  `src` coverage and the convention backstop for non-MSBuild files.
- **Schedule/outerloop-only targets.** `api-diffs`, `ats-diffs`, `deployment-e2e`,
  and `Aspire.EndToEnd.Tests` are not in the regular PR matrix today; their rules
  give the *would-be* trigger paths.
- **Integration dirs with no test.** `src/Aspire.Hosting.Orleans`,
  `Aspire.Hosting.AppHost`, and `Aspire.Hosting.Tasks` have no dedicated test
  project. Their MSBuild files are owned by Layer 1 and matched by the
  `src/Aspire.Hosting*/**` job rules; nothing special is needed for them.

