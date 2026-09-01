# AI agents hosting integration

Use this integration to model and configure endpoint-backed agent resources in an Aspire solution.

## Getting started

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Agents` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Agents
```

## Usage example

Then, in the AppHost, mark an application as an A2A agent and reference it from another resource with either C# or TypeScript:

**C#**

```csharp
var weatherAgent = builder.AddProject<Projects.WeatherAgent>("weather-agent")
    .WithHttpEndpoint()
    .AsAgent(AgentProtocol.A2A);

builder.AddProject<Projects.Frontend>("frontend")
    .WithReference(weatherAgent);
```

**TypeScript**

```typescript
const weatherAgent = await builder.addNodeApp("weather-agent", "../weather-agent", "server.js")
    .withHttpEndpoint()
    .asAgent(AgentProtocol.A2A);

await builder.addNodeApp("frontend", "../frontend", "server.js")
    .withReference(weatherAgent);
```

The A2A resource receives `A2A_AGENT_BASE_URL`, which it can use as the URL advertised in its agent card. Referencing an A2A resource injects an environment variable named `<RESOURCE_NAME>_AGENTCARD_URL` into the consumer. For example, the preceding reference injects `WEATHER_AGENT_AGENTCARD_URL`.

## Agent protocols

Call `AsAgent` once for each protocol exposed by a resource. Protocol paths default to `/.well-known/agent-card.json` for A2A, `/v1/responses` for Responses, `/ag-ui` for AG-UI, and `/runs` for ACP.

### A2A

A2A dashboard invocation is non-streaming by default. Enable streaming only when the agent card advertises streaming support:

```csharp
var agent = builder.AddProject<Projects.WeatherAgent>("weather-agent")
    .WithHttpEndpoint()
    .AsAgent(AgentProtocol.A2A, A2AInvocationMode.Streaming);
```

Use the path overload for a non-default agent-card path:

```csharp
var agent = builder.AddProject<Projects.WeatherAgent>("weather-agent")
    .WithHttpEndpoint()
    .AsAgent("/agent-card.json", AgentProtocol.A2A);
```

The equivalent TypeScript APIs are `asAgentWithInvocationMode(...)`, `asAgentWithPath(...)`, and `asAgentWithPathAndInvocationMode(...)`.

### OpenAI Responses

The registered Responses agent name is independent from the Aspire resource name. Configure it explicitly so the dashboard command invokes the correct agent:

```csharp
var agent = builder.AddProject<Projects.WeatherAgent>("agent-service")
    .WithHttpEndpoint()
    .AsAgent(AgentProtocol.Responses, agentName: "weather-agent");
```

```typescript
const agent = await builder.addNodeApp("agent-service", "../weather-agent", "server.js")
    .withHttpEndpoint()
    .asAgent(AgentProtocol.Responses, { agentName: "weather-agent" });
```

When the registered name is omitted, the dashboard command prompts for it.

### AG-UI and ACP

A resource can expose multiple protocols. ACP also accepts an explicit registered agent name:

```csharp
var agent = builder.AddProject<Projects.WeatherAgent>("agent-service")
    .WithHttpEndpoint()
    .AsAgent(AgentProtocol.AgUi)
    .AsAgent(AgentProtocol.Acp, agentName: "weather-agent");
```

Use `asAgent(AgentProtocol.AgUi)` and `asAgent(AgentProtocol.Acp, { agentName: "weather-agent" })` for the equivalent TypeScript configuration.

## MCP servers

MCP is configured independently from agent protocols with `WithMcpServer`. The endpoint defaults to the first non-excluded HTTPS or HTTP endpoint, and the path defaults to `/mcp`.

```csharp
var agent = builder.AddProject<Projects.WeatherAgent>("weather-agent")
    .WithHttpEndpoint()
    .AsAgent(AgentProtocol.Responses, agentName: "weather-agent")
    .WithMcpServer();
```

Use `withMcpServer()` for the equivalent TypeScript configuration.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://a2a-protocol.org/
* https://platform.openai.com/docs/api-reference/responses
* https://docs.ag-ui.com/
* https://agentcommunicationprotocol.dev/
* https://modelcontextprotocol.io/

## Feedback & contributing

https://github.com/microsoft/aspire
