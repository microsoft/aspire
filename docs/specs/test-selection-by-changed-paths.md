# Test Selection by Changed Paths

> **Note:** This is the original design spec. The implementation has evolved:
> - Categories `templates`, `endtoend`, and `playground` have been removed and replaced by `sourceToTestMappings`
> - `integrations_projects` output has been renamed to `affected_test_projects` with `.csproj` paths
> - A matrix filtering step (`filter-test-matrix-by-scope.ps1`) was added for per-project test filtering
> - See `docs/conditional-tests-run.md` for the current documentation.
> - The planned next design direction is `docs/ci/conditional-test-selection-unified-rules.md`, which keeps `dotnet-affected` as additive resolver behavior but replaces categories/source-to-test mappings as the target config model.

## Overview

This document describes the MSBuild-based test selection system for determining which test projects to run based on git changes. The system uses `dotnet-affected` for dependency analysis combined with category-based rules and project mappings for edge cases.

## Problem Statement

Running all tests on every change leads to:
- Longer CI times
- Unnecessary resource consumption
- Slower feedback loops for developers

The solution uses MSBuild dependency analysis to accurately determine which tests are affected by code changes.

## Design Goals

1. **Accurate**: Uses MSBuild project graph for precise dependency tracking
2. **Portable**: Works for both GitHub Actions and Azure DevOps
3. **Configurable**: Non-.NET rules defined in a separate config file
4. **Safe**: Falls back to running all tests when uncertain
5. **Debuggable**: Clear JSON output explaining decisions

## Architecture

```text
Changed Files
     │
     ▼
┌────────────────────────────────┐
│ Step 1: Filter Ignored Files   │  docs/**, *.md, .github/**
│ (never trigger tests)          │  → Remove from consideration
└────────────────────────────────┘
     │ (active files remain)
     ▼
┌────────────────────────────────┐
│ Step 2: Check Critical Files   │  global.json, Directory.Build.props,
│ (triggerAllPaths)              │  src/Aspire.Hosting/**, etc.
│                                │  → Run everything, stop here
└────────────────────────────────┘
     │ (not critical)
     ▼
┌────────────────────────────────┐
│ Step 3a: Match Files to        │  Category triggerPaths matching
│ Categories                     │  → Sets run_<category>=true/false
└────────────────────────────────┘
     │
     ▼
┌────────────────────────────────┐
│ Step 3b: Run dotnet-affected   │  MSBuild dependency graph analysis
│                                │  → Finds affected projects
└────────────────────────────────┘
     │
     ▼
┌────────────────────────────────┐
│ Step 3c: Apply Source-to-Test  │  tests/{name}.Tests/** → test project
│ Mappings                       │  → Discovers test projects by pattern
└────────────────────────────────┘
     │
     ▼
┌────────────────────────────────┐
│ Step 4: Check Unmatched Files  │  Any file not matched by categories,
│ (conservative fallback)        │  solution scope, or mappings
│                                │  → Run everything
└────────────────────────────────┘
     │
     ▼
┌────────────────────────────────┐
│ Step 5: Filter + Combine       │  Filter dotnet-affected to test projects
│ Test Projects                  │  Merge with source-to-test mapping results
└────────────────────────────────┘
     │
     ▼
┌────────────────────────────────┐
│ Step 6: Match Test Projects    │  Re-run category matching on discovered
│ to Categories                  │  test project paths (additive)
└────────────────────────────────┘
     │
     ▼
┌────────────────────────────────┐
│ Step 7: Build Final Result     │  Categories from step 3a + step 6
│                                │  IntegrationsProjects = all test projects
│                                │  run_integrations = category OR projects
└────────────────────────────────┘
     │
     ▼
  OUTPUT: GitHub Actions outputs + JSON
```

## What dotnet-affected Handles

