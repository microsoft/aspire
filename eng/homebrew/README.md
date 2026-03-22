# Homebrew Distribution for Aspire CLI

## Overview

Aspire CLI is distributed via [Homebrew Cask](https://docs.brew.sh/Cask-Cookbook) for macOS (arm64 and x64). Cask PRs are submitted to the upstream [Homebrew/homebrew-cask](https://github.com/Homebrew/homebrew-cask) repository.

### Install commands

```bash
brew install --cask aspire              # stable
# brew install --cask aspire@prerelease   # preview (not yet supported)
```

## Contents

| File | Description |
|---|---|
| `aspire.rb.template` | Cask template for stable releases |
| `aspire@prerelease.rb.template` | Cask template for prerelease builds |
| `generate-cask.sh` | Downloads tarballs, computes SHA256 hashes, generates cask from template |

### Pipeline templates

| File | Description |
|---|---|
| `eng/pipelines/templates/prepare-homebrew-cask.yml` | Generates, validates, audits, and tests the cask |
| `eng/pipelines/templates/publish-homebrew.yml` | Submits the cask as a PR to `Homebrew/homebrew-cask` |

## Supported Platforms

macOS only (arm64, x64). The cask uses `arch arm: "arm64", intel: "x64"` for URL templating.

## Artifact URLs

```text
https://ci.dot.net/public/aspire/{VERSION}/aspire-cli-osx-{arch}-{VERSION}.tar.gz
```

Where arch is `arm64` or `x64`.

## Why Cask

| Product | Type | Install command | Preview channel |
|---|---|---|---|
| GitHub Copilot CLI | homebrew-cask | `brew install --cask copilot-cli` | `copilot-cli@prerelease` |
| .NET SDK | homebrew-cask | `brew install --cask dotnet-sdk` | `dotnet-sdk@preview` |
| PowerShell | homebrew-cask | `brew install --cask powershell` | `powershell@preview` |

- **URL templating**: `url "...osx-#{arch}-#{version}.tar.gz"` — a single line instead of nested `on_macos do / if Hardware::CPU.arm?` blocks
- **Official repo path**: Casks can be submitted to `Homebrew/homebrew-cask` for `brew install aspire` without a tap
- **Cleaner multi-channel**: `aspire` and `aspire@prerelease` follow established cask naming conventions

## CI Pipeline

| Pipeline | Prepares | Publishes |
|---|---|---|
| `azure-pipelines.yml` (prepare stage) | Stable casks (artifacts only) | — |
| `release-publish-nuget.yml` (release) | — | Stable cask only |

Publishing submits a PR to `Homebrew/homebrew-cask` using `gh pr create`:

1. Forks `Homebrew/homebrew-cask` (idempotent — reuses existing fork)
2. Creates a branch named `aspire-{version}`
3. Copies the generated cask to `Casks/a/aspire.rb` (or `aspire@prerelease.rb`)
4. Pushes and opens a PR with title `aspire {version}`

## Open Items

- [ ] Submit initial `aspire` cask PR to `Homebrew/homebrew-cask` for acceptance
- [ ] Submit `aspire@prerelease` cask PR to `Homebrew/homebrew-cask`
- [ ] Configure `aspire-homebrew-bot-pat` secret in the pipeline variable group

## References

- [Homebrew Cask Cookbook](https://docs.brew.sh/Cask-Cookbook)
- [Copilot CLI cask](https://formulae.brew.sh/cask/copilot-cli) — our reference implementation
- [.NET SDK cask](https://formulae.brew.sh/cask/dotnet-sdk) — stable + preview example
