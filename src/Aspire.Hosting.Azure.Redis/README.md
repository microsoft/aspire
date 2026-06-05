# Aspire.Hosting.Azure.Redis library

Provides extension methods and resource definitions for an Aspire AppHost to configure Azure Managed Redis.

> **Note**: The `AddAzureRedis` method is obsolete. Use `AddAzureManagedRedis` instead, which provisions Azure Managed Redis. Azure Cache for Redis announced its [retirement timeline](https://learn.microsoft.com/azure/azure-cache-for-redis/retirement-faq).

## Getting started

### Prerequisites

- Azure subscription - [create one for free](https://azure.microsoft.com/free/)

### Install the package

In your AppHost project, install the `Aspire.Hosting.Azure.Redis` library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Azure.Redis
```

## Configure Azure Provisioning for local development

Adding Azure resources to the Aspire application model will automatically enable development-time provisioning
for Azure resources so that you don't need to configure them manually. Provisioning requires a number of settings
to be available via .NET configuration. Set these values in user secrets in order to allow resources to be configured
automatically.

```json
{
    "Azure": {
      "SubscriptionId": "<your subscription id>",
      "ResourceGroupPrefix": "<prefix for the resource group>",
      "Location": "<azure location>"
    }
}
```

> NOTE: Developers must have Owner access to the target subscription so that role assignments
> can be configured for the provisioned resources.

## Usage example

Then, in the _AppHost.cs_ file of `AppHost`, register an Azure Managed Redis resource using the following methods:

```csharp
var redis = builder.AddAzureManagedRedis("cache");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(redis);
```

The `WithReference` method configures a connection in the `MyService` project named `cache`. By default, `AddAzureManagedRedis` configures [Microsoft Entra ID](https://learn.microsoft.com/azure/redis/entra-for-authentication) authentication. This requires changes to applications that need to connect to these resources. In the _Program.cs_ file of `MyService`, the redis connection can be consumed using the client library [Aspire.Microsoft.Azure.StackExchangeRedis](https://www.nuget.org/packages/Aspire.Microsoft.Azure.StackExchangeRedis):

```csharp
builder.AddRedisClientBuilder("cache")
       .WithAzureAuthentication();
```

## Connection Properties

When you reference Azure Redis resources using `WithReference`, the following connection properties are made available to the consuming project:

### Azure Redis Enterprise

| Property Name | Description |
|---------------|-------------|
| `Host` | The hostname of the Azure Redis Enterprise database endpoint. |
| `Port` | The port of the Azure Redis Enterprise database endpoint (10000 for Azure). |
| `Uri` | The Redis connection URI. In Azure mode this is `redis://{Host}`; when running via `RunAsContainer` it matches `redis://[:{Password}@]{Host}:{Port}`. |
| `Password` | The access key for the Redis server. Empty when using Entra ID authentication; populated when using `WithAccessKeyAuthentication()` or running as a container. |

Aspire exposes each property as an environment variable named `[RESOURCE]_[PROPERTY]`. For instance, the `Uri` property of a resource called `cache` becomes `CACHE_URI`.

## Additional documentation

* https://stackexchange.github.io/StackExchange.Redis/Basics
* https://github.com/dotnet/aspire/tree/main/src/Components/README.md

## Feedback & contributing

https://github.com/dotnet/aspire

_*Redis is a registered trademark of Redis Ltd. Any rights therein are reserved to Redis Ltd._
