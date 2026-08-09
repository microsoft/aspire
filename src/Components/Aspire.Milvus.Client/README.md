# Aspire.Milvus.Client library

Registers a [MilvusClient](https://github.com/milvus-io/milvus-sdk-csharp) in the DI container for connecting to a Milvus server.

## Getting started

### Prerequisites

- Milvus server and connection string for accessing the server API endpoint.

### Install the package

Install the Aspire Milvus Client library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Milvus.Client
```

## Usage example

In the _AppHost.cs_ file of your project, call the `AddMilvusClient` extension method to register a `MilvusClient` for use via the dependency injection container. The method takes a connection name parameter.

```csharp
builder.AddMilvusClient("milvus");
```

## Configuration

The Aspire Milvus Client component provides multiple options to configure the server connection based on the requirements and conventions of your project.

### Use a connection string

When using a connection string from the `ConnectionStrings` configuration section, you can provide the name of the connection string when calling `builder.AddMilvusClient()`:

```csharp
builder.AddMilvusClient("milvus");
```

And then the connection string will be retrieved from the `ConnectionStrings` configuration section:

```json
{
  "ConnectionStrings": {
    "milvus": "Endpoint=http://localhost:19530/;Key=root:123456!@#$%"
  }
}
```

By default the `MilvusClient` uses the gRPC API endpoint.

### Use configuration providers

The Aspire Milvus Client component supports [Microsoft.Extensions.Configuration](https://learn.microsoft.com/dotnet/api/microsoft.extensions.configuration). It loads the `MilvusSettings` from configuration by using the `Aspire:Milvus:Client` key. Example `appsettings.json` that configures some of the options:

```json
{
  "Aspire": {
    "Milvus": {
      "Client": {
        "Key": "root:123456!@#$%"
      }
    }
  }
}
```

### Use inline delegates

Also you can pass the `Action<MilvusSettings> configureSettings` delegate to set up some or all the options inline, for example to set the API key from code:

```csharp
builder.AddMilvusClient("milvus", settings => settings.Key = "root:12345!@#$%");
```

## AppHost extensions

In your AppHost project, install the `Aspire.Hosting.Milvus` library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Milvus
```

Then, in the _AppHost.cs_ file of `AppHost`, register a Milvus server and consume the connection using the following methods:

```csharp
var milvus = builder.AddMilvus("milvus");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(milvus);
```

The `WithReference` method configures a connection in the `MyService` project named `milvus`. In the _Program.cs_ file of `MyService`, the Milvus connection can be consumed using:

```csharp
builder.AddMilvusClient("milvus");
```

## Additional documentation

* https://github.com/milvus-io/milvus-sdk-csharp
* https://github.com/microsoft/aspire/tree/main/src/Components/README.md

## Feedback & contributing

https://github.com/microsoft/aspire

_*Milvus and the Milvus logo are used with permission from the Milvus project. All rights reserved by LF AI & Data foundation._
