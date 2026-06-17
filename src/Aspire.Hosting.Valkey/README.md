# Valkey hosting integration

Use this integration to model, configure, and orchestrate a Valkey cache resource in an Aspire solution.

## Getting started

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Valkey` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Valkey
```

## Usage example

In the AppHost, add a Valkey resource and reference it from another resource with either C# or TypeScript:

**C#**

```csharp
var valkey = builder.AddValkey("cache");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(valkey);
```

**TypeScript**

```typescript
const valkey = await builder.addValkey("cache");

const myService = await builder.addNodeApp("myService", "../my-service", "server.js")
                       .withReference(valkey);
```

## Connection Properties

When you reference a Valkey resource using `WithReference`, the following connection properties are made available to the consuming project:

### Valkey

The Valkey resource exposes the following connection properties:

| Property Name | Description |
|---------------|-------------|
| `Host` | The hostname or IP address of the Valkey server |
| `Port` | The port number the Valkey server is listening on |
| `Password` | The password for authentication |
| `Uri` | The connection URI, with the format `valkey://:{Password}@{Host}:{Port}` |

Aspire exposes each property as an environment variable named `[RESOURCE]_[PROPERTY]`. For instance, the `Uri` property of a resource called `db1` becomes `DB1_URI`.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://aspire.dev/integrations/caching/valkey/valkey-host/
* https://valkey.io
* https://github.com/valkey-io/valkey/blob/unstable/README.md
* https://valkey.io/docs/

## Feedback & contributing

https://github.com/microsoft/aspire
