# Agent setup

`AgentInitCommand` resolves workspace, independent asset choices, and logical
client selections before calling `IAgentInitService`. Discovery returns immutable
client evidence; it does not register applicators, migrate settings, or install
tools. See the [CLI usage guide](../README.md#ai-agent-setup) for the flags and
defaults.

`AgentClient` is an immutable record containing the client ID, display name, and
`IAgentClientEnvironment` implementation. `AgentClientCatalog` constructs the
entries and exposes a read-only `Clients` list. The record is declared alongside
the catalog, with no static instances, client-kind enum, or descriptor mapping.

The catalog does no discovery, configuration, or I/O. `AgentInitCommand` groups its
entries by environment and invokes read-only scans, passing the matching entries,
working directory, and workspace root directly. There is no separate detector
wrapper. After selection, `AgentInitService` uses each selected client's
`Environment` to request edits, including for clients that were not detected.

Each `*AgentEnvironmentScanner` owns both discovery and native configuration in
its client namespace. Copilot CLI/App share one scanner, invoked once per phase.
The environment interface has no client list, and scanners do not repeat selection
dispatch. The shared writer applies their edits; hook targeting remains
detection-based.

## Native configuration

`AgentConfigurationWriter` groups edits by physical file and entry identity;
its grouping and read snapshots are implementation details, not separate services.
Project and user targets are automatic; selecting multiple frontends does not
duplicate a shared target.

| Client | Aspire source targets | MCP targets |
|--------|-----------------------|-------------|
| Copilot CLI / App | `.github/copilot/settings.json` and `settings.json` under the native Copilot configuration directory (`COPILOT_HOME` supported) | Shared project `.github/mcp.json` or an applicable root `.mcp.json`, plus user `mcp-config.json` |
| VS Code | The shared Copilot registration for the supported Copilot-backed runtime | Project `.vscode/mcp.json` and supported native user/profile MCP locations, with stable/Insiders and path overrides respected |
| Claude Code | Project `.claude/settings.json` and user settings, honoring `CLAUDE_CONFIG_DIR` | Project `.mcp.json` and native user `.claude.json` |
| OpenCode | Existing project/user `opencode.json` or `opencode.jsonc`, or the documented defaults | The same selected OpenCode configuration files |

Copilot and Claude registration merges the `aspire-skills` marketplace for
`microsoft/aspire-skills` and the `aspire@aspire-skills` enabled entry. It does not
write `installedPlugins`, opt into automatic updates, resolve refs, or invoke
native plugin acquisition. Compatible existing pins and disabled choices remain
authoritative. Known policy conflicts are reported without editing managed
settings; effective activation that cannot be determined offline is client-owned.

OpenCode uses one coherent format per target: V1 `skills.urls` and `mcp.<name>`,
or V2 `skills` arrays and `mcp.servers.<name>`. Version/configuration evidence
selects the format. An explicitly selected undetected client with no configuration
uses stable V1. Conflicting evidence is blocked rather than converted.
V1 Aspire skill catalogs require OpenCode **1.18.31 or later**. Detected older V1
versions, including 1.18.31 prereleases, block only Aspire source registration;
native MCP configuration remains available. The catalog publication gate below
still applies.

Deprecated MCP prefix repair preserves trailing arguments and environment values.
If an existing scope has customizations such as `--verbose` or custom environment
variables, an absent counterpart scope is intentionally skipped rather than given
defaults that could shadow or broaden those settings. This is an advisory warning,
not a core failure.

The shared writer accepts JSONC and trailing commas, validates container shapes,
reads current settings at application time, preserves unrelated values, and
avoids unchanged writes. Formatting and comments need not survive a changed-file
serialization. Conflicts and concurrent changes must not overwrite a target.
Core target failures are reflected in the command exit code alongside any
successful independent targets.

## Managed skills and hooks

Playwright remains a verified npm acquisition: acquire once, generate once in an
isolated workspace, then distribute the complete skill to deduplicated selected
project/user destinations. dotnet-inspect remains a static bootstrap skill; no
AppHost or language check is needed to select it.

Usage hooks apply to detected supported clients independently of native client
selection and Aspire source/MCP configuration outcomes, including during
Playwright-only or dotnet-inspect-only setup. An explicitly selected undetected
client does not receive a hook. Copilot CLI/App share one effective user hook;
there is no invented VS Code or OpenCode hook schema. Disabling all assets or
selecting no clients still performs no writes. Hook policies and existing hooks
remain authoritative, and failures are best-effort warnings with qualified
completion output. Preserve the embedded scripts' existing events and
`ASPIRE_CLI_TELEMETRY_OPTOUT` behavior.

## Telemetry hook maintenance

See [Telemetry hook maintenance](Hooks/README.md) for canonical source provenance,
synchronization, and verification. Agent setup uses the embedded scripts without
fetching them at runtime.

## Delivery gate

The Aspire skills bundle runtime, embedded archive/metadata, and archive
maintenance have been removed. Existing user skills and caches are not deleted or
migrated into native client-owned caches.

[microsoft/aspire-skills#72](https://github.com/microsoft/aspire-skills/issues/72)
must be complete and the real `0.0.3` release must be published before this
transition can merge. Synchronize and verify the actual released-main commit,
canonical hook hashes, and OpenCode V1/V2 catalog publication together.
Registration is offline and does not establish that an endpoint has been
published. Do not relabel existing provenance, substitute illustrative URLs, or
report this gate as complete from isolated fixtures. This merge gate does not
add a minimum release version or legacy bundle fallback to hook maintenance.

## Regression coverage

Command tests inject `TestAgentInitService` and the shared environment fake rather
than touching host settings. Shared CLI test defaults represent one deterministic
Copilot CLI detection; explicit empty-detector tests enforce the unattended
`--clients` requirement. Client definitions, safe merges, managed payloads, and hook
behavior have their own isolated tests.

`AgentCommandTests` exercises the existing Linux-container/Hex1b flow with native
source assertions, explicit unattended selections, idempotency, and the
standalone-only MCP boundary. `NewWithAgentInitTests` retains its network-heavy
outerloop classification for real Playwright provenance verification. See the
[E2E guide](../../../tests/Aspire.Cli.EndToEnd.Tests/README.md#agent-setup-coverage).
