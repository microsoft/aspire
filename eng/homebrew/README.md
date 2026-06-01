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

## Supported Platforms

macOS only (arm64, x64). The cask uses `arch arm: "arm64", intel: "x64"`
for URL templating and declares `depends_on :macos` so Homebrew's tap-syntax
check (`brew test-bot --only-tap-syntax`, which evaluates every cask on
every supported platform including Linux) doesn't try to load it on Linux
where the `arch` hash has no matching key.

## Artifact URLs

The cask installs from GitHub release assets:

```text
https://github.com/microsoft/aspire/releases/download/v{VERSION}/aspire-cli-osx-{arch}-{VERSION}.tar.gz
```

Where arch is `arm64` or `x64`. The same version value appears in the
release tag and the filename — having the URL parameterized on a single
version is what lets `brew bump-cask-pr --version=<v>` rewrite the cask
in one substitution (see "Submission: upstream autobump" below).

The SHA256 baked into the cask is computed from the local source-build
archive (`generate-cask.sh --archive-root`) — every current prepare path
passes `--archive-root`. The GitHub release asset is uploaded byte-for-byte
from that same source-build artifact, so the SHA256 in the cask matches
what `brew install` fetches from the GitHub release URL.

## Why Cask

| Product | Type | Install command |
|---|---|---|
| GitHub Copilot CLI | homebrew-cask | `brew install --cask copilot-cli` |
| .NET SDK | homebrew-cask | `brew install --cask dotnet-sdk` |
| PowerShell | homebrew-cask | `brew install --cask powershell` |

- **URL templating**: `url "...osx-#{arch}-#{version}.tar.gz"` — a single line instead of nested `on_macos do / if Hardware::CPU.arm?` blocks
- **Official repo path**: Casks can be submitted to `Homebrew/homebrew-cask` for `brew install aspire` without a tap
- **Stable-only release flow**: only the stable `aspire` cask is shipped
  via `Homebrew/homebrew-cask`. A prerelease cask shipped via an
  Aspire-owned tap remains a possible future option; the artifact that
  the prepare stage emits would be the input to such a future publisher.

## CI Pipeline

| Pipeline | Prepares | Validates against live release | Publishes |
|---|---|---|---|
| `.github/workflows/tests.yml` | Prerelease casks (artifacts only) | — | — |
| `azure-pipelines.yml` (prepare stage) | Stable or prerelease casks (artifacts only) | — | — |
| `release-publish-nuget.yml` (release) | — | Stable cask, LiveRelease mode | — (autobump handles bumps; see below) |

The release pipeline's `HomebrewValidateJob` runs `validate-cask-artifact.sh`
in LiveRelease mode against the cask emitted by the source build, after the
release-asset upload step has attached the `aspire-cli-osx-*.tar.gz`
archives to the GitHub release. This is the first point at which the
cask's `url` (a `v#{version}` GitHub release-asset URL) actually resolves;
the source-build prepare stage can only validate offline because the
GitHub release for the version being built does not exist yet. Failures
in this job catch problems that would otherwise only surface to end
users running `brew install aspire`, or block Homebrew/homebrew-cask's
autobump PR a few hours later. Gated by `SkipHomebrewValidation` for
partial-failure re-runs.

### Submission: upstream autobump

Stable cask version bumps for `Homebrew/homebrew-cask` are submitted by
upstream's [autobump workflow](https://github.com/Homebrew/homebrew-cask/blob/master/.github/workflows/autobump.yml),
which runs `brew bump --auto --tap=Homebrew/cask --no-fork --open-pr --casks`
on a 3-hour schedule. Every cask in the tap is autobump-eligible by default;
casks opt out by adding `no_autobump! because: :reason` to their `.rb`. The
Aspire cask does not opt out.

For autobump to detect a new version, the cask's `livecheck` block must
resolve to a version string. The Aspire cask uses the `:github_latest`
strategy against its own `url`, which scrapes the latest release tag from
`github.com/microsoft/aspire/releases` via the GitHub API. The cask is
only autobump-correct when the release-asset upload step in
`release-publish-nuget.yml` reliably attaches `aspire-cli-osx-*.tar.gz`
assets to the release; otherwise autobump will open a PR whose URLs
return 404 at install time.

