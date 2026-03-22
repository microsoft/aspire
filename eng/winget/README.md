# WinGet Distribution for Aspire CLI

## Overview

Aspire CLI is distributed via [WinGet](https://learn.microsoft.com/windows/package-manager/) for Windows (x64, arm64). Manifest PRs are submitted to the upstream [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) repository using [wingetcreate](https://github.com/microsoft/winget-create).

### Install commands

```powershell
winget install Microsoft.Aspire              # stable
# winget install Microsoft.Aspire.Prerelease   # preview (not yet supported)
```

## Contents

| Directory / File               | Description                                                                      |
|--------------------------------|----------------------------------------------------------------------------------|
| `microsoft.aspire/`            | Manifest templates for stable releases                                           |
| `microsoft.aspire.prerelease/` | Manifest templates for prerelease builds                                         |
| `generate-manifests.ps1`       | Downloads installers, computes SHA256 hashes, generates manifests from templates |

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
https://ci.dot.net/public/aspire/{VERSION}/aspire-cli-win-{arch}-{VERSION}.zip
```

Where arch is `x64` or `arm64`.

## CI Pipeline

| Pipeline                              | Prepares                                       | Publishes             |
|---------------------------------------|------------------------------------------------|-----------------------|
| `azure-pipelines.yml` (prepare stage) | Stable manifests (artifacts only) | —                     |
| `release-publish-nuget.yml` (release) | —                                              | Stable manifests only |

Publishing submits a PR to `microsoft/winget-pkgs` using `wingetcreate submit`.
