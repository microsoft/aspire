# Azure Provisioning Service Bus hosting integration

> [!WARNING]
> This package is experimental and emits `ASPIREAZUREPROVISIONING001`.

Use this integration to customize Azure Service Bus infrastructure from a polyglot Aspire AppHost.

## Getting started

### Prerequisites

An Azure subscription.

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure.Provisioning.ServiceBus` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.Provisioning.ServiceBus
```

## Usage example

Then, in a TypeScript AppHost, customize the Service Bus namespace created by Aspire:

```typescript
import { createBuilder } from "./.aspire/modules/aspire.mjs";

const builder = await createBuilder();
const serviceBus = await builder.addAzureServiceBus("messaging");

await serviceBus.configureInfrastructure(async infrastructure => {
    const namespace = await infrastructure.getServiceBusNamespace();
    const tags = await namespace.tags.get();
    await tags.set("environment", "production");
});
```

The generated proxy exposes an intentionally bounded Azure Provisioning Service Bus API. Properties backed by `BicepValue<T>` accept either the corresponding language primitive or a shared Bicep expression.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://learn.microsoft.com/azure/service-bus-messaging/
* https://learn.microsoft.com/dotnet/azure/sdk/provisioning/

## Feedback & contributing

https://github.com/microsoft/aspire
