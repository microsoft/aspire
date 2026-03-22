# Dogfooding Pull Requests

This section explains how to locally try out (dogfood) changes from a specific pull request (PR) by installing the Aspire CLI and corresponding NuGet packages built by that PR's CI run.

Two cross-platform helper scripts are available:
- Bash: `eng/scripts/get-aspire-cli-pr.sh`
- PowerShell: `eng/scripts/get-aspire-cli-pr.ps1`

They download the correct build artifacts for your OS/architecture, install the `aspire` CLI to a local prefix, and populate a PR-scoped NuGet "hive" with the matching packages. This local, PR-specific NuGet hive keeps packages isolated, making it easy to create new projects that use only the packages from the PR build.

## Prerequisites

- GitHub CLI installed and authenticated
  - Install: https://cli.github.com/
  - Authenticate: `gh auth login`
- Network access to GitHub Actions
- Archive tools:
  - On Unix/macOS: `tar` (and/or `unzip`, depending on the archive format)
  - On Windows (PowerShell script): built-in extraction is used; for Git Bash + `.sh` script, ensure `unzip` or `tar` is available
- Optional (for one-liners):
  - Bash: `curl`
  - PowerShell: `Invoke-RestMethod`/`irm` (built-in)

Notes:
- On Alpine and other musl-based distros, use `--os linux-musl` (Bash) or `-OS linux-musl` (PowerShell).
- You can target a fork by setting `ASPIRE_REPO=owner/repo` in your environment. Defaults to `microsoft/aspire`.

## What gets installed

- Aspire CLI:
  - Default location: `~/.aspire/bin/aspire` (or `aspire.exe` on Windows)
  - Important: If you already have the Aspire CLI installed under the same prefix (default `~/.aspire`), running this script will overwrite that installation. To switch back to the official build, simply re-run the standard Aspire CLI install script referenced in the README to reinstall the released version.

- PR-scoped NuGet packages "hive":
  - Default location: `~/.aspire/hives/pr-<PR_NUMBER>/packages`
  - This local, PR-specific hive is isolated, making it easy to create new projects with just the packages produced by the PR build without affecting your global NuGet caches or other projects.

The scripts attempt to add `~/.aspire/bin` to your shell/profile PATH so you can invoke `aspire` directly in new terminals. If PATH isn't updated automatically, add it manually per the script's message.

## Quickstart

> **⚠️ WARNING: Do not do this without first carefully reviewing the code of this PR to satisfy yourself it is safe.**

Pick one of the approaches below.

### One-liner (Bash)

- Run remotely (downloads and executes the script from main):
  ```bash
  curl -fsSL https://raw.githubusercontent.com/microsoft/aspire/main/eng/scripts/get-aspire-cli-pr.sh | bash -s -- 1234
  ```

### One-liner (PowerShell)

- Run remotely in PowerShell:
  ```powershell
  iex "& { $(irm https://raw.githubusercontent.com/microsoft/aspire/main/eng/scripts/get-aspire-cli-pr.ps1) } 1234"
  ```

### From a local clone (Bash)

```bash
./eng/scripts/get-aspire-cli-pr.sh 1234
```

### From a local clone (PowerShell)

```powershell
./eng/scripts/get-aspire-cli-pr.ps1 1234
```

Replace `1234` with the PR number you want to try.

## Common options

The scripts auto-detect your OS and architecture and locate the latest `ci.yml` workflow run for the PR. You can override defaults as needed.

- Select a specific workflow run (if there are multiple or you want to pin):
  - Bash:
    ```bash
    ./eng/scripts/get-aspire-cli-pr.sh 1234 --run-id 987654321
    ```
  - PowerShell:
    ```powershell
    ./eng/scripts/get-aspire-cli-pr.ps1 1234 -WorkflowRunId 987654321
    ```
  Tip: The run ID is visible in the Actions run URL.

- Choose install location (default `~/.aspire`):
  - Bash:
    ```bash
    ./eng/scripts/get-aspire-cli-pr.sh 1234 --install-path ~/.aspire-pr
    ```
  - PowerShell:
    ```powershell
    ./eng/scripts/get-aspire-cli-pr.ps1 1234 -InstallPath $HOME/.aspire-pr
    ```

- Override OS and architecture (auto-detected by default):
  - Allowed OS values: `win`, `linux`, `linux-musl`, `osx`
  - Allowed arch values: `x64`, `x86`, `arm64`
  - Bash:
    ```bash
    ./eng/scripts/get-aspire-cli-pr.sh 1234 --os linux --arch arm64
    ```
  - PowerShell:
    ```powershell
    ./eng/scripts/get-aspire-cli-pr.ps1 1234 -OS linux -Architecture arm64
    ```

- Only fetch the NuGet "hive" (skip CLI):
  - Bash: `--hive-only`
  - PowerShell: `-HiveOnly`

- Verbose, keep archives, or dry run:
  - Bash: `-v/--verbose`, `-k/--keep-archive`, `--dry-run`
  - PowerShell: `-Verbose`, `-WhatIf` (PowerShell's dry-run), or provide equivalent parameters if present

- Target a fork instead of `microsoft/aspire`:
  - Bash:
    ```bash
    ASPIRE_REPO=myfork/aspire ./eng/scripts/get-aspire-cli-pr.sh 1234
    ```
  - PowerShell:
    ```powershell
    $env:ASPIRE_REPO = "myfork/aspire"
    ./eng/scripts/get-aspire-cli-pr.ps1 1234
    ```

## Examples

- Install CLI + packages for PR 1234, default locations:
  - Bash: `./eng/scripts/get-aspire-cli-pr.sh 1234`
  - PowerShell: `./eng/scripts/get-aspire-cli-pr.ps1 1234`

- Alpine Linux (musl) on arm64 into a custom prefix:
  - Bash:
    ```bash
    ./eng/scripts/get-aspire-cli-pr.sh 1234 --os linux-musl --arch arm64 --install-path ~/.aspire-alpine
    ```

- Only NuGet packages (no CLI), verbose:
  - Bash:
    ```bash
    ./eng/scripts/get-aspire-cli-pr.sh 1234 --hive-only --verbose
    ```
  - PowerShell:
    ```powershell
    ./eng/scripts/get-aspire-cli-pr.ps1 1234 -HiveOnly -Verbose
    ```

## Troubleshooting

- "No workflow run found":
  - Ensure the PR has a completed `ci.yml` run. If the PR just updated, wait for the run to finish.
  - If you know the run you want, pass `--run-id`/`-WorkflowRunId`.

- "Failed to download artifact … may not be available yet":
  - The run might still be in progress, or artifacts haven't been published. Retry after the CI completes.

- "GitHub CLI not installed/authenticated":
  - Install `gh` and run `gh auth login`.

- Archive extraction errors:
  - Ensure `tar` and/or `unzip` are available when using the Bash script.

## Uninstall/Cleanup

- Remove the CLI:
  - Delete `~/.aspire/bin/aspire` (or the custom install path you used)
  - Remove the PATH entry from your shell profile if added

- Remove PR-specific packages:
  - Delete `~/.aspire/hives/pr-<PR_NUMBER>/packages`

## Safety note

Remote one-liners execute scripts fetched from the repository. Review the script source before running if needed:
- Bash: `eng/scripts/get-aspire-cli-pr.sh`
- PowerShell: `eng/scripts/get-aspire-cli-pr.ps1`