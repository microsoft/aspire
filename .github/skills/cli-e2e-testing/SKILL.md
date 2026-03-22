---
name: cli-e2e-testing
description: Guide for writing Aspire CLI end-to-end tests using Hex1b terminal automation. Use this when asked to create, modify, or debug CLI E2E tests.
---

# Aspire CLI End-to-End Testing with Hex1b

This skill provides patterns and practices for writing end-to-end tests for the Aspire CLI using the Hex1b terminal automation library.

## Overview

CLI E2E tests use the Hex1b library to automate terminal sessions, simulating real user interactions with the Aspire CLI. Tests run in CI with asciinema recordings for debugging.

**Location**: `tests/Aspire.Cli.EndToEnd.Tests/`

**Supported Platforms**: Linux only. Hex1b requires a Linux terminal environment. Tests are configured to skip on Windows and macOS in CI.

## Key Components

### Core Classes

- **`Hex1bTerminal`**: The main terminal class from the Hex1b library for terminal automation
- **`Hex1bTerminalAutomator`**: Async/await API for driving a `Hex1bTerminal` — the preferred approach for new tests
- **`Hex1bAutomatorTestHelpers`** (shared helpers): Async extension methods on `Hex1bTerminalAutomator` (`WaitForSuccessPromptAsync`, `AspireNewAsync`, etc.)
- **`CliE2EAutomatorHelpers`** (`Helpers/CliE2EAutomatorHelpers.cs`): CLI-specific async extension methods on `Hex1bTerminalAutomator` (`PrepareDockerEnvironmentAsync`, `InstallAspireCliInDockerAsync`, etc.)
- **`CellPatternSearcher`**: Pattern matching for terminal cell content
- **`SequenceCounter`** (`Helpers/SequenceCounter.cs`): Tracks command execution count for deterministic prompt detection
- **`CliE2ETestHelpers`** (`Helpers/CliE2ETestHelpers.cs`): Environment variable helpers and terminal factory methods
- **`TemporaryWorkspace`**: Creates isolated temporary directories for test execution
- **`Hex1bTerminalInputSequenceBuilder`** *(legacy)*: Fluent builder API for building sequences of terminal input/output operations. Prefer `Hex1bTerminalAutomator` for new tests.

### Test Architecture

Each test:
1. Creates a `TemporaryWorkspace` for isolation
2. Builds a `Hex1bTerminal` with headless mode and asciinema recording
3. Creates a `Hex1bTerminalAutomator` wrapping the terminal
4. Drives the terminal with async/await calls and awaits completion

## Test Structure

```csharp
public sealed class SmokeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task MyCliTest()
    {
        var workspace = TemporaryWorkspace.Create(output);
        var installMode = CliE2ETestHelpers.DetectDockerInstallMode();

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal();
        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliInDockerAsync(installMode, counter);

        await auto.TypeAsync("aspire --version");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("exit");
        await auto.EnterAsync();
        await pendingRun;
    }
}
```

## SequenceCounter and Prompt Detection

The `SequenceCounter` class tracks the number of shell commands executed. This enables deterministic waiting for command completion via a custom shell prompt.

### How It Works

1. `PrepareDockerEnvironmentAsync()` configures the shell with a custom prompt: `[N OK] $ ` or `[N ERR:code] $ `
2. Each command increments the counter
3. `WaitForSuccessPromptAsync(counter)` waits for a prompt showing the current count with `OK`

```csharp
var counter = new SequenceCounter();
var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

await auto.PrepareDockerEnvironmentAsync(counter, workspace);  // Sets up prompt, counter starts at 1

await auto.TypeAsync("echo hello");
await auto.EnterAsync();
await auto.WaitForSuccessPromptAsync(counter);  // Waits for "[1 OK] $ ", then increments to 2

await auto.TypeAsync("ls -la");
await auto.EnterAsync();
await auto.WaitForSuccessPromptAsync(counter);  // Waits for "[2 OK] $ ", then increments to 3

await auto.TypeAsync("exit");
await auto.EnterAsync();
```

