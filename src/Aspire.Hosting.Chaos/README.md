# Aspire.Hosting.Chaos

Local-dev fault-injection proxy resource for .NET Aspire. Drops a YARP+middleware container between Aspire resources to inject latency, errors, response drops, rate limits, idempotency-key collisions, header tampering, partial responses, replay duplication, and Azure-SDK-shaped failures.

> **Status: M3, `[Experimental("ASPIRECHAOS001")]`.** The fluent API, runtime policy API, mesh, dashboard commands, and the `Aspire.Hosting.Chaos.Azure` companion are all shipped. The API surface may still change without notice until the M4 public-release gate.

---

## Package family

The chaos-proxy stack ships as five packages with distinct customers:

```
                       ┌──────────────────────────────────────┐
AI agent ──MCP──────▶ │ Aspire.Hosting.Chaos.Mcp             │   container sidecar
                       │  (typed MCP tools for chaos ops)     │   exposing chaos as
                       └────────────────┬─────────────────────┘   MCP tools
                                        │ uses
                                        ▼
C# test/script/CLI ──direct──▶ ┌──────────────────────────────┐
                                │ Aspire.Chaos.Client          │   no AppHost deps
                                │  (ChaosProxyClient + DTOs)   │
                                └────────────────┬─────────────┘
                                                 │ HTTP
                                                 ▼
                               ┌──────────────────────────────┐
                               │  chaos proxy container       │   the actual proxy
                               │  (YARP + middleware)         │   (runtime)
                               └──────────────────────────────┘
                                                 ▲
                                                 │ wires into AppHost
AppHost author ───────▶ ┌────────────────────────┴────────────┐
                         │ Aspire.Hosting.Chaos                │   core: AddChaosProxy,
                         │ (+ .Azure, .DurableTask companions) │   AddChaosProxyMesh,
                         └─────────────────────────────────────┘   Azure-SDK transforms,
                                                                   DTFx-aware helpers
```

| Package | Customer | What it does | When to install |
|---|---|---|---|
| **`Aspire.Hosting.Chaos`** *(this package)* | AppHost author | Wires chaos proxies into your `Program.cs`: `AddChaosProxyMesh()`, `AddChaosProxy("name").WithTarget(...)`. Owns the proxy container. | Always — this is the hub. |
| **`Aspire.Hosting.Chaos.Azure`** | AppHost author | Azure-SDK-shaped transforms (Cosmos throttling, Storage timeouts, KeyVault failures) on top of `AddChaosProxy`. | You're injecting Azure-SDK-shaped faults at the resource level. |
| **`Aspire.Hosting.Chaos.DurableTask`** | C# test author | Typed `ChaosPolicy` factories for DTFx scenarios (e.g., `DtfxChaosPolicies.ActivityReplayRace(...)`). Has no AppHost deps. | You write DTFx replay/timeout repros in C# tests and want compile-time help building the policy body. |
| **`Aspire.Chaos.Client`** | C# test/script/CLI author | Aspire client integration: `AddChaosProxyClient(connectionName)` extension on `IHostApplicationBuilder` registers a typed `ChaosProxyClient` (HTTP client + DTOs) via `IHttpClientFactory` with auto health-check. Also usable directly for non-DI scenarios. **No AppHost deps.** | You write chaos assertions in C# (test runner, console app, CLI). Transitively pulled in by all packages above. |
| **`Aspire.Hosting.Chaos.Mcp`** | AI coding agent | Containerized MCP server. Exposes chaos ops as typed MCP tools (`chaos_install_policy`, `chaos_await_fire`, …) so agents don't hand-author JSON, hunt for proxy URLs, or poll for fire counts. | An AI agent (e.g., Copilot CLI) needs to drive chaos in your AppHost. |

**Net:**
- AppHost authors install `Aspire.Hosting.Chaos` (+ `.Azure` / `.DurableTask` as needed).
- C# tests / scripts add `Aspire.Chaos.Client` (already transitively present if your test project references the AppHost package).
- AI-agent-driven workflows add `Aspire.Hosting.Chaos.Mcp` — the agent then drives chaos via MCP tools instead of curl or C#.

## Installation

```bash
dotnet add package Aspire.Hosting.Chaos
# Optional companion for Azure-SDK-shaped transforms (Cosmos, Storage, KeyVault):
dotnet add package Aspire.Hosting.Chaos.Azure
```

The proxy container is built locally from the package's `container/` source via Aspire's `WithDockerfile`. Each mesh edge builds its own image (a distinct per-edge image name), but every edge's restore/publish/ReadyToRun/cert-bake layers are byte-identical, so the container layer cache serves builds 2..N cheaply — only the first edge of a multi-edge mesh pays the cold build. No registry pull is needed during in-house incubation (D2). Switches to a published image at M4.

