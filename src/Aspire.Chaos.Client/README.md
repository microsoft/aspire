# Aspire.Chaos.Client

Aspire client integration for [`Aspire.Hosting.Chaos`](../Aspire.Hosting.Chaos/) — registers a typed `ChaosProxyClient` in your DI container so your service or test code can install / inspect / clear chaos policies at runtime using the standard Aspire connection-string pattern.

> **Has NO `Aspire.Hosting` (AppHost) dependencies.** Safe to reference from test runners, scripts, CLIs, the MCP container — anywhere that needs to install / inspect / clear chaos policies at runtime without pulling in the orchestration stack.

> **Status: in-house incubation.** Marked `[Experimental("ASPIRECHAOS001")]` (inherited from the core). API may change without notice until the M4 public-release gate.

> **New to the chaos packages?** See the [package family overview](../Aspire.Hosting.Chaos/README.md#package-family) in the core README for how this fits with `Aspire.Hosting.Chaos`, `Aspire.Hosting.Chaos.Azure`, `Aspire.Hosting.Chaos.DurableTask`, and `Aspire.Hosting.Chaos.Mcp`.

## Why this package exists

The chaos proxy ships with a typed runtime API (`ChaosProxyClient`) that test harnesses, CLIs, and service code use to install / inspect / clear chaos policies. Shipping that surface in a dedicated client-integration package — separate from the AppHost-side `Aspire.Hosting.Chaos` — means a consumer-side test project can register the client via DI without pulling the entire AppHost SDK as a transitive dependency.

The package shape follows Aspire's canonical client-integration pattern (e.g., `Aspire.Azure.Data.Tables`, `Aspire.StackExchange.Redis`): types live in `namespace Aspire.Chaos.Client`, and the DI extensions are in `namespace Microsoft.Extensions.Hosting` so they're auto-imported wherever Aspire is already used.

## Quick start (DI integration)

The Aspire-idiomatic path. Wire up in your service's `Program.cs`:

```csharp
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

// "chaos-be" is the connection name — matches the chaos proxy resource name
// in your AppHost (Aspire populates ConnectionStrings:chaos-be at start).
builder.AddChaosProxyClient("chaos-be");

var host = builder.Build();
var chaos = host.Services.GetRequiredService<ChaosProxyClient>();
```

In your AppHost, expose the proxy to your service via the standard `WithReference`:

```csharp
// AppHost
var chaosBe = builder.AddChaosProxy("chaos-be").WithTarget(be);
builder.AddProject<Projects.MyTests>("tests").WithReference(chaosBe);
```

Aspire wires the connection string automatically — no `appsettings.json` work.

### Keyed registration (multiple proxies)

When you have multiple chaos proxies and need to resolve them by key:

```csharp
builder.AddKeyedChaosProxyClient("chaos-be");
builder.AddKeyedChaosProxyClient("chaos-dtfx-queue");

// ...
var be = host.Services.GetRequiredKeyedService<ChaosProxyClient>("chaos-be");
var dtfx = host.Services.GetRequiredKeyedService<ChaosProxyClient>("chaos-dtfx-queue");
```

### Configuration

Settings are bound from `Aspire:Chaos:Client` (and `Aspire:Chaos:Client:{connectionName}` for per-connection overrides):

```json
{
  "Aspire": {
    "Chaos": {
      "Client": {
        "DisableHealthChecks": false,
        "chaos-be": {
          "Endpoint": "http://override:9000"
        }
      }
    }
  }
}
```

Or via the in-code callback:

```csharp
builder.AddChaosProxyClient("chaos-be", configureSettings: s =>
{
    s.DisableHealthChecks = true;
});
```

## Direct (non-DI) usage

For ad-hoc test runners and CLIs without an `IHostApplicationBuilder`, construct the client directly:

```csharp
using Aspire.Chaos.Client;

var http = new HttpClient { BaseAddress = new Uri("http://chaos-dtfx-queue:NNNN") };
var client = new ChaosProxyClient(http);

var policy = new ChaosPolicy
{
    Id = "my-test-replay-race",
    Matcher = new ChaosMatcher { DtfxActivityName = "MyOrchestrator_DoStuff_Activity" },
    DropResponse = new ChaosDropResponse { FailFirst = 1, MaxFires = 1 },
    TtlSeconds = 600,
};

var policyId = await client.InstallPolicyAsync(policy);
// ... run the test that triggers the chaos ...
var fireCounts = await client.GetFireCountsAsync(policyId);
await client.RemovePolicyAsync(policyId);
```

For test runners that need to install pre-shaped JSON bodies (e.g., synth tests that round-trip arbitrary chaos shapes through JSON), use the `object`-accepting overload:

```csharp
var bodyDict = new Dictionary<string, object?>
{
    ["id"] = "raw-shape-test",
    ["matcher"] = new { method = "POST", pathPrefix = "/api/things" },
    ["error"] = new { httpStatus = 503, failFirst = 1 },
};

var policyId = await client.InstallPolicyAsync(bodyDict);
```

## API surface

The full `ChaosProxyClient` surface mirrors the proxy's runtime `/chaos/*` API one-method-per-endpoint. See the source for the complete list — install/preview/list/get/match/match/remove/clear/state/healthz/pause/resume/fire-once/extend-ttl/reset-fire-counts/fire-counts.

## See also

- [`Aspire.Hosting.Chaos`](../Aspire.Hosting.Chaos/) — AppHost extension package (transitively includes this client)
- [`Aspire.Hosting.Chaos.Azure`](../Aspire.Hosting.Chaos.Azure/) — Azure-SDK-shaped transforms
- [`Aspire.Hosting.Chaos.DurableTask`](../Aspire.Hosting.Chaos.DurableTask/) — DTFx-aware typed helpers
- [`Aspire.Hosting.Chaos.Mcp`](../Aspire.Hosting.Chaos.Mcp/) — MCP server companion for agent-driven chaos
