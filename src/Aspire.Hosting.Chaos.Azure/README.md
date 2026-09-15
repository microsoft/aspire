# Aspire.Hosting.Chaos.Azure

Companion package to [Aspire.Hosting.Chaos](../Aspire.Hosting.Chaos/) adding **Azure-SDK-shaped fault transforms**. Each transform emits the exact response shape (status code + headers + body) that the corresponding Azure SDK retry policy expects, so the SDK takes the matching retry branch rather than falling through to a generic error path.

> **Status: in-house incubation.** Marked `[Experimental("ASPIRECHAOS001")]` (inherited from the core). API may change without notice until the M4 public-release gate.

> **New to the chaos packages?** See the [package family overview](../Aspire.Hosting.Chaos/README.md#package-family) in the core README for how this fits with `Aspire.Chaos.Client`, `Aspire.Hosting.Chaos.DurableTask`, and `Aspire.Hosting.Chaos.Mcp`.

## Why a companion package?

Per D9 in the design doc, the Azure-shaped transforms live in this companion (rather than the core `Aspire.Hosting.Chaos`) so non-Azure Aspire users don't pull `Microsoft.Azure.Cosmos` / `Azure.Storage.Blobs` / etc. as transitive dependencies. This follows the `Aspire.Hosting.Azure.{Service}` ProjectReference-the-base pattern (e.g., `Aspire.Hosting.Azure.Redis` project-references `Aspire.Hosting.Azure`), but extends an existing resource type (`ChaosProxyResource`) rather than introducing a new one — a shape not yet seen elsewhere in Aspire.

This first slice ships **3 transforms** covering the design doc's primary Azure-emulator targets:
- `WithCosmosThrottle` — Cosmos DB 429 RU throttling
- `WithStorageServerBusy` — Azure Storage 503 ServerBusy
- `WithKeyVaultThrottle` — Key Vault 429 throttling

The remaining 4 (Cosmos concurrency conflict, Cosmos service unavailable, Storage etag mismatch, ServiceBus duplicate delivery) arrive in subsequent slices.

## Quick start

> Mesh is the canonical wiring for HTTP edges (see the [core README](../Aspire.Hosting.Chaos/README.md#quick-start)). Azure-SDK-shaped transforms target the **non-mesh-eligible** Azure emulators — Cosmos DB is connection-string-only, Azure Storage's Blob/Queue/Table endpoints aren't named `http`, Key Vault uses connection-string init. For these you wire a **per-resource proxy** in your AppHost. The wiring happens once; the policy install happens at runtime (typically from a test harness).

```csharp
// AppHost (Program.cs)
using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

var cosmos = builder.AddAzureCosmosDB("cosmos").RunAsEmulator();
var db = cosmos.AddCosmosDatabase("workspaces");

var be = builder.AddProject<Projects.MyBackend>("be")
    .WithReference(db);

// Per-resource chaos proxy in front of the Cosmos emulator (mesh can't auto-
// instrument connection-string-only resources).
var cosmosProxy = builder.AddChaosProxy("chaos-be-cosmos")
    .WithTarget(cosmos);
// (rewire BE's CosmosClient connection string at "cosmosProxy" — see core README
//  "Escape hatch: per-resource proxy" for the rewire pattern)

builder.Build().Run();
```

Then install the throttle policy at runtime (from your test):

```bash
# 429 + retry-after for the first matching request; SDK retries and succeeds.
curl -X POST http://chaos-be-cosmos:NNNN/chaos/policies \
  -H "Content-Type: application/json" \
  -d '{
    "id": "cosmos-throttle",
    "matcher": { "method": "POST", "pathContains": "/docs" },
    "cosmosThrottle": { "retryAfterMs": 250 }
  }'
```

The `failFirst` default of 1 (per D13) means the first matching request fires 429-with-retry-after, and the SDK's retry (which carries the same `x-ms-client-request-id` and so keys to the same logical request) succeeds. This is the canonical retry-path validation shape.

If you'd rather pre-install the throttle policy at AppHost build time (always-on baseline, e.g., for local-dev inner loop), the fluent design-time style is available too:

```csharp
// AppHost (Program.cs) — design-time baseline policy.
cosmosProxy
    .When(method: "POST", pathContains: "/docs")
    .WithCosmosThrottle(retryAfterMs: 250);
```

## DurableTask Framework integration

DTFx (Microsoft.Azure.WebJobs.Extensions.DurableTask) uses Azure Queue Storage as its persistence backend in the default Azure Storage provider. Azure Storage's `"queue"` endpoint isn't HTTP-named, so the **mesh** can't auto-instrument it — wire a per-resource proxy explicitly. The customer-friendly typed helpers live in a dedicated companion package: **[`Aspire.Hosting.Chaos.DurableTask`](../Aspire.Hosting.Chaos.DurableTask/)**.

AppHost wiring (once per AppHost — no service-side code changes):

```csharp
// AppHost (Program.cs) — dotnet add package Aspire.Hosting.Chaos.DurableTask
using Aspire.Hosting.Chaos.DurableTask;

var storage = builder.AddAzureStorage("storage").RunAsEmulator();

var dtfxProxy = builder.AddChaosProxy("chaos-dtfx-queue")
    .WithTarget(storage, endpointName: "queue")
    .WaitFor(storage);

// Override the worker's queue connection string so its DTFx Azure Storage
// backend routes through the chaos proxy.
worker.WithEnvironment(ctx =>
{
    var url = dtfxProxy.GetEndpoint("http").Url.TrimEnd('/');
    ctx.EnvironmentVariables["ConnectionStrings__queues"] =
        $"DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=...;QueueEndpoint={url}/devstoreaccount1;";
});
```

Then install the replay-race policy at runtime:

```bash
curl -X POST http://chaos-dtfx-queue:NNNN/chaos/policies \
  -H "Content-Type: application/json" \
  -d '{
    "id": "dtfx-replay-mystuff",
    "matcher": { "dtfxActivityName": "MyOrchestrator_DoStuff_Activity" },
    "dropResponse": { "failFirst": 1, "maxFires": 1 }
  }'
```

The proxy auto-correlates `TaskScheduledEvent` → `TaskCompletedEvent` via the DTFx envelope's `(InstanceId, TaskScheduledId)` pair, so the matcher works across multiple in-flight orchestrations. See the [`Aspire.Hosting.Chaos.DurableTask`](../Aspire.Hosting.Chaos.DurableTask/) README for the full pattern (including the `DtfxChaosPolicies.ActivityReplayRace(...)` typed factory and the local-emulator timing caveat).

The `dtfxActivityName` field is technically defined on the core package's `ChaosMatcher` (it's a string property with zero cost regardless of who uses it), but the typed helpers and recipes live in the DurableTask companion to keep this Azure companion focused on Azure-SDK-shaped wire transforms.

## Transform list

| Method | What it returns | SDK behavior it tests |
|---|---|---|
| `WithCosmosThrottle(retryAfterMs)` | 429 + `x-ms-retry-after-ms: {n}` + `x-ms-substatus: 3200` | CosmosClient's RU-throttle retry policy (waits the requested ms, retries) |
| `WithCosmosConcurrencyConflict()` | 449 + Cosmos Conflict body | CosmosClient's optimistic-concurrency retry path (direct mode retries with backoff; gateway surfaces to caller) |
| `WithCosmosPreconditionFailed()` | 412 + `x-ms-substatus: 0` + Cosmos PreconditionFailed body | Application-level optimistic-concurrency handlers on an ETag-conditional write (`UpsertItemAsync` + `IfMatchEtag`); NOT in the SDK's retriable set — surfaced as `CosmosException(PreconditionFailed)`. Use to verify the app maps a lost ETag race to the right customer response (e.g. ARM 409) instead of leaking a 500. |
| `WithCosmosServiceUnavailable()` | 503 + `x-ms-substatus: 0` + Cosmos SU body | CosmosClient's failover-region retry on multi-region accounts |
| `WithStorageServerBusy()` | 503 + `x-ms-error-code: ServerBusy` + Azure Storage XML envelope | Azure Storage SDK's transient-error retry policy |
| `WithStorageEtagMismatch()` | 412 + `x-ms-error-code: ConditionNotMet` + Azure Storage XML envelope | Application-level ETag/optimistic-concurrency handlers (NOT classified as retriable by the SDK) |
| `WithKeyVaultThrottle(retryAfterSeconds)` | 429 + `Retry-After: {n}` + KV JSON error body | Key Vault SDK throttle retry policy (waits the requested seconds, retries) |

Not yet shipped (per design doc):
- `WithServiceBusDuplicateDelivery()` — deferred until AMQP support lands per D3 (current proxy is HTTP+gRPC unary only). The HTTP equivalent for ServiceBus REST API calls would be `WithReplayDuplicate()` on the core package.

All accept the same `probability` / `failFirst` parameters as the core `WithError` extension (mutually exclusive per D13). Default behavior: `failFirst: 1` — fire once, let the SDK retry succeed.