## Quick start

> **AppHost-side wiring is one line.** Drop `AddChaosProxyMesh()` after all your `WithReference(...)` / `WithServiceUrl(...)` calls and every edge between your own services gets a pass-through chaos proxy auto-instrumented in front of it. There's no per-edge handwiring, no SDK references for your services to take. Add `.IncludeInfrastructure()` to extend the mesh to datastore edges (Cosmos emulator, Azurite queue). Per-scenario chaos is then **declared at runtime** (typically from your test harness) by POSTing a policy to the proxy — that way your AppHost layout stays static across test sessions and policies change per test.

```csharp
// AppHost (Program.cs)
using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

var backend = builder.AddProject<Projects.MyBackend>("backend");

builder.AddProject<Projects.MyClient>("client")
    .WithReference(backend);

// One mesh call covers every service-tier edge in the AppHost (project/container
// <-> project/container). Each edge gets a pass-through proxy "mesh-{client}-to-{target}".
builder.AddChaosProxyMesh();

// Or, to also fault your datastores (Cosmos emulator, Azurite queue):
// builder.AddChaosProxyMesh().IncludeInfrastructure();

builder.Build().Run();
```

Then from a test (or curl, or the dashboard) — install a policy on the relevant mesh edge:

```bash
# Add 50-150ms latency on every client -> backend request.
curl -X POST http://mesh-client-to-backend:NNNN/chaos/policies \
  -H "Content-Type: application/json" \
  -d '{
    "id": "client-to-backend-latency",
    "matcher": { "method": "GET", "pathPrefix": "/api/things" },
    "latency": { "minMs": 50, "maxMs": 150 }
  }'
```

Mesh proxies are dev-only — they're automatically excluded from the publish manifest via `ExcludeFromManifest()`.

### When to use the per-resource API instead of mesh

The per-resource `builder.AddChaosProxy("name").WithTarget(target)` pattern is the lower-level primitive that mesh is built on. Use it as an **escape hatch** when mesh doesn't apply — see [Escape hatch: per-resource proxy](#escape-hatch-per-resource-proxy):
- Infra types the built-in handlers don't cover (Service Bus AMQP, Postgres, Redis — `.IncludeInfrastructure()` ships Cosmos-emulator + Azurite-queue handlers)
- AppHost-time baseline chaos you want pre-installed on every run of a specific edge (rare — runtime install is preferred)

> Note: connection-string datastore edges (Cosmos emulator, Azurite queue) and custom-env service edges no longer need the escape hatch — use `.IncludeInfrastructure()` and `WithServiceUrl(...)` respectively.

## When to use this

