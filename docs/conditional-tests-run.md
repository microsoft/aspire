# Conditional Test Runs

> **Status note:** This document primarily describes the current category-based and `sourceToTestMappings`-based implementation shape.
> The planned design direction is a **unified rules config** plus **additive `dotnet-affected` resolver behavior**.
> For that target design, see `docs/ci/conditional-test-selection-unified-rules.md`.
> For design and planning discussions, treat the unified-rules document as the canonical target-design reference.

## Overview

Conditional test runs is a CI optimization feature that selectively runs tests based on which files have changed in a pull request. Instead of running all tests for every change, the current implementation uses glob pattern matching combined with `dotnet-affected` for transitive dependency analysis and convention-based source-to-test mappings to determine exactly which test projects are affected, reducing CI time and resource usage.

## Motivation

Running the full test suite for every pull request is expensive:

- **CI Efficiency**: Integration tests run on 3 platforms with ~20 projects each. Running all tests can take significant time and compute resources.
- **Faster Feedback**: Developers get quicker feedback when only relevant tests run.
- **Resource Optimization**: Reduces GitHub Actions minutes and runner usage.

## How It Works

The system combines glob pattern matching with `dotnet-affected` for transitive dependencies:

1. **Filter out ignored files** (docs, workflows, etc.)
2. **Check for critical infrastructure changes** — files matching `triggerAllPaths` run everything
3. **Match files to categories** via `triggerPaths` patterns — sets `run_<category>` flags
4. **Apply source-to-test mappings** — resolve files to test project directories via `{name}` capture patterns
5. **Run `dotnet-affected`** to find all projects affected by the changes when source-to-test mappings do not already fully account for the active files
6. **Check for unmatched files** — conservative fallback runs everything if any file is unaccounted for
7. **Filter test projects** — identify test projects using glob patterns from `testProjectPatterns`
8. **Combine test projects** from dotnet-affected + source-to-test mappings
9. **Match test projects to categories** — categories can be triggered by both source and test paths
10. **Build final result** — `run_integrations` is `true` if the category is triggered by paths OR if any test projects were discovered

The key insight in the current implementation is that source-to-test mappings use naming conventions (e.g., `src/Components/Aspire.Redis/**` → `tests/Aspire.Redis.Tests/`) to discover test projects, while `dotnet-affected` provides MSBuild's transitive dependency graph for comprehensive coverage. In the planned unified design, direct rule-based project selection would replace these dedicated mapping concepts, while `dotnet-affected` would remain as additive resolver behavior.

## Architecture

### 1. Test Selector Tool (`tools/TestSelector`)

A C# CLI tool that:
- Reads the configuration file
- Gets changed files from git
- Matches files to categories via `triggerPaths`
- Applies source-to-test mappings via `{name}` capture patterns
- Runs `dotnet-affected` for transitive dependency analysis when mapping-only resolution is not sufficient
- Filters test projects using glob patterns
- Outputs JSON results and GitHub Actions outputs

### 2. Configuration File (`eng/scripts/test-selection-rules.json`)

A JSON file that defines:
- **ignorePaths**: Files that never trigger tests (docs, workflows)
- **triggerAllPaths**: Critical files that trigger ALL tests when changed
- **categories**: Category definitions with `triggerPaths` and optional `excludePaths`
- **sourceToTestMappings**: Pattern-based mappings from source/test files to test project directories
- **testProjectPatterns**: Glob patterns to identify test projects (include/exclude)

### 3. GitHub Actions Integration

The workflow runs the tool and uses the output to conditionally run test jobs.

## Configuration Reference

### `ignorePaths`

Files matching these glob patterns are completely ignored — they don't trigger any tests.

```json
"ignorePaths": [
  ".editorconfig",
  ".gitignore",
  "**/*.md",
  "docs/**",
  "eng/pipelines/**",
  "eng/test-configuration.json",
  "eng/testing/**",
  ".github/workflows/**",
  ".github/actions/**",
  ".github/instructions/**",
  ".github/skills/**",
  "tests/agent-scenarios/**",
  "eng/scripts/test-selection-rules.json",
  "eng/scripts/test-selection-rules.schema.json"
]
```