This approach is more reliable than arbitrary timeouts because it deterministically waits for each command to complete.

## Pattern Searching with CellPatternSearcher

Use `CellPatternSearcher` to find text patterns in terminal output:

```csharp
// Simple text search (literal string matching - PREFERRED)
var waitingForPrompt = new CellPatternSearcher()
    .Find("Enter the project name");

// Literal string with special characters (use Find, not FindPattern!)
var waitingForTemplate = new CellPatternSearcher()
    .Find("> Starter App (FastAPI/React)");  // Parentheses and slashes are literal

// Regex pattern (only when you need wildcards/regex features)
var waitingForAnyStarter = new CellPatternSearcher()
    .FindPattern("> Starter App.*");  // .* matches anything

// Chained patterns (find "b", then scan right until "$", then right of " ")
var waitingForShell = new CellPatternSearcher()
    .Find("b").RightUntil("$").Right(' ').Right(' ');

// Use in WaitUntilAsync
await auto.WaitUntilAsync(
    snapshot => waitingForPrompt.Search(snapshot).Count > 0,
    TimeSpan.FromSeconds(30),
    description: "waiting for prompt");
```

### Find vs FindPattern

- **`Find(string)`**: Literal string matching. Use this for most cases.
- **`FindPattern(string)`**: Regex pattern matching. Use only when you need regex features like wildcards.

**Important**: If your search string contains regex special characters like `(`, `)`, `/`, `.`, `*`, `+`, `?`, `[`, `]`, `{`, `}`, `^`, `$`, `|`, or `\`, use `Find()` instead of `FindPattern()` to avoid regex interpretation.

## Extension Methods

### Hex1bAutomatorTestHelpers Extensions (Shared — Automator API)

| Method | Description |
|--------|-------------|
| `WaitForSuccessPromptAsync(counter, timeout?)` | Waits for `[N OK] $ ` prompt and increments counter |
| `WaitForAnyPromptAsync(counter, timeout?)` | Waits for any prompt (`OK` or `ERR`) and increments counter |
| `WaitForErrorPromptAsync(counter, timeout?)` | Waits for `[N ERR:code] $ ` prompt and increments counter |
| `WaitForSuccessPromptFailFastAsync(counter, timeout?)` | Waits for success prompt, fails immediately if error prompt appears |
| `DeclineAgentInitPromptAsync()` | Declines the `aspire agent init` prompt if it appears |
| `AspireNewAsync(projectName, counter, template?, useRedisCache?)` | Runs `aspire new` interactively, handling template selection, project name, output path, URLs, Redis, and test project prompts |

See [AspireNew Helper](#aspirenew-helper) below for detailed usage.

### CliE2EAutomatorHelpers Extensions on Hex1bTerminalAutomator

| Method | Description |
|--------|-------------|
| `PrepareDockerEnvironmentAsync(counter, workspace)` | Sets up Docker container environment with custom prompt and command tracking |
| `InstallAspireCliInDockerAsync(installMode, counter)` | Installs the Aspire CLI inside the Docker container |
| `ClearScreenAsync(counter)` | Clears the terminal screen and waits for prompt |

### SequenceCounterExtensions

| Method | Description |
|--------|-------------|
| `IncrementSequence(counter)` | Manually increments the counter |

### Legacy Builder Extensions

The following extensions on `Hex1bTerminalInputSequenceBuilder` are still available but should not be used in new tests:

| Method | Description |
|--------|-------------|
| `WaitForSuccessPrompt(counter, timeout?)` | *(legacy)* Waits for `[N OK] $ ` prompt and increments counter |
| `PrepareEnvironment(workspace, counter)` | *(legacy)* Sets up custom prompt with command tracking |
| `InstallAspireCliFromPullRequest(prNumber, counter)` | *(legacy)* Downloads and installs CLI from PR artifacts |
| `SourceAspireCliEnvironment(counter)` | *(legacy)* Adds `~/.aspire/bin` to PATH |

## DO: Use CellPatternSearcher for Output Detection

Wait for specific output patterns rather than arbitrary delays:

```csharp
var waitingForMessage = new CellPatternSearcher()
    .Find("Project created successfully.");

