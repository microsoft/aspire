# Aspire.Hosting.MySql library

Provides extension methods and resource definitions for an Aspire AppHost to configure a MySQL resource.

## Getting started

### Install the package

In your AppHost project, install the Aspire MySQL Hosting library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.MySql
```

## Usage example

Then, in the _AppHost.cs_ file of `AppHost`, add a MySQL resource and consume the connection using the following methods:

```csharp
var db = builder.AddMySql("mysql").AddDatabase("mydb");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(db);
```

## Connection Properties

When you reference a MySQL resource using `WithReference`, the following connection properties are made available to the consuming project:

### MySQL server

The MySQL server resource exposes the following connection properties:

| Property Name | Description |
|---------------|-------------|
| `Host` | The hostname or IP address of the MySQL server |
| `Port` | The port number the MySQL server is listening on |
| `Username` | The username for authentication |
| `Password` | The password for authentication |
| `Uri` | The connection URI, with the format `mysql://root:{Password}@{Host}:{Port}` |
| `JdbcConnectionString` | The JDBC connection string for MySQL, with the format `jdbc:mysql://{Host}:{Port}`. User and password credentials are provided as separate `Username` and `Password` properties. |

### MySQL database

The MySQL database resource combines the server properties above and adds the following connection properties:

| Property Name | Description |
|---------------|-------------|
| `DatabaseName` | The MySQL database name |
| `Uri` | The database-specific URI, with the format `mysql://root:{Password}@{Host}:{Port}/{DatabaseName}` |
| `JdbcConnectionString` | The database-specific JDBC connection string, with the format `jdbc:mysql://{Host}:{Port}/{DatabaseName}`. User and password credentials are provided as separate `Username` and `Password` properties. |

Aspire exposes each property as an environment variable named `[RESOURCE]_[PROPERTY]`. For instance, the `Uri` property of a resource called `db1` becomes `DB1_URI`.

## Additional documentation

* https://learn.microsoft.com/dotnet/aspire/database/mysql-component

## Feedback & contributing

https://github.com/dotnet/aspire