| Scenario | Coverage |
|----------|----------|
| `src/Aspire.Hosting/Foo.cs` changed | ✅ Finds all test projects that depend on Aspire.Hosting |
| `tests/Aspire.TestUtilities/` changed | ✅ Finds all test projects that reference TestUtilities |
| `Directory.Build.props` changed | ✅ Detected via MSBuild.Prediction |
| `Directory.Packages.props` changed | ✅ Explicit CPM support |
| Shared source files (`<Compile Include>`) | ✅ Detected via ProjectGraph |
| Transitive dependencies | ✅ Core feature |

## What Needs Custom Logic

| Scenario | Handling |
|----------|----------|
| `global.json` changed | Top-level `triggerAllPaths` → run all tests |
| `NuGet.config` changed | Top-level `triggerAllPaths` → run all tests |
| `*.sln/*.slnx` changed | Top-level `triggerAllPaths` → run all tests |
| `extension/**` changed | `extension` category triggered |
| Test file changes not caught by dotnet-affected | `sourceToTestMappings` resolves to test project |
| Any `IsPackable=true` project affected | NuGet-dependent tests triggered (planned) |

## Tool: TestSelector

### Location

`tools/TestSelector/`

### CLI Usage

```bash
# Basic usage (compares to origin/main)
dotnet run --project tools/TestSelector -- --solution Aspire.slnx --from origin/main

# With explicit changed files (for testing)
dotnet run --project tools/TestSelector -- --solution Aspire.slnx \
  --changed-files "src/Aspire.Hosting/Foo.cs,tests/Aspire.TestUtilities/Bar.cs"

# Output to file
dotnet run --project tools/TestSelector -- --solution Aspire.slnx \
  --from origin/main \
  --output test-selection.json

# GitHub Actions format (writes to $GITHUB_OUTPUT)
dotnet run --project tools/TestSelector -- --solution Aspire.slnx \
  --from origin/main --github-output

# Verbose mode
dotnet run --project tools/TestSelector -- --solution Aspire.slnx --from origin/main --verbose
```

### Parameters

| Parameter | Short | Description | Default |
|-----------|-------|-------------|---------|
| `--solution` | `-s` | Path to the solution file (required) | — |
| `--config` | `-c` | Path to the config file (optional; category logic skipped if omitted) | — |
| `--from` | `-f` | Git ref to compare from (required unless `--changed-files` provided) | — |
| `--to` | `-t` | Git ref to compare to | `HEAD` |
| `--changed-files` | | Comma-separated changed files (for testing) | |
| `--output` | `-o` | Output file path for JSON | (stdout) |
| `--github-output` | | Write outputs in GitHub Actions format | `false` |
| `--verbose` | `-v` | Enable verbose output | `false` |

## Configuration File

### Location

`eng/scripts/test-selection-rules.json`

### Schema

