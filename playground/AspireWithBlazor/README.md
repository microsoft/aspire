# AspireWithBlazor

This playground demonstrates the integration of Blazor Web applications with .NET Aspire using the BFF (Backend for Frontend) pattern.

## Project Structure

- **AspireWithBlazor.AppHost** - Aspire orchestration project that manages all services
- **AspireWithBlazor** - Blazor Web App (host) with embedded gateway functionality
- **AspireWithBlazor.Client** - Blazor WebAssembly project for client-side interactivity
- **AspireWithBlazor.ServiceDefaults** - Server-side Aspire service defaults (telemetry, health checks, configuration endpoint)
- **AspireWithBlazor.ClientServiceDefaults** - Client-side Aspire service defaults for WebAssembly (telemetry, service discovery)
- **AspireWithBlazor.WeatherApi** - Minimal API project providing weather data

## Features

### Service Discovery
The Blazor host exposes a `/_blazor/_configuration` endpoint that provides service discovery information to WebAssembly clients. This enables the client to communicate with backend services through the gateway.

### OpenTelemetry Integration
Both server and client are configured with OpenTelemetry for distributed tracing and metrics:
- Server uses standard OTLP exporter
- Client uses HTTP/Protobuf exporter through the gateway

### Gateway Pattern
The Blazor host acts as a gateway that:
1. Serves the WebAssembly application assets
2. Proxies API requests to backend services
3. Provides configuration to WebAssembly clients
4. Handles telemetry forwarding

## Running the Application

```bash
cd playground/AspireWithBlazor
dotnet run --project AspireWithBlazor.AppHost
```

This will start:
- The Aspire dashboard
- The Weather API service
- The Blazor Web application (with embedded gateway)

Navigate to the dashboard URL to see all services and their telemetry.

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                      Aspire AppHost                          │
│  ┌─────────────────┐    ┌──────────────────────────────┐   │
│  │   Weather API   │    │    Blazor Web App (Gateway)  │   │
│  │  /weatherforecast│◄───│  - Serves WASM assets        │   │
│  └─────────────────┘    │  - Proxies API calls          │   │
│                         │  - Exposes /_configuration    │   │
│                         │                                │   │
│                         │    ┌────────────────────────┐ │   │
│                         │    │  Blazor WASM Client    │ │   │
│                         │    │  (Counter component)   │ │   │
│                         │    └────────────────────────┘ │   │
│                         └──────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────┘
```