| Scenario | Use this proxy | Use something else |
|----------|----------------|--------------------|
| Reproduce an HTTP-shaped failure on demand (5xx, 429, 409, ETag conflict, etc.) | ✅ | — |
| Validate your SDK retry / backoff handling | ✅ | — |
| Hold an upstream operation in non-terminal state to exercise concurrency guards | ✅ | — |
| Test gRPC-unary fault behavior | ✅ | — |
| L4 network faults (packet loss, latency below TLS, DNS) | — | Toxiproxy, netem |
| Client-side retry/circuit-breaker policy testing | — | Polly + your own mock |
| Production chaos on real Azure resources | — | [Azure Chaos Studio](https://learn.microsoft.com/azure/chaos-studio) |
| gRPC streaming / WebSocket fault injection | — | Not supported (see [Protocols](#protocols)) |

## Use-case recipes

> **Recipes are runtime policy installs against mesh edges**, not new AppHost resources. The AppHost setup you wrote in [Quick start](#quick-start) is enough — every recipe below is a single POST to the relevant `mesh-{client}-to-{target}` proxy. Substitute your own client/target names in the URLs.
>
> Where appropriate I show both the raw `curl` form (works from any test harness or shell) and the equivalent `ChaosProxyClient` C# call (for tests that have a typed reference to the proxy).

### Verify retry-on-5xx logic

```bash
# First request 503s; subsequent succeed.
curl -X POST http://mesh-client-to-backend:NNNN/chaos/policies \
  -H "Content-Type: application/json" \
  -d '{
    "id": "retry-on-5xx",
    "matcher": { "method": "GET", "pathPrefix": "/api/things" },
    "error": { "httpStatus": 503, "failFirst": 1 }
  }'
```

`failFirst: 1` is deterministic — guarantees one failure then recovery. Use over `probability` whenever you're asserting "did the retry path work?"

### Reproduce Cosmos throttling

The Cosmos DB emulator edge is meshed by `.IncludeInfrastructure()` — the mesh stands up an HTTPS-terminating proxy in front of the emulator and rewrites the client's `ConnectionStrings__{name}` automatically (see [Infra tier (opt-in)](#infra-tier-opt-in)). No escape hatch needed. Install the throttle policy at runtime against the generated `mesh-{client}-to-{cosmos}` proxy:

```bash
curl -X POST http://mesh-backend-to-cosmos:NNNN/chaos/policies \
  -H "Content-Type: application/json" \
  -d '{
    "id": "cosmos-throttle",
    "matcher": { "pathContains": "/dbs/mydb/colls/mycol/docs" },
    "cosmosThrottle": { "retryAfterMs": 250, "failFirst": 2 }
  }'
```

Emits the exact wire shape Cosmos returns under throttle (`429 + x-ms-retry-after-ms + x-ms-substatus: 3200`) so the SDK takes its built-in retry branch. The `cosmosThrottle` transform ships in the [`Aspire.Hosting.Chaos.Azure`](../Aspire.Hosting.Chaos.Azure/) companion.

### Reproduce a DTFx replay storm (state-guard 409 family)

DurableTask Framework uses Azure Queue Storage (the Azurite emulator) as its persistence backend. That queue edge is meshed by `.IncludeInfrastructure()` — the mesh proxies the Azurite queue endpoint and rewrites the worker's `ConnectionStrings__{name}` automatically (see [Infra tier (opt-in)](#infra-tier-opt-in)). No escape hatch needed. Install the replay-race policy at runtime against the generated `mesh-{worker}-to-{queues}` proxy:

```bash
# Drop the first TaskCompletedEvent for the named activity. DTFx redelivers
# the work-item after its visibility timeout, the activity re-executes, and
# the downstream service returns its real 409 state-guard.
curl -X POST http://mesh-worker-to-queues:NNNN/chaos/policies \
  -H "Content-Type: application/json" \
  -d '{
    "id": "dtfx-replay-mystuff",
    "matcher": { "dtfxActivityName": "MyOrchestrator_DoStuff_Activity" },
    "dropResponse": { "failFirst": 1, "maxFires": 1 }
  }'
```

For C# test harnesses, the [`Aspire.Hosting.Chaos.DurableTask`](../Aspire.Hosting.Chaos.DurableTask/) companion ships a typed factory that builds the same shape with sensible defaults:

```csharp
using Aspire.Hosting.Chaos.DurableTask;

var policy = DtfxChaosPolicies.ActivityReplayRace(
    activityName: "MyOrchestrator_DoStuff_Activity",
    failFirst: 1,
    maxFires: 1);

await chaosProxyClient.InstallPolicyAsync(policy);
```

The proxy:
1. Observes each `TaskScheduledEvent` flowing through the queue, records (orchestrationInstanceId, taskScheduledId) → activityName
2. On a later `TaskCompletedEvent` with that taskScheduledId, looks up the activity name
3. Fires the drop only when the recorded name matches `dtfxActivityName`

`failFirst:1` fires on the first matching completion per request key; `maxFires:1` caps total fires globally so DTFx queue partition fan-out doesn't multiply your drops.

**The activity name must match the DTFx `[FunctionName(...)]` value** — NOT the C# class name. For Chaos Studio's V2 workspace activities those are `AsyncOperation_Action_TriggerScenarioEvaluation_Workspace`, `AsyncOperation_GetOperation_Workspace`, etc.

### Verify hung-request / client-timeout handling

```bash
# Forwards to upstream, then drops the response. Client sees its own timeout.
curl -X POST http://mesh-client-to-backend:NNNN/chaos/policies \
  -H "Content-Type: application/json" \
  -d '{
    "id": "drop-charge",
    "matcher": { "method": "POST", "pathPrefix": "/api/charge" },
    "dropResponse": {}
  }'
```

Exercises `OperationCanceledException` / `TaskCanceledException` handlers.

### Verify duplicate-side-effect (idempotency) handling

```bash
# Upstream sees TWO requests; client sees ONE response.
curl -X POST http://mesh-client-to-backend:NNNN/chaos/policies \
  -H "Content-Type: application/json" \
  -d '{
    "id": "replay-charge",
    "matcher": { "method": "POST", "pathPrefix": "/api/charge" },
    "replayDuplicate": {}
  }'
```

Direct reproduction of the activity-replay failure mode. Test asserts the side-effect happened exactly once.

### Verify rate-limit backoff

```bash
# 11th request in any 1s window returns 429.
curl -X POST http://mesh-client-to-backend:NNNN/chaos/policies \
  -H "Content-Type: application/json" \
  -d '{
    "id": "rate-limit",
    "matcher": { "pathPrefix": "/api/" },
    "rateLimit": { "requestsPerWindow": 10, "windowMs": 1000 }
  }'
```

Asserts the client either honors `Retry-After` or fails predictably.

### Per-tenant chaos (header-scoped)

```bash
# Affect only one tenant in a shared dev environment.
curl -X POST http://mesh-client-to-backend:NNNN/chaos/policies \
  -H "Content-Type: application/json" \
  -d '{
    "id": "flaky-tenant-503",
    "matcher": { "headerEquals": { "X-Tenant-Id": "flaky-tenant" } },
    "error": { "httpStatus": 503 }
  }'
```

Lets you reproduce per-customer issues without affecting other tenants.

## Escape hatch: per-resource proxy

For edges the mesh's built-in handlers **don't** cover — infra types beyond the Cosmos emulator + Azurite queue that `.IncludeInfrastructure()` ships (Service Bus AMQP, Postgres, Redis, Azure Blob/Table storage, real-Azure Cosmos), or AppHost-time baseline chaos you want pre-installed on a specific edge — wire a per-resource proxy explicitly in the AppHost.

> Cosmos-emulator, Azurite-queue, and custom-env service edges are **not** escape-hatch cases anymore — use `.IncludeInfrastructure()` and [`WithServiceUrl`](#withserviceurl--custom-env-var-service-edges) instead.

```csharp
// AppHost (Program.cs)

// Example: a Postgres edge. The mesh's infra tier ships Cosmos-emulator + Azurite-queue
// handlers only, so a Postgres connection-string edge is skipped (with a visible reason
// in the startup summary). Wire it explicitly and rewire the consumer's connection string.
var pg = builder.AddPostgres("pg");

var pgProxy = builder.AddChaosProxy("chaos-pg")
    .WithTarget(pg, endpointName: "tcp")
    .WaitForStart(pg);

consumer.WithEnvironment(ctx =>
{
    var url = pgProxy.GetEndpoint("http").Url.TrimEnd('/');
    ctx.EnvironmentVariables["ConnectionStrings__pg"] =
        $"Host=...;Port=...;"; // point the consumer's connection string at pgProxy
});
```

Per-resource proxies support the same runtime policy API as mesh proxies — every recipe above works against them. They additionally support **fluent design-time configuration** for the rare case where you want a baseline policy pre-installed on every run of this AppHost (e.g., always inject 50 ms baseline latency on a specific edge in your local-dev loop):

```csharp
// AppHost (Program.cs) — baseline policy installed at AppHost build time.
pgProxy
    .When(method: "POST", pathPrefix: "/api/charge")
    .WithDropResponse(failFirst: 1, maxFires: 1);
```

This compile-time fluent style is the lower-level primitive that mesh + runtime install is built on. Reach for it only when you specifically need design-time configuration; the runtime install pattern (recipes above) is the canonical customer flow.

## Transforms (reference)

All transforms are composable per policy and per request matcher. `probability` (0.0-1.0) and `failFirst` (first N occurrences per request key) are mutually exclusive; when both are omitted, probability defaults to 1.0.

`dropResponse` additionally accepts `maxFires` — a global cap on total fires for that policy (across all request keys). Useful when the protocol fans across many request keys (e.g., DTFx Azure Queue Storage POSTs spread across multiple control-queue partitions) and you want exactly N drops total, not N per key.

| Transform | Description | Builder method |
|-----------|-------------|----------------|
| Latency | Inject uniform-random delay before forwarding | `WithLatency(min, max, ...)` |
| Error | Short-circuit with HTTP status + body | `WithError(httpStatus, body, ...)` |
| Replay-duplicate | Forward normally AND fire a background duplicate to upstream | `WithReplayDuplicate(...)` |
| Drop-response | Hang the request indefinitely (client sees timeout) | `WithDropResponse(probability?, failFirst?, maxFires?)` |
| Rate-limit | Short-circuit with 429 once sliding-window budget exceeded | `WithRateLimit(requestsPerWindow, window, ...)` |
| Header-tamper | Mutate request / response headers (Remove + Set + Add) | `WithHeaderTamper(direction, remove, set, add)` |
| Partial-response | Write headers + partial body then abort mid-stream | `WithPartialResponse(body, advertisedContentLength, ...)` |
| Idempotency-key collision | Reject duplicate idempotency keys with 409 within a sliding window | `WithIdempotencyKeyCollision(window, ...)` |
| Slow-response | Stream a synthesized body at a configurable bytes/sec | `WithSlowResponse(body, bytesPerSecond, ...)` |

Azure-SDK-shaped (in `Aspire.Hosting.Chaos.Azure`):

| Transform | Wire shape |
|-----------|------------|
| `WithCosmosThrottle(retryAfterMs)` | 429 + `x-ms-retry-after-ms` + `x-ms-substatus: 3200` |
| `WithCosmosConcurrencyConflict()` | 449 |
| `WithCosmosServiceUnavailable()` | 503 + `x-ms-substatus: 0` |
| `WithStorageServerBusy()` | 503 + `x-ms-error-code: ServerBusy` |
| `WithStorageEtagMismatch()` | 412 + `x-ms-error-code: ConditionNotMet` |
| `WithKeyVaultThrottle(retryAfterSeconds)` | 429 + `Retry-After` |

Each transform is also available as a declarative `ChaosPolicy` record passed to `WithPolicy(...)` — see [Multi-policy](#multi-policy).

## Matchers

Scope which requests a transform fires on. All non-null fields AND together:

```csharp
proxy
    .When(
        method: "POST",
        pathPrefix: "/api/v1",
        pathContains: "/things",
        headerEquals: new() { ["X-Tenant-Id"] = "flaky-tenant" },
        headerContains: new() { ["User-Agent"] = "Postman" },
        bodyContains: "DoTheThingEvent",
        dtfxActivityName: "MyOrchestrator_DoStuff_Activity")
    .WithError(503);
```

| Matcher field | What it matches | Notes |
|---|---|---|
| `method` | HTTP method (case-insensitive exact) | |
| `pathPrefix` | Path startsWith (case-insensitive) | |
| `pathContains` | Path substring (case-insensitive) | |
| `headerEquals` | Header exact value (first value of multi-valued; case-insensitive name + value) | ALL listed headers must match |
| `headerContains` | Header substring (case-insensitive) | ALL listed headers must match |
| `bodyContains` | Request body substring (case-insensitive) | Triggers per-request body buffering (1 MB cap). Bodies over the cap are treated as non-matching. Azure Queue Storage envelopes are auto-base64-decoded so the substring search reaches the inner message text. |
| `dtfxActivityName` | DurableTask Framework activity name (case-sensitive `[FunctionName(...)]` value) | Fires only on DTFx `TaskCompletedEvent` queue messages whose corresponding `TaskScheduledEvent` (observed earlier by the proxy) recorded this activity name. Auto-correlates by `(InstanceId, TaskScheduledId)` so it works across multiple in-flight orchestrations. **For typed helpers, install the [`Aspire.Hosting.Chaos.DurableTask`](../Aspire.Hosting.Chaos.DurableTask/) companion package.** See the [DTFx replay storm recipe](#reproduce-a-dtfx-replay-storm-state-guard-409-family). |

## Multi-policy

For tests that need different chaos on different paths, install multiple policies via `WithPolicy(ChaosPolicy)` — they accumulate. First-installed-wins on matcher overlap per transform type:

```csharp
proxy
    .WithPolicy(new ChaosPolicy
    {
        Id = "cosmos-throttle",
        Matcher = new ChaosMatcher { PathPrefix = "/cosmos/" },
        Error = new ChaosError { Status = 429, Headers = new() { ["x-ms-retry-after-ms"] = "250" } },
    })
    .WithPolicy(new ChaosPolicy
    {
        Id = "storage-slow",
        Matcher = new ChaosMatcher { PathPrefix = "/storage/" },
        Latency = new ChaosLatency { Min = TimeSpan.FromMilliseconds(500), Max = TimeSpan.FromSeconds(1) },
    });
```

## Mesh (deep dive)

[Quick start](#quick-start) introduced `AddChaosProxyMesh()` as the canonical way to drop chaos into an AppHost. Here's the full reference.

The mesh derives its scope from Aspire's resource model **by type**, not from a hand-maintained edge list. There are two tiers:

### Service tier (default)

```csharp
builder.AddChaosProxyMesh();
```

Meshes every edge where **both** endpoints are your own services — a `ProjectResource` (from `AddProject<T>`) or an author-added `ContainerResource` (from `AddContainer` / `AddWireMock` / …) — discovered by type. Each edge gets a pass-through `mesh-{client}-to-{target}` proxy, and the client's service discovery (`services__{target}__http__0`) is rewired through it. This answers "what if my own service B is slow / erroring / returns 409?" and is the zero-config default.

**Eligibility rules for a service edge:**
- Neither side is itself a `ChaosProxyResource` (idempotent — calling twice is safe)
- Neither side is a managed-infra / `IResourceWithConnectionString` resource (those are the infra tier)
- Target exposes an `http` endpoint
- Client is `IResourceWithEnvironment` (mesh needs to override `services__{target}__http__0`)
- The proxy resource name doesn't already exist

### `WithServiceUrl` — custom env-var service edges

If a client reads a service URL from a **custom env var** instead of Aspire service discovery (e.g. `WORKSPACES__SERVICEBASEURL`), the mesh can't see that edge from an opaque `WithEnvironment` delegate. Declare it with `WithServiceUrl` instead — it sets the env var to the target's `http` endpoint **and** records a binding the mesh reads, so the edge is auto-routed through its proxy:

```csharp
// Replaces a raw WithEnvironment("WORKSPACES__SERVICEBASEURL", workspaces.GetEndpoint("http")).
armGatewayApi.WithServiceUrl("WORKSPACES__SERVICEBASEURL", workspacesApi);

builder.AddChaosProxyMesh(); // discovers the binding, overrides the env var with the proxy URL.
```

### Infra tier (opt-in)

```csharp
builder.AddChaosProxyMesh().IncludeInfrastructure();
```

Additionally meshes connection-string edges to managed-infra resources, with protocol-aware interception handlers. v1 ships handlers for the Azure emulators:

- **Cosmos DB emulator** — terminates TLS on the proxy's `https` listener (the Cosmos SDK in Gateway mode won't dial `http`), rewrites `ConnectionStrings__{name}` to the proxy with `DisableServerCertificateValidation=True`, and waits for the emulator container to **START** (not be HEALTHY) so a slow `/ready` check never wedges startup.
- **Azurite Storage queue** — rewrites `ConnectionStrings__{name}` so the queue SDK dials the proxy's `http` endpoint.

Unknown infra types (Service Bus AMQP, Postgres, Redis, blob/table, …) are **skipped with a visible reason** in the startup summary — never silently — and remain wireable via the [per-resource escape hatch](#escape-hatch-per-resource-proxy).

### Attribute-based exclusion (not a name list)

`AddChaosProxyMesh` accepts an optional **exclusion** predicate. Return `true` to exclude an edge. Use it for attribute/type-based exclusions (cost / blast-radius control), not a name allowlist:

```csharp
builder.AddChaosProxyMesh(excludeEdge: (client, target) =>
    target.HasAnnotation<DashboardResourceAnnotation>());
```

### Observability (no silent no-ops)

On startup the mesh logs a structured summary (prefix `[Aspire.Hosting.Chaos.Mesh]`) of every candidate edge — meshed, or skipped with the reason (non-http endpoint, no env support, unknown infra type, …). The same data is available programmatically:

```csharp
var mesh = builder.AddChaosProxyMesh().IncludeInfrastructure();
foreach (var edge in mesh.Summary)
{
    Console.WriteLine(edge); // MESHED / SKIPPED [tier/provider] client -> target ...
}
```

### Architecture: pluggable edge providers

Discovery + interception are organized as edge providers, each of which enumerates edges of its kind and knows how to redirect the client: `ServiceDiscoveryEdgeProvider` (service tier — `services__` override + `WithServiceUrl` bindings) and `ConnectionStringEdgeProvider` (infra tier). New resource types / protocols are additive.

Mesh proxies are pass-through by default (no transforms pre-installed). Policies are installed at runtime via the API below — perfect for harness-driven scenarios.


## Resource-aware random chaos

The transforms above are *deterministic*: you specify the exact fault. That's right for **reproducing a known bug**. For the other half of resilience work — **validating that a feature degrades gracefully under the failures its dependencies actually produce** — use `WithRandomChaos`.

It samples (weighted, seeded) the faults that are *realistic for each resource type* from a built-in **fault profile**, and applies one per firing request. One line arms it across the whole mesh, auto-picking the profile from each edge's target type:

```csharp
// Fault every meshed edge with the failures reasonable for its target type.
builder.AddChaosProxyMesh()
       .WithRandomChaos(intensity: 0.1, seed: 1234)   // seed optional; auto-generated + logged if omitted
       .IncludeInfrastructure();
//   service→service edges → service.http profile (500 / 503 / 504 / latency / drop)
//   service→Cosmos edge    → azure.cosmos profile (429 / 449 / 412 / 503 + latency)
//   service→Storage queue  → azure.storagequeue profile (503 ServerBusy / 412 + latency)
```

- **`intensity`** (0–1) is the per-request fire probability. Default `0.1` — the feature mostly works, faults surface.
- **`seed`** makes the fault stream reproducible. Each proxy derives a stable sub-seed, so one global seed reproduces the whole mesh.
- **Safety rails:** health/readiness/startup paths are excluded by default; an optional `MaxFires` caps total fires per proxy; a paused mesh and pass-through invariants are preserved.

Tune per-profile, or arm a single proxy directly:

```csharp
builder.AddChaosProxyMesh().IncludeInfrastructure().WithRandomChaos(intensity: 0.1, seed: 1234,
    configure: o =>
    {
        o.ProfileIntensity["azure.cosmos"] = 0.25;     // hit Cosmos edges harder
        o.MaxFires = 50;                                // global blast-radius cap per proxy
        o.ExcludePaths.Add("/metrics");
    });

// Single edge, explicit profile:
builder.AddChaosProxy("chaos-gw-be").InterceptCallsFrom(gw).To(be)
       .WithRandomChaos(intensity: 0.25, profileId: "service.http");
```

You can also install a random policy at runtime (same `POST /chaos/policies`, with a `randomFault` body) or per-test via the harness.

### Freeze a random run into a deterministic repro

When random chaos breaks a feature, `POST /chaos/freeze` converts the fired-fault log into a deterministic `chaos_policies[]` block (one `failFirst:1` policy per distinct fault, scoped to the request it fired on). Drop that block into a target-config to reproduce exactly what broke — standalone, no randomness — and hand it to the bug-fix loop.


## Runtime policy API (for test harnesses)

Each proxy exposes an HTTP API for installing / inspecting policies on an already-running AppHost. Design assumption: AppHost topology is static across the test session; policies change per test.

**Install + teardown:**
- `POST /chaos/policies` — install one policy (returns `{ id }`)
- `POST /chaos/policies/bulk` — install N atomically; validates the whole batch before any go live
- `POST /chaos/preview-policy` — validate a policy + return the canonical shape it WOULD take, without installing
- `DELETE /chaos/policies/{id}` — tear one down
- `DELETE /chaos/policies` — wipe all + reset all chaos state (fire counters, failFirst counters, rate-limit windows, idempotency-key cache, fire-once triggers). Pause flag is preserved.

**Inspect:**
- `GET /chaos/policies` — list active policies inline with their fire counts
- `GET /chaos/policies/{id}` — single-policy view
- `GET /chaos/policies/{id}/fire-counts` — just the counters; smallest assertion target
- `GET /chaos/state` — `{ paused, policyCount, totalFireCount, fireCountsByTransform, armedFireOnceTriggers }`
- `POST /chaos/match` — predict which policies would fire for a hypothetical `{ path, method?, headers? }`
- `GET /chaos/healthz` — `200 OK`

**Targeted triggers:**
- `POST /chaos/fire-once?transform=X` — arm a global trigger; next matching request fires regardless of probability/failFirst
- `POST /chaos/policies/{id}/fire-once?transform=X` — same, scoped to one policy
- `POST /chaos/pause` / `POST /chaos/resume` — global "all transforms off" toggle (idempotent)

**Lifecycle:**
- `POST /chaos/policies/{id}/extend?seconds=N` — bump TTL without reinstall; `seconds=0` clears expiry
- `DELETE /chaos/policies/{id}/fire-counts` — reset counters without touching the policy

Policies installed via the runtime API default to a 5-minute TTL (safety net against orphans).

### Typed client: `ChaosProxyClient`

For harnesses that prefer typed methods over raw HttpClient + JSON:

```csharp
var http = new HttpClient { BaseAddress = new Uri("http://chaos-backend:5000") };
var chaos = new ChaosProxyClient(http);

var id = await chaos.InstallPolicyAsync(new ChaosPolicy
{
    Matcher = new ChaosMatcher { PathPrefix = "/api/" },
    Error = new ChaosError { Status = 503, Probability = 1.0 },
});

// exercise the system under test...
var counts = await chaos.GetFireCountsAsync(id);
Assert.True(counts!["error"] > 0);

await chaos.RemovePolicyAsync(id);
```

Every `/chaos/*` endpoint has a matching method. Non-success responses surface the server's error body in the thrown `HttpRequestException`.

## Dashboard commands

Every chaos proxy exposes these commands in the Aspire dashboard:

| Command | HTTP endpoint | Effect |
|---------|---------------|--------|
| pause-faults | `POST /chaos/pause` | Stop firing transforms; proxy still forwards traffic |
| resume-faults | `POST /chaos/resume` | Resume firing after a pause |
| fire-once-latency | `POST /chaos/fire-once?transform=latency` | Next matching request fires regardless of gates |
| fire-once-error | `POST /chaos/fire-once?transform=error` | Same for error |
| fire-once-replay | `POST /chaos/fire-once?transform=replay-duplicate` | Same for replay-duplicate |

Each proxy also surfaces one-click links in the dashboard's resource details panel: **Chaos state** (`/chaos/state`), **Installed policies** (`/chaos/policies`), **Health probe** (`/chaos/healthz`).

## Protocols

HTTP/1.1, HTTP/2 (h2c + h2), and gRPC unary all work through the pipeline — the middleware operates on `HttpContext` which is protocol-agnostic. Validated by `ChaosHttp2ProtocolTests`.

**Not supported:** gRPC streaming + WebSockets. Response-buffering transforms (PartialResponse, SlowResponse) will swallow long-lived bidirectional streams. Use Latency, Error, RateLimit, HeaderTamper, ReplayDuplicate, or DropResponse for stream-shaped traffic.

## OTLP traces

When a transform fires, the proxy tags the ASP.NET Core request span AND emits a `chaos.{transform}` child span:

- `chaos.proxy.fired` = `true`
- `chaos.proxy.{transform}.policy_id` — which policy fired
- `chaos.proxy.{transform}.fire_reason` — `probability` / `fail-first` / `fire-once` / `rate-exceeded`
- Transform-specific: `delay_ms`, `status`, `written_bytes`, `advertised_bytes`, etc.

## OTLP metrics

Meter named `Aspire.Hosting.Chaos`, auto-discovered by the dashboard's Metrics tab:

- `chaos.proxy.fires` (Counter, unit `{fire}`) — one per transform fire, tagged with `policy_id`, `transform`, `fire_reason`
- `chaos.proxy.policies.active` (ObservableGauge, unit `{policy}`) — count of currently-installed policies

## Beyond direct use: workflow integration

For autonomous test workflows that need chaos as part of bug reproduction, the [`run-to-green`](../../../.github/skills/run-to-green/) workflow (Chaos Studio internal) supports declaring chaos policies directly on the test definition:

| `arm` value (on a `chaos[]` entry) | When the runner installs the policy | Use for |
|---|---|---|
| `pre-install` (or top-level `chaos_policies` on the target-config) | BEFORE the test runs (workflow's `install_chaos_policy` step) | Wire-level faults active for the whole test (HTTP edge errors, latency, etc.) |
| `after-workspace-ready` (the DEFAULT — omit `arm`) | AFTER `WaitForWorkspaceReady`, before the first scenario/refresh action | DTFx-replay state-guard bugs — chaos must NOT fire during workspace creation, only during the test's actual bug-trigger action |

All entries share the same per-entry wire shape (`target` + `matcher` + transform config + `ttlSeconds`); `arm` only selects the phase. The workflow:
1. `install_chaos_policy` posts the `pre-install` entries (and target-config `chaos_policies`) to each `target` proxy via the runtime API
2. `emit_chaos_proxy_endpoints` writes the proxy-name → URL map to `tmp/run-to-green/chaos-proxy-endpoints.json`
3. `invoke-runner.ps1` exports the map to the runner as `CHAOS_PROXY_ENDPOINTS`
4. The runner's `ArmDeferredChaosPolicies` step (between `WaitForWorkspaceReady` and refresh) posts the `after-workspace-ready` entries to the right proxies
5. `teardown_chaos_policy` captures fire counts into the validation receipt

See:
- [`target-config-chaos-policies-spec.md`](../../../docs/projects/aspire-chaos-proxy/target-config-chaos-policies-spec.md) — wire shape
- [`chaos-hypothesis-catalog.md`](../../../docs/projects/aspire-chaos-proxy/chaos-hypothesis-catalog.md) — symptom-to-transform mapping with 11 patterns

## Container

During in-house incubation (D2 in the design doc), the container is built locally from the package's `container/` directory via Aspire's `WithDockerfile`. No image pull required. M4 will switch to a published image.

**Startup cost.** Each mesh edge builds its own image via `WithDockerfile` (a distinct per-edge image name). That's deliberate: Aspire 13.3.5's `WithDockerfile` derives the build's output tag from the resource name plus a random per-build hash and ignores any `WithImage` tag, so a "build once, share via a fixed `WithImage` tag" scheme would leave every non-owner proxy pointed at a tag no build produced — a hard `image not found` at container create on a clean image cache. Per-edge builds stay cheap because every edge's restore/publish/ReadyToRun/cert-bake layers are byte-identical, so the container layer cache serves builds 2..N and only the first edge pays the cold build. The dev HTTPS cert is **baked into the image at build time** (`--generate-dev-cert`) so each container start loads it rather than doing an RSA-2048 keygen per start, and the publish is **ReadyToRun** for faster cold starts — together these are what keep `mesh-*` sidecars from piling up in `Created` under contention. (A correct true "build once" would need a real pre-provision build step that emits a fixed tag before DCP runs; that's a separate follow-up, not `WithImage` tag-matching.)

The container is a small ASP.NET Core app that hosts YARP-as-library plus the chaos middleware (latency → header-tamper → idempotency-collision → error → rate-limit → partial-response → slow-response → drop-response → replay-duplicate → YARP). Configuration via environment variables — the AppHost author calls `.WithLatency(...)` etc., translated to `CHAOS_*` env vars the container reads at startup. Multi-policy configs serialize to `CHAOS_POLICIES_JSON` so the full set survives a flat env-var transit.

## Design doc

Full design rationale, decisions, and roadmap live at [`docs/projects/aspire-chaos-proxy/aspire-chaos-proxy.plan.md`](../../../docs/projects/aspire-chaos-proxy/aspire-chaos-proxy.plan.md).