```json
{
  "$schema": "./test-selection-rules.schema.json",

  "ignorePaths": [
    ".editorconfig",
    ".gitignore",
    "*.md",
    "docs/**",
    "eng/common/**",
    "eng/pipelines/**",
    ".github/workflows/**",
    ".github/actions/**",
    "tests/agent-scenarios/**",
    "eng/scripts/test-selection-rules.json",
    "eng/scripts/test-selection-rules.schema.json",
    "tests/Aspire.Infrastructure.Tests/**",
    "Aspire.slnx",
    "src/Grafana/**",
    "src/Schema/**"
  ],

  "triggerAllPaths": [
    "global.json",
    "Directory.Build.props",
    "Directory.Build.targets",
    "Directory.Packages.props",
    "NuGet.config",
    "tests/Shared/**",
    "*.sln",
    "*.slnx",
    "src/Aspire.Hosting/**"
  ],

  "testProjectPatterns": {
    "include": ["tests/**/*.csproj"],
    "exclude": ["tests/testproject/**"]
  },

  "sourceToTestMappings": [
    {
      "source": "src/Components/{name}/**",
      "test": "tests/{name}.Tests/"
    },
    {
      "source": "src/Aspire.Hosting.{name}/**",
      "test": "tests/Aspire.Hosting.{name}.Tests/"
    },
    {
      "source": "tests/{name}.Tests/**",
      "test": "tests/{name}.Tests/"
    },
    {
      "source": "tools/TestSelector/**",
      "test": "tests/Infrastructure.Tests/"
    },
    {
      "source": "playground/**",
      "test": "tests/Aspire.Playground.Tests/"
    },
    {
      "source": "src/Tools/ConfigurationSchemaGenerator/**",
      "test": "tests/ConfigurationSchemaGenerator.Tests/"
    }
  ],

  "categories": {
    "templates": {
      "description": "Template tests - runs on 3 platforms",
      "triggerPaths": [
        "src/Aspire.ProjectTemplates/**",
        "tests/Aspire.Templates.Tests/**",
        "tests/Shared/**"
      ]
    },

    "cli_e2e": {
      "description": "CLI end-to-end tests",
      "triggerPaths": [
        "src/Aspire.Cli/**",
        "eng/clipack/**",
        "tests/Aspire.Cli.EndToEnd.Tests/**"
      ]
    },

    "endtoend": {
      "description": "General E2E tests",
      "triggerPaths": [
        "tests/Aspire.EndToEnd.Tests/**"
      ]
    },

    "extension": {
      "description": "VS Code extension tests",
      "triggerPaths": [
        "extension/**"
      ]
    },

    "playground": {
      "description": "Playground tests",
      "triggerPaths": [
        "playground/**"
      ]
    },

    "polyglot": {
      "description": "Polyglot SDK validation tests",
      "triggerPaths": [
        ".github/workflows/polyglot-validation/**",
        ".github/workflows/polyglot-validation.yml"
      ]
    },

    "integrations": {
      "description": "Integration tests for components, dashboard, and hosting extensions",
      "triggerPaths": [
        "src/Aspire.Dashboard/**",
        "src/Aspire.Hosting.*/**",
        "src/Aspire.*/**",
        "src/Components/**",
        "src/Shared/**",
        "src/Vendoring/**",
        "tests/Aspire.*.Tests/**"
      ],
      "excludePaths": [
        "src/Aspire.ProjectTemplates/**",
        "src/Aspire.Cli/**",
        "src/Aspire.Hosting/**",
        "tests/Aspire.Templates.Tests/**",
        "tests/Aspire.Cli.EndToEnd.Tests/**",
        "tests/Aspire.EndToEnd.Tests/**"
      ]
    }
  }
}
```

### Configuration Sections

| Section | Description |
|---------|-------------|
| `ignorePaths` | Glob patterns for files that don't trigger any tests |
| `triggerAllPaths` | Glob patterns for critical files that trigger ALL tests when changed |
| `testProjectPatterns` | Include/exclude glob patterns to identify test projects from dotnet-affected output |
| `sourceToTestMappings` | Pattern-based mappings from source/test files to test project directories. Uses `{name}` capture groups with `source` and `test` keys. |
| `categories` | Test category definitions with `triggerPaths` and optional `excludePaths` |

### Category Properties

| Property | Type | Description |
|----------|------|-------------|
| `description` | string | Human-readable description |
| `triggerPaths` | string[] | Glob patterns that trigger this category |
| `excludePaths` | string[] | Glob patterns to exclude from matching (optional) |

## GitHub Actions Output

When run with `--github-output`, the tool writes these outputs:

| Output | Description |
|--------|-------------|
| `run_all` | `true` if all tests should run (critical path, error fallback) |
| `run_<category>` | `true`/`false` for each category defined in config |
| `run_integrations` | `true` if the integrations category is triggered by paths **OR** if any test projects were discovered via dotnet-affected/sourceToTestMappings |
| `integrations_projects` | JSON array of test project paths for matrix builds |

