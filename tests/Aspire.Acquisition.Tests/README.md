# Aspire CLI Scripts Tests

Functional tests for the CLI acquisition scripts in `eng/scripts/`.

## Target Scripts

| Script | Type | Dry-Run |
|--------|------|---------|
| `get-aspire-cli.sh` | Bash release | `--dry-run` |
| `get-aspire-cli.ps1` | PowerShell release | `-WhatIf` |
| `get-aspire-cli-pr.sh` | Bash PR | `--dry-run` |
| `get-aspire-cli-pr.ps1` | PowerShell PR | `-WhatIf` |

## Safety

All tests run in isolated temp directories. No user directories, shell profiles, or PATH variables are modified. Tests use `--dry-run` / `-WhatIf` to validate parameter parsing without performing downloads.

PR script tests use a mock `gh` CLI that returns canned JSON responses, avoiding any need for GitHub authentication in unit tests.

## Running Tests

```bash
# Unit tests only (default - excludes integration tests)
dotnet test tests/Aspire.Acquisition.Tests -- --filter-not-trait "Category=integration" --filter-not-trait "quarantined=true" --filter-not-trait "outerloop=true"

# Integration tests (requires GH_TOKEN, on-demand only)
GH_TOKEN=<token> dotnet test tests/Aspire.Acquisition.Tests -- --filter-trait "Category=integration"
```

## Platform Behavior

- **Linux/macOS**: All tests run (bash + PowerShell if pwsh is available)
- **Windows**: Bash tests skipped; PowerShell tests run if pwsh is available

## Integration Tests

Integration tests (in `PRScriptIntegrationTests.cs`) query real GitHub PRs and are:

- Excluded from default CI runs via `[OuterloopTest]` and `Category=integration` trait filters
- Require `GH_TOKEN` environment variable
- Skip gracefully if no suitable PR is found
