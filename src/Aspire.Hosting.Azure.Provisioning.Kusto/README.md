# Azure Provisioning Kusto hosting integration

> [!WARNING]
> This package is experimental and emits `ASPIREAZUREPROVISIONING001`.

Use this integration to customize Azure Data Explorer (Kusto) infrastructure from a polyglot Aspire AppHost. It references `Aspire.Hosting.Azure.Kusto` and projects a bounded configuration surface from `Azure.Provisioning.Kusto`; it does not add a separate resource lifecycle.

## Getting started

### Prerequisites

An Azure subscription when provisioning resources.

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure.Provisioning.Kusto` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.Provisioning.Kusto
```

## Usage example

Then, in a TypeScript AppHost, customize the Kusto cluster created by Aspire:

```typescript
import { createBuilder } from "./.aspire/modules/aspire.mjs";

const builder = await createBuilder();
const kusto = await builder.addAzureKustoCluster("kusto");

await kusto.configureInfrastructure(async infrastructure => {
    const cluster = await infrastructure.getKustoCluster();
    const tags = await cluster.tags.get();
    await tags.set("environment", "production");
});
```

## Scope and limitations

`KustoCluster` is the selected infrastructure root. Compatible SDK models, including database resources, are also projected; child resources use identifier-based lookups. This does not imply support for every SDK member. Supported `BicepValue<T>` properties accept the corresponding language value or a shared Bicep expression. These customizations affect Azure infrastructure, not the local emulator.

## Additional documentation

* [Hosting integration](../Aspire.Hosting.Azure.Kusto/README.md)
* [Provisioning inventory and shared expressions](../Aspire.Hosting.Azure.Provisioning/README.md)
* https://aspire.dev/integrations/gallery/
* https://learn.microsoft.com/azure/data-explorer/
* https://learn.microsoft.com/dotnet/azure/sdk/provisioning/

## Feedback & contributing

https://github.com/microsoft/aspire
