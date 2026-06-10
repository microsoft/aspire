# Test trigger map

A map of **repo path → CI targets that must run** when a matching file changes,
covering the .NET test projects and the validation/polyglot jobs in
[`tests.yml`](../../.github/workflows/tests.yml).

The machine-readable form lives next to this doc:
[`test-trigger-map.yml`](./test-trigger-map.yml).

## Status and intent

This is a **descriptive** map. Today CI does **not** select tests per-file: the
[`enumerate-tests`](../../.github/actions/enumerate-tests) action builds a matrix
of **all** test projects, and the only path-based gate is the coarse
skip-everything check in
[`eng/testing/github-ci-trigger-patterns.txt`](../../eng/testing/github-ci-trigger-patterns.txt)
(see [ci-trigger-patterns.md](./ci-trigger-patterns.md)).

The map exists so a future selective-CI implementation can be **audited and
validated** against a derived ground truth. Nothing consumes
`test-trigger-map.yml` yet.

## How it was derived

Two layers:

1. **Graph-derived base (deterministic).** Parse every `.csproj` and build a
   dependency graph:
   - **`ProjectReference` edges** carry the transitive closure (referencing a
     project pulls its dependencies). For each test project, traverse this
     closure over **`src` projects only** to get `src project → test projects`.
     This populates `leaf_source` and `core_source`.
   - **`<Compile Include>` of a *foreign* `src` file** (a project link-compiling
     a single `.cs` file owned by another `src` project) is tracked separately at
     **file granularity** in `shared_compiled_source`. A link-compiled file does
     **not** drag in its owner project's references, so it is *not* folded into
     the `ProjectReference` closure — otherwise a test that borrows one constants
     file (e.g. `PostgresContainerImageTags.cs`) would falsely depend on all of
     `Aspire.Hosting`.

   Traversing `src`-only `ProjectReference` edges avoids inflating leaf
   integrations through shared **test** hubs (see caveat below).

2. **Curated special cases.** Dependencies the csproj graph cannot see —
   runtime/loose-file reads, packaging, and the non-.NET jobs
   (polyglot / TypeScript / extension / CLI / API / deployment).

The MSBuild path resolver expands `$(RepoRoot)`, `$(SharedDir)` → `src/Shared/`,
`$(TestsSharedDir)` → `tests/Shared/`, `$(ComponentsDir)` → `src/Components/`, and
`$(VendoringDir)` → `src/Vendoring/`.

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
| `ALL_HOSTING_TESTS` | alias for every hosting-side test project (expanded under `aliases:` in the YAML) |
| `ALL_COMPONENT_TESTS` | alias for every client-component test project (expanded under `aliases:` in the YAML) |

Base builds (packages, CLI native archives, installer artifacts, the CLI E2E
image) are not modelled as targets: they are upstream `needs:` of the targets
above and run whenever any dependent target runs. Their **workflow files** are in
`run_all_globs` because a change to *how* they build can affect every consumer.

## Rule categories

Rules are **additive**: a changed file activates the union of targets from every
rule whose glob it matches. A match in `run_all_globs` short-circuits to `ALL`.

### 0. Test self-changes (`test_self`)

`tests/<X>/**` → `test:<X>` for every test project. A change under a test
project's own folder always runs that test project.

### 1. Catch-all → `ALL` (`run_all_globs`)

Build infrastructure and shared code re-run everything. Examples (full list in
the YAML):

```
global.json, NuGet.config, .config/dotnet-tools.json
Directory.Build.*, Directory.Packages.props, Aspire.slnx
src/Directory.Build.*, tests/Directory.Build.*
eng/Versions.props, eng/Version.Details.xml, eng/Testing.props, eng/Testing.targets, eng/Tools.props, eng/common/**
src/Shared/**, tests/Shared/**
.github/actions/enumerate-tests/**, eng/scripts/split-test-matrix-by-deps.ps1
.github/workflows/tests.yml, run-tests.yml, build-packages.yml, build-cli-native-archives.yml, build-cli-e2e-image.yml
```

### 2. Shared test hubs (`test_hubs`)

Test-side helper libraries; changing one rebuilds every test that references it.

| Path | Effect |
|------|--------|
| `tests/Aspire.Components.Common.TestUtilities/**` | `ALL_COMPONENT_TESTS` (~36 projects) |
| `tests/Aspire.Hosting.Tests/**` | `ALL_HOSTING_TESTS` (~34 projects) |
| `tests/Aspire.TestUtilities/**` | `ALL` (used on both sides) |

### 3. Shared source with no owning csproj (`shared_source`)

| Path | Targets |
|------|---------|
| `src/Components/Common/**` | `ALL_COMPONENT_TESTS` (compiled into many client components) |
| `src/Vendoring/OpenTelemetry.Instrumentation.StackExchangeRedis/**`, `src/Vendoring/OpenTelemetry.Shared/**` | the StackExchange.Redis client tests |
| `src/Vendoring/OpenTelemetry.Instrumentation.ConfluentKafka/**`, `src/Vendoring/OpenTelemetry.Shared/**` | `test:Aspire.Confluent.Kafka.Tests` |

