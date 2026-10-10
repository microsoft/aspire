# Aspire.Azure.AI.Extensions.OpenAI library

Registers the [Azure SDK's `ProjectOpenAIClient`](https://github.com/Azure/azure-sdk-for-net/tree/main/sdk/ai/Azure.AI.Extensions.OpenAI) and its `OpenAIClient` alias in the DI container as singletons, with Foundry project configuration, Microsoft Entra authentication, and Microsoft.Extensions.AI telemetry.

Also registers the standard OpenAI SDK's `OpenAIClient` for Azure OpenAI account endpoints,
with Microsoft Entra or API-key authentication and the same chat/embedding helpers.

## Getting started

### Prerequisites

An [Azure subscription](https://azure.microsoft.com/free/) and a Microsoft Entra identity authorized to invoke a deployed model or agent, or an API key for account-based access. Use `AddAzureProjectOpenAIClient` for a Foundry project endpoint, or `AddAzureOpenAIClient` for an Azure OpenAI account. Project clients do not accept API keys or account/Foundry Local endpoints.

### Install the package

Install the Aspire Azure AI Extensions OpenAI library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Azure.AI.Extensions.OpenAI
```

## Usage example

In the consuming application's _Program.cs_, register the client with a connection name:

```csharp
builder.AddAzureProjectOpenAIClient("ai");
```

```json
{
  "ConnectionStrings": {
    "ai": "Endpoint=https://account.services.ai.azure.com/api/projects/project"
  }
}
```

A bare project URI is also accepted. Resolve `Azure.AI.Extensions.OpenAI.ProjectOpenAIClient` or `OpenAI.OpenAIClient` from dependency injection; both refer to the same instance.

```csharp
using Azure.AI.Extensions.OpenAI;

public class AgentService(ProjectOpenAIClient client)
{
    public ProjectOpenAIClient Client => client;
}
```

For Microsoft.Extensions.AI chat or embeddings:

```csharp
var clientBuilder = builder.AddAzureProjectOpenAIClient("ai");
clientBuilder.AddChatClient("chat");
clientBuilder.AddEmbeddingGenerator("embeddings");
```

Resolve `IChatClient` or `IEmbeddingGenerator<string, Embedding<float>>` from dependency injection. Model names refer to deployments available to the project. An optional `Deployment=chat` or `Model=chat` connection-string field supplies the default model; do not specify both.

For prompt agents, use the registered `ProjectOpenAIClient`:

```csharp
using Azure.AI.Extensions.OpenAI;

var responses = client.GetProjectResponsesClientForAgent(new AgentReference("my-agent"));
var response = await responses.CreateResponseAsync("Hello!");
```

Hosted Responses agents use `GetProjectResponsesClientForAgentEndpoint("my-agent")` instead. Responses APIs may require an `OPENAI001` experimental-API warning suppression.

When manually adapting a model Responses client with `GetProjectResponsesClientForModel("chat").AsIChatClient()`, also supply `ChatOptions.ModelId = "chat"` on requests. The Microsoft.Extensions.AI adapter does not infer the model from the project SDK's defaults. This is not required by the `AddChatClient("chat")` helper, which uses the Chat Completions API.

### Keyed clients

```csharp
builder.AddKeyedAzureProjectOpenAIClient("ai")
    .AddKeyedChatClient("chat-client", "chat");
```

Resolve the SDK client with service key `ai`, or the `IChatClient` with key `chat-client`.

## Configuration

Settings are applied in this order: `Aspire:Azure:AI:Extensions:OpenAI`, its connection-specific subsection, the connection string, and finally `configureSettings`. Root and connection-specific `ClientOptions` are bound before `configureClientBuilder`.

### Use a connection string

Provide `ConnectionStrings:ai` as shown in the usage example. Include `Deployment=chat` when model helpers should use a default deployment instead of an explicit method argument.

### Use configuration providers

```json
{
  "Aspire": {
    "Azure": {
      "AI": {
        "Extensions": {
          "OpenAI": {
            "DisableTracing": false,
            "DisableMetrics": false,
            "EnableSensitiveTelemetryData": false,
            "ai": {
              "Endpoint": "https://account.services.ai.azure.com/api/projects/project",
              "ClientOptions": {
                "NetworkTimeout": "00:01:00"
              }
            }
          }
        }
      }
    }
  }
}
```

### Use inline delegates

Without a connection string, settings can be supplied entirely in code:

```csharp
using Azure.Identity;
using Microsoft.Extensions.Azure;

builder.AddAzureProjectOpenAIClient("ai",
    configureSettings: settings =>
    {
        settings.Endpoint = new Uri("https://account.services.ai.azure.com/api/projects/project");
        settings.Credential = new AzureCliCredential();
    },
    configureClientBuilder: client => client.ConfigureOptions(options =>
    {
        options.NetworkTimeout = TimeSpan.FromSeconds(60);
    }));
```

Without an explicit credential, Aspire uses a development credential locally and managed identity in Azure. API-key connection strings are rejected.

## Azure OpenAI account clients

For an Azure OpenAI account, keep the existing chat-registration pattern:

```csharp
builder.AddAzureOpenAIClient("chat").AddChatClient();
```

```json
{
  "ConnectionStrings": {
    "chat": "Endpoint=https://account.openai.azure.com/;Deployment=chat"
  }
}
```

The account endpoint is normalized to `/openai/v1/`. An existing HTTPS v1 endpoint is also
accepted; project endpoints and legacy deployment-specific routes are rejected.
Resolve `OpenAI.OpenAIClient` or `IChatClient`, not `Azure.AI.OpenAI.AzureOpenAIClient`.

Without a key, the registration uses Microsoft Entra authentication and Aspire's default
Azure credential selection. Supply a custom credential through `configureSettings` or
`configureClientBuilder.WithCredential`. The default token scope is
`https://ai.azure.com/.default`; set `TokenScope` for other Azure cloud audiences.
A `Key` connection-string field or settings property selects API-key authentication instead.

```csharp
builder.AddAzureOpenAIClient("chat",
    configureSettings: settings =>
    {
        settings.Credential = new AzureCliCredential();
    },
    configureClientBuilder: client => client.ConfigureOptions(options =>
    {
        options.NetworkTimeout = TimeSpan.FromSeconds(60);
    }))
    .AddChatClient();

builder.AddKeyedAzureOpenAIClient("other-account")
    .AddKeyedChatClient("other-chat", "deployment");
```

Account settings use `Aspire:Azure:AI:OpenAI` and its connection-specific subsection, followed
by the connection string and `configureSettings`. Root/named `ClientOptions` and the client
callback customize `OpenAIClientOptions`. `DisableTracing`, `DisableMetrics`, and
`EnableSensitiveTelemetryData` have the same meaning as for project clients.
Use distinct service keys when registering multiple account/project clients.

In the AppHost, use `Aspire.Hosting.Azure.CognitiveServices` and keep the deployment reference:

```csharp
var chat = builder.AddAzureOpenAI("openai")
    .AddDeployment("chat", "gpt-4o", "2024-05-13");

builder.AddProject<Projects.MyApp>("app").WithReference(chat);
```

## AppHost extensions

Install `Aspire.Hosting.Foundry` in the AppHost:

```dotnetcli
dotnet add package Aspire.Hosting.Foundry
```

```csharp
using Aspire.Hosting.Foundry;

var foundry = builder.AddFoundry("foundry");
var project = foundry.AddProject("ai");
var chat = project.AddModelDeployment("chat", FoundryModel.OpenAI.Gpt5Mini);

builder.AddProject<Projects.MyApp>("app")
    .WithReference(foundry)
    .WithReference(project)
    .WaitFor(chat);
```

Register `builder.AddAzureProjectOpenAIClient("ai").AddChatClient("chat")` in `MyApp`. The project reference supplies the endpoint; the account reference configures runtime identity and Foundry access. A model-deployment reference alone supplies an account endpoint, not a project endpoint.

## Telemetry and health checks

Chat and embedding helpers register Microsoft.Extensions.AI telemetry. The activity sources and meters are `Experimental.Microsoft.Extensions.AI` and `Microsoft.Extensions.AI`; exporters come from the application's OpenTelemetry configuration.

`DisableTracing` and `DisableMetrics` independently disable the respective subscriptions. The helpers retain the telemetry wrapper when either signal is enabled, so disabling tracing does not disable metrics. `EnableSensitiveTelemetryData` controls capture of message content in the helpers; it defaults to false unless `OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT=true`. Direct SDK calls use the SDK's own diagnostic options, not the Microsoft.Extensions.AI wrappers.

No health check is registered: the integration does not make billable inference calls or require model-list permissions for a health probe.

## Migration from retired client integrations

`Aspire.Azure.AI.OpenAI` and `Aspire.Azure.AI.Inference` are no longer produced, following [microsoft/aspire#20401](https://github.com/microsoft/aspire/issues/20401). Previously published versions remain available. `Aspire.OpenAI`, `Aspire.Hosting.Foundry`, and the Azure OpenAI hosting integration remain supported.

For a Foundry project, replace the old package with this package, replace `AddAzureOpenAIClient` or `AddAzureChatCompletionsClient` with `AddAzureProjectOpenAIClient`, and provide the **project endpoint**, not the old account/inference endpoint. Replace API-key authentication with Microsoft Entra credentials and grant the runtime identity the permissions it needs.

For Azure OpenAI accounts, replace the old package with this package and keep
`AddAzureOpenAIClient` / `AddKeyedAzureOpenAIClient` and the existing chat/embedding helpers.
The registration now uses the standard OpenAI SDK and v1 routes, while preserving Entra
authentication and account connection strings. Settings/builder types now live in
`Aspire.Azure.AI.Extensions.OpenAI`; client-options callbacks use `OpenAIClientOptions`
instead of `AzureOpenAIClientOptions`. This is registration-pattern compatibility, not
binary compatibility with the retired package.

SDK consumers must replace `AzureOpenAIClient`, `ChatCompletionsClient`, or `EmbeddingsClient`
with `OpenAIClient` for account access, or `ProjectOpenAIClient` for project access.
Inference SDK request/response types are not interchangeable with the OpenAI SDK types.

`AddOpenAIClientFromConfiguration` and its keyed counterpart are not carried forward.
Select explicitly: `AddOpenAIClient` for generic API-key endpoints, `AddAzureOpenAIClient`
for Azure OpenAI accounts, or `AddAzureProjectOpenAIClient` for Foundry projects.

For Foundry Local, use `Aspire.OpenAI` with the connection supplied by `RunAsFoundryLocal`.
See [Azure's OpenAI SDK migration guide](https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/openai/Azure.AI.OpenAI/migration-guidance.md)
for v1 API differences and scenario-specific limitations.

For project and agent-management operations rather than inference, use `Aspire.Azure.AI.Projects` and `AddAzureAIProjectClient`.

## Additional documentation

* [Azure AI Extensions OpenAI SDK](https://github.com/Azure/azure-sdk-for-net/tree/main/sdk/ai/Azure.AI.Extensions.OpenAI)
* https://github.com/microsoft/aspire/tree/main/src/Components/README.md

## Feedback & contributing

https://github.com/microsoft/aspire
