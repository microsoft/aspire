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

To initialize an empty C# AppHost without discovering incidental `.sln` or `.slnx` files, run this from the repository root:

```bash
aspire init --file-based --language csharp
```

This creates `apphost.cs` and its supporting configuration in the current directory instead of creating a solution-based AppHost project. `--file-based` requires C#: it reports an error before scaffolding if another language is selected explicitly, configured, or chosen at the language prompt. Omit `--file-based` (or pass `--file-based false`) to use normal non-C# scaffolding. It does not overwrite existing AppHosts or suppress agent setup.

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

## Stopping a specific AppHost instance

Use the AppHost PID reported by `aspire ps` (not its launcher CLI PID) to stop one
instance, including an instance outside the current working directory or worktree:

```bash
aspire stop --pid 12345
aspire stop --apphost /absolute/path/AppHost.csproj --pid 12345 --non-interactive --nologo
```

`--pid` requires a positive integer. When combined with `--apphost`, the full
project or file path must match the connected AppHost's path exactly (case-insensitive
on Windows); directories are not searched. A missing instance, path mismatch, or
ambiguous connection fails with a nonzero exit code without selecting another instance.

Instance-targeted stops use only the selected live backchannel and wait for that
AppHost to exit. They do not stop sibling instances of the same project, clean up
orphaned sockets or persistent resources, or fall back to killing process trees.
An unavailable stop RPC or shutdown timeout fails without escalation. `--pid`
cannot be combined with `--all` or `--force`. Without `--pid`, existing project-level
stop behavior is unchanged.

### Experimental macOS and Windows tray companion

The native macOS and Windows CLI bundles include the experimental Aspire menu
bar or system tray companion:

```bash
aspire tray start
aspire tray stop
```

`start` starts the companion or restores its existing icon, and returns only after
the native UI is ready and protects its bundle version with its own lease.
`stop` requests and acknowledges graceful companion shutdown; it does not stop
user AppHosts. Both helper invocations have a 30-second deadline.
Cancellation is honored before startup, but once the start helper launches, a
first Ctrl+C waits for its bounded readiness and lease handoff rather than killing
it prematurely. The command then reports the helper's result.

The companion comes from the leased CLI bundle at
`tray/Aspire Tray.app/Contents/MacOS/aspire-tray` on macOS, or
`tray/aspire-tray.exe` alongside `tray/Aspire.ico` on Windows. Starting it passes the absolute
invoking CLI executable and the leased version directory; it never copies a
private CLI, searches `PATH`, or falls back to a checkout-relative executable.
The CLI holds its bundle lease until the helper exits, and the native GUI holds
its own lease for its lifetime. If readiness fails, the helper terminates and
waits for its newly launched GUI process before releasing the launcher lease;
an already-running companion is not terminated by a failed start.

These commands are experimental and available on macOS and Windows (x64/ARM64).
Starting the companion requires an interactive desktop and
a native CLI, not a managed development build or `dotnet aspire.dll`. A missing
bundle or tray payload fails explicitly; install a platform-matching bundle containing the
companion rather than using a standalone CLI binary.

Before upgrading from an older preview, quit its running companion using its
**Quit** menu action. Preview single-instance identifiers have changed, so
`aspire tray stop` in this version does not manage an older preview's instance.

### AppHost snapshot output

`aspire ps --output <default|snapshot>` selects the output mode. The default
preserves existing output. Monitoring tools, including the native tray, can use
`snapshot` with `--follow --format json` for complete AppHost lists, process
identities, aggregate resource health, heartbeats, and typed errors:

```bash
aspire ps --output snapshot --follow --format json --non-interactive --nologo
```

Snapshot discovery is read-only and emits only NDJSON on stdout. Messages include
`version: 1` for compatibility; callers do not select a protocol version.
See the [snapshot output contract](../../docs/specs/cli-output-formats.md#snapshot-output)
for fields, empty snapshots, health values, heartbeats, errors, and limits.

### Experimental native tray stop protocol

The tray's separate, hidden stop mode provides typed, lifetime-guarded results:

```bash
aspire stop --protocol-version 1 --format json --apphost /absolute/path/AppHost.csproj --pid 12345 --started-at 1789250000000 --non-interactive --nologo
```

Pass `--started-at` from the selected row's `processStartTimeUnixMilliseconds`;
do not enable Stop if that value is unavailable. `--started-at` requires
`--protocol-version 1 --format json`; incomplete requests are rejected rather
than falling back to legacy PID-only stopping. This experimental mode uses
protocol-only stdout and does not change ordinary stop behavior. See the
[exact stop contract and outcomes](../../docs/specs/cli-output-formats.md#experimental-exact-stop-response).

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

Bash and Zsh complete the current command after separators and preserve literal
arguments inside single or double quotes. Bash also decodes ANSI-C quotes such as
`$'a\tb'`, including their escape sequences. Input is decoded without evaluating
shell substitutions; accepting a suggestion must not execute its contents.
Settings used for completion are normalized in memory without rewriting the
configuration files.

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
| Homebrew cask | Homebrew owns generated Bash/Zsh/Fish scripts and a PowerShell loader. Shell activation remains user-controlled; see below. |

Custom release scripts keep generated files at
`$HOME/.aspire/completions/aspire.{bash,zsh,fish,ps1}`. Bash registration uses
`.bashrc` and the first existing login profile; a newly created `.bash_profile`
also sources `.bashrc`. Zsh registration uses `${ZDOTDIR:-$HOME}/.zshrc`, Fish uses
`${XDG_CONFIG_HOME:-$HOME/.config}/fish/conf.d/aspire-completions.fish`, and PowerShell
uses `$PROFILE.CurrentUserAllHosts`. Entries are marked `Aspire CLI completions`.

`--skip-path` / `-SkipPath` and archive dogfood installs only generate an artifact
under `<CLI-directory>/completions`, with manual activation instructions. The explicitly
selected CLI directory can be outside the user home; artifact writes remain confined
to that directory and reject redirection outside it. A link explicitly selected as
the installation root anchors the boundary at its target; links below that root
cannot redirect artifact writes elsewhere. Package-manager dogfood modes
do not register profiles. Automatic profile writes and persistent user completion
files remain home-confined; redirected/symlinked profiles, elevated installs, unsupported shells,
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

This PowerShell file is a loader that gets the current script from `aspire` when
sourced. Homebrew installs it as a managed artifact so fresh prefixes do not depend
on the completion generator's sandbox being able to create `share/pwsh`.

Homebrew removes its files on uninstall, but not your profile entry. Its native
completion generator has no completion-specific opt-out; custom installer flags
do not apply to Homebrew. No installer should change machine-wide profiles or
shell execution policies.

## Browser certificate trust configuration

On Linux, the NSS databases used to trust the Aspire development certificate can be configured locally or in the user-level Aspire configuration:

```bash
aspire config set --global certificates.nssDbPaths "firefox=/path/to/firefox/profile:chromium=/path/to/chromium/nssdb"
```

Prefix a path with `firefox=` or `chromium=` to apply the trust settings expected by that browser family.

The `certificates.nssDbPaths` setting takes precedence over the upstream `DOTNET_DEV_CERTS_NSSDB_PATHS` environment variable. When the Aspire setting has no value, the CLI preserves the upstream behavior.

## Additional documentation

* [CLI output formats](../../docs/specs/cli-output-formats.md)
* https://aspire.dev
* https://learn.microsoft.com/microsoft/aspire

## Feedback & contributing

https://github.com/microsoft/aspire
