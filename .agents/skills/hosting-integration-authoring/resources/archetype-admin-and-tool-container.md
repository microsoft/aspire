# Archetype: admin and tool container integration

Use this archetype for local development tools backed by containers: admin UIs, inspectors, dashboards, test/load tools, and other utility containers. These resources may be standalone tools, singleton helpers shared by several resources, or companions attached to a parent service.

Examples:

- Database admin UIs such as Adminer, DbGate, PgAdmin, Mongo Express, RedisInsight.
- Inspectors and utility UIs such as MCP Inspector, Swagger-like viewers, dashboards, or protocol explorers.
- Test/load tools such as k6 when they are modeled as resources.

## Distinguish tool containers from service dependencies

DO:

- Decide whether the tool is a parent-scoped companion or a standalone/singleton utility.
- Use `With{Tool}` on the parent when the tool is meaningful only for that parent and can be preconfigured from the parent endpoint/credentials.
- Use top-level `Add{Tool}` when the tool is a shared singleton, can connect to many resources, or is useful independently of one parent.
- Return the parent builder from parent-scoped `With{Tool}` helpers.
- Return the tool builder from top-level `Add{Tool}` helpers.
- Add dashboard URLs with clear display text for tools users are expected to open.
- Hide implementation-only or details-only URLs when the tool is not a primary entry point.

DON'T:

- Don't model a tool container as a service dependency unless workloads are supposed to consume it through `WithReference`.
- Don't force all admin UIs into parent-scoped APIs; singleton admin tools are valid when one resource instance can serve multiple backing services.
- Don't include development-only tools in publish/deploy output by default.

## Manifest and lifecycle

DO:

- Call `.ExcludeFromManifest()` for run-only admin/dev tools.
- If a tool participates in publish/deploy, document why and test the generated output.
- Avoid duplicate singleton tools by checking for an existing resource and returning its builder when repeat calls are intentional.
- Make optional tools opt-in; adding a database or broker should not implicitly add heavy UI/tool containers unless that is an established convention for the integration.

DON'T:

- Don't make a dev tool a required dependency of the primary service.
- Don't let repeated `Add{Tool}` or `With{Tool}` calls create duplicate singleton resources unless multiple instances are intentionally supported.

## Preconfiguration

DO:

- Preconfigure parent-scoped tools with deferred endpoint, credential, database, broker, or cluster values when the tool image supports it.
- Preserve secret handling with `ParameterResource` and `ReferenceExpression`; avoid resolved secret strings in environment variables until runtime evaluation.
- Sanitize generated environment variable names or connection identifiers according to the target container's documented constraints.
- Document what the tool can and cannot auto-discover.

DON'T:

- Don't leak parent credentials into logs, generated files, or manifests.
- Don't assume Aspire resource names are valid as container-specific IDs without normalization.

## Health checks

DO:

- Add a health check when readiness affects dependent resources or the dashboard should reflect tool health.
- Keep display-only tool health checks lightweight.

DON'T:

- Don't require deep protocol health checks for tools that are purely optional UI surfaces and have no dependents.
- Don't block a primary service on an optional admin tool unless the user explicitly requested that dependency.
