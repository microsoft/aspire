# AspireWithBlazorStandalone

This sample demonstrates how to integrate a **standalone Blazor WebAssembly** application with .NET Aspire, enabling full observability (logs, metrics, traces) and service discovery without requiring a hosted Blazor Server backend.

## Overview

In a typical Blazor WebAssembly setup with Aspire, the WASM client is hosted by a Blazor Server application that acts as the Backend-for-Frontend (BFF). The server handles configuration injection and proxies telemetry to the Aspire dashboard.

For **standalone** Blazor WebAssembly applications, there is no server-side Blazor host. This sample shows how to use the **Gateway** package to bridge this gap, enabling:

- ✅ **Service Discovery** - Resolve service endpoints at runtime
- ✅ **Distributed Tracing** - Traces flow from browser → API → dashboard
- ✅ **Structured Logging** - Client-side logs appear in Aspire dashboard
- ✅ **Metrics** - Runtime and HTTP client metrics from the browser

## Architecture

```
┌─────────────────────────────────────────────────────────────────────┐
│                        Aspire AppHost                                │
│  ┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐  │
│  │   Dashboard     │    │  standalonewasm │    │   weatherapi    │  │
│  │  (OTLP + UI)    │    │    (Gateway)    │    │   (Web API)     │  │
│  └────────▲────────┘    └────────┬────────┘    └────────▲────────┘  │
│           │                      │                      │           │
└───────────┼──────────────────────┼──────────────────────┼───────────┘
            │                      │                      │
            │ OTLP (logs,          │ Static files +       │ HTTP
            │ metrics, traces)     │ /_blazor/_config     │ /weatherforecast
            │                      │                      │
┌───────────┴──────────────────────▼──────────────────────┴───────────┐
│                           Browser                                    │
│  ┌──────────────────────────────────────────────────────────────┐   │
│  │                  Blazor WebAssembly Client                    │   │
│  │  • Fetches config from /_blazor/_configuration                │   │
│  │  • Uses service discovery to resolve "weatherapi"             │   │
│  │  • Sends telemetry directly to Aspire dashboard               │   │
│  └──────────────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────────┘
```

## How It Works

### Step 1: Gateway Replaces DevServer

The standalone WASM project references the **Gateway** package instead of the standard Blazor DevServer:

```xml
<!-- AspireWithBlazorStandalone.csproj -->
<Project Sdk="Microsoft.NET.Sdk.BlazorWebAssembly">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>

  <!-- Reference the Gateway package instead of DevServer -->
  <ItemGroup>
    <ProjectReference Include="..\AspireWithBlazorStandalone.Gateway\AspireWithBlazorStandalone.Gateway.csproj" />
  </ItemGroup>
</Project>
```

The Gateway package uses MSBuild targets to:
1. Suppress the default `Microsoft.AspNetCore.Components.WebAssembly.DevServer` dependency
2. Replace it with a custom ASP.NET Core host that serves the WASM files

### Step 2: Gateway Exposes Configuration Endpoint

When running under Aspire, the Gateway receives configuration including:
- Service endpoints (e.g., `services:weatherapi:https:0`)
- OTLP endpoint URL and authentication headers

The Gateway exposes this configuration at `/_blazor/_configuration`:

```json
{
  "webAssembly": {
    "environment": {
      "services:weatherapi:https:0": "https://localhost:7101",
      "services:weatherapi:http:0": "http://localhost:5101",
      "OTEL_EXPORTER_OTLP_ENDPOINT": "https://localhost:21187",
      "OTEL_EXPORTER_OTLP_HEADERS": "x-otlp-api-key=abc123..."
    }
  }
}
```

### Step 3: JavaScript Initializer Injects Environment Variables

