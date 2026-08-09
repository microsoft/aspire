# MySQL hosting integration

Use this integration to model, configure, and orchestrate a MySQL resource in an Aspire solution.

## Getting started

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.MySql` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.MySql
```

## Usage example

In the AppHost, add a MySQL resource and reference it from another resource with either C# or TypeScript:

**C#**

```csharp
var db = builder.AddMySql("mysql").AddDatabase("mydb");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(db);
```

**TypeScript**

```typescript
const db = await builder.addMySql("mysql").addDatabase("mydb");

const myService = await builder.addNodeApp("myService", "../my-service", "server.js")
                       .withReference(db);
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

* https://aspire.dev/integrations/gallery/
* https://aspire.dev/integrations/databases/mysql/mysql-host/

## Feedback & contributing

https://github.com/microsoft/aspire
