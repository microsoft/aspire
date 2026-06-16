# Test trigger map

A map of **repo path → CI targets that must run** when a matching file changes,
covering the .NET test projects and the validation/polyglot jobs in
[`tests.yml`](../../.github/workflows/tests.yml).

The machine-readable form lives at
[`eng/github-ci/test-trigger-map.yml`](../../eng/github-ci/test-trigger-map.yml). The tool that consumes it and
the rollout plan are in
[`test-trigger-selector-design.md`](./test-trigger-selector-design.md).

## Two layers

Selective CI is split by **who can know a dependency**:

- **Layer 1 — derived (zero maintenance).** `tools/SelectTests` builds an
  MSBuild `ProjectGraph` from `Aspire.slnx` at the PR head and computes the
  reverse `ProjectReference` closure in process. It reports the full affected
  set — **production and test** projects.

  The graph indexes evaluated project inputs (`Compile`, `Content`, `None`,
  `EmbeddedResource`, `AdditionalFiles`, registered `AvailableItemName` types,
  and imports) by resolved `FullPath`. Linked/shared files therefore map to
  every consuming project. Deleted files and rename old paths fall back to
  longest-prefix project directory containment.

- **Layer 2 — curated (this file).** Only what the project graph provably cannot
  see. The selector unions Layer 1's result with the rules here.

Projects not represented by `Aspire.slnx` are Layer-1 blind spots and therefore
Layer 2's responsibility. That includes template placeholders, `playground/**`,
and RID-specific packaging projects under `eng/**`.

## What stays curated here

Only five selector matchers exist; a section is its own key only when the
selector treats it differently. Everything that is "a path glob set → a target
set" lives in one section (`path_rules`); the groupings inside it are comments.

`groups` are reusable bundles, not a matcher. `affected_project_rules` is
separate because it keys off the affected-**project** set by name, not file
paths.

| Section | What it is |
|---------|------------|
| `prefilter` | `{ patterns_file, keep_routed }`. Changed files matched by a pattern in `patterns_file` are **dropped before both layers run** (reaching neither Layer 1 nor Layer 2), except those carved out by `keep_routed` (files the selector routes to a target — `.github/workflows/**`, `eng/pipelines/**`, and the patterns file itself → `Infrastructure.Tests`). `patterns_file` is `eng/github-ci/ci-skip-entirely-patterns.txt`, the same list the top-level CI skip gate uses, read at runtime so the two can't drift; its glob syntax is the check-changed-files action's (ported in `ChangedFileFilter`). Why this — not `ignore` — is what stops the `README.md` fan-out: see [test-trigger-selector-design.md](./test-trigger-selector-design.md) §Layer 2. |
| `conventions` | `<name>`-capture pattern → target template, emitted only if the derived test exists (existence guard). Additive. Covers a test's own folder and the Hosting/Components integration dirs as a backstop for non-MSBuild files the graph cannot attribute. |
| `ignore` | globs Layer 2 accounts for with **no** target, so they do not trip the run-all fallback. Only needed for files Layer 1 *cannot* attribute (the inert `Vendoring/OpenTelemetry.Shared`); link-compiled `src/Shared` / `tests/Shared` / `Components/Common` files are already attributed by Layer 1 and need no entry. See [test-trigger-selector-design.md](./test-trigger-selector-design.md) §Layer 2. |
| `path_rules` | a path glob set → a target set (`test:` / `job:` / a group / `ALL`). The single general path matcher: catch-all-to-`ALL`, convention misses, non-.NET job loose-file triggers, and loose-file reads all live here under comment headers |
| `affected_project_rules` | an affected **production** project, matched by project-name glob against Layer 1's affected set, → a target set. Matched against production names **only** — affected matrix test projects are excluded, so a test-only change cannot fire production jobs via a glob like `Aspire.Hosting*`. Follows the graph's transitive closure |
| `derived_targets` | "if any of these tests is selected, also run these jobs/tests" — a *test-set* relationship, not a file edge |
| `groups` | named, reusable bundles of `test:`/`job:` targets that expand recursively |

The map stays small by keeping each dependency in the layer that can prove it:

- broad run-all paths are `path_rules` entries whose target is `ALL`;
- a test project's own folder is a convention;
- override/job/loose-file buckets are all `path_rules`, because they have no
  distinct selector behavior;
- linked-file, `Components/Common`, and `src/Shared` / `tests/Shared` `*.cs` edges
  are owned by Layer 1 at runtime (the attributed-paths set), so they need no
  curated entry;
- docs and other no-CI files are dropped up front by `prefilter`, reading the
  CI skip-gate patterns file.

## Target vocabulary

