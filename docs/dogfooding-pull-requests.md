# Dogfooding Pull Requests

This section explains how to locally try out (dogfood) changes from a specific pull request (PR) by installing the Aspire CLI and corresponding NuGet packages built by that PR's CI run.

Two cross-platform helper scripts are available:
- Bash: `eng/scripts/get-aspire-cli-pr.sh`
- PowerShell: `eng/scripts/get-aspire-cli-pr.ps1`

They download the correct build artifacts for your OS/architecture and support two CLI install modes:

- **Archive mode** (default): installs the native CLI archive into an isolated PR-specific directory and populates a PR-scoped NuGet "hive" with the matching packages.
- **Tool mode**: installs the `Aspire.Cli` .NET tool from the PR's RID-specific NuGet artifact and populates the same PR-scoped NuGet hive. Use this when you also want to dogfood the dotnet-tool packaging or acquisition route.

Both modes give `aspire new`, `aspire add`, and `aspire run` access to the PR's NuGet packages via the hive at `~/.aspire/hives/pr-<PR_NUMBER>/packages`. The difference is only how the CLI binary itself is acquired.

## Prerequisites

- GitHub CLI installed and authenticated
  - Install: https://cli.github.com/
  - Authenticate: `gh auth login`
- Network access to GitHub Actions
- .NET SDK available on PATH when using tool mode (`--install-mode tool` or `-InstallMode Tool`)
- Archive tools:
  - On Unix/macOS: `tar` (and/or `unzip`, depending on the archive format)
  - On Windows (PowerShell script): built-in extraction is used; for Git Bash + `.sh` script, ensure `unzip` or `tar` is available
- Optional (for one-liners):
  - Bash: `curl`
  - PowerShell: `Invoke-RestMethod`/`irm` (built-in)

Notes:
- On Alpine and other musl-based distros, use `--os linux-musl` (Bash) or `-OS linux-musl` (PowerShell).
- You can target a fork by setting `ASPIRE_REPO=owner/repo` in your environment. Defaults to `microsoft/aspire`.

## Install modes

### Archive mode (default)

Archive mode installs from the PR's `cli-native-archives-<rid>` artifact and copies matching packages into a local NuGet hive.

Use archive mode when you want the CLI and project/package operations such as `aspire new`, `aspire add`, or `aspire update` to use packages from the same PR build.

### Tool mode

Tool mode installs the `Aspire.Cli` package as a .NET tool from the PR's `built-nugets-for-<rid>` artifact and also populates the PR-scoped NuGet hive from the cross-platform `built-nugets` artifact (just like archive mode), so `aspire new`, `aspire add`, and `aspire run` resolve against the PR build.

Use tool mode when you also want to dogfood the `Aspire.Cli` dotnet-tool package layout / install route in addition to the rest of the PR. By default it installs as a global .NET tool:

```bash
./eng/scripts/get-aspire-cli-pr.sh 1234 --install-mode tool
```

```powershell
./eng/scripts/get-aspire-cli-pr.ps1 1234 -InstallMode Tool
```

If `Aspire.Cli` is already installed as a .NET tool, re-run with `--force` (Bash) or `-Force` (PowerShell) to update it to the PR version, including downgrades.

To avoid changing the global .NET tool installation, pass a custom install path. In tool mode, the scripts pass that path through to `dotnet tool --tool-path` using the PR-specific `bin` directory under the prefix:

```bash
./eng/scripts/get-aspire-cli-pr.sh 1234 --install-mode tool --install-path ~/.aspire-pr
```

```powershell
./eng/scripts/get-aspire-cli-pr.ps1 1234 -InstallMode Tool -InstallPath $HOME/.aspire-pr
```

## What gets installed

