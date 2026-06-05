# Aspire.Hosting.Qdrant library

Provides extension methods and resource definitions for an Aspire AppHost to configure a Qdrant vector database resource.

## Getting started

### Install the package

In your AppHost project, install the Aspire Qdrant Hosting library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Qdrant
```

## Usage example

Then, in the _AppHost.cs_ file of `AppHost`, add a Qdrant resource and consume the connection using the following methods:

```csharp
var qdrant = builder.AddQdrant("qdrant");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(qdrant);
```

## Connection Properties

When you reference a Qdrant resource using `WithReference`, the following connection properties are made available to the consuming project:

### Qdrant server

The Qdrant server resource exposes the following connection properties:

| Property Name | Description |
|---------------|-------------|
| `GrpcHost` | The gRPC hostname of the Qdrant server |
| `GrpcPort` | The gRPC port of the Qdrant server |
| `HttpHost` | The HTTP hostname of the Qdrant server |
| `HttpPort` | The HTTP port of the Qdrant server |
| `ApiKey` | The API key for authentication |
| `Uri` | The gRPC connection URI, with the format `http://{GrpcHost}:{GrpcPort}` |
| `HttpUri` | The HTTP connection URI, with the format `http://{HttpHost}:{HttpPort}` |

Aspire exposes each property as an environment variable named `[RESOURCE]_[PROPERTY]`. For instance, the `Uri` property of a resource called `db1` becomes `DB1_URI`.

## Additional documentation

* https://qdrant.tech/documentation

## Feedback & contributing

https://github.com/microsoft/aspire

_Qdrant, and the Qdrant logo are trademarks or registered trademarks of Qdrant Solutions GmbH of Germany, and used with their permission._
