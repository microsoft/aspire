# Agent Instructions

Instructions for GitHub Copilot and other AI coding agents working with the Aspire repository.

## Repository Overview

**Aspire** provides tools, templates, and packages for building observable, production-ready distributed applications. At its core is an app model that defines services, resources, and connections in a code-first approach.

### Key Components
- **Aspire.Hosting**: Application host orchestration and resource management
- **Aspire.Dashboard**: Web-based dashboard for monitoring and debugging
- **Service Discovery**: Infrastructure for service-to-service communication
- **Integrations**: 40+ packages for databases (SQL Server, PostgreSQL, Redis, MongoDB), message queues (RabbitMQ, Kafka), cloud services (Azure), and more
- **CLI Tools**: Command-line interface for project creation and management
- **Project Templates**: Starter templates for new Aspire applications

### Technology Stack
- .NET 10.0
- C# 13 preview features
- xUnit SDK v3 with Microsoft.Testing.Platform for testing
- Microsoft.DotNet.Arcade.Sdk for build infrastructure
- Native AOT compilation for CLI tools
- Multi-platform support (Windows, Linux, macOS, containers)

## General

* Make only high confidence suggestions when reviewing code changes.
* Always use the latest version C#, currently C# 13 features.
* Never change global.json unless explicitly asked to.
* Never change package.json or package-lock.json files unless explicitly asked to.
* Never change NuGet.config files unless explicitly asked to.
* Before adding helper methods, utilities, test fakes, scripts, or workflow logic, search for existing implementations and reuse or extend them instead of duplicating private helpers. For tests, prefer shared helpers from `tests/Shared/` (for example `TestTempDirectory` for auto-cleaned temporary directories and `TestRepoRoot` for locating the repository root).
* Do not use cryptographic hashes such as SHA-256 when the hash is not security-related. Prefer `System.IO.Hashing.XxHash3` when you need a stable non-cryptographic hash.
* When code needs a temporary directory, prefer the repository temp directory abstractions first (for example `IFileSystemService.TempDirectory` / `ITempFileSystemService`) and otherwise use `Directory.CreateTempSubdirectory()` instead of `Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())`; if you need a temporary file path, place it under a securely created temp directory.
* Don't update files under `*/api/*.cs` (e.g. src/Aspire.Hosting/api/Aspire.Hosting.cs) as they are generated.

## Code Review Instructions

### API Files and Public API Surface

The API files located in `*/api/*.cs` (e.g., `src/Aspire.Hosting/api/Aspire.Hosting.cs`) track the public API surface that has already been shipped in the latest release. These files are auto-generated and serve as a baseline for API compatibility checks.

When reviewing pull requests:

* **Do not comment when new public API is introduced and the API files are not regenerated**. This is expected behavior during active development between releases.
* New public APIs should be reviewed for design, naming, and functionality, but the absence of API file updates during PR development is normal.
* API files are regenerated as part of the release process when we ship a new version, not during individual PRs.
* Only flag API file concerns if:
  - API files are manually edited (they should never be manually modified)
  - There are breaking changes to existing APIs without proper justification
  - The PR explicitly claims to update API compatibility but doesn't regenerate the files

### NuGet Feed Configuration

The NuGet.config file defines approved package sources for the internal build. External package feeds can break the internal build pipeline.

When reviewing pull requests:

* **Flag any changes to NuGet.config that add package sources not from these approved domains:**
  - `https://pkgs.dev.azure.com/dnceng`
  - `https://dnceng.pkgs.visualstudio.com/public`
* **Flag any additions of external NuGet feeds** such as:
  - `https://api.nuget.org/v3/index.json` (nuget.org)
  - Any other public or third-party package sources
* If a PR adds an external feed, request that:
  - The packages be mirrored to an approved internal feed, or
  - Use existing internal feeds that already mirror public packages (like dotnet-public, dotnet-eng)
* The wildcard pattern mappings (`<package pattern="*" />`) in dotnet-public and dotnet-eng feeds typically provide access to commonly-used public packages

## Formatting

* Apply code-formatting style defined in `.editorconfig`.
* Prefer file-scoped namespace declarations and single-line using directives.
* Insert a newline before the opening curly brace of any code block (e.g., after `if`, `for`, `while`, `foreach`, `using`, `try`, etc.).
* Ensure that the final return statement of a method is on its own line.
* Use pattern matching and switch expressions wherever possible.
* Use `nameof` instead of string literals when referring to member names.
* Place private class declarations at the bottom of the file.