- Aspire CLI binary:
  - **Archive mode** default location: `~/.aspire/dogfood/pr-<PR_NUMBER>/bin/aspire` (or `aspire.exe` on Windows).
  - **Tool mode** default location: `dotnet tool install --global` puts the tool on the PATH per the .NET SDK's global-tools convention. When tool mode is combined with `--install-path`, the scripts pass that path to `dotnet tool --tool-path` using the PR-specific `bin` directory under the prefix.
  - Important: If you already have an Aspire CLI installed for the same PR under the same prefix, running this script will overwrite that PR installation. Other PR installs and the regular script install under `~/.aspire/bin` are isolated from this path.

- PR-scoped NuGet packages "hive":
  - Default location: `~/.aspire/hives/pr-<PR_NUMBER>/packages`
  - Populated in both archive and tool mode. This local, PR-specific hive is isolated, making it easy to create new projects with just the packages produced by the PR build without affecting your global NuGet caches or other projects.

In archive mode, the scripts attempt to add the PR-specific `bin` directory to your shell/profile PATH so you can invoke `aspire` directly in new terminals. If PATH isn't updated automatically, add it manually per the script's message.

In default tool mode, PATH setup is left to `dotnet tool install --global`. When tool mode is used with a custom install path, the scripts treat the PR-specific `bin` directory as the tool path and can add it to PATH.

## Channel names

Every Aspire CLI binary is built for a specific channel. The channel name controls which package feed (or local hive) is used by commands like `aspire new` and `aspire add`, and is the value you pass to `aspire update --self --channel`.

| Channel | Description |
|---------|-------------|
| `stable` | Official release builds. Default for most users. |
| `staging` | RC and preview builds. |
| `daily` | Latest daily CI builds. |
| `local` | Locally built from source. Uses `~/.aspire/hives/local/packages/` as the package feed. This is the default channel for developer builds with no explicit `/p:AspireCliChannel=` override. |
| `pr-<N>` | A single PR's CI build (for example `pr-16820`). Uses `~/.aspire/hives/pr-<N>/packages/` as the package feed. |

PR dogfooding installs a `pr-<N>` CLI and populates the matching hive directory automatically — you do not need to set the channel manually. This is true for both archive and tool install modes.

## Quickstart

> **⚠️ WARNING: Do not do this without first carefully reviewing the code of this PR to satisfy yourself it is safe.**

Pick one of the approaches below.

### One-liner (Bash)

- Run remotely from archives (downloads and executes the script from main):
  ```bash
  curl -fsSL https://raw.githubusercontent.com/microsoft/aspire/main/eng/scripts/get-aspire-cli-pr.sh | bash -s -- 1234
  ```

- Run remotely as a dotnet tool:
  ```bash
  curl -fsSL https://raw.githubusercontent.com/microsoft/aspire/main/eng/scripts/get-aspire-cli-pr.sh | bash -s -- 1234 --install-mode tool
  ```

### One-liner (PowerShell)

- Run remotely from archives in PowerShell:
  ```powershell
  iex "& { $(irm https://raw.githubusercontent.com/microsoft/aspire/main/eng/scripts/get-aspire-cli-pr.ps1) } 1234"
  ```

