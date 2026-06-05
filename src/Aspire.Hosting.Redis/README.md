# Aspire.Hosting.Redis library

Provides extension methods and resource definitions for an Aspire AppHost to configure a Redis resource.

## Getting started

### Install the package

In your AppHost project, install the Aspire Redis Hosting library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Redis
```

## Usage example

Then, in the _AppHost.cs_ file of `AppHost`, add a Redis resource and consume the connection using the following methods:

```csharp
var redis = builder.AddRedis("redis");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(redis);
```

## Connection Properties

When you reference a Redis resource using `WithReference`, the following connection properties are made available to the consuming project:

### Redis

The Redis resource exposes the following connection properties:

| Property Name | Description |
|---------------|-------------|
| `Host` | The hostname or IP address of the Redis server |
| `Port` | The port number the Redis server is listening on |
| `Password` | The password for authentication |
| `Uri` | The connection URI, with the format `redis://:{Password}@{Host}:{Port}` |

Aspire exposes each property as an environment variable named `[RESOURCE]_[PROPERTY]`. For instance, the `Uri` property of a resource called `cache` becomes `CACHE_URI`.

## Additional documentation

* https://learn.microsoft.com/dotnet/aspire/caching/stackexchange-redis-component
* https://learn.microsoft.com/dotnet/aspire/caching/stackexchange-redis-output-caching-component
* https://learn.microsoft.com/dotnet/aspire/caching/stackexchange-redis-distributed-caching-component

## Feedback & contributing

https://github.com/dotnet/aspire

_*Redis is a registered trademark of Redis Ltd. Any rights therein are reserved to Redis Ltd._
