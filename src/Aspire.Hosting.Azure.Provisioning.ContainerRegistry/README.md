# Azure Provisioning Container Registry hosting integration

> [!WARNING]
> This package is experimental and emits `ASPIREAZUREPROVISIONING001`.

Use this integration to customize Azure Container Registry infrastructure from a polyglot Aspire AppHost.

## Getting started

### Prerequisites

An Azure subscription.

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure.Provisioning.ContainerRegistry` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.Provisioning.ContainerRegistry
```

## Usage example

Then, in a TypeScript AppHost, customize the container registry created by Aspire:

```typescript
import { createBuilder } from "./.aspire/modules/aspire.mjs";

const builder = await createBuilder();
const registry = await builder.addAzureContainerRegistry("registry");

await registry.configureInfrastructure(async infrastructure => {
    const service = await infrastructure.getContainerRegistryService();
    const tags = await service.tags.get();
    await tags.set("environment", "production");
});
```

The generated proxy exposes an intentionally bounded Azure Provisioning Container Registry API. Properties backed by `BicepValue<T>` accept either the corresponding language primitive or a shared Bicep expression.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://learn.microsoft.com/azure/container-registry/
* https://learn.microsoft.com/dotnet/azure/sdk/provisioning/

## Feedback & contributing

https://github.com/microsoft/aspire