await auto.TypeAsync("aspire new");
await auto.EnterAsync();
await auto.WaitUntilAsync(
    s => waitingForMessage.Search(s).Count > 0,
    TimeSpan.FromMinutes(2),
    description: "waiting for project created message");
```

## DO: Use WaitForSuccessPromptAsync After Commands

After running shell commands, use `WaitForSuccessPromptAsync()` to wait for the command to complete:

```csharp
await auto.TypeAsync("dotnet build");
await auto.EnterAsync();
await auto.WaitForSuccessPromptAsync(counter);  // Waits for prompt, verifies success

await auto.TypeAsync("dotnet run");
await auto.EnterAsync();
await auto.WaitForSuccessPromptAsync(counter);
```

## AspireNew Helper

The `AspireNew` extension method centralizes the multi-step `aspire new` interactive flow. Use it instead of manually building the prompt sequence.

### AspireTemplate Enum

| Value | Template | Arrow Keys |
|-------|----------|------------|
| `Starter` (default) | Starter App (Blazor) | None (first option) |
| `JsReact` | Starter App (ASP.NET Core/React) | Down ×1 |
| `PythonReact` | Starter App (FastAPI/React) | Down ×2 |
| `ExpressReact` | Starter App (Express/React) | Down ×3 |
| `EmptyAppHost` | Empty AppHost | Down ×4 |

### Parameters

| Parameter | Default | Description |
|-----------|---------|-------------|
| `projectName` | (required) | Project name typed at the prompt |
| `counter` | (required) | `SequenceCounter` for prompt tracking |
| `template` | `AspireTemplate.Starter` | Which template to select |
| `useRedisCache` | `true` | Accept Redis (Enter) or decline (Down+Enter). Only applies to Starter, JsReact, PythonReact. |

### Usage Examples

```csharp
// Starter template with defaults (Redis=Yes, TestProject=No)
await auto.AspireNewAsync("MyProject", counter);

// Starter template, no Redis
await auto.AspireNewAsync("MyProject", counter, useRedisCache: false);

// JsReact template, no Redis
await auto.AspireNewAsync("MyProject", counter, template: AspireTemplate.JsReact, useRedisCache: false);

// PythonReact template
await auto.AspireNewAsync("MyProject", counter,
    template: AspireTemplate.PythonReact,
    useRedisCache: false);

// Empty app host
await auto.AspireNewAsync("MyProject", counter, template: AspireTemplate.EmptyAppHost);
```

## DO: Handle Interactive Prompts

For `aspire new`, use the `AspireNewAsync` helper instead of manually building the prompt sequence:

```csharp
// DO: Use the helper
await auto.AspireNewAsync("MyProject", counter);

// DON'T: Manually build the sequence (this is what AspireNewAsync does internally)
var waitingForTemplatePrompt = new CellPatternSearcher()
    .FindPattern("> Starter App");
var waitingForProjectNamePrompt = new CellPatternSearcher()
    .Find("Enter the project name");
await auto.TypeAsync("aspire new");
await auto.EnterAsync();
await auto.WaitUntilAsync(
    s => waitingForTemplatePrompt.Search(s).Count > 0,
    TimeSpan.FromSeconds(30),
    description: "waiting for template prompt");
await auto.EnterAsync();
await auto.WaitUntilAsync(
    s => waitingForProjectNamePrompt.Search(s).Count > 0,
    TimeSpan.FromSeconds(10),
    description: "waiting for project name prompt");
await auto.TypeAsync("MyProject");
await auto.EnterAsync();
```

For other interactive CLI commands, wait for each prompt before responding:

```csharp
var waitingForPrompt = new CellPatternSearcher()
    .Find("Enter your choice");