`brew livecheck --debug aspire` (against a local tap; see `dogfood.sh`)
prints the URL fetched and the matched version — use it whenever the
livecheck block changes.

### Initial cask submission is manual

Autobump only handles **version bumps** for an existing upstream cask.
The very first submission of the `aspire` cask to
`Homebrew/homebrew-cask` is a human-driven, one-time operation —
first-time submissions require additional `--new`-specific audit checks
plus maintainer review that aren't appropriate for an automated pipeline.

### Prepare validation

`eng/homebrew/validate-cask-artifact.sh` runs the validation gauntlet
modeled on the per-cask CI matrix in
[`Homebrew/homebrew-cask`'s `ci.yml`](https://github.com/Homebrew/homebrew-cask/blob/main/.github/workflows/ci.yml).
It has two modes that pick the audit-arg combination based on where the
cask URL points at validation time:

| Mode | Cask URL resolves? | Audit args | Used by |
|---|---|---|---|
| `LiveRelease` | Yes — points at a live GitHub release | `brew audit --cask --online --signing` + `brew install`/`brew uninstall` | `release-publish-nuget.yml` `HomebrewValidateJob`, after `PublishReleaseAssetsJob` uploads the archives |
| `LiveArchives` | Not yet — release for `v#{version}` hasn't been published | `brew audit --cask --no-signing` (no `--online`) | `azure-pipelines.yml` Homebrew Cask job; `.github/workflows/tests.yml`; `dogfood.sh` PR validation |

Common to both modes:

1. `ruby -c aspire.rb` — Ruby syntax check
2. `brew style --fix` — Cookbook formatting rules
3. `brew test-bot --tap local/aspire --only-tap-syntax` — tap-level
   cross-platform syntax check; this is the upstream job that catches
   "Invalid cask (Linux on …)" or other platform-evaluation failures

LiveArchives intentionally drops `--online` because several `--online`-gated
audit methods (`audit_download`, `audit_signing`, `audit_rosetta`,
`audit_min_os`) try to fetch the cask URL, and that URL points at a GitHub
release that doesn't exist yet at source-build time. Excluding them
individually with `--except` is brittle — any new `--online`-gated audit
method that touches the archive in a future brew release would silently
start failing. `--no-signing` is also used because PR-build /
source-build archives are unsigned CI artifacts.

The price is that LiveArchives doesn't run the `--online`-only checks:
github/gitlab repo probes, homepage redirect/404 detection, livecheck
strategy resolution. LiveRelease in `HomebrewValidateJob` runs all of
them on every released version, so a regression in any surfaces there.

`LiveRelease` is the contract that matches what
`Homebrew/homebrew-cask`'s own CI runs on the autobump PR — a clean run
in `HomebrewValidateJob` implies the autobump PR will audit cleanly too.

To dogfood a GitHub Actions artifact locally, download the
`homebrew-cask-prerelease` artifact and the `cli-native-archives-osx-*`
artifacts into the same parent directory, then run:

```bash
./dogfood.sh --archive-root ..
```

## Open Items

- [ ] Submit the initial `aspire` cask PR to `Homebrew/homebrew-cask`
      manually for first-time acceptance. Autobump only handles subsequent
      version bumps.
- [ ] (Future) Decide whether to add a separate prerelease cask (for
      example, `aspire@prerelease`) shipped via an Aspire-owned tap. The
      prepare stage already emits a `homebrew-cask-prerelease` artifact;
      a future publisher pipeline would consume that artifact and push
      to the Aspire-owned tap.

## References

- [Homebrew Cask Cookbook](https://docs.brew.sh/Cask-Cookbook)
- [Copilot CLI cask](https://formulae.brew.sh/cask/copilot-cli) — our reference implementation
- [.NET SDK cask](https://formulae.brew.sh/cask/dotnet-sdk) — stable + preview example
