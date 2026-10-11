# Azure Provisioning Managed Redis hosting integration

> [!WARNING]
> This package is experimental and emits `ASPIREAZUREPROVISIONING001`.

Use this integration to customize Azure Managed Redis infrastructure from a polyglot Aspire AppHost. It references `Aspire.Hosting.Azure.Redis`; the `RedisEnterprise` suffix follows the underlying `Azure.Provisioning.RedisEnterprise` SDK, not a separate hosting integration.

## Getting started

### Prerequisites

An Azure subscription when provisioning resources.

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure.Provisioning.RedisEnterprise` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.Provisioning.RedisEnterprise
```

## Usage example

Then, in a TypeScript AppHost, customize the managed Redis cluster created by Aspire:

```typescript
import { createBuilder } from "./.aspire/modules/aspire.mjs";

const builder = await createBuilder();
const redis = await builder.addAzureManagedRedis("redis");

await redis.configureInfrastructure(async infrastructure => {
    const cluster = await infrastructure.getRedisEnterpriseCluster();
    const tags = await cluster.tags.get();
    await tags.set("environment", "production");
});
```

## Scope and limitations

`RedisEnterpriseCluster` is the selected root. Compatible models from the same SDK assembly, including database resources, are also projected; child resources use identifier-based lookups. This does not imply support for every SDK member. Supported `BicepValue<T>` properties accept the corresponding language value or a shared Bicep expression.

Azure Cache for Redis uses the separate `Aspire.Hosting.Azure.Provisioning.Redis` overlay. For SDK-level customization of Key Vault resources used by access-key authentication, also opt into `Aspire.Hosting.Azure.Provisioning.KeyVault`. This overlay does not change the hosting integration's authentication defaults, local Redis container, or resource lifecycle.

## Additional documentation

* [Hosting integration](../Aspire.Hosting.Azure.Redis/README.md)
* [Legacy Redis overlay](../Aspire.Hosting.Azure.Provisioning.Redis/README.md)
* [Provisioning inventory and shared expressions](../Aspire.Hosting.Azure.Provisioning/README.md)
* https://aspire.dev/integrations/gallery/
* https://learn.microsoft.com/azure/redis/
* https://learn.microsoft.com/dotnet/azure/sdk/provisioning/

## Feedback & contributing

https://github.com/microsoft/aspire

_*Redis* is a registered trademark of Redis Ltd. Any rights therein are reserved to *Redis Ltd*._
