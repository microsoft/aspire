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

| Section | Why the graph can't see it |
|---------|----------------------------|
| `run_all_globs` | "build infrastructure → run everything" is not a graph concept |
| `curated_jobs` | non-.NET jobs (polyglot, extension, TypeScript, …) — no MSBuild edge |
| `loose_file_deps` | tests that read files by path at runtime; hive installs; packaging |
| `shared_source` | shared dirs with no owning csproj (`src/Components/Common`, `src/Vendoring`) |
| `shared_compiled_source` | specific files link-compiled elsewhere; verified statically because the oracle (`--assume-changes`) is project-granular and cannot confirm file-level edges |
| `test_self` | a test project's own folder change → that test project |
| `groups` | named, reusable bundles of `test:` and/or `job:` targets |

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
| `job:api-diffs` | [`generate-api-diffs.yml`](../../.github/workflows/generate-api-diffs.yml) — *schedule-only today* |
| `job:ats-diffs` | [`generate-ats-diffs.yml`](../../.github/workflows/generate-ats-diffs.yml) — *schedule-only today* |
| `job:deployment-e2e` | [`deployment-tests.yml`](../../.github/workflows/deployment-tests.yml) — *schedule/dispatch-only today* |
| `ALL` | full test matrix + all jobs |
| `<GROUP_NAME>` | a named group (see `groups:`) expanding to its `test:`/`job:` members |

Base builds (packages, CLI native archives, installer artifacts, the CLI E2E
image) are not modelled as targets: they are upstream `needs:` of the targets
above and run whenever any dependent target runs. Their **workflow files** are in
`run_all_globs` because a change to *how* they build can affect every consumer.

## Rule categories

Rules are **additive**: a changed file activates the union of targets from every
rule whose glob it matches, plus the Layer 1 projects. A match in `run_all_globs`
short-circuits to `ALL`. Group names in a rule's targets expand to their members.

### Named groups (`groups`)

Reusable bundles of `test:` and/or `job:` targets, so a glob can map to a named
set instead of repeating it. Example:

```yaml
groups:
  ALL_COMPONENT_TESTS: [test:Aspire.Npgsql.Tests, ...]   # ~50 client-component tests
  CLI_BUNDLE: [test:Aspire.Cli.EndToEnd.Tests, job:cli-starter, job:extension-e2e]
```

### Test self-changes (`test_self`)

`tests/<X>/**` → `test:<X>` for every test project.

### Catch-all → `ALL` (`run_all_globs`)

Build infrastructure and broadly shared code re-run everything. Examples:

```
global.json, NuGet.config, .config/dotnet-tools.json
Directory.Build.*, Directory.Packages.props, Aspire.slnx
eng/*.props, eng/*.targets, eng/common/**, eng/OuterPreBuild.proj
src/Shared/**, tests/Shared/**
.github/workflows/tests.yml, run-tests.yml, build-packages.yml, ...
```

Note `eng/OuterPreBuild.proj` (build-wide project-name validation) is here, but
`eng/Bundle.proj` is **not** — it assembles only the CLI bundle, so it maps to
`CLI_BUNDLE`, not `ALL`. `.proj` traversal files are invisible to `dotnet-affected`
(not in the slnx; not a supported project type), so they must be curated.

### Shared source with no owning csproj (`shared_source`)

| Path | Targets |
|------|---------|
| `src/Components/Common/**` | `ALL_COMPONENT_TESTS` (compiled into many client components) |
| `src/Vendoring/OpenTelemetry.Shared/**` (+ the StackExchange.Redis vendor dir) | the StackExchange.Redis client tests |
| `src/Vendoring/OpenTelemetry.Shared/**` (+ the ConfluentKafka vendor dir) | `test:Aspire.Confluent.Kafka.Tests` |

### Curated job rules (`curated_jobs`)

Non-.NET jobs and a few outerloop/e2e `test:` targets gated by path. See the YAML
for full globs. Highlights:

- `job:polyglot` ← `src/Aspire.TypeSystem/**`, the `CodeGeneration.*` generators,
  `src/Aspire.Cli/**`, `src/Aspire.Managed/**`, `tests/PolyglotAppHosts/**`, …
