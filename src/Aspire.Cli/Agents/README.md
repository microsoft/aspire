# Agent setup

`AgentInitCommand` resolves workspace, independent asset choices, and logical
client selections before calling `IAgentInitService`. Discovery returns immutable
client evidence; it does not register applicators, migrate settings, or install
tools. See the [CLI usage guide](../README.md#ai-agent-setup) for the flags and
defaults.

## Native configuration

The configuration service groups mutations by physical file and entry identity.
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
not a core failure. A successfully repaired project MCP target still qualifies
the selected supported client for its single user-level usage hook.

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

Usage hooks apply only to selected supported clients with successful or unchanged
Aspire source/MCP configuration. Copilot CLI/App share one effective user hook;
there is no invented VS Code or OpenCode hook schema. Playwright-only and
dotnet-inspect-only setup does not need Aspire usage hooks. Hook failures are
best-effort warnings with qualified completion output. Preserve the embedded
scripts' existing events and `ASPIRE_CLI_TELEMETRY_OPTOUT` behavior.

## Delivery gates

The Aspire skills archive acquisition, cache, attestation client, providers,
serialization models, location/skill selectors, and their configuration knobs
have been removed from the production runtime. Agent setup registers native
sources instead. The raw embedded archive and metadata are retained **only for
hook verification and provenance maintenance**, not as a runtime fallback.

- [microsoft/aspire#20246](https://github.com/microsoft/aspire/issues/20246) must
  decouple hook verification/provenance before deleting those remaining raw
  inputs or retiring their maintenance/verifier workflow. It does not block
  removal of the obsolete runtime. Canonical hook scripts and the maintenance
  source/attestation verifier scripts and workflow remain unchanged.
- [microsoft/aspire-skills#72](https://github.com/microsoft/aspire-skills/issues/72)
  must publish and verify the real released-main OpenCode V1/V2 catalogs before
  shipping that registration path. Registration is offline and does not establish
  that an endpoint has been published. Do not substitute illustrative URLs or
  report this publication gate as complete from isolated fixtures.

Existing user skills and caches are not deleted or migrated into native
client-owned caches.

`TelemetryHookArchiveReader` is test-only and reads Tar/GZip streams without disk
extraction. It verifies the full archive SHA-512 against retained metadata before
reading entries; rejects duplicate, missing, or nonregular expected manifest/hook
entries; and checks complete hook names, matching commit identities, and both
manifest and metadata SHA-512 hashes. The installer test also compares installed
and archived LF-normalized UTF-8 bytes and both per-hook hashes. The updater takes
its current version from metadata and no longer stamps a runtime installer class.

## Regression coverage

Command tests inject `TestAgentInitService` and the shared detector fake rather
than touching host settings. Shared CLI test defaults represent one deterministic
Copilot CLI detection; explicit empty-detector tests enforce the unattended
`--clients` requirement. Native handlers, safe merges, managed payloads, and hook
behavior have their own isolated tests.

`AgentCommandTests` exercises the existing Linux-container/Hex1b flow with native
settings snapshots, explicit unattended selections, idempotency, and the
standalone-only MCP boundary. `NewWithAgentInitTests` retains its network-heavy
outerloop classification for real Playwright provenance verification. See the
[E2E guide](../../../tests/Aspire.Cli.EndToEnd.Tests/README.md#agent-setup-coverage).
