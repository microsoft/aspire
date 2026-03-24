# Aspire.Microsoft.Data.SqlClient library

Registers 'Scoped' [Microsoft.Data.SqlClient.SqlConnection](https://learn.microsoft.com/dotnet/api/microsoft.data.sqlclient.sqlconnection) factory in the DI container for connecting Azure SQL, MS SQL database. Enables health check and telemetry.

## Getting started

### Prerequisites

- Azure SQL or MS SQL server database and the connection string for accessing the database.

### Install the package

Install the Aspire SQL Server SqlClient library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Microsoft.Data.SqlClient
```

## Usage example

In the _AppHost.cs_ file of your project, call the `AddSqlServerClient` extension method to register a `SqlConnection` for use via the dependency injection container. The method takes a connection name parameter.

```csharp
builder.AddSqlServerClient("sqldata");
```

You can then retrieve the `SqlConnection` instance using dependency injection. For example, to retrieve the connection from a Web API controller:

```csharp
private readonly SqlConnection _connection;

public ProductsController(SqlConnection connection)
{
    _connection = connection;
}
```

## Configuration

The Aspire SqlClient component provides multiple options to configure the SQL connection based on the requirements and conventions of your project.

### Use a connection string

When using a connection string from the `ConnectionStrings` configuration section, you can provide the name of the connection string when calling `builder.AddSqlServerClient()`:

```csharp
builder.AddSqlServerClient("myConnection");
```

And then the connection string will be retrieved from the `ConnectionStrings` configuration section:

```json
{
  "ConnectionStrings": {
    "myConnection": "Data Source=myserver;Initial Catalog=master"
  }
}
```

See the [ConnectionString documentation](https://learn.microsoft.com/dotnet/api/system.data.sqlclient.sqlconnection.connectionstring#remarks) for more information on how to format this connection string.

### Use configuration providers

The Aspire SqlClient component supports [Microsoft.Extensions.Configuration](https://learn.microsoft.com/dotnet/api/microsoft.extensions.configuration). It loads the `MicrosoftDataSqlClientSettings` from configuration by using the `Aspire:Microsoft:Data:SqlClient` key. Example `appsettings.json` that configures some of the options:

```json
{
  "Aspire": {
    "Microsoft": {
      "Data": {
        "SqlClient": {
          "DisableHealthChecks": false
        }
      }
    }
  }
}
```

### Use inline delegates

Also you can pass the `Action<MicrosoftDataSqlClientSettings> configureSettings` delegate to set up some or all the options inline, for example to disable health checks from code:

```csharp
builder.AddSqlServerClient("sqldata", settings => settings.DisableHealthChecks = true);
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
builder.AddSqlServerClient("sqldata");
```

## Additional documentation

* https://learn.microsoft.com/dotnet/framework/data/adonet/sql/
* https://learn.microsoft.com/dotnet/api/system.data.sqlclient.sqlconnection
* https://github.com/microsoft/aspire/tree/main/src/Components/README.md

## Feedback & contributing

https://github.com/microsoft/aspire
