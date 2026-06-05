# Aspire.Hosting.Milvus library

Provides extension methods and resource definitions for an Aspire AppHost to configure a Milvus vector database resource.

## Getting started

### Install the package

In your AppHost project, install the Aspire Milvus Hosting library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Milvus
```

## Usage example

Then, in the _AppHost.cs_ file of `AppHost`, add a Milvus resource and consume the connection using the following methods:

```csharp
var milvus = builder.AddMilvus("milvus");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(milvus);
```

## Connection Properties

When you reference a Milvus resource using `WithReference`, the following connection properties are made available to the consuming project:

### Milvus server

The Milvus server resource exposes the following connection properties:

| Property Name | Description |
|---------------|-------------|
| `Host` | The hostname or IP address of the Milvus server |
| `Port` | The gRPC port exposed by the Milvus server |
| `Token` | The authentication token, with the format `root:{ApiKey}` |
| `Uri` | The gRPC endpoint URI, with the format `http://{Host}:{Port}` |

### Milvus database

The Milvus database resource combines the server properties above and adds the following connection property:

| Property Name | Description |
|---------------|-------------|
| `DatabaseName` | The Milvus database name |

Aspire exposes each property as an environment variable named `[RESOURCE]_[PROPERTY]`. For instance, the `Uri` property of a resource called `db1` becomes `DB1_URI`.

## Additional documentation

* https://milvus.io/docs

## Feedback & contributing

https://github.com/dotnet/aspire

_*Milvus and the Milvus logo are used with permission from the Milvus project. All rights reserved by LF AI & Data foundation._
