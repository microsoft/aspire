# Eventing and initialization

Use lifecycle hooks based on what data is available and what side effects are safe.

## Hook selection

| Hook/location | Use for | Avoid |
| --- | --- | --- |
| Resource constructor | Store immutable app-model state only | Service provider access, connection resolution, file/network side effects |
| `WithEnvironment` callback | Populate environment variables from references | Resolving runtime-only values in publish mode |
| `OnConnectionStringAvailable` / `ConnectionStringAvailableEvent` | Create clients or cache resolved connection strings after references become resolvable | Creating databases/queues or other service state |
| `OnBeforeResourceStarted` / `BeforeResourceStartedEvent` | Prepare runtime clients or config before process/container start when connection string events are not available | Long-running service initialization that requires the service to be healthy |
| `OnResourceReady` / `ResourceReadyEvent` | Create databases, queues, containers, topics, models, or other child state after service health | Health checks, constructors, publish callbacks |
| Pipeline step | Publish/build/deploy validation, generated artifact checks, deployment target preparation | Local run-only setup unless guarded to run mode |

## Initialization rules

DO:

- Keep health checks side-effect-free.
- Make health checks match the readiness contract. Prefer protocol-level or client-level checks over raw port checks when consumers need the service protocol to be ready, and include configured credentials when authentication is part of client readiness.
- Create dependent service state in `OnResourceReady` after the parent is healthy.
- If a child resource only models reference metadata and does not create service state, mark it `IResourceWithoutLifetime` and document that it is metadata-only.
- Fail clearly when required connection strings or clients are unavailable.
- Derive user-facing pipeline exceptions from `DistributedApplicationException` when the pipeline should surface them without extra wrapping.
- Use separate run-mode setup sibling resources for commands like dependency restore, tool install, `go mod`, or virtual environment creation.
- Mark setup siblings `.ExcludeFromManifest()` and wire the main resource with `WaitForCompletion`.
- Add comments explaining non-obvious lifecycle ordering.

DON'T:

- Don't create databases, queues, containers, or topics inside health checks.
- Don't cache annotation callback results without an invalidation path if inputs can change on restart/retry.
- Don't cache a faulted task when inputs may change.
- Don't treat a null process exit code as success; null means unknown.
- Don't throw raw, cryptic exceptions from publish/deploy pipeline user errors.

## Final-model hooks and derived resources

Some integrations need the final app model before they can wire setup resources, sidecars, package-manager commands, or telemetry forwarding.

DO:

- Register final-model hooks only in the modes where they are needed.
- Register global lifecycle hooks or event subscriptions idempotently.
- Discover only resources that opted in through a specific annotation or interface.
- Materialize derived resources before startup when the target process needs sidecars, config, setup resources, or command rewrites.
- Propagate relevant waits and relationships from source resources to derived resources.

DON'T:

- Don't scan and mutate unrelated resources just because they implement a broad interface.
- Don't register duplicate global subscriptions when multiple fluent calls enable the same feature.

If multiple lifecycle hooks, commands, background probes, and resource operations must coordinate shared mutable state, use the controller/reconciler archetype instead of adding more independent event handlers. Read `archetype-controller-reconciler.md`.

## Custom lifecycle resources

Some integrations create facade resources whose status is driven by a parent resource or external service rather than by a DCP-started process/container. Read `custom-lifecycle-and-facade-resources.md` for the full pattern.

DO:

- Prefer normal resources first; manual lifecycle is an escape hatch.
- Drive facade status with `ResourceNotificationService` from the owning resource's lifecycle callbacks.
- Keep health checks observational, and let lifecycle callbacks perform create/update/delete side effects.

DON'T:

- Don't manually publish lifecycle events unless the facade resource has no normal DCP lifecycle and other components need those events.
- Don't leave manually managed resources in stale running states after their owner stops.

## Runtime values in callbacks

Any callback that reads allocated endpoints, host ports, local file paths generated at run time, container IDs, or process state must branch on publish mode.

In publish mode, use a `ReferenceExpression`, manifest expression, environment placeholder, Bicep output, compose variable, or deployment model reference instead.

## Runtime output parsing

Some tools expose required run-mode values only through stdout/stderr, such as webhook signing secrets or public tunnel URLs.

DO:

- Prefer structured APIs or files over log parsing when available.
- Keep output parsing run-only and cancellation-aware.
- Include a nearby comment with an example of the raw output shape being parsed.
- Fail clearly when a required value is not observed before the timeout/cancellation path.
- Redact parsed secrets in diagnostics.

DON'T:

- Don't parse logs during app-model construction.
- Don't treat missing required runtime output as success.
- Don't write parsed secrets or machine-local runtime values to publish/deploy artifacts.