When **all** changed files in a pull request match `ignorePaths`, the selector produces no active files for the managed test matrix. Whole-workflow skipping is still handled earlier by the lightweight `prepare_for_ci` gate using `eng/testing/github-ci-trigger-patterns.txt`, while the selector outputs are consumed later in `tests.yml` `setup_for_tests`.

### `triggerAllPaths`

Critical files that trigger ALL tests when changed. This is specified at the top level of the config.

```json
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
]
```

### `categories`

Category definitions with trigger paths. Categories serve two purposes:

1. **Standalone job flags** (`polyglot`, `extension`): Skip/run independent CI jobs
2. **Test discovery signals** (`integrations`, `cli_e2e`): Identify which .NET test projects to run

```json
"categories": {
  "cli_e2e": {
    "description": "CLI end-to-end tests",
    "triggerPaths": [
      "src/Aspire.Cli/**",
      "eng/clipack/**",
      "tests/Aspire.Cli.EndToEnd.Tests/**"
    ]
  },

  "extension": {
    "description": "VS Code extension tests",
    "triggerPaths": [
      "extension/**"
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
```

Category properties:

| Property | Type | Description |
|----------|------|-------------|
| `description` | string | Human-readable description |
| `triggerPaths` | string[] | Glob patterns that trigger this category |
| `excludePaths` | string[] | Glob patterns to exclude from matching (optional) |

### `sourceToTestMappings`

Pattern-based mappings that resolve changed files to test project directories. Uses `{name}` as a capture group.

```json
"sourceToTestMappings": [
  {
    "source": "playground/**",
    "test": "tests/Aspire.Playground.Tests/Aspire.Playground.Tests.csproj"
  },
  {
    "source": "src/Aspire.ProjectTemplates/**",
    "test": "tests/Aspire.Templates.Tests/Aspire.Templates.Tests.csproj"
  }
]
```

When a changed file matches a `source` pattern, the captured `{name}` (if present) is substituted into `test` to identify the test project. Test projects discovered this way are added to `affected_test_projects` and contribute to matrix filtering.

### `testProjectPatterns`

Glob patterns to identify which projects are test projects. Used to filter `dotnet-affected` output.

```json
"testProjectPatterns": {
  "include": [
    "tests/**/*.csproj"
  ],
  "exclude": [
    "tests/testproject/**"
  ]
}
```

## Evaluation Algorithm

1. **Filter Ignored Files**: Remove files matching `ignorePaths`. If all files are ignored, no tests run.

2. **Check Critical Files**: If any file matches patterns in `triggerAllPaths`, ALL tests run.

3. **Match Files to Categories**: Apply each category's `triggerPaths` (minus `excludePaths`) to set `run_<category>` flags.

4. **Apply Source-to-Test Mappings**: Resolve changed files to test project directories via `{name}` capture patterns.

5. **Run dotnet-affected**: Find all MSBuild projects affected by the changed files (transitive dependencies) when active files are not already fully covered by source-to-test mappings.

6. **Check Unmatched Files**: If any active file isn't matched by categories, solution scope, or source-to-test mappings, conservatively run all tests.

7. **Filter Test Projects**: Use `testProjectPatterns` to identify test projects from `dotnet-affected` output.

8. **Combine Test Projects**: Merge test projects from dotnet-affected and source-to-test mappings.

9. **Match Test Projects to Categories**: Categories can be triggered by both source paths and resolved test project paths.

10. **Build Final Result**: Set `run_integrations=true` if the integrations category is triggered OR if any test projects were discovered.

## Tool Usage

### CLI Reference

