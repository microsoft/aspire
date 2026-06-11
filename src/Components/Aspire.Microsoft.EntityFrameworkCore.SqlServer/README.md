# Aspire.Microsoft.EntityFrameworkCore.SqlServer library

Registers [EntityFrameworkCore](https://learn.microsoft.com/ef/core/) [DbContext](https://learn.microsoft.com/dotnet/api/microsoft.entityframeworkcore.dbcontext) service for connecting Azure SQL, MS SQL server database. Enables connection pooling, retries, health check, logging and telemetry.

## Getting started

### Prerequisites

- Azure SQL or MS SQL server database and connection string for accessing the database.

### Install the package

Install the Aspire SQL Server EntityFrameworkCore SqlClient library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Microsoft.EntityFrameworkCore.SqlServer
```

## Usage example

In the _AppHost.cs_ file of your project, call the `AddSqlServerDbContext` extension method to register a `DbContext` for use via the dependency injection container. The method takes a connection name parameter.

```csharp
builder.AddSqlServerDbContext<MyDbContext>("sqldata");
```

You can then retrieve the `MyDbContext` instance using dependency injection. For example, to retrieve the context from a Web API controller:

```csharp
private readonly MyDbContext _context;

public ProductsController(MyDbContext context)
{
    _context = context;
}
```

You might also need to configure specific option of Sql Server, or register a `DbContext` in other ways. In this case call the `EnrichSqlServerDbContext` extension method, for example:

```csharp
var connectionString = builder.Configuration.GetConnectionString("catalogdb");
builder.Services.AddDbContextPool<CatalogDbContext>(dbContextOptionsBuilder => dbContextOptionsBuilder.UseSqlServer(connectionString));
builder.EnrichSqlServerDbContext<CatalogDbContext>();
```

## Configuration

The Aspire SQL Server EntityFrameworkCore SqlClient component provides multiple options to configure the SQL connection based on the requirements and conventions of your project.

### Use a connection string

When using a connection string from the `ConnectionStrings` configuration section, you can provide the name of the connection string when calling `builder.AddSqlServerDbContext<TContext>()`:

```csharp
builder.AddSqlServerDbContext<MyDbContext>("myConnection");
```

And then the connection string will be retrieved from the `ConnectionStrings` configuration section:

```json
{
  "ConnectionStrings": {
    "myConnection": "Data Source=myserver;Initial Catalog=master"
  }
}
```

The `EnrichSqlServerDbContext` won't make use of the `ConnectionStrings` configuration section since it expects a `DbContext` to be registered at the point it is called.

See the [ConnectionString documentation](https://learn.microsoft.com/dotnet/api/system.data.sqlclient.sqlconnection.connectionstring#remarks) for more information on how to format this connection string.

### Use configuration providers

The Aspire SQL Server EntityFrameworkCore SqlClient component supports [Microsoft.Extensions.Configuration](https://learn.microsoft.com/dotnet/api/microsoft.extensions.configuration). It loads the `MicrosoftEntityFrameworkCoreSqlServerSettings` from configuration by using the `Aspire:Microsoft:EntityFrameworkCore:SqlServer` key. Example `appsettings.json` that configures some of the options:

```json
{
  "Aspire": {
    "Microsoft": {
      "EntityFrameworkCore": {
        "SqlServer": {
          "DisableHealthChecks": true,
          "DisableTracing": true
        }
      }
    }
  }
}
```

### Use inline delegates

Also you can pass the `Action<MicrosoftEntityFrameworkCoreSqlServerSettings> configureSettings` delegate to set up some or all the options inline, for example to disable health checks from code:

```csharp
    builder.AddSqlServerDbContext<MyDbContext>("sqldata", settings => settings.DisableHealthChecks = true);
```

or

```csharp
    builder.EnrichSqlServerDbContext<MyDbContext>(settings => settings.DisableHealthChecks = true);
```

## AppHost extensions

In your AppHost project, install the `Aspire.Hosting.SqlServer` library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.SqlServer
```

Then, in the _AppHost.cs_ file of `AppHost`, register a SqlServer database and consume the connection using the following methods:

```csharp
var sql = builder.AddSqlServer("sql").AddDatabase("sqldata");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(sql);
```

The `WithReference` method configures a connection in the `MyService` project named `sqldata`. In the _Program.cs_ file of `MyService`, the sql connection can be consumed using:

```csharp
builder.AddSqlServerDbContext<MyDbContext>("sqldata");
```

## Additional documentation

* https://learn.microsoft.com/ef/core/
* https://github.com/microsoft/aspire/tree/main/src/Components/README.md

## Feedback & contributing

https://github.com/microsoft/aspire
