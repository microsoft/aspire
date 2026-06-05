# Aspire Resource Model: Concepts, Design, and Authoring Guidance
> **Audience** – Aspire integrators, advanced users, and contributors who are defining custom resource types, implementing publishers, or working across both runtime and publish workflows.  
> This documentation's focus is on hosting integrations *NOT* client integrations.
> *Just getting started? Jump straight to [Quick Start](#quick-start) and come back later for the deep‑dive.*

---

## Quick Start

A two‑minute "hello‑world" that shows the happy path.

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var db = builder.AddPostgres("pg");
var api = builder.AddProject("api").WithReference(db);
var web = builder.AddNpmApp("web").WithReference(api);

builder.Build().Run();
```

```mermaid
%% Graph showing resource dependencies
graph LR
  web --> api
  api --> pg
```

1. Use `AddXyz` helper methods to declare resources (e.g., `AddPostgres`, `AddProject`).
2. Use `.WithReference()` (or similar) to wire explicit dependencies between resources.
3. Call `Build().Run()` – Aspire builds the application model (graph) and executes it, handling port allocation, environment variables, and startup order.

---

## Table of Contents
- [Quick Start](#quick-start)
- [Resource Basics](#resource-basics)
  - [Annotations](#annotations)
  - [Fluent Extension Methods](#fluent-extension-methods)
  - [Example: Adding Resources and Wiring Dependencies](#example-adding-resources-and-wiring-dependencies)
  - [Key Takeaways](#key-takeaways)
- [Built‑In Resources & Lifecycle](#built-in-resources--lifecycle)
  - [Known Resource States](#known-resource-states)
  - [Built-In Types](#built-in-types)
  - [Well-Known Lifecycle Events](#well-known-lifecycle-events)
  - [Status Reporting](#status-reporting)
  - [Resource Health](#resource-health)
  - [Resource Logging](#resource-logging)
- [Standard Interfaces](#standard-interfaces)
  - [Common Interfaces](#common-interfaces)
  - [Examples Per Interface](#examples-per-interface)
  - [Importance of Polymorphism](#importance-of-polymorphism)
- [Resource Hierarchy and Parent‑Child Relationships](#resource-hierarchy-and-parent-child-relationships)
  - [Lifecycle Containment](#lifecycle-containment)
  - [Visual Grouping (Without Lifecycle Impact)](#visual-grouping-without-lifecycle-impact)
  - [Manual Relationships — No Inference](#manual-relationships--no-inference)
  - [Real-World Examples](#real-world-examples)
- [Values and References](#values-and-references)
  - [Special Case: Endpoints](#special-case-endpoints)
  - [How the DAG Forms](#how-the-dag-forms)
  - [Structured vs Literal Values](#structured-vs-literal-values)
  - [Value Providers and Deferred Evaluation](#value-providers-and-deferred-evaluation)
  - [Core Value Types (Expanded)](#core-value-types-expanded)
  - [Core Value Types (Expanded)](#core-value-types-expanded)
- [ReferenceExpression](#referenceexpression)
- [Endpoint Primitives](#endpoint-primitives)
- [Context-Based Endpoint Resolution](#context-based-endpoint-resolution)
- [API Patterns](#api-patterns)
- [Full Examples](#full-examples)
  - [Example: Derived Container Resource (Redis)](#example-derived-container-resource-redis)
  - [Example: Custom Resource - Talking Clock](#example-custom-resource---talking-clock)
- [Glossary](#glossary)

---

## Resource Basics

In Aspire, a **resource** is the fundamental unit of modeling for distributed applications. Resources represent services, infrastructure elements, or supporting components that together compose a distributed system.

Resources in Aspire implement the `IResource` interface, with most built-in resources deriving from the base `Resource` class.

- Resources are **inert by default** — they are **pure data objects** that describe capabilities, configuration, and relationships. They **do not manage their own lifecycle** (e.g., starting, stopping, checking health). Resource lifecycle is coordinated externally by orchestrators and lifecycle hooks.
- Resources are identified by a **unique name** within the application graph. This name forms the basis for referencing, wiring, and visualizing resources.

---

### Annotations

Resource metadata is expressed through **annotations**, which are strongly-typed objects implementing the `IResourceAnnotation` interface.

Annotations allow attaching additional structured information to a resource without modifying its core class. They are the **primary extensibility mechanism** in Aspire, enabling:

- Core system behaviors (e.g., service discovery, connection strings, health probes)
- Custom extensions and third-party integrations
- Layering of optional capabilities without inheritance or tight coupling

> **Example:** A resource might be annotated with environment variables, endpoint information, or service discovery metadata based on what other components need.

---

### Fluent Extension Methods

Resources are typically added using fluent **extension methods** such as `AddRedis`, `AddProject`, or `AddPostgres`.

Extension methods encapsulate:

- **Construction** of the resource object
- **Attachment of annotations** that describe defaults, discovery hints, or runtime behavior
- **Relationships** like wiring up dependencies (e.g., via `.WithReference()`)

This pattern improves the developer experience by:

- Setting **sane defaults** automatically
- Making **required configuration obvious and discoverable**
- Providing a **product-like feel** to adding infrastructure

> **Without** extension methods, adding a resource manually would require constructing it directly, setting annotations manually, and remembering to wire relationships by hand.

---

### Example: Adding Resources and Wiring Dependencies

```csharp
var builder = DistributedApplication.CreateBuilder(args);
var pg = builder.AddPostgres("pg");
var api = builder.AddProject("backend").WithReference(pg);
var frontend = builder.AddNpmApp("frontend").WithReference(api);
```

In this example:
- A PostgreSQL database (`pg`) is created.
- A backend service (`api`) is created and connected to the database.
- A frontend app (`frontend`) is created and reverse-proxies traffic to the backend.

Each resource participates in the application graph passively, with dependencies expressed through references.

---

### Key Takeaways
- Resources **describe** capabilities; they don't control them.
- **Annotations** add rich, extensible metadata to resources.
- **Fluent extension methods** guide developers toward correct and complete configurations.
- **Names** are the identity anchors for wiring and dependency resolution.

## Built-In Resources & Lifecycle

In Aspire, many common infrastructure and application patterns are available as **built-in resource types**. Built-in resources simplify modeling real-world systems by providing ready-made building blocks that automatically integrate with the Aspire runtime, lifecycle management, health tracking, and dashboard visualization.

Built-in resources:

- Handle **lifecycle transitions** automatically.
- Raise **lifecycle events** (like startup and readiness signals).
- Push **status updates** to the system for real-time orchestration and monitoring.
- Expose **endpoints, environment variables, and metadata** needed for dependent resources.

They help developers express distributed applications **consistently** without needing to manually orchestrate startup, shutdown, and dependency wiring.

---

### Known Resource States

All resources in Aspire begin in the `Unknown` state when added to the application graph. This ensures that the **resource graph can be fully constructed** before any execution, dependency resolution, or publishing occurs.

| State            | Meaning                                                                                              |
| ---------------- | ---------------------------------------------------------------------------------------------------- |
| Unknown          | Default state when first added to the graph. No execution planned yet.                               |
| NotStarted       | Defined but not yet scheduled to start.                                                              |
| Waiting          | Awaiting dependencies to become ready (e.g., using `WaitFor`).                                       |
| Starting         | Actively starting; readiness not yet confirmed.                                                      |
| Running          | Successfully started; may have separate application-level health probing.                            |
| RuntimeUnhealthy | The container or host runtime environment (e.g., Docker daemon) is unavailable, preventing start-up. |
| Stopping         | Resource is shutting down gracefully.                                                                |
| Exited           | Completed execution (typically for short-lived jobs, migrations, one-shot tasks).                    |
| Finished         | Ran to successful completion (used for batch workloads or scripts).                                  |
| FailedToStart    | Failed during startup initialization.                                                                |
| Hidden           | Present in the model but intentionally hidden from dashboard UI (e.g., infrastructure helpers).      |

`TerminalStates` (e.g., `Finished`, `Exited`, `FailedToStart`) represent states where the resource has stopped progressing.

Resource states drive:

- **Readiness checks** to unblock dependent resources.
- **Dashboard visualization** and state coloring.
- **Orchestration sequencing** for startup and shutdown.
- **Health monitoring** at runtime.

---

### Built-In Types

Aspire provides a set of fundamental built-in resource types that serve as the foundation for modeling execution units:

| Type               | Purpose                                                 |
| ------------------ | ------------------------------------------------------- |
| ContainerResource  | Runs Docker containers as resources.                    |
| ProjectResource    | Runs a .NET project directly (build + launch workflow). |
| ExecutableResource | Launches arbitrary executables or scripts as resources. |

These types are **infrastructure-oriented primitives**. They model how code and applications are packaged and executed.

> **Note:** Specialized services like Redis, Postgres, or RabbitMQ are **not** true "built-in" resource types in Aspire core — they are typically provided through external packages or extensions that build on `ContainerResource` or custom resource types.

Built-in types:

- Automatically participate in resource orchestration.
- Raise standard lifecycle events without manual intervention.
- Report health and readiness status.
- Expose connection endpoints for dependent services.

Custom resources must **opt-in manually** to these behaviors.

---

### Well-Known Lifecycle Events

Aspire defines standard events to orchestrate resource lifecycles:

| Event                          | When Emitted                             | Purpose                                                 |
| ------------------------------ | ---------------------------------------- | ------------------------------------------------------- |
| InitializeResourceEvent        | The first event fired for any resource.  | Kick the resource's lifecycle.                          |
| ResourceEndpointsAllocatedEvent| Fired when endpoints have been allocated | Can successfully evaluate endpoint values at this point  |
| BeforeResourceStartedEvent     | Just before execution begins.            | Last-chance dynamic setup or validation before startup  |
| ResourceReadyEvent             | When the resource is considered "ready." | Unblocks dependents waiting for readiness.              |
| ConnectionStringAvailableEvent | When a connection string is ready.       | Enables dependent resources to be wired dynamically.    |

Lifecycle events allow:

- Dynamic reconfiguration just before startup.
- Dependent resource activation based on readiness.
- Wiring services together based on runtime-generated outputs.

> **Important:** Event publishing is **synchronous and blocking** — event handlers can delay further execution.

---

### Status Reporting

Beyond events, Aspire uses **asynchronous state snapshots** to report resource status continuously.

- **ResourceNotificationService** handles snapshot updates.
- Status updates involve:
  1. Receiving the previous immutable snapshot.
  2. Mutating to a new snapshot representing the updated state.
  3. Publishing the new snapshot to the dashboard and orchestrators.

Snapshots:

- Always reflect the **latest known status**.
- Are **non-blocking** and do not delay orchestration.
- Drive **dashboard visualization** and orchestration decisions.

> Events represent **moment-in-time actions**.
> Snapshots represent **ongoing state**.

---

### Resource Health

Aspire integrates with .NET health checks to monitor the status of resources after they have started. The health check mechanism is tied into the resource lifecycle:

1. When a resource transitions to the `Running` state, Aspire checks if it has any associated health check annotations (typically added via `.WithHealthCheck(...)`).
2. **If health checks are configured:** Aspire begins executing these checks periodically. The resource is considered fully "ready" only after its health checks pass successfully. Once healthy, Aspire automatically publishes the `ResourceReadyEvent`.
3. **If no health checks are configured:** The resource is considered "ready" as soon as it enters the `Running` state. Aspire automatically publishes the `ResourceReadyEvent` immediately in this case.

This automatic handling ensures that dependent resources (using mechanisms like `WaitFor`) only proceed when the target resource is truly ready, either by simply running or by passing its defined health checks.

> **Important:** Developers should **not** manually publish the `ResourceReadyEvent`. Aspire manages the transition to the ready state based on the presence and outcome of health checks. Manually firing this event can interfere with the orchestration logic.

---

### Resource Logging

Aspire supports logging output on a per-resource basis, which is displayed in the console window and can be surfaced in the dashboard. This log stream is especially useful for monitoring what a resource is doing in real time.

For built-in resources, Aspire captures and forwards output from:
- stdout and stderr of containers (e.g., Docker)
- Process output from executables or .NET projects

For custom resources, developers can write directly to a resource’s log using the ResourceLoggerService.

This service provides an ILogger scoped to the individual resource instance, enabling human-readable, contextual logging.

```csharp
var logger = resourceLoggerService.GetLogger(myResource);
logger.LogInformation("Starting provisioning…");
```

See the Talking Clock example for a full implementation of a custom resource with logging.

> **Note:** A full example demonstrating custom resource logging with the Talking Clock resource can be found in the [Full Examples](#example-custom-resource---talking-clock) section.

#### Key APIs

| API | Description |
|-----|-------------|
| ResourceLoggerService.GetLogger(IResource) | Returns a scoped ILogger. |
| ResourceLoggerService.WatchAsync | Stream log lines. |

Use logs for human-readable diagnostics.  
Use `ResourceNotificationService` for structured state.

---

## Standard Interfaces

Aspire defines a set of **optional standard interfaces** that allow resources to declare their capabilities in a structured, discoverable way. Implementing these interfaces enables **dynamic wiring, publishing, service discovery, and orchestration** without hardcoded type knowledge.

These interfaces are the foundation for Aspire's polymorphic behaviors — enabling tools, publishers, and the runtime to treat resources uniformly based on what they can do, rather than what they are.

---

### Why?

- **Dynamic discovery:** Tooling and runtime systems can automatically adapt based on resource capabilities.
- **Loose coupling:** Behaviors (like environment wiring, service discovery, or connection sharing) are opt-in.
- **Extensibility:** New resource types can integrate seamlessly into the Aspire ecosystem by implementing one or more interfaces.

---

### Common Interfaces

| Interface | Purpose |
|-----------|---------|
| `IResourceWithEnvironment` | Supports setting environment variables for the resource. |
| `IResourceWithServiceDiscovery` | Registers a service hostname and metadata for discovery by other resources. |
| `IResourceWithEndpoints` | Exposes ports, URLs, or connection points that other resources can consume. |
| `IResourceWithConnectionString` | Provides a connection string output for consumers to connect to the resource. |
| `IResourceWithArgs` | Supplies additional CLI arguments when launching a project or executable. |
| `IResourceWithWaitSupport` | This resource can wait for other resources. |
| `IResourceWithWithoutLifetime` (>= 9.3) | This resource does not have a lifecycle. (e.g. connection string, parameter) |

---

### Examples Per Interface

**`IResourceWithEnvironment`**
```csharp
builder.WithEnvironment("MY_SETTING", "value");
```
Allows setting environment variables that are passed to the resource when it starts.

**`IResourceWithServiceDiscovery`**
```csharp
builder.WithReference(myResourceWithDiscovery);
```
Exposes the resource via DNS-style service discovery. Downstream resources can refer to it by logical name.

**`IResourceWithEndpoints`**
```csharp
builder.GetEndpoint("http");
```
When a resource implements `IResourceWithEndpoints`, it allows referencing specific endpoints (e.g., `http`, `tcp`) for reverse proxies or connection targets.

**`IResourceWithConnectionString`**
```csharp
builder.WithReference(myDatabaseResource);
```
Allows wiring a database connection string into environment variables, configurations, or CLI arguments.

**`IResourceWithArgs`**
```csharp
builder.WithArgs("2", "--url", endpoint);
```
Allows setting command-line arguments on the resource.

**`IResourceWithWaitSupport`**
```csharp
builder.WaitFor(otherResource)
```

This resource can wait on other resources. A ParameterResource is an example of resources that cannot wait.

> **Source:** These APIs and behaviors are defined in the [Aspire.Hosting](https://github.com/microsoft/aspire/blob/main/src/Aspire.Hosting/api/Aspire.Hosting.cs) package.

---

### Importance of Polymorphism

By modeling behaviors through interfaces rather than concrete types, Aspire enables:
- **Tooling flexibility:** Publishers can wire environment variables, endpoints, and arguments generically.
- **Runtime uniformity:** Dashboards and orchestrators treat resources based on capabilities, not type-specific logic.
- **Ecosystem extensibility:** New resource types can plug into the system without modifying core code.

Interfaces allow Aspire to remain **open, flexible, and adaptable** as new types of services, platforms, and deployment targets emerge.

---

## Resource Hierarchy and Parent-Child Relationships

Aspire supports modeling **parent-child relationships** between resources to express ownership, containment, and grouping.

Parent-child relationships serve two purposes:
- **Lifecycle Containment**: The child's execution is tied to the parent's — starting, stopping, and failures cascade from parent to child automatically.
- **Dashboard Visualization**: The child appears **nested beneath** the parent in dashboards and visualizations, improving readability.

---

### Lifecycle Containment

When a resource implements the `IResourceWithParent` interface, it declares **true containment** — meaning its lifecycle is controlled by its parent:

- **Startup**: The child resource will only start after its parent starts (though readiness is independent).
- **Shutdown**: If the parent is stopped or removed, the child is also stopped automatically.
- **Failure Propagation**: If a parent enters a terminal failure state (`FailedToStart`, etc.), dependent children are stopped.

> **Example:**  
> A logging sidecar container is tied to the lifecycle of a main application container — if the main app stops, the logging sidecar is also terminated.

---

### Visual Grouping (Without Lifecycle Impact)

Aspire also supports **visual-only parent-child relationships** using the `WithParentRelationship()` method during resource construction.

Visual relationships:
- Affect **only the dashboard layout**.
- **Do not affect lifecycle** — the resources are independent operationally.
- Improve **clarity** by logically grouping related components.

> **Example:**  
> A Redis database container and a Redis Commander admin UI container can be grouped visually, even though they start independently.

---

### Manual Relationships — No Inference

Aspire **does not infer** parent-child relationships automatically based on names, dependencies, or network links.  
You must **explicitly declare** either:

- `IResourceWithParent` (for lifecycle and visual nesting)  
- or `.WithParentRelationship()` (for visual nesting only)

This explicitness ensures developers have full control over resource containment and presentation.

---

### Real-World Examples

| Scenario | Parent | Child |
|----------|--------|-------|
| Main application container with logging sidecar | App container | Fluentd container |
| Database with admin dashboard | Database container | Admin UI container |
| Microservice with associated health monitor | API container | Health probe container |

---

## Values and References

In Aspire, configuration, connectivity details, and dependencies between distributed components are modeled using **structured values**. These values capture relationships explicitly—not just as simple strings—making the application graph **portable, inspectable, and evolvable**.

Aspire represents these relationships through a **heterogeneous Directed Acyclic Graph (DAG)**. This graph tracks not only dependency ordering but also how **structured values** are passed between resources at multiple abstraction levels: configuration, connection, and runtime behavior.

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var db = builder.AddPostgres("pg");
var api = builder.AddProject("api").WithReference(db);
var web = builder.AddNpmApp("web").WithReference(api);

builder.Build().Run();
```

```mermaid
%% Graph showing resource dependencies
graph LR
  web --> EndpointReference --> api
  api --> ConnectionStringReference --> pg
```

---

### Special Case: Endpoints

Normally, resource references form an acyclic graph — **no cycles allowed**.  
However, **endpoint references are treated specially** and **may form cycles** intentionally.

Endpoints are modeled as **external entities**:
- They are **not edges** in the resource dependency graph.
- They enable realistic mutual references like:
  - A frontend app and an OIDC server mutually referencing each other's URLs (redirects, login callbacks).
  - A backend exposing CORS settings that reference the frontend URL.

> Endpoints are managed separately from strict dependency edges to allow flexible, real-world service wiring.

---

### How the DAG Forms

Resources connect to each other through:
- **WithReference()** calls
- **Environment variables**, **CLI arguments**, and **other configurations** populated by structured value references.

Each reference **adds an edge** to the graph, allowing Aspire to:
- Track dependency ordering.
- Propagate structured values cleanly between services.
- Validate application integrity before execution.

> **Important:**  
> Aspire **never infers references automatically** — all value flows must be explicitly authored by developers.

---

### Structured vs Literal Values

Aspire distinguishes between **structured values** and **literal values**.

- **Structured values** preserve meaning (e.g., "this is a service URL" vs. "this is a raw string").
- **Literal values** are inert — they are carried unchanged across modes.

At publish time and run time:
- Structured values are either **resolved** (if possible) or **translated into target artifacts** (e.g., environment variables, argument values etc.).
- Literal values are simply copied.

> **Flattening values too early destroys portability, environment substitution, and cross-platform compatibility.**
> Aspire delays flattening as long as possible to maintain graph fidelity.

---

### Value Providers and Deferred Evaluation

Every structured value type in Aspire implements two fundamental interfaces:

| Interface | When Used | Purpose |
|-----------|-----------|---------|
| `IValueProvider` | Run mode | Resolves live values when the application starts. |
| `IManifestExpressionProvider` | Publish mode | Emits structured expressions (like `{pg.outputs.url}`) into deployment artifacts. |

This dual-interface model enables **deferred evaluation**:
- During **publish**, structured placeholders are emitted — no runtime values are resolved yet.
- During **run**, structured references are resolved to live values like URLs, ports, or connection strings.

Internally, value providers are attached to environment variables, CLI arguments, configuration fields, and other structured outputs during application graph construction.

> Deferred evaluation guarantees that Aspire applications can be **published safely**, **deployed flexibly**, and **run consistently** across environments.

---

### Core Value Types (Expanded)

| Type | Represents | Run Mode | Publish Mode |
|------|------------|----------|--------------|
| `string` | A literal string value. | Same literal. | Same literal. |
| `EndpointReference` | A link to a named endpoint on another resource. | Concrete URL (`http://localhost:5000`). | Target-specific endpoint translation (DNS, ingress, etc.). |
| `EndpointReferenceExpression` | A property of an endpoint (`Host`, `Port`, `Scheme`). | Concrete value. | Platform-specific translation. |
| `ConnectionStringReference` | A symbolic pointer to a resource's connection string. | Concrete string. | Token or externalized secret. |
| `ParameterResource` | An external input, secret, or setting. | Local dev value or environment lookup. | Placeholder `${PARAM}` for substitution. |
| `ReferenceExpression` | A composite string with embedded references. | Concrete formatted string. | Format string preserved for substitution. |

---

## ReferenceExpression

`ReferenceExpression` preserves **structured value objects**—endpoints, parameters, connection strings, etc.—inside an interpolated string and defers evaluation until it is safe.

Aspire evaluates the model in **two distinct modes**:

| Phase       | `ReferenceExpression` yields                                            |
| ----------- | ----------------------------------------------------------------------- |
| **Publish** | Publisher‑specific placeholder text (e.g., `{api.bindings.http.host}`). |
| **Run**     | Concrete value such as `localhost`.                                     |

### Minimal example

```csharp
var ep = api.GetEndpoint("http");

builder.WithEnvironment("HEALTH_URL",
    ReferenceExpression.Create(
        $"https://{ep.Property(EndpointProperty.Host)}:{ep.Property(EndpointProperty.Port)}/health"
    )
);
```

*Publish manifest excerpt*

```env
HEALTH_URL=https://{api.bindings.http.host}:{api.bindings.http.port}/health
```

*Run‑time value*

```env
HEALTH_URL=https://localhost:5000/health
```

> **Best practice** – Avoid resolving values directly. Build the string inside `ReferenceExpression.Create` so structure is preserved.

### Alternate pattern using `ExecutionContext`

```csharp
var ep = api.GetEndpoint("http");

if (builder.ExecutionContext.IsRunMode)
{
    builder.WithEnvironment("HEALTH_URL", ep.Url + "/health");   // concrete
}
else
{
    builder.WithEnvironment("HEALTH_URL",
        ReferenceExpression.Create($"{ep}/health"));              // structured
}
```

### Pattern used by `IResourceWithConnectionString`

A common implementation builds the connection string with `ReferenceExpression`, mixing any value objects (endpoint properties, parameters, other references):

```csharp
private static ReferenceExpression BuildConnectionString(
    EndpointReference endpoint,
    ParameterResource  passwordParameter)
{
    var host = endpoint.Property(EndpointProperty.IPV4Host);
    var port = endpoint.Property(EndpointProperty.Port);
    var pwd  = passwordParameter;

    return ReferenceExpression.Create(
        $"Server={host},{port};User ID=sa;Password={pwd};TrustServerCertificate=true");
}
```

### Common errors

| Error                                 | Correct approach                                        |
| ------------------------------------- | ------------------------------------------------------- |
| Build the string first, wrap later    | Build **inside** `ReferenceExpression.Create(...)`.     |
| Access `Endpoint.Url` during publish  | Use `Endpoint.Property(...)` in the expression.         |
| Mix resolved strings and placeholders | Keep the entire value inside one `ReferenceExpression`. |

---

## Endpoint Primitives

The [EndpointReference](https://aspire.dev/reference/api/csharp/aspire.hosting/endpointreference/) is the fundamental type used to interact with another resource's endpoint. It provides properties such as:

- Url
- Host
- Port

These properties are dynamically resolved during the application’s startup sequence. Accessing them before the endpoints are allocated results in an exception.

### IResourceWithEndpoints

Resources supporting endpoints should implement IResourceWithEndpoints, enabling the use of GetEndpoint(name) to retrieve an EndpointReference. This is implemented on the built-in ProjectResource, ContainerResource and ExecutableResource. It allows endpoints to be programmatically accessed and passed between resources.

**Key Example: Endpoint Access and Resolution**

```C#
var builder = DistributedApplication.CreateBuilder(args);

var redis = builder.AddContainer("redis", "redis")
                   .WithEndpoint(name: "tcp", targetPort: 6379);

// Get a reference to the "tcp" endpoint by name
var endpoint = redis.GetEndpoint("tcp");

builder.Build().Run();
```

---

### Understanding Endpoint Allocation and Resolution
> **See the canonical [Endpoint Primitives](#endpoint-primitives) section for the full explanation.**  
The following is a short recap for quick reference.

### What Does "Allocated" Mean?

An endpoint is **allocated** when Aspire resolves its runtime values (e.g., `Host`, `Port`, `Url`) during **run mode**. Allocation happens as part of the **startup sequence**, ensuring endpoints are ready for use in local development.

In **publish mode**, endpoints are not allocated with concrete values. Instead, their values are represented as **manifest expressions** (e.g., `{redis.bindings.tcp.host}:{redis.bindings.tcp.port}`) that are resolved by the deployment infrastructure.

#### Comparison: Run Mode vs. Publish Mode

| **Context**         | **Run Mode**                          | **Publish Mode**                          |
|----------------------|---------------------------------------|-------------------------------------------|
| **Endpoint Values**  | Fully resolved (`tcp://localhost:6379`).    | Represented by manifest expressions (`{redis.bindings.tcp.url}`). |
| **Use Case**         | Local development and debugging.      | Deployed environments (e.g., Kubernetes, Azure). |
| **Behavior**         | Endpoints are allocated dynamically. | Endpoint placeholders resolve at runtime. |

Use the `IsAllocated` property on an `EndpointReference` to check if an endpoint has been allocated before accessing its runtime values.

---

### Accessing Allocated Endpoints Safely

Endpoint resolution happens during the startup sequence of the DistributedApplication. To safely access endpoint values (e.g., Url, Host, Port), you must wait until endpoints are allocated.

Aspire provides eventing APIs, such as `AfterEndpointsAllocatedEvent`, to access endpoints after allocation. These APIs ensure code executes only when endpoints are ready. 

#### Example: Checking Allocation and Using Eventing

```csharp
var builder = DistributedApplication.CreateBuilder(args);

// Add a Redis container with a TCP endpoint
var redis = builder.AddContainer("redis", "redis")
                   .WithEndpoint(name: "tcp", targetPort: 6379);

// Retrieve the EndpointReference
var endpoint = redis.GetEndpoint("tcp");

// Check allocation status and access Url
Console.WriteLine($"IsAllocated: {endpoint.IsAllocated}");

try
{
    Console.WriteLine($"Url: {endpoint.Url}");
}
catch (Exception ex)
{
    Console.WriteLine($"Error accessing Url: {ex.Message}");
}

// Subscribe to AfterEndpointsAllocatedEvent for resolved properties
builder.Eventing.Subscribe<AfterEndpointsAllocatedEvent>(
    (@event, cancellationToken) =>
    {
        Console.WriteLine($"Endpoint allocated: {endpoint.IsAllocated}");
        Console.WriteLine($"Resolved Url: {endpoint.Url}");
        return Task.CompletedTask;
    });

// Start the application
builder.Build().Run();
```

#### Output

- **Run Mode**:
  ```text
  IsAllocated: True
  Resolved Url: http://localhost:6379
  ```
- **Publish Mode**:
  ```text
  IsAllocated: False
  Error accessing Url: Endpoint has not been allocated.
  ```

**NOTE: The overloads of [WithEnvironment](https://aspire.dev/reference/api/csharp/aspire.hosting/resourcebuilderextensions/methods/#withenvironment-iresourcebuilder-t-action-environmentcallbackcontext) that take a callback run after endpoints have been allocated.** 

---

## Referencing Endpoints from Other Resources

### Using WithReference

The WithReference API allows you to pass an endpoint reference directly to a target resource.

```C#
var builder = DistributedApplication.CreateBuilder(args);

var redis = builder.AddContainer("redis", "redis")
                   .WithEndpoint(name: "tcp", targetPort: 6379);

builder.AddProject<Projects.Worker>("worker")
       .WithReference(redis.GetEndpoint("tcp"));

builder.Build().Run();
```

`WithReference` is optimized for applications that use service discovery.

### Using WithEnvironment

The WithEnvironment API exposes endpoint details as environment variables, enabling runtime configuration.

Example: Passing Redis Endpoint as Environment Variable

```C#
var builder = DistributedApplication.CreateBuilder(args);

var redis = builder.AddContainer("redis", "redis")
                   .WithEndpoint(name: "tcp", targetPort: 6379);

builder.AddProject<Worker>("worker")
       .WithEnvironment("RedisUrl", redis.GetEndpoint("tcp"));

builder.Build().Run();
```

WithEnvironment gives full control over the configuration names injected into the target resource. 

## EndpointReferenceExpression – accessing endpoint parts

`EndpointReferenceExpression` represents **one field** of an endpoint (Host, Port, Scheme, etc.).
Call `endpoint.Property(...)` to get that field; the result is still a structured value and stays deferred until publish/run time.

| Need                                    | Pattern                                                |
| --------------------------------------- | ------------------------------------------------------ |
| Only one part (e.g., host)              | `endpoint.Property(EndpointProperty.Host)`             |
| Compose multiple parts into one setting | Build a `ReferenceExpression` (see dedicated section). |

### Example – expose host and port separately

```csharp
var redis = builder.AddContainer("redis", "redis")
                   .WithEndpoint("tcp", 6379);

builder.AddProject("worker")
       .WithEnvironment(ctx =>
       {
           var ep = redis.GetEndpoint("tcp");
           ctx.EnvironmentVariables["REDIS_HOST"] = ep.Property(EndpointProperty.Host);
           ctx.EnvironmentVariables["REDIS_PORT"] = ep.Property(EndpointProperty.Port);
       });
```

### Example – build a full URL

```csharp
var ep = redis.GetEndpoint("tcp");

builder.WithEnvironment("REDIS_URL",
    ReferenceExpression.Create(
        $"redis://{ep.Property(EndpointProperty.HostAndPort)}"
    )
);
```

This pattern avoids resolving endpoint values prematurely and works in both publish and run modes.

---

### API surface (reference)

```csharp
public enum EndpointProperty
{
    Url = 0,
    Host = 1,
    IPV4Host = 2,
    Port = 3,
    Scheme = 4,
    TargetPort = 5,
    HostAndPort = 6
}
```

| Property              | Meaning                                          |
| --------------------- | ------------------------------------------------ |
| **Url**               | Full URL (scheme://host\:port).                  |
| **Host / IPV4Host**   | Host name or IPv4 literal.                       |
| **Port / TargetPort** | Allocated host port vs. container‑internal port. |
| **Scheme**            | `http`, `tcp`, etc.                              |
| **HostAndPort**       | Convenience composite (`host:port`).             |

`EndpointReference` exposes live or placeholder values for an endpoint and provides `.Property(...)` to create an **EndpointReferenceExpression**.

Key members:

| Member                                                   | Description                                            |
| -------------------------------------------------------- | ------------------------------------------------------ |
| `Url`, `Host`, `Port`, `Scheme`, `TargetPort`            | Concrete in run mode; undefined in publish mode.       |
| `bool IsAllocated`                                       | Indicates if concrete values are available (run mode). |
| `EndpointReferenceExpression Property(EndpointProperty)` | Creates a deferred expression for one field.           |

`EndpointReferenceExpression` implements the same `IManifestExpressionProvider` / `IValueProvider` pair, so it can be embedded in a `ReferenceExpression` or resolved directly with `GetValueAsync()`.

---

## Context-Based Endpoint Resolution

Aspire resolves endpoints differently based on the relationship between the source and target resources. This ensures proper communication across all environments.

### Resolution Rules

| **Source**                | **Target**                | **Resolution**                                | **Example URL**                         |
|---------------------------|---------------------------|-----------------------------------------------|-----------------------------------------|
| **Container**             | **Container**            | Container network (`resource name:port`).    | `redis:6379`                            |
| **Executable/Project**    | **Container**            | Host network (`localhost:port`).             | `localhost:6379`                        |
| **Container**             | **Executable/Project**   | Host network (`host.docker.internal:port`).  | `host.docker.internal:5000`             |

---

#### Advanced Scenario: Dynamic Endpoint Resolution Across Contexts

Aspire resolves endpoints differently based on the execution context (e.g., run mode vs. publish mode, container vs. executable).  Sometimes you want to override that resolution behavior.

**Scenario**

Below example shows a project that is going to setup up grafana and keycloak. We need to give the project the address for container-to-container communication between grafana and keycloak even though the target resource is a project. The project isn’t directly talking to keycloak or grafana, it's a mediator that is just setting URLs in the appropriate configuration of each container.

### Example: Cross-Context Communication

#### Code Example

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddProject<Projects.Api>("api")
    .WithEnvironment(ctx =>
    {
        var keyCloakEndpoint = keycloak.GetEndpoint("http");
        var grafanaEndpoint = grafana.GetEndpoint("http");

        ctx.EnvironmentVariables["Grafana__Url"] = grafanaEndpoint;

        if (ctx.ExecutionContext.IsRunMode)
        {
            // The project needs to get the URL for keycloak in the context of the container network,
            // but since this is a project, it'll resolve the url in the context of the host network.
            // We get the runtime url and change the host and port to match the container network pattern (host = resource name, port = target port ?? port)
            var keycloakUrl = new UriBuilder(keyCloakEndpoint.Url)
            {
                Host = keycloak.Resource.Name,
                Port = keyCloakEndpoint.TargetPort ?? keyCloakEndpoint.Port,
            };

            ctx.EnvironmentVariables["Keycloak__AuthServerUrl"] = keycloakUrl.ToString();
        }
        else
        {
            // In publish mode let the endpoint resolver handle the URL
            ctx.EnvironmentVariables["Keycloak__AuthServerUrl"] = keyCloakEndpoint;
        }
    });

builder.Build().Run();
```

## API Patterns

Aspire separates **resource data models** from **behavior** using **fluent extension methods**.  
- **Resource classes** define only constructors and properties.  
- **Extension methods** implement resource creation, configuration, and runtime wiring.  

This guide describes each pattern and shows a **verbatim Redis example** at the end. It also covers how to publish manifests via custom resources.

---

## Adding Resources with `AddX(...)`

An `AddX(...)` method will:

1. **Validate inputs** (`builder`, `name`, required arguments).  
2. **Instantiate** the data-only resource (`new TResource(...)`).  
3. **Register** it with `builder.AddResource(resource)`.  
4. **Optional wiring** of endpoints, health checks, container settings, environment variables, command-line arguments, and event subscriptions.

### Signature Pattern

```csharp
public static IResourceBuilder<TResource> AddX(
    this IDistributedApplicationBuilder builder,
    [ResourceName] string name,
    /* optional parameters */)
{
    // 1. Validate inputs
    // 2. Instantiate resource
    // 3. builder.AddResource(resource)
    // 4. Optional wiring:
    //    .WithEndpoint(...)
    //    .WithHealthCheck(...)
    //    .WithImage(...)
    //    .WithEnvironment(...)
    //    .WithArgs(...)
    //    Eventing.Subscribe<...>(...)
}
```

### Optional Wiring Examples

- **Endpoints**:  
  ```csharp
  .WithEndpoint(port: hostPort, targetPort: containerPort, name: endpointName)
  ```
- **Health checks**:  
  ```csharp
  .WithHealthCheck(healthCheckKey)
  ```
- **Container images / registries**:  
  ```csharp
  .WithImage(imageName, imageTag)
  .WithImageRegistry(registryUrl)
  ```
- **Entrypoint & args**:  
  ```csharp
  .WithEntrypoint("/bin/sh")
  .WithArgs(context => { /* build args */ return Task.CompletedTask; })
  ```
- **Environment variables**:  
  ```csharp
  .WithEnvironment(context => new("ENV_VAR", valueProvider))
  ```
- **Event subscriptions**:  
  ```csharp
  builder.Eventing.Subscribe<EventType>(resource, handler);
  ```

### Summary Table

| Step               | Call/Method                         | Purpose                                          |
|--------------------|-------------------------------------|--------------------------------------------------|
| **Validate**       | `ArgumentNullException.ThrowIfNull(...)`                  | Ensure non-null builder, name, and args          |
| **Instantiate**    | `new TResource(name, …)`            | Create data-only instance                        |
| **Register**       | `builder.AddResource(resource)`     | Add resource to the application model            |
| **Optional wiring**| `.WithEndpoint…`, `.WithHealthCheck…`, `.WithImage…`, `.WithEnvironment…`, `.WithArgs…`, `Eventing.Subscribe…` | Configure container details, wiring, and runtime hooks |

---

## Configuring Resources with `WithX(...)`

`WithX(...)` methods **attach annotations** to resource builders.

### Signature Pattern

```csharp
public static IResourceBuilder<TResource> WithFoo(
    this IResourceBuilder<TResource> builder,
    FooOptions options) =>
  builder.WithAnnotation(new FooAnnotation(options));
```

- **Target**: `IResourceBuilder<TResource>`  
- **Action**: `WithAnnotation(...)`  
- **Returns**: `IResourceBuilder<TResource>`  

### Summary Table

| Method        | Target                        | Action                                         |
|---------------|-------------------------------|------------------------------------------------|
| `WithX(...)`  | `IResourceBuilder<TResource>` | Attaches `XAnnotation` via `WithAnnotation`    |
| Returns       | `IResourceBuilder<TResource>` | Enables fluent chaining                        |

---

## Annotations

Annotations are **public** metadata types implementing `IResourceAnnotation`. They can be added or removed dynamically at runtime via hooks or events. Consumers can query annotations using `TryGetLastAnnotation<T>()` when necessary.

### Definition & Attachment

```csharp
public sealed record PersistenceAnnotation(
    TimeSpan? Interval,
    int KeysChangedThreshold) : IResourceAnnotation;

builder.WithAnnotation(new PersistenceAnnotation(
    TimeSpan.FromSeconds(60),
    100));
```

### Summary Table

| Concept          | Pattern                                              | Notes                                               |
|------------------|------------------------------------------------------|-----------------------------------------------------|
| Annotation Type  | `public record XAnnotation(...) : IResourceAnnotation` | Public to support dynamic runtime use             |
| Attach           | `builder.WithAnnotation(new XAnnotation(...))`       | Adds metadata to resource builder                 |
| Query            | `resource.TryGetLastAnnotation<XAnnotation>(out var a)` | Consumers inspect annotations as needed            |

---

## Custom Value Objects

Custom value objects defer evaluation and allow the framework to discover dependencies between resources.

### Core Interfaces

| Interface                        | Member                                      | When Used    | Purpose                                  |
|----------------------------------|---------------------------------------------|--------------|------------------------------------------|
| **`IValueProvider`**             | `ValueTask<string?> GetValueAsync(CancellationToken)` | Run mode      | Resolve live values at runtime           |
| **`IManifestExpressionProvider`**| `string ValueExpression { get; }`           | Publish mode | Emit structured expressions in manifests |
| **`IValueWithReferences`** _(opt.)_| `IEnumerable<object> References { get; }`   | Both (if needed) | Declare dependencies on other resources |

> **Implement** `IValueProvider` and `IManifestExpressionProvider` on all structured value types.  
> **Implement** `IValueWithReferences` only when your type holds resource references.

### Attaching to Resources

```csharp
builder.WithEnvironment(context =>
    new("REDIS_CONNECTION_STRING", redis.GetConnectionStringAsync));
```

### Example: `BicepOutputReference`

```csharp
public sealed partial class BicepOutputReference : 
    IManifestExpressionProvider, 
    IValueProvider, 
    IValueWithReferences
{
    public string ValueExpression { get; }
    public ValueTask<string?> GetValueAsync(CancellationToken cancellationToken = default);
    IEnumerable<object> IValueWithReferences.References { get; }
}

public static IResourceBuilder<T> WithEnvironment<T>(
    this IResourceBuilder<T> builder,
    string name,
    BicepOutputReference bicepOutputReference)
    where T : IResourceWithEnvironment
{ /* attaches environment variable from Bicep output */ }
```

### Summary Table

| Concept                          | Pattern                                        | Purpose                                      |
|----------------------------------|------------------------------------------------|----------------------------------------------|
| `IValueProvider`                 | `GetValueAsync(...)`                           | Deferred runtime resolution                  |
| `IManifestExpressionProvider`    | `ValueExpression`                              | Structured publish-time expression           |
| `IValueWithReferences` _(opt.)_  | `References`                                   | Declare resource dependencies                |
| `WithEnvironment(...)`           | `new("NAME", valueProvider)`                 | Attach structured values unflattened         |

---

## Manifest Publishing & Resource Serialization

Custom resources that publish JSON manifest entries must:

1. **Register a callback** using `ManifestPublishingCallbackAnnotation` in the constructor.  
2. **Implement the callback** to write JSON via `ManifestPublishingContext.Writer`.  
3. **Use value objects** (`IManifestExpressionProvider`) for structured fields.

Resources can opt out of being included in the publishing manifest entirely by calling the `ExcludeFromManifest()` extension method on the `IResourceBuilder<T>`. Resources marked this way will be omitted when generating publishing assets like Docker Compose files or Kubernetes manifests.

### Registering the Callback

```csharp
public class AzureBicepResource : Resource, IAzureResource
{
    public AzureBicepResource(string name, ...) : base(name)
    {
        Annotations.Add(new ManifestPublishingCallbackAnnotation(WriteToManifest));
    }
}
```

### Writing to the Manifest

```csharp
public virtual void WriteToManifest(ManifestPublishingContext context)
{
    context.Writer.WriteString("type", "azure.bicep.v0");
    context.Writer.WriteString("path", context.GetManifestRelativePath(path));

    context.Writer.WriteStartObject("params");
    foreach (var kv in Parameters)
    {
        context.Writer.WritePropertyName(kv.Key);
        var v = kv.Value is IManifestExpressionProvider p ? p.ValueExpression : kv.Value?.ToString();
        context.Writer.WriteString(kv.Key, v ?? "");
        context.TryAddDependentResources(kv.Value);
    }
    context.Writer.WriteEndObject();
}
```

### Summary Table

| Step                       | API / Call                                    | Purpose                                  |
|----------------------------|-----------------------------------------------|------------------------------------------|
| Register callback          | `Annotations.Add(new ManifestPublishingCallbackAnnotation(WriteToManifest))` | Hook custom JSON writer                  |
| Implement `WriteToManifest`| Use `context.Writer` to emit JSON properties  | Define resource manifest representation  |
| Structured fields          | `IManifestExpressionProvider.ValueExpression`| Ensure publish-time placeholders are preserved |

---

## Key Conventions

| Convention                       | Rationale                                       |
|----------------------------------|-------------------------------------------------|
| Data-only resource classes       | Separates data model from behavior              |
| `*BuilderExtensions` classes     | Groups all API methods per integration          |
| Public annotations               | Allow dynamic runtime addition/removal          |
| `[ResourceName]` attribute        | Enforces valid resource naming at compile time  |
| Preserve parameter/value objects | Ensures deferred evaluation of secrets/outputs  |

---

## Full Examples

This section contains complete, runnable examples demonstrating key concepts.

### Example: Derived Container Resource (Redis)

This example shows how to create a custom resource (`RedisResource`) that derives from `ContainerResource` and implements `IResourceWithConnectionString`. It demonstrates:
- Defining a data-only resource class.
- Implementing `IResourceWithConnectionString` with deferred evaluation using `ReferenceExpression`.
- Creating an `AddRedis` extension method that handles parameter validation, password management, event subscription, health checks, and container configuration using fluent APIs.

```csharp
// AddRedis Extension Method
// This extension method provides a convenient way to add a Redis resource to the Aspire application model.
public static IResourceBuilder<RedisResource> AddRedis(
    this IDistributedApplicationBuilder builder, // Extends the main application builder interface.
    [ResourceName] string name,                   // The unique name for this Redis resource.
    int? port = null,                             // Optional host port mapping.
    IResourceBuilder<ParameterResource>? password = null) // Optional parameter resource for the password.
{
    // 1. Validate inputs before any side effects
    // Ensure the builder and name are not null to prevent downstream errors.
    ArgumentNullException.ThrowIfNull(builder);
    ArgumentNullException.ThrowIfNull(name);

    // 2. Preserve or generate the password ParameterResource (deferred evaluation)
    // If a password parameter is provided, use it. Otherwise, create a default one.
    // ParameterResource allows the actual password value to be resolved later (e.g., from secrets).
    var passwordParameter = password?.Resource
        ?? ParameterResourceBuilderExtensions.CreateDefaultPasswordParameter(
               builder, $"{name}-password", special: false); // Creates a default password parameter if none is supplied.

    // 3. Instantiate the data-only RedisResource with its password parameter
    // Create the RedisResource instance, passing the name and the (potentially deferred) password parameter.
    var redis = new RedisResource(name, passwordParameter);

    // Variable to hold the resolved connection string at runtime.
    string? connectionString = null;

    // 4. Subscribe to ConnectionStringAvailableEvent to capture the connection string at runtime
    // This event hook allows capturing the connection string *after* it has been resolved
    // by the Aspire runtime, including potentially allocated ports and resolved parameter values.
    builder.Eventing.Subscribe<ConnectionStringAvailableEvent>(redis, async (@event, ct) =>
    {
        // Resolve the connection string using the resource's method.
        connectionString = await redis.GetConnectionStringAsync(ct).ConfigureAwait(false);
        // Ensure the connection string was actually resolved.
        if (connectionString == null)
        {
            throw new DistributedApplicationException(
                $"Connection string for '{redis.Name}' was unexpectedly null.");
        }
    });

    // 5. Register a health check that uses the connection string once it becomes available
    // Define a unique key for the health check.
    var healthCheckKey = $"{name}_check";
    // Add a Redis-specific health check to the application's health check services.
    // The lambda `_ => connectionString ?? ...` ensures the health check uses the
    // connection string *after* it has been resolved by the event handler above.
    builder.Services
           .AddHealthChecks()
           .AddRedis(_ => connectionString
                             ?? throw new InvalidOperationException("Connection string is unavailable"), // Throw if accessed too early.
                     name: healthCheckKey); // Name the health check for identification.

    // 6. Add & configure container using the fluent builder pattern
    // Add the RedisResource instance to the application model.
    return builder.AddResource(redis)
                  // 6.a Expose the Redis TCP endpoint
                  // Map the host port (if provided) to the container's default Redis port (6379).
                  // Name the endpoint "tcp" for reference.
                  .WithEndpoint(
                      port: port,                             // Optional host port.
                      targetPort: 6379,                       // Default Redis port inside the container.
                      name: RedisResource.PrimaryEndpointName) // Use the constant defined in RedisResource.
                  // 6.b Specify container image and tag
                  // Define the Docker image to use for the Redis container.
                  .WithImage(RedisContainerImageTags.Image, RedisContainerImageTags.Tag)
                  // 6.c Configure container registry if needed
                  // Specify a container registry if the image is not on Docker Hub.
                  .WithImageRegistry(RedisContainerImageTags.Registry)
                  // 6.d Wire the health check into the resource
                  // Associate the previously defined health check with this resource.
                  // Aspire uses this for dashboard status and orchestration.
                  .WithHealthCheck(healthCheckKey)
                  // 6.e Define the container’s entrypoint
                  // Override the default container entrypoint if necessary. Here, it's set to use shell.
                  .WithEntrypoint("/bin/sh")
                  // 6.f Pass the password ParameterResource into an environment variable
                  // Set environment variables for the container. This uses a callback to access
                  // the resource instance (`redis`) and its properties.
                  .WithEnvironment(context =>
                  {
                      // If a password parameter exists, expose it as the REDIS_PASSWORD environment variable.
                      // The actual value resolution happens later via the ParameterResource.
                      if (redis.PasswordParameter is { } pwd)
                      {
                          context.EnvironmentVariables["REDIS_PASSWORD"] = pwd;
                      }
                  })
                  // 6.g Build the container arguments lazily, preserving annotations
                  // Define the command-line arguments for the container. This also uses a callback
                  // to allow dynamic argument construction based on resource state or annotations.
                  .WithArgs(context =>
                  {
                      // Start with the basic command to run the Redis server.
                      var cmd = new List<string> { "redis-server" };

                      // If a password parameter is set, add the necessary Redis CLI arguments.
                      // Note: It uses the environment variable name set earlier ($REDIS_PASSWORD).
                      if (redis.PasswordParameter is not null)
                      {
                          cmd.Add("--requirepass");
                          cmd.Add("$REDIS_PASSWORD"); // Reference the environment variable.
                      }

                      // Check if a PersistenceAnnotation has been added to the resource.
                      // Annotations allow adding optional configuration or behavior.
                      if (redis.TryGetLastAnnotation<PersistenceAnnotation>(out var pa))
                      {
                          // If persistence is configured, add the corresponding Redis CLI arguments.
                          var interval = (pa.Interval ?? TimeSpan.FromSeconds(60))
                              .TotalSeconds
                              .ToString(CultureInfo.InvariantCulture);
                          cmd.Add("--save");
                          cmd.Add(interval); // Save interval in seconds.
                          cmd.Add(pa.KeysChangedThreshold.ToString(CultureInfo.InvariantCulture)); // Number of key changes threshold.
                      }

                      // Finalize the arguments for the shell entrypoint.
                      context.Args.Add("-c"); // Argument for /bin/sh to execute a command string.
                      context.Args.Add(string.Join(' ', cmd)); // Join all parts into a single command string.
                      return Task.CompletedTask; // Return a completed task as the callback is synchronous.
                  });
}
```

```csharp
// RedisResource Class
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

// Data-only Redis resource derived from ContainerResource.
// It implements IResourceWithConnectionString to provide connection details.
public class RedisResource(string name)
    // Inherits common container properties and behaviors from ContainerResource.
    : ContainerResource(name),
    // Implements this interface to indicate it can provide a connection string.
    IResourceWithConnectionString
{
    // Constant for the primary endpoint name, used for consistency.
    internal const string PrimaryEndpointName = "tcp";

    // Backing field for the lazy-initialized primary endpoint reference.
    private EndpointReference? _primaryEndpoint;

    // Public property to get the EndpointReference for the primary "tcp" endpoint.
    // EndpointReference allows deferred access to endpoint details (host, port, URL).
    // It's lazy-initialized on first access.
    public EndpointReference PrimaryEndpoint
        => _primaryEndpoint ??= new(this, PrimaryEndpointName);

    // Property to hold the ParameterResource representing the Redis password.
    // ParameterResource allows the password value to be resolved later (e.g., from secrets).
    public ParameterResource? PasswordParameter { get; private set; }

    // Constructor that accepts a password ParameterResource.
    public RedisResource(string name, ParameterResource password)
        : this(name) // Call the base constructor.
    {
        PasswordParameter = password; // Store the provided password parameter.
    }

    // Helper method to build the ReferenceExpression for the connection string.
    // ReferenceExpression captures the structure of the connection string, including
    // references to endpoints and parameters, allowing deferred resolution.
    private ReferenceExpression BuildConnectionString()
    {
        // Use a builder to construct the expression piece by piece.
        var builder = new ReferenceExpressionBuilder();
        // Append the host and port part, referencing the PrimaryEndpoint properties.
        // .Property() ensures deferred resolution suitable for both run and publish modes.
        builder.Append($"{PrimaryEndpoint.Property(EndpointProperty.HostAndPort)}");
        // If a password parameter exists, append it to the connection string format.
        if (PasswordParameter is not null)
        {
            // Append the password parameter directly; ReferenceExpression handles its deferred resolution.
            builder.Append($",password={PasswordParameter}");
        }
        // Build and return the final ReferenceExpression.
        return builder.Build();
    }

    // Implementation of IResourceWithConnectionString.ConnectionStringExpression.
    // Provides the connection string as a ReferenceExpression, suitable for publish mode
    // where concrete values aren't available yet.
    public ReferenceExpression ConnectionStringExpression =>
        BuildConnectionString();
}
```

### Example: Custom Resource - Talking Clock

This example demonstrates creating a completely custom resource (`TalkingClockResource`) that doesn't derive from built-in types. It shows:
- Defining a simple resource class.
- Implementing a custom lifecycle hook (`TalkingClockLifecycleHook`) to manage the resource's behavior (starting, logging, state updates).
- Using `ResourceLoggerService` for per-resource logging.
- Using `ResourceNotificationService` to publish state updates.
- Creating an `AddTalkingClock` extension method to register the resource and its lifecycle hook.

```csharp
// Define the custom resource type. It inherits from the base Aspire 'Resource' class.
// This class is primarily a data container; Aspire behavior is added via eventing and extension methods.
public sealed class TalkingClockResource(string name, ClockHandResource tickHand, ClockHandResource tockHand) : Resource(name)
{
    public ClockHandResource TickHand { get; } = tickHand; // The tick hand resource instance.
    public ClockHandResource TockHand { get; } = tockHand; // The tock hand resource instance.
}

public sealed class ClockHandResource(string name) : Resource(name);

// Define Aspire extension methods for adding the TalkingClockResource to the application builder.
// This provides a fluent API for users to add the custom resource.
public static class TalkingClockExtensions
{
    // The main Aspire extension method to add a TalkingClockResource.
    public static IResourceBuilder<TalkingClockResource> AddTalkingClock(
        this IDistributedApplicationBuilder builder, // Extends the Aspire application builder.
        string name)                                 // The name for this resource instance.
    {
        // Create a new instance of the TalkingClockResource.
        var tickHandResource = new ClockHandResource(name + "-tick-hand");
        var tockHandResource = new ClockHandResource(name + "-tock-hand");
        var clockResource = new TalkingClockResource(name, tickHandResource, tockHandResource);

        // Add the resource instance to the Aspire application builder and configure it using fluent APIs.
        var clockBuilder = builder.AddResource(clockResource)
            // Use Aspire's ExcludeFromManifest to prevent this resource from being included in deployment manifests.
            .ExcludeFromManifest()
            // Set a URL for the resource, which will be displayed in the Aspire dashboard.
            .WithUrl("https://www.speaking-clock.com/", "Speaking Clock")
            // Use Aspire's WithInitialState to set an initial state snapshot for the resource.
            // This provides initial metadata visible in the Aspire dashboard.
            .WithInitialState(new CustomResourceSnapshot // Aspire type for custom resource state.
            {
                ResourceType = "TalkingClock", // A string identifying the type of resource for Aspire, this shows in the dashboard.
                CreationTimeStamp = DateTime.UtcNow,
                State = KnownResourceStates.NotStarted, // Use an Aspire well-known state.
                // Add custom properties displayed in the Aspire dashboard's resource details.
                Properties =
                [
                    // Use Aspire's known property key for source information.
                    new(CustomResourceKnownProperties.Source, "Talking Clock")
                ]
            });

        clockBuilder.OnInitializeResource(static async (resource, initEvent, token) =>
        {
            // This event is published when the resource is initialized.
            // You add custom logic here to establish the lifecycle for your custom resource.

            var log = initEvent.Logger; // Get the logger for this resource instance.
            var eventing = initEvent.Eventing; // Get the eventing service for publishing events.
            var notification = initEvent.Notifications; // Get the notification service for state updates.
            var services = initEvent.Services; // Get the service provider for dependency injection.

            // Publish an Aspire event indicating that this resource is about to start.
            // Other components could subscribe to this event for pre-start actions.
            await eventing.PublishAsync(new BeforeResourceStartedEvent(resource, services), token);
            await eventing.PublishAsync(new BeforeResourceStartedEvent(resource.TickHand, services), token);
            await eventing.PublishAsync(new BeforeResourceStartedEvent(resource.TockHand, services), token);

            // Log an informational message associated with the resource.
            log.LogInformation("Starting Talking Clock...");

            // Publish an initial state update to the Aspire notification service.
            // This sets the resource's state to 'Running' and records the start time.
            // The Aspire dashboard and other orchestrators observe these state updates.
            await notification.PublishUpdateAsync(resource, s => s with
            {
                StartTimeStamp = DateTime.UtcNow,
                State = KnownResourceStates.Running // Use an Aspire well-known state.
            });
            await notification.PublishUpdateAsync(resource.TickHand, s => s with
            {
                StartTimeStamp = DateTime.UtcNow,
                State = "Waiting on clock tick" // Custom state string for the tick hand.
            });
            await notification.PublishUpdateAsync(resource.TockHand, s => s with
            {
                StartTimeStamp = DateTime.UtcNow,
                State = "Waiting on clock tock" // Custom state string for the tock hand.
            });

            // Enter the main loop that runs as long as cancellation is not requested.
            while (!token.IsCancellationRequested)
            {
                // Log the current time, associated with the resource.
                log.LogInformation("The time is {time}", DateTime.UtcNow);

                // Publish a custom state update "Tick" using Aspire's ResourceStateSnapshot.
                // This demonstrates using custom state strings and styles in the Aspire dashboard.
                await notification.PublishUpdateAsync(resource,
                    s => s with { State = new ResourceStateSnapshot("Tick", KnownResourceStateStyles.Success) });
                await notification.PublishUpdateAsync(resource.TickHand,
                    s => s with { State = new ResourceStateSnapshot("On", KnownResourceStateStyles.Success) });
                await notification.PublishUpdateAsync(resource.TockHand,
                    s => s with { State = new ResourceStateSnapshot("Off", KnownResourceStateStyles.Info) });

                await Task.Delay(1000, token);

                // Publish another custom state update "Tock" using Aspire's ResourceStateSnapshot.
                await notification.PublishUpdateAsync(resource,
                    s => s with { State = new ResourceStateSnapshot("Tock", KnownResourceStateStyles.Success) });
                await notification.PublishUpdateAsync(resource.TickHand,
                    s => s with { State = new ResourceStateSnapshot("Off", KnownResourceStateStyles.Info) });
                await notification.PublishUpdateAsync(resource.TockHand,
                    s => s with { State = new ResourceStateSnapshot("On", KnownResourceStateStyles.Success) });

                await Task.Delay(1000, token);
            }
        });

        AddHandResource(tickHandResource);
        AddHandResource(tockHandResource);

        return clockBuilder;

        void AddHandResource(ClockHandResource clockHand)
        {
            builder.AddResource(clockHand)
                .WithParentRelationship(clockBuilder) // Establish a parent-child relationship with the TalkingClockResource.
                .WithInitialState(new()
                {
                    ResourceType = "ClockHand",
                    CreationTimeStamp = DateTime.UtcNow,
                    State = KnownResourceStates.NotStarted,
                    Properties =
                    [
                        new(CustomResourceKnownProperties.Source, "Talking Clock")
                    ]
                });
        }
    }
}
```

---

## Glossary

| Extra API Terms | Description |
|-----------------|-------------|
| IResourceAnnotation | Typed metadata object attached to resources. |
| WithAnnotation() | Fluent method to attach typed annotations. |
| ReferenceExpression | Structured formatter preserving value references. |

| Term | Definition |
|------|-----------|
| Resource | Service or infrastructure element in your app. |
| Annotation | Metadata attached to a resource. |
| DAG | Directed acyclic graph. |
| Heterogeneous DAG | DAG containing varied resource types. |
| Publisher | Emits deployment artifacts from the model. |
| Hoisting | Leaving a value unresolved for later substitution. |
| Deferred evaluation | Computing a value only when needed. |
| ResourceNotificationService | Publishes observable state updates. |
| Lifecycle events | Time-based signals for resource transitions. |

---
