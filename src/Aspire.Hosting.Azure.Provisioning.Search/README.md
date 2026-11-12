# Azure Provisioning AI Search hosting integration

> [!WARNING]
> This package is experimental and emits `ASPIREAZUREPROVISIONING001`.

Use this integration to customize Azure AI Search infrastructure from a polyglot Aspire AppHost.

## Getting started

### Prerequisites

An Azure subscription.

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure.Provisioning.Search` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.Provisioning.Search
```

## Usage example

Then, in a TypeScript AppHost, customize the search service created by Aspire:

```typescript
import { createBuilder } from "./.aspire/modules/aspire.mjs";

const builder = await createBuilder();
const search = await builder.addAzureSearch("search");

await search.configureInfrastructure(async infrastructure => {
    const service = await infrastructure.getSearchService();
    const tags = await service.tags.get();
    await tags.set("environment", "production");
});
```

The generated proxy exposes an intentionally bounded Azure Provisioning Search API. Properties backed by `BicepValue<T>` accept either the corresponding language primitive or a shared Bicep expression.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://learn.microsoft.com/azure/search/search-what-is-azure-search
* https://learn.microsoft.com/dotnet/azure/sdk/provisioning/

## Feedback & contributing

https://github.com/microsoft/aspire
