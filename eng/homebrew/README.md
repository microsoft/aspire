# Homebrew Distribution for Aspire CLI

## Overview

Aspire CLI is distributed via [Homebrew Cask](https://docs.brew.sh/Cask-Cookbook) for macOS (arm64 and x64). Cask PRs are submitted to the upstream [Homebrew/homebrew-cask](https://github.com/Homebrew/homebrew-cask) repository.

### Install commands

```bash
brew install --cask aspire              # stable
```

## Contents

| File | Description |
|---|---|
| `aspire.rb.template` | Cask template for stable releases |
| `generate-cask.sh` | Downloads tarballs, computes SHA256 hashes, generates cask from template |
| `prepare-cask-artifact.sh` | Prepares CI artifacts by generating, validating, and adding dogfood helpers |
| `validate-cask-artifact.sh` | Runs shared cask syntax, style, audit, and install validation used by GitHub Actions and Azure DevOps |
| `dogfood.sh` | Installs a generated cask locally, optionally using downloaded native archive artifacts |

### Pipeline templates

| File | Description |
|---|---|
| `eng/pipelines/templates/prepare-homebrew-cask.yml` | Generates, styles, validates, audits, and tests the cask |
| `eng/pipelines/templates/publish-homebrew.yml` | Submits the cask as a PR to `Homebrew/homebrew-cask` |

## Supported Platforms

macOS only (arm64, x64). The cask uses `arch arm: "arm64", intel: "x64"` for URL templating.

## Artifact URLs

```text
https://ci.dot.net/public/aspire/{ARTIFACT_VERSION}/aspire-cli-osx-{arch}-{VERSION}.tar.gz
```

Where arch is `arm64` or `x64`.

## Why Cask

| Product | Type | Install command |
|---|---|---|
| GitHub Copilot CLI | homebrew-cask | `brew install --cask copilot-cli` |
| .NET SDK | homebrew-cask | `brew install --cask dotnet-sdk` |
| PowerShell | homebrew-cask | `brew install --cask powershell` |

- **URL templating**: `url "...osx-#{arch}-#{version}.tar.gz"` — a single line instead of nested `on_macos do / if Hardware::CPU.arm?` blocks
- **Official repo path**: Casks can be submitted to `Homebrew/homebrew-cask` for `brew install aspire` without a tap
- **Stable-only release flow**: the current Aspire Homebrew publishing pipeline prepares and submits only the stable `aspire` cask, while a separate prerelease cask remains a possible future option

## CI Pipeline

| Pipeline | Prepares | Publishes |
|---|---|---|
| `.github/workflows/tests.yml` | Prerelease casks (artifacts only) | — |
| `azure-pipelines.yml` (prepare stage) | Stable or prerelease casks (artifacts only) | — |
| `release-publish-nuget.yml` (release) | — | Stable cask only |

Publishing submits a PR to `Homebrew/homebrew-cask` using the GitHub REST API:

1. Forks `Homebrew/homebrew-cask` (idempotent — reuses existing fork)
2. Creates or resets a branch named `aspire-{version}`
3. Copies the generated cask to `Casks/a/aspire.rb`
4. Reuses the existing open PR for that branch when present
5. Force-pushes the same branch for reruns; if prior PRs from that branch were closed, the publish step opens a fresh PR and marks the old ones as superseded
6. Opens a PR with title `aspire {version}` when none exists

Prepare validation currently runs:

1. `ruby -c` for syntax validation
2. `brew style --fix` on the generated cask
3. `brew audit --cask --online local/aspire/aspire`
   - Adds `--new` only when the cask is absent upstream (existing casks fail the additional `--new` checks).
   - Adds `--no-signing` in offline mode (PR-artifact validation), because the served archives are local loopback URLs of unsigned PR builds rather than notarized release assets.
4. `HOMEBREW_NO_INSTALL_FROM_API=1 brew install --cask ...` followed by uninstall validation

PR artifact validation uses the same shared script and local tap, but rewrites
the cask URLs to loopback archive URLs and runs in offline mode (see above).
Release preparation keeps the full online signing audit.

To dogfood a GitHub Actions artifact locally, download the `homebrew-cask-prerelease`
artifact and the `cli-native-archives-osx-*` artifacts into the same parent directory, then run:

```bash
./dogfood.sh --archive-root ..
```

## Open Items

- [ ] Submit initial `aspire` cask PR to `Homebrew/homebrew-cask` for acceptance
- [ ] (Future) Decide whether to add a separate prerelease cask (for example, `aspire@prerelease`) and update pipelines/docs accordingly
- [ ] Configure `aspire-homebrew-bot-pat` secret in the pipeline variable group

## References

- [Homebrew Cask Cookbook](https://docs.brew.sh/Cask-Cookbook)
- [Copilot CLI cask](https://formulae.brew.sh/cask/copilot-cli) — our reference implementation
- [.NET SDK cask](https://formulae.brew.sh/cask/dotnet-sdk) — stable + preview example
