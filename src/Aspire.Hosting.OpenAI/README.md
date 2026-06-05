# Aspire.Hosting.OpenAI library

Provides extension methods and resource definitions for an Aspire AppHost to configure OpenAI resources and models.

## Getting started

### Prerequisites

- An OpenAI account with access to the OpenAI API
- OpenAI [API key](https://platform.openai.com/api-keys)

### Install the package

In your AppHost project, install the Aspire OpenAI Hosting library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.OpenAI
```

## Usage example

Then, in the _AppHost.cs_ file of `AppHost`, add an OpenAI resource and one or more model resources, and consume connections as needed:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var openai = builder.AddOpenAI("openai");
var chat = openai.AddModel("chat", "gpt-4o-mini");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(chat);
```

The `WithReference` method passes that connection information into a connection string named `chat` in the `MyService` project. Multiple models can share the same OpenAI API key via the parent `OpenAIResource`.

In the _Program.cs_ file of `MyService`, the connection can be consumed using the client library [Aspire.OpenAI](https://www.nuget.org/packages/Aspire.OpenAI):

#### OpenAI client usage

```csharp
builder.AddOpenAIClient("chat");
```

When no model resources are defined, clients can be created with the OpenAI resource only:

```csharp
builder.AddOpenAIClient("openai")
       .AddChatClient("gpt-4o-mini");
```

## Configuration

The OpenAI resources can be configured with the following options:

### API Key

The API key is configured on the parent OpenAI resource via a parameter named `{resource_name}-openai-apikey` or the `OPENAI_API_KEY` environment variable.

Then in user secrets:

```json
{
    "Parameters": 
    {
        "openai-openai-apikey": "YOUR_OPENAI_API_KEY_HERE"
    }
}
```

You can replace the parent API key with a custom parameter on the parent resource:

```csharp
var apiKey = builder.AddParameter("my-api-key", secret: true);
var openai = builder.AddOpenAI("openai").WithApiKey(apiKey);

// share a single key across multiple models
var chat = openai.AddModel("chat", "gpt-4o-mini");
var embeddings = openai.AddModel("embeddings", "text-embedding-3-small");
```

Then in user secrets:

```json
{
    "Parameters": 
    {
        "my-api-key": "YOUR_OPENAI_API_KEY_HERE",
        "alt-key": "ANOTHER_OPENAI_API_KEY"
    }
}
```

## Available Models

OpenAI supports various AI models. Some popular options include:

- `gpt-4o-mini`
- `gpt-4o`
- `gpt-4-turbo`
- `gpt-3.5-turbo`
- `text-embedding-3-small`
- `text-embedding-3-large`
- `dall-e-3`
- `whisper-1`

Check the [OpenAI Models documentation](https://platform.openai.com/docs/models) for the most up-to-date list of available models.

### Custom endpoint

By default, the OpenAI service endpoint is `https://api.openai.com/v1`. To use an OpenAI-compatible gateway or self-hosted endpoint, set a custom endpoint on the parent resource:

```csharp
var openai = builder.AddOpenAI("openai")
                    .WithEndpoint("https://my-gateway.example.com/v1");

var chat = openai.AddModel("chat", "gpt-4o-mini");
```

Both the parent and model connection strings will include the custom endpoint.

## Connection Properties

When you reference an OpenAI resource using `WithReference`, the following connection properties are made available to the consuming project:

### OpenAIResource

The OpenAI resource exposes the following connection properties:

| Property Name | Description |
|---------------|-------------|
| `Endpoint` | The base endpoint URI for the OpenAI API, with the format `https://api.openai.com/v1` |
| `Uri` | The endpoint URI (same as Endpoint), with the format `https://api.openai.com/v1` |
| `Key` | The API key for authentication |

### OpenAI model

The OpenAI model resource combines the parent properties above and adds the following connection property:

| Property Name | Description |
|---------------|-------------|
| `ModelName` | The model identifier for inference requests, for instance `gpt-4o-mini` |

Aspire exposes each property as an environment variable named `[RESOURCE]_[PROPERTY]`. For instance, the `Uri` property of a resource called `chat` becomes `CHAT_URI`.

## Additional documentation

* https://platform.openai.com/docs/models
* https://github.com/dotnet/aspire/tree/main/src/Components/README.md

## Feedback & contributing

https://github.com/dotnet/aspire
