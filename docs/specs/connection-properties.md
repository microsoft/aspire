# Connection Properties
> Audience - Aspire contributors and integration authors implementing or consuming `IResourceWithConnectionString` resources.

## Overview
- `IResourceWithConnectionString.GetConnectionProperties()` returns a stable set of well known keys mapped to `ReferenceExpression` values that are then injected as environment variables in select resources.
- Keys describe connection metadata beyond the raw connection string so dependent resources can access specific pieces (via `GetConnectionProperty` or environment splatting).
- Keys are emitted in PascalCase; lookups are case insensitive and duplicates should be avoided.
- `WithReference(..., ReferenceEnvironmentInjectionFlags.ConnectionProperties)` splats every key as an environment variable using the reference name prefix and an uppercased key (for example `POSTGRES_HOST`).

## Common Behavior
- Prefer short, technology neutral names when one value is broadly useful (`Host`, `Port`, `Uri`).
- Use explicit prefixes only when a resource exposes multiple endpoints (`GrpcHost`, `HttpHost`).
- Child resources (`*DatabaseResource`, `OpenAIModelResource`) typically return the parent set plus a small overlay using `Union` so downstream callers see both shared and resource specific keys.
- When adding a new resource, surface the minimal property set needed for common configurations.

## Logical and Physical Connection String Names

`WithReference` keeps the resource name, or its explicit `connectionName`, as the logical .NET configuration name. Physical environment-variable names are derived separately:

| Logical name | Legacy physical name | Portable physical name |
| --- | --- | --- |
| `mydb` | `ConnectionStrings__mydb` | `ConnectionStrings__mydb` |
| `my-db` | `ConnectionStrings__my-db` | `ConnectionStrings__my_db` |
| `my_db` | `ConnectionStrings__my_db` | `ConnectionStrings__my_db` |
| `my--db` | `ConnectionStrings__my--db` | `ConnectionStrings__my_db` |
| `my__db` | `ConnectionStrings__my__db` | `ConnectionStrings__my_db` |

Aspire emits both physical aliases when they differ and the target supports both. Deployment targets with stricter naming rules, including Azure App Service, Kubernetes-based publishers (Kubernetes, AKS, and Radius), and Foundry Hosted Agents, emit only the portable alias. Connection properties continue to use their existing normalized prefixes.

Portable suffixes must be derived with `EnvironmentVariableNameEncoder.EncodeConnectionStringName`; it also collapses underscore runs so `__` is not interpreted as another .NET configuration path delimiter. Publishers and integrations must consume `ConnectionStringReferenceAnnotation` instead of reconstructing names. Different logical names that map to the same physical name fail during model construction. Choose unique explicit aliases with `WithReference(resource, connectionName: "...")` to resolve a collision.

`IResourceWithConnectionString.ConnectionStringEnvironmentVariable` is an explicit physical-name override. Aspire emits that exact name only and does not normalize it.

### Client resolution and precedence

Updated Aspire client integrations resolve the logical key through the composed configuration first, then fall back to the portable key:

```csharp
configuration.GetConnectionString(logicalName)
    ?? configuration.GetConnectionString(portableName);
```

Aspire writes the logical and portable values to the same configuration provider, so interleaving provider precedence between the aliases is unnecessary. Direct `IConfiguration` consumers that need both forms should use the same logical-then-portable fallback.

### Migration and compatibility

1. **Compatibility release:** Capable targets receive both aliases. Strict targets receive the portable alias, and updated integrations resolve either alias.
2. **Adoption period:** Integration authors consume connection-reference metadata, and direct consumers adopt logical-then-portable fallback resolution. Applications using older integrations on strict targets should update their clients or use explicit portable `connectionName` values.
3. **Future major release:** Legacy physical aliases may be considered for removal only after direct consumers have a public shared resolver and every workload type has an explicit migration path. A package major version alone is not sufficient reason to remove them.

## Property Catalog

### Network Identity

| Key | Description | Provided By | Notes |
| --- | --- | --- | --- |
| Host | Primary hostname or address for the resource. | PostgreSQL, MySQL, SQL Server, Oracle, Redis, Garnet, Valkey, RabbitMQ, NATS, Kafka, MongoDB, Milvus. | Derived from `EndpointProperty.Host` of the primary endpoint. |
| Port | Primary TCP port number. | Same set as `Host`. | Derived from `EndpointProperty.Port`. |