### 4. Core / build-SDK source (`core_source`)

| Path | Fan-out | Notes |
|------|---------|-------|
| `src/Aspire.Hosting/**` | 43 hosting test projects | the core hosting library |
| `src/Aspire.Hosting.AppHost/**` | 27 | apphost build SDK (targets) |
| `src/Aspire.Hosting.Tasks/**` | 27 | MSBuild tasks used by the apphost build |
| `src/Aspire.Hosting.Analyzers/**` | 27 | analyzers shipped into apphost builds |

`src/Aspire.Hosting` reaches the **hosting** side but not the ~53 pure
**client-component** test projects, which don't reference it — the natural
hosting-vs-components split.

### 5. Curated job rules (`curated_jobs`)

| Job | Triggered by (abridged — see YAML for full globs) |
|-----|---------------------------------------------------|
| `job:polyglot` | `src/Aspire.TypeSystem/**`, `src/Aspire.Hosting.RemoteHost/**`, `src/Aspire.Managed/**`, `src/Aspire.Cli/**`, `src/Aspire/Cli/**`, `src/Aspire.AppHost.Sdk/**`, `src/Aspire.Hosting.CodeGeneration.{Go,Java,Python,Rust,TypeScript}/**`, `src/Aspire.Hosting.{Go,JavaScript,Python}/**`, `tests/PolyglotAppHosts/**`, `.github/workflows/polyglot-validation/**` |
| `job:typescript-sdk` | `src/Aspire.Hosting.CodeGeneration.TypeScript/**`, `tests/Aspire.Hosting.CodeGeneration.TypeScript.JsTests/**`, `src/Aspire.Cli/Templating/Templates/ts-starter/**` |
| `job:typescript-api-compat` | `src/Aspire.Hosting*/**`, `src/Aspire.Hosting*/api/*.ats.txt`, `src/**/*.tscompat.suppression.txt`, `src/Aspire.Cli/**`, `tools/TypeScriptApiCompat/**` |
| `job:extension-unit` | `extension/**` |
| `job:extension-e2e` | `extension/**`, `src/Aspire.Cli/**`, `src/Aspire/Cli/**`, `src/Aspire.Hosting*/**`, `src/Aspire.Dashboard*/**`, `tests/Aspire.Cli.Tests/**`, `tests/Aspire.Cli.EndToEnd.Tests/**`, `tests/Aspire.Hosting.Tests/Cli/**`, `eng/scripts/get-aspire-cli*` (verbatim from `tests.yml` `extension_e2e_changes`, including the `src/Aspire/Cli/` second CLI path and the `tests.yml` self-reference) |
| `job:cli-starter` | `eng/scripts/get-aspire-cli-pr.ps1`, `eng/scripts/cli-starter-validation.ps1`, `src/Aspire.Cli/**`, `src/Aspire/Cli/**`, `src/Aspire.TypeSystem/**`, `src/Aspire.Managed/**` |
| `job:api-diffs` *(schedule-only)* | `src/**/*.csproj`, `src/**/api/*.cs`, the workflow file |
| `job:ats-diffs` *(schedule-only)* | `src/Aspire.Hosting*/**`, `src/Aspire.Hosting*/api/*.ats.txt`, `src/Aspire.Cli/**`, `src/**/*.tscompat.suppression.txt`, the workflow file |
| `job:deployment-e2e` *(schedule/dispatch-only)* | `tests/Aspire.Deployment.EndToEnd.Tests/**`, `src/Aspire.Hosting.Azure*/**`, `src/Aspire.Cli/**`, `src/Aspire/Cli/**`, `src/Aspire.TypeSystem/**`, `src/Aspire.Managed/**`, `src/Aspire.ProjectTemplates/**`, the workflow file |
| `test:Aspire.EndToEnd.Tests` *(outerloop-only today)* | `tests/Aspire.EndToEnd.Tests/**`, `tests/testproject/**`, core hosting + PostgreSQL/Redis |
| `test:Aspire.Cli.EndToEnd.Tests` | `src/Aspire.Cli/**`, `src/Aspire/Cli/**`, `src/Aspire.TypeSystem/**`, `src/Aspire.Managed/**`, `src/Aspire.AppHost.Sdk/**`, `eng/clipack/**` (consumes the CLI native archive) |

`Aspire.EndToEnd.Tests` is **outerloop-only** today (`SkipTests` on GH Actions
unless `RunOuterloopTests`), so it is not part of the regular `tests.yml` matrix —
it is included here for completeness, like the schedule-only jobs.

Note on `Aspire.TypeSystem`: it is **not** a run-everything core library
(`src/Aspire.Hosting` does not reference it). It is referenced only by
`Aspire.Cli`, the five `CodeGeneration.*` generators, `Aspire.Hosting.RemoteHost`,
and the `Aspire.Hosting.Tests` hub — so a TypeSystem change drives the
**polyglot/codegen + CLI (incl. CLI starter/e2e/deployment)** surface, not all
hosting tests.

### 6. Runtime / loose-file dependencies (`loose_file_deps`)

Dependencies invisible to the csproj graph:

