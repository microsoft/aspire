# Seq hosting integration

Use this integration to model, configure, and orchestrate a Seq resource in an Aspire solution.

## Getting started

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Seq` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Seq
```

## Usage example

In the AppHost, add a Seq resource and reference it from another resource with either C# or TypeScript:

**C#**

```csharp
var seq = builder.AddSeq("seq");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(seq);
```

**TypeScript**

```typescript
const seq = await builder.addSeq("seq");

const myService = await builder.addNodeApp("myService", "../my-service", "server.js")
                       .withReference(seq);
```

## Connection Properties

When you reference a Seq resource using `WithReference`, the following connection properties are made available to the consuming project:

### Seq

The Seq resource exposes the following connection properties:

| Property Name | Description |
|---------------|-------------|
| `Host` | The hostname or IP address of the Seq server |
| `Port` | The port number the Seq server is listening on |
| `Uri` | The connection URI, with the format `http://{Host}:{Port}` |

Aspire exposes each property as an environment variable named `[RESOURCE]_[PROPERTY]`. For instance, the `Uri` property of a resource called `db1` becomes `DB1_URI`.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://aspire.dev/integrations/observability/seq/seq-host/

## Feedback & contributing

https://github.com/microsoft/aspire
