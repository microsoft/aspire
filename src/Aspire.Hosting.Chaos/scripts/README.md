# Chaos proxy scripts

## `Smoke-ChaosProxy.ps1`

End-to-end smoke test for the chaos proxy. Installs a policy per transform at runtime via `POST /chaos/policies`, probes it, verifies the wire shape fired (status / latency / body markers / fire counts), tears down.

Mirrors the M3 run-to-green model: nothing is pre-installed on the proxy; each test owns its own install + teardown. The same pattern a real harness test should follow via `ChaosProxyClient`.

### Usage

```powershell
$env:CHAOS_PROXY_ENABLED = 'true'
$env:ASPIRE_CONTAINER_RUNTIME = 'podman'

# 1) Start the AppHost in another terminal:
#    dotnet run --project src/Chaos.Studio.V2.AppHost
# 2) Find a mesh proxy URL from podman ps (AppHost auto-creates several):
#    podman ps --format "{{.Names}}|{{.Ports}}" | Select-String mesh
#    e.g. mesh-mims-to-mirp-mock-xxxx|127.0.0.1:39257->8080/tcp
# 3) Run the smoke test against that URL:
./Smoke-ChaosProxy.ps1 -ProxyBaseUrl 'http://localhost:39257'
```

Exits 0 on success, 1 on any assertion failure.

### What it covers

For each transform: installs a policy with a unique per-run path prefix, probes it, asserts the wire shape, captures fire counts, tears down.

| Transform | Probe | Assertion |
|-----------|-------|-----------|
| `error` | `GET /{runId}/error/x` | status == 503 |
| `latency` | `GET /{runId}/latency/x` | duration >= 500ms + passed through |
| `dropResponse` | `GET /{runId}/drop/x` (2s timeout) | client times out |
| `rateLimit` | 3x `GET /{runId}/rate/x` | pass, pass, 429 |
| `headerTamper` | `GET /{runId}/headers/x` with `Authorization` | round-trip ok |
| `partialResponse` | `GET /{runId}/partial/x` | Content-Length mismatch detected |
| `idempotencyCollision` | 2x `POST /{runId}/idem/x` same key | upstream, then 409 |
| `slowResponse` | `GET /{runId}/slow-stream/x` | duration >= 300ms + status 200 |
| `replayDuplicate` | `GET /{runId}/replay/x` | original passes through; fire counter shows replay |

Per-run prefix means concurrent invocations don't collide on matchers and don't see stale state on the proxy.

### Equivalent in C#

The script is the PowerShell flavor of what a real harness does via the typed `ChaosProxyClient`. Each `Install-Policy` block maps 1:1 to:

```csharp
var http = new HttpClient { BaseAddress = new Uri(proxyBaseUrl) };
var chaos = new ChaosProxyClient(http);

await chaos.InstallPolicyAsync(new ChaosPolicy
{
    Id = "my-error-policy",
    Matcher = new ChaosMatcher { PathPrefix = "/error/" },
    Error = new ChaosError { Status = 503, Probability = 1.0 },
});

// exercise the system under test ...

var counts = await chaos.GetFireCountsAsync("my-error-policy");
Assert.True(counts!["error"] >= 1);

await chaos.RemovePolicyAsync("my-error-policy");
```

Use this script for ad-hoc CLI validation; use `ChaosProxyClient` directly for in-process harness integration; use the M3 `chaos_policies` block on a target-config or synth-test for run-to-green workflow integration.
