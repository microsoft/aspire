# Azure Provisioning SignalR hosting integration

> [!WARNING]
> This package is experimental and emits `ASPIREAZUREPROVISIONING001`.

Use this integration to customize Azure SignalR Service infrastructure from a polyglot Aspire AppHost. It references `Aspire.Hosting.Azure.SignalR` and projects a bounded configuration surface from `Azure.Provisioning.SignalR`; it does not add a separate resource lifecycle.

## Getting started

### Prerequisites

An Azure subscription when provisioning resources.

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure.Provisioning.SignalR` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.Provisioning.SignalR
```

## Usage example

Then, in a TypeScript AppHost, customize the SignalR service created by Aspire:

```typescript
import { createBuilder } from "./.aspire/modules/aspire.mjs";

const builder = await createBuilder();
const signalr = await builder.addAzureSignalR("signalr");

await signalr.configureInfrastructure(async infrastructure => {
    const service = await infrastructure.getSignalRService();
    const tags = await service.tags.get();
    await tags.set("environment", "production");
});
```

## Scope and limitations

`SignalRService` is the selected infrastructure root. Compatible models from the same SDK assembly are also projected, not every SDK member. Supported `BicepValue<T>` properties accept the corresponding language value or a shared Bicep expression. Customizations affect generated Azure infrastructure; the hosting integration continues to own references, authentication, and resource lifecycle.

## Additional documentation

* [Hosting integration](../Aspire.Hosting.Azure.SignalR/README.md)
* [Provisioning inventory and shared expressions](../Aspire.Hosting.Azure.Provisioning/README.md)
* https://aspire.dev/integrations/gallery/
* https://learn.microsoft.com/azure/azure-signalr/
* https://learn.microsoft.com/dotnet/azure/sdk/provisioning/

## Feedback & contributing

https://github.com/microsoft/aspire
