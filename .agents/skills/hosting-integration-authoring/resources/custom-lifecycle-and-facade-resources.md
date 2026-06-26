# Custom lifecycle and facade resources

Use this guidance when an integration resource is visible in the Aspire model but its lifecycle is not directly managed as a normal project, executable, or container instance.

Representative examples:

- A tunnel executable that hosts an external service session and creates synthetic child resources for each public forwarded endpoint.
- A local CLI bridge that creates remote state before the host process can start.
- A synthetic endpoint resource that exists only so other resources can reference a URL discovered at runtime.

## When this pattern is appropriate

Custom lifecycle resources are an escape hatch. Prefer normal `ProjectResource`, `ExecutableResource`, `ContainerResource`, setup sibling, or `IResourceWithoutLifetime` shapes first.

DO:

- Use a custom lifecycle/facade resource only when users need dashboard visibility, resource logs, endpoint references, waits, or service discovery for something that DCP does not manage directly.
- Keep the primary owner as a normal resource when possible, such as an `ExecutableResource` running the long-lived CLI.
- Model each externally addressable thing as a separate facade resource only when users can reason about it independently, such as a tunnel port, public callback URL, generated proxy endpoint, or inspection endpoint.
- Implement standard interfaces on facade resources, such as `IResourceWithEndpoints`, `IResourceWithServiceDiscovery`, or `IResourceWithWaitSupport`, instead of making consumers special-case concrete types.

DON'T:

- Don't create facade resources only to expose implementation details.
- Don't use manual lifecycle events when a normal resource plus annotations would work.
- Don't make a facade resource perform work from its constructor.

## Lifecycle ownership

The owner resource should drive lifecycle for its facades.

DO:

- Create external state in `OnBeforeResourceStarted` when the owner process needs that state before launch.
- Wait for target endpoint allocation before creating external endpoint/port state.
- Reconcile external child state to the app model if the integration owns it, for example deleting unmodeled forwarded ports.
- Use `OnResourceReady` on the owner to mark facade resources running after the owner is healthy and the external state is observable.
- Use `OnResourceStopped` on the owner to mark facade resources stopped and their URLs inactive.
- Keep remote parent state deletion explicit. If the external service expires or persists state by design, do not delete it on AppHost stop unless the public API promises session-scoped cleanup.
- Use the stateless lifecycle-orchestrator variant in `archetype-controller-reconciler.md` when several facade callbacks need the same helper logic but remain independent.
- Use the serialized controller/reconciler variant only when lifecycle callbacks, commands, background probes, and multiple resources must coordinate shared mutable state.

DON'T:

- Don't manually publish `ResourceReadyEvent`; let Aspire readiness drive it.
- Don't mutate external service state from health checks. Health checks should observe status and cache what lifecycle callbacks need.
- Don't assume `WithParentRelationship` controls lifecycle; it is only visual/semantic grouping.

## Persistence and ownership contracts

Executable, project, and container resources can use Aspire lifetime annotations. External services may also have their own persistence semantics, such as remote tunnels that expire after inactivity.

DO:

- Decide whether the process/container lifetime is session-scoped, persistent, matched to another resource, or tied to a parent process.
- Use the standard lifetime APIs (`WithSessionLifetime`, `WithPersistentLifetime`, `WithLifetimeOf`, or `WithParentProcessLifetime`) instead of custom annotations when they express the behavior.
- Document the difference between the local owner process lifetime and any remote state lifetime.
- Keep cleanup behavior aligned with the public API. If the integration leaves remote state behind for reuse or service-managed expiration, say so and do not imply AppHost stop deletes it.
- Detect circular resource lifetime references when creating custom lifetime-like behavior.

DON'T:

- Don't delete persistent external resources on stop unless the API explicitly requested session cleanup.
- Don't make a facade outlive its owner in the dashboard unless the facade is independently reachable and modeled as persistent.
- Don't invent new lifetime modes for normal executable/container/project resources when `PersistenceAnnotation` already covers the behavior.

## Manual status and events

When a facade has no DCP process/container, use resource notifications to keep the dashboard and waiters honest.

DO:

- Set an initial state with a clear resource type and useful properties.
- Publish `Starting`, `Running`, and terminal state snapshots through `ResourceNotificationService`.
- Publish `BeforeResourceStartedEvent` for a facade only when other integrations need a pre-start hook for that facade.
- Publish `ResourceStoppedEvent` for a facade only after publishing the terminal snapshot that represents the stopped state.
- Mark facade URLs inactive when the owner stops.
- Store only observed, non-authoritative status on the resource object; refresh it from the external service during health checks or lifecycle callbacks.

DON'T:

- Don't publish lifecycle events out of order.
- Don't leave facade resources in `Running` after the owner has stopped.
- Don't use a successful-looking state when the external service failed to create or expose the facade.
- Don't add a global controller queue for facade resources that are independently owned by different parent resources.

## Runtime endpoint allocation

Some facades expose endpoints that are unknown until an external service returns a host or URL.

DO:

- Add an `EndpointAnnotation` during model construction so consumers can hold an `EndpointReference`.
- Set `EndpointAnnotation.AllocatedEndpoint` only after the real runtime endpoint is known.
- Publish `ResourceEndpointsAllocatedEvent` once, the first time the facade endpoint is allocated.
- If the external endpoint changes on a later restart, update the resource snapshot URL directly instead of republishing the one-time allocation event.
- Set exceptions on endpoint allocation snapshots when external setup fails so dependent `EndpointReference` resolution fails clearly.
- Provide helper APIs such as `GetEndpoint(...)` that return the facade endpoint, and use custom `EndpointReference.ErrorMessage` values for missing associations.

DON'T:

- Don't read or allocate runtime endpoint values in publish mode.
- Don't publish local `localhost` URLs for externally hosted endpoints that cannot be reached through localhost.
- Don't require consumers to parse logs or dashboard URLs to discover the facade endpoint.

## Process, CLI, auth, and interaction

External CLIs often need toolchain validation and user authentication before resource startup.

DO:

- Use structured process arguments (`ProcessStartInfo.ArgumentList`) instead of shell command strings.
- Wire cancellation to kill child process trees for short-lived CLI operations.
- Drain stdout and stderr concurrently.
- Parse documented JSON output when available instead of human-readable logs.
- Validate required CLI versions before the first lifecycle step that depends on the CLI. If that step runs before the global required-command hook, call `IRequiredCommandValidator` explicitly with a `RequiredCommandAnnotation`.
- Coalesce login prompts and other interactive operations so concurrent resources do not prompt multiple times.
- Use `IInteractionService` only when available, and provide configuration overrides for non-interactive environments.
- Redact tokens, public-write secrets, and auth details in logs and dashboard properties.

DON'T:

- Don't assume an external CLI is installed or logged in.
- Don't block startup indefinitely waiting for interactive auth.
- Don't pass secrets on command lines when environment variables, files, or provider auth can be used.

## Publish/deploy behavior

Most custom lifecycle/facade resources are local run-only resources.

DO:

- Call `.ExcludeFromManifest()` for run-only owner and facade resources.
- Make publish-mode reference injection a no-op when the facade has no deployment story.
- If a deployment story exists, model it explicitly as a deployment target/provider feature rather than serializing local runtime URLs.

DON'T:

- Don't write public tunnel URLs, local callback URLs, allocated ports, or auth state into manifests.
- Don't let run-only facade resources appear as deployable infrastructure by accident.