await auto.TypeAsync("aspire some-command");
await auto.EnterAsync();
await auto.WaitUntilAsync(
    s => waitingForPrompt.Search(s).Count > 0,
    TimeSpan.FromSeconds(30),
    description: "waiting for choice prompt");
await auto.EnterAsync();
```

## DO: Use Ctrl+C to Stop Long-Running Processes

For processes like `aspire run` that don't exit on their own:

```csharp
using Hex1b.Input;

await auto.TypeAsync("aspire run");
await auto.EnterAsync();
await auto.WaitUntilAsync(
    s => waitForCtrlCMessage.Search(s).Count > 0,
    TimeSpan.FromSeconds(30),
    description: "waiting for Ctrl+C message");
await auto.Ctrl().KeyAsync(Hex1bKey.C);  // Send Ctrl+C
await auto.WaitForSuccessPromptAsync(counter);
```

## DO: Check IsRunningInCI for CI-Only Operations

Some operations only apply in CI (like installing CLI from PR artifacts):

```csharp
var installMode = CliE2ETestHelpers.DetectDockerInstallMode();

await auto.PrepareDockerEnvironmentAsync(counter, workspace);
await auto.InstallAspireCliInDockerAsync(installMode, counter);

// Continue with test commands...
```

## DO: Get Environment Variables Using Helpers

Use `CliE2ETestHelpers` for CI environment variables:

```csharp
var prNumber = CliE2ETestHelpers.GetRequiredPrNumber();   // GITHUB_PR_NUMBER (0 when local)
var commitSha = CliE2ETestHelpers.GetRequiredCommitSha(); // GITHUB_PR_HEAD_SHA ("local0000" when local)
var isCI = CliE2ETestHelpers.IsRunningInCI;               // true when both env vars set
```

## DO: Always Include `description:` on WaitUntilAsync

Every `WaitUntilAsync` call requires a named `description:` parameter. This description appears in logs and asciinema recordings to make debugging easier when a wait times out.

```csharp
// DON'T: Missing description
await auto.WaitUntilAsync(
    s => pattern.Search(s).Count > 0,
    TimeSpan.FromSeconds(30));

// DO: Include a meaningful description
await auto.WaitUntilAsync(
    s => pattern.Search(s).Count > 0,
    TimeSpan.FromSeconds(30),
    description: "waiting for build output");
```

## DO: Inline Code Where `ExecuteCallback` Was Used

The old builder API used `ExecuteCallback()` to run synchronous operations mid-sequence. With the automator API, simply inline the code directly — no special wrapper is needed.

```csharp
// Old builder API (DON'T use in new tests)
sequenceBuilder
    .ExecuteCallback(() => File.WriteAllText(configPath, newConfig))
    .Type("aspire run")
    .Enter();

// Automator API (DO)
File.WriteAllText(configPath, newConfig);
await auto.TypeAsync("aspire run");
await auto.EnterAsync();
```

## DON'T: Use Hard-coded Delays

Use `WaitUntilAsync()` with specific output patterns instead of arbitrary delays:

```csharp
// DON'T: Arbitrary delays
await Task.Delay(TimeSpan.FromSeconds(30));

// DO: Wait for specific output
await auto.WaitUntilAsync(
    snapshot => pattern.Search(snapshot).Count > 0,
    TimeSpan.FromSeconds(30),
    description: "waiting for expected output");
```

## DON'T: Hard-code Prompt Sequence Numbers

Don't hard-code the sequence numbers in `WaitForSuccessPromptAsync` calls. Use the counter:

```csharp
// DON'T: Hard-coded sequence numbers
await auto.WaitUntilAsync(
    s => s.GetScreenText().Contains("[3 OK] $ "),
    timeout,
    description: "waiting for prompt");

