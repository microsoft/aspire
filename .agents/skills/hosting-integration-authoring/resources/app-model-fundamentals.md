# App model fundamentals

Use these rules for every hosting integration. Resources are app-model data first; orchestration, publishing, dashboard display, and deployment are driven by annotations, standard interfaces, values, references, and lifecycle hooks.

## Resource model basics

DO:

- Keep resource classes inert data objects. Constructors should capture model state only.
- Use fluent extension methods for behavior: construction, defaults, annotations, endpoints, environment, event subscriptions, and relationships.
- Treat the Aspire resource name as the unique graph identity.
- Use annotations as the primary extension mechanism for optional behavior and metadata.
- Prefer standard interfaces so runtime, tooling, publishers, and generated SDKs can discover capabilities polymorphically.

DON'T:

- Don't make resources start, stop, probe, allocate endpoints, resolve connection strings, write files, or call services from constructors.
- Don't infer dependencies, parent-child relationships, or value flows from names.
- Don't flatten structured values into strings before run/publish resolution.

## Standard capability interfaces

Implement interfaces for what the resource can do, not for what concrete type it happens to be:

| Interface | Use when |
| --- | --- |
| `IResourceWithEnvironment` | The resource can receive environment variables. |
| `IResourceWithServiceDiscovery` | Other resources should call it through service discovery. |
| `IResourceWithEndpoints` | The resource exposes named endpoints. |
| `IResourceWithConnectionString` | Consumers need a connection string or connection properties. |
| `IResourceWithArgs` | The resource accepts launch arguments. |
| `IResourceWithWaitSupport` | The resource can wait on other resources. |
| `IResourceWithoutLifetime` | The resource is a value/configuration resource with no runtime lifecycle. |
| `IComputeResource` | The resource can be hosted by a compute environment or deployment target. |
| `IComputeEnvironmentResource` | The resource represents a deployment/hosting environment. |

DO:

- Let `WithReference`, publishers, dashboard, and deployment target code key off these interfaces where possible.
- Keep resource-specific helpers thin wrappers over standard interfaces.

DON'T:

- Don't require publishers or consumers to special-case your concrete type when an existing interface expresses the capability.

## Lifecycle, readiness, and status

Known resource states and dashboard status are managed by Aspire. `Unknown` is the normal initial state while the graph is still being constructed.

Important lifecycle events:

| Event | Use for |
| --- | --- |
| `InitializeResourceEvent` | First lifecycle setup for a resource. |
| `ResourceEndpointsAllocatedEvent` | Reading allocated endpoint values in run mode. |
| `BeforeResourceStartedEvent` | Last-chance runtime setup before process/container start. |
| `ConnectionStringAvailableEvent` | Creating clients or caching connection strings after references are resolvable. |
| `ResourceReadyEvent` | Creating service state after the resource is healthy and ready. |

DO:

- Remember event publishing is synchronous and blocking; keep handlers bounded and cancellation-aware.
- Use `ResourceNotificationService` snapshots for ongoing status updates.
- Use resource logs for human-readable diagnostics and setup/command progress.
- Let health checks drive readiness. Aspire publishes `ResourceReadyEvent` after the resource is running and health checks pass, or immediately after running when no health checks exist.

DON'T:

- Don't manually publish `ResourceReadyEvent`.
- Don't use health checks for side effects.
- Don't confuse one-time events with ongoing status snapshots.

## Relationships and dependency graph

References form a heterogeneous directed acyclic graph of value flows and dependency ordering. Endpoints are special: endpoint references are modeled outside the strict resource dependency graph and can support real-world cycles such as mutual frontend/OIDC callback URL wiring.

DO:

- Add explicit references through `WithReference`, environment variables, args, connection strings, or custom structured values.
- Use `IResourceWithParent<TParent>` for true lifecycle containment.
- Use `.WithParentRelationship()` only for visual/dashboard grouping with no lifecycle impact.
- Use `.WithRelationship(parent, "Label")` for custom semantic relationships that are not ownership.

DON'T:

