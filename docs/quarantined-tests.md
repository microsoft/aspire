# Quarantined Tests

## Overview

Quarantined tests are tests that are flaky or known to have issues but are not yet fixed. They are marked with the `[QuarantinedTest]` attribute and are excluded from regular CI runs to prevent false negatives.

## How Quarantined Tests Work

The `QuarantinedTestAttribute` applies the xUnit trait `quarantined=true` to tests. This trait can then be used with test filters to include or exclude these tests.

## Running Tests Without Quarantined Tests

To run tests excluding quarantined tests (this is what CI does):

```bash
dotnet test --filter-not-trait "quarantined=true"
```

Or using the direct test runner:

```bash
dotnet exec YourTestAssembly.dll --filter-not-trait "quarantined=true"
```

## Running Only Quarantined Tests

To run only quarantined tests (useful for debugging):

```bash
dotnet test --filter-trait "quarantined=true"
```

Or using the direct test runner:

```bash
dotnet exec YourTestAssembly.dll --filter-trait "quarantined=true"
```

## Quarantined Test Lifecycle

1. **Mark as Quarantined**: When a test is consistently flaky, mark it with `[QuarantinedTest("reason")]`
2. **Quarantined Execution**: Quarantined tests run automatically in the quarantine CI (every 6 hours)
3. **Fix the Issue**: Investigate and fix the underlying issue causing the flakiness
4. **Remove Quarantine**: Once fixed and stable, remove the `[QuarantinedTest]` attribute

## Example

```csharp
[Fact]
[QuarantinedTest("https://github.com/microsoft/aspire/issues/7920")]
public async Task FlakyTest()
{
    // Test implementation
}
```

## CI Integration

- **Regular CI**: Uses `--filter-not-trait "quarantined=true"` to exclude quarantined tests
- **Quarantine CI on Schedule/Push**: Runs all quarantined tests twice daily (02:00 and 14:00 UTC) to monitor their status
- **Quarantine CI on Pull Requests**: Runs only the first quarantined test project across all OSes (Windows, Linux, macOS) as a sanity check when PRs modify workflow/infrastructure files
- **Results**: Quarantined test failures don't block PR merges but are tracked for fixes

## For Copilot Agent and Test Runners

When running tests in automated environments like Copilot agent, always use the quarantine filter to avoid false negatives:

```bash
# Good - excludes quarantined tests
dotnet test --filter-not-trait "quarantined=true"

# Bad - runs all tests including quarantined ones
dotnet test
```
