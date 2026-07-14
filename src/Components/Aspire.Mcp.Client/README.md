# Aspire.Mcp.Client library

Registers an [McpClient](https://modelcontextprotocol.io/specification/2025-06-18/basic/architecture) in the DI container for connecting to a Model Context Protocol (MCP) server through Aspire service discovery.

## Getting started

### Prerequisites

- An MCP server exposed in your distributed application.
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

`AddMcpClient("mcp")` resolves the server endpoint as `https://mcp/mcp`.

## Configuration

The Aspire MCP Client library configures the server endpoint from the connection name and supports inline configuration delegates for client and transport options.

### Use the connection name

Provide the same connection name configured by AppHost references:

```csharp
builder.AddMcpClient("mcp");
```

The client resolves `https://{connectionName}/mcp`.

### Use inline delegates

Use the overload to configure `McpClientOptions` and `HttpClientTransportOptions` inline.

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
        options.AdditionalHeaders["x-api-key"] = "api-key-value";
        options.OAuth = oauthProvider;
    });
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
