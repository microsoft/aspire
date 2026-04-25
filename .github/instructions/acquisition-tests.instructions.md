---
applyTo: "eng/scripts/get-aspire-cli*.sh,eng/scripts/get-aspire-cli*.ps1"
---

# CLI Acquisition Script Tests

When modifying `get-aspire-cli-pr.sh`, `get-aspire-cli-pr.ps1`, `get-aspire-cli.sh`, or `get-aspire-cli.ps1`, update the corresponding tests in `tests/Aspire.Acquisition.Tests/Scripts/`.

## Test project layout

| Script | Shell tests | Function tests | PS tests |
|--------|------------|----------------|----------|
| `get-aspire-cli-pr.sh` | `PRScriptShellTests.cs` | `PRScriptFunctionTests.cs` | — |
| `get-aspire-cli-pr.ps1` | `PRScriptPowerShellTests.cs` | `PRScriptPSFunctionTests.cs` | — |
| `get-aspire-cli.sh` | `ReleaseScriptShellTests.cs` | — | — |
| `get-aspire-cli.ps1` | `ReleaseScriptPowerShellTests.cs` | — | — |

- **Shell tests** (`*ShellTests.cs`): End-to-end parameter handling tests that invoke the script with `--dry-run` and a mock `gh` CLI.
- **Function tests** (`*FunctionTests.cs`): Unit tests for individual bash/PowerShell functions (e.g., `get_runtime_identifier`, `extract_version_suffix_from_packages`).
- **Common helpers**: `tests/Aspire.Acquisition.Tests/Scripts/Common/` contains `TestEnvironment`, `ScriptToolCommand`, `ScriptFunctionCommand`, `FakeArchiveHelper`, and mock `gh` script creation.

## Running tests

```bash
dotnet test --project tests/Aspire.Acquisition.Tests/Aspire.Acquisition.Tests.csproj --no-launch-profile -- --filter-not-trait "quarantined=true" --filter-not-trait "outerloop=true"
```

Bash script tests are skipped on Windows (`SkipOnPlatform`). PowerShell tests require `pwsh` (`RequiresTools`).