### URIs and URLs

| Key | Description | Provided By | Notes |
| --- | --- | --- | --- |
| Uri | Service specific URI built from host, port, credentials, and scheme. | PostgreSQL, PostgresDatabase, MySQL, MySqlDatabase, Redis, Garnet, Valkey, RabbitMQ, NATS, Seq, MongoDBDatabase, Milvus, Qdrant (gRPC), GitHubModel, OpenAI. | Formatting rules per resource are listed in [URI Construction](#uri-construction). |

### Credentials and Secrets

| Key | Description | Provided By | Notes |
| --- | --- | --- | --- |
| Username | Login user for the primary endpoint. | PostgreSQL, MySQL, SQL Server, Oracle, RabbitMQ, NATS, MongoDB. | Defaults align with respective containers (`postgres`, `root`, `sa`, etc.). |
| Password | Login password. | PostgreSQL, MySQL, SQL Server, Oracle, Redis (when configured), Garnet (when configured), Valkey (when configured), RabbitMQ, NATS (when configured). | Omitted when the resource does not manage credentials. |
| Key | API key or token parameter. | OpenAI, GitHubModel, Qdrant. | For GitHub Models the key is a PAT or minted token. |

### Database Specific Metadata

| Key | Description | Provided By | Notes |
| --- | --- | --- | --- |
| Database | Logical database name associated with the resource. | PostgresDatabase, MySqlDatabase, SqlServerDatabase, OracleDatabase, MongoDBDatabase, MilvusDatabase. | Added on top of the parent server keys. |
| JdbcConnectionString | JDBC compatible connection string. | PostgreSQL, PostgresDatabase, MySQL, MySqlDatabase, SQL Server, SqlServerDatabase, Oracle, OracleDatabase. | Formats vary per vendor; see [JDBC Formats](#jdbc-formats). |

## URI Construction

URIs as convey connection information in a generic format that is commonly used by client SDKs. It follows the following pattern: `{SCHEME}://[[USERNAME]:[PASSWORD]@]{HOST}:{PORT}[/{RESOURCE}]`

URI components should be url-encoded to prevent parsing issues. This is important as components like the password may contain conflicting characters.

- PostgreSQL server: `postgresql://{Username}:{Password}@{Host}:{Port}` (database resources append `/{Database}`).
- MySQL server: `mysql://{Username}:{Password}@{Host}:{Port}` (database resources append `/{Database}`).
- Redis, Garnet, Valkey: `{scheme}://[:{Password}@]{Host}:{Port}` where the scheme matches the resource (`redis`, `valkey`).
- RabbitMQ: `amqp://{Username}:{Password}@{Host}:{Port}`; `ManagementUri` uses HTTP(S) per endpoint configuration.
- NATS: `nats://[Username:Password@]{Host}:{Port}` with credentials omitted when absent.
- MongoDB database: `mongodb://[Username:Password@]{Host}:{Port}/{Database}` with optional `?authSource` and `authMechanism` query string when credentials are present.
- Milvus: `http(s)://{Host}:{Port}` produced from the primary endpoint; credentials are exposed via the `Token` property.

All URIs are composed with `:uri` formatting to ensure values are escaped correctly when rendered at deploy time.

## JDBC Formats

JDBC connection strings do not include user and password credentials. These are provided as separate `Username` and `Password` connection properties.

- PostgreSQL: `jdbc:postgresql://{Host}:{Port}[/{Database}]` (database resources append the database segment).
- MySQL: `jdbc:mysql://{Host}:{Port}[/{Database}]`.
- SQL Server: `jdbc:sqlserver://{Host}:{Port};trustServerCertificate=true[;databaseName={Database}]`.
- Oracle: `jdbc:oracle:thin:@//{Host}:{Port}[/{Database}]`.

## Implementation Guidance

- Reuse existing key names whenever possible; introduce new keys only for data that is unique or disambiguates multiple endpoints.
- When a resource inherits another resource's connection properties, merge the parent set first to preserve overrides and annotations.
- Prefer emitting a single URI per endpoint type; if multiple endpoints exist, use a suffix that clarifies the transport (`HttpUri`, `GrpcUri`).
- Avoid recomputing expensive values in `GetConnectionProperties`; build reusable `ReferenceExpression` instances or helper methods instead.
- Expose the values as reusable properties on the resource such that users can create their own expressions when necessary.
