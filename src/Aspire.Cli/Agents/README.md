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

## Telemetry hook maintenance

The two embedded hook scripts are synchronized directly from
`microsoft/aspire-skills`, independently of skill/plugin distribution.
`Hooks/telemetry-hooks.metadata.json` records their released version, immutable
main commit, and LF-normalized UTF-8 SHA-512 hashes. Agent setup never fetches the
scripts at runtime.

The `Update Telemetry Hooks` workflow accepts required `source_commit` and
`version` inputs through `workflow_dispatch`. It has no daily bundle-refresh
schedule. The source must be on main's first-parent lineage, and its canonical
plugin version must match the requested version. A dev input SHA merged through a
release branch is not the released-main SHA.

The updater fetches and validates both scripts before publishing either script or
metadata. Replays are idempotent, and older/divergent sources cannot replace a
newer synchronization. Serialized workflow runs also check metadata on an
existing `update-telemetry-hooks` PR branch, so delayed dispatches cannot overwrite
a newer unmerged update.

`Verify Telemetry Hooks` requires complete metadata and matches the local scripts'
normalized hashes to both metadata and canonical source at the pinned commit.
Missing data, malformed provenance, failed fetches, or mismatches fail verification.
No skill archive or archive attestation is involved.

For a local replay:

```powershell
.\eng\scripts\update-telemetry-hooks.ps1 -SourceCommit "<released-main-sha>" -Version "<release-version>"
.\eng\scripts\verify-telemetry-hooks.ps1
```

The workflow creates a draft PR on `update-telemetry-hooks`, matching the protected
script branch guard. The branch-name guard is not proof of bot identity; canonical
source verification remains independent. Deploy this receiver before enabling
upstream release notifications. The upstream sender and dispatch credentials
remain tracked by [#20246](https://github.com/microsoft/aspire/issues/20246).

## Delivery gate

The Aspire skills bundle runtime, embedded archive/metadata, and archive
maintenance have been removed. Existing user skills and caches are not deleted or
migrated into native client-owned caches.

[microsoft/aspire-skills#72](https://github.com/microsoft/aspire-skills/issues/72)
must publish and verify the real released-main OpenCode V1/V2 catalogs before
shipping that registration path. Registration is offline and does not establish
that an endpoint has been published. Do not substitute illustrative URLs or
report this publication gate as complete from isolated fixtures.

## Regression coverage

Command tests inject `TestAgentInitService` and the shared detector fake rather
than touching host settings. Shared CLI test defaults represent one deterministic
Copilot CLI detection; explicit empty-detector tests enforce the unattended
`--clients` requirement. Native handlers, safe merges, managed payloads, and hook
behavior have their own isolated tests.

`AgentCommandTests` exercises the existing Linux-container/Hex1b flow with native
source assertions, explicit unattended selections, idempotency, and the
standalone-only MCP boundary. `NewWithAgentInitTests` retains its network-heavy
outerloop classification for real Playwright provenance verification. See the
[E2E guide](../../../tests/Aspire.Cli.EndToEnd.Tests/README.md#agent-setup-coverage).
