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

### Experimental macOS tray companion

The native macOS CLI bundle can include the Aspire menu bar companion:

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
`tray/Aspire Tray.app/Contents/MacOS/aspire-tray`. Starting it passes the absolute
invoking CLI executable and the leased version directory; it never copies a
private CLI, searches `PATH`, or falls back to a checkout-relative executable.
The CLI holds its bundle lease until the helper exits, and the native GUI holds
its own lease for its lifetime.

These commands are experimental and macOS-only. Starting the companion requires
a native CLI, not a managed development build or `dotnet aspire.dll`. A missing
bundle or tray payload fails explicitly; install a macOS bundle containing the
companion rather than using a standalone CLI binary.

Before upgrading from an older preview, quit its running companion using its
**Quit** menu action. Preview single-instance identifiers have changed, so
`aspire tray stop` in this version does not manage an older preview's instance.

### Experimental native tray protocol

The hidden `--protocol-version 1` opt-in provides complete discovery snapshots,
heartbeats, and typed, lifetime-guarded stop results for the experimental native tray companion:

```bash
aspire ps --protocol-version 1 --follow --format json --non-interactive --nologo
aspire stop --protocol-version 1 --format json --apphost /absolute/path/AppHost.csproj --pid 12345 --started-at 1789250000000 --non-interactive --nologo
```

Pass `--started-at` from the selected row's `processStartTimeUnixMilliseconds`;
do not enable Stop if that value is unavailable. `--started-at` requires
`--protocol-version 1 --format json`; incomplete requests are rejected rather
than falling back to legacy PID-only stopping. This experimental mode uses
protocol-only stdout and read-only discovery. It does not change the existing
unversioned JSON formats. See the [protocol schema, limits, and outcomes](../../docs/specs/cli-output-formats.md#experimental-native-tray-protocol-version-1).

## Additional documentation

* [CLI output formats](../../docs/specs/cli-output-formats.md)
* https://aspire.dev
* https://learn.microsoft.com/microsoft/aspire

## Feedback & contributing

https://github.com/microsoft/aspire
