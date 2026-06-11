# RabbitMQ hosting integration

Use this integration to model, configure, and orchestrate a RabbitMQ resource in an Aspire solution.

## Getting started

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.RabbitMQ` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.RabbitMQ
```

## Usage example

In the AppHost, add a RabbitMQ resource and reference it from another resource with either C# or TypeScript:

**C#**

```csharp
var rmq = builder.AddRabbitMQ("rmq");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(rmq);
```

**TypeScript**

```typescript
const rmq = await builder.addRabbitMQ("rmq");

const myService = await builder.addNodeApp("myService", "../my-service", "server.js")
                       .withReference(rmq);
```

## Connection Properties

When you reference a RabbitMQ resource using `WithReference`, the following connection properties are made available to the consuming project:

### RabbitMQ server

The RabbitMQ server resource exposes the following connection properties:

| Property Name | Description |
|---------------|-------------|
| `Host` | The hostname or IP address of the RabbitMQ server |
| `Port` | The port number the RabbitMQ server is listening on |
| `Username` | The username for authentication |
| `Password` | The password for authentication |
| `Uri` | The connection URI, with the format `amqp://{Username}:{Password}@{Host}:{Port}` |

Aspire exposes each property as an environment variable named `[RESOURCE]_[PROPERTY]`. For instance, the `Uri` property of a resource called `db1` becomes `DB1_URI`.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://aspire.dev/integrations/messaging/rabbitmq/rabbitmq-host/

## Feedback & contributing

https://github.com/microsoft/aspire
