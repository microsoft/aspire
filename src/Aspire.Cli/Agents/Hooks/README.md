# Telemetry hooks

The Aspire CLI owns telemetry classification, delivery, and the embedded
`track-telemetry.sh` and `track-telemetry.ps1` compatibility scripts. Plugins
provide skills and references; they do not register telemetry hooks.

The release-driven
[Aspire skills update workflow](https://github.com/microsoft/aspire-skills/blob/dev/.github/workflows/update-aspire.md)
reviews released skills source and proposes telemetry code, script, and test
changes in this repository. Updates ship through normal Aspire pull requests
and CLI releases, without relying on Aspire-skills bundles or caches.

Keep the managed classifier and both scripts behaviorally aligned, with focused
tests for recognized skills, references, and first-party MCP tools.

See [Agent usage telemetry](../../../../docs/specs/aspire-product-telemetry.md#agent-usage-telemetry)
for registration, classification, privacy, and delivery behavior.
