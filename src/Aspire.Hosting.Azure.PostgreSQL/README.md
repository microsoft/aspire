# Aspire.Hosting.Azure.PostgreSQL library

Provides extension methods and resource definitions for an Aspire AppHost to configure Azure Database for PostgreSQL.

## Getting started

### Prerequisites

- Azure subscription - [create one for free](https://azure.microsoft.com/free/)

### Install the package

In your AppHost project, install the Aspire Azure PostgreSQL Hosting library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Azure.PostgreSQL
```

## Configure Azure Provisioning for local development

Adding Azure resources to the Aspire application model will automatically enable development-time provisioning
for Azure resources so that you don't need to configure them manually. Provisioning requires a number of settings
to be available via .NET configuration. Set these values in user secrets in order to allow resources to be configured
automatically.

```json
{
    "Azure": {
      "SubscriptionId": "<your subscription id>",
      "ResourceGroupPrefix": "<prefix for the resource group>",
      "Location": "<azure location>"
    }
}
```

> NOTE: Developers must have Owner access to the target subscription so that role assignments
> can be configured for the provisioned resources.

## Usage example

Then, in the _AppHost.cs_ file of `AppHost`, register a Postgres database and consume the connection using the following methods:

```csharp
var postgresdb = builder.AddAzurePostgresFlexibleServer("pg")
                        .AddDatabase("postgresdb");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(postgresdb);
```

The `WithReference` method configures a connection in the `MyService` project named `postgresdb`. By default, `AddAzurePostgresFlexibleServer` configures [Microsoft Entra ID](https://learn.microsoft.com/azure/postgresql/flexible-server/concepts-azure-ad-authentication) authentication. This requires changes to applications that need to connect to these resources. In the _Program.cs_ file of `MyService`, the database connection can be consumed using the client library [Aspire.Azure.Npgsql](https://www.nuget.org/packages/Aspire.Azure.Npgsql) or [Aspire.Azure.Npgsql.EntityFrameworkCore.PostgreSQL](https://www.nuget.org/packages/Aspire.Azure.Npgsql.EntityFrameworkCore.PostgreSQL):

```csharp
builder.AddAzureNpgsqlDataSource("postgresdb");
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

* https://www.npgsql.org/doc/basic-usage.html
* https://github.com/dotnet/aspire/tree/main/src/Components/README.md

## Feedback & contributing

https://github.com/dotnet/aspire

_*Postgres, PostgreSQL and the Slonik Logo are trademarks or registered trademarks of the PostgreSQL Community Association of Canada, and used with their permission._
