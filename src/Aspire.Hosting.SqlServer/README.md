# SQL Server hosting integration

Use this integration to model, configure, and orchestrate a SQL Server database resource in an Aspire solution.

## Getting started

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.SqlServer` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.SqlServer
```

## Usage example

In the AppHost, add a SQL Server resource and reference it from another resource with either C# or TypeScript:

**C#**

```csharp
var db = builder.AddSqlServer("sql").AddDatabase("db");

var myService = builder.AddProject<Projects.MyService>()
   .WithReference(db);
```

**TypeScript**

```typescript
const db = await builder.addSqlServer("sql").addDatabase("db");

const myService = await builder.addNodeApp("myService", "../my-service", "server.js")
   .withReference(db);
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

https://aspire.dev/integrations/gallery/
https://aspire.dev/integrations/databases/sql-server/sql-server-host/

## Feedback & contributing

https://github.com/microsoft/aspire
