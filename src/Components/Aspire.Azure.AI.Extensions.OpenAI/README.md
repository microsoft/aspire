# Aspire.Azure.AI.Extensions.OpenAI library

Registers the [Azure SDK's `ProjectOpenAIClient`](https://github.com/Azure/azure-sdk-for-net/tree/main/sdk/ai/Azure.AI.Extensions.OpenAI) and its `OpenAIClient` alias in the DI container as singletons, with Foundry project configuration, Microsoft Entra authentication, and Microsoft.Extensions.AI telemetry.

## Getting started

### Prerequisites

An [Azure subscription](https://azure.microsoft.com/free/), a Microsoft Foundry project, a deployed model or agent, and a Microsoft Entra identity authorized to invoke it. The endpoint must identify the project, for example `https://account.services.ai.azure.com/api/projects/project`. API-key connections, Azure OpenAI account endpoints, and Foundry Local endpoints are not project endpoints.

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

`DisableTracing` and `DisableMetrics` disable the respective subscriptions. `EnableSensitiveTelemetryData` controls capture of message content in the helpers; it defaults to false unless `OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT=true`. Direct SDK calls use the SDK's own diagnostic options, not the Microsoft.Extensions.AI wrappers.

No health check is registered: the integration does not make billable inference calls or require model-list permissions for a health probe.

## Migration from retired client integrations

`Aspire.Azure.AI.OpenAI` and `Aspire.Azure.AI.Inference` are no longer produced, following [microsoft/aspire#20401](https://github.com/microsoft/aspire/issues/20401). Previously published versions remain available. `Aspire.OpenAI`, `Aspire.Hosting.Foundry`, and the Azure OpenAI hosting integration remain supported.

For a Foundry project, replace the old package with this package, replace `AddAzureOpenAIClient` or `AddAzureChatCompletionsClient` with `AddAzureProjectOpenAIClient`, and provide the **project endpoint**, not the old account/inference endpoint. Replace API-key authentication with Microsoft Entra credentials and grant the runtime identity the permissions it needs.

Existing `AddChatClient` and `AddEmbeddingGenerator` helpers can be used with the new builder. SDK consumers must replace `AzureOpenAIClient`, `ChatCompletionsClient`, or `EmbeddingsClient` with `ProjectOpenAIClient` or its OpenAI model clients. Inference SDK request/response types are not interchangeable with the OpenAI SDK types.

`AddOpenAIClientFromConfiguration` and its keyed counterpart are not carried forward. Select the registration explicitly: `AddOpenAIClient` for OpenAI-compatible API-key endpoints, or `AddAzureProjectOpenAIClient` for Foundry projects.

For Foundry Local, use `Aspire.OpenAI` with the connection supplied by `RunAsFoundryLocal`. For an existing Azure OpenAI account that is not a Foundry project, continue using the native `Azure.AI.OpenAI` SDK, or migrate to the OpenAI-compatible v1 API with appropriate authentication; do not pass the account endpoint to a project client. See [Azure's inference migration guide](https://learn.microsoft.com/azure/foundry/how-to/model-inference-to-openai-migration?tabs=openai).

For project and agent-management operations rather than inference, use `Aspire.Azure.AI.Projects` and `AddAzureAIProjectClient`.

## Additional documentation

* [Azure AI Extensions OpenAI SDK](https://github.com/Azure/azure-sdk-for-net/tree/main/sdk/ai/Azure.AI.Extensions.OpenAI)
* https://github.com/microsoft/aspire/tree/main/src/Components/README.md

## Feedback & contributing

https://github.com/microsoft/aspire
