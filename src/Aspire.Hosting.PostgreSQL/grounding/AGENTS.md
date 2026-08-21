# Aspire.Hosting.PostgreSQL — Agent Guide

You are writing **AppHost** code that uses `Aspire.Hosting.PostgreSQL` to model PostgreSQL server and database resources. This package is a **hosting integration only** — it does NOT provide a client driver. For consuming projects, wire up `Aspire.Npgsql` (or `Aspire.Npgsql.EntityFrameworkCore.PostgreSQL`) or read the injected environment variables directly. Apply the rules below when generating or editing AppHost code.

## Core rules

- **Add the server, then a database, then reference the database.** Call `AddPostgres(...)` for the server and `AddDatabase(...)` for each logical database. Always `WithReference(...)` the **database** resource, never the server.
- **Never hardcode connection strings, hosts, ports, or credentials.** Let `WithReference(...)` inject them as environment variables (e.g. `MYDB_URI`, `MYDB_HOST`).
- **Never hardcode container data paths.** The path changed in PostgreSQL 18 — use `WithDataVolume()` / `WithDataBindMount()`, which select the correct path automatically.

```csharp
var postgres = builder.AddPostgres("postgres");
var db = postgres.AddDatabase("mydb");
builder.AddProject<Projects.MyApi>().WithReference(db);
```

## APIs you can use

### WithPostgresMcp() — AI agent database access via MCP (v13.2+)

Call on a **database** resource (not the server) to add a Model Context Protocol sidecar container (`crystaldba/postgres-mcp:0.3.0`) for AI-agent interaction:

```csharp
var db = builder.AddPostgres("pg")
    .AddDatabase("mydb")
    .WithPostgresMcp();

// Optionally configure the sidecar container:
var db = builder.AddPostgres("pg")
    .AddDatabase("mydb")
    .WithPostgresMcp(configureContainer: mcp => mcp.WithHostPort(8000));
```

### WithCreationScript() — Custom database initialization SQL

Call on a **database** resource to replace the default `CREATE DATABASE "<name>"` script. Use it for extensions, schemas, or seed data — and **always pair it with `WaitFor(...)`** so consumers don't start before the database is ready:

```csharp
var db = postgres.AddDatabase("mydb")
    .WithCreationScript("""
        CREATE DATABASE mydb;
        \connect mydb
        CREATE EXTENSION IF NOT EXISTS "uuid-ossp";
        """);

builder.AddProject<Projects.MyService>("myservice")
    .WithReference(db)
    .WaitFor(db);
```

- **Do NOT use the `\c` shorthand** — it is not supported. Use the full `\connect`, or add extensions via init scripts instead.

### WithInitFiles() — Copy SQL init scripts into the container (v13.1+)

Call on the **server** resource to copy files from a host directory into `/docker-entrypoint-initdb.d` via `docker cp`. Prefer this over `WithInitBindMount` because it also works in publish mode:

```csharp
var postgres = builder.AddPostgres("postgres")
    .WithInitFiles(source: "../sql-scripts");
```

### WithPassword() / WithUserName() / WithHostPort() — Fluent credential config (v13.2+)

Call on the **server** resource. Prefer parameters for secrets — pass a secret parameter to `WithPassword()`, never a literal:

```csharp
var password = builder.AddParameter("pg-pass", secret: true);
var username = builder.AddParameter("pg-user");

var postgres = builder.AddPostgres("postgres")
    .WithUserName(username)
    .WithPassword(password)
    .WithHostPort(5432);
```

## Version-specific behavior — read before changing data volumes or images

### PostgreSQL 18 data volume path (Aspire 13.4+)

Aspire **13.4** bumped the default image to **PostgreSQL 18**. Aspire selects the container data directory automatically from the configured image tag:

| PostgreSQL version | Container data directory |
|--------------------|--------------------------|
| 17 and earlier     | `/var/lib/postgresql/data` |
| 18 and later       | `/var/lib/postgresql` |

- **Do NOT migrate existing data volumes silently.** Path *selection* is automatic, but data is **not** migrated. A volume created by an earlier Aspire version (PostgreSQL 17) is **incompatible** with PostgreSQL 18 and the container fails to start with:

  ```text
  Error: in 18+, these Docker images are configured to store database data in a
         format which is compatible with "pg_ctlcluster" ...
         Counter to that, there appears to be PostgreSQL data in:
           /var/lib/postgresql
  ```

- **If a user has an existing PostgreSQL 17 volume, prefer pinning back to 17** (no migration). Call `WithImageTag(...)` (or `WithImage(...)`) **before** `WithDataVolume()` — Aspire reads the tag at the moment `WithDataVolume()` runs:

  ```csharp
  var db = builder.AddPostgres("pgsql")
                  .WithImageTag("17.6")
                  .WithDataVolume();
  ```

- **To move to PostgreSQL 18, migrate explicitly** (`pg_dumpall`/restore or `pg_upgrade`, per the official [upgrade docs](https://www.postgresql.org/docs/current/upgrading.html)). Only if the data is disposable, recreate the volume from scratch (stop the app, detach referencing containers, `docker volume rm`, rerun). **Always back up first.**

### Connection properties replace connection strings (Aspire 13.1+)

Aspire exposes individual **connection properties** as environment variables, prefixed with the uppercased resource name — do NOT expect a single connection-string variable:

| Variable | Example value |
|----------|---------------|
| `POSTGRESDB_HOST` | `localhost` |
| `POSTGRESDB_PORT` | `5432` |
| `POSTGRESDB_USERNAME` | `postgres` |
| `POSTGRESDB_PASSWORD` | `generated_pw` |
| `POSTGRESDB_DATABASENAME` | `postgresdb` |
| `POSTGRESDB_URI` | `postgresql://postgres:pw@localhost:5432/postgresdb` |

### JDBC connection strings exclude credentials (Aspire 13.0+)

`JdbcConnectionString` contains only `jdbc:postgresql://{host}:{port}/{db}`. Read username and password from the separate `{NAME}_USERNAME` / `{NAME}_PASSWORD` properties.

## Deployment rules

- **Use the right package per project role:**
  - AppHost project → `Aspire.Hosting.PostgreSQL` (this package).
  - Consuming C# project → `Aspire.Npgsql` (`NpgsqlDataSource` with DI, health checks, telemetry).
  - Consuming C# project with EF Core → `Aspire.Npgsql.EntityFrameworkCore.PostgreSQL`.
- **Do NOT rely on PostgreSQL container data volumes when deploying to Azure Container Apps (ACA)** — ACA uses SMB mounts that are incompatible with the container. Use `Aspire.Hosting.Azure.PostgreSQL` (Azure Database for PostgreSQL Flexible Server) for deployed scenarios.

## Defaults to assume (unless overridden)

- **Container image:** `docker.io/library/postgres:18.3`
- **Username:** `postgres` (override with `WithUserName()`)
- **Password:** randomly generated via `CreateDefaultPasswordParameter` (override with `WithPassword()`)
- **Database creation:** triggered by `ResourceReadyEvent` — the database is created once the server becomes healthy

## Reference docs

- Hosting API reference: https://aspire.dev/integrations/databases/postgres/postgres-host.md
- Connecting from apps: https://aspire.dev/integrations/databases/postgres/postgres-connect.md
- Getting started: https://aspire.dev/integrations/databases/postgres/postgres-get-started.md
- Azure PostgreSQL: https://aspire.dev/integrations/cloud/azure/azure-postgresql/azure-postgresql-host.md
- Source: https://github.com/microsoft/aspire/tree/main/src/Aspire.Hosting.PostgreSQL
