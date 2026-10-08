# Aspire.Azure.AI.Projects library

Registers an [Azure AI Projects `AIProjectClient`](https://learn.microsoft.com/dotnet/api/overview/azure/ai.projects-readme) in the DI container as a singleton, with configuration binding, Microsoft Entra authentication, and tracing.

## Getting started

### Prerequisites

An [Azure subscription](https://azure.microsoft.com/free/), a Microsoft Foundry project, and a Microsoft Entra identity with access to the operations you use. Use a project endpoint such as `https://account.services.ai.azure.com/api/projects/project`, not an Azure OpenAI account endpoint, a Foundry Local endpoint, or a hub-based project's connection string.

### Install the package

Install the Aspire Azure AI Projects library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Azure.AI.Projects
```

## Usage example

In the consuming application's `Program.cs`:

```csharp
builder.AddAzureAIProjectClient("ai");
```

Supply the connection string through configuration:

```json
{
  "ConnectionStrings": {
    "ai": "Endpoint=https://account.services.ai.azure.com/api/projects/project"
  }
}
```

A bare project URI is also accepted. Project clients use Microsoft Entra authentication; `Key` connection strings are rejected rather than silently ignored. By default, Aspire uses a development credential locally and managed identity when deployed to Azure.

Resolve the singleton through dependency injection:

```csharp
using Azure.AI.Projects;

public class AgentService(AIProjectClient projectClient)
{
    public AIProjectClient ProjectClient => projectClient;
}
```

The client exposes project connections, deployments, datasets, `AgentAdministrationClient`, and `ProjectOpenAIClient`. Refer to the SDK documentation for individual operations and required permissions.

### Keyed clients

```csharp
builder.AddKeyedAzureAIProjectClient("ai");

var client = serviceProvider.GetRequiredKeyedService<AIProjectClient>("ai");
```

Each key identifies both the connection string and the registered service.

## Configuration

Configuration is applied in this order: `Aspire:Azure:AI:Projects`, its connection-specific subsection, the named connection string, and finally `configureSettings`. SDK options are bound from the root and connection-specific `ClientOptions` sections before `configureClientBuilder` runs.

### Use a connection string

Provide `ConnectionStrings:ai` as shown in the usage example. Both a bare project URI and `Endpoint=https://account.services.ai.azure.com/api/projects/project` are supported.

### Use configuration providers

```json
{
  "Aspire": {
    "Azure": {
      "AI": {
        "Projects": {
          "DisableTracing": false,
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
```

### Use inline delegates

Settings and SDK options can also be supplied in code, without a connection string:

```csharp
using Azure.Identity;
using Microsoft.Extensions.Azure;

builder.AddAzureAIProjectClient("ai",
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

## AppHost extensions

Install `Aspire.Hosting.Foundry` in the AppHost and reference the project from the consuming application:

```dotnetcli
dotnet add package Aspire.Hosting.Foundry
```

```csharp
var foundry = builder.AddFoundry("foundry");
var project = foundry.AddProject("ai");

builder.AddProject<Projects.MyApp>("app")
    .WithReference(foundry)
    .WithReference(project);
```

`WithReference(project)` supplies the project connection string. The account reference configures runtime identity and Foundry access. Register `builder.AddAzureAIProjectClient("ai")` in `MyApp`. Deployment permissions and the application's runtime permissions are distinct; additional operations may require additional roles.

## Telemetry and health checks

The integration subscribes to `Azure.AI.Projects.*` activity sources unless `DisableTracing` is true. Azure identity logs use the `Azure.Identity` category. Exporters are configured by the application's service defaults or OpenTelemetry setup.

No metrics or health check is registered. The integration does not perform inference or agent-management requests as a health probe.

## Migration

`Aspire.Azure.AI.OpenAI` and `Aspire.Azure.AI.Inference` are no longer produced. Previously published versions remain available; this change does not uninstall them or retire the hosting integrations. See [microsoft/aspire#20401](https://github.com/microsoft/aspire/issues/20401).

Use this package for project and agent-management operations. For model clients, chat, embeddings, or Responses, use `Aspire.Azure.AI.Extensions.OpenAI`; its README describes the endpoint, authentication, and API migration. These project integrations are not drop-in replacements for account-level API-key connections or Foundry Local.

## Additional documentation

* [Azure AI Projects SDK](https://learn.microsoft.com/dotnet/api/overview/azure/ai.projects-readme)
* https://github.com/microsoft/aspire/tree/main/src/Components/README.md

## Feedback & contributing

https://github.com/microsoft/aspire
