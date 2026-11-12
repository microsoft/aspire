# Telemetry hooks

The Aspire CLI owns telemetry classification and delivery. Agent clients
registered by `aspire agent init` invoke `aspire agent telemetry --hook`
directly. Plugins provide skills and references; they do not register
telemetry hooks.

The skills and references that may be reported are listed in
[`telemetry-catalog.json`](telemetry-catalog.json), which is embedded in the
CLI and read by `AgentTelemetryCatalog`:

```json
{
  "skills": [ "aspire", "aspire-init" ],
  "references": [ "aspire-init/references/templates.md" ]
}
```

Each property is required and must be a non-empty, ordinally sorted array of
unique strings. References are skill-relative paths under a listed skill's
`references/` directory. `AgentTelemetryCatalogTests` validate parsing, and
`AgentTelemetryHookTests` exercise the embedded catalog.

First-party MCP tool names come from the CLI's
[`KnownMcpTools.All`](../../Mcp/KnownMcpTools.cs), not the catalog file.
The [aspire-skills](https://github.com/microsoft/aspire-skills) repository will
regenerate the catalog deterministically from `skills/*` and
`skills/*/references`, without an agentic workflow.

See [Agent usage telemetry](../../../../docs/specs/aspire-product-telemetry.md#agent-usage-telemetry)
for registration, legacy hook handling, classification, privacy, and delivery
behavior.
