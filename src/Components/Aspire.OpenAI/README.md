# Aspire.OpenAI library

Registers [OpenAIClient](https://github.com/openai/openai-dotnet?tab=readme-ov-file#using-the-openaiclient-class) as a singleton in the DI container for using the OpenAI REST API. Enables corresponding metrics, logging and telemetry.

## Getting started

### Prerequisites

- An OpenAI REST API compatible service like OpenAI.com, ollama.com, and others.
- An API key.

### Install the package

Install the Aspire OpenAI library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.OpenAI
```

## Usage example

In the _AppHost.cs_ file of your project, call the `AddOpenAIClient` extension method to register an `OpenAIClient` for use via the dependency injection container. The method takes a connection name parameter.

```csharp
builder.AddOpenAIClient("openaiConnectionName");
```

You can then retrieve the `OpenAIClient` instance using dependency injection. For example, to retrieve the client from a Web API controller:

```csharp
private readonly OpenAIClient _client;

public ChatController(OpenAIClient client)
{
    _client = client;
}
```

To learn how to use the OpenAI client library refer to [Using the OpenAIClient class](https://github.com/openai/openai-dotnet?tab=readme-ov-file#using-the-openaiclient-class).

## Configuration

The Aspire OpenAI library provides multiple options to configure the OpenAI service based on the requirements and conventions of your project. Note that either an `Endpoint` or a `ConnectionString` is required to be supplied.

### Use a connection string

A connection can be constructed from an __Endpoint__ and a __Key__ value with the format `Endpoint={endpoint};Key={key};`. If the `Endpoint` value is omitted the default OpenAI endpoint will be used. You can provide the name of the connection string when calling `builder.AddOpenAIClient()`:

```csharp
builder.AddOpenAIClient("openaiConnectionName");
```

#### Connection string

Alternatively, a custom connection string can be used.

```json
{
  "ConnectionStrings": {
    "openaiConnectionName": "Endpoint=https://{openai_rest_api_url};Key={account_key};"
  }
}
```

### Use configuration providers

The Aspire OpenAI library supports [Microsoft.Extensions.Configuration](https://learn.microsoft.com/dotnet/api/microsoft.extensions.configuration). It loads the `OpenAISettings` and `OpenAIClientOptions` from configuration by using the `Aspire:OpenAI` key. Example `appsettings.json` that configures some of the options:

```json
{
  "Aspire": {
    "OpenAI": {
      "DisableTracing": false,
      "ClientOptions": {
        "UserAgentApplicationId": "myapp"
      }
    }
  }
}
```

### Use inline delegates

You can also pass the `Action<OpenAISettings> configureSettings` delegate to set up some or all the options inline, for example to disable tracing from code:

```csharp
builder.AddOpenAIClient("openaiConnectionName", settings => settings.DisableTracing = true);
```

You can also setup the `OpenAIClientOptions` using the optional `Action<OpenAIClientOptions>? configureOptions` parameter of the `AddOpenAIClient` method. For example, to set a custom `NetworkTimeout` value for this client:

```csharp
builder.AddOpenAIClient("openaiConnectionName", configureOptions: options => options.NetworkTimeout = TimeSpan.FromSeconds(2));
```

## AppHost extensions

There is no specific AppHost extension corresponding to the OpenAI integration. Instead a connection string resource can be registered:

In the _AppHost.cs_ file of `AppHost`, add an OpenAI REST API connection string name:

```csharp
var openai = builder.AddConnectionString("openai");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(openai);
```

The `AddConnectionString` can be used to read connection information from the AppHost's configuration (for example, from "user secrets") under the `ConnectionStrings:openai` config key. The `WithReference` method passes that connection information into a connection string named `openai` in the `MyService` project. In the _Program.cs_ file of `MyService`, the connection can be consumed using:

```csharp
builder.AddOpenAIClient("openai");
```

## Experimental Telemetry

OpenAI telemetry support is experimental, the shape of traces may change in the future without notice.
It can be enabled by invoking

```c#
AppContext.SetSwitch("OpenAI.Experimental.EnableOpenTelemetry", true);
```

or by setting the "OPENAI_EXPERIMENTAL_ENABLE_OPEN_TELEMETRY" environment variable to "true".

## Additional documentation

* https://github.com/openai/openai-dotnet
* https://github.com/microsoft/aspire/tree/main/src/Components/README.md

## Feedback & contributing

https://github.com/microsoft/aspire