- Don't assume `WithParentRelationship()` controls lifecycle.
- Don't assume `IResourceWithParent<TParent>` and a visual parent relationship are interchangeable.
- Don't rely on call order or resource names to infer dependency edges.

## Structured values and deferred evaluation

Structured values preserve graph meaning across run and publish. A value may resolve to a concrete local value in run mode and a manifest/deployment expression in publish mode.

Core value shapes:

| Type | Run mode | Publish mode |
| --- | --- | --- |
| `string` | Literal value | Literal value |
| `EndpointReference` | Concrete endpoint URL/host/port after allocation | Target-specific endpoint expression |
| `EndpointReferenceExpression` | One endpoint property | Target-specific property expression |
| `ConnectionStringReference` | Concrete connection string | Token or externalized secret/reference |
| `ParameterResource` | Local value or environment/user-secret lookup | Placeholder/parameter |
| `ReferenceExpression` | Composite resolved string | Composite expression with placeholders preserved |

DO:

- Build composite values inside `ReferenceExpression.Create(...)`.
- Implement `IValueProvider` and `IManifestExpressionProvider` on custom structured values.
- Implement `IValueWithReferences` when the value holds resource references.
- Pass value objects directly into environment variables, args, and annotations.

DON'T:

- Don't build a string first and wrap it later; structure is already lost.
- Don't mix resolved strings with placeholders in the same value.
- Don't concatenate secrets into plain strings.

## Endpoint resolution

Endpoints are allocated during run-mode startup. In publish mode, endpoint values are manifest expressions and concrete properties such as `Url`, `Host`, and `Port` are not available.

DO:

- Use `EndpointReference.Property(EndpointProperty.Host)`, `Port`, `HostAndPort`, `Scheme`, `TargetPort`, or `Url` when constructing deferred expressions.
- Use `ResourceEndpointsAllocatedEvent`, `BeforeResourceStartedEvent`, or `WithEnvironment` callbacks when you must access allocated run-mode values.
- Check `EndpointReference.IsAllocated` before reading concrete endpoint properties outside known allocation points.
- Let endpoint resolution account for source/target context: container-to-container, executable/project-to-container, and container-to-host communication can resolve differently.
- In unusual cross-context scenarios, branch on execution context and explain why the default endpoint resolver is not enough.

DON'T:

- Don't read `endpoint.Url`, `endpoint.Host`, or `endpoint.Port` during publish.
- Don't use host-process endpoint values for container-to-container traffic.
- Don't turn an endpoint property into a string when an `EndpointReferenceExpression` can stay structured.

## Add/With API split

DO:

- Use `Add{Technology}` methods to validate inputs, instantiate a data-only resource, register it with `builder.AddResource(resource)`, and apply default annotations/wiring.
- Use `With{Configuration}` methods to attach or replace annotations on an existing resource builder.
- Keep resource-producing APIs returning `IResourceBuilder<TResource>`.
- Keep fluent configuration APIs returning the builder they receive unless they intentionally create a child resource.

DON'T:

- Don't put behavior in resource constructors because it is convenient for `Add*`.
- Don't expose annotation plumbing when a fluent method can express the feature.

## Annotation and manifest publishing

Annotations are strongly typed model metadata. Public annotations can be used by runtime code, publishers, tests, and user callbacks.

DO:

- Use `ResourceAnnotationMutationBehavior.Replace` for last-wins configuration.
- Accumulate annotations only when multiple entries intentionally compose.
- Query annotations with `TryGetLastAnnotation<T>()` when the last applied setting wins.
- For custom manifest resources, add `ManifestPublishingCallbackAnnotation` and write JSON through `ManifestPublishingContext.Writer`.
- Use `IManifestExpressionProvider.ValueExpression` for structured manifest fields and call `context.TryAddDependentResources(value)` when emitted values may reference resources.
- Use `.ExcludeFromManifest()` for run-only setup helpers, admin/dev companions, or resources that should not publish.

DON'T:

- Don't duplicate mutually exclusive annotations.
- Don't serialize resolved runtime values into manifest output.
- Don't include implementation-only resources in generated deployment artifacts.