**Important**: `run_integrations` merges two signals:
1. The `integrations` category being triggered by its `triggerPaths`
2. Any test projects being discovered via `dotnet-affected` or `projectMappings`

This means even if no files match `integrations` trigger paths, `run_integrations` will be `true` if test projects were found through other mechanisms (e.g., a test file changed and was resolved via project mappings).

## Output JSON Structure

```json
{
  "runAllTests": false,
  "reason": "selective",

  "categories": {
    "templates": false,
    "cli_e2e": false,
    "endtoend": false,
    "extension": false,
    "playground": false,
    "polyglot": false,
    "integrations": true
  },

  "affectedTestProjects": [
    "tests/Aspire.Dashboard.Tests/"
  ],

  "integrationsProjects": [
    "tests/Aspire.Dashboard.Tests/"
  ],

  "changedFiles": ["src/Aspire.Dashboard/Components/Layout.razor"],
  "ignoredFiles": ["README.md"],
  "dotnetAffectedProjects": [
    "src/Aspire.Dashboard/Aspire.Dashboard.csproj",
    "tests/Aspire.Dashboard.Tests/Aspire.Dashboard.Tests.csproj"
  ]
}
```

## Test Categories

| Category | Description | Trigger Condition |
|----------|-------------|-------------------|
| `templates` | Template tests (3 platforms) | Template/shared paths |
| `cli_e2e` | CLI end-to-end tests | CLI paths |
| `endtoend` | General E2E tests | E2E test paths |
| `extension` | VS Code extension tests | `extension/**` changes |
| `playground` | Playground tests | `playground/**` changes |
| `polyglot` | Polyglot SDK validation | Polyglot workflow paths |
| `integrations` | Integration tests | Path matching OR test projects found via dotnet-affected/sourceToTestMappings |

Critical infrastructure files (`global.json`, `Directory.Build.props`, etc.) are handled by the top-level `triggerAllPaths` — any match runs ALL tests across all categories.

## NuGet-Dependent Tests (Planned)

> **Note**: This feature is planned but not yet implemented. The output model includes a `nugetDependentTests` field, but no analyzer currently populates it.

Tests in these projects require built NuGet packages:
- `tests/Aspire.Templates.Tests/`
- `tests/Aspire.EndToEnd.Tests/`
- `tests/Aspire.Cli.EndToEnd.Tests/`

**Planned logic**: If any affected source project has `IsPackable=true`, all NuGet-dependent tests would be triggered.

## Rule Evaluation Examples

### Example 1: Test file changed (project mappings)

**Changed files:**
- `tests/Aspire.Dashboard.Tests/Model/ResourceViewModelTests.cs`
- `extension/Extension.proj`

**Result:**
- `extension` category triggered by `extension/Extension.proj`
- `Aspire.Dashboard.Tests` resolved via sourceToTestMapping `tests/{name}.Tests/**`
- `run_extension=true`, `run_integrations=true` (due to discovered test project)
- `integrations_projects=["tests/Aspire.Dashboard.Tests/"]`

### Example 2: Critical file changed

**Changed files:**
- `global.json`

**Result:**
```json
{
  "runAllTests": true,
  "reason": "critical_path",
  "triggerFile": "global.json",
  "triggerPattern": "global.json"
}
```
All `run_<category>` outputs set to `true`.

### Example 3: Only docs changed

**Changed files:**
- `docs/getting-started.md`
- `README.md`

**Result:**
```json
{
  "runAllTests": false,
  "reason": "all_ignored"
}
```
All `run_<category>` outputs set to `false`.

### Example 4: Extension files only

**Changed files:**
- `extension/package.json`
- `extension/src/index.ts`

**Result:**
- `run_extension=true`
- All other categories `false`
- `integrations_projects=[]`

### Example 5: Source project changed (dotnet-affected)

**Changed files:**
- `src/Aspire.Dashboard/Components/Layout.razor`

