# PostgreSQL hosting integration

Use this integration to model, configure, and orchestrate a PostgreSQL resource in an Aspire solution.

## Getting started

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.PostgreSQL` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.PostgreSQL
```

## Usage example

In the AppHost, add a PostgreSQL resource and reference it from another resource with either C# or TypeScript:

**C#**

```csharp
var db = builder.AddPostgres("pgsql").AddDatabase("mydb");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(db);
```

**TypeScript**

```typescript
const db = await builder.addPostgres("pgsql").addDatabase("mydb");

const myService = await builder.addNodeApp("myService", "../my-service", "server.js")
                       .withReference(db);
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

## Data volumes and bind mounts

Use `WithDataVolume` (or `WithDataBindMount`) to persist database data across restarts:

```csharp
var db = builder.AddPostgres("pgsql")
                .WithDataVolume();
```

The data directory mounted into the container depends on the PostgreSQL version of the configured
container image, which Aspire selects automatically:

| PostgreSQL version | Container data directory |
|--------------------|--------------------------|
| 17 and earlier     | `/var/lib/postgresql/data` |
| 18 and later       | `/var/lib/postgresql` |

### Keeping an existing data volume working (PostgreSQL 18 upgrade)

PostgreSQL 18 changed the on-disk data layout: the official image now expects the data directory at
`/var/lib/postgresql` and stores cluster files in a major-version-specific subdirectory (see
[docker-library/postgres#1259](https://github.com/docker-library/postgres/pull/1259) and
[docker-library/postgres#37](https://github.com/docker-library/postgres/issues/37)). Because Aspire 13.4
upgraded the default PostgreSQL image to 18, a data volume that was created by an earlier Aspire version
(PostgreSQL 17) is not compatible and the container fails to start with an error such as:

```text
Error: in 18+, these Docker images are configured to store database data in a
       format which is compatible with "pg_ctlcluster" ...
       Counter to that, there appears to be PostgreSQL data in:
         /var/lib/postgresql
```

PostgreSQL does not upgrade data files between major versions automatically. Choose one of the following.

#### Option 1 — Stay on PostgreSQL 17 (no migration, recommended)

Pin the container image back to a PostgreSQL 17 tag. Your existing data volume keeps working unchanged,
and Aspire automatically selects the matching data directory (`/var/lib/postgresql/data`) for version 17.

```csharp
var db = builder.AddPostgres("pgsql")
                .WithImageTag("17.6")
                .WithDataVolume();
```

> [!IMPORTANT]
> Call `WithImageTag` (or `WithImage`) **before** `WithDataVolume`. Aspire picks the data directory from
> the configured image tag at the time `WithDataVolume` is called, so the tag must be set first.

#### Option 2 — Upgrade the data volume to PostgreSQL 18

To move to PostgreSQL 18, upgrade your existing cluster by following the official PostgreSQL
[upgrading documentation](https://www.postgresql.org/docs/current/upgrading.html) (for example, using
`pg_dumpall`/restore or `pg_upgrade`), then run with the default (18+) image.

If the data is disposable (for example, it is re-seeded on startup), you can instead start fresh by
removing the old volume and letting Aspire create a new PostgreSQL 18 volume:

```bash
# Find the volume (Aspire names it "{appname}-{hash}-{resourceName}-data")
docker volume ls
docker volume rm <old-volume-name>
```

> [!IMPORTANT]
> Back up your data volume before performing any migration.

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

https://aspire.dev/integrations/gallery/
https://aspire.dev/integrations/databases/postgres/postgres-host/

## Feedback & contributing

https://github.com/microsoft/aspire

_*Postgres, PostgreSQL and the Slonik Logo are trademarks or registered trademarks of the PostgreSQL Community Association of Canada, and used with their permission._