The **ClientServiceDefaults** package includes a [JavaScript initializer](https://learn.microsoft.com/aspnet/core/blazor/fundamentals/startup#javascript-initializers) that runs before Blazor starts:

```javascript
// Runs during beforeWebAssemblyStart
export async function beforeWebAssemblyStart(options, extensions, blazorOptions) {
  // Fetch configuration from the Gateway
  const response = await fetch('/_blazor/_configuration');
  const config = await response.json();
  
  // Inject into Blazor's environment variables
  const envVars = config?.webAssembly?.environment;
  if (envVars) {
    blazorOptions.environmentVariables = {
      ...blazorOptions.environmentVariables,
      ...envVars
    };
  }
}
```

This makes the configuration available via `Environment.GetEnvironmentVariable()` in the WASM client.

### Step 4: WASM Client Adds Environment Variables to Configuration

Environment variables are available via `Environment.GetEnvironmentVariable()`, but **not** automatically in `IConfiguration`. Since Service Discovery reads from `IConfiguration`, we must bridge this gap:

```csharp
// Program.cs (WASM client)
var builder = WebAssemblyHostBuilder.CreateDefault(args);

// Bridge environment variables into IConfiguration
// This converts "services__weatherapi__https__0" to "services:weatherapi:https:0"
builder.Configuration.AddEnvironmentVariables();

// Add Aspire service defaults (service discovery, telemetry, resilience)
builder.AddBlazorClientServiceDefaults();
```

### Step 5: Service Discovery Resolves Endpoints

With configuration properly set up, the WASM client can use service discovery:

```csharp
// Register a named HttpClient that uses service discovery
builder.Services.AddHttpClient("weatherapi", client =>
{
    // "https+http://weatherapi" is resolved via configuration
    client.BaseAddress = new Uri("https+http://weatherapi");
});
```

In Razor components:

```csharp
@inject IHttpClientFactory HttpClientFactory

private async Task LoadWeather()
{
    var client = HttpClientFactory.CreateClient("weatherapi");
    forecasts = await client.GetFromJsonAsync<WeatherForecast[]>("weatherforecast");
}
```

### Step 6: Telemetry Flows to Aspire Dashboard

The **ClientServiceDefaults** package configures OpenTelemetry to send logs, metrics, and traces directly to the Aspire dashboard. The OTLP endpoint and headers are read from configuration (injected in Step 3).

**Important:** WebAssembly doesn't automatically start `IHostedService`, so the telemetry providers must be manually initialized:

```csharp
var host = builder.Build();

// Force initialization of OpenTelemetry providers
// Required because IHostedService doesn't run in WebAssembly
_ = host.Services.GetService<MeterProvider>();
_ = host.Services.GetService<TracerProvider>();

await host.RunAsync();
```

## Project Structure

```
AspireWithBlazorStandalone/
├── AspireWithBlazorStandalone.AppHost/     # Aspire orchestrator
│   └── Program.cs                          # Registers Gateway and WeatherAPI
│
├── AspireWithBlazorStandalone/             # Standalone Blazor WASM client
│   ├── Program.cs                          # AddEnvironmentVariables() + service discovery
│   └── Pages/Weather.razor                 # Calls WeatherAPI via HttpClientFactory
│
├── AspireWithBlazorStandalone.ClientServiceDefaults/  # Client telemetry + JS initializer
│   ├── Extensions.cs                       # AddBlazorClientServiceDefaults()
│   └── wwwroot/*.lib.module.js             # Fetches /_blazor/_configuration
│
└── AspireWithBlazorStandalone.WeatherApi/  # Sample API with CORS enabled
```

## AppHost Configuration

The AppHost registers the standalone wasm as a standard project:

```csharp
// AspireWithBlazorStandalone.AppHost/Program.cs
var builder = DistributedApplication.CreateBuilder(args);

var weatherApi = builder.AddProject<Projects.AspireWithBlazorStandalone_WeatherApi>("weatherapi");

// The Gateway project hosts the WASM files and exposes configuration
var standaloneWasm = builder.AddProject<Projects.AspireWithBlazorStandalone>("standalonewasm")
    .WithReference(weatherApi);

builder.Build().Run();
```

### Required launchSettings.json Configuration

For client-side telemetry to work, the AppHost must configure the HTTP OTLP endpoint with CORS:

```json
{
  "profiles": {
    "https": {
      "environmentVariables": {
        "ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL": "https://localhost:21188",
        "ASPIRE_DASHBOARD_CORS_ALLOWED_ORIGINS": "*"
      }
    }
  }
}
```

- **`ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL`**: Creates an HTTP-based OTLP endpoint (browsers can't use gRPC)
- **`ASPIRE_DASHBOARD_CORS_ALLOWED_ORIGINS`**: Enables CORS so browsers can send telemetry cross-origin

## WeatherAPI CORS Configuration

The WeatherAPI must enable CORS to allow browser requests:

```csharp
// WeatherAPI/Program.cs
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// ...

app.UseCors();
```

## Running the Sample

1. **Start the AppHost:**
   ```bash
   cd AspireWithBlazorStandalone.AppHost
   dotnet run
   ```

2. **Open the Aspire Dashboard** at `https://localhost:17198`

3. **Navigate to the WASM app** (click the Gateway URL in the dashboard)

4. **Click "Weather"** to trigger an API call

5. **View telemetry** in the Aspire dashboard:
   - **Structured Logs**: Shows logs from `blazorapp-client`
   - **Traces**: Shows distributed traces spanning client → API
   - **Metrics**: Shows HTTP client and runtime metrics

## Key Differences from Hosted Blazor

| Aspect | Hosted Blazor | Standalone with Gateway |
|--------|---------------|------------------------|
| **Server** | Blazor Server hosts WASM | Gateway hosts WASM |
| **Config Injection** | Automatic via `blazor.boot.json` | Via `/_blazor/_configuration` endpoint |
| **Telemetry** | Can proxy through server | Sent directly from browser |
| **Service Discovery** | Works out of the box | Requires `AddEnvironmentVariables()` |
| **CORS** | Not needed (same origin) | Required for API + OTLP |

## Summary

The Gateway package enables standalone Blazor WebAssembly applications to integrate with Aspire by:

1. **Replacing DevServer** with a production-ready ASP.NET Core host
2. **Exposing configuration** at `/_blazor/_configuration`
3. **Enabling service discovery** through environment variable injection
4. **Supporting direct telemetry** from browser to Aspire dashboard

This approach maintains the benefits of standalone deployment while gaining full Aspire observability.
