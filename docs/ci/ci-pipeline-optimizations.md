# CI Pipeline Optimizations

This document explains the CI pipeline optimizations in the current test workflow, why the jobs are structured the way they are, and what to verify when changing that structure.

## Goals

The test pipeline is optimized for three things:

1. **Short time to first useful test signal**
2. **Short total wall-clock time**
3. **Clear dependency boundaries between shared artifacts and test jobs**

Those goals are the reason the workflow prefers several smaller, dependency-aware jobs over a single large serialized build-and-test lane.

## Current structure

The main test workflow in `.github/workflows/tests.yml` is organized into these phases:

1. `setup_for_tests`
   - Enumerates tests and produces the expanded `all_tests` matrix
   - Splits the matrix into dependency buckets with `eng/scripts/split-test-matrix-by-deps.ps1`

2. `build_packages`
   - Produces the shared Aspire package feed used by package-dependent tests
   - Leaves CLI packaging and RID-specific DCP/Dashboard work to the CLI archive jobs

3. `build_cli_archive_linux`, `build_cli_archive_windows`, `build_cli_archive_macos`
   - Build native CLI archives per RID
   - Build the matching RID-specific DCP and Dashboard packages locally
   - Upload `built-nugets-for-{rid}` artifacts for downstream tests
   - Upload `cli-native-archives-{rid}` artifacts for archive consumers such as polyglot validation

4. Test execution lanes
   - `tests_no_nugets`
   - `tests_requires_nugets_linux`
   - `tests_requires_nugets_windows`
   - `tests_requires_nugets_macos`
   - `tests_requires_cli_archive`
   - `polyglot_validation`
   - `extension_tests_win`

5. `results`
   - Aggregates the final workflow status after all required lanes finish

## Why the jobs are split this way

### 1. Package build and CLI archive build run in parallel

`build_packages` and the CLI archive jobs intentionally run side by side.

Without that split, CLI archive creation would sit behind the shared package build even though most of the archive work is independent. The archive workflow now builds the RID-specific DCP and Dashboard packages it needs locally, so there is no reason to serialize it behind `build_packages`.

This reduces time-to-artifact for:

- CLI archive consumers
- polyglot validation
- any tests that need RID-specific DCP or Dashboard packages

### 2. CLI archive builds are per OS

The workflow uses one CLI archive job per platform instead of one combined matrix dependency.

That allows downstream jobs to wait only for the archive that matches their runner and RID:

- Linux tests wait for `build_cli_archive_linux`
- Windows tests wait for `build_cli_archive_windows`
- macOS tests wait for `build_cli_archive_macos`

If these stayed grouped behind one logical dependency, the slowest platform would delay every dependent test lane.

### 3. `requires_nugets` tests are split by OS

Tests that need built packages also need the correct RID-specific DCP/Dashboard packages. Because those packages are produced by the CLI archive job for the same OS, the workflow splits the `requires_nugets` bucket into Linux, Windows, and macOS lanes.

That keeps the dependency graph aligned with artifact production:

- the Linux lane does not wait for Windows or macOS artifacts
- the Windows lane does not wait for Linux or macOS artifacts
- the macOS lane does not wait for Linux or Windows artifacts

### 4. `tests_requires_cli_archive` depends only on the Linux CLI archive lane

The current `requires_cli_archive` consumers run on Linux, so the workflow depends on `build_cli_archive_linux` instead of every CLI archive job. It still waits for `build_packages`, but among the CLI archive jobs it only needs the Linux lane.

This is intentional. Requiring all CLI archive builds would add wait time with no benefit for the current consumers. If future archive-dependent tests are added for Windows or macOS, this dependency should be revisited.

### 5. `tests_no_nugets` starts as soon as possible

The `no_nugets` buckets are the fastest way to get broad CI feedback because they do not depend on package or archive production. Starting them immediately reduces the time before developers see failures in unit tests and other self-contained lanes.

### 6. `results` stays centralized

Even though execution is more parallel, the workflow still uses a single `results` job so GitHub reports one final, easy-to-understand outcome for the overall test pipeline.

## Additional optimizations in the current workflow

### 1. Linux critical-path builds use the 8-core runner

