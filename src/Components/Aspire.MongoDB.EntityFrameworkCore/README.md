# Aspire.MongoDB.EntityFrameworkCore library

Registers [EntityFrameworkCore](https://learn.microsoft.com/ef/core/) [DbContext](https://learn.microsoft.com/dotnet/api/microsoft.entityframeworkcore.dbcontext) in the DI container for connecting to a MongoDB database. Enables connection pooling, health check, logging and telemetry.

## Getting started

### Prerequisites

- MongoDB database and connection string for accessing the database.

### Install the package

Install the Aspire MongoDB EntityFrameworkCore library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.MongoDB.EntityFrameworkCore
```

## Usage example

In the _Program.cs_ file of your project, call the `AddMongoDbContext` extension method to register a `DbContext` for use via the dependency injection container. The method takes a connection name parameter, and optionally a database name.

```csharp
builder.AddMongoDbContext<MyDbContext>("mongodb", "mydb");
```

The database name can be omitted if it's included in the connection string or configured via settings:

```csharp
builder.AddMongoDbContext<MyDbContext>("mongodb");
```

You can then retrieve the `MyDbContext` instance using dependency injection. For example, to retrieve the context from a Web API controller:

```csharp
private readonly MyDbContext _context;

public ProductsController(MyDbContext context)
{
    _context = context;
}
```

You might also need to configure specific options of MongoDB, or register a `DbContext` in other ways. In this case call the `EnrichMongoDbContext` extension method, for example:

```csharp
var connectionString = builder.Configuration.GetConnectionString("mongodb");
builder.Services.AddDbContextPool<MyDbContext>(dbContextOptionsBuilder => dbContextOptionsBuilder.UseMongoDB(connectionString, "mydb"));
builder.EnrichMongoDbContext<MyDbContext>();
```

## Configuration

The Aspire MongoDB EntityFrameworkCore component provides multiple options to configure the database connection based on the requirements and conventions of your project.

### Use a connection string

When using a connection string from the `ConnectionStrings` configuration section, you can provide the name of the connection string when calling `builder.AddMongoDbContext()`:

```csharp
builder.AddMongoDbContext<MyDbContext>("myConnection", "mydb");
```

And then the connection string will be retrieved from the `ConnectionStrings` configuration section:

```json
{
  "ConnectionStrings": {
    "myConnection": "mongodb://server:port"
  }
}
```

If your connection string includes the database name, you can omit the `databaseName` parameter:

```json
{
  "ConnectionStrings": {
    "myConnection": "mongodb://server:port/mydb"
  }
}
```

```csharp
builder.AddMongoDbContext<MyDbContext>("myConnection");
```

The `EnrichMongoDbContext` won't make use of the `ConnectionStrings` configuration section since it expects a `DbContext` to be registered at the point it is called.

See the [ConnectionString documentation](https://www.mongodb.com/docs/v3.0/reference/connection-string/) for more information on how to format this connection string.

### Use configuration providers

The Aspire MongoDB EntityFrameworkCore component supports [Microsoft.Extensions.Configuration](https://learn.microsoft.com/dotnet/api/microsoft.extensions.configuration). It loads the `MongoDBEntityFrameworkCoreSettings` from configuration by using the `Aspire:MongoDB:EntityFrameworkCore` key. Example `appsettings.json` that configures some of the options:

```json
{
  "Aspire": {
    "MongoDB": {
      "EntityFrameworkCore": {
        "DatabaseName": "mydb",
        "DisableHealthChecks": true,
        "DisableTracing": false
      }
    }
  }
}
```

### Use inline delegates

Also you can pass the `Action<MongoDBEntityFrameworkCoreSettings> configureSettings` delegate to set up some or all the options inline, for example to disable health checks from code:

```csharp
builder.AddMongoDbContext<MyDbContext>("mongodb", "mydb", settings => settings.DisableHealthChecks = true);
```

or

```csharp
builder.EnrichMongoDbContext<MyDbContext>(settings => settings.DisableHealthChecks = true);
```

## AppHost extensions

In your AppHost project, install the `Aspire.Hosting.MongoDB` library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.MongoDB
```

Then, in the _AppHost.cs_ file of `AppHost`, register a MongoDB database and consume the connection using the following methods:

```csharp
var mongodb = builder.AddMongoDB("mongodb").AddDatabase("mydatabase");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(mongodb);
```

The `WithReference` method configures a connection in the `MyService` project named `mydatabase`. In the _Program.cs_ file of `MyService`, the database connection can be consumed using:

```csharp
builder.AddMongoDbContext<MyDbContext>("mydatabase");
```

Note: When using `AddDatabase` in the AppHost, the database name is included in the generated connection string, so you don't need to specify it again in `AddMongoDbContext`.

## Additional documentation

* https://learn.microsoft.com/ef/core/
* https://www.mongodb.com/docs/entity-framework/current/
* https://github.com/microsoft/aspire/tree/main/src/Components/README.md

## Feedback & contributing

https://github.com/microsoft/aspire
