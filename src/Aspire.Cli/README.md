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

## AI agent setup

Run `aspire agent init` to choose assets first, then the clients that should use
them. An AppHost is not required, including when selecting dotnet-inspect.

| Option | What Aspire configures | Default |
|--------|------------------------|---------|
| `--mcp` | The Aspire MCP server in the selected clients' native configuration | No |
| `--playwright` | Verified Playwright CLI installation and its generated skill | No |
| `--dotnet-inspect` | The bootstrap skill that invokes dotnet-inspect on demand | No |
| `--aspire-skills` | Native Aspire plugin/catalog source registration and supported enablement | Yes |
| `--clients` | `copilot-cli`, `copilot-app`, `vscode`, `claude-code`, or `opencode` | Detected clients |

The asset flags accept `y`/`n` or `true`/`false`, case-insensitively. A bare flag
means yes. Omitted assets prompt interactively and use the defaults above in
non-interactive mode. The client picker includes all five clients, with detected
clients preselected; explicitly selecting an undetected client is supported.
`--clients` accepts comma-separated IDs, `all`, or `none`, case-insensitively.
Unattended setup with requested assets and no detected clients requires an explicit
`--clients` value.

```bash
# Register the native Aspire source for both Copilot frontends, without installing either client
aspire agent init --non-interactive --clients copilot-cli,copilot-app

# Configure only MCP for Claude Code; Aspire source registration is independently optional
aspire agent init --non-interactive --clients claude-code --mcp y --aspire-skills n

# Install only the dotnet-inspect bootstrap skill, even before creating an AppHost
aspire agent init --non-interactive --clients vscode --dotnet-inspect --aspire-skills false

# Keep existing agent configuration untouched
aspire agent init --mcp n --playwright n --dotnet-inspect n --aspire-skills n
```

Project and user scopes are automatic: there is no skill-location or scope picker.
Project targets use `--workspace-root`, defaulting to the Git root or the current
directory outside Git. User targets use the clients' native configuration
locations and supported overrides. Shared Copilot targets are written once even
when multiple supported frontends are selected.

`--aspire-skills` registers the official `microsoft/aspire-skills` source; it does
not download a skills archive, install a native plugin, or populate a client cache.
The selected client acquires, trusts, and loads the content. Canvases are available
only in clients that support them. “Registered” and “already configured” therefore
do not mean “installed” or “loaded.” Playwright and dotnet-inspect are the only
skill payloads managed by Aspire itself.

An explicit no leaves existing assets in place; it does not uninstall or disable
them. Disabling all assets skips client discovery and configuration.
`--clients none` skips configuration, including hooks and deprecated MCP-command migration.
Migration runs only for selected MCP targets when MCP is requested.

Settings merges preserve unrelated values, compatible source pins, and explicit
disabled choices. JSONC is accepted, but a changed document may be rewritten
without its comments or original formatting. Semantically unchanged settings
are not rewritten. Conflicting or malformed core targets are reported separately
and cause a nonzero exit code; independent targets may still succeed. Usage-hook
failures are advisory warnings, not unconditional setup success. Existing hooks
are not removed by an opt-out.

`aspire new` and `aspire init` offer the same non-MCP assets and client choices
after creating the project, unless `--suppress-agent-init` is specified. They
never expose or prompt for `--mcp`; use standalone `aspire agent init --mcp`
instead. Disabling agent setup does not suppress normal project creation.
After a successful native source registration, `aspire init` explains how to
request Aspireify once the selected client has acquired the content.

The old `--skills` and `--skill-locations` options are no longer accepted.
The hidden legacy `aspire mcp init` command delegates to the same setup flow and
shows a deprecation warning.

See [agent setup implementation and delivery gates](Agents/README.md) for native
target details and the outstanding hook-provenance and OpenCode publication
prerequisites.

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
