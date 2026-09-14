# Azure Cache for Redis hosting integration

Use this integration to model, configure, and orchestrate Azure Managed Redis in an Aspire solution.

> **Note**: The `AddAzureRedis` method is obsolete. Use `AddAzureManagedRedis` instead, which provisions Azure Managed Redis. Azure Cache for Redis announced its [retirement timeline](https://learn.microsoft.com/azure/azure-cache-for-redis/retirement-faq).

## Getting started

### Prerequisites

- Azure subscription - [create one for free](https://azure.microsoft.com/free/)

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure.Redis` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.Redis
```

## Configure Azure Provisioning for local development

Adding Azure resources to the AppHost model will automatically enable development-time provisioning
for Azure resources so that you don't need to configure them manually. Provisioning requires a number of settings
to be available via AppHost configuration. From your AppHost directory, set these values with `aspire secret set`:

```bash
aspire secret set Azure:SubscriptionId "<your subscription id>"
aspire secret set Azure:ResourceGroupPrefix "<prefix for the resource group>"
aspire secret set Azure:Location "<azure location>"
```

> NOTE: Developers must have Owner access to the target subscription so that role assignments
> can be configured for the provisioned resources.

## Usage example

In the AppHost, register an Azure Managed Redis resource with either C# or TypeScript:

**C#**

```csharp
var redis = builder.AddAzureManagedRedis("cache");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(redis);
```

**TypeScript**

```typescript
const redis = await builder.addAzureManagedRedis("cache");

const myService = await builder.addNodeApp("myService", "../my-service", "server.js")
                       .withReference(redis);
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

* https://aspire.dev/integrations/gallery/
* https://aspire.dev/integrations/cloud/azure/azure-cache-redis/azure-cache-redis-host/
* https://learn.microsoft.com/azure/azure-cache-for-redis/cache-overview

## Feedback & contributing

https://github.com/microsoft/aspire

_*Redis is a registered trademark of Redis Ltd. Any rights therein are reserved to Redis Ltd._