// DO: Use the counter
await auto.WaitForSuccessPromptAsync(counter);
```

The counter automatically tracks which command you're waiting for, even if command sequences change.

## Writing New Tests with Hex1b MCP Server

When writing new CLI E2E tests, use the **Hex1b MCP server** to interactively explore what terminal output to expect. The MCP server provides tools to start terminal sessions, send commands, and capture screenshots—helping you discover the exact strings and prompts to use in `CellPatternSearcher`.

### Workflow for Discovering Patterns

1. **Start a bash terminal session** using the MCP server's terminal creation tools
2. **Send commands** (like `aspire new` or `aspire run`) and observe the output
3. **Capture terminal screenshots** (SVG or text) to see exact formatting
4. **Use captured text** to build your `CellPatternSearcher` patterns

### Example: Finding Prompt Text for `aspire new`

Ask the MCP server to:
1. Start a new bash terminal
2. Run `aspire new` interactively
3. Capture the terminal text at each prompt

This reveals the exact strings like:
- `"> Starter App"` for template selection
- `"Enter the project name"` for name input
- `"Press Ctrl+C to stop..."` for run completion

### Benefits

- **See real output**: No guessing what text appears in the terminal
- **Exact formatting**: Capture shows spacing, ANSI codes stripped, actual cell content
- **Interactive exploration**: Try different inputs and see responses before writing test code
- **Debug patterns**: If a `CellPatternSearcher` isn't matching, capture current terminal state to compare

### Tips

- Use `Capture Terminal Text` to get plain text for pattern matching
- Use `Capture Terminal Screenshot` (SVG) for visual debugging
- The `Wait for Terminal Text` tool works similarly to `WaitUntil` in tests
- Terminal sessions persist, so you can step through multi-command sequences

## Adding New Extension Methods

When adding new CLI operations as extension methods, define them on `Hex1bTerminalAutomator`:

```csharp
internal static async Task MyNewOperationAsync(
    this Hex1bTerminalAutomator auto,
    string arg,
    SequenceCounter counter,
    TimeSpan? timeout = null)
{
    var expectedOutput = new CellPatternSearcher()
        .Find("Expected output");

    await auto.TypeAsync($"aspire my-command {arg}");
    await auto.EnterAsync();
    await auto.WaitUntilAsync(
        snapshot => expectedOutput.Search(snapshot).Count > 0,
        timeout ?? TimeSpan.FromSeconds(30),
        description: "waiting for expected output from my-command");
    await auto.WaitForSuccessPromptAsync(counter);
}
```

Key points:
1. Define as async extension method on `Hex1bTerminalAutomator`
2. Accept `SequenceCounter` parameter for prompt tracking
3. Use `CellPatternSearcher` for output detection
4. Always include `description:` on `WaitUntilAsync` calls
5. Call `WaitForSuccessPromptAsync(counter)` after command completion
6. Return `Task` (no fluent chaining needed with async/await)

## CI Configuration

Environment variables set in CI:
- `GITHUB_PR_NUMBER`: PR number for downloading CLI artifacts
- `GITHUB_PR_HEAD_SHA`: PR head commit SHA for version verification (not the merge commit)
- `GH_TOKEN`: GitHub token for API access
- `GITHUB_WORKSPACE`: Workspace root for artifact paths

Each test class runs as a separate CI job via the unified `TestEnumerationRunsheetBuilder` infrastructure (using `SplitTestsOnCI=true`) for parallel execution.

## CI Troubleshooting

When CLI E2E tests fail in CI, follow these steps to diagnose the issue:

### Quick Start: Download and Play Recordings

The fastest way to debug a CLI E2E test failure is to download and play the asciinema recording.

**Using the helper scripts (recommended):**

```bash
# Linux/macOS - Download and play recording from latest CI run on current branch
./eng/scripts/get-cli-e2e-recording.sh -p

# List available test recordings
./eng/scripts/get-cli-e2e-recording.sh -l

# Download specific test
./eng/scripts/get-cli-e2e-recording.sh -t SmokeTests -p

# Download from specific run
./eng/scripts/get-cli-e2e-recording.sh -r 20944531393 -p
```

```powershell
# Windows PowerShell
.\eng\scripts\get-cli-e2e-recording.ps1 -Play

