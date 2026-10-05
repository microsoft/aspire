# Archetype: container-backed service integration

Use this archetype for primary local service dependencies backed by containers: databases, caches, brokers, vector databases, search engines, object stores, and similar infrastructure that workloads reference with `WithReference`.

Representative examples:

- `src/Aspire.Hosting.MongoDB/MongoDBBuilderExtensions.cs`
- `src/Aspire.Hosting.MongoDB/MongoDBServerResource.cs`
- `src/Aspire.Hosting.PostgreSQL/PostgresBuilderExtensions.cs`
- `src/Aspire.Hosting.Kafka/KafkaServerResource.cs`

## Resource shape

Primary resources usually derive from `ContainerResource` and implement `IResourceWithConnectionString` when they can be referenced by apps.

Child resources derive from `Resource`, implement `IResourceWithParent<TParent>`, and often implement `IResourceWithConnectionString`.

This archetype is not the right primary guide for every container:

- Admin UIs and development tools should also read `archetype-admin-and-tool-container.md`.
- Local tunnels and webhook forwarders should read `archetype-tunnel-and-webhook-bridge.md`.
- Sidecars, telemetry collectors, and middleware should read `archetype-sidecar-and-middleware.md`.
- One-shot migration/setup containers should read `archetype-setup-and-migration-helper.md`.

## Builder pattern

DO:

- Add the primary resource with `Add{Technology}`.
- Validate builder and name.
- Create default credentials as `ParameterResource`s.
- Verify the container image actually consumes every generated credential you expose in connection strings or connection properties, for example through required command-line flags, environment variables, config files, or init scripts.
- When a container command relies on shell features such as environment-variable expansion, invoke the shell correctly, for example `/bin/sh -c 'exec service --password "$PASSWORD"'`; otherwise pass arguments directly without expecting shell expansion.
- Register health checks.
- Make health checks prove the readiness consumers need. A plain TCP-port check is not sufficient for services that open sockets before they can complete the service protocol, authenticate with configured credentials, or accept real client operations.
- Add the resource with `builder.AddResource(resource)`.
- Configure endpoint, image, registry, environment, icon, volumes, and health check in the returned chain.
- Use stable endpoint names, commonly `"tcp"` for protocol endpoints and `"http"` for HTTP UIs.
- Use separate host and internal endpoints when container-to-container connectivity differs from host-process connectivity.

DON'T:

- Don't hardcode host ports by default; accept `int? port = null` and let Aspire allocate.
- Don't inline image tags in fluent chains; use a container image tags class.
- Don't create random passwords directly; use `CreateDefaultPasswordParameter`.
- Don't add a password/API-key parameter to the connection string unless the underlying service is configured to require that credential.
- Don't resolve connection strings before `ConnectionStringAvailableEvent`.

## Connection strings

DO:

- Build connection strings with `ReferenceExpressionBuilder`.
- Encode credentials and database names with URI formatting.
- Expose `Host`, `Port`, `Username`, `Password`, `Uri`, and database-specific properties as applicable.
- For child database resources, compose parent properties and override `Uri`/database values.

DON'T:

- Don't concatenate plain secret values.
- Don't make child resources guess parent endpoint/auth details.

## Initialization

Use `OnResourceReady` for side effects that require the service to be reachable, such as creating databases, queues, topics, or containers.

Use `ConnectionStringAvailableEvent` to create clients or capture resolved connection strings.

If a child resource is metadata-only and does not create provider state, implement `IResourceWithoutLifetime` and document that the API only supplies reference metadata. Otherwise, create or verify the child state from `OnResourceReady` after the parent service is healthy.

Do not perform service-state creation in health checks.

## Volumes and init files

DO:

- Provide `WithDataVolume` for named Docker volumes.
- Provide `WithDataBindMount` when host paths are a legitimate scenario.
- Use `VolumeNameGenerator.Generate(builder, "data")` for default names.
- Prefer `WithInitFiles` over obsolete bind-mount patterns for initialization scripts/files.
- Document standard container paths in XML docs and README when user-facing.

DON'T:

- Don't hand-roll default volume names.
- Don't assume all data mounts are writable; expose `isReadOnly` where useful.

## Admin companions

Admin UIs such as PgAdmin, Mongo Express, RedisInsight, or Kafka UI should be optional `With{Tool}` methods on the parent resource.

They should add a companion container, configure endpoint/environment, add a parent/custom relationship, call `.ExcludeFromManifest()`, and return the original parent builder.

Companions must be preconfigured to connect to the parent service when the image supports it. Include host, internal target port, credentials, and database/topic/cluster names required by the companion image, using deferred `ReferenceExpression`/parameters rather than resolved secret values where possible.

If enabling a built-in admin UI requires changing the container image tag, preserve the user's version and variant whenever possible. Throw for digest-pinned images or tags that cannot be safely mapped; do not silently clear `SHA256` pins or downgrade to a default tag.
