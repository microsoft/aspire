# MongoDB hosting integration

Use this integration to model, configure, and orchestrate a MongoDB resource in an Aspire solution.

## Getting started

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.MongoDB` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.MongoDB
```

## Usage example

In the AppHost, add a MongoDB resource and reference it from another resource with either C# or TypeScript:

**C#**

```csharp
var db = builder.AddMongoDB("mongodb").AddDatabase("mydb");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(db);
```

**TypeScript**

```typescript
const db = await builder.addMongoDB("mongodb").addDatabase("mydb");

const myService = await builder.addNodeApp("myService", "../my-service", "server.js")
                       .withReference(db);
```

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

### MongoDB database

The MongoDB database resource combines the server properties above and adds the following connection property:

| Property Name | Description |
|---------------|-------------|
| `DatabaseName` | The MongoDB database name |

Aspire exposes each property as an environment variable named `[RESOURCE]_[PROPERTY]`. For instance, the `Uri` property of a resource called `db1` becomes `DB1_URI`.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://aspire.dev/integrations/databases/mongodb/mongodb-host/

## Feedback & contributing

https://github.com/microsoft/aspire