# List available recordings
.\eng\scripts\get-cli-e2e-recording.ps1 -List

# Download specific test
.\eng\scripts\get-cli-e2e-recording.ps1 -TestName SmokeTests -Play

# Download from specific run
.\eng\scripts\get-cli-e2e-recording.ps1 -RunId 20944531393 -Play
```

**Manual download steps:**

### Step 1: Find the CI Run

```bash
# List recent CI runs for your branch
gh run list --branch $(git branch --show-current) --workflow CI --limit 5

# Get the run ID from the output or use:
RUN_ID=$(gh run list --branch $(git branch --show-current) --workflow CI --limit 1 --json databaseId --jq '.[0].databaseId')
echo "Run ID: $RUN_ID"
echo "URL: https://github.com/microsoft/aspire/actions/runs/$RUN_ID"
```

### Step 2: Find CLI E2E Test Artifacts

Job names follow the pattern: `Tests / Cli E2E Linux (<TestClass>) / <TestClass> (ubuntu-latest)`

Artifact names follow the pattern: `logs-<TestClass>-ubuntu-latest`

```bash
# Check if CLI E2E tests ran and their status
gh run view $RUN_ID --json jobs --jq '.jobs[] | select(.name | test("Cli E2E")) | {name, conclusion}'

# List available CLI E2E artifacts
gh api --paginate "repos/microsoft/aspire/actions/runs/$RUN_ID/artifacts" \
  --jq '.artifacts[].name' | grep -i "smoke"
```

### Step 3: Download and Play Recording

```bash
# Download the artifact
mkdir -p /tmp/cli-e2e-debug
gh run download $RUN_ID -n logs-SmokeTests-ubuntu-latest -D /tmp/cli-e2e-debug

# Find the recording
find /tmp/cli-e2e-debug -name "*.cast"

# Play it (requires asciinema: pip install asciinema)
asciinema play /tmp/cli-e2e-debug/testresults/recordings/CreateAndRunAspireStarterProject.cast

# Or view raw content for AI analysis
head -100 /tmp/cli-e2e-debug/testresults/recordings/CreateAndRunAspireStarterProject.cast
```

### Artifact Contents

Downloaded artifacts contain:

```
testresults/
├── <TestClass>_net10.0_*.trx          # Test results XML
├── Aspire.Cli.EndToEnd.Tests_*.log     # Console output log
├── *.crash.dmp                        # Crash dump (if test crashed)
├── test.binlog                        # MSBuild binary log
└── recordings/
    ├── CreateAndRunAspireStarterProject.cast   # Asciinema recording
    └── ...
```

### One-Liner: Download Latest Recording

```bash
# Download and play the latest CLI E2E recording from current branch
RUN_ID=$(gh run list --branch $(git branch --show-current) --workflow CI --limit 1 --json databaseId --jq '.[0].databaseId') && \
  rm -rf /tmp/cli-e2e-debug && mkdir -p /tmp/cli-e2e-debug && \
  gh run download $RUN_ID -n logs-SmokeTests-ubuntu-latest -D /tmp/cli-e2e-debug && \
  CAST=$(find /tmp/cli-e2e-debug -name "*.cast" | head -1) && \
  echo "Recording: $CAST" && \
  asciinema play "$CAST"
```

### Common Issues and Solutions

| Symptom | Likely Cause | Solution |
|---------|--------------|----------|
| Timeout waiting for prompt | Command failed or hung | Check recording to see terminal output at timeout |
| `[N ERR:code] $ ` in prompt | Previous command exited with non-zero | Check recording to see which command failed |
| Pattern not found | Output format changed | Update `CellPatternSearcher` patterns |
| Pattern not found but text is visible | Using `FindPattern` with regex special chars | Use `Find()` instead of `FindPattern()` for literal strings containing `(`, `)`, `/`, etc. |
| Test hangs indefinitely | Waiting for wrong prompt number | Verify `SequenceCounter` usage matches commands |
| Timeout waiting for dashboard URL | Project failed to build/run | Check recording for build errors |
