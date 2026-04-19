# Azure DevOps Public Pipeline (`azure-pipelines-public`)

This document describes the Azure DevOps (AzDO) public pipeline for the Aspire repository. Understanding this pipeline is critical because **tests on AzDO do not run on PRs by default** and the scheduled pipeline runs **only once a week** (Monday midnight UTC). This makes it easy to unknowingly break AzDO tests with changes that pass GitHub Actions CI.

## Overview

| Property                | Value                                                                                                              |
|-------------------------|--------------------------------------------------------------------------------------------------------------------|
| Pipeline file           | `eng/pipelines/azure-pipelines-public.yml`                                                                         |
| Schedule                | Weekly, Monday 00:00 UTC (`main`, `release/*`)                                                                     |
| PR trigger              | `main`, `release/*`, `feature/*` (excludes `.md`, `eng/Version.Details.xml`, `.github/*`, `docs/*`, license files) |
| Manual trigger pipeline | `eng/pipelines/azdo-tests.yml` (invoked with `/azp run aspire-tests`)                                              |

### What runs when

| Trigger                   | Test variants                  | What runs                                    |
|---------------------------|--------------------------------|----------------------------------------------|
| **PR**                    | `''` (empty)                   | Build + pack only, **no tests**              |
| **Scheduled** (weekly)    | `_pipeline_tests,_helix_tests` | Build + pack + non-helix tests + Helix tests |
| **Manual** (`aspire-tests`) | `_pipeline_tests,_helix_tests` | Build + pack + non-helix tests + Helix tests |

> **Key insight**: PR builds only verify compilation and packaging. Tests are skipped on PRs. This is why changes that break AzDO-specific test infrastructure can go unnoticed.

## Pipeline Architecture

```text
azure-pipelines-public.yml
  └── extends: templates/public-pipeline-template.yml
        └── stage: build
              ├── Windows_pipeline_tests  (non-helix tests on Windows)
              ├── Windows_helix_tests     (helix tests on Windows)
              ├── Linux_pipeline_tests    (non-helix tests on Linux)
              └── Linux_helix_tests       (helix tests on Linux)
                    └── each job uses: templates/BuildAndTest.yml
```

### Template: `public-pipeline-template.yml`

This is the main orchestrator. It:

1. Sets the `testVariants` variable based on build reason (PR vs scheduled) or the `testVariants` parameter (for manual runs).
2. Iterates over `testVariants` (comma-separated) using `${{ each testVariant in split(...) }}` to create jobs for each variant on both Windows and Linux.
3. Each job calls `BuildAndTest.yml` with `runHelixTests` and `runPipelineTests` flags derived from the variant name.

### Template: `BuildAndTest.yml`

This is the build-and-test workhorse. For public builds (`runAsPublic: true`), it does the following:

#### Build Step (always runs for public)

```text
build.sh/cmd -restore -build -configuration Release -pack
    /p:PrepareForHelix={true|false}
```

When `PrepareForHelix=true`, the build also:
- Archives (zips) each test project's output into `artifacts/helix/` directories
- Generates support files for out-of-repo test execution

#### Non-Helix Tests (`runPipelineTests: true`)

These tests run **directly on the AzDO agent** (not Helix), with code coverage via `dotnet-coverage`:

```bash
dotnet-coverage collect "build.sh -testnobuild -test -configuration Release /maxcpucount:1 /p:BuildInParallel=false"
```