**Result:**
- `dotnet-affected` finds `Aspire.Dashboard.Tests` as affected
- `integrations` category triggered by path matching `src/Aspire.Dashboard/**`
- `run_integrations=true`
- `integrations_projects=["tests/Aspire.Dashboard.Tests/"]`

## CI Integration

### GitHub Actions

The workflow runs the tool in the `setup_for_tests` job inside `tests.yml` after the lightweight `prepare_for_ci` changed-files gate:

```yaml
jobs:
  setup_for_tests:
    runs-on: ubuntu-latest
    outputs:
      run_all: ${{ steps.detect.outputs.run_all }}
      run_extension: ${{ steps.detect.outputs.run_extension }}
      run_integrations: ${{ steps.detect.outputs.run_integrations }}
      # ... other categories
    steps:
      - uses: actions/checkout@v4
        with:
          fetch-depth: 0

      - name: Install dotnet-affected
        if: github.event_name == 'pull_request'
        run: dotnet tool install --global dotnet-affected --prerelease

      - name: Evaluate changed paths
        id: detect
        run: |
          if [ "${{ github.event_name }}" == "pull_request" ]; then
            dotnet run --project tools/TestSelector/TestSelector.csproj -- \
              --solution Aspire.slnx \
              --config eng/scripts/test-selection-rules.json \
              --from "${{ github.event.pull_request.base.sha }}" \
              --to "${{ github.event.pull_request.head.sha }}" \
              --github-output --verbose
          else
            # Non-PR: run everything
            echo "run_all=true" >> $GITHUB_OUTPUT
            echo "run_extension=true" >> $GITHUB_OUTPUT
            # ... all categories true
          fi
```

### Conditional Jobs

```yaml
extension_tests_win:
  needs: setup_for_tests
  if: ${{ needs.setup_for_tests.outputs.run_all == 'true' || needs.setup_for_tests.outputs.run_extension == 'true' }}

integrations_test_lin:
  needs: [setup_for_tests, setup_for_tests_lin]
  if: ${{ needs.setup_for_tests.outputs.run_all == 'true' || needs.setup_for_tests.outputs.run_integrations == 'true' }}
```

### Event Type Behavior

| Event | Behavior |
|-------|----------|
| `pull_request` | Evaluate changed files, skip unaffected categories |
| `push` (to main) | Run all tests |
| `schedule` | Run all tests |
| `workflow_dispatch` | Run all tests |

## Dependencies

The tool requires:
- `dotnet-affected` CLI tool (installed via `dotnet tool install --global dotnet-affected --prerelease`)
- .NET 10.0 SDK

## File Structure

```text
tools/TestSelector/
├── TestSelector.csproj
├── Program.cs                       # CLI entry point + evaluation logic
├── CategoryMapper.cs                # Map files → categories via triggerPaths
├── DiagnosticLogger.cs              # Verbose logging helper
├── Models/
│   ├── TestSelectionResult.cs       # Output model + GitHub output writer
│   └── TestSelectorConfig.cs        # Config model for JSON deserialization
└── Analyzers/
    ├── CriticalFileDetector.cs      # Check triggerAllPaths
    ├── DotNetAffectedResult.cs      # Result model from dotnet-affected
    ├── DotNetAffectedRunner.cs      # Wraps dotnet-affected CLI
    ├── IgnorePathFilter.cs          # Filter ignorePaths
    ├── ProjectMappingResolver.cs    # Resolve sourceToTestMappings patterns
    └── TestProjectFilter.cs         # Filter test projects via testProjectPatterns

eng/scripts/
├── test-selection-rules.json        # Configuration rules
└── test-selection-rules.schema.json # JSON schema for validation
```

## References

- [dotnet-affected](https://github.com/leonardochaia/dotnet-affected) - MSBuild dependency analysis tool
- [MSBuild.Prediction](https://github.com/microsoft/MSBuildPrediction) - Input/output prediction for MSBuild projects
