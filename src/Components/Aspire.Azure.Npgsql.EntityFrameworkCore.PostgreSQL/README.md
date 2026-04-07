# Aspire.Azure.Npgsql.EntityFrameworkCore.PostgreSQL library

Registers [EntityFrameworkCore](https://learn.microsoft.com/ef/core/) [DbContext](https://learn.microsoft.com/dotnet/api/microsoft.entityframeworkcore.dbcontext) in the DI container for connecting to PostgreSQL® and Azure Database for PostgreSQL®. Enables connection pooling, retries, health check, logging and telemetry.

## Getting started

### Prerequisites

- PostgreSQL database and connection string for accessing the database.
- or an Azure Database for PostgreSQL instance, learn more about how to [Create an Azure Database for PostgreSQL resource](https://learn.microsoft.com/azure/postgresql/flexible-server/quickstart-create-server?tabs=portal-create-flexible%2Cportal-get-connection%2Cportal-delete-resources).

### Differences with Aspire.Npgsql.EntityFrameworkCore.PostgreSQL

The Aspire.Azure.Npgsql.EntityFrameworkCore.PostgreSQL library is a wrapper around the Aspire.Npgsql.EntityFrameworkCore.PostgreSQL library that provides additional features for connecting to Azure Database for PostgreSQL. If you don't need these features, you can use the Aspire.Npgsql.EntityFrameworkCore.PostgreSQL library instead.
At runtime the client integration will detect whether the connection string has a Username and Password, and if not, it will use Entra Id to authenticate with Azure Database for PostgreSQL.

### Install the package

Install the Aspire Azure PostgreSQL EntityFrameworkCore Npgsql library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Azure.Npgsql.EntityFrameworkCore.PostgreSQL
```

## Usage example

In the _AppHost.cs_ file of your project, call the `AddAzureNpgsqlDbContext` extension method to register a `DbContext` for use via the dependency injection container. The method takes a connection name parameter.

```csharp
builder.AddAzureNpgsqlDbContext<MyDbContext>("postgresdb");
```

You can then retrieve the `MyDbContext` instance using dependency injection. For example, to retrieve the context from a Web API controller:

```csharp
private readonly MyDbContext _context;

public ProductsController(MyDbContext context)
{
    _context = context;
}
```

You might also need to configure specific option of Npgsql, or register a `DbContext` in other ways. In this case call the `EnrichAzureNpgsqlDbContext` extension method, for example:

```csharp
var connectionString = builder.Configuration.GetConnectionString("postgresdb");
builder.Services.AddDbContextPool<MyDbContext>(dbContextOptionsBuilder => dbContextOptionsBuilder.UseNpgsql(connectionString));
builder.EnrichAzureNpgsqlDbContext<MyDbContext>();
```

## Configuration

The Aspire Azure PostgreSQL EntityFrameworkCore Npgsql component provides multiple options to configure the database connection based on the requirements and conventions of your project.

### Use a connection string

When using a connection string from the `ConnectionStrings` configuration section, you can provide the name of the connection string when calling `builder.AddAzureNpgsqlDbContext()`:

```csharp
builder.AddAzureNpgsqlDbContext<MyDbContext>("myConnection");
```

And then the connection string will be retrieved from the `ConnectionStrings` configuration section:

```json
{
  "ConnectionStrings": {
    "myConnection": "Host=myserver;Database=test"
  }
}
```

The `EnrichAzureNpgsqlDbContext` won't make use of the `ConnectionStrings` configuration section since it expects a `DbContext` to be registered at the point it is called.

See the [ConnectionString documentation](https://www.npgsql.org/doc/connection-string-parameters.html) for more information on how to format this connection string.

### Use configuration providers

The Aspire PostgreSQL EntityFrameworkCore Npgsql component supports [Microsoft.Extensions.Configuration](https://learn.microsoft.com/dotnet/api/microsoft.extensions.configuration). It loads the `AzureNpgsqlEntityFrameworkCorePostgreSQLSettings` from configuration by using the `Aspire:Npgsql:EntityFrameworkCore:PostgreSQL` key. Example `appsettings.json` that configures some of the options:

```json
{
  "Aspire": {
    "Npgsql": {
      "EntityFrameworkCore": {
        "PostgreSQL": {
          "DisableHealthChecks": true,
          "DisableTracing": true
        }
      }
    }
  }
}
```

### Use inline delegates

Also you can pass the `Action<AzureNpgsqlEntityFrameworkCorePostgreSQLSettings> configureSettings` delegate to set up some or all the options inline, for example to disable health checks from code:

```csharp
    builder.AddAzureNpgsqlDbContext<MyDbContext>("postgresdb", settings => settings.DisableHealthChecks = true);
```

or

```csharp
    builder.EnrichAzureNpgsqlDbContext<MyDbContext>(settings => settings.DisableHealthChecks = true);
```

Use the `AzureNpgsqlEntityFrameworkCorePostgreSQLSettings.Credential` property to establish a connection. If no credential is configured, a [default TokenCredential is created based on the current environment](https://aka.ms/aspire/default-azure-credential).

If the connection string contains a username and a password then the credential will be ignored.

## AppHost extensions

In your AppHost project, install the `Aspire.Hosting.Azure.PostgreSQL` library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Azure.PostgreSQL
```

Then, in the _AppHost.cs_ file of `AppHost`, register Azure Database for PostgreSQL instance and consume the connection using the following methods:

```csharp
var postgresdb = builder.AddAzurePostgresFlexibleServer("pg").AddDatabase("postgresdb");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(postgresdb);
```

The `WithReference` method configures a connection in the `MyService` project named `postgresdb`. In the _Program.cs_ file of `MyService`, the database connection can be consumed using:

```csharp
builder.AddAzureNpgsqlDbContext<MyDbContext>("postgresdb");
```

## Troubleshooting

In the rare case that the Username property is not provided and the integration can't detect it using the application's Managed Identity, Npgsql will throw an exception like the following:

> Npgsql.PostgresException (0x80004005): 28P01: password authentication failed for user ...

In that case you can configure the Username property in the connection string and use `EnrichAzureNpgsqlDbContext`, passing the connection string in `UseNpgsql`:

```csharp
builder.Services.AddDbContextPool<MyDbContext>(options => options.UseNpgsql(newConnectionString));
builder.EnrichAzureNpgsqlDbContext<MyDbContext>();
```

## Additional documentation

* https://learn.microsoft.com/ef/core/
* https://github.com/microsoft/aspire/tree/main/src/Components/README.md

## Feedback & contributing

https://github.com/microsoft/aspire

_*Postgres, PostgreSQL and the Slonik Logo are trademarks or registered trademarks of the PostgreSQL Community Association of Canada, and used with their permission._