- `job:extension-unit` / `job:extension-e2e` ← `extension/**` (e2e also CLI / hosting / dashboard)
- `job:typescript-api-compat`, `job:ats-diffs` ← `src/Aspire.Hosting*/**` (so a hosting
  integration change *does* trigger these — `Aspire.Hosting*` matches `Aspire.Hosting.Kafka`
  etc., which is intended)

### Runtime / loose-file dependencies (`loose_file_deps`)

Dependencies invisible to the csproj graph. Highlights:

| Path | Targets | Why |
|------|---------|-----|
| `eng/clipack/**` | `Aspire.Cli.Tests`, `Aspire.Cli.EndToEnd.Tests`, `Infrastructure.Tests` | `AspireJsLauncherTests` reads `eng/clipack/npm/aspire.js`; CLI archive consumed by e2e |
| `eng/Bundle.proj` | `CLI_BUNDLE` | assembles the CLI bundle; consumed by CLI e2e/starter/extension, not integrations |
| `src/Aspire.ProjectTemplates/**` | `Aspire.Templates.Tests` | installs the template hive (also covers the out-of-slnx template placeholder projects) |
| `playground/**` | `Aspire.Playground.Tests` | builds/runs apps under `playground/` |
| `.github/workflows/**`, `eng/pipelines/**` | `Infrastructure.Tests` | asserts on workflow/pipeline files |

### Shared compiled source (`shared_compiled_source`)

Specific `src` files link-compiled into *other* projects. A change to the **file**
(not the whole owning project) runs the consuming test(s). `dotnet-affected` can
see these in a real `--from`/`--to` diff, but the oracle cross-check uses
`--assume-changes` (project-granular) and cannot, so they are kept here and
verified statically. Example:

```
src/Aspire.Hosting.PostgreSQL/PostgresContainerImageTags.cs
  -> test:Aspire.Npgsql.Tests, test:Aspire.Azure.Npgsql.Tests, test:Aspire.Hosting.Tests, ...
```

## Maintenance

The map is hand-curated; there is no generator. The verifier tests
(`Infrastructure.Tests/TestTriggerMap/TestTriggerMapTests.cs`) keep it honest and
tell you exactly what to fix when the repo changes.

Steady state:

1. Make the change. The graph closure (Layer 1) tracks itself — you do **not**
   edit the map for a new `ProjectReference` or a new `src` project that is in the
   solution.
2. Run the verifier. Each failure names the offending path/project/target:
   - new `src` project neither in `Aspire.slnx` nor curated → add a rule (or add
     it to the solution);
   - new foreign `<Compile Include>` edge → add a `shared_compiled_source` entry;
   - renamed/removed test project, job, or path → fix the name/glob.
3. The selector's audit summary lists **unattributed changed files** — files no
   rule matched and no project owns. A new non-.NET job or runtime file read shows
   up there (nothing graph-based can flag it), prompting a `curated_jobs` /
   `loose_file_deps` addition.

When the curated sections need a periodic refresh, only the derived-style content
is regenerable; the genuinely hand-owned knowledge (`curated_jobs`,
`loose_file_deps`, and the build-infra judgment in `run_all_globs`) must be
carried forward, never silently regenerated — it encodes dependencies a fresh
codebase read cannot recover.

## Caveats

- **Compile-include is file-granular.** Link-compiling a single foreign `.cs`
  file couples the consumer to that file only — `shared_compiled_source` is keyed
  on the file, not the owning project, so a different file in the same project
  does not drag the borrowed-file consumers.
- **Safety vs. selectivity.** `run_all_globs` and the kill switch err toward
  `ALL`; otherwise the selector relies on Layer 1 for `src` coverage. A missed
  test is a silent regression; an extra test is just slower.
- **Schedule/outerloop-only targets.** `api-diffs`, `ats-diffs`, `deployment-e2e`,
  and `Aspire.EndToEnd.Tests` are not in the regular PR matrix today; their rules
  give the *would-be* trigger paths.

## Known gaps

- `src/Aspire.Hosting.Orleans/**` has no `Aspire.Hosting.Orleans.Tests` project;
  it is covered only transitively (via the graph) through `src/Aspire.Hosting`.
