# Aspire.Hosting.PostgreSQL library

Provides extension methods and resource definitions for an Aspire AppHost to configure a PostgreSQL resource.

## Getting started

### Install the package

In your AppHost project, install the Aspire PostgreSQL Hosting library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.PostgreSQL
```

## Usage example

Then, in the _AppHost.cs_ file of `AppHost`, add a PostgreSQL resource and consume the connection using the following methods:

```csharp
var db = builder.AddPostgres("pgsql").AddDatabase("mydb");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(db);
```

## Connection Properties

When you reference a PostgreSQL resource using `WithReference`, the following connection properties are made available to the consuming project:

### PostgreSQL server

The PostgreSQL server resource exposes the following connection properties:

| Property Name | Description |
|---------------|-------------|
| `Host` | The hostname or IP address of the PostgreSQL server |
| `Port` | The port number the PostgreSQL server is listening on |
| `Username` | The username for authentication |
| `Password` | The password for authentication |
| `Uri` | The connection URI in postgresql:// format, with the format `postgresql://{Username}:{Password}@{Host}:{Port}` |
| `JdbcConnectionString` | JDBC-format connection string, with the format `jdbc:postgresql://{Host}:{Port}`. User and password credentials are provided as separate `Username` and `Password` properties. |

### PostgreSQL database

The PostgreSQL database resource inherits all properties from its parent `PostgresServerResource` and adds:

| Property Name | Description |
|---------------|-------------|
| `Uri` | The connection URI with the database name, with the format `postgresql://{Username}:{Password}@{Host}:{Port}/{DatabaseName}` |
| `JdbcConnectionString` | JDBC connection string with database name, with the format `jdbc:postgresql://{Host}:{Port}/{DatabaseName}`. User and password credentials are provided as separate `Username` and `Password` properties. |
| `DatabaseName` | The name of the database |

Aspire exposes each property as an environment variable named `[RESOURCE]_[PROPERTY]`. For instance, the `Uri` property of a resource called `db1` becomes `DB1_URI`.

## MCP (Model Context Protocol) Support

The PostgreSQL hosting integration provides support for adding an MCP sidecar container that enables AI agents to interact with PostgreSQL databases. This is enabled by calling `WithPostgresMcp()` on a PostgreSQL database resource.

```csharp
var db = builder.AddPostgres("pg")
                .AddDatabase("mydb")
                .WithPostgresMcp();
```

The PostgreSQL MCP server is currently powered by [Postgres MCP Pro](https://github.com/crystaldba/postgres-mcp)) and provides tools
for database exploration, query execution, index tuning, and health checks.

## Additional documentation

https://aspire.dev/integrations/databases/postgres/postgres-get-started/
https://aspire.dev/integrations/databases/efcore/postgresql/

## Feedback & contributing

https://github.com/microsoft/aspire

_*Postgres, PostgreSQL and the Slonik Logo are trademarks or registered trademarks of the PostgreSQL Community Association of Canada, and used with their permission._
