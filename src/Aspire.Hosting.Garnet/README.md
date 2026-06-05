# Aspire.Hosting.Garnet library

Provides extension methods and resource definitions for an Aspire AppHost to configure a Garnet cache resource.

## Getting started

### Install the package

In your AppHost project, install the Aspire Garnet Hosting library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Garnet
```

## Usage example

Then, in the _AppHost.cs_ file of `AppHost`, add a Garnet resource and consume the connection using the following methods:

```csharp
var garnet = builder.AddGarnet("cache");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(garnet);
```

The `WithReference` method configures a connection in the `MyService` project named `cache`. In the _Program.cs_ file of `MyService`, the redis connection can be consumed using the client library [Aspire.StackExchange.Redis](https://www.nuget.org/packages/Aspire.StackExchange.Redis):

```csharp
builder.AddRedisClient("cache");
```

## Connection Properties

When you reference a Garnet resource using `WithReference`, the following connection properties are made available to the consuming project:

### Garnet

The Garnet resource exposes the following connection properties:

| Property Name | Description |
|---------------|-------------|
| `Host` | The hostname or IP address of the Garnet server |
| `Port` | The port number the Garnet server is listening on |
| `Password` | The password for authentication (available when a password parameter is configured) |
| `Uri` | The connection URI, with the format `redis://:{Password}@{Host}:{Port}` |

Aspire exposes each property as an environment variable named `[RESOURCE]_[PROPERTY]`. For instance, the `Uri` property of a resource called `cache` becomes `CACHE_URI`.

## Additional documentation

* https://github.com/microsoft/garnet/blob/main/README.md
* https://stackexchange.github.io/StackExchange.Redis/Basics
* https://github.com/dotnet/aspire/tree/main/src/Components/README.md

## Feedback & contributing

https://github.com/dotnet/aspire

_*Garnet MIT License. Copyright (c) Microsoft Corporation.._
