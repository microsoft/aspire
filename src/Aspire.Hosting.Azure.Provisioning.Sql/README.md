# Azure Provisioning SQL hosting integration

> [!WARNING]
> This package is experimental and emits `ASPIREAZUREPROVISIONING001`.

Use this integration to customize Azure SQL infrastructure from a polyglot Aspire AppHost.

## Getting started

### Prerequisites

An Azure subscription.

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure.Provisioning.Sql` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.Provisioning.Sql
```

## Usage example

Then, in a TypeScript AppHost, customize the SQL server created by Aspire:

```typescript
import { createBuilder } from "./.aspire/modules/aspire.mjs";

const builder = await createBuilder();
const sql = await builder.addAzureSqlServer("sql");

await sql.configureInfrastructure(async infrastructure => {
    const server = await infrastructure.getSqlServer();
    const tags = await server.tags.get();
    await tags.set("environment", "production");
});
```

The generated proxy exposes an intentionally bounded Azure Provisioning SQL API. Properties backed by `BicepValue<T>` accept either the corresponding language primitive or a shared Bicep expression.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://learn.microsoft.com/azure/azure-sql/
* https://learn.microsoft.com/dotnet/azure/sdk/provisioning/

## Feedback & contributing

https://github.com/microsoft/aspire
