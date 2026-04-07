# Aspire.Hosting.SqlServer library

Provides extension methods and resource definitions for an Aspire AppHost to configure a SQL Server database resource.

## Getting started

### Install the package

In your AppHost project, install the Aspire SQL Server Hosting library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.SqlServer
```

## Usage example

Then, in the _AppHost.cs_ file of `AppHost`, add a SQL Server resource and consume the connection using the following methods:

```csharp
var db = builder.AddSqlServer("sql").AddDatabase("db")

var myService = builder.AddProject<Projects.MyService>()
   .WithReference(db);
```

## Connection Properties

When you reference a SQL Server resource using `WithReference`, the following connection properties are made available to the consuming project:

### SQL Server server

The SQL Server server resource exposes the following connection properties:

| Property Name | Description |
|---------------|-------------|
| `Host` | The hostname or IP address of the SQL Server |
| `Port` | The port number the SQL Server is listening on |
| `Username` | The username for authentication |
| `Password` | The password for authentication |
| `Uri` | The connection URI in mssql:// format, with the format `mssql://{Username}:{Password}@{Host}:{Port}` |
| `JdbcConnectionString` | JDBC-format connection string, with the format `jdbc:sqlserver://{Host}:{Port};trustServerCertificate=true`. User and password credentials are provided as separate `Username` and `Password` properties. |

### SQL Server database

The SQL Server database resource inherits all properties from its parent `SqlServerServerResource` and adds:

| Property Name | Description |
|---------------|-------------|
| `Uri` | The connection URI in mssql:// format, with the format `mssql://{Username}:{Password}@{Host}:{Port}/{DatabaseName}` |
| `JdbcConnectionString` | JDBC connection string with database name, with the format `jdbc:sqlserver://{Host}:{Port};trustServerCertificate=true;databaseName={DatabaseName}`. User and password credentials are provided as separate `Username` and `Password` properties. |
| `DatabaseName` | The name of the database |

Aspire exposes each property as an environment variable named `[RESOURCE]_[PROPERTY]`. For instance, the `Uri` property of a resource called `db1` becomes `DB1_URI`.

## Additional documentation
https://aspire.dev/integrations/databases/sql-server/
https://aspire.dev/integrations/databases/efcore/sql-server/

## Feedback & contributing

https://github.com/microsoft/aspire
