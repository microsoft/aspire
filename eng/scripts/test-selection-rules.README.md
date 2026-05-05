# Test Selection Rules Configuration

The `test-selection-rules.json` file configures which tests run based on changed files in a pull request. This enables faster CI by only running relevant tests.

## Configuration Reference

### Ignore Paths

Files matching `ignorePaths` patterns are completely ignored—they don't trigger any tests and don't cause fallback to running all tests:

```json
{
  "ignorePaths": [
    "*.md",
    "docs/**",
    ".github/workflows/**"
  ]
}
```

### Trigger All Paths (Critical Files)

Use `triggerAllPaths` at the top level for critical files where any change should run all tests:

```json
{
  "triggerAllPaths": [
    "global.json",
    "Directory.Build.props",
    "Directory.Packages.props"
  ]
}
```

### Categories

Categories define groups of tests that run together. Each category has a name (used as `run_{name}` in workflow outputs) and configuration options.

#### Trigger a Category Based on Changed Files

Use `triggerPaths` to specify glob patterns that trigger the category:

```json
{
  "categories": {
    "cli_e2e": {
      "description": "CLI end-to-end tests",
      "triggerPaths": [
        "src/Aspire.Cli/**",
        "tests/Aspire.Cli.EndToEnd.Tests/**"
      ]
    }
  }
}
```

#### Exclude Paths from Triggering

Use `excludePaths` to prevent certain files from triggering a category even if they match `triggerPaths`:

```json
{
  "categories": {
    "integrations": {
      "description": "Integration tests",
      "triggerPaths": [
        "src/Aspire.*/**"
      ],
      "excludePaths": [
        "src/Aspire.Cli/**",
        "src/Aspire.ProjectTemplates/**"
      ]
    }
  }
}
```

### Source-to-Test Mappings

Source-to-test mappings automatically discover which test projects to run based on source file changes:

```json
{
  "sourceToTestMappings": [
    {
      "source": "src/Components/{name}/**",
      "test": "tests/{name}.Tests/"
    },
    {
      "source": "src/Aspire.Hosting.{name}/**",
      "test": "tests/Aspire.Hosting.{name}.Tests/"
    }
  ]
}
```

The `{name}` placeholder captures part of the path and substitutes it into the test pattern.

### Test Project Patterns

Use `testProjectPatterns` to configure how test projects are identified from dotnet-affected output:

```json
{
  "testProjectPatterns": {
    "include": [
      "tests/**/*.csproj"
    ],
    "exclude": [
      "tests/testproject/**"
    ]
  }
}
```

## Workflow Integration

The lightweight workflow skip remains in `prepare_for_ci` via `.github/actions/check-changed-files` and `eng/testing/github-ci-trigger-patterns.txt`.

The test selector outputs are produced in `.github/workflows/tests.yml` `setup_for_tests` and used by downstream jobs:

- `run_all` - Set to `true` when critical paths change or on non-PR events
- `run_integrations`, `run_cli_e2e`, `run_extension`, `run_polyglot` - Category flags
- `affected_test_projects` - JSON array of affected `.csproj` paths for matrix filtering

Standalone jobs (`polyglot_validation`, `extension_tests_win`) use category flags as `if` guards.
Matrix-based test jobs use `affected_test_projects` for per-project filtering (currently in audit mode).

Example workflow usage:

```yaml
jobs:
  setup_for_tests:
    outputs:
      run_all: ${{ steps.detect.outputs.run_all }}
      run_cli_e2e: ${{ steps.detect.outputs.run_cli_e2e }}
      run_polyglot: ${{ steps.detect.outputs.run_polyglot }}
      affected_test_projects: ${{ steps.detect.outputs.affected_test_projects }}

  my_standalone_job:
    needs: setup_for_tests
    if: ${{ needs.setup_for_tests.outputs.run_all == 'true' || needs.setup_for_tests.outputs.run_my_category == 'true' }}
```

## Adding a New Category

1. Add the category to `test-selection-rules.json`:

   ```json
   {
     "categories": {
       "my_category": {
         "description": "Description of tests",
         "triggerPaths": ["path/to/trigger/**"]
       }
     }
   }
   ```

2. Add the output to `setup_for_tests` in `tests.yml`:

   ```yaml
   outputs:
     run_my_category: ${{ steps.detect.outputs.run_my_category }}
   ```

3. Add `run_my_category=true` to the non-PR event case.

4. Add the category to the "Show test selection results" step.

5. Create or update jobs to use the new condition:

   ```yaml
   if: ${{ needs.setup_for_tests.outputs.run_all == 'true' || needs.setup_for_tests.outputs.run_my_category == 'true' }}
   ```

## Testing Changes

Run the test selector locally to verify your configuration:

```bash
dotnet run --project tools/TestSelector/TestSelector.csproj -- \
  --solution Aspire.slnx \
  --config eng/scripts/test-selection-rules.json \
  --from <base-commit> \
  --to <head-commit> \
  --verbose
```
