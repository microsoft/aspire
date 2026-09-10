# Aspire CLI

The Aspire CLI is used to create, run, and manage Aspire-based distributed applications.

## Usage

```text
aspire <command> [options]
```

## Global Options

| Option | Description |
|--------|-------------|
| `-h, /h` | Show help and usage information. |
| `-v, --version` | Show version information. |
| `-l, --log-level` | Set the minimum log level for console output (Trace, Debug, Information, Warning, Error, Critical). |
| `--non-interactive` | Run the command in non-interactive mode, disabling all interactive prompts and spinners. |
| `--nologo` | Suppress the startup banner and telemetry notice. |
| `--banner` | Display the animated Aspire CLI welcome banner. |
| `--wait-for-debugger` | Wait for a debugger to attach before executing the command. |

## Commands

### App Commands

| Command | Description |
|---------|-------------|
| `new` | Create a new app from an Aspire starter template. |
| `init` | Initialize Aspire in an existing codebase. |
| `add [<integration>]` | Add a hosting integration to the apphost. |
| `update` | Update integrations in the Aspire project. |
| `run` | Run an apphost in development mode. |
| `stop` | Stop a running apphost or the specified resource. |
| `ps` | List running apphosts. |

### Resource Management

| Command | Description |
|---------|-------------|
| `start <resource>` | Start a stopped resource. |
| `stop [<resource>]` | Stop a running apphost or the specified resource. |
| `restart <resource>` | Restart a running resource. |
| `wait <resource>` | Wait for a resource to reach a target status. |
| `command <resource> <command>` | Execute a command on a resource. |

### Monitoring

| Command | Description |
|---------|-------------|
| `describe [<resource>]` | Describe resources in a running apphost. |
| `logs [<resource>]` | Display logs from resources in a running apphost. |
| `otel` | View OpenTelemetry data (logs, spans, traces) from a running apphost. |

### Deployment

| Command | Description |
|---------|-------------|
| `publish` | Generate deployment artifacts for an apphost. |
| `deploy` | Deploy an apphost to its deployment targets. |
| `destroy` | Destroy a previously deployed AppHost environment. |
| `do <step>` | Execute a specific pipeline step and its dependencies. |

### Tools & Configuration

| Command | Description |
|---------|-------------|
| `config` | Manage CLI configuration including feature flags. |
| `cache` | Manage disk cache for CLI operations. |
| `completions script [<shell>]` | Generate a completion script for Bash, Zsh, Fish, or PowerShell 7+. |
| `doctor` | Diagnose Aspire environment issues and verify setup. |
| `docs` | Browse and search Aspire documentation and API reference from aspire.dev. |
| `agent` | Manage AI agent specific setup. |

## Examples

```bash
# Create a new Aspire application
aspire new

# Run the apphost
aspire run

# Start in the background (useful for CI and agent environments)
aspire start --isolated

# Check resource status
aspire describe

# Stream resource state changes
aspire describe --follow

# View logs
aspire logs
aspire logs webapi

# Stop the apphost
aspire stop

# Wait for a resource to be healthy (CI/scripts)
aspire start
aspire wait webapi --timeout 60

# Add an integration
aspire add redis

# Diagnose environment issues
aspire doctor

# Search the API reference
aspire docs api search "RunAsEmulator" --language csharp

# Search Aspire documentation
aspire docs search "redis"
```

## Shell completion

`aspire completions script [bash|fish|pwsh|zsh]` writes a shell script to stdout.
When omitted, the shell is inferred from `SHELL`, falling back to `pwsh` on Windows.
Specify the shell explicitly in installers and profiles: a login shell need not be
the shell currently running. `cmd.exe` and Windows PowerShell are not supported.

The hooks query the CLI's live command model for commands, subcommands, options,
and values with completion sources (including enum values). They do not need
`dotnet-suggest`. Tab requests do not execute commands, launch AppHosts, prefetch
packages, collect telemetry, or write CLI logs or first-use state. Suggestions do
not query running resources or search package feeds.

The examples below assume `aspire` is on PATH. They resolve the active command
on every request, so upgrades and switching install routes do not pin completion
to an old binary. Do not replace it with a versioned npm cache, tool store, or
Homebrew staging path.

### Bash

Enable in the current shell:

```bash
source <(aspire completions script bash)
```

For future interactive shells, add this once to `~/.bashrc`:

```bash
if command -v aspire >/dev/null 2>&1; then
    source <(aspire completions script bash)
fi
```

If you use login shells, ensure the login profile sources `~/.bashrc`.
Remove the block to disable persistent completion; `complete -r aspire` removes
the registration from the current shell.

### Zsh

If you manage Zsh initialization yourself, source the script after `compinit`.
Otherwise, the generated script initializes the completion system, excluding
insecure completion directories rather than prompting during profile loading:

```zsh
source <(aspire completions script zsh)
```

For persistence, add the following once to `${ZDOTDIR:-$HOME}/.zshrc`, after
the existing `compinit` call:

```zsh
if (( $+commands[aspire] )); then
    source <(aspire completions script zsh)
fi
```

Alternatively, save the generated script as `_aspire` in a directory on `fpath`
before calling `compinit`. Remove your source block or `_aspire` file to undo
setup; `compdef -d aspire` removes the current session's registration.

