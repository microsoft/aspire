# Azure Event Hubs hosting integration

Use this integration to model, configure, and orchestrate Azure Event Hubs in an Aspire solution.

## Getting started

### Prerequisites

- Azure subscription - [create one for free](https://azure.microsoft.com/free/)

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure.EventHubs` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.EventHubs
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

In the AppHost, add an Event Hubs connection and reference it from another resource with either C# or TypeScript:

**C#**

```csharp
var eventHubs = builder.AddAzureEventHubs("eh");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(eventHubs);
```

**TypeScript**

```typescript
const eventHubs = await builder.addAzureEventHubs("eh");

const myService = await builder.addNodeApp("myService", "../my-service", "server.js")
                       .withReference(eventHubs);
```

## Connection Properties

When you reference Azure Event Hubs resources using `WithReference`, the following connection properties are made available to the consuming project:

### Event Hubs namespace

The Event Hubs namespace resource exposes the following connection properties:

| Property Name | Description |
|---------------|-------------|
| `Host`        | The hostname of the Event Hubs namespace |
| `Port`        | The port of the Event Hubs namespace when the emulator is used |
| `Uri`         | The connection URI for the Event Hubs namespace, with the format `sb://myeventhubs.servicebus.windows.net` on azure and `sb://localhost:62824` for the emulator |
| `ConnectionString` | **Emulator only.** Includes SAS key material for the local emulator connection. |

### Event Hub

The Event Hub resource inherits all properties from its parent Event Hubs namespace and adds:

| Property Name | Description |
|---------------|-------------|
| `EventHubName` | The name of the event hub |

### Event Hub consumer group

The Event Hub consumer group resource inherits all properties from its parent Event Hub and adds:

| Property Name | Description |
|---------------|-------------|
| `ConsumerGroupName` | The name of the consumer group |

Aspire exposes each property as an environment variable named `[RESOURCE]_[PROPERTY]`. For instance, the `Uri` property of a resource called `db1` becomes `DB1_URI`.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://aspire.dev/integrations/cloud/azure/azure-event-hubs/azure-event-hubs-host/
* https://learn.microsoft.com/azure/event-hubs/event-hubs-about

## Feedback & contributing

https://github.com/microsoft/aspire
