# Aspire.MongoDB.Driver library

Registers [IMongoClient](https://www.mongodb.com/docs/drivers/csharp/current/quick-start/#add-mongodb-as-a-dependency) in the DI container for connecting MongoDB database.

## Getting started

### Prerequisites

- MongoDB database and connection string for accessing the database.

### Install the package

Install the Aspire MongoDB.Driver library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.MongoDB.Driver
```

## Usage example

In the _AppHost.cs_ file of your project, call the `AddMongoDBClient` extension method to register a `IMongoClient` for use via the dependency injection container. The method takes a connection name parameter.

```csharp
builder.AddMongoDBClient("mongodb");
```

You can then retrieve a `IMongoClient` instance using dependency injection. For example, to retrieve a connection from a Web API controller:

```csharp
private readonly IMongoClient _client;

public ProductsController(IMongoClient client)
{
    _client = client;
}
```

## Configuration

The Aspire MongoDB component provides multiple options to configure the database connection based on the requirements and conventions of your project.

### Use a connection string

When using a connection string from the `ConnectionStrings` configuration section, you can provide the name of the connection string when calling `builder.AddMongoDBClient()`:

```csharp
builder.AddMongoDBClient("myConnection");
```

And then the connection string will be retrieved from the `ConnectionStrings` configuration section:

```json
{
  "ConnectionStrings": {
    "myConnection": "mongodb://server:port/test",
  }
}
```

See the [ConnectionString documentation](https://www.mongodb.com/docs/v3.0/reference/connection-string/) for more information on how to format this connection string.

### Use configuration providers

The Aspire MongoDB component supports [Microsoft.Extensions.Configuration](https://learn.microsoft.com/dotnet/api/microsoft.extensions.configuration). It loads the `MongoDBSettings` from configuration by using the `Aspire:MongoDB:Driver` key. Example `appsettings.json` that configures some of the options:

```json
{
  "Aspire": {
    "MongoDB": {
      "Driver": {
        "ConnectionString": "mongodb://server:port/test",
        "DisableHealthChecks": false,
        "HealthCheckTimeout": 10000,
        "DisableTracing": false
      },
    }
  }
}
```

### Use inline delegates

Also you can pass the `Action<MongoDBSettings> configureSettings` delegate to set up some or all the options inline:

```csharp
    builder.AddMongoDBClient("mongodb", settings => settings.ConnectionString = "mongodb://server:port/test");
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

The `WithReference` method configures a connection in the `MyService` project named `mongodb`. In the _Program.cs_ file of `MyService`, the database connection can be consumed using:

```csharp
builder.AddMongoDBClient("mongodb");
```

## Additional documentation

* https://www.mongodb.com/docs/drivers/csharp/current/quick-start/
* https://github.com/microsoft/aspire/tree/main/src/Components/README.md

## Feedback & contributing

https://github.com/microsoft/aspire
