# Aspire.Npgsql.EntityFrameworkCore.PostgreSQL library

Registers [EntityFrameworkCore](https://learn.microsoft.com/ef/core/) [DbContext](https://learn.microsoft.com/dotnet/api/microsoft.entityframeworkcore.dbcontext) in the DI container for connecting PostgreSQL®* database. Enables connection pooling, retries, health check, logging and telemetry.

## Getting started

### Prerequisites

- PostgreSQL database and connection string for accessing the database.

### Install the package

Install the Aspire PostgreSQL EntityFrameworkCore Npgsql library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Npgsql.EntityFrameworkCore.PostgreSQL
```

## Usage example

In the _AppHost.cs_ file of your project, call the `AddNpgsqlDbContext` extension method to register a `DbContext` for use via the dependency injection container. The method takes a connection name parameter.

```csharp
builder.AddNpgsqlDbContext<MyDbContext>("postgresdb");
```

You can then retrieve the `MyDbContext` instance using dependency injection. For example, to retrieve the context from a Web API controller:

```csharp
private readonly MyDbContext _context;

public ProductsController(MyDbContext context)
{
    _context = context;
}
```

You might also need to configure specific option of Npgsql, or register a `DbContext` in other ways. In this case call the `EnrichNpgsqlDbContext` extension method, for example:

```csharp
var connectionString = builder.Configuration.GetConnectionString("postgresdb");
builder.Services.AddDbContextPool<MyDbContext>(dbContextOptionsBuilder => dbContextOptionsBuilder.UseNpgsql(connectionString));
builder.EnrichNpgsqlDbContext<MyDbContext>();
```

## Configuration

The Aspire PostgreSQL EntityFrameworkCore Npgsql component provides multiple options to configure the database connection based on the requirements and conventions of your project.

### Use a connection string

When using a connection string from the `ConnectionStrings` configuration section, you can provide the name of the connection string when calling `builder.AddNpgsqlDbContext()`:

```csharp
builder.AddNpgsqlDbContext<MyDbContext>("myConnection");
```

And then the connection string will be retrieved from the `ConnectionStrings` configuration section:

```json
{
  "ConnectionStrings": {
    "myConnection": "Host=myserver;Database=test"
  }
}
```

The `EnrichNpgsqlDbContext` won't make use of the `ConnectionStrings` configuration section since it expects a `DbContext` to be registered at the point it is called.

See the [ConnectionString documentation](https://www.npgsql.org/doc/connection-string-parameters.html) for more information on how to format this connection string.

### Use configuration providers

The Aspire PostgreSQL EntityFrameworkCore Npgsql component supports [Microsoft.Extensions.Configuration](https://learn.microsoft.com/dotnet/api/microsoft.extensions.configuration). It loads the `NpgsqlEntityFrameworkCorePostgreSQLSettings` from configuration by using the `Aspire:Npgsql:EntityFrameworkCore:PostgreSQL` key. Example `appsettings.json` that configures some of the options:

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

Also you can pass the `Action<NpgsqlEntityFrameworkCorePostgreSQLSettings> configureSettings` delegate to set up some or all the options inline, for example to disable health checks from code:

```csharp
    builder.AddNpgsqlDbContext<MyDbContext>("postgresdb", settings => settings.DisableHealthChecks = true);
```

or

```csharp
    builder.EnrichNpgsqlDbContext<MyDbContext>(settings => settings.DisableHealthChecks = true);
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
builder.AddNpgsqlDbContext<MyDbContext>("postgresdb");
```

## Additional documentation

* https://learn.microsoft.com/ef/core/
* https://github.com/microsoft/aspire/tree/main/src/Components/README.md

## Feedback & contributing

https://github.com/microsoft/aspire

_*Postgres, PostgreSQL and the Slonik Logo are trademarks or registered trademarks of the PostgreSQL Community Association of Canada, and used with their permission._