### Fish

Enable in the current shell:

```fish
aspire completions script fish | source
```

For persistence, save the generated file in Fish's user completion directory:

```fish
set -l completion_dir ~/.config/fish/completions
if set -q XDG_CONFIG_HOME
    set completion_dir "$XDG_CONFIG_HOME/fish/completions"
end
mkdir -p "$completion_dir"
aspire completions script fish > "$completion_dir/aspire.fish"
```

Remove that file to undo persistent setup; `complete --erase --command aspire`
and `functions --erase __aspire_complete` remove the current session's registration
and helper.

### PowerShell 7+

Enable in the current session:

```powershell
aspire completions script pwsh | Out-String | Invoke-Expression
```

For persistence, add this once to your chosen PowerShell profile:

```powershell
if (Get-Command aspire -ErrorAction SilentlyContinue) {
    aspire completions script pwsh | Out-String | Invoke-Expression
}
```

`$PROFILE.CurrentUserAllHosts` applies to all PowerShell hosts for the current
user and edition; `$PROFILE` applies only to the current host. Obtain the path
inside **pwsh**, not Windows PowerShell. Create the profile's parent directory
and file if needed, without replacing existing contents. Do not modify a signed
profile or change execution policy to enable completions. Remove the block and
start a new session to undo registration. `-NoProfile` deliberately skips profiles.

### Installation routes

| Route | Completion setup |
|-------|------------------|
| Custom release scripts | Generate a completion file and register it in the supported user's shell. `--skip-completions` / `-SkipCompletions` opts out. Dry-run/WhatIf does not write registration. |
| PR/dogfood scripts | Keep transient installs out of persistent profiles; use the printed activation instructions for the session. |
| WinGet | Portable ZIP installation has no post-install hook. Installation notes point to manual setup above. |
| npm global install | Use the manual setup above. npm lifecycle hooks do not reliably identify the user's shell, can be disabled, and have no uninstall hook. No profile edits occur during npm install. |
| .NET global or `--tool-path` install | Use the manual setup above after putting the tool shim on PATH. `dotnet tool install` has no supported publisher post-install hook. |
| Homebrew cask | Homebrew generates and owns completion files for all four shells. Shell activation remains user-controlled; see below. |

Custom release scripts keep generated files at
`$HOME/.aspire/completions/aspire.{bash,zsh,fish,ps1}`. Bash registration uses
`.bashrc` and the first existing login profile; a newly created `.bash_profile`
also sources `.bashrc`. Zsh registration uses `${ZDOTDIR:-$HOME}/.zshrc`, Fish uses
`${XDG_CONFIG_HOME:-$HOME/.config}/fish/conf.d/aspire-completions.fish`, and PowerShell
uses `$PROFILE.CurrentUserAllHosts`. Entries are marked `Aspire CLI completions`.

`--skip-path` / `-SkipPath` and archive dogfood installs only generate an artifact
under `<CLI-directory>/completions`, with manual activation instructions. Package-manager
dogfood modes do not register profiles. Automatic writes are limited to user-home
locations; redirected/symlinked profiles, elevated installs, unsupported shells,
older PowerShell engines, and signed profiles can require manual setup. `AllSigned`
policy leaves generated PowerShell files untouched as well. Installers report the
reason and do not change execution policy. Older CLIs without the generation command
leave working completion files intact.

Shell functions cannot be installed into a parent process by its child installer.
Open a new shell or explicitly source the generated file to activate registration.
Reinstalling custom scripts updates their generated files without duplicating
registration. Removing the CLI does not remove user-owned profile entries: remove
the Aspire source block and generated completion file as well, without deleting
the rest of your profile. Saved scripts query the current CLI dynamically; regenerate
them when upgrading to a version that changes the shell integration protocol.

Local .NET tools (`dotnet aspire` / `dotnet tool run aspire`) and `npx` / `npm exec`
are different outer commands and are **not** completed by these hooks. A local npm
setup must remain scoped to a session with the intended `node_modules/.bin` on PATH.
Generating a script through a local-tool command does not add `aspire` to the parent
shell or register completion for `dotnet`.

Homebrew uses `etc/bash_completion.d/aspire`, `share/zsh/site-functions/_aspire`,
`share/fish/vendor_completions.d/aspire.fish`, and
`share/pwsh/completions/_aspire.ps1` under `brew --prefix`. Configure your shell's
Homebrew completion paths/loader as described in
[Homebrew's shell completion documentation](https://docs.brew.sh/Shell-Completion).
For PowerShell, explicitly dot-source the generated file in your chosen profile:

```powershell
. (Join-Path (brew --prefix) 'share/pwsh/completions/_aspire.ps1')
```

Homebrew removes its files on uninstall, but not your profile entry. Its native
completion generator has no completion-specific opt-out; custom installer flags
do not apply to Homebrew. No installer should change machine-wide profiles or
shell execution policies.

## Additional documentation

* [CLI output formats](../../docs/specs/cli-output-formats.md)
* https://aspire.dev
* https://learn.microsoft.com/microsoft/aspire

## Feedback & contributing

https://github.com/microsoft/aspire
