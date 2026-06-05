# Aspire.Hosting.Oracle library

Provides extension methods and resource definitions for an Aspire AppHost to configure an Oracle database resource.

## Getting started

### Install the package

In your AppHost project, install the Aspire PostgreSQL Oracle library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Oracle
```

## Usage example

Then, in the _AppHost.cs_ file of `AppHost`, add a Oracle database resource and consume the connection using the following methods:

```csharp
var db = builder.AddOracle("oracle").AddDatabase("mydb");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(db);
```

## Connection Properties

When you reference an Oracle database resource using `WithReference`, the following connection properties are made available to the consuming project:

### Oracle database server

The Oracle database server resource exposes the following connection properties:

| Property Name | Description |
|---------------|-------------|
| `Host` | The hostname or IP address of the Oracle server |
| `Port` | The port number the Oracle server is listening on |
| `Username` | The username for authentication |
| `Password` | The password for authentication |
| `Uri` | The connection URI in oracle:// format, with the format `oracle://{Username}:{Password}@{Host}:{Port}` |
| `JdbcConnectionString` | JDBC-format connection string, with the format `jdbc:oracle:thin:@//{Host}:{Port}`. User and password credentials are provided as separate `Username` and `Password` properties. |

### Oracle database

The Oracle database resource inherits all properties from its parent `OracleDatabaseServerResource` and adds:

| Property Name | Description |
|---------------|-------------|
| `Uri` | The connection URI in oracle:// format, with the format `oracle://{Username}:{Password}@{Host}:{Port}/{DatabaseName}` |
| `JdbcConnectionString` | JDBC connection string with database name, with the format `jdbc:oracle:thin:@//{Host}:{Port}/{DatabaseName}`. User and password credentials are provided as separate `Username` and `Password` properties. |
| `DatabaseName` | The name of the database |

Aspire exposes each property as an environment variable named `[RESOURCE]_[PROPERTY]`. For instance, the `Uri` property of a resource called `db1` becomes `DB1_URI`.

## Additional documentation

https://learn.microsoft.com/dotnet/aspire/database/oracle-entity-framework-component?tabs=dotnet-cli

## Feedback & contributing

https://github.com/dotnet/aspire