`build_cli_archive_linux` and `build_packages` both run on `8-core-ubuntu-latest`. The Linux CLI archive is on the critical path for linux-only consumers such as `tests_requires_cli_archive` and `polyglot_validation`, and `build_packages` is shared by every package-dependent lane.

That makes Linux the best place to spend extra build capacity: finishing the shared package build and Linux archive earlier directly reduces the time before downstream validation lanes can start.

### 2. The shared package build stays package-only

The shared build produces the common package feed; the per-RID jobs own CLI
archives, CLI tool and npm packages, and bundle dependencies. This keeps the
jobs independent and avoids producing CLI payloads twice.

Excluding bundle dependencies alone does not enforce that boundary: CLI
archive projects are selected independently and can still invoke publishing
and packaging. Changes to project selection must preserve both sides of the
boundary, not just remove the CLI from the managed project list.

### 3. The VS Code extension is built in its dedicated test lane

The extension work now lives in `extension_tests_win` instead of also being folded into `build_packages`.

That keeps `build_packages` focused on the shared package feed, and because `extension_tests_win` has no dependency on the package or archive jobs it can run independently instead of lengthening those critical paths.

## Artifact ownership and consumption

Artifact boundaries follow what downstream jobs consume, rather than
everything a producer happens to build:

| Artifact | Producer | Purpose |
|---|---|---|
| `built-nugets` | Shared package build | Common NuGet feed, without CLI payloads |
| `built-nugets-for-{rid}` | Matching CLI archive job | RID-specific NuGets for the local test feed |
| `cli-native-archives-{rid}` | Matching CLI archive job | Native CLI for archive consumers and downstream packaging |
| `cli-npm-packages-{rid}` | Matching CLI archive job | npm pointer and RID tarballs for investigation and manual downloads |

Package-dependent tests combine the shared feed with the RID-specific feed
matching their runner. That is why their dependencies must preserve the OS
relationship between test lane and CLI archive lane.

npm packaging and verification still run, but their outputs are separate so
NuGet consumers do not pay to download them. This reduces artifact transfer,
not native compilation. Keep archive and package consumers explicit rather
than relying on incidental files in another artifact.

The workflow files are the source of truth for build arguments and step
ordering. For the signing and payload constraints behind native packaging,
see [Native CLI Packaging](native-cli-packaging.md).

## What to validate when changing this area

When changing the CI structure, verify:

1. **Artifact production still matches artifact consumption**
   - `build_packages` still publishes the shared package feed
   - each CLI archive lane still publishes the correct `built-nugets-for-{rid}` artifact

2. **Each test lane waits only for the work it actually needs**
   - avoid adding cross-OS dependencies unless a consumer truly needs them

3. **Linux-only consumers stay on the shortest path**
   - especially `tests_requires_cli_archive` and `polyglot_validation`

4. **Matrix bucket sizes stay under GitHub's limits**
   - `split-test-matrix-by-deps.ps1` enforces the 256-job hard limit

5. **Documentation stays in sync**
   - update this file and `docs/ci/TestingOnCI.md` whenever the dependency graph changes

## Source-index build

The standalone daily pipeline in `eng/pipelines/azure-pipelines-source-index.yml`
builds the full repository for source indexing, including managed Dashboard,
CLI, tests and playground projects. It skips native Dashboard publishing and
packaging, and browser provisioning: neither contributes managed source
coverage. Managed Dashboard and test compilation remain included.

These exclusions are local to the source-index pipeline; other builds retain
their defaults, and native CLI work remains enabled. A broad native-build
exclusion is not a substitute because it also removes CLI projects. Likewise,
repackaging existing native Dashboard outputs is not a way to skip producing
them: it fails when those outputs are absent.

When changing this boundary, compare the before/after compiler source inputs
in the build binlogs and the solution generated by the pipeline's `BinLogToSln`
step. A successful build alone does not establish indexing coverage. Preserve
the full managed project selection, including tests and playground, and verify
source-index conversion/upload and CodeQL finalization on an active-scan run.

## CodeQL pipeline SDK setup

The scheduled CodeQL pipeline relies on the repository's restore bootstrap to
install the SDK pinned in `global.json`. Keep that responsibility with the
repository bootstrap: the Azure Pipelines SDK installation task's global-json
mode excludes prerelease SDKs and can fail before the bootstrap has a chance
to install the pinned version.
