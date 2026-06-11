# Aspire.Azure.Npgsql library

Registers [NpgsqlDataSource](https://www.npgsql.org/doc/api/Npgsql.NpgsqlDataSource.html) in the DI container for connecting to PostgreSQL and Azure Database for PostgreSQL. Enables corresponding health check, metrics, logging and telemetry.

## Getting started

### Prerequisites

- PostgreSQL database and connection string for accessing the database.
- or an Azure Database for PostgreSQL instance, learn more about how to [Create an Azure Database for PostgreSQL resource](https://learn.microsoft.com/azure/postgresql/flexible-server/quickstart-create-server?tabs=portal-create-flexible%2Cportal-get-connection%2Cportal-delete-resources).

### Differences with Aspire.Npgsql

The Aspire.Azure.Npgsql library is a wrapper around the Aspire.Npgsql library that provides additional features for connecting to Azure Database for PostgreSQL. If you don't need these features, you can use the Aspire.Npgsql library instead.
At runtime the client integration will detect whether the connection string has a Username and Password, and if not, it will use Entra Id to authenticate with Azure Database for PostgreSQL.

### Install the package

Install the Aspire Azure Npgsql library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Azure.Npgsql
```

## Usage example

In the _AppHost.cs_ file of your project, call the `AddAzureNpgsqlDataSource` extension method to register a `NpgsqlDataSource` for use via the dependency injection container. The method takes a connection name parameter.

```csharp
builder.AddAzureNpgsqlDataSource("postgresdb");
```

You can then retrieve the `NpgsqlDataSource` instance using dependency injection. For example, to retrieve the data source from a Web API controller:

```csharp
private readonly NpgsqlDataSource _dataSource;

public ProductsController(NpgsqlDataSource dataSource)
{
    _dataSource = dataSource;
}
```

## Configuration

The Aspire Azure Npgsql component provides multiple options to configure the database connection based on the requirements and conventions of your project.

### Use a connection string

When using a connection string from the `ConnectionStrings` configuration section, you can provide the name of the connection string when calling `builder.AddAzureNpgsqlDataSource()`:

```csharp
builder.AddAzureNpgsqlDataSource("myConnection");
```

And then the connection string will be retrieved from the `ConnectionStrings` configuration section:

```json
{
  "ConnectionStrings": {
    "myConnection": "Host=myserver;Database=test"
  }
}
```

See the [ConnectionString documentation](https://www.npgsql.org/doc/connection-string-parameters.html) for more information on how to format this connection string.

Note that the username and password will be automatically inferred from the credential provided in the settings.

### Use configuration providers

The Aspire Azure Npgsql component supports [Microsoft.Extensions.Configuration](https://learn.microsoft.com/dotnet/api/microsoft.extensions.configuration). It loads the `AzureNpgsqlSettings` from configuration by using the `Aspire:Npgsql` key. Example `appsettings.json` that configures some of the options:

```json
{
  "Aspire": {
    "Npgsql": {
      "DisableHealthChecks": true,
      "DisableTracing": true
    }
  }
}
```

### Use inline delegates

Also you can pass the `Action<AzureNpgsqlSettings> configureSettings` delegate to set up some or all the options inline, for example to disable health checks from code:

```csharp
    builder.AddAzureNpgsqlDataSource("postgresdb", settings => settings.DisableHealthChecks = true);
```

Use the `AzureNpgsqlSettings.Credential` property to establish a connection. If no credential is configured, a [default TokenCredential is created based on the current environment](https://aka.ms/aspire/default-azure-credential).

If the connection string contains a username and a password then the credential will be ignored.

## AppHost extensions

In your AppHost project, install the `Aspire.Hosting.Azure.PostgreSQL` library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Azure.PostgreSQL
```

Then, in the _AppHost.cs_ file of `AppHost`, register a Azure Database for PostgreSQL instance and consume the connection using the following methods:

```csharp
var postgresdb = builder.AddAzurePostgresFlexibleServer("pg").AddDatabase("postgresdb");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(postgresdb);
```

The `WithReference` method configures a connection in the `MyService` project named `postgresdb`. In the _Program.cs_ file of `MyService`, the database connection can be consumed using:

```csharp
builder.AddAzureNpgsqlDataSource("postgresdb");
```

This will also require your Azure environment to be configure by following [these instructions](https://aspire.dev/integrations/cloud/azure/local-provisioning#configuration).

## Troubleshooting

In the rare case that the Username property is not provided and the integration can't detect it using the application's Managed Identity, Npgsql will throw an exception like the following:

> Npgsql.PostgresException (0x80004005): 28P01: password authentication failed for user ...

In that case you can configure the Username property in the connection string by using the `configureDataSourceBuilder` callback like so:

```csharp
builder.AddAzureNpgsqlDataSource("db", configureDataSourceBuilder:
  dataSourceBuilder => dataSourceBuilder.ConnectionStringBuilder.Username = "<PRINCIPALNAME>");
```

## Additional documentation

* https://aspire.dev/integrations/cloud/azure/azure-postgresql/
* https://www.npgsql.org/doc/basic-usage.html
* https://github.com/microsoft/aspire/tree/main/src/Components/README.md

## Feedback & contributing

https://github.com/microsoft/aspire

_*Postgres, PostgreSQL and the Slonik Logo are trademarks or registered trademarks of the PostgreSQL Community Association of Canada, and used with their permission._
