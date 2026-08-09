# Qdrant hosting integration

Use this integration to model, configure, and orchestrate a Qdrant vector database resource in an Aspire solution.

## Getting started

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Qdrant` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Qdrant
```

## Usage example

In the AppHost, add a Qdrant resource and reference it from another resource with either C# or TypeScript:

**C#**

```csharp
var qdrant = builder.AddQdrant("qdrant");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(qdrant);
```

**TypeScript**

```typescript
const qdrant = await builder.addQdrant("qdrant");

const myService = await builder.addNodeApp("myService", "../my-service", "server.js")
                       .withReference(qdrant);
```

## Connection Properties

When you reference a Qdrant resource using `WithReference`, the following connection properties are made available to the consuming project:

### Qdrant server

The Qdrant server resource exposes the following connection properties:

| Property Name | Description |
|---------------|-------------|
| `GrpcHost` | The gRPC hostname of the Qdrant server |
| `GrpcPort` | The gRPC port of the Qdrant server |
| `HttpHost` | The HTTP hostname of the Qdrant server |
| `HttpPort` | The HTTP port of the Qdrant server |
| `ApiKey` | The API key for authentication |
| `Uri` | The gRPC connection URI, with the format `http://{GrpcHost}:{GrpcPort}` |
| `HttpUri` | The HTTP connection URI, with the format `http://{HttpHost}:{HttpPort}` |

Aspire exposes each property as an environment variable named `[RESOURCE]_[PROPERTY]`. For instance, the `Uri` property of a resource called `db1` becomes `DB1_URI`.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://aspire.dev/integrations/databases/qdrant/qdrant-host/
* https://qdrant.tech/documentation

## Feedback & contributing

https://github.com/microsoft/aspire

_Qdrant, and the Qdrant logo are trademarks or registered trademarks of Qdrant Solutions GmbH of Germany, and used with their permission._
