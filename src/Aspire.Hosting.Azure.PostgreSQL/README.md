# Azure Database for PostgreSQL hosting integration

Use this integration to model, configure, and orchestrate Azure Database for PostgreSQL in an Aspire solution.

## Getting started

### Prerequisites

- Azure subscription - [create one for free](https://azure.microsoft.com/free/)

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure.PostgreSQL` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.PostgreSQL
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

In the AppHost, register a Postgres database and reference it from another resource with either C# or TypeScript:

**C#**

```csharp
var postgresdb = builder.AddAzurePostgresFlexibleServer("pg")
                        .AddDatabase("postgresdb");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(postgresdb);
```

**TypeScript**

```typescript
const postgresdb = await builder.addAzurePostgresFlexibleServer("pg")
                        .addDatabase("postgresdb");

const myService = await builder.addNodeApp("myService", "../my-service", "server.js")
                       .withReference(postgresdb);
```

## Connection Properties

When you reference Azure PostgreSQL resources using `WithReference`, the following connection properties are made available to the consuming project:

### Azure PostgreSQL flexible server

The Azure PostgreSQL server resource exposes the following connection properties:

| Property Name | Description |
|---------------|-------------|
| `Host` | The hostname for the PostgreSQL server |
| `Port` | The PostgreSQL port (fixed at `5432` in Azure Flexible Server) |
| `Uri` | The connection URI for the server, with the format `postgresql://{Username}:{Password}@{Host}` (credentials omitted when not applicable) |
| `JdbcConnectionString` | JDBC-format connection string for the server, with the format `jdbc:postgresql://{Host}?sslmode=require&authenticationPluginClassName=com.azure.identity.extensions.jdbc.postgresql.AzurePostgresqlAuthenticationPlugin` |
| `Username` | Present when password authentication is enabled; the configured administrator username |
| `Password` | Present when password authentication is enabled; the configured administrator password |

### Azure PostgreSQL database

The Azure PostgreSQL database resource inherits all properties from its parent server and adds:

| Property Name | Description |
|---------------|-------------|
| `DatabaseName` | The name of the database |
| `Uri` | The database-specific connection URI, with the format `postgresql://{Username}:{Password}@{Host}/{DatabaseName}` (credentials omitted when not applicable) |
| `JdbcConnectionString` | JDBC-format connection string for the database, with the format `jdbc:postgresql://{Host}/{DatabaseName}?sslmode=require&authenticationPluginClassName=com.azure.identity.extensions.jdbc.postgresql.AzurePostgresqlAuthenticationPlugin` |

Aspire exposes each property as an environment variable named `[RESOURCE]_[PROPERTY]`. For instance, the `Uri` property of a resource called `db1` becomes `DB1_URI`.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://aspire.dev/integrations/cloud/azure/azure-postgresql/azure-postgresql-host/
* https://learn.microsoft.com/azure/postgresql/flexible-server/overview

## Feedback & contributing

https://github.com/microsoft/aspire

_*Postgres, PostgreSQL and the Slonik Logo are trademarks or registered trademarks of the PostgreSQL Community Association of Canada, and used with their permission._
