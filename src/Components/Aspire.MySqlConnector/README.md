# Aspire.MySqlConnector library

Registers [MySqlDataSource](https://mysqlconnector.net/api/mysqlconnector/mysqldatasourcetype/) in the DI container for connecting MySQL database. Enables corresponding health check, metrics, logging and telemetry.

## Getting started

### Prerequisites

- MySQL database and connection string for accessing the database.

### Install the package

Install the Aspire MySQL library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.MySqlConnector
```

## Usage example

In the _AppHost.cs_ file of your project, call the `AddMyDataSource` extension method to register a `MySqlDataSource` for use via the dependency injection container. The method takes a connection name parameter.

```csharp
builder.AddMySqlDataSource("server=mysql;user id=myuser;password=mypass");
```

You can then retrieve a `MySqlConnection` instance using dependency injection. For example, to retrieve a connection from a Web API controller:

```csharp
private readonly MySqlConnection _connection;

public ProductsController(MySqlConnection connection)
{
    _connection = connection;
}
```

## Configuration

The Aspire MySQL component provides multiple options to configure the database connection based on the requirements and conventions of your project.

### Use a connection string

When using a connection string from the `ConnectionStrings` configuration section, you can provide the name of the connection string when calling `builder.AddMySqlDataSource()`:

```csharp
builder.AddMySqlDataSource("myConnection");
```

And then the connection string will be retrieved from the `ConnectionStrings` configuration section:

```json
{
  "ConnectionStrings": {
    "myConnection": "Server=mysql;Database=test"
  }
}
```

See the [ConnectionString documentation](https://mysqlconnector.net/connection-options/) for more information on how to format this connection string.

### Use configuration providers

The Aspire MySQL component supports [Microsoft.Extensions.Configuration](https://learn.microsoft.com/dotnet/api/microsoft.extensions.configuration). It loads the `MySqlConnectorSettings` from configuration by using the `Aspire:MySqlConnector` key. Example `appsettings.json` that configures some of the options:

```json
{
  "Aspire": {
    "MySqlConnector": {
      "DisableHealthChecks": true,
      "DisableTracing": true
    }
  }
}
```

### Use inline delegates

Also you can pass the `Action<MySqlConnectorSettings> configureSettings` delegate to set up some or all the options inline, for example to disable health checks from code:

```csharp
builder.AddMySqlDataSource("mysql", settings => settings.DisableHealthChecks = true);
```

## AppHost extensions

In your AppHost project, install the `Aspire.Hosting.MySql` library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.MySql
```

Then, in the _AppHost.cs_ file of `AppHost`, register a MySQL database and consume the connection using the following methods:

```csharp
var mysqldb = builder.AddMySql("mysql").AddDatabase("mysqldb");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(mysqldb);
```

The `WithReference` method configures a connection in the `MyService` project named `mysqldb`. In the _Program.cs_ file of `MyService`, the database connection can be consumed using:

```csharp
builder.AddMySqlDataSource("mysqldb");
```

## Additional documentation

* https://mysqlconnector.net/tutorials/basic-api/
* https://github.com/microsoft/aspire/tree/main/src/Components/README.md

## Feedback & contributing

https://github.com/microsoft/aspire
