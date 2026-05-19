# Aspire.Hosting.Foundry library

Provides extension methods and resource definitions for an Aspire AppHost to configure Microsoft Foundry.

## Getting started

### Prerequisites

- Azure subscription - [create one for free](https://azure.microsoft.com/free/)

### Install the package

In your AppHost project, install the Aspire Microsoft Foundry Hosting library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Foundry
```

## Configure Azure Provisioning for local development

Adding Azure resources to the Aspire application model will automatically enable development-time provisioning
for Azure resources so that you don't need to configure them manually. Provisioning requires a number of settings
to be available via .NET configuration. Set these values in user secrets in order to allow resources to be configured
automatically.

```json
{
    "Azure": {
      "SubscriptionId": "<your subscription id>",
      "ResourceGroupPrefix": "<prefix for the resource group>",
      "Location": "<azure location>"
    }
}
```

## Usage example

Then, in the _AppHost.cs_ file of `AppHost`, add a Microsoft Foundry deployment and consume the connection using the following methods:

```csharp
var chat = builder.AddFoundry("foundry")
                  .AddDeployment("chat", "Phi-4", "1", "Microsoft");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(chat);
```

The `WithReference` method passes that connection information into a connection string named `chat` in the `MyService` project.

### Configuring deployment rate limits

The `SkuCapacity` property controls the rate limit in thousands of tokens per minute (TPM). For example, a value of `10` means 10,000 TPM. The default is `1` (1,000 TPM). Use `WithProperties` to configure it:

```csharp
var chat = builder.AddFoundry("foundry")
                  .AddDeployment("chat", "gpt-4", "1", "OpenAI")
                  .WithProperties(d => d.SkuCapacity = 10);
```

See the [Azure AI quota management](https://learn.microsoft.com/azure/ai-foundry/openai/how-to/quota) documentation for available quota limits per model and region.

In the _Program.cs_ file of `MyService`, the connection can be consumed using a client library like [Aspire.Azure.AI.Inference](https://www.nuget.org/packages/Aspire.Azure.AI.Inference) or [Aspire.OpenAI](https://www.nuget.org/packages/Aspire.OpenAI) if the model is compatible with the OpenAI API:

Note: The `format` parameter of the `AddDeployment()` method can be found in the Microsoft Foundry portal in the details
page of the model, right after the `Quick facts` text.

#### Inference client usage
```csharp
builder.AddAzureChatCompletionsClient("chat")
       .AddChatClient();
```

#### OpenAI client usage
```csharp
builder.AddOpenAIClient("chat")
       .AddChatClient();
```

### Emulator usage

Aspire supports the usage of Foundry Local. Add the following to your AppHost project:

```csharp
// AppHost
var chat = builder.AddFoundry("foundry")
                  .RunAsFoundryLocal()
                  .AddDeployment("chat", "phi-3.5-mini", "1", "Microsoft");
```

When the AppHost starts up, the local Foundry service will also be started.

This requires the local machine to have [Foundry Local](https://learn.microsoft.com/azure/ai-foundry/foundry-local/get-started) installed and running.

## Connection Properties

When you reference Microsoft Foundry resources using `WithReference`, the following connection properties are made available to the consuming project:

### Microsoft Foundry resource

The Microsoft Foundry resource exposes the following connection properties:

| Property Name | Description |
|---------------|-------------|
| `Uri` | The endpoint URI for the Microsoft Foundry resource, with the format `https://<resource_name>.services.ai.azure.com/` or the emulator service URI when running Foundry Local (e.g., `http://127.0.0.1:61799/v1`) |
| `Key` | The API key when using Foundry Local |

### Microsoft Foundry deployment

The Microsoft Foundry deployment resource inherits all properties from its parent Microsoft Foundry resource and adds:

| Property Name | Description |
|---------------|-------------|
| `ModelName` | The deployment name when targeting Azure or model identifier when running Foundry Local, e.g., `Phi-4`, `my-chat` |
| `Format` | The deployment format, e.g., `OpenAI`, `Microsoft`, `xAi`, `Deepseek` |
| `Version` | The deployment version, e.g., `1`, `2025-08-07` |

### Microsoft Foundry project

The Microsoft Foundry project resource exposes the following connection properties:

| Property Name | Description |
|---------------|-------------|
| `Uri` | The project endpoint URI, with the format `https://<account>.services.ai.azure.com/api/projects/<project>` |
| `ConnectionString` | The connection string, with the format `Endpoint=<uri>` |
| `ApplicationInsightsConnectionString` | The Application Insights connection string for telemetry |

Aspire exposes each property as an environment variable named `[RESOURCE]_[PROPERTY]`. For instance, the `Uri` property of a resource called `chat` becomes `CHAT_URI`.

## Microsoft Foundry project usage

You can create a Microsoft Foundry project resource to organize agents and model deployments:

```csharp
var foundry = builder.AddFoundry("foundry");
var project = foundry.AddProject("my-project");

var chat = project.AddModelDeployment("chat", "gpt-4", "1.0", "OpenAI");

var myService = builder.AddPythonApp("agent", "./app", "main:app")
                       .WithReference(project);
```

The project can also be configured with additional Azure resources:

```csharp
var appInsights = builder.AddAzureApplicationInsights("ai");
var keyVault = builder.AddAzureKeyVault("kv");

var project = foundry.AddProject("my-project")
                     .WithAppInsights(appInsights)
                     .WithKeyVault(keyVault);
```

## Hosted agent usage

To deploy a containerized application as a hosted agent in Microsoft Foundry:

```csharp
var foundry = builder.AddFoundry("foundry");
var project = foundry.AddProject("my-project");

builder.AddPythonApp("agent", "./app", "main:app")
       .PublishAsHostedAgent(project);
```

In run mode, the agent runs locally with health check endpoints and OpenTelemetry instrumentation. In publish mode, the agent is deployed as a hosted agent in Microsoft Foundry.

## Prompt agent usage

Prompt agents are declarative agents defined by a model, instructions, and tools. They are always deployed to Azure Foundry — even during local development (`aspire run`) — and local services communicate with the cloud-provisioned agent.

Tools are project-level resources that can be reused across multiple agents:

```csharp
var foundry = builder.AddFoundry("foundry");
var project = foundry.AddProject("my-project");
var chat = project.AddModelDeployment("gpt41", FoundryModel.OpenAI.Gpt41);

// Create tools at the project level
var codeInterp = project.AddCodeInterpreterTool("code-interp");
var webSearch = project.AddWebSearchTool("web-search");

// Add agent with tools
var agent = project.AddPromptAgent(chat, "joker-agent",
    instructions: "You are good at telling jokes.")
    .WithTool(codeInterp)
    .WithTool(webSearch);

builder.AddPythonApp("app", "./app", "main:app")
       .WithReference(agent);
```

### Available tools

Prompt agents support several tool types, all created as project-level resources:

| Tool | Extension Method | Description |
|------|-----------------|-------------|
| Code Interpreter | `project.AddCodeInterpreterTool(name)` | Runs Python code in a sandbox |
| File Search | `project.AddFileSearchTool(name, vectorStoreIds)` | Searches uploaded documents via vector search |
| Web Search | `project.AddWebSearchTool(name)` | Retrieves real-time web information |
| Azure AI Search | `project.AddAISearchTool(name).WithReference(search)` | Grounds responses using Azure AI Search indexes |
| Bing Grounding | `project.AddBingGroundingTool(name).WithReference(conn)` | Grounds responses using Bing Search |
| SharePoint | `project.AddSharePointTool(name, connectionIds)` | Searches SharePoint data |
| Microsoft Fabric | `project.AddFabricTool(name, connectionIds)` | Queries data through Fabric data agents |
| Azure Functions | `project.AddAzureFunctionTool(name, ...)` | Invokes serverless Azure Functions |
| Function Calling | `project.AddFunctionTool(name, funcName, params)` | Calls application-defined functions |
| Image Generation | `project.AddImageGenerationTool(name)` | Generates and edits images (preview) |
| Computer Use | `project.AddComputerUseTool(name, w, h)` | Interacts with a computer desktop (preview) |

### Azure AI Search tool example

```csharp
var search = builder.AddAzureSearch("search");
var aiSearch = project.AddAISearchTool("search-tool").WithReference(search);

var agent = project.AddPromptAgent(chat, "research-agent")
    .WithTool(aiSearch);
```

### Bing Grounding tool example

> **Note:** The Bing Search resource (`Microsoft.Bing/accounts`) cannot be provisioned via Bicep or ARM templates.
> You must create it manually in the [Azure portal](https://portal.azure.com). Once created, Aspire can
> automatically provision the Foundry project connection for you.

The simplest approach is to pass the Bing resource ID directly — Aspire will create the connection with the correct authentication and metadata:

```csharp
var bingResourceId = "/subscriptions/{subId}/resourceGroups/{rg}/providers/Microsoft.Bing/accounts/{name}";
var bing = project.AddBingGroundingTool("bing-tool").WithReference(bingResourceId);

var agent = project.AddPromptAgent(chat, "news-agent")
    .WithTool(bing);
```

Alternatively, you can create the connection yourself for full control:

```csharp
var bingConnection = project.AddBingGroundingConnection("bing-conn", bingResourceId);
var bing = project.AddBingGroundingTool("bing-tool").WithReference(bingConnection);
```

### Tool reuse across agents

Tools are project-level resources, so they can be shared across multiple agents:

```csharp
var codeInterp = project.AddCodeInterpreterTool("code-interp");
var webSearch = project.AddWebSearchTool("web-search");

var agent1 = project.AddPromptAgent(chat, "agent-1").WithTool(codeInterp).WithTool(webSearch);
var agent2 = project.AddPromptAgent(chat, "agent-2").WithTool(codeInterp);
```

## Additional documentation

* https://learn.microsoft.com/azure/ai-foundry/what-is-azure-ai-foundry
* https://learn.microsoft.com/azure/ai-foundry/foundry-local/
* https://github.com/microsoft/aspire/tree/main/src/Components/README.md

## Feedback & contributing

https://github.com/microsoft/aspire
