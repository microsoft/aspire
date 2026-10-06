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

### Agent setup

`aspire agent init` registers native Aspire plugin/catalog sources without downloading plugins. Marketplace policies are checked against the source actually registered, including its source type, pin, and path. The shared project `.mcp.json` is not evidence that Claude Code is installed.

`aspire doctor` checks for local Aspire skill files along the active directory's ancestor chain within the workspace. `aspire update --migrate` uses the selected AppHost's Git root, or the nearest `.sln`/`.slnx` directory outside Git, falling back to the selected directory when neither exists. Sibling projects are not scanned, and migration preserves existing local skill files.

Legacy installs have no reliable ownership/version receipt, so discovery reports possible conflicts without classifying files as CLI-owned or outdated. Automatic retirement is intentionally unsupported. After verifying the registered content in each client, review duplicate local skills and supporting files manually; retain customizations and intentional standalone installs.

Changed agent configuration and managed skill files are published atomically. Replacement preserves the destination DACL on Windows and mode bits on Unix. Unix ownership, custom ACLs, and extended attributes are not preserved; files relying on that metadata should be configured manually instead.

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

## Additional documentation

* [CLI output formats](../../docs/specs/cli-output-formats.md)
* https://aspire.dev
* https://learn.microsoft.com/microsoft/aspire

## Feedback & contributing

https://github.com/microsoft/aspire
