# Aspire.StackExchange.Redis.DistributedCaching library

Registers an [IDistributedCache](https://learn.microsoft.com/dotnet/api/microsoft.extensions.caching.distributed.idistributedcache) in the DI container that connects to a [Redis](https://redis.io/)®* server. See [Distributed Caching](https://learn.microsoft.com/aspnet/core/performance/caching/distributed) for more information. Enables corresponding health check, logging, and telemetry.

## Getting started

### Prerequisites

- Redis server and the server hostname for connecting a client.

### Install the package

Install the Aspire StackExchange Redis Distributed Cache library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.StackExchange.Redis.DistributedCaching
```

## Usage example

In the _AppHost.cs_ file of your project, call the `AddRedisDistributedCache` extension method to register an `IDistributedCache` for use via the dependency injection container. The method takes a connection name parameter.

```csharp
builder.AddRedisDistributedCache("cache");
```

You can then retrieve the `IDistributedCache` instance using dependency injection. For example, to retrieve the cache from a Web API controller:

```csharp
private readonly IDistributedCache _cache;

public ProductsController(IDistributedCache cache)
{
    _cache = cache;
}
```

### Keyed IDistributedCache instances

You can register multiple keyed `IDistributedCache` instances by building off the `AddRedisClientBuilder` extension method. The keyed services pattern allows you to register multiple instances of a service with different configurations and retrieve them using a key. The key used for the `IConnectionMultiplexer` service is separate from the key used to register the `IDistributedCache` service.

```csharp
builder.AddRedisClientBuilder("redis") // register an unkeyed IConnectionMultiplexer with the connection name "redis"
       .WithKeyedDistributedCache("distributedCache"); // register a keyed IDistributedCache with key "distributedCache"
```

Then retrieve the keyed service using dependency injection. For example, to retrieve the cache from a Web API controller:

```csharp
private readonly IDistributedCache _cache;

public ProductsController([FromKeyedServices("distributedCache")] IDistributedCache cache)
{
    _cache = cache;
}
```

## Configuration

The Aspire StackExchange Redis Distributed Cache component provides multiple options to configure the Redis connection based on the requirements and conventions of your project. Note that at least one host name is required to connect.

### Use a connection string

When using a connection string from the `ConnectionStrings` configuration section, you can provide the name of the connection string when calling `builder.AddRedisDistributedCache()`:

```csharp
builder.AddRedisDistributedCache("myRedisConnectionName");
```

And then the connection string will be retrieved from the `ConnectionStrings` configuration section:

```json
{
  "ConnectionStrings": {
    "myRedisConnectionName": "localhost:6379"
  }
}
```

See the [Basic Configuration Settings](https://stackexchange.github.io/StackExchange.Redis/Configuration.html#basic-configuration-strings) of the StackExchange.Redis docs for more information on how to format this connection string.

### Use configuration providers

The Redis Distributed Cache component supports [Microsoft.Extensions.Configuration](https://learn.microsoft.com/dotnet/api/microsoft.extensions.configuration). It loads the `StackExchangeRedisSettings` and `ConfigurationOptions` from configuration by using the `Aspire:StackExchange:Redis` key. Example `appsettings.json` that configures some of the options:

```json
{
  "Aspire": {
    "StackExchange": {
      "Redis": {
        "ConfigurationOptions": {
          "ConnectTimeout": 3000,
          "ConnectRetry": 2
        },
        "DisableHealthChecks": true,
        "DisableTracing": false
      }
    }
  }
}
```

### Use inline delegates

You can also pass the `Action<StackExchangeRedisSettings> configureSettings` delegate to set up some or all the options inline, for example to disable health checks from code:

```csharp
builder.AddRedisDistributedCache("cache", settings => settings.DisableHealthChecks = true);
```

You can also setup the [ConfigurationOptions](https://stackexchange.github.io/StackExchange.Redis/Configuration.html#configuration-options) using the `Action<ConfigurationOptions> configureOptions` delegate parameter of the `AddRedisDistributedCache` method. For example to set the connection timeout:

```csharp
builder.AddRedisDistributedCache("cache", configureOptions: options => options.ConnectTimeout = 3000);
```

## AppHost extensions

In your AppHost project, install the `Aspire.Hosting.Redis` library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Redis
```

Then, in the _AppHost.cs_ file of `AppHost`, register a Redis server and consume the connection using the following methods:

```csharp
var redis = builder.AddRedis("cache");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(redis);
```

The `WithReference` method configures a connection in the `MyService` project named `cache`. In the _Program.cs_ file of `MyService`, the redis connection can be consumed using:

```csharp
builder.AddRedisDistributedCache("cache");
```

## Additional documentation

* https://learn.microsoft.com/aspnet/core/performance/caching/distributed
* https://github.com/microsoft/aspire/tree/main/src/Components/README.md

## Feedback & contributing

https://github.com/microsoft/aspire

_*Redis is a registered trademark of Redis Ltd. Any rights therein are reserved to Redis Ltd._
