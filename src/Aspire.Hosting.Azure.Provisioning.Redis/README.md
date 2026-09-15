# Azure Provisioning Redis hosting integration

> [!WARNING]
> This package is experimental and emits `ASPIREAZUREPROVISIONING001`.

Use this integration to customize Azure Cache for Redis infrastructure from a polyglot Aspire AppHost. It references `Aspire.Hosting.Azure.Redis` and projects a bounded configuration surface from `Azure.Provisioning.Redis`. For Azure Managed Redis, use `Aspire.Hosting.Azure.Provisioning.RedisEnterprise` instead.

## Getting started

### Prerequisites

An Azure subscription when provisioning resources.

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure.Provisioning.Redis` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.Provisioning.Redis
```

## Usage example

Then, in a TypeScript AppHost, create and customize legacy Redis infrastructure:

```typescript
import { RedisSkuFamily, RedisSkuName, createBuilder } from "./.aspire/modules/aspire.mjs";

const builder = await createBuilder();
const cache = await builder.addAzureInfrastructure("legacyRedis", async infrastructure => {
    const redis = await infrastructure.addRedisResource("legacyRedis");
    const sku = await infrastructure.createRedisSku();
    await sku.name.set(RedisSkuName.Basic);
    await sku.family.set(RedisSkuFamily.C);
    await sku.capacity.set(0);
    await redis.sku.set(sku);
});

await cache.configureInfrastructure(async infrastructure => {
    const redis = await infrastructure.getRedisResource();
    await redis.enableNonSslPort.set(false);
    const tags = await redis.tags.get();
    await tags.set("environment", "production");
});
```

The C# hosting factory `AddAzureRedis` is obsolete and is not exported to polyglot AppHosts. This example uses the exported `addAzureInfrastructure` API and matching Aspire/Bicep identifiers instead. It creates raw infrastructure, not a connection-string resource with the Redis hosting integration's authentication, references, or local container setup. Prefer `addAzureManagedRedis` with the RedisEnterprise overlay for new applications.

## Scope and limitations

`Azure.Provisioning.Redis.RedisResource` is the selected root. Compatible SDK models are projected, not every member. Supported `BicepValue<T>` properties accept the corresponding language value or a shared Bicep expression.

`AdditionalProperties` is explicitly excluded because `BicepDictionary<BinaryData>` contains opaque JSON without a type-safe ATS representation.

Both Redis SDK overlays reference the same Redis hosting integration but expose different Azure resource models. Opt into `.RedisEnterprise` for managed clusters and `.KeyVault` for SDK-level customization of supporting secret resources; the full IDs share the `Aspire.Hosting.Azure.Provisioning` prefix.

## Additional documentation

* [Hosting integration](../Aspire.Hosting.Azure.Redis/README.md)
* [Managed Redis overlay](../Aspire.Hosting.Azure.Provisioning.RedisEnterprise/README.md)
* [Provisioning inventory and shared expressions](../Aspire.Hosting.Azure.Provisioning/README.md)
* https://aspire.dev/integrations/gallery/
* https://learn.microsoft.com/azure/azure-cache-for-redis/
* https://learn.microsoft.com/dotnet/azure/sdk/provisioning/

## Feedback & contributing

https://github.com/microsoft/aspire

_*Redis* is a registered trademark of Redis Ltd. Any rights therein are reserved to *Redis Ltd*._
