# Aspire.Npgsql library

Registers [NpgsqlDataSource](https://www.npgsql.org/doc/api/Npgsql.NpgsqlDataSource.html) in the DI container for connecting PostgreSQL®* database. Enables corresponding health check, metrics, logging and telemetry.

## Getting started

### Prerequisites

- PostgreSQL database and connection string for accessing the database.

### Install the package

Install the Aspire PostgreSQL Npgsql library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Npgsql
```

## Usage example

In the _AppHost.cs_ file of your project, call the `AddNpgsqlDataSource` extension method to register a `NpgsqlDataSource` for use via the dependency injection container. The method takes a connection name parameter.

```csharp
builder.AddNpgsqlDataSource("postgresdb");
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

The Aspire PostgreSQL Npgsql component provides multiple options to configure the database connection based on the requirements and conventions of your project.

### Use a connection string

When using a connection string from the `ConnectionStrings` configuration section, you can provide the name of the connection string when calling `builder.AddNpgsqlDataSource()`:

```csharp
builder.AddNpgsqlDataSource("myConnection");
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

### Use configuration providers

The Aspire PostgreSQL Npgsql component supports [Microsoft.Extensions.Configuration](https://learn.microsoft.com/dotnet/api/microsoft.extensions.configuration). It loads the `NpgsqlSettings` from configuration by using the `Aspire:Npgsql` key. Example `appsettings.json` that configures some of the options:

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

Also you can pass the `Action<NpgsqlSettings> configureSettings` delegate to set up some or all the options inline, for example to disable health checks from code:

```csharp
    builder.AddNpgsqlDataSource("postgresdb", settings => settings.DisableHealthChecks = true);
```

## AppHost extensions

In your AppHost project, install the `Aspire.Hosting.PostgreSQL` library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.PostgreSQL
```

Then, in the _AppHost.cs_ file of `AppHost`, register a Postgres database and consume the connection using the following methods:

```csharp
var postgresdb = builder.AddPostgres("pg").AddDatabase("postgresdb");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(postgresdb);
```

The `WithReference` method configures a connection in the `MyService` project named `postgresdb`. In the _Program.cs_ file of `MyService`, the database connection can be consumed using:

```csharp
builder.AddNpgsqlDataSource("postgresdb");
```

## Additional documentation

* https://www.npgsql.org/doc/basic-usage.html
* https://github.com/microsoft/aspire/tree/main/src/Components/README.md

## Feedback & contributing

https://github.com/microsoft/aspire

_*Postgres, PostgreSQL and the Slonik Logo are trademarks or registered trademarks of the PostgreSQL Community Association of Canada, and used with their permission._
