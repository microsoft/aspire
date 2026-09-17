# Azure Provisioning Cosmos DB hosting integration

> [!WARNING]
> This package is experimental and emits `ASPIREAZUREPROVISIONING001`.

Use this integration to customize Azure Cosmos DB infrastructure from a polyglot Aspire AppHost.

## Getting started

### Prerequisites

An Azure subscription.

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure.Provisioning.CosmosDB` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.Provisioning.CosmosDB
```

## Usage example

Then, in a TypeScript AppHost, customize the Cosmos DB account created by Aspire:

```typescript
import { createBuilder } from "./.aspire/modules/aspire.mjs";

const builder = await createBuilder();
const cosmos = await builder.addAzureCosmosDB("cosmos");

await cosmos.configureInfrastructure(async infrastructure => {
    const account = await infrastructure.getCosmosDBAccount();
    const tags = await account.tags.get();
    await tags.set("environment", "production");
});
```

The generated proxy exposes an intentionally bounded Azure Provisioning Cosmos DB API. Properties backed by `BicepValue<T>` accept either the corresponding language primitive or a shared Bicep expression.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://learn.microsoft.com/azure/cosmos-db/
* https://learn.microsoft.com/dotnet/azure/sdk/provisioning/

## Feedback & contributing

https://github.com/microsoft/aspire