| Target | Maps to |
|--------|---------|
| `test:<Name>` | a .NET test project `tests/<Name>` (a `run-tests.yml` matrix entry) |
| `job:polyglot` | `tests.yml` → [`polyglot-validation.yml`](../../.github/workflows/polyglot-validation.yml) (py/go/java/rust/ts) |
| `job:typescript-sdk` | `tests.yml` → [`typescript-sdk-tests.yml`](../../.github/workflows/typescript-sdk-tests.yml) |
| `job:typescript-api-compat` | `tests.yml` → [`typescript-api-compat.yml`](../../.github/workflows/typescript-api-compat.yml) |
| `job:extension-unit` | `tests.yml` `extension_tests_win` + `extension_bootstrap_linux` |
| `job:extension-e2e` | `tests.yml` → [`extension-e2e-tests.yml`](../../.github/workflows/extension-e2e-tests.yml) |
| `job:cli-starter` | `tests.yml` `cli_starter_validation_windows` |
| `job:winget-installer` | `tests.yml` `prepare_winget_installer_artifacts` |
| `job:homebrew-installer` | `tests.yml` `prepare_homebrew_installer_artifacts` |
| `job:api-diffs` | [`generate-api-diffs.yml`](../../.github/workflows/generate-api-diffs.yml) — *schedule-only today* |
| `job:ats-diffs` | [`generate-ats-diffs.yml`](../../.github/workflows/generate-ats-diffs.yml) — *schedule-only today* |
| `job:deployment-e2e` | [`deployment-tests.yml`](../../.github/workflows/deployment-tests.yml) — *schedule/dispatch-only today* |
| `ALL` | full test matrix + all jobs |
| `<GROUP_NAME>` | a named group (see `groups:`) expanding **recursively** to its `test:`/`job:` members |

Base builds (packages, CLI native archives, installer artifacts, the CLI E2E
image) are not modelled as targets. They are upstream `needs:` of the targets
above and run whenever any dependent target runs.

Their **workflow files** are in the catch-all `path_rules` entry (target `ALL`)
because a change to *how* they build can affect every consumer.

## Rule categories

Rules are **additive**: a changed file activates the union of targets from every
matching rule, plus the Layer 1 projects. A `path_rules` entry whose target is
`ALL` selects the whole matrix.

Group names in a rule's targets expand recursively to their members.

### Named groups (`groups`)

Reusable bundles of `test:` and/or `job:` targets, so a rule can map to a named
set instead of repeating it. Example:

```yaml
groups:
  CLI_BUNDLE: [test:Aspire.Cli.EndToEnd.Tests, job:cli-starter, job:extension-e2e, job:winget-installer, job:homebrew-installer]
```

Group members may themselves be group names; expansion is recursive and
cycle-safe.

### Conventions (`conventions`)

A `<name>` capture (one path segment) → a target template, emitted only when the
derived test exists in the matrix (existence guard), and additive. This includes
a test project's own folder and the integration/component backstop:

```text
tests/<name>/**               -> test:<name>
src/Aspire.Hosting.<name>/**  -> test:Aspire.Hosting.<name>.Tests
src/Components/<name>/**      -> test:<name>.Tests
```

`tests/Shared/**`, `src/Aspire.Hosting.Azure.CosmosDB/**`, `Orleans`, and other
dirs with no same-named test produce nothing here. They fall through to
`path_rules` / Layer 1.

### Catch-all → `ALL`

A single `path_rules` entry whose target is `ALL`. Build infrastructure and
broadly shared code re-run everything. Examples:

```text
global.json, NuGet.config, .config/dotnet-tools.json
Directory.Build.*, Directory.Packages.props, Aspire.slnx
eng/*.props, eng/*.targets, eng/common/**, eng/OuterPreBuild.proj
tests/Shared/**/*.props, tests/Shared/**/*.targets, tests/Shared/Dockerfile*
.github/workflows/tests.yml, run-tests.yml, build-packages.yml, ...
```

`Directory.Packages.props` is intentionally here. Layer 1 uses a HEAD-only graph
and does not attempt two-commit central-package diffing, so central package
changes run `ALL`.

Note `eng/OuterPreBuild.proj` (build-wide project-name validation) is here, but
`eng/Bundle.proj` is **not** — it assembles only the CLI bundle, so it maps to
`CLI_BUNDLE`, not `ALL`.

### Ignore (`ignore`)

Files Layer 2 deliberately accounts for with **no** target, so they do not trip
the run-all fallback. Each is either covered precisely by Layer 1 or is inert:

```text
src/Components/Common/**                          # link-compiled into many components; Layer 1 covers
src/Vendoring/OpenTelemetry.Instrumentation.*/**  # glob-compiled into Redis/Kafka components; Layer 1 covers
src/Vendoring/OpenTelemetry.Shared/**             # compiled by nothing; inert
```

### Path rules (`path_rules`)

The one general path-glob → targets matcher. `targets` may be `test:` / `job:` /
a group name / `ALL`. Comment headers in the YAML group the entries by intent;
the selector treats them all identically.

Highlights:

