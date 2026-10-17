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

## Release version resolution

Quality installs resolve the archive redirect once and download the archive and
its SHA-512 checksum from the same immutable build. Resolution failures stop the
install; checksum verification remains mandatory. Dry-run and `-WhatIf` installs
remain offline.

Use `--resolve-only` (PowerShell: `-ResolveOnly`) to print a JSON object containing
`version` and `buildVersion`, without downloading or installing the CLI. The
product version in the archive filename can differ from the artifact build
directory, especially for stable-shaped staging builds. Pass both values back
to the script to preserve the exact build:

```bash
resolved=$(eng/scripts/get-aspire-cli.sh --quality dev --resolve-only)
eng/scripts/get-aspire-cli.sh \
  --version "$(jq -r '.version' <<< "$resolved")" \
  --build-version "$(jq -r '.buildVersion' <<< "$resolved")"
```

```powershell
$resolved = ./eng/scripts/get-aspire-cli.ps1 -Quality dev -ResolveOnly | ConvertFrom-Json
./eng/scripts/get-aspire-cli.ps1 -Version $resolved.version -BuildVersion $resolved.buildVersion
```

`--build-version` / `-BuildVersion` requires an explicit product version.
With a build pin, quality may also be supplied to retain the requested install
channel without resolving the CLI alias again.
Existing explicit-version installs without a build pin keep their routing:
stable versions use GitHub release assets, and prereleases use versioned build
artifacts. Normal quality installs still persist the requested channel.

The VS Code extension has its own version. Extension installation continues to
use the requested dev quality alias; the CLI pin does not pin the extension.

The release-script tests simulate the alias advancing after resolution, verify
both download URLs stay pinned, and reject a mismatched checksum before extraction.
They also cover resolution failures, stable-shaped build directories, independently
versioned extension downloads, and the legacy PowerShell response shape.

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