| Path | Targets | Why |
|------|---------|-----|
| `eng/scripts/get-aspire-cli*.{sh,ps1}` | `test:Aspire.Acquisition.Tests`, `test:Aspire.Cli.EndToEnd.Tests`, `job:cli-starter`, `job:extension-e2e` | Acquisition.Tests asserts script behavior; `CliE2EAutomatorHelpers.cs` invokes `get-aspire-cli*.sh` at runtime |
| `eng/clipack/**` | `test:Aspire.Cli.Tests`, `test:Aspire.Cli.EndToEnd.Tests`, `test:Infrastructure.Tests` | `Cli.Tests/Npm/AspireJsLauncherTests` reads `eng/clipack/npm/aspire.js`; CLI archive consumed by e2e |
| `eng/scripts/pack-cli-npm-package.ps1` | `test:Aspire.Cli.Tests`, `test:Infrastructure.Tests` | parsed by both test projects |
| `eng/dashboardpack/**` | `test:Aspire.Hosting.Sdk.Tests` | asserts `Aspire.Dashboard.Sdk.*` ref |
| `eng/dcppack/**` | `test:Aspire.Hosting.Sdk.Tests`, `test:Aspire.TerminalHost.Tests` | asserts `Aspire.Hosting.Orchestration.*`; TerminalHost exercises DCP |
| `src/Aspire.ProjectTemplates/**` | `test:Aspire.Templates.Tests` | Templates.Tests installs the template hive |
| `playground/**` | `test:Aspire.Playground.Tests` | builds/runs apps under `playground/` |
| `src/Aspire.AppHost.Sdk/**` | `test:Aspire.Hosting.Sdk.Tests`, `test:Aspire.Hosting.DotnetTool.Tests`, `job:polyglot` | AppHost SDK ships to every generated apphost |
| `tools/QuarantineTools/**` | `test:QuarantineTools.Tests` | tests the quarantine/disable source rewriting |
| `.github/workflows/**`, `eng/pipelines/**` | `test:Infrastructure.Tests` | asserts on workflow/pipeline files |

### 7. Leaf source rules (`leaf_source`)

The 1:1 (and small) mappings — integration projects (`src/Aspire.Hosting.*`) and
client components (`src/Components/*`) → their owning test project(s) via the
`ProjectReference` closure. These are where selective execution pays off.

```
src/Aspire.Hosting.Kafka/**        -> test:Aspire.Hosting.Kafka.Tests
src/Components/Aspire.Npgsql/**    -> test:Aspire.Azure.Npgsql.Tests, test:Aspire.Hosting.PostgreSQL.Tests, test:Aspire.Npgsql.Tests
```

### 8. Shared compiled source (`shared_compiled_source`)

Specific `src` files that are link-compiled into *other* projects. A change to
the **file** (not the whole owning project) runs the consuming test(s). Examples:

```
src/Aspire.Hosting.Yarp/YarpContainerImageTags.cs
  -> test:Aspire.Hosting.JavaScript.Tests, test:Aspire.Hosting.Azure.Tests,
     test:Aspire.Hosting.CodeGeneration.TypeScript.Tests, test:Aspire.Hosting.Tests
src/Components/Aspire.MongoDB.Driver/MongoDBSettings.cs  -> test:Aspire.MongoDB.Driver.v2.Tests
src/Components/Aspire.Npgsql/NpgsqlCommon.cs            -> test:Aspire.Npgsql.EntityFrameworkCore.PostgreSQL.Tests, ...
```

## Caveats for an implementation

- **Test-hub inflation.** `tests/Aspire.Hosting.Tests` directly references many
  integrations (Yarp, DevTunnels, JavaScript, Python, …). A naive *full*
  transitive closure (following `test → test` edges) makes changing any of those
  leaves appear to require ~36 test projects. This map traverses `src → src`
  edges only, so `src/Aspire.Hosting.Yarp/**` → just
  `{Yarp.Tests, Aspire.Hosting.Tests, RemoteHost.Tests}`, with the JavaScript-side
  coupling captured precisely in `shared_compiled_source`.

- **Compile-include is file-granular.** Link-compiling a single foreign `.cs`
  file couples the consumer to that file only, not to the owner project's whole
  reference closure. Folding it into the closure wrongly pulls client-component
  tests into `src/Aspire.Hosting`'s trigger set.

- **Direction matters.** A leaf integration's *direct* referencer count
  understates impact (everything reaches `Aspire.Hosting` only transitively),
  while the *full* transitive closure overstates it. The `src`-only
  `ProjectReference` closure is the middle ground.

- **Safety vs. selectivity.** When in doubt, widen to `ALL`. A missed test is a
  silent regression; an extra test is just slower.

- **Schedule/outerloop-only targets.** `api-diffs`, `ats-diffs`,
  `deployment-e2e`, and `Aspire.EndToEnd.Tests` are not in the regular PR matrix
  today. Their rows give the *would-be* trigger paths.

## Known gaps

- `src/Aspire.Hosting.Orleans/**` has no `Aspire.Hosting.Orleans.Tests` project;
  it is covered only transitively via `src/Aspire.Hosting`.
