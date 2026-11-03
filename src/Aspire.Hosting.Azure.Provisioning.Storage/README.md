# Azure Provisioning Storage hosting integration

> [!WARNING]
> This package is experimental and emits `ASPIREAZUREPROVISIONING001`.

Use this integration to customize Azure Storage infrastructure from a polyglot Aspire AppHost.

## Getting started

### Prerequisites

An Azure subscription.

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure.Provisioning.Storage` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.Provisioning.Storage
```

## Usage example

Then, in a TypeScript AppHost, customize the storage account created by Aspire:

```typescript
import { createBuilder } from "./.aspire/modules/aspire.mjs";

const builder = await createBuilder();
const storage = await builder.addAzureStorage("storage");

await storage.configureInfrastructure(async infrastructure => {
    const account = await infrastructure.getStorageAccount();
    const tags = await account.tags.get();
    await tags.set("environment", "production");
});
```

The generated proxy exposes an intentionally bounded Azure Provisioning Storage API. Properties backed by `BicepValue<T>` accept either the corresponding language primitive or a shared Bicep expression.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://learn.microsoft.com/azure/storage/
* https://learn.microsoft.com/dotnet/azure/sdk/provisioning/

## Feedback & contributing

https://github.com/microsoft/aspire
