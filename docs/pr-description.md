## Summary

Adds `Aspire.Hosting.Blazor` — a hosting integration that brings Blazor WebAssembly apps into Aspire orchestration with service discovery, YARP-based API proxying, and OpenTelemetry forwarding.

For the detailed design and architecture, see [docs/aspire-hosting-blazor.md](https://github.com/javiercn/aspire/blob/javiercn/aspire-blazor-host/docs/aspire-hosting-blazor.md).

## What's in this PR

### Hosting library — `src/Aspire.Hosting.Blazor/`

A new hosting integration that extends Aspire to Blazor WebAssembly apps:

**Standalone model** (WASM app with no server):
- `AddBlazorWasmProject()` — registers a WASM project as an Aspire resource
- `AddBlazorGateway()` — creates an auto-generated ASP.NET Core + YARP gateway process
- `WithClient()` — attaches a WASM app to the gateway under a path prefix
- The gateway serves WASM static files, proxies API traffic, and forwards OTLP telemetry
- The gateway process is auto-generated as a [`dotnet run --file`](https://learn.microsoft.com/dotnet/core/tools/dotnet-run#file-based-apps) script

**Hosted model** (Blazor Web App with WASM client components):
- `ProxyService()` — adds a YARP route so the WASM client can call a backend service through the host
- `ProxyTelemetry()` — adds a YARP route for OTLP traffic from the browser to the Aspire dashboard
- Client configuration delivered via DOM comment (`<!--Blazor-Client-Config:BASE64-->`) embedded during prerendering

**Shared infrastructure:**
- `GatewayConfigurationBuilder` — emits YARP route/cluster config and client config as environment variables
- `EndpointsManifestTransformer` — rewrites `staticwebassets.endpoints.json` to add path prefixes and SPA fallback routes
- Configurable `apiPrefix`/`otlpPrefix` route segments (defaults: `_api`, `_otlp`)
- Publish mode support for Azure Container Apps

### Tests — `tests/Aspire.Hosting.Blazor.Tests/`

38 unit tests covering:
- `AddBlazorWasmAppTests` — resource registration, project path resolution
- `GatewayConfigurationBuilderTests` — YARP route/cluster generation, client config JSON, custom prefixes, OTLP proxy config
- `EndpointsManifestTransformerTests` — manifest rewriting, prefix injection, SPA fallback
- `WithBlazorAppTests` — gateway annotation management, service reference forwarding
- `BlazorHostedExtensionsTests` — `ProxyService`/`ProxyTelemetry` env var emission, annotation tracking

### Playgrounds

**`playground/AspireWithBlazorStandalone/`** — standalone WASM + gateway + weather API
- Demonstrates `AddBlazorWasmProject` + `AddBlazorGateway` + `WithClient`
- Includes `ClientServiceDefaults` with custom OTLP/HTTP protobuf exporters for WASM
- JS initializer fetches config from `/_blazor/_configuration` endpoint

**`playground/AspireWithBlazorHosted/`** — Blazor Web App with interactive WASM client components
- Demonstrates `ProxyService` + `ProxyTelemetry`
- `BlazorClientConfiguration.razor` component embeds config in DOM comment
- JS initializer reads config from DOM comment in `beforeWebAssemblyStart`

Both playgrounds include READMEs with Mermaid architecture diagrams.

## Client-side telemetry (`ClientServiceDefaults`)

Each playground includes a `ClientServiceDefaults` project with:

- **Custom OTLP/HTTP exporters** — the standard OpenTelemetry gRPC exporter doesn't work in the browser (no HTTP/2). These exporters serialize traces, logs, and metrics as protobuf and send via `HttpClient` (browser fetch) over HTTP/1.1
- **`TaskBasedBatchExportProcessor`** — replaces the default `Thread`-based batch processor that isn't compatible with WebAssembly's single-threaded runtime
- **`AddBlazorClientServiceDefaults()`** — configures OpenTelemetry, service discovery, and resilience for the WASM client
- **JS initializer** — `onRuntimeConfigLoaded` (standalone) / `beforeWebAssemblyStart` (hosted) injects environment variables into the WASM runtime

## Approach: same-origin reverse proxy

All browser traffic routes through a YARP reverse proxy on the same origin as the WASM app:
- API calls: `/_api/{service}/*` → proxied via YARP to the backend service
- OTLP telemetry: `/_otlp/*` → proxied to the Aspire dashboard's OTLP collector

This eliminates CORS entirely — the browser only talks to its own origin. Backend services and the OTLP collector remain on the internal network, and auth tokens never reach the browser.

## What won't be needed in .NET 11

Several workarounds in this PR exist because .NET 10 lacks certain capabilities. Work already in `dotnet/aspnetcore` and `dotnet/sdk` for .NET 11 will eliminate them:

### Environment variables → IConfiguration ([dotnet/aspnetcore#64578](https://github.com/dotnet/aspnetcore/pull/64578))
Currently, the WASM client must manually call `builder.Configuration.AddEnvironmentVariables()` to bridge env vars into `IConfiguration` for service discovery. In .NET 11, `WebAssemblyHostBuilder.CreateDefault()` does this automatically. The playground `Program.cs` files will no longer need this call.

### Framework-provided Blazor Gateway ([dotnet/aspnetcore `blazor-gateway` branch](https://github.com/dotnet/aspnetcore/tree/javiercn/blazor-gateway))
The auto-generated `Gateway.cs` script in this PR (`Scripts/Gateway.cs`) will be replaced by `Microsoft.AspNetCore.Components.Gateway` — a framework-provided gateway process that reads `ClientApps` config, supports YARP with service discovery, and replaces the old `WasmDevServer`. The `Aspire.Hosting.Blazor` integration will launch the framework gateway instead of generating its own.

### SPA fallback via MSBuild ([dotnet/sdk `fallback-endpoints` branch](https://github.com/dotnet/sdk/tree/javiercn/fallback-endpoints))
The `EndpointsManifestTransformer` in this PR writes C# code to create SPA fallback routes (`{**fallback:nonfile}` catch-all for `index.html`). In .NET 11, setting `StaticWebAssetSpaFallbackEnabled=true` in the project file handles this declaratively — the endpoints manifest includes the fallback automatically, and the transformer code can be removed.

### Blazor component metrics and tracing ([dotnet/aspnetcore#61609](https://github.com/dotnet/aspnetcore/pull/61609), [#64737](https://github.com/dotnet/aspnetcore/pull/64737))
The `ClientServiceDefaults` telemetry currently only captures `HttpClient` instrumentation from the WASM client. .NET 11 adds `ComponentsMetrics` and `ComponentsActivitySource` with instruments for component rendering, navigation, event handling, and render diffs — all emittable from WASM when the `System.Diagnostics.Metrics.Meter.IsSupported` feature switch is enabled. These will show up in the Aspire dashboard automatically.

### JS initializer URL fix ([dotnet/aspnetcore#63185](https://github.com/dotnet/aspnetcore/pull/63185))
The gateway serves WASM apps under path prefixes (e.g., `/store/`). An aspnetcore fix ensures JS initializer module URLs are computed correctly with `new URL(path, document.baseURI)` instead of string concatenation, preventing load failures through the gateway.

### Static web asset framework source type ([dotnet/sdk#53135](https://github.com/dotnet/sdk/pull/53135))
When multiple WASM apps share the same Blazor framework assets, the current build can produce duplicate-asset errors. The .NET 11 SDK adds a `Framework` source type that materializes assets locally per consuming project, resolving identity conflicts.

### Endpoint filtering ([dotnet/sdk#50292](https://github.com/dotnet/sdk/pull/50292))
`StaticWebAssetEndpointExclusionPattern` will allow trimming the endpoint manifest at build/publish time, reducing route-matching overhead when the gateway hosts multiple WASM apps.
