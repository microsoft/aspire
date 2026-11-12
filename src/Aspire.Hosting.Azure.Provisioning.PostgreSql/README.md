# Azure Provisioning PostgreSQL hosting integration

> [!WARNING]
> This package is experimental and emits `ASPIREAZUREPROVISIONING001`.

Use this integration to customize Azure Database for PostgreSQL infrastructure from a polyglot Aspire AppHost. It references `Aspire.Hosting.Azure.PostgreSQL`; the `PostgreSql` spelling follows the underlying `Azure.Provisioning.PostgreSql` SDK.

## Getting started

### Prerequisites

An Azure subscription when provisioning resources.

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure.Provisioning.PostgreSql` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.Provisioning.PostgreSql
```

## Usage example

Then, in a TypeScript AppHost, customize the PostgreSQL flexible server created by Aspire:

```typescript
import { createBuilder } from "./.aspire/modules/aspire.mjs";

const builder = await createBuilder();
const postgres = await builder.addAzurePostgresFlexibleServer("postgres");

await postgres.configureInfrastructure(async infrastructure => {
    const server = await infrastructure.getPostgreSqlFlexibleServer();
    const tags = await server.tags.get();
    await tags.set("environment", "production");
});
```

## Scope and limitations

`PostgreSqlFlexibleServer` is the selected infrastructure root. Compatible SDK models, including child databases, are also projected; child resources use identifier-based lookups. This does not imply support for every SDK member. Supported `BicepValue<T>` properties accept the corresponding language value or a shared Bicep expression.

For SDK-level customization of Key Vault resources used by password authentication, also opt into `Aspire.Hosting.Azure.Provisioning.KeyVault`. The overlay configures Azure infrastructure, not the local PostgreSQL container, and does not change the hosting integration's authentication or lifecycle defaults.

## Additional documentation

* [Hosting integration](../Aspire.Hosting.Azure.PostgreSQL/README.md)
* [Provisioning inventory and shared expressions](../Aspire.Hosting.Azure.Provisioning/README.md)
* https://aspire.dev/integrations/gallery/
* https://learn.microsoft.com/azure/postgresql/
* https://learn.microsoft.com/dotnet/azure/sdk/provisioning/

## Feedback & contributing

https://github.com/microsoft/aspire

_*Postgres*, *PostgreSQL* and the *Slonik Logo* are trademarks or registered trademarks of the *PostgreSQL Community Association of Canada*, and used with their permission._
