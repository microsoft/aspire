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

To dogfood a GitHub Actions artifact locally, download the `winget-manifests-prerelease`
artifact and the `cli-native-archives-win-*` artifacts into the same parent directory, then run:

```powershell
.\dogfood.ps1 -ArchiveRoot ..
```

If `Microsoft.Aspire` is already installed through WinGet, uninstall it first or pass `-Force` to allow the local dogfood manifest to replace it.
