# Telemetry hooks

The Aspire CLI owns telemetry classification and delivery. Agent clients
registered by `aspire agent init` invoke `aspire agent telemetry --hook`
directly. Plugins provide skills and references; they do not register
telemetry hooks.

The skills, references, and first-party MCP tools that may be reported are
listed in [`telemetry-allowlist.json`](telemetry-allowlist.json), which is
embedded in the CLI and read by `AgentTelemetryCatalog`:

```json
{
  "skills": [ "aspire", "aspire-init" ],
  "references": [ "aspire-init/references/templates.md" ],
  "mcpTools": [ "doctor", "list_resources" ]
}
```

Each property is required and must be a non-empty, ordinally sorted array of
unique strings. References are skill-relative paths under a listed skill's
`references/` directory. `AgentTelemetryCatalogTests` and
`AgentTelemetryHookTests` validate the embedded file.

The release-driven
[Aspire skills update workflow](https://github.com/microsoft/aspire-skills/blob/dev/.github/workflows/update-aspire.md)
reviews released skills source and proposes allowlist, code, and test changes
in this repository. Updates ship through normal Aspire pull requests and CLI
releases, without relying on Aspire-skills bundles or caches.

See [Agent usage telemetry](../../../../docs/specs/aspire-product-telemetry.md#agent-usage-telemetry)
for registration, legacy hook handling, classification, privacy, and delivery
behavior.
