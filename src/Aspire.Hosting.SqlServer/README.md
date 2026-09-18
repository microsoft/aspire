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

## SQL REPL

While the SQL Server container is running, its **REPL** command opens `sqlcmd` in the terminal dock. It connects inside the container as `sa` to the `master` database, using the configured password without putting it in command-line arguments. No local SQL client installation is required.

Enter SQL statements followed by `GO` on its own line to execute a batch:

```sql
SELECT DB_NAME();
GO
```

Use `USE [db];` followed by `GO` to switch databases, and `QUIT` to exit. The command trusts the local server's self-signed certificate, matching the integration's local connection string.

The REPL supports both `/opt/mssql-tools18/bin/sqlcmd` in newer SQL Server images and `/opt/mssql-tools/bin/sqlcmd` in older images. Custom images must include one of these clients. This command is available only in run mode and uses the configured Docker or Podman runtime.

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