- Run remotely as a dotnet tool in PowerShell:
  ```powershell
  iex "& { $(irm https://raw.githubusercontent.com/microsoft/aspire/main/eng/scripts/get-aspire-cli-pr.ps1) } 1234 -InstallMode Tool"
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
  - Tool mode uses `dotnet tool install`, which resolves RID-specific packages for the current host. Use OS/architecture overrides with archive mode, not to cross-install a dotnet tool for another RID.
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
  - This option is only valid in archive mode.

- Install the PR's `Aspire.Cli` dotnet tool instead of the native archive:
  - Bash: `--install-mode tool` or `-m tool`
  - PowerShell: `-InstallMode Tool`
  - If an existing dotnet-tool install blocks the requested version, use Bash `--force` or PowerShell `-Force`.

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

- Install the PR's `Aspire.Cli` dotnet tool globally:
  - Bash: `./eng/scripts/get-aspire-cli-pr.sh 1234 --install-mode tool`
  - PowerShell: `./eng/scripts/get-aspire-cli-pr.ps1 1234 -InstallMode Tool`

- Update an existing global `Aspire.Cli` dotnet tool to the PR version:
  - Bash: `./eng/scripts/get-aspire-cli-pr.sh 1234 --install-mode tool --force`
  - PowerShell: `./eng/scripts/get-aspire-cli-pr.ps1 1234 -InstallMode Tool -Force`

- Install the PR's dotnet tool into an isolated path:
  - Bash:
    ```bash
    ./eng/scripts/get-aspire-cli-pr.sh 1234 --install-mode tool --install-path ~/.aspire-pr
    ```
  - PowerShell:
    ```powershell
    ./eng/scripts/get-aspire-cli-pr.ps1 1234 -InstallMode Tool -InstallPath $HOME/.aspire-pr
    ```

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

## C# AppHost scaffolding

When you run `aspire init` against a workspace using a `pr-<N>` CLI (such as one installed by the scripts above), the command writes a workspace-scoped `NuGet.config` to the solution root pinned to the matching `~/.aspire/hives/pr-<N>/packages/` feed. Package restore in the scaffolded C# AppHost resolves the PR build's `Aspire.Hosting.*` packages and `Aspire.AppHost.Sdk` from that hive.

The same behavior applies to other non-stable CLI channels (`staging`, `daily`, locally-built `local`), with the workspace `NuGet.config` pinned to the matching channel feed — a remote Azure DevOps feed for `staging` / `daily`, a local hive for `local` / `run-*`.

The file is scoped to the solution directory and only affects projects under it. It does not modify your user-level or machine-level NuGet configuration.

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

- "The .NET SDK 'dotnet' command is required":
  - Tool mode requires `dotnet` on PATH because it shells out to `dotnet tool install`.

- "`Aspire.Cli` is already installed" or the install fails because the PR version is lower than the installed version:
  - Re-run with Bash `--force` or PowerShell `-Force` to use `dotnet tool update --allow-downgrade`.

## Uninstall/Cleanup

- Remove an archive-mode PR CLI:
  - Delete `~/.aspire/dogfood/pr-<PR_NUMBER>/bin/aspire` (or `aspire.exe` on Windows)
  - Remove the PATH entry from your shell profile if added

- Remove a global tool-mode install:
  - Run `dotnet tool uninstall --global Aspire.Cli`

- Remove a custom-path tool-mode install:
  - Delete the PR-specific `bin` directory under the custom prefix, for example `~/.aspire-pr/dogfood/pr-<PR_NUMBER>/bin`
  - Remove the PATH entry from your shell profile if added

- Remove PR-specific packages:
  - Delete `~/.aspire/hives/pr-<PR_NUMBER>/packages`

### Brew uninstall caveats

The Homebrew cask (`eng/homebrew/aspire.rb.template`) installs Aspire entirely
inside the Caskroom version directory — `brew uninstall aspire` removes
the binary and the route sidecar end-to-end. The cask intentionally carries
no `zap` stanza, because `~/.aspire/` is a shared prefix with the script-route
and PR-route installers and a brew-driven recursive delete would clobber state
those installers still own.

If you installed via the Homebrew cask before this change, you may have a
stale dogfood state directory under `~/.aspire/installs/brew-stable/`. The
new cask never touches that path, and `brew uninstall` will not remove it.
Clean it up manually once after upgrading the cask:

```bash
rm -rf ~/.aspire/installs/brew-stable
```

NuGet hives under `~/.aspire/hives/` and any script-route or PR-route
binaries under `~/.aspire/bin/` and `~/.aspire/dogfood/` are not touched by
the cask in either direction; manage those with the steps above.

## Safety note

Remote one-liners execute scripts fetched from the repository. Review the script source before running if needed:
- Bash: `eng/scripts/get-aspire-cli-pr.sh`
- PowerShell: `eng/scripts/get-aspire-cli-pr.ps1`
