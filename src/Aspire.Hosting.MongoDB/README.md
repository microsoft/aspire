# MongoDB hosting integration

Use this integration to model, configure, and orchestrate a MongoDB resource in an Aspire solution.

## Getting started

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.MongoDB` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.MongoDB
```

## Usage example

Then, in the AppHost, add a MongoDB server with a single-member replica set for local transactions and change streams. Add a database and reference it using the normal resource APIs:

> **Experimental:** Replica set, keyfile, and TLS configuration APIs are marked `ASPIREMONGODB001` and may change. C# AppHosts must explicitly opt in by suppressing this diagnostic where these APIs are used.

**C#**

```csharp
#pragma warning disable ASPIREMONGODB001
var mongodb = builder.AddMongoDB("mongodb").WithReplicaSet();
#pragma warning restore ASPIREMONGODB001
var db = mongodb.AddDatabase("mydb");

var myService = builder.AddProject<Projects.MyService>("myservice")
                       .WithReference(db)
                       .WaitFor(db);
```

**TypeScript**

```typescript
const mongodb = await builder.addMongoDB("mongodb").withReplicaSet();
const db = await mongodb.addDatabase("mydb");

const myService = await builder.addNodeApp("myService", "../my-service", "server.js")
    .withReference(db)
    .waitFor(db);
```

Omit `WithReplicaSet()` / `withReplicaSet()` when a standalone server is sufficient.

### Single-member replica set

`WithReplicaSet()` configures **and initializes** a single-member replica set on the same `MongoDBServerResource`; it is no longer just a low-level `mongod` option. No separate replica set resource is needed. `AddDatabase`, `WithReference`, and `WaitFor` continue to work with the server or its databases. Initialization runs during the resource lifecycle, not in health checks. Readiness waits for initialization and primary election, so consumers can use transactions and change streams after `WaitFor`.

The optional set name defaults to the server's Aspire resource name. To choose a different name:

**C#**

```csharp
var mongodb = builder.AddMongoDB("mongodb").WithReplicaSet("app-rs");
```

**TypeScript**

```typescript
const mongodb = await builder.addMongoDB("mongodb").withReplicaSet({ name: "app-rs" });
```

Repeating the same configuration is a no-op; a conflicting name throws. Omitting the name on a later call preserves the previously configured name.

Consumer connection strings use the server's normal endpoint with `directConnection=true` and the driver's default primary read preference. This path does not use topology discovery, split-horizon addresses, or fixed host ports. It preserves normal automatic TLS behavior but does not require a developer certificate or insecure TLS flags.

#### Keyfiles and persistence

MongoDB requires a keyfile when authentication and replication are enabled, even with one member. `WithReplicaSet()` generates a secret keyfile if none is configured. To supply your own keyfile content, pass a secret parameter to `WithKeyFile` **before** calling `WithReplicaSet`:

**C#**

```csharp
var keyfile = builder.AddParameter("mongo-keyfile", secret: true);
var mongodb = builder.AddMongoDB("mongodb")
    .WithKeyFile(keyfile.Resource)
    .WithReplicaSet();
```

**TypeScript**

```typescript
const keyfile = await builder.addParameter("mongo-keyfile", { secret: true });
const mongodb = await builder.addMongoDB("mongodb")
    .withKeyFile(keyfile)
    .withReplicaSet();
```