### Nullable Reference Types

* Declare variables non-nullable, and check for `null` at entry points.
* Always use `is null` or `is not null` instead of `== null` or `!= null`.
* Trust the C# null annotations and don't add null checks when the type system says a value cannot be null.

### Building

**Always run restore first to set up the local SDK.** Run `./restore.sh` (Linux/macOS) or `./restore.cmd` (Windows) first to install the local SDK. After restore, you can use standard `dotnet` commands, which will automatically use the local SDK when available due to the paths configuration in global.json.

#### Prerequisites
1. **Restore First**: Always run `./restore.sh` (Linux/macOS) or `./restore.cmd` (Windows) to set up the local .NET SDK (~30 seconds)

#### Build Commands
- **Full Build**: `./build.sh` (Linux/macOS) or `./build.cmd` (Windows) - defaults to restore + build (~3-5 minutes)
- **Build Only**: `./build.sh --build` (assumes restore already done)
- **Skip Native Build**: Add `/p:SkipNativeBuild=true` to avoid slow native AOT compilation (~1-2 minutes saved)
- **Clean Build**: `./build.sh --rebuild`
- **Package Generation**: `./build.sh --pack` to create NuGet packages
- If you need to disable the terminal logger for `dotnet`/build-related commands, prefer setting `MSBUILDTERMINALLOGGER=false` instead of passing `-tl:false`; avoid `-tl:false` on commands that may invoke tests because it can be forwarded to the test host and fail under Microsoft.Testing.Platform native runner mode

#### Build Troubleshooting
- If temporarily introducing warnings during refactoring, add `/p:TreatWarningsAsErrors=false` to prevent build failure
- **Important**: All warnings should be addressed before committing any final changes
- Template engine warnings about "Missing generatorVersions" are expected and not errors
- If build fails with SDK errors, run `./restore.sh` again to ensure correct .NET 10 RC is installed
- Build artifacts go to `./artifacts/` directory

#### Visual Studio / VS Code Setup
- **VS Code**: Run `./build.sh` first, then use `./start-code.sh` to launch with correct environment
- **Visual Studio**: Run `./build.cmd` first, then use `./startvs.cmd` to launch with local SDK environment

### Start App Hosts in a background job

When running an App Host from an interactive console (for example, PowerShell or a terminal session you plan to reuse), start it in a background job/process. If you start the App Host in the foreground and then reuse the console for another command (or stop the current interactive session), the App Host process can be terminated.

Run the App Host in the background so it continues running while you execute other commands:

**PowerShell (Start-Job)**

```powershell
# From the AppHost project directory
Start-Job -Name "AspireAppHost" -ScriptBlock { dotnet run }

# Inspect output / state
Get-Job -Name "AspireAppHost" | Format-List *
Receive-Job -Name "AspireAppHost" -Keep

# Stop when done
Stop-Job -Name "AspireAppHost"
Remove-Job -Name "AspireAppHost"
```

**bash (background process)**

```bash
# From the AppHost project directory
dotnet run > apphost.log 2>&1 &

# Find and stop later
ps aux | grep dotnet
kill <pid>
```

### Testing

