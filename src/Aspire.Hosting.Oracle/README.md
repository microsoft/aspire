# Oracle Database hosting integration

Use this integration to model, configure, and orchestrate an Oracle database resource in an Aspire solution.

## Getting started

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Oracle` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Oracle
```

## Usage example

In the AppHost, add an Oracle database resource and reference it from another resource with either C# or TypeScript:

**C#**

```csharp
var db = builder.AddOracle("oracle").AddDatabase("mydb");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(db);
```

**TypeScript**

```typescript
const db = await builder.addOracle("oracle").addDatabase("mydb");

const myService = await builder.addNodeApp("myService", "../my-service", "server.js")
                       .withReference(db);
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

https://aspire.dev/integrations/gallery/
https://aspire.dev/integrations/databases/efcore/oracle/oracle-host/

## Feedback & contributing

https://github.com/microsoft/aspire
