# Aspire.Oracle.EntityFrameworkCore library

Registers [EntityFrameworkCore](https://learn.microsoft.com/ef/core/) [DbContext](https://learn.microsoft.com/dotnet/api/microsoft.entityframeworkcore.dbcontext) service for connecting Oracle database. Enables connection pooling, retries, health check, logging and telemetry.

## Getting started

### Prerequisites

- Oracle database and connection string for accessing the database.

### Install the package

Install the Aspire Oracle EntityFrameworkCore library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Oracle.EntityFrameworkCore
```

## Usage example

In the _AppHost.cs_ file of your project, call the `AddOracleDatabaseDbContext` extension method to register a `DbContext` for use via the dependency injection container. The method takes a connection name parameter.

```csharp
builder.AddOracleDatabaseDbContext<MyDbContext>("orcl");
```

You can then retrieve the `MyDbContext` instance using dependency injection. For example, to retrieve the context from a Web API controller:

```csharp
private readonly MyDbContext _context;

public ProductsController(MyDbContext context)
{
    _context = context;
}
```

You might also need to configure specific option of Oracle database, or register a `DbContext` in other ways. In this case call the `EnrichOracleDatabaseDbContext` extension method, for example:

```csharp
var connectionString = builder.Configuration.GetConnectionString("catalogdb");
builder.Services.AddDbContextPool<CatalogDbContext>(dbContextOptionsBuilder => dbContextOptionsBuilder.UseOracle(connectionString));
builder.EnrichOracleDatabaseDbContext<CatalogDbContext>();
```

## Configuration

The Aspire Oracle EntityFrameworkCore component provides multiple options to configure the database connection based on the requirements and conventions of your project.

### Use a connection string

When using a connection string from the `ConnectionStrings` configuration section, you can provide the name of the connection string when calling `builder.AddOracleDatabaseDbContext<TContext>()`:

```csharp
builder.AddOracleDatabaseDbContext<MyDbContext>("myConnection");
```

And then the connection string will be retrieved from the `ConnectionStrings` configuration section:

```json
{
  "ConnectionStrings": {
    "myConnection": "Data Source=TORCL;User Id=myUsername;Password=myPassword;"
  }
}
```

The `EnrichOracleDatabaseDbContext` won't make use of the `ConnectionStrings` configuration section since it expects a `DbContext` to be registered at the point it is called.

See the [ODP.NET documentation](https://www.oracle.com/database/technologies/appdev/dotnet/odp.html) for more information on how to format this connection string.

### Use configuration providers

The Aspire Oracle EntityFrameworkCore component supports [Microsoft.Extensions.Configuration](https://learn.microsoft.com/dotnet/api/microsoft.extensions.configuration). It loads the `OracleEntityFrameworkCoreSettings` from configuration by using the `Aspire:Oracle:EntityFrameworkCore` key. Example `appsettings.json` that configures some of the options:

```json
{
  "Aspire": {
    "Oracle": {
      "EntityFrameworkCore": {
        "DisableHealthChecks": true,
        "DisableTracing": true
      }
    }
  }
}
```

### Use inline delegates

Also you can pass the `Action<OracleEntityFrameworkCoreSettings> configureSettings` delegate to set up some or all the options inline, for example to disable health checks from code:

```csharp
    builder.AddOracleDatabaseDbContext<MyDbContext>("orcl", settings => settings.DisableHealthChecks = true);
```

or

```csharp
    builder.EnrichOracleDatabaseDbContext<MyDbContext>(settings => settings.DisableHealthChecks = true);
```

## AppHost extensions 

In your AppHost project, install the `Aspire.Hosting.Oracle` library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Oracle
```

Then, in the _AppHost.cs_ file of `AppHost`, register an Oracle container and consume the connection using the following methods: 
  
 ```csharp 
 var freepdb1 = builder.AddOracle("oracle").AddDatabase("freepdb1");
  
 var myService = builder.AddProject<Projects.MyService>() 
                        .WithReference(freepdb1); 
 ``` 

The `WithReference` method configures a connection in the `MyService` project named `freepdb1`. In the _Program.cs_ file of `MyService`, the database connection can be consumed using:

```csharp
builder.AddOracleDatabaseDbContext<MyDbContext>("freepdb1");
```

## Additional documentation

* https://learn.microsoft.com/ef/core/
* https://github.com/microsoft/aspire/tree/main/src/Components/README.md

## Feedback & contributing

https://github.com/microsoft/aspire