```bash
# Basic usage - compare against origin/main
dotnet run --project tools/TestSelector -- --solution Aspire.slnx --from origin/main

# With verbose output
dotnet run --project tools/TestSelector -- --solution Aspire.slnx --from origin/main --verbose

# Test with explicit changed files
dotnet run --project tools/TestSelector -- --solution Aspire.slnx \
  --changed-files "src/Aspire.Dashboard/App.razor" --verbose

# Output to file
dotnet run --project tools/TestSelector -- --solution Aspire.slnx --from origin/main --output result.json

# GitHub Actions format
dotnet run --project tools/TestSelector -- --solution Aspire.slnx --from origin/main --github-output
```

### Options

| Option | Short | Description |
|--------|-------|-------------|
| `--solution` | `-s` | Path to solution file (required) |
| `--config` | `-c` | Path to config file (optional; category logic skipped if omitted) |
| `--from` | `-f` | Git ref to compare from (required unless `--changed-files` provided) |
| `--to` | `-t` | Git ref to compare to (default: `HEAD`) |
| `--changed-files` | | Comma-separated list of files (bypasses git) |
| `--output` | `-o` | Output file path for JSON result |
| `--github-output` | | Write outputs in GitHub Actions format |
| `--verbose` | `-v` | Enable verbose output |

### Output Format

```json
{
  "runAllTests": false,
  "reason": "selective",
  "categories": {
    "cli_e2e": false,
    "extension": true,
    "polyglot": false,
    "integrations": false
  },
  "affectedTestProjects": [
    "tests/Aspire.Dashboard.Tests/Aspire.Dashboard.Tests.csproj"
  ],
  "integrationsProjects": [
    "tests/Aspire.Dashboard.Tests/Aspire.Dashboard.Tests.csproj"
  ],
  "changedFiles": [
    "tests/Aspire.Dashboard.Tests/Model/ResourceViewModelTests.cs",
    "extension/Extension.proj"
  ],
  "dotnetAffectedProjects": [],
  "ignoredFiles": []
}
```

### GitHub Actions Outputs

| Output | Description |
|--------|-------------|
| `run_all` | `true` if all tests should run |
| `run_integrations` | `true` if category triggered by paths OR test projects discovered |
| `run_cli_e2e` | `true` if CLI E2E test category triggered |
| `run_extension` | `true` if VS Code extension category triggered |
| `run_polyglot` | `true` if polyglot validation category triggered |
| `affected_test_projects` | JSON array of affected test project `.csproj` paths |

## Testing

### Running Tool Tests

```bash
# Run all test selector tests
dotnet test tests/Infrastructure.Tests

# Run specific test categories
dotnet test tests/Infrastructure.Tests -- --filter-class "*.IgnorePathFilterTests"
dotnet test tests/Infrastructure.Tests -- --filter-class "*.EndToEndEvaluationTests"
```

### Test Coverage

The test project provides coverage for:

- **Analyzers**: IgnorePathFilter, CriticalFileDetector, TestProjectFilter, DotNetAffectedRunner, ProjectMappingResolver
- **Models**: TestSelectorConfig, TestSelectionResult (including GitHub output generation)
- **CategoryMapper**: File-to-category matching via triggerPaths/excludePaths
- **Path normalization**: Forward/back slash handling across platforms

## Troubleshooting

### All Tests Running Unexpectedly

**Cause**: A changed file matches `triggerAllPaths`, or a file is unmatched by any rule (conservative fallback).

**Solution**: Run with `--verbose` to see which file triggered it. If it's an unmatched file, add it to `ignorePaths` or ensure it's covered by a category or source-to-test mapping.

### Tests Not Running When Expected

**Cause**: Missing project reference, file is being ignored, or missing source-to-test mapping.

**Solution**:
1. Check if file matches `ignorePaths`
2. Verify project references exist in the `.csproj` files
3. Check if a `sourceToTestMappings` entry is needed
4. Run `dotnet affected` manually to debug

### Non-.NET Files Not Triggering Tests

**Cause**: Files like extension code can't be analyzed by `dotnet-affected`.

**Solution**: Add a category with `triggerPaths` matching the file pattern.

## Dependencies

- .NET 10.0 SDK
- `dotnet-affected` global tool: `dotnet tool install --global dotnet-affected --prerelease`
- Git
