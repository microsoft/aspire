# Aspire.Azure.AI.OpenAI library

Registers [OpenAIClient](https://learn.microsoft.com/dotnet/api/azure.ai.openai.openaiclient) as a singleton in the DI container for connecting to Azure OpenAI or OpenAI. Enables corresponding metrics, logging and telemetry.

## Getting started

### Prerequisites

- Azure subscription - [create one for free](https://azure.microsoft.com/free/)
- Azure OpenAI or OpenAI account - [create an Azure OpenAI Service resource](https://learn.microsoft.com/azure/ai-services/openai/how-to/create-resource)

### Install the package

Install the Aspire Azure OpenAI library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Azure.AI.OpenAI
```

## Usage example

In the _AppHost.cs_ file of your project, call the `AddAzureOpenAIClient` extension method to register an `OpenAIClient` for use via the dependency injection container. The method takes a connection name parameter.

```csharp
builder.AddAzureOpenAIClient("openaiConnectionName");
```

You can then retrieve the `AzureOpenAIClient` instance using dependency injection. For example, to retrieve the client from a Web API controller:

```csharp
private readonly AzureOpenAIClient _client;

public CognitiveController(AzureOpenAIClient client)
{
    _client = client;
}
```

See the [Azure OpenAI Service quickstarts](https://learn.microsoft.com/azure/ai-services/openai/quickstart) for examples on using the `AzureOpenAIClient`.

## Azure-agnostic client resolution

You can retrieve the `AzureOpenAIClient` object using the base `OpenAIClient` service type. This allows for code that is not dependent on Azure OpenAI-specific features to not depend directly on Azure types.

Additionally this package provides the `AddOpenAIClientFromConfiguration` extension method to register an `OpenAIClient` instance based on the connection string that is provided. This allows your application
to register the best implementation for the OpenAI Rest API it connects. The following rules are followed:

- If the `Endpoint` attribute is empty or missing, the OpenAI service is used and an `OpenAIClient` instance is registered, e.g., `Key={key};`.
- If the attribute `IsAzure` is provided and `true` then `AzureOpenAIClient` is registered, `OpenAIClient` otherwise, e.g., `Endpoint={azure_endpoint};Key={key};IsAzure=true` would register an `AzureOpenAIClient`, while `Endpoint=https://localhost:18889;Key={key}` would register an `OpenAIClient`.
- If the `Endpoint` attribute contains `".azure."` then `AzureOpenAIClient` is registered, `OpenAIClient` otherwise, e.g., `Endpoint=https://{account}.azure.com;Key={key};`.

In any case a valid connection string must contain at least either an `Endpoint` or a `Key`.

## Configuration

The Aspire Azure OpenAI library provides multiple options to configure the Azure OpenAI Service based on the requirements and conventions of your project. Note that either an `Endpoint` or a `ConnectionString` is required to be supplied.

### Use a connection string

A connection can be constructed from the __Keys and Endpoint__ tab with the format `Endpoint={endpoint};Key={key};`. You can provide the name of the connection string when calling `builder.AddAzureOpenAIClient()`:

```csharp
builder.AddAzureOpenAIClient("openaiConnectionName");
```

And then the connection string will be retrieved from the `ConnectionStrings` configuration section. Two connection formats are supported:

#### Account Endpoint

The recommended approach is to use an Endpoint, which works with the `AzureOpenAISettings.Credential` property to establish a connection. If no credential is configured, a [default TokenCredential is created based on the current environment](https://aka.ms/aspire/default-azure-credential).

```json
{
  "ConnectionStrings": {
    "openaiConnectionName": "https://{account_name}.openai.azure.com/"
  }
}
```

#### Connection string

Alternatively, a custom connection string can be used.

```json
{
  "ConnectionStrings": {
    "openaiConnectionName": "Endpoint=https://{account_name}.openai.azure.com/;Key={account_key};"
  }
}
```

In order to connect to the non-Azure OpenAI service, drop the Endpoint property and only set the Key property to set the API key (https://platform.openai.com/account/api-keys).

### Use configuration providers

The Aspire Azure OpenAI library supports [Microsoft.Extensions.Configuration](https://learn.microsoft.com/dotnet/api/microsoft.extensions.configuration). It loads the `AzureOpenAISettings` and `AzureOpenAIClientOptions` from configuration by using the `Aspire:Azure:AI:OpenAI` key. Example `appsettings.json` that configures some of the options:

```json
{
  "Aspire": {
    "Azure": {
      "AI": {
        "OpenAI": {
          "DisableTracing": false,
          "ClientOptions": {
            "UserAgentApplicationId": "myapp"
          }
        }
      }
    }
  }
}
```

### Use inline delegates

You can also pass the `Action<AzureOpenAISettings> configureSettings` delegate to set up some or all the options inline, for example to disable tracing from code:

```csharp
builder.AddAzureOpenAIClient("openaiConnectionName", settings => settings.DisableTracing = true);
```

You can also setup the [AzureOpenAIClientOptions](https://learn.microsoft.com/dotnet/api/azure.ai.openai.openaiclientoptions) using the optional `Action<IAzureClientBuilder<AzureOpenAIClient, AzureOpenAIClientOptions>> configureClientBuilder` parameter of the `AddAzureOpenAIClient` method. For example, to set the client ID for this client:

```csharp
builder.AddAzureOpenAIClient("openaiConnectionName", configureClientBuilder: configureClientBuilder: builder => builder.ConfigureOptions(options => options.NetworkTimeout = TimeSpan.FromSeconds(2)));
```

## AppHost extensions

In your AppHost project, install the Aspire Azure Cognitive Services Hosting library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Azure.CognitiveServices
```

Then, in the _AppHost.cs_ file of `AppHost`, add an Azure OpenAI service and consume the connection using the following methods:

```csharp
var openai = builder.ExecutionContext.IsPublishMode
    ? builder.AddAzureOpenAI("openai")
    : builder.AddConnectionString("openai");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(openai);
```

The `AddAzureOpenAI` method adds an Azure OpenAI resource to the builder. Or `AddConnectionString` can be used to read connection information from the AppHost's configuration (for example, from "user secrets") under the `ConnectionStrings:openai` config key. The `WithReference` method passes that connection information into a connection string named `openai` in the `MyService` project. In the _Program.cs_ file of `MyService`, the connection can be consumed using:

```csharp
builder.AddAzureOpenAIClient("openai");
```

## Experimental Telemetry

Azure AI OpenAI telemetry support is experimental, the shape of traces may change in the future without notice.
It can be enabled by invoking

```c#
AppContext.SetSwitch("OpenAI.Experimental.EnableOpenTelemetry", true);
```

or by setting the "OPENAI_EXPERIMENTAL_ENABLE_OPEN_TELEMETRY" environment variable to "true".

## Additional documentation

* https://learn.microsoft.com/dotnet/api/overview/azure/ai.openai-readme
* https://github.com/microsoft/aspire/tree/main/src/Components/README.md

## Feedback & contributing

https://github.com/microsoft/aspire
