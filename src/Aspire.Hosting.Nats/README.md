# NATS hosting integration

Use this integration to model, configure, and orchestrate a NATS resource in an Aspire solution.

## Getting started

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Nats` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Nats
```

## Usage example

In the AppHost, add a NATS resource and reference it from another resource with either C# or TypeScript:

**C#**

```csharp
var nats = builder.AddNats("nats");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(nats);
```

**TypeScript**

```typescript
const nats = await builder.addNats("nats");

const myService = await builder.addNodeApp("myService", "../my-service", "server.js")
                       .withReference(nats);
```

## Connection Properties

When you reference a NATS resource using `WithReference`, the following connection properties are made available to the consuming project:

### NATS server

The NATS server resource exposes the following connection properties:

| Property Name | Description |
|---------------|-------------|
| `Host` | The hostname or IP address of the NATS server |
| `Port` | The port number the NATS server is listening on |
| `Username` | The username for authentication |
| `Password` | The password for authentication |
| `Uri` | The connection URI with the format `nats://{Username}:{Password}@{Host}:{Port}` |

Aspire exposes each property as an environment variable named `[RESOURCE]_[PROPERTY]`. For instance, the `Uri` property of a resource called `nats` becomes `NATS_URI`.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://aspire.dev/integrations/messaging/nats/nats-host/

## Feedback & contributing

https://github.com/microsoft/aspire
