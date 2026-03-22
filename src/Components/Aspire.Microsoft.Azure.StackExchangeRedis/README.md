# Aspire.Microsoft.Azure.StackExchangeRedis library

Configures the [Aspire.StackExchange.Redis](https://www.nuget.org/packages/Aspire.StackExchange.Redis) package with Azure AD authentication when connecting to an Azure Cache for Redis instance.

## Getting started

### Prerequisites

- Azure Cache for Redis instance, learn more about how to [Create an Azure Cache for Redis resource](https://learn.microsoft.com/azure/azure-cache-for-redis/quickstart-create-redis) or [Use the Aspire Azure Redis hosting integration](https://aspire.dev/integrations/cloud/azure/azure-cache-redis/).

### Differences with Aspire.StackExchange.Redis

The Aspire.Microsoft.Azure.StackExchangeRedis library is a wrapper around the Aspire.StackExchange.Redis library that provides additional features for connecting to Azure Cache for Redis with Azure AD authentication. If you don't need these features, you can use the Aspire.StackExchange.Redis library instead.

At runtime the client integration will use Azure AD authentication when a credential is configured, allowing passwordless authentication with Azure Cache for Redis.

### Install the package

Install the Aspire Azure StackExchange.Redis library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Microsoft.Azure.StackExchangeRedis
```

## Usage example

In the _Program.cs_ file of your project, use the `AddRedisClientBuilder` extension method with `WithAzureAuthentication` to register a `IConnectionMultiplexer` for use via the dependency injection container. The method takes a connection name parameter.

```csharp
builder.AddRedisClientBuilder("cache")
    .WithAzureAuthentication();
```

You can then retrieve the `IConnectionMultiplexer` instance using dependency injection. For example, to retrieve the connection from a Web API controller:

```csharp
private readonly IConnectionMultiplexer _cache;

public ProductsController(IConnectionMultiplexer cache)
{
    _cache = cache;
}
```

### Using keyed services

You can also register keyed Redis clients using the keyed services pattern:

```csharp
builder.AddKeyedRedisClientBuilder("cache")
    .WithAzureAuthentication();
```

Then retrieve the keyed service:

```csharp
public ProductsController([FromKeyedServices("cache")] IConnectionMultiplexer cache)
{
    _cache = cache;
}
```

### Adding distributed caching and output caching

You can add distributed caching and output caching to the Redis client by chaining additional methods:

```csharp
builder.AddRedisClientBuilder("cache")
    .WithAzureAuthentication()
    .WithDistributedCache()
    .WithOutputCache();
```

### Configure Azure AD authentication

Use the `WithAzureAuthentication` method to establish a connection using Azure AD authentication. If no credential is provided, a [default TokenCredential is created based on the current environment](https://aka.ms/aspire/default-azure-credential).

```csharp
builder.AddRedisClientBuilder("cache")
    .WithAzureAuthentication(new ManagedIdentityCredential());
```

## AppHost extensions

In your AppHost project, install the `Aspire.Hosting.Azure.Redis` library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Azure.Redis
```

Then, in the _Program.cs_ file of `AppHost`, register an Azure Cache for Redis instance and consume the connection using the following methods:

```csharp
var redis = builder.AddAzureRedis("cache");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(redis);
```

The `WithReference` method configures a connection in the `MyService` project named `cache`. In the _Program.cs_ file of `MyService`, the Redis connection can be consumed using:

```csharp
builder.AddRedisClientBuilder("cache")
    .WithAzureAuthentication();
```

This will also require your Azure environment to be configured by following [these instructions](https://aspire.dev/integrations/cloud/azure/local-provisioning#configuration).

## Additional documentation

* https://stackexchange.github.io/StackExchange.Redis/
* https://github.com/Azure/Microsoft.Azure.StackExchangeRedis

## Feedback & contributing

https://github.com/microsoft/aspire

_*Redis is a registered trademark of Redis Ltd. Any rights therein are reserved to Redis Ltd._