Which tests run here is controlled by the `RunOnAzdoCI` property (see [Test Routing](#test-routing) below).

#### Helix Tests (`runHelixTests: true`)

1. Installs SDKs for testing (`tests/workloads.proj`)
2. Sends test work items to Helix via `send-to-helix.yml` → `send-to-helix-ci.proj`
3. Downloads `.trx` result files from Helix after completion

## Helix Test Infrastructure

### How Tests Are Sent to Helix

The entry point is `tests/helix/send-to-helix-ci.proj`, which defines **three test categories**:

| Category            | Targets file                              | Runs on Windows | Runs on Linux | Description                                                       |
|---------------------|-------------------------------------------|-----------------|---------------|-------------------------------------------------------------------|
| `basictests`        | `send-to-helix-basictests.targets`        | ✅              | ✅            | Standard unit/integration tests                                   |
| `endtoendtests`     | `send-to-helix-endtoendtests.targets`     | ❌              | ✅            | End-to-end scenario tests (needs Docker)                          |
| `buildonhelixtests` | `send-to-helix-buildonhelixtests.targets` | ❌              | ✅            | Tests that `dotnet build` + `dotnet test` on Helix (needs Docker) |

The `send-to-helix-ci.proj` first runs `PrepareDependencies` sequentially, then dispatches all categories in parallel via MSBuild.

Each category is handled by `send-to-helix-inner.proj` (the Helix SDK project), which imports the category-specific `.targets` file.

### How Tests Are Split Into Work Items

Each test category has its own strategy for splitting tests into Helix work items:

#### `basictests`

- **One work item per test project zip** — each test project is archived as a separate `.zip` in `artifacts/helix/tests/`
- The `ZipTestArchive` target (in `tests/Directory.Build.targets`) creates a zip per test project after build
- Glob pattern: `$(TestArchiveTestsDir)**/*.zip`
- Each work item runs `dotnet exec <TestProject>.dll` with MTP filters
- Timeout: 20 minutes per work item

#### `endtoendtests`

- **One work item per test scenario** — the `Aspire.EndToEnd.Tests.zip` archive is reused for each scenario
- Scenarios are hardcoded: `basicservices`, `cosmos`
- Each work item filters tests by `--filter-trait "scenario=<scenario>"`
- Tests run only on Linux (Docker required)

#### `buildonhelixtests`

- **One work item per test project zip** — similar to `basictests`
- Key difference: runs `dotnet build` followed by `dotnet test` on Helix (tests build from source on the agent)
- Includes Playwright dependencies and Azure Functions CLI as correlation payloads
- `Aspire.Playground.Tests` gets an extended 25-minute timeout (vs 15 minutes for others)

### Helix Queues

| Platform | Public project | Internal project |
|---|---|---|
| Windows | `Windows.11.Amd64.Client.Open` | `Windows.11.Amd64.Client` |
| Linux | `Ubuntu.2204.Amd64.Open` | `Ubuntu.2204.Amd64` |

### Helix Work Item Lifecycle

Each work item follows this lifecycle:

1. **Pre-commands**: Clean up stale processes (dotnet-tests, dcp.exe), start Docker cleanup, set environment variables (DCP paths, SDK paths, dev certs, Docker BuildKit)
2. **Command**: Run the test executable with MTP arguments, blame/crash dump collection, quarantine exclusion
3. **Post-commands**: List Docker state, rename `.trx` files for collection

### Correlation Payloads (Shared Dependencies)

These are shared across all work items in a Helix job:

- **DCP binary** — the orchestrator binary, set via `DcpPublisher__CliPath`
- **Dev cert scripts** — for HTTPS dev certificate setup on Linux
- **Docker CLI** — specific version installed on the agent
- **SDKs for testing** — `dotnet-tests` directory with a configured .NET SDK
- **Built NuGet packages** — `artifacts/packages/Shipping/` for template tests
- **Playwright browser dependencies** — for UI tests
- **Azure Functions CLI** — for Functions integration tests

## Test Routing

The system uses MSBuild properties to control where each test project runs. These are defined in `eng/Testing.props` (defaults) and overridden per-project in `.csproj` files.

### Properties

| Property | Default | Purpose |
|---|---|---|
| `RunOnAzdoHelixWindows` | `true` | Run on Helix Windows queue |
| `RunOnAzdoHelixLinux` | `true` | Run on Helix Linux queue |
| `RunOnAzdoCIWindows` | `true` | Run on AzDO agent (Windows) |
| `RunOnAzdoCILinux` | `true` | Run on AzDO agent (Linux) |
| `RunOnGithubActionsWindows` | `true` | Run on GitHub Actions (Windows) |
| `RunOnGithubActionsLinux` | `true` | Run on GitHub Actions (Linux) |
| `RunOnGithubActionsMacOS` | `true` | Run on GitHub Actions (macOS) |

### Routing Logic (from `eng/Testing.targets`)

- If `RunOnAzdoHelix` is `true`, then `RunOnAzdoCI` is forced to `false` (tests don't run in both places)
- Tests are skipped based on the detected runner context (`IsGitHubActionsRunner`, `IsAzdoCIRunner`, `IsAzdoHelixRunner`)
- Locally, tests are never skipped

## Test Archive Process

When `PrepareForHelix=true` is passed during build, the following happens for each test project:

This is also the easiest way to inspect locally what payload Helix agents will receive for test projects. After running a build with `PrepareForHelix=true`, review the generated `.zip` files under `artifacts/helix/*` to confirm that expected binaries, resources, snapshots, and support files were included before queueing Helix work items.

1. **`ZipTestArchive` target** (in `tests/Directory.Build.targets`):
   - Runs after `Build` for projects where `IsTestProject=true`, `RunOnAzdoHelix=true`, and `IsTestUtilityProject!=true`
   - Zips the `$(OutDir)` contents to `artifacts/helix/<category>/<ProjectName>.zip`
   - Multi-TFM projects append the TFM suffix: `<ProjectName>-<tfm>.zip`

2. **Archive directories** (defined in `tests/Directory.Build.props`):
   - `artifacts/helix/tests/` — basic tests
   - `artifacts/helix/e2e-tests/` — end-to-end tests
   - `artifacts/helix/build-on-helix-tests/` — build-on-helix tests
   - `artifacts/helix/cli-e2e-tests/` — CLI E2E tests
   - `artifacts/helix/deployment-e2e-tests/` — deployment E2E tests

3. **Out-of-repo support files** (via `Aspire.RepoTesting.targets`):
   - `Directory.Build.props` / `Directory.Build.targets` — empty, to isolate the build
   - `Directory.Packages.Versions.props` — generated, with all package versions from the repo
   - `nuget.config` — configured to resolve built packages from artifacts
   - Shared test utilities

## Helix xUnit Configuration

When `PrepareForHelix=true`, a special `xunit.runner.json` is used (`tests/helix/xunit.runner.json`):

```json
{
  "longRunningTestSeconds": 120,
  "parallelizeAssembly": false,
  "parallelizeTestCollections": false
}
```

This disables parallel test execution within assemblies on Helix (parallelism is achieved by running multiple work items concurrently instead).

## Test Retries

The `eng/test-configuration.json` configures test retries:

- **Local reruns**: 0 retries (disabled so the first retry is queued on a different machine)
- **Remote (Helix) reruns**: 3 retries
- **Retry-on rules**: Matches reported Testcontainers Ryuk image failures (`No such image: .*testcontainers/ryuk:.*`)

## Local validation tip: Helix dry runs

When changing Helix infrastructure, you can validate the generated work item commands locally without sending anything to Helix.

First, build the Helix archives:

```bash
./build.sh --build /p:SkipNativeBuild=true /p:PrepareForHelix=true /p:ArchiveTests=true
```

Then run the Helix project in dry-run mode:

```bash
./dotnet.sh msbuild tests/helix/send-to-helix-ci.proj \
  /t:Test \
  /p:HelixDryRun=true \
  /p:Configuration=Release \
  /p:HelixTargetQueues=Ubuntu.2204.Amd64.Open
```

This prints `HelixWorkItem:` lines that include the generated command, pre-commands, and payload archive for each work item. It is a fast way to verify that environment variables, command-line arguments, and payload wiring are being propagated as expected.

Important notes:

- The dry run still requires the test archives under `artifacts/helix/*`, so run the build step above first.
- Outside AzDO, you must pass `HelixTargetQueues` explicitly or the Helix SDK fails before printing any work items.
- `HelixDryRun=true` intentionally stops the build after printing the generated work items.

## Known Breakage Patterns (from Real Incidents)

Since AzDO tests don't run on PRs, changes can silently break the pipeline. The following 8 patterns have caused real breakages over the past year, identified from git history. **Code reviewers should watch for these on PRs.**

### 1. Missing Files in Helix Payload

**Pattern**: Tests pass on GitHub Actions (in-repo) but fail on Helix because a source file, config file, or resource is not included in the archived zip.

**Real incidents**:
- `ab077992`: `dotnet.config` was missing from the archive → Helix runs failed
- `d4fbd474` / `4cac8573` / `fd536bb9`: `Snapshots/` directories for Verify snapshot testing were missing or conflicting (multiple projects' snapshots were copied into a single directory, overwriting each other)
- `1bce7a83`: `X509Certificate2Extensions.cs` was missing from Playground.Tests Helix payload → build failure on Helix
- `02eadb88`: `PathLookupHelper.cs` was deployed to `src/Shared/` but the `Compile` condition `$(RepoRoot)!=''` skipped it on Helix (where RepoRoot is empty)

**What to watch for**: Adding new source files, shared utilities, config files, or snapshot directories that tests depend on. If the test runs on Helix (`RunOnAzdoHelix=true`), verify the file is included in the archive payload. Check for conditional compilation (`Condition=`) that might exclude files when building outside the repo.

### 2. DOTNET_ROOT / SDK Path Mismatches

**Pattern**: Helix agents have a different (often older) system .NET SDK than what tests require. Tests work on GitHub Actions because the correct SDK is on PATH, but fail on Helix/AzDO because the system dotnet is used instead.

**Real incidents**:
- `49b1fd3b`: Template tests ran `dotnet test --list-tests` which invoked the system dotnet (6.0) instead of the repo's dotnet (8.0+) → "You must install or update .NET" error. A prior PR removed `DOTNET_ROOT` environment variable override for GitHub Actions, breaking AzDO.
- `258d2e95`: `dotnet-tests` SDK directory wasn't properly prepared for template and helix test runs

**What to watch for**: Changes to `DOTNET_ROOT`, `PATH`, or SDK version settings in `BuildAndTest.yml`, `tests/Directory.Build.targets`, or helix targets. If a change works by relying on the system dotnet or GitHub Actions' pre-installed SDK, it will likely break AzDO/Helix.

### 3. Docker/Buildx Availability Differences

**Pattern**: Tests that require Docker or Docker Buildx pass on GitHub Actions but fail on AzDO/Helix agents where Docker capabilities differ. The `RequiresFeatureAttribute` and `TestFeature` enum (`tests/Aspire.TestUtilities/TestFeature.cs`) provide the mechanism to declare these dependencies:

- **`[RequiresFeature(TestFeature.Docker)]`** — test needs Docker. Supported on Linux (local + CI) but **not on Windows CI** (AzDO/Helix Windows agents don't have Docker).
- **`[RequiresFeature(TestFeature.DockerPluginBuildx)]`** — test needs `docker buildx`. **Not available on any AzDO/Helix agent** (`IsDockerPluginBuildxSupported()` returns `false` when `PlatformDetection.IsRunningFromAzdo`).

If a test depends on a Docker capability not covered by these (e.g., a new Docker plugin or compose feature), a new `TestFeature` flag must be added to the enum and the detection logic added to `RequiresFeatureAttribute.IsSupported()`.

**Real incidents**:
- `62d71279`: 51 tests had to be disabled with `ActiveIssue` because they required Docker buildx (not installed on AzDO), testcontainers with specific images, or Azure deployment infrastructure
- `6832752429`: `VerifyPnpmDockerfileBuildSucceeds` test skipped on AzDO due to missing buildx
- `8692e43b`: Docker runtime detection (`docker info`) behaved differently on AzDO

**What to watch for**: New tests that use `WithDockerfile`, Docker buildx, testcontainers, or `DOCKER_BUILDKIT`. Every such test **must** have `[RequiresFeature(TestFeature.Docker)]` or `[RequiresFeature(TestFeature.DockerPluginBuildx)]` as appropriate. Without this attribute, the test will attempt to run on environments where Docker is unavailable and fail. If the test depends on a Docker capability not covered by existing `TestFeature` values, add a new flag.

### 4. Incorrect RunOnAzdoHelix* Overrides

**Pattern**: Test routing properties are incorrectly added or removed, causing tests to not run where expected or to be archived when they shouldn't be.

**Real incidents**:
- `ec674aa2`: A test-splitting PR incorrectly added `RunOnAzdoHelixWindows=false` to template tests → archive wasn't produced on Windows → Helix jobs failed
- `a9abd28a`: Projects with `RunOnAzdoHelix=false` were still being archived, wasting build time

**What to watch for**: Changes to `RunOnAzdoHelix*` properties in `.csproj` files. Verify that the routing matches the test category in `send-to-helix-ci.proj`. Template tests run on both Windows and Linux Helix, so don't set `RunOnAzdoHelixWindows=false` on them.

### 5. Helix Timeout Exhaustion

**Pattern**: Tests that fit within GitHub Actions' generous timeouts exceed Helix's per-work-item timeout (originally 20 minutes), especially Docker-based tests that pull images.

**Real incidents**:
- `c6ff7dc2`: Docker-based functional tests (Milvus, MongoDB, Redis, MySQL, Nats, PostgreSQL, Seq) consistently timed out at 20 minutes → increased to 30 minutes
- `2dfa7e21`: `Aspire.Hosting.Azure.Tests` timed out at 31 minutes with the 30-minute limit → increased to 35 minutes

**What to watch for**: New Docker-based tests or tests with significant setup time being added to projects that run on Helix. Check if the existing timeout (`_workItemTimeout` in `send-to-helix-inner.proj`) is sufficient.

### 6. Tests Referencing Repo Paths or Test Projects at Runtime

**Pattern**: Tests that reference files relative to the repo root, or that use `AddProject<T>()` to load test project binaries, fail on Helix because the repo directory structure does not exist on the Helix agent. At runtime, Aspire resolves the original project paths embedded at build time, and those paths won't be present on Helix.

This applies in two ways:
- **File/directory references**: Tests that use `$(RepoRoot)`, `AppHostDirectory`, `Directory.GetCurrentDirectory()`, or hardcoded repo-relative paths to read/write files.
- **`AddProject` calls**: Tests that call `builder.AddProject<Projects.SomeTestProject>()` embed the original `.csproj` path. On Helix, that path doesn't exist, so the resource fails to start. Any test project whose tests reference other projects via `AddProject` cannot run on Helix as a basic test — all such files and project outputs would need to be packaged into the archive.

**Exception**: `Aspire.Playground.Tests` runs in the `buildonhelixtests` category, which does `dotnet build` + `dotnet test` on Helix itself with the full source tree. So `AddProject` calls work there because the projects are built and present on the agent.

**Real incidents**:
- `00e8aba4`: Python venv tests created directories under `builder.AppHostDirectory` which resolved to `/mnt/vss/` on Helix agents where the test user lacks write access
- `306e5419`: `VerifyDefaultDockerfile` test failed on Helix because it used a path relative to the repo

**What to watch for**: Tests that use `AddProject<T>()`, `AppHostDirectory`, `Directory.GetCurrentDirectory()`, repo-relative paths, or write to directories that might be read-only on Helix agents. If a test needs `AddProject`, it likely cannot run on Helix as a basic test — set `RunOnAzdoHelixWindows=false` / `RunOnAzdoHelixLinux=false`, or move it to the `buildonhelixtests` category. Use `TempDirectory` or `Path.GetTempPath()` for write operations.

### 7. Out-of-Repo Build Differences (buildonhelixtests)

**Pattern**: The `buildonhelixtests` category runs `dotnet build` + `dotnet test` from source on Helix. These tests use `AspireProjectOrPackageReference` instead of `ProjectReference` to resolve Aspire packages from built NuGet packages. Missing package references or incorrect conditions break the build.

**What to watch for**: Tests in the `buildonhelixtests` category (e.g., Playground.Tests) that add new `ProjectReference` entries. These should be `AspireProjectOrPackageReference` to work both in-repo and on Helix. Check `Aspire.RepoTesting.targets` for the conversion logic.

### 8. Snapshot Testing File Conflicts

**Pattern**: Multiple test projects have `Snapshots/` directories with identically-named verified files. When these are all copied to a single Helix correlation payload directory, they overwrite each other.

**Real incidents**:
- `4cac8573`: Snapshot files from different test projects overwrote each other → Verify assertions failed with unexpected diffs
- Fix: changed to per-archive snapshots instead of a shared correlation payload

**What to watch for**: Adding new Verify snapshot tests. Ensure the `Snapshots/` directory is included in the test archive (`tests/Directory.Build.targets` handles this) and that `TestModuleInitializer.cs` resolves snapshots relative to the test assembly, not the repo root.

## Summary: PR Review Checklist for AzDO Safety

When reviewing PRs, flag these for manual AzDO validation (`/azp run aspire-tests`):

- [ ] Changes to `tests/Directory.Build.props` or `tests/Directory.Build.targets`
- [ ] Changes to `eng/Testing.props` or `eng/Testing.targets`
- [ ] Changes to any file in `tests/helix/`
- [ ] Changes to `eng/pipelines/templates/BuildAndTest.yml`
- [ ] New test projects (check `RunOnAzdoHelix*` defaults)
- [ ] New source files added to tests that run on Helix
- [ ] Changes to `DOTNET_ROOT`, `PATH`, or SDK configuration
- [ ] New Docker/buildx-dependent tests (must use `[RequiresFeature]`)
- [ ] Tests using `AddProject<T>()` in projects that run on Helix
- [ ] Changes to `AspireProjectOrPackageReference` items
- [ ] New or modified Verify snapshot tests
- [ ] Changes to `tests/workloads.proj` or SDK setup

## How to Manually Trigger AzDO Tests

If you suspect your PR may affect AzDO tests, you can trigger a manual run:

```text
/azp run aspire-tests
```

This comment in a PR will trigger the `aspire-tests` pipeline, which runs both pipeline tests and Helix tests (same as the weekly scheduled run).

## Key Files Reference

| File | Purpose |
|---|---|
| `eng/pipelines/azure-pipelines-public.yml` | Pipeline entry point (triggers, schedule) |
| `eng/pipelines/azdo-tests.yml` | Manual trigger pipeline |
| `eng/pipelines/templates/public-pipeline-template.yml` | Job orchestration, test variant iteration |
| `eng/pipelines/templates/BuildAndTest.yml` | Build + test step definitions |
| `eng/pipelines/templates/send-to-helix.yml` | Helix submission step wrapper |
| `tests/helix/send-to-helix-ci.proj` | Helix category dispatcher |
| `tests/helix/send-to-helix-inner.proj` | Helix SDK project (work item builder) |
| `tests/helix/send-to-helix-basictests.targets` | Basic test work items |
| `tests/helix/send-to-helix-endtoendtests.targets` | E2E test work items (by scenario) |
| `tests/helix/send-to-helix-buildonhelixtests.targets` | Build-on-Helix test work items |
| `eng/Testing.props` | Default test runner properties |
| `eng/Testing.targets` | Test skip/run logic per runner context |
| `tests/Directory.Build.props` | Archive directory paths |
| `tests/Directory.Build.targets` | `ZipTestArchive` + `ExtractTestClassNames` targets |
| `tests/Shared/RepoTesting/Aspire.RepoTesting.targets` | Out-of-repo build support |
| `eng/test-configuration.json` | Retry configuration |
| `tests/helix/xunit.runner.json` | Helix-specific xUnit settings (no parallelism) |
