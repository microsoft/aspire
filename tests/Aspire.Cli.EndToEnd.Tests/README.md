# Aspire CLI End-to-End Tests

This test project contains end-to-end tests for the Aspire CLI using the [Hex1b](https://github.com/hex1b/hex1b) terminal automation library.

## Overview

These tests simulate real user interactions with the Aspire CLI by automating terminal input/output. Each test class runs as a **separate CI job** to enable parallel execution across GitHub Actions runners.

## Architecture

### Test Infrastructure

- **`CliEndToEndTestBase`**: Base class providing Hex1b terminal setup, work directory management, and helper methods
- **`HeadlessPresentationAdapter`**: A headless rendering adapter for running tests without a display
- **`TestEnumerationRunsheetBuilder`**: Unified MSBuild targets that extract test classes and generate per-class runsheets (via `SplitTestsOnCI=true`)

### CI Pipeline

The CI infrastructure uses a matrix strategy to fan out test execution:

1. **Discovery Phase**: The `TestEnumerationRunsheetBuilder` builds the test project and invokes the `GenerateTestPartitionsForCI` target to discover all test classes
2. **Matrix Generation**: A matrix entry is created for each unique test class, expanded for the target platform
3. **Parallel Execution**: GitHub Actions creates a separate job for each test class, running them in parallel on different agents

```plaintext
┌─────────────────────────────────────────────────────────────────┐
│                    generate_cli_e2e_matrix                       │
│    Discovers test classes and generates runsheet                 │
└─────────────────────────────────────────────────────────────────┘
                                │
                                ▼
┌─────────────────────────────────────────────────────────────────┐
│                      build_packages                              │
│    Builds NuGet packages needed for tests                        │
└─────────────────────────────────────────────────────────────────┘
                                │
                    ┌───────────┼───────────┐
                    ▼           ▼           ▼
            ┌───────────┐ ┌───────────┐ ┌───────────┐
            │ Job 1     │ │ Job 2     │ │ Job N     │
            │ NewCmd    │ │ RunCmd    │ │ ...       │
            │ Tests     │ │ Tests     │ │           │
            └───────────┘ └───────────┘ └───────────┘
```

## Writing Tests

### Test Class Structure

Each test class should:
1. Inherit from `CliEndToEndTestBase`
2. Implement `IAsyncLifetime` for proper setup/teardown
3. Call `await base.InitializeAsync()` in `InitializeAsync`
4. Call `await base.DisposeAsync()` in `DisposeAsync`

```csharp
public sealed class MyCommandTests : CliEndToEndTestBase, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await base.InitializeAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
    }

    [Fact]
    public async Task MyCommand_DoesExpectedThing()
    {
        // Use the helper methods to interact with the CLI
        await RunAspireAsync("my-command --option value");
        await WaitAsync(2000);
        
        // Assert on file system changes, process output, etc.
    }
}
```

### Available Helper Methods

- `RunAspireAsync(string arguments)`: Runs `aspire <arguments>` in the terminal
- `TypeAndEnterAsync(string text)`: Types text and presses Enter
- `WaitAsync(int milliseconds)`: Waits for the specified duration
- `CreateSequence()`: Creates a new `Hex1bTerminalInputSequenceBuilder` for complex interactions
- `WorkDirectory`: The unique temporary directory for this test

### Complex Interactions

For interactive prompts and complex terminal interactions:

```csharp
var sequence = CreateSequence()
    .SlowType("aspire new")
    .Enter()
    .Wait(2000)
    .Key(Hex1bKey.DownArrow)  // Navigate menu
    .Enter()                   // Select option
    .SlowType("myproject")     // Enter project name
    .Enter()
    .Build();

await sequence.ApplyAsync(Terminal);
```

## Running Tests Locally

### Daily smoke version pinning

`.github/workflows/tests-daily-smoke.yml` uses the acquisition script's
`--resolve-only` mode once per run. It passes the returned product version as
`ASPIRE_E2E_VERSION` and artifact build directory as `ASPIRE_E2E_BUILD_VERSION`.
Native archive smoke tests then invoke the script with `--version` and `--build-version`;
artifact URLs are constructed only by the acquisition script.

The resolved product/build pair takes precedence over `ASPIRE_E2E_QUALITY` in
strategy detection; quality is passed to the script only to retain the requested
install channel. Without a build pin, quality still takes precedence over a
version. The two version values preserve
stable-shaped staging artifacts whose build directory differs from their product
version. The stable-channel AppHost smoke scenarios still explicitly request
`channel: "stable"`; pinning the CLI does not pin their AppHost SDK.

Published dotnet tool installs select by `ASPIRE_E2E_QUALITY` when a complete
native product/build pin is present: `dev` and `staging` include prerelease
packages from the configured feeds, and `release` uses the latest stable package.
The native archive's product version may not yet be published as a tool package,
so these installs do not share its exact version. A version-only
`ASPIRE_E2E_VERSION` still requests that exact published tool version.
Explicit `ASPIRE_E2E_DOTNET_TOOL_SOURCE` and CI-built `BUILT_NUGETS_PATH`
sources retain precedence over published-feed selection.

Explicit native versions are verified against the installed product version,
ignoring `+commit` metadata and normalizing a stable version's optional `v`/`V`
prefix. The artifact build directory is not an installed-version expectation.
Quality-only installs have no predetermined version and remain diagnostic-only.

### npm test dependencies

The project-level `package.json` pins npm tools used to scaffold test fixtures.
`ViteTestHelpers.GetCreateCommand` reads the `create-vite` version from this manifest
instead of using `vite@latest`. The manifest is embedded in the test assembly so
local runs and CI test archives use the same version; no local `npm install` is
needed for this manifest.

Dependabot checks this directory weekly and waits seven days before proposing
new versions. The cooldown avoids adopting freshly published Vite templates whose
dependencies can be rejected by Deno's minimum dependency age policy. Keep tool
versions exact in the manifest, and use the shared helper rather than embedding
versions in individual tests.

### Commands

```bash
# Build the test project
./build.sh -restore -build -projects tests/Aspire.Cli.EndToEnd.Tests/Aspire.Cli.EndToEnd.Tests.csproj

# Run all tests
dotnet test tests/Aspire.Cli.EndToEnd.Tests/Aspire.Cli.EndToEnd.Tests.csproj

# Run a specific test class
dotnet test tests/Aspire.Cli.EndToEnd.Tests/Aspire.Cli.EndToEnd.Tests.csproj -- --filter-class "Aspire.Cli.EndToEnd.Tests.AspireNewCommandTests"
```

## Requirements

- **Linux only**: Hex1b requires a Linux terminal environment. Tests are skipped on Windows and macOS.
- **Aspire CLI installed**: The `aspire` command must be available in the PATH.
- **Built NuGet packages**: These tests require the Aspire packages to be built first.

## Adding New Test Classes

When adding a new test class:

1. Create a new file with the pattern `Aspire*Tests.cs` or `*CommandTests.cs`
2. Follow the test class structure above
3. The CI will automatically discover and run your tests as a separate job

No changes to the CI configuration are required — the `TestEnumerationRunsheetBuilder` automatically discovers all test classes in projects with `SplitTestsOnCI=true`.