The parameter supplies the file's **contents**, not a host file path. Contents must satisfy [MongoDB's keyfile requirements](https://www.mongodb.com/docs/manual/tutorial/deploy-replica-set-with-keyfile-access-control/#create-a-keyfile). The file is mounted inside the container at `/etc/rs.key` by default with restricted permissions. It authenticates replica set members and does not replace the username and password used by applications. Supplying a different keyfile after `WithReplicaSet()` conflicts with the generated keyfile and throws; repeating the same explicit keyfile parameter and container path is a no-op.

Use `WithDataVolume()` or `WithDataBindMount()` to persist data. MongoDB only applies initial credentials to an empty data directory, so keep the configured credentials and replica set identity unchanged when reusing data. With the default set name, renaming the Aspire resource also changes the set identity. Existing compatible single-member data is reused without forced reconfiguration; mismatched set names, member addresses, or multi-member data are rejected. Preserve the original configuration or deliberately start with an empty development volume rather than expecting automatic migration.

### Advanced multi-member experiments

`AddMongoDBReplicaSet(...).WithMember(...)` is a separate experimental path for **local multi-member replication experiments**, not a production-ready deployment model. Use it when exploring replication or elections across multiple containers, not merely to enable transactions or change streams. Production topology and deployment projection require separate support.

Create plain MongoDB servers and let `WithMember` configure their shared replication settings. Do **not** call `WithReplicaSet` on these servers: a single-member set configured that way cannot be adopted by the advanced API, and `WithMember` rejects it even if the set names match.

**C#**

```csharp
var mongo1 = builder.AddMongoDB("mongo-1");
var mongo2 = builder.AddMongoDB("mongo-2");
var mongo3 = builder.AddMongoDB("mongo-3");

var replicaSet = builder.AddMongoDBReplicaSet("rs0")
                        .WithMember(mongo1)
                        .WithMember(mongo2)
                        .WithMember(mongo3);

var myService = builder.AddProject<Projects.MyService>("myservice")
                       .WithReference(replicaSet)
                       .WaitFor(replicaSet);
```

**TypeScript**

```typescript
const mongo1 = await builder.addMongoDB("mongo-1");
const mongo2 = await builder.addMongoDB("mongo-2");
const mongo3 = await builder.addMongoDB("mongo-3");

const replicaSet = await builder.addMongoDBReplicaSet("rs0")
    .withMember(mongo1)
    .withMember(mongo2)
    .withMember(mongo3);

const myService = await builder.addNodeApp("myService", "../my-service", "server.js")
    .withReference(replicaSet)
    .waitFor(replicaSet);
```

Reference and wait for the advanced replica set resource, rather than an individual member. A replica set holds at most 50 members, the first seven of which vote in elections; the rest join as non-voting members that still carry a full copy of the data.

Members share credentials and a keyfile owned by the advanced replica set. Pass a username or password to `AddMongoDBReplicaSet`, not to individual members; conflicting credentials or member-specific keyfiles are rejected. Existing volumes retain their original credentials, so use matching credential parameters or intentionally start with empty development volumes. This does not migrate an existing single-member set into the advanced topology.

### Publish limitation

**Both replica set paths are supported only for local runs.** `WithReplicaSet` and `AddMongoDBReplicaSet` reject publish mode as unsupported, rather than being silently excluded or becoming no-ops. `WithKeyFile` and the TLS configuration methods also reject publish mode. Applications using these configurations cannot be published or deployed yet; production deployment projection is separate work.

## TLS

A MongoDB server serves TLS whenever an HTTPS/TLS certificate is available for it, which by default is the ASP.NET Core developer certificate. `WithoutHttpsCertificate()` opts out and `WithTlsMode()` chooses how strict the server is about TLS on incoming connections. The connection string reports this through a `tls=true` flag that is resolved when the connection string is read, so consumers pick it up automatically.

The developer certificate is issued for `localhost`, so a consumer running on the host validates it without any further configuration. A consumer running in a container is a different matter: it reaches the server by its resource name on the container network, which is not a name the certificate carries, so its TLS handshake fails host name validation. Until certificates covering container network names are available, a containerized consumer of a TLS-enabled MongoDB server has to be configured to relax host name validation.

`WithoutHttpsCertificate()` can opt out of TLS for either a standalone server or the simple `WithReplicaSet()` path. The single-member path does not require TLS and does not automatically enable `WithTlsAllowInvalidCertificates()`. When TLS is available, consumers still need to trust the certificate and connect using a hostname it covers.

Only the advanced `AddMongoDBReplicaSet(...).WithMember(...)` path requires TLS for split-horizon addressing: host-reachable addresses are selected using the incoming connection's SNI, and a member without TLS fails initialization. Its current member configuration relaxes peer certificate validation, and containerized clients need certificates covering container network names or development-only hostname-validation relaxation. These limitations are another reason to keep this path for local experiments.

## Connection Properties

When you reference a MongoDB resource using `WithReference`, the following connection properties are made available to the consuming project:

### MongoDB server

The MongoDB server resource exposes the following connection properties:

| Property Name | Description |
|---------------|-------------|
| `Host` | The hostname or IP address of the MongoDB server |
| `Port` | The port number the MongoDB server is listening on |
| `Username` | The username for authentication |
| `Password` | The password for authentication (available when a password parameter is configured) |
| `AuthenticationDatabase` | The authentication database (available when a password parameter is configured) |
| `AuthenticationMechanism` | The authentication mechanism (available when a password parameter is configured) |
| `Uri` | The connection URI, with the format `mongodb://{Username}:{Password}@{Host}:{Port}/?authSource={AuthenticationDatabase}&authMechanism={AuthenticationMechanism}` |

With `WithReplicaSet()`, this remains a server resource with a single `Host` and `Port`; its URI adds `directConnection=true` and keeps the default primary read preference. TLS adds `tls=true` when enabled.

### MongoDB database

The MongoDB database resource combines the server properties above and adds the following connection property:

| Property Name | Description |
|---------------|-------------|
| `DatabaseName` | The MongoDB database name |

### Advanced MongoDB replica set

The resource returned by `AddMongoDBReplicaSet` exposes the following connection properties. Unlike the server configured with `WithReplicaSet`, it has no single `Host` and `Port`, because clients discover the members through the seed list carried in the `Uri`:

| Property Name | Description |
|---------------|-------------|
| `Username` | The username for authentication, shared by every member of the replica set |
| `Password` | The password for authentication, shared by every member of the replica set |
| `AuthenticationDatabase` | The authentication database |
| `AuthenticationMechanism` | The authentication mechanism |
| `ReplicaSetName` | The name of the replica set |
| `Uri` | The connection URI, with the format `mongodb://{Username}:{Password}@{Host1}:{Port1},{Host2}:{Port2}/?replicaSet={ReplicaSetName}&authSource={AuthenticationDatabase}&authMechanism={AuthenticationMechanism}` |

Aspire exposes each property as an environment variable named `[RESOURCE]_[PROPERTY]`. For instance, the `Uri` property of a resource called `db1` becomes `DB1_URI`.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://aspire.dev/integrations/databases/mongodb/mongodb-host/

## Feedback & contributing

https://github.com/microsoft/aspire
