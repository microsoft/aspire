# WinGet Distribution for Aspire CLI

## Overview

Aspire CLI is distributed via [WinGet](https://learn.microsoft.com/windows/package-manager/) for Windows (x64, arm64). Manifest PRs are submitted to the upstream [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) repository using [wingetcreate](https://github.com/microsoft/winget-create).

### Install commands

```powershell
winget install Microsoft.Aspire              # stable
```

## Contents

| Directory / File               | Description                                                                      |
|--------------------------------|----------------------------------------------------------------------------------|
| `microsoft.aspire/`            | Manifest templates for stable releases                                           |
| `generate-manifests.ps1`       | Downloads installers, computes SHA256 hashes, generates manifests from templates |
| `prepare-manifest-artifact.ps1` | Prepares CI artifacts by generating, validating, and adding dogfood helpers      |
| `dogfood.ps1`                  | Installs generated manifests locally, optionally using downloaded native archives |

Each manifest set contains three YAML files following the [WinGet manifest schema v1.10](https://learn.microsoft.com/windows/package-manager/package/manifest):

| File                                | Purpose                                         |
|-------------------------------------|-------------------------------------------------|
| `Aspire.yaml.template`              | Version manifest                                |
| `Aspire.installer.yaml.template`    | Installer manifest (URLs, SHA256, architecture) |
| `Aspire.locale.en-US.yaml.template` | Locale manifest (description, tags, license)    |

### Pipeline templates

| File                                                   | Description                                     |
|--------------------------------------------------------|-------------------------------------------------|
| `eng/pipelines/templates/prepare-winget-manifest.yml` | Generates, validates, and tests the manifests   |
| `eng/pipelines/templates/publish-winget.yml`           | Submits the manifests via `wingetcreate submit` |

## Supported Platforms

Windows only (x64, arm64). Installers are zip archives containing a portable `aspire.exe`.

## Artifact URLs

```text
https://ci.dot.net/public/aspire/{ARTIFACT_VERSION}/aspire-cli-win-{arch}-{VERSION}.zip
```

Where arch is `x64` or `arm64`.

## CI Pipeline

| Pipeline                              | Prepares                                        | Publishes             |
|---------------------------------------|-------------------------------------------------|-----------------------|
| `.github/workflows/tests.yml`         | Prerelease manifests (artifacts only)           | —                     |
| `azure-pipelines.yml` (prepare stage) | Stable or prerelease manifests (artifacts only) | —                     |
| `release-publish-nuget.yml` (release) | —                                               | Stable manifests only |

Publishing submits a PR to `microsoft/winget-pkgs` using `wingetcreate submit`.

## Validation model

Aspire CI relies on the upstream
[`microsoft/winget-pkgs`](https://github.com/microsoft/winget-pkgs)
validation pipeline for comprehensive manifest validation. That pipeline
runs on every submitted PR (see the
[validation failure guide](https://github.com/microsoft/winget-pkgs/blob/master/doc/ValidationFailureGuide.md))
and checks:

- **Schema validation** (what `winget validate --manifest` does) — missing
  required fields, type errors, deprecated schema versions, directory
  layout, regex constraints like `InstallerUrl ^https?://`.
- **Binary scanning** of the actual installer through multiple antivirus
  engines.
- **URL accessibility + Microsoft Defender SmartScreen reputation** for every
  `InstallerUrl`.
- **SHA256 hash verification** — downloads the installer and compares it
  to the manifest `InstallerSha256`.
- **Install/uninstall round-trip** in a clean VM, including verification of
  `AppsAndFeaturesEntries`.

On the Aspire side, `prepare-manifest-artifact.ps1` runs `winget validate
--manifest` opportunistically — whenever `winget.exe` is available on the
agent — as a fast local schema check. That covers the GitHub Actions
`windows-latest` runner used by
`.github/workflows/prepare-installer-artifacts.yml` (winget is
pre-installed there). It does **not** cover the official 1ES
`1es-windows-2022` pool used by the release pipeline: winget is not
pre-installed there and the agent cannot reach
`cdn.winget.microsoft.com` to install it (see
`eng/pipelines/templates/prepare-winget-manifest.yml`), so on that pool
the script skips local validate and the upstream-CI checks are the only
ones that run.

The same `.github/workflows/prepare-installer-artifacts.yml` job goes a
step further on every PR: after generating the manifest it runs
`eng/winget/dogfood.ps1 -Force` (real `winget install --manifest` from
the freshly built archive) and a smoke test (`aspire new` + restore)
against the installed shim. That catches manifest issues `winget
validate` does not — installer SHA mismatches, broken `InstallerSwitches`,
missing `Commands`. It does not exercise uninstall, so the full
install/uninstall round-trip stays delegated to upstream CI on every
submission.

Other Microsoft repos publishing to WinGet (`microsoft/PowerToys`,
`microsoft/terminal`, `microsoft/winget-create`) follow the same pattern
of relying on upstream CI for the install/uninstall round-trip.

## Local dogfood

To dogfood a GitHub Actions artifact locally, download the `winget-manifests-prerelease`
artifact and the `cli-native-archives-win-*` artifacts into the same parent directory, then run:

```powershell
.\dogfood.ps1 -ArchiveRoot ..
```

If `Microsoft.Aspire` is already installed through WinGet, uninstall it first or pass `-Force` to allow the local dogfood manifest to replace it.
