# Apache Kafka hosting integration

Use this integration to model, configure, and orchestrate a Kafka resource in an Aspire solution.

## Getting started

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Kafka` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Kafka
```

## Usage example

In the AppHost, add a Kafka resource and reference it from another resource with either C# or TypeScript:

**C#**

```csharp
var kafka = builder.AddKafka("messaging");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(kafka);
```

**TypeScript**

```typescript
const kafka = await builder.addKafka("messaging");

const myService = await builder.addNodeApp("myService", "../my-service", "server.js")
                       .withReference(kafka);
```

## Connection Properties

When you reference a Kafka resource using `WithReference`, the following connection properties are made available to the consuming project:

### Kafka server

The Kafka server resource exposes the following connection properties:

| Property Name | Description |
|---------------|-------------|
| `Host` | The host-facing Kafka listener hostname or IP address |
| `Port` | The host-facing Kafka listener port |

Aspire exposes each property as an environment variable named `[RESOURCE]_[PROPERTY]`. For instance, the `Uri` property of a resource called `messaging` becomes `MESSAGING_URI`.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://aspire.dev/integrations/messaging/apache-kafka/apache-kafka-host/

## Feedback & contributing

https://github.com/microsoft/aspire