* We use xUnit SDK v3 with Microsoft.Testing.Platform (https://learn.microsoft.com/dotnet/core/testing/microsoft-testing-platform-intro)
* Do not emit "Act", "Arrange" or "Assert" comments.
* We do not use any mocking framework at the moment.
* Copy existing style in nearby files for test method names and capitalization.
* Do not leave newly-added tests commented out. All added tests should be building and passing.
* Do not use Directory.SetCurrentDirectory in tests as it can cause side effects when tests execute concurrently.
* Prefer using shared test service implementations (e.g., project-level `TestServices/` or `Helpers/` directories, or the cross-project `tests/Shared/` folder) rather than creating private implementation classes within individual test files. Reusing existing test fakes and helpers keeps tests consistent, reduces duplication, and makes maintenance easier. Do not create private test classes when a shared one already exists or can be extended.
* MTP diagnostic args (hang dump, crash dump, exit code handling) are defined in `eng/Testing.props` via `MtpBaseArgs`. Do not hardcode these args in workflow YAML. See [docs/ci/mtp-args-pipeline.md](docs/ci/mtp-args-pipeline.md) for details.

## Running tests

(1) Build from the root with `./build.sh` (~3-5 minutes).
(2) If that produces errors, fix those errors and build again. Repeat until the build is successful.
(3) To run tests for a specific project: `dotnet test --project tests/ProjectName.Tests/ProjectName.Tests.csproj --no-build --no-launch-profile -- --filter-not-trait "quarantined=true" --filter-not-trait "outerloop=true"`

Note that tests for a project can be executed without first building from the root.

(4) To run specific tests, include the filter after `--`:
```bash
dotnet test --project tests/Aspire.Hosting.Testing.Tests/Aspire.Hosting.Testing.Tests.csproj --no-launch-profile -- --filter-method "*.TestingBuilderHasAllPropertiesFromRealBuilder" --filter-not-trait "quarantined=true" --filter-not-trait "outerloop=true"
```

(5) To apply a timeout for a specific test run use `--hangdump` and `--hangdump-timeout` options after `--`, for example:
```bash
dotnet test --project tests/Aspire.Hosting.Testing.Tests/Aspire.Hosting.Testing.Tests.csproj --no-launch-profile -- --filter-method "*.TestingBuilderHasAllPropertiesFromRealBuilder" --hangdump --hangdump-timeout 2m
```
You need both options (`--hangdump-timeout` does not work without `--hangdump`). Timeout can be expressed in minutes (e.g. `3m` for 3-minute timeout), or seconds (e.g. `30s` for 30-seconds timeout).

**Important**: Avoid passing `--no-build` unless you have just built in the same session and there have been no code changes since. In automation or while iterating on code, omit `--no-build` so changes are compiled and picked up by the test run.

### CRITICAL: Do NOT use VSTest-style `--filter` with `dotnet test`

This repo uses **Microsoft.Testing.Platform (MTP)** as the test runner, not VSTest. The classic `--filter` argument (before `--`) uses VSTest filter syntax and **will hang or behave unexpectedly** with MTP.

```bash
# WRONG - VSTest-style filter, will hang with MTP
dotnet test --project tests/Project.Tests/Project.Tests.csproj --filter "FullyQualifiedName~ClassName"

# CORRECT - MTP-native filters go after the -- separator
dotnet test --project tests/Project.Tests/Project.Tests.csproj --no-launch-profile -- --filter-class "*.ClassName"
dotnet test --project tests/Project.Tests/Project.Tests.csproj --no-launch-profile -- --filter-method "*.MethodName"
```

All test filtering must use MTP-native switches placed **after `--`**. See the filter switches listed below for the full set of options.

### CRITICAL: Excluding Quarantined and Outerloop Tests

When running tests in automated environments (including Copilot agent), **always exclude quarantined and outerloop tests** to avoid false negatives and long-running tests:

```bash
# Correct - excludes quarantined and outerloop tests (use this in automation)
dotnet test --project tests/Project.Tests/Project.Tests.csproj --no-launch-profile -- --filter-not-trait "quarantined=true" --filter-not-trait "outerloop=true"

# For specific test filters, combine with quarantine and outerloop exclusion
dotnet test --project tests/Project.Tests/Project.Tests.csproj --no-launch-profile -- --filter-method "TestName" --filter-not-trait "quarantined=true" --filter-not-trait "outerloop=true"
```

Never run all tests without the quarantine and outerloop filters in automated environments, as this will include flaky tests that are known to fail intermittently and long-running tests that slow down CI.

Valid test filter switches include: --filter-class, --filter-not-class, --filter-method, --filter-not-method, --filter-namespace, --filter-not-namespace, --filter-not-trait, --filter-trait
The switches `--filter-class` and `--filter-method` expect fully qualified names, unless a filter is used as a prefix like `--filter-class "*.SomeClassName"` or `--filter-method "*.SomeMethodName"`.
These switches can be repeated to run tests on multiple classes or methods at once, e.g., `--filter-method "*.SomeMethodName1" --filter-method "*.SomeMethodName2"`.

### Test Verification Commands
- **Single Test Project**: Typical runtime ~10-60 seconds per test project
- **Full Test Suite**: Can take 30+ minutes, use targeted testing instead

## Project Layout and Architecture

### Directory Structure
- **`/src`**: Main source code for all Aspire packages
  - `Aspire.Hosting/`: Core hosting and orchestration infrastructure
  - `Aspire.Dashboard/`: Web dashboard UI (Blazor application)
  - `Components/`: 40+ integration packages for databases, messaging, cloud services
  - `Aspire.Cli/`: Command-line interface tools
- **`/tests`**: Comprehensive test suites mirroring src structure
- **`/playground`**: Sample applications including TestShop for verification
- **`/docs`**: Documentation including contributing guides and area ownership
- **`/eng`**: Build scripts, tools, and engineering infrastructure
- **`/.github`**: CI/CD workflows, issue templates, and GitHub automation
- **`/extension`**: VS Code extension source code

### Key Configuration Files
- **`global.json`**: Pins .NET SDK version - never modify without explicit request
- **`.editorconfig`**: Code formatting rules, null annotations, diagnostic configurations
- **`Directory.Build.props`**: Shared MSBuild properties across all projects
- **`Directory.Packages.props`**: Centralized package version management
- **`Aspire.slnx`**: Main solution file (XML-based solution format)

### Continuous Integration

#### GitHub Actions (primary, runs on PRs)
- **`tests.yml`**: Main test workflow running across Windows/Linux/macOS
- **`tests-quarantine.yml`**: Runs quarantined tests separately every 6 hours
- **`tests-outerloop.yml`**: Runs outerloop tests separately every 6 hours
- **`ci.yml`**: Main CI workflow triggered on PRs and pushes to main/release branches
- **Build validation**: Includes package generation, API compatibility checks, template validation
- **Workflow matcher maintenance**: When changing CI workflow job or step names that are referenced by automation or tests, update the corresponding workflow helpers, behavior tests, and docs together. For the transient rerun workflow, keep `.github/workflows/auto-rerun-transient-ci-failures.js`, `tests/Infrastructure.Tests/WorkflowScripts/AutoRerunTransientCiFailuresTests.cs`, and `docs/ci/auto-rerun-transient-ci-failures.md` aligned with the live workflow YAML.
- **⚠️ Quarantine and outerloop tests are easily broken** because they primarily run on schedule, not on most PRs. Changes to `tests-quarantine.yml`, `tests-outerloop.yml`, `specialized-test-runner.yml`, or `run-tests.yml` will automatically trigger the affected workflow(s) on the PR via `paths:` filters. Verify the triggered runs pass before merging.

#### Azure DevOps (secondary, does NOT run tests on PRs)
- **`eng/pipelines/azure-pipelines-public.yml`**: Weekly scheduled pipeline (Monday midnight UTC) that builds and runs tests on Helix
- **⚠️ AzDO tests are easily broken** because they don't run on PRs — only weekly or via manual trigger (`/azp run aspire-tests`)
- Changes to test infrastructure (`eng/Testing.props`, `eng/Testing.targets`, `tests/Directory.Build.*`, `tests/helix/*`) should be validated by triggering a manual AzDO run
- See [docs/ci/azdo-public-pipeline.md](docs/ci/azdo-public-pipeline.md) for full architecture details including Helix test categories, archive process, and test routing

### Dependencies and Hidden Requirements
- **Local .NET SDK**: Automatically uses local SDK when available after running restore due to paths configuration in global.json
- **Package References**: Centrally managed via Directory.Packages.props
- **API Surface**: Public APIs tracked in `src/*/api/*.cs` files (auto-generated, don't edit)

### Common Validation Steps
1. **Build Verification**: `./build.sh` should complete without errors
2. **Package Generation**: `./build.sh --pack` verifies all packages can be created
3. **Specific Tests**: Target individual test projects related to your changes

## Quarantined tests

- Tests that are flaky and don't fail deterministically are marked with the `QuarantinedTest` attribute.
- Such tests are not run as part of the regular tests workflow (`tests.yml`).
    - Instead they are run in the `Quarantine` workflow (`tests-quarantine.yml`).
- A github issue url is used with the attribute
- To **reproduce or fix** a flaky/quarantined test, use the `fix-flaky-test` skill (`.github/skills/fix-flaky-test/SKILL.md`).
- To **quarantine or unquarantine** a test, use the `test-management` skill (`.github/skills/test-management/SKILL.md`).

Example: `[QuarantinedTest("..issue url..")]`

### Quarantine/Unquarantine via GitHub Commands (Preferred)

Use these commands in any issue or PR comment. They require write access to the repository.

```bash
# Quarantine a flaky test (creates a new PR)
/quarantine-test Namespace.Type.Method https://github.com/microsoft/aspire/issues/1234

# Quarantine multiple tests at once
/quarantine-test TestMethod1 TestMethod2 https://github.com/microsoft/aspire/issues/1234

# Quarantine and push to an existing PR
/quarantine-test TestMethod https://github.com/microsoft/aspire/issues/1234 --target-pr https://github.com/microsoft/aspire/pull/5678

# Unquarantine a test (creates a new PR)
/unquarantine-test Namespace.Type.Method

# Unquarantine and push to an existing PR
/unquarantine-test TestMethod --target-pr https://github.com/microsoft/aspire/pull/5678
```

When you comment on a PR, the changes are automatically pushed to that PR's branch (no need for `--target-pr`).

### Quarantine/Unquarantine via Local Tool

For local development, use the QuarantineTools directly:

```bash
# Quarantine a test
dotnet run --project tools/QuarantineTools -- -q -i https://github.com/microsoft/aspire/issues/1234 Full.Namespace.Type.Method

# Unquarantine a test
dotnet run --project tools/QuarantineTools -- -u Full.Namespace.Type.Method
```

## Disabled tests (ActiveIssue)

- Tests that consistently fail due to a known bug or infrastructure issue are marked with the `ActiveIssue` attribute.
- These tests are completely skipped until the underlying issue is resolved.
- Use this for tests that are **blocked**, not for flaky tests (use `QuarantinedTest` for flaky tests).

Example: `[ActiveIssue("https://github.com/microsoft/aspire/issues/1234")]`

### Disable/Enable via GitHub Commands (Preferred)

```bash
# Disable a test due to an active issue (creates a new PR)
/disable-test Namespace.Type.Method https://github.com/microsoft/aspire/issues/1234

# Disable and push to an existing PR
/disable-test TestMethod https://github.com/microsoft/aspire/issues/1234 --target-pr https://github.com/microsoft/aspire/pull/5678

# Enable a previously disabled test (creates a new PR)
/enable-test Namespace.Type.Method

# Enable and push to an existing PR
/enable-test TestMethod --target-pr https://github.com/microsoft/aspire/pull/5678
```

### Disable/Enable via Local Tool

```bash
# Disable a test with ActiveIssue
dotnet run --project tools/QuarantineTools -- -q -m activeissue -i https://github.com/microsoft/aspire/issues/1234 Full.Namespace.Type.Method

# Enable a test (remove ActiveIssue)
dotnet run --project tools/QuarantineTools -- -u -m activeissue Full.Namespace.Type.Method
```

## Outerloop tests

- Tests that are long-running, resource-intensive, or require special infrastructure are marked with the `OuterloopTest` attribute.
- In this repository, always use `OuterloopTest` for outerloop coverage; do not replace it with Arcade's `OuterLoop` attribute because our CI scripts and some test projects rely on assembly-level use of the custom trait.
- Such tests are not run as part of the regular tests workflow (`tests.yml`).
    - Instead they are run in the `Outerloop` workflow (`tests-outerloop.yml`).
- An optional reason can be provided with the attribute

Example: `[OuterloopTest("Long running integration test")]`

## Snapshot Testing with Verify

* We use the Verify library (Verify.XunitV3) for snapshot testing in several test projects.
* Snapshot files are stored in `Snapshots` directories within test projects.
* When tests that use snapshot testing are updated and generate new output, the snapshots need to be accepted.
* Use `dotnet verify accept -y` to accept all pending snapshot changes after running tests.
* The verify tool is available globally as part of the copilot setup.

## Editing resources

The `*.Designer.cs` files are in the repo, but are intended to match same named `*.resx` files. If you add/remove/change resources in a resx, make the matching changes in the `*.Designer.cs` file that matches that resx. The `*.Designer.cs` files are generated by a Visual Studio design-time tool (`ResXFileCodeGenerator` or `PublicResXFileCodeGenerator`) and there is no command-line tool to regenerate them, so they must be updated manually.

Some projects also have `*.xlf` (XLIFF) translation files in an `xlf` subdirectory next to the `.resx` files. After modifying a `.resx` file, run the following command on the affected project to update the `.xlf` files:

```shell
dotnet build /t:UpdateXlf <path-to-project.csproj>
```

Do not manually edit `*.xlf` files. They are updated by the `UpdateXlf` MSBuild target (provided by [Microsoft.DotNet.XliffTasks](https://github.com/dotnet/arcade/tree/main/src/Microsoft.DotNet.XliffTasks)).

## Markdown files

* Markdown files should not have multiple consecutive blank lines.
* Code blocks should be formatted with triple backticks (```) and include the language identifier for syntax highlighting.
* JSON code blocks should be indented properly.

## Localization files
* Files matching the pattern `*/localize/templatestrings.*.json` are localization files. Do not translate their content. It is done by a dedicated workflow.
## Trust These Instructions

These instructions are comprehensive and tested. Only search for additional information if:
1. The instructions appear outdated or incorrect
2. You encounter specific errors not covered here
3. You need details about new features not yet documented

For most development tasks, following these instructions should be sufficient to build, test, and validate changes successfully.

## Typescript

* When possible, you should create Typescript files instead of Javascript files.
* You must not use dynamic imports unless absolutely necessary. Instead, use static imports.

## Aspire VS Code Extension

* When displaying text to the user, ensure that the strings are localized. New localized strings must be put both in the extension `package.nls.json` and also `src/loc/strings.ts`.

## Available Skills

The following specialized skills are available in `.github/skills/`:

- **cli-e2e-testing**: Guide for writing Aspire CLI end-to-end tests using Hex1b terminal automation
- **ci-test-failures**: Diagnoses GitHub Actions test failures, extracts failed tests from runs, and creates or updates failing-test issues
- **code-review**: Reviews a GitHub pull request for problems (bugs, security, correctness, convention violations). Use this when asked to review a PR or do a code review.
- **fix-flaky-test**: Reproduces and fixes flaky/quarantined tests using the CI reproduce workflow (`reproduce-flaky-tests.yml`). Use this when investigating, reproducing, or fixing a flaky or quarantined test.
- **dashboard-testing**: Guide for writing tests for the Aspire Dashboard using xUnit and bUnit
- **test-management**: Quarantines or disables flaky/problematic tests using the QuarantineTools utility
- **connection-properties**: Expert for creating and improving Connection Properties in Aspire resources
- **dependency-update**: Guides dependency version updates by checking nuget.org, triggering the dotnet-migrate-package Azure DevOps pipeline, and monitoring runs
- **api-review**: Reviews .NET API surface area PRs for design guideline violations, applies rules from .NET Framework Design Guidelines and Aspire conventions, and attributes findings to the author who introduced each API
- **backport-pr**: Triggers the `/backport` bot on a source PR, waits for the bot-created backport PR, and fills in the shiproom template (Customer Impact, Testing, Risk, Regression?). Use when backporting a fix to a release branch.
- **startup-perf**: Measures Aspire application startup performance using dotnet-trace and the TraceAnalyzer tool
- **reviewing-aspire-architecture**: Reviews PRs for Aspire-specific architectural patterns across 15 dimensions including API design, resource model, Azure provisioning, pattern conformance, dashboard UX, CLI behavior, and more. Complements the code-review skill with domain knowledge that generic review cannot catch.

## Pattern-Based Instructions

Additional instructions are automatically applied when editing files matching specific patterns:

| Pattern | Instructions File |
|---------|-------------------|
| `src/**/*.cs` | `.github/instructions/xmldoc.instructions.md` - XML documentation standards |
| `src/Aspire.Hosting/**/*.cs` | `.github/instructions/hosting-core.instructions.md` - Hosting core review patterns |
| `src/Aspire.Hosting.Azure*/**/*.cs` | `.github/instructions/hosting-azure.instructions.md` - Hosting Azure review patterns |
| `src/Aspire.Dashboard/**/*.{cs,razor,js}` | `.github/instructions/dashboard.instructions.md` - Dashboard review patterns |
| `src/Components/**/*.cs` | `.github/instructions/components.instructions.md` - Client integration review patterns |
| `src/Aspire.Hosting*/README.md` | `.github/instructions/hosting-readme.instructions.md` - Hosting integration READMEs |
| `src/Components/**/README.md` | `.github/instructions/client-readme.instructions.md` - Client integration READMEs |
| `tools/QuarantineTools/*` | `.github/instructions/quarantine.instructions.md` - QuarantineTools usage |
| `tests/**/*.cs` | `.github/instructions/test-review-guidelines.instructions.md` - Flaky test patterns and test review guidelines |
| `eng/scripts/get-aspire-cli*.sh`, `eng/scripts/get-aspire-cli*.ps1` | `.github/instructions/acquisition-tests.instructions.md` - CLI acquisition script tests |
