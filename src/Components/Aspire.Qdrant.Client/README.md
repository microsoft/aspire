# Aspire.Qdrant.Client library

Registers a [QdrantClient](https://github.com/qdrant/qdrant-dotnet) in the DI container for connecting to a Qdrant server.

## Getting started

### Prerequisites

- Qdrant server and connection string for accessing the server API endpoint.

### Install the package

Install the Aspire Qdrant Client library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Qdrant.Client
```

## Usage example

In the _AppHost.cs_ file of your project, call the `AddQdrantClient` extension method to register a `QdrantClient` for use via the dependency injection container. The method takes a connection name parameter.

```csharp
builder.AddQdrantClient("qdrant");
```

## Configuration

The Aspire Qdrant Client component provides multiple options to configure the server connection based on the requirements and conventions of your project.

### Use a connection string

When using a connection string from the `ConnectionStrings` configuration section, you can provide the name of the connection string when calling `builder.AddQdrantClient()`:

```csharp
builder.AddQdrantClient("qdrant");
```

And then the connection string will be retrieved from the `ConnectionStrings` configuration section:

```json
{
  "ConnectionStrings": {
    "qdrant": "Endpoint=http://localhost:6334;Key=123456!@#$%"
  }
}
```

By default the `QdrantClient` uses the gRPC API endpoint.

### Use configuration providers

The Aspire Qdrant Client component supports [Microsoft.Extensions.Configuration](https://learn.microsoft.com/dotnet/api/microsoft.extensions.configuration). It loads the `QdrantSettings` from configuration by using the `Aspire:Qdrant:Client` key. Example `appsettings.json` that configures some of the options:

```json
{
  "Aspire": {
    "Qdrant": {
      "Client": {
        "Key": "123456!@#$%"
      }
    }
  }
}
```

### Use inline delegates

Also you can pass the `Action<QdrantSettings> configureSettings` delegate to set up some or all the options inline, for example to set the API key from code:

```csharp
builder.AddQdrantClient("qdrant", settings => settings.ApiKey = "12345!@#$%");
```

## AppHost extensions

In your AppHost project, install the `Aspire.Hosting.Qdrant` library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Qdrant
```

Then, in the _AppHost.cs_ file of `AppHost`, register a Qdrant server and consume the connection using the following methods:

```csharp
var qdrant = builder.AddQdrant("qdrant");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(qdrant);
```

The `WithReference` method configures a connection in the `MyService` project named `qdrant`. In the _Program.cs_ file of `MyService`, the Qdrant connection can be consumed using:

```csharp
builder.AddQdrantClient("qdrant");
```

## Additional documentation

* https://github.com/qdrant/qdrant-dotnet
* https://github.com/microsoft/aspire/tree/main/src/Components/README.md

## Feedback & contributing

https://github.com/microsoft/aspire

_Qdrant, and the Qdrant logo are trademarks or registered trademarks of Qdrant Solutions GmbH of Germany, and used with their permission._
