---
applyTo: "src/Components/**/*.cs"
---

# Client Integration Review Patterns

- Prefer reusing the DI-registered client for health checks when the health check library supports it; if it does not, justify any separate client/connection.
- Standard pattern: bind the integration section, then the named subsection, then override from `ConnectionStrings:{name}`, then invoke `configureSettings` last. If a component diverges, make the exception explicit.
- Do not throw for a missing connection string before the `configureSettings` delegate runs — the user may supply connection info programmatically.
- Client-side extension methods (on `IHostApplicationBuilder`) must have names distinct from AppHost-side methods (on `IDistributedApplicationBuilder`) — e.g., `AddRedisClient` vs `AddRedis`.
- For components round-tripping through AppHost `WithReference()`, all required connection details should fit in `ConnectionStrings:{name}` or be derivable from it.
- Use `exclusionPaths` when the generated `ConfigurationSchema` would expose misleading or non-bindable members (for example Azure client-option `Default` singletons or certificate-context members).
