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

## Runtime values in callbacks

Any callback that reads allocated endpoints, host ports, local file paths generated at run time, container IDs, or process state must branch on publish mode.

In publish mode, use a `ReferenceExpression`, manifest expression, environment placeholder, Bicep output, compose variable, or deployment model reference instead.
