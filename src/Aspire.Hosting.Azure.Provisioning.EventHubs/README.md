# Azure Provisioning Event Hubs hosting integration

> [!WARNING]
> This package is experimental and emits `ASPIREAZUREPROVISIONING001`.

Use this integration to customize Azure Event Hubs infrastructure from a polyglot Aspire AppHost.

## Getting started

### Prerequisites

An Azure subscription.

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure.Provisioning.EventHubs` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.Provisioning.EventHubs
```

## Usage example

Then, in a TypeScript AppHost, customize the Event Hubs namespace created by Aspire:

```typescript
import { createBuilder } from "./.aspire/modules/aspire.mjs";

const builder = await createBuilder();
const eventHubs = await builder.addAzureEventHubs("eventhubs");

await eventHubs.configureInfrastructure(async infrastructure => {
    const namespace = await infrastructure.getEventHubsNamespace();
    const tags = await namespace.tags.get();
    await tags.set("environment", "production");
});
```

The generated proxy exposes an intentionally bounded Azure Provisioning Event Hubs API. Properties backed by `BicepValue<T>` accept either the corresponding language primitive or a shared Bicep expression.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://learn.microsoft.com/azure/event-hubs/
* https://learn.microsoft.com/dotnet/azure/sdk/provisioning/

## Feedback & contributing

https://github.com/microsoft/aspire
