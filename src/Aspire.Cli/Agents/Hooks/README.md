# Telemetry hooks

The Aspire CLI owns telemetry classification and delivery. Agent clients
registered by `aspire agent init` invoke `aspire agent telemetry --hook`
directly. Plugins provide skills and references; they do not register
telemetry hooks.

`aspire doctor` warns about user-level hooks that still invoke legacy telemetry
scripts and recommends `aspire agent init --agents <client>` to refresh them.

The skills and references that may be reported are listed in
[`telemetry-catalog.json`](telemetry-catalog.json), which is embedded in the
CLI and read by `AgentTelemetryCatalog`:

```json
{
  "skills": [ "aspire", "aspire-init" ],
  "references": [ "aspire-init/references/templates.md" ]
}
```

Each property is required exactly once and must be a non-empty, ordinally
sorted array of unique strings. References are skill-relative paths under a
listed skill's `references/` directory. Both the hook classifier and
`aspire agent telemetry` check values against the catalog, so legacy hook
scripts installed by older CLIs cannot report unlisted values.
`AgentTelemetryCatalogTests` validate parsing, and `AgentTelemetryHookTests`
exercise the embedded catalog.

First-party MCP tool names come from the CLI's
[`KnownMcpTools.All`](../../Mcp/KnownMcpTools.cs), not the catalog file.
Update the catalog by hand when skills or references change. The aspire-skills
[`update-aspire`](https://github.com/microsoft/aspire-skills/blob/dev/.github/workflows/update-aspire.md)
release workflow still targets the legacy hook scripts and their
allowlists, which the CLI no longer ships, so
it does not regenerate this file.

See [Agent usage telemetry](../../../../docs/specs/aspire-product-telemetry.md#agent-usage-telemetry)
for registration, legacy hook handling, classification, privacy, and delivery
behavior.
