# Aspire.Mcp.Client library

Registers an [McpClient](https://modelcontextprotocol.io/specification/2025-06-18/basic/architecture) in the DI container for connecting to a Model Context Protocol (MCP) server through Aspire service discovery. Enables a corresponding health check and logging.

## Getting started

### Prerequisites

- An MCP server exposed in your distributed application, with its HTTP route mapped at `/mcp`.
- A consuming service that calls `AddServiceDefaults()` so logical service names are resolved by service discovery.

### Install the package

Install the Aspire MCP Client library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Mcp.Client
```

## Usage example

In the _Program.cs_ file of your project, call the `AddMcpClient` extension method to register an `McpClient` for use via the dependency injection container. The method takes the MCP server connection name.

```csharp
builder.AddServiceDefaults();
builder.AddMcpClient("mcp");
```

You can then retrieve the `McpClient` instance using dependency injection. For example, to retrieve the client from a Web API controller:

```csharp
public sealed class ToolsController(McpClient mcpClient)
{
    private readonly McpClient _mcpClient = mcpClient;
}
```

`AddMcpClient("mcp")` resolves the server endpoint as `https://mcp/mcp` by default. If service discovery only provides HTTP endpoints, it resolves `http://mcp/mcp` instead.

## Configuration

The Aspire MCP Client library provides multiple options to configure the endpoint and behavior based on your project requirements and configuration conventions.

### Use a connection string

When using a connection string from the `ConnectionStrings` configuration section, provide the connection name when calling `builder.AddMcpClient()`:

```csharp
builder.AddMcpClient("mcp");
```

And then configure the endpoint URI in the `ConnectionStrings` configuration section:

```json
{
  "ConnectionStrings": {
    "mcp": "https://my-mcp-server.example.com/mcp"
  }
}
```

The connection string must be an absolute HTTP or HTTPS URI; malformed values throw
`FormatException` unless `configureSettings` supplies an endpoint.

### Use configuration providers

The Aspire MCP Client library supports [Microsoft.Extensions.Configuration](https://learn.microsoft.com/dotnet/api/microsoft.extensions.configuration). It loads [McpClientSettings](https://github.com/microsoft/aspire/blob/main/src/Components/Aspire.Mcp.Client/McpClientSettings.cs) from configuration using the `Aspire:Mcp:Client` key:

```json
{
  "Aspire": {
    "Mcp": {
      "Client": {
        "Endpoint": "https://my-mcp-server.example.com/mcp",
        "DisableHealthChecks": false
      }
    }
  }
}
```

You can also use named configuration (`Aspire:Mcp:Client:{connectionName}`) to override base settings for specific registrations.
Configured endpoint values must be absolute HTTP or HTTPS URIs with a non-empty host.

### Use the connection name

When using `WithReference` in AppHost, provide the same connection name in your service:

```csharp
builder.AddMcpClient("mcp");
```

The client resolves `https://{connectionName}/mcp` by default. If service discovery only provides HTTP endpoints, it resolves `http://{connectionName}/mcp` instead.

### Use inline delegates

Use the overload to configure [McpClientOptions](https://github.com/modelcontextprotocol/csharp-sdk/blob/main/src/ModelContextProtocol.Core/Client/McpClientOptions.cs) and [HttpClientTransportOptions](https://github.com/modelcontextprotocol/csharp-sdk/blob/main/src/ModelContextProtocol.Core/Client/HttpClientTransportOptions.cs) inline.

Use `configureClientOptions` to configure MCP client behavior, such as client metadata and initialization timeout:

```csharp
builder.AddMcpClient(
    "mcp",
    configureClientOptions: options =>
    {
        options.ClientInfo = new() { Name = "MyService", Version = "1.0.0" };
        options.InitializationTimeout = TimeSpan.FromSeconds(60);
    });
```

Use `configureTransportOptions` to configure HTTP transport behavior, such as transport mode, timeout, headers, and OAuth:

```csharp
builder.AddMcpClient(
    "mcp",
    configureTransportOptions: options =>
    {
        options.TransportMode = HttpTransportMode.StreamableHttp;
        options.ConnectionTimeout = TimeSpan.FromSeconds(15);
        options.AdditionalHeaders = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["x-api-key"] = "api-key-value"
        };
        options.OAuth = oauthProvider;
    });
```

Use `configureSettings` to configure Aspire integration behavior, such as disabling health checks:

```csharp
builder.AddMcpClient(
    "mcp",
    configureSettings: settings => settings.DisableHealthChecks = true);
```

### Use keyed registrations

When your application consumes multiple MCP servers, register keyed clients:

```csharp
builder.AddServiceDefaults();
builder.AddKeyedMcpClient("weather");
```

And resolve the keyed `McpClient`:

```csharp
var weatherClient = serviceProvider.GetRequiredKeyedService<McpClient>("weather");
```

## AppHost extensions

There is no dedicated `Aspire.Hosting.Mcp` package. In the _AppHost.cs_ file of `AppHost`, register your MCP server project and reference it from the consuming service:

```csharp
var mcp = builder.AddProject<Projects.Mcp>("mcp");

var api = builder.AddProject<Projects.Api>("api")
    .WithReference(mcp)
    .WaitFor(mcp);
```

The `WithReference` method configures a connection in the `api` project named `mcp`. In the _Program.cs_ file of `api`, consume that connection with:

```csharp
builder.AddServiceDefaults();
builder.AddMcpClient("mcp");
```

## Additional documentation

* https://modelcontextprotocol.io/
* https://github.com/modelcontextprotocol/csharp-sdk
* https://github.com/microsoft/aspire/tree/main/src/Components/README.md

## Feedback & contributing

https://github.com/microsoft/aspire