- **convention misses** — `src/Aspire.Hosting.Azure.*/**` →
  `test:Aspire.Hosting.Azure.Tests`, and
  `src/Aspire.Hosting.Integration.Analyzers/**` →
  `test:Aspire.Hosting.Analyzers.Tests`.
- **non-.NET job loose triggers** — only the paths the project graph cannot
  attribute, such as `tests/PolyglotAppHosts/**`, checked-in `*.ats.txt` /
  `*.tscompat.suppression.txt` baselines, `tools/TypeScriptApiCompat/**`, and
  `extension/**`.
- **linked source with no owning project directory** — `src/Aspire/Cli/**`
  carries explicit targets because it is linked into another project but is not
  itself a project directory.
- **loose-file deps** — `eng/clipack/**`, `eng/winget/**`, `eng/homebrew/**`,
  `src/Aspire.ProjectTemplates/**`, `playground/**`, `.github/workflows/**`,
  and `eng/Bundle.proj`.

### Project rules (`affected_project_rules`)

An affected **production** project → a target set, matched by project-**name**
glob against Layer 1's affected set. Matrix test projects are handled by the
Layer 1 intersection.

This is keyed by project identity rather than literal production-project path
globs, so it follows the graph's transitive closure and survives project
directory moves. It is additive and inert when Layer 1 is explicitly skipped
with `--skip-layer1`; the loose-file `path_rules` still cover those triggers.

Project name means the `.csproj` base name, which is what Layer 1 emits.

```yaml
- projects: [Aspire.Cli, Aspire.TypeSystem, Aspire.Managed]
  targets: [job:cli-starter]
- projects: [Aspire.Hosting*, Aspire.Cli]
  targets: [job:typescript-api-compat]
```

### Derived targets (`derived_targets`)

"If **any** of these test projects is selected by either layer, also run these
targets." Applied to the union of Layer 1 and Layer 2 selected tests, to a
fixpoint.

A `test → test` edge whose target has its own rule is followed; cycles terminate.
This is how a job fires based on *which tests run*, not on which file changed:

```yaml
- tests: [test:Aspire.Cli.Tests, test:Aspire.Cli.EndToEnd.Tests]
  targets: [job:cli-starter]
- tests: [test:Aspire.Acquisition.Tests]
  targets: [job:cli-starter, job:winget-installer, job:homebrew-installer]
```

## Maintenance

The map is hand-curated; there is no generator. The verifier tests
(`Infrastructure.Tests/TestTriggerMap/TestTriggerMapTests.cs`) keep it honest
and tell you exactly what to fix when the repo changes.

Steady state:

1. Make the change. The graph closure (Layer 1) tracks itself — you do **not**
   edit the map for a new `ProjectReference`, a new `src` project in
   `Aspire.slnx`, or a new linked-file edge.
2. Run the verifier. Each failure names the offending path/project/target:
   - new `src` project neither in `Aspire.slnx` nor matched by a rule → add a
     `path_rules` entry, or add it to the solution;
   - a convention-miss dir whose non-MSBuild changes should run a specific test
     → add a `path_rules` entry;
   - renamed/removed test project, job, or path → fix the name/glob.
3. Check the selector's audit summary for **unattributed changed files**. A new
   non-.NET job or runtime file read shows up there, prompting a `path_rules`
   addition.

The hand-owned knowledge (`conventions`, `ignore`, `path_rules`,
`derived_targets`) encodes dependencies a fresh codebase read cannot recover, so
carry it forward. Never silently regenerate it.

## Caveats

- **Convention is a backstop, not the primary signal.** Normal compiled changes
  are attributed by Layer 1. The convention exists for non-MSBuild files the
  graph cannot see, and to keep the common case selective instead of falling to
  the run-all fallback.
- **Run-all fallback.** A changed file (anywhere, not just `src/**`) that no
  Layer 2 rule matched, that is not ignored, and that is not Layer-1-owned (under
  no project in `Aspire.slnx`) forces the full matrix. A missed test is a silent
  regression; an extra run is just slower. Files that need no CI are dropped by
  the `prefilter` before this point.
- **Layer 1 failure is fatal.** Audit mode returns run-all only after a
  successful selection. A graph-computation failure fails the selector in every
  mode.
- **Safety vs. selectivity.** The catch-all `ALL` rule, the run-all fallback, and
  the kill switch err toward `ALL`; otherwise the selector relies on Layer 1 for
  `src` coverage and the convention backstop for non-MSBuild files.
- **Schedule/outerloop-only targets.** `api-diffs`, `ats-diffs`,
  `deployment-e2e`, and `Aspire.EndToEnd.Tests` are not in the regular PR matrix
  today; their rules give the *would-be* trigger paths.
- **Integration dirs with no test.** `src/Aspire.Hosting.Orleans`,
  `Aspire.Hosting.AppHost`, and `Aspire.Hosting.Tasks` have no dedicated test
  project. Their MSBuild files are owned by Layer 1, and their non-MSBuild files
  fall through to curated rules or the fallback.
