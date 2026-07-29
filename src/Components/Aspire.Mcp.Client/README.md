# Aspire.Mcp.Client library

Registers [McpClient](https://modelcontextprotocol.io/specification/2025-06-18/basic/architecture) with Aspire service discovery plus MCP-specific behavior that is awkward to wire manually:

- per-session endpoint affinity with reconnect rotation;
- lazy initialization (no network I/O during DI resolution);
- optional MCP OAuth or per-request bearer token injection;
- keyed registrations with isolated HTTP pipelines;
- health checks and OpenTelemetry source/meter registration.

## Getting started

### Prerequisites

- An MCP server exposed from your distributed app (typically at `/mcp`).
- A consuming service that calls `AddServiceDefaults()`.

### Install the package

```dotnetcli
dotnet add package Aspire.Mcp.Client
```

## AppHost and service wiring

No dedicated `Aspire.Hosting.Mcp` package is required. Use normal `WithReference` wiring in AppHost and consume the same connection name.

```csharp
// AppHost
var mcp = builder.AddProject<Projects.Mcp>("mcp");
builder.AddProject<Projects.Api>("api")
    .WithReference(mcp)
    .WaitFor(mcp);
```

```csharp
// Api Program.cs
builder.AddServiceDefaults();
builder.AddMcpClient("mcp");
```

By default the client resolves `https://{connectionName}/mcp`, and falls back to HTTP when only HTTP endpoints are available.

## Builder API

`AddMcpClient` and `AddKeyedMcpClient` return `AspireMcpClientBuilder`, which lets you compose settings, transport, client, and authentication behavior.

```csharp
builder.AddMcpClient("mcp")
    .ConfigureClientOptions(options =>
    {
        options.ClientInfo = new() { Name = "MyService", Version = "1.0.0" };
        options.InitializationTimeout = TimeSpan.FromSeconds(60);
    })
    .ConfigureTransportOptions(options =>
    {
        options.TransportMode = HttpTransportMode.StreamableHttp;
        options.ConnectionTimeout = TimeSpan.FromSeconds(15);
    });
```

### Keyed registrations

```csharp
builder.AddKeyedMcpClient("weather");
var weatherClient = serviceProvider.GetRequiredKeyedService<McpClient>("weather");
```

## Authentication

### MCP OAuth

OAuth is explicit and requires an authorization redirect delegate.

```csharp
builder.AddMcpClient("mcp")
    .UseOAuth(options =>
    {
        options.AuthorizationRedirectDelegate = async (authorizationUri, _, cancellationToken) =>
        {
            // Perform your app-specific redirect/authorization flow.
            await HandleAuthorizationRedirectAsync(authorizationUri, cancellationToken);
        };
    });
```

### Bearer token provider

Use a per-request token callback when your service already manages token acquisition/refresh.

```csharp
builder.AddMcpClient("mcp")
    .UseBearerTokenProvider(async (services, request, cancellationToken) =>
    {
        var provider = services.GetRequiredService<IMyTokenProvider>();
        return await provider.GetAccessTokenAsync(cancellationToken);
    });
```

`UseOAuth` and `UseBearerTokenProvider` are mutually exclusive for a registration.

## Configuration

Settings bind from:

1. `Aspire:Mcp:Client`
2. `Aspire:Mcp:Client:{connectionName}`
3. `ConnectionStrings:{connectionName}`
4. `configureSettings` delegate

```json
{
  "Aspire": {
    "Mcp": {
      "Client": {
        "Endpoint": "https://my-mcp-server.example.com/mcp",
        "DisableHealthChecks": false,
        "DisableTracing": false,
        "DisableMetrics": false
      }
    }
  }
}
```

Connection strings and configured endpoints must be absolute HTTP/HTTPS URIs with a host.

## Additional documentation

- https://modelcontextprotocol.io/
- https://github.com/modelcontextprotocol/csharp-sdk
- https://github.com/microsoft/aspire/tree/main/src/Components/README.md

## Feedback & contributing

https://github.com/microsoft/aspire
