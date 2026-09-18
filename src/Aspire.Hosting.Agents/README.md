# AI agents hosting integration

Use this integration to model, configure, and orchestrate endpoint-backed agent resources in an Aspire solution.

## Getting started

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Agents` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Agents
```

## Usage example

Then, in the AppHost, mark an application as an A2A agent and explicitly pass its agent-card URL to a consumer with either C# or TypeScript:

**C#**

```csharp
var weatherAgent = builder.AddProject<Projects.WeatherAgent>("weather-agent")
    .WithHttpEndpoint()
    .AsAgent(AgentProtocol.A2A);

builder.AddProject<Projects.Frontend>("frontend")
    .WithEnvironment("WEATHER_AGENT_AGENTCARD_URL",
        ReferenceExpression.Create($"{weatherAgent.GetEndpoint("http")}/.well-known/agent-card.json"));
```

**TypeScript**

```typescript
import { AgentProtocol, refExpr } from "./.aspire/modules/aspire.mjs";

const weatherAgent = await builder.addNodeApp("weather-agent", "../weather-agent", "server.js")
    .withHttpEndpoint()
    .asAgent(AgentProtocol.A2A);
const weatherEndpoint = await weatherAgent.getEndpoint("http");

await builder.addNodeApp("frontend", "../frontend", "server.js")
    .withEnvironment("WEATHER_AGENT_AGENTCARD_URL",
        refExpr`${weatherEndpoint}/.well-known/agent-card.json`);
```

The A2A resource receives `A2A_AGENT_BASE_URL`, which it can use as the URL advertised in its agent card. The consumer chooses its environment variable name, endpoint, and agent-card path explicitly. Endpoint expressions retain Aspire's network-aware resolution in run mode and remain expressions when publishing.

`AsAgent` does not change `WithReference` behavior. Use `WithReference` separately when the source resource supports standard service discovery or connection strings.

## Scope and authentication

The invocation commands are developer-productivity tools for sending a message or calling a tool and inspecting the response during local development. They run from the AppHost process, not from the browser, and require endpoints reachable by that process. Commands and their diagnostic URLs are not added in publish mode.

This integration is not a general-purpose agent client or an authentication framework. It does not implement interactive sign-in, acquire or refresh access tokens, or forward the dashboard user's identity. For endpoints requiring those capabilities or application-specific authentication, use a driver application that manages the interaction and authentication; Aspire can orchestrate and observe that application. Do not disable endpoint authentication to use these commands.

Authentication used by an agent to call a model provider, such as Foundry, remains the agent application's responsibility and is separate from authentication on the agent endpoint itself.

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

MCP discovery and proxying remain configured by the existing `WithMcpServer` API in `Aspire.Hosting`. That API alone does not add interactive tool-invocation commands.

To opt into tool invocation from this integration, call `WithMcpToolCommands` after `WithMcpServer`. The commands use the endpoint resolver configured by `WithMcpServer`, including its custom path and endpoint selection.

```csharp
var agent = builder.AddProject<Projects.WeatherAgent>("weather-agent")
    .WithHttpEndpoint()
    .AsAgent(AgentProtocol.Responses, agentName: "weather-agent")
    .WithMcpServer()
    .WithMcpToolCommands();
```

Use `.withMcpServer().withMcpToolCommands()` for the equivalent TypeScript configuration. `WithMcpToolCommands` is experimental (`ASPIREAGENTS001`).

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://a2a-protocol.org/
* https://platform.openai.com/docs/api-reference/responses
* https://docs.ag-ui.com/
* https://agentcommunicationprotocol.dev/
* https://modelcontextprotocol.io/

## Feedback & contributing

https://github.com/microsoft/aspire
