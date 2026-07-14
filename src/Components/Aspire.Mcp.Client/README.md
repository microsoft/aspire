# Aspire MCP Client library

Registers an [MCP client](https://modelcontextprotocol.io/) that connects to a remote MCP server through Aspire service discovery.

## Install the package

```dotnetcli
dotnet add package Aspire.Mcp.Client
```

## Usage

Reference the MCP server from the consuming project in the AppHost:

```csharp
var mcp = builder.AddProject<Projects.Mcp>("mcp");

builder.AddProject<Projects.Api>("api")
    .WithReference(mcp)
    .WaitFor(mcp);
```

In the consuming application, register the client after service defaults:

```csharp
builder.AddServiceDefaults();
builder.AddMcpClient("mcp");
```

The client is registered as a singleton and can be injected directly:

```csharp
public sealed class WeatherAgent(McpClient mcpClient)
{
}
```

`AddMcpClient("mcp")` connects to the `https` endpoint of the `mcp` service at `/mcp`. The application must call `AddServiceDefaults` so the configured HTTP client resolves the logical service name.

Use `AddKeyedMcpClient` when the application consumes multiple MCP servers:

```csharp
builder.AddKeyedMcpClient("weather");

var mcpClient = serviceProvider.GetRequiredKeyedService<McpClient>("weather");
```
