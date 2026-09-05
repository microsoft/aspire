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

## Authentication

The broker is protected with SASL/PLAIN authentication over the `SASL_PLAINTEXT` security protocol. A random
password is generated when none is supplied, and is stored in the AppHost user secrets so that it is stable
across runs. Supply your own parameters to control the credentials:

```csharp
var userName = builder.AddParameter("kafka-user");
var password = builder.AddParameter("kafka-password", secret: true);

var kafka = builder.AddKafka("messaging", userName: userName, password: password);
```

When no user name is supplied the broker accepts the user `kafka`.

Authentication can be turned off, which makes the broker listen in plaintext:

```csharp
var kafka = builder.AddKafka("messaging").WithPassword(null);
```

> [!WARNING]
> The password is used to derive the broker configuration, not the stored data, so changing it does not
> invalidate an existing data volume. Clients connecting outside of Aspire must be updated to authenticate.

## Connection Properties

When you reference a Kafka resource using `WithReference`, the following connection properties are made available to the consuming project:

### Kafka server

The Kafka server resource exposes the following connection properties:

| Property Name | Description |
|---------------|-------------|
| `Host` | The host-facing Kafka listener hostname or IP address |
| `Port` | The host-facing Kafka listener port |
| `Username` | The SASL user name for authentication. Only present when the broker is password protected |
| `Password` | The SASL password for authentication. Only present when the broker is password protected |

Aspire exposes each property as an environment variable named `[RESOURCE]_[PROPERTY]`. For instance, the `Host` property of a resource called `messaging` becomes `MESSAGING_HOST`.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://aspire.dev/integrations/messaging/apache-kafka/apache-kafka-host/

## Feedback & contributing

https://github.com/microsoft/aspire
