# Azure Service Bus hosting integration

Use this integration to model, configure, and orchestrate Azure Service Bus in an Aspire solution.

## Getting started

### Prerequisites

- Azure subscription - [create one for free](https://azure.microsoft.com/free/)

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure.ServiceBus` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.ServiceBus
```

## Configure Azure Provisioning for local development

Adding Azure resources to the AppHost model will automatically enable development-time provisioning
for Azure resources so that you don't need to configure them manually. Provisioning requires a number of settings
to be available via AppHost configuration. From your AppHost directory, set these values with `aspire secret set`:

```bash
aspire secret set Azure:SubscriptionId "<your subscription id>"
aspire secret set Azure:ResourceGroupPrefix "<prefix for the resource group>"
aspire secret set Azure:Location "<azure location>"
```

> NOTE: Developers must have Owner access to the target subscription so that role assignments
> can be configured for the provisioned resources.

## Usage example

In the AppHost, add a Service Bus connection and reference it from another resource with either C# or TypeScript:

**C#**

```csharp
var serviceBus = builder.AddAzureServiceBus("sb");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(serviceBus);
```

**TypeScript**

```typescript
const serviceBus = await builder.addAzureServiceBus("sb");

const myService = await builder.addNodeApp("myService", "../my-service", "server.js")
                       .withReference(serviceBus);
```

## Connection Properties

When you reference Azure Service Bus resources using `WithReference`, the following connection properties are made available to the consuming project:

### Service Bus namespace

The Service Bus namespace resource exposes the following connection properties:

| Property Name | Description |
|---------------|-------------|
| `Host` | The hostname of the Service Bus namespace |
| `Port` | The port of the Service Bus namespace when the emulator is used |
| `Uri` | The connection URI, with the format `sb://myservicebus.servicebus.windows.net` |
| `ConnectionString` | **Emulator only.** Includes SAS key material for the local emulator connection. |

### Service Bus queue

The Service Bus queue resource inherits all properties from its parent Service Bus namespace and adds:

| Property Name | Description |
|---------------|-------------|
| `QueueName` | The name of the queue |

### Service Bus topic

The Service Bus topic resource inherits all properties from its parent Service Bus namespace and adds:

| Property Name | Description |
|---------------|-------------|
| `TopicName` | The name of the topic |

### Service Bus subscription

The Service Bus subscription resource inherits all properties from its parent Service Bus topic and adds:

| Property Name | Description |
|---------------|-------------|
| `SubscriptionName` | The name of the subscription |
| `ConnectionString` | The connection string for the subscription |

Aspire exposes each property as an environment variable named `[RESOURCE]_[PROPERTY]`. For instance, the `Uri` property of a resource called `db1` becomes `DB1_URI`.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://aspire.dev/integrations/cloud/azure/azure-service-bus/azure-service-bus-host/
* https://learn.microsoft.com/azure/service-bus-messaging/service-bus-messaging-overview

## Feedback & contributing

https://github.com/microsoft/aspire
