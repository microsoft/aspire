# MTP Args Pipeline

This document describes how Microsoft Testing Platform (MTP) command-line arguments flow from MSBuild through the CI pipeline to the final test execution in GitHub Actions.

## Overview

MTP diagnostic arguments (hang dump, crash dump, exit code handling, timeouts) are defined once in MSBuild and flow through the runsheet pipeline to the workflow that executes tests. This avoids duplication and allows per-project overrides.

```text
┌───────────────────────────────────────────────────────────────────────────┐
│                           MSBuild (build time)                            │
│                                                                           │
│  eng/Testing.props                                                        │
│    MtpBaseArgs = "<diagnostic flags> --hangdump-timeout <T> --timeout <S>"│
│    _BlameArgs  = MtpBaseArgs                                              │
│    TestRunnerAdditionalArguments = ... + _BlameArgs  (Arcade/Helix path)  │
│                                                                           │
│  TestEnumerationRunsheetBuilder.targets                                   │
│    → emits "mtpBaseArgs" in .tests-metadata.json (resolved per-project)   │
└───────────────────────┬───────────────────────────────────────────────────┘
                        │
                        ▼
┌───────────────────────────────────────────────────────────────────────────┐
│                       Pipeline scripts                                    │
│                                                                           │
│  build-test-matrix.ps1 → expand-test-matrix-github.ps1                   │
│    → matrix entries include mtpBaseArgs field (passthrough)               │
└───────────────────────┬───────────────────────────────────────────────────┘
                        │
                        ▼
┌───────────────────────────────────────────────────────────────────────────┐
│                       GitHub Actions workflows                            │
│                                                                           │
│  tests.yml / specialized-test-runner.yml                                  │
│    → passes mtpBaseArgs to run-tests.yml                                  │
│                                                                           │
│  run-tests.yml                                                            │
│    → uses ${{ inputs.mtpBaseArgs }} in test execution commands             │
│    → has a default value for backward compatibility                       │
└───────────────────────────────────────────────────────────────────────────┘
```

## Properties

### `MtpBaseArgs` (defined in `eng/Testing.props`)

The single source of truth for all MTP diagnostic and timeout arguments. Contains everything that should be passed to every test execution. Timeouts are baked into this property so they flow as one resolved string.

**Default value:** `--ignore-exit-code 8 --crashdump --hangdump --hangdump-type none --hangdump-timeout 10m --timeout 20m`

| Arg | Purpose |
|-----|---------|
| `--ignore-exit-code 8` | Don't fail the test run when zero tests match filters ([MTP exit codes](https://learn.microsoft.com/dotnet/core/testing/microsoft-testing-platform-exit-codes)) |
| `--crashdump` | Collect crash dumps on test host crash |
| `--hangdump` | Enable hang detection and hang dump handling |
| `--hangdump-type none` | Disable hang dump file creation; hang detection and timeout handling still occur, but no dump file is generated |
| `--hangdump-timeout <T>` | Per-test hang timeout (from `TestHangTimeout`, default `10m`); when reached, the hang is detected and the run aborts/fails |
| `--timeout <S>` | Overall session timeout (from `TestSessionTimeout`, default `20m`) |

### Timeout properties

Timeouts are composed into `MtpBaseArgs` at build time. Override per project by setting these in your test `.csproj`:

```xml
<PropertyGroup>
  <TestSessionTimeout>30m</TestSessionTimeout>
  <TestHangTimeout>15m</TestHangTimeout>
</PropertyGroup>
```

| Property | Default | Purpose |
|----------|---------|---------|
| `TestSessionTimeout` | `20m` | Overall test session timeout (`--timeout`) |
| `TestHangTimeout` | `10m` | Individual test hang timeout (`--hangdump-timeout`) |
| `UncollectedTestsSessionTimeout` | `15m` | Session timeout for the uncollected bucket in split tests |
| `UncollectedTestsHangTimeout` | `10m` | Hang timeout for the uncollected bucket in split tests |

For split test projects, the "uncollected" bucket (tests not in any named partition) uses `_UncollectedMtpBaseArgs`, which is built from the `Uncollected*` properties.

### `TestRunnerAdditionalArguments` (Arcade SDK)

Consumed by Arcade's `Microsoft.Testing.Platform.targets` for direct assembly execution (`TestAssembly.exe <args>`). This is the Helix/AzDO path.

Assembled in `eng/Testing.props` from:
- `--filter-not-trait "category=failing"`
- Filter args (quarantine/outerloop exclusions)
- `_BlameArgs` (which equals `MtpBaseArgs` — diagnostic flags + timeouts)

**Do not set this directly.** Modify `MtpBaseArgs` for diagnostic args, or the filter properties for test selection.

### `TestingPlatformCommandLineArguments` (MTP SDK)

Consumed by MTP's MSBuild integration during `dotnet test`. Auto-injected by the `Microsoft.Testing.Platform.MSBuild` package.

Set in:
- `tests/Directory.Build.targets` — filter shortcuts (`--filter-method`, `--filter-class`, `--filter-namespace`)
- `tests/Shared/RepoTesting/Aspire.RepoTesting.targets` — `--filter-not-trait "category=failing"`, TRX filename

**Do not put diagnostic args here.** They would duplicate with the explicit `-- <args>` passed by `run-tests.yml`.

## How to add a new MTP diagnostic arg

1. Edit `eng/Testing.props` — append the arg to `MtpBaseArgs` (or to `_MtpDiagnosticFlags` if it's timeout-independent)
2. That's it. The arg flows automatically through both paths:
   - **Arcade/Helix**: via `_BlameArgs` → `TestRunnerAdditionalArguments`
   - **GitHub Actions**: via runsheet → `run-tests.yml` `mtpBaseArgs` input

## Execution paths in `run-tests.yml`

### Nuget-dependent tests (direct assembly execution)

```bash
dotnet <assembly>.dll \
  ${{ inputs.mtpBaseArgs }} \
  --report-trx --report-trx-filename "..." \
  --results-directory ... \
  --filter-not-trait "category=failing" \
  ${{ inputs.extraTestArgs }}
```

MSBuild does not run in this path, so `TestingPlatformCommandLineArguments` does not apply. The `--filter-not-trait "category=failing"` is explicit here for that reason.

### Non-nuget tests (`dotnet test`)

```bash
dotnet test --project <path> --no-build -- \
  ${{ inputs.mtpBaseArgs }} \
  --report-trx \
  --results-directory ... \
  ${{ inputs.extraTestArgs }}
```

MTP MSBuild integration injects `TestingPlatformCommandLineArguments` automatically (includes `--filter-not-trait "category=failing"` and TRX filename). These args are complementary to the explicit `-- <args>`.

## Backward compatibility

The `run-tests.yml` `mtpBaseArgs` input has a default value that includes diagnostic flags (crashdump, hangdump, exit-code handling) but does not include timeout arguments. Timeout values are baked into `mtpBaseArgs` at build time by MSBuild (via `eng/Testing.props`) and flow through the test matrix metadata. Callers that bypass the matrix and don't pass `mtpBaseArgs` should include the timeout arguments explicitly if needed.
