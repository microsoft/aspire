# Azure Provisioning Network hosting integration

> [!WARNING]
> This package is experimental and emits `ASPIREAZUREPROVISIONING001`.

Use this integration to customize Azure networking infrastructure from a polyglot Aspire AppHost. It references `Aspire.Hosting.Azure.Network` and projects a bounded configuration surface from `Azure.Provisioning.Network`; it does not add a separate resource lifecycle.

## Getting started

### Prerequisites

An Azure subscription when provisioning resources.

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure.Provisioning.Network` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.Provisioning.Network
```

## Usage example

Then, in a TypeScript AppHost, customize the virtual network created by Aspire:

```typescript
import { createBuilder } from "./.aspire/modules/aspire.mjs";

const builder = await createBuilder();
const vnet = await builder.addAzureVirtualNetwork("vnet");

await vnet.configureInfrastructure(async infrastructure => {
    const network = await infrastructure.getVirtualNetwork();
    const tags = await network.tags.get();
    await tags.set("environment", "production");
});
```

## Scope and limitations

The selected roots are `VirtualNetwork`, `NetworkSecurityGroup`, `NatGateway`, `PublicIPAddress`, `PrivateEndpoint`, and `NetworkSecurityPerimeter`. Each root lookup applies to the corresponding hosting resource's infrastructure callback; it does not search the whole application.

Compatible SDK models are projected, not every member. Supported `BicepValue<T>` properties accept the corresponding language value or a shared Bicep expression. `AdditionalProperties` is explicitly excluded because `BicepDictionary<BinaryData>` contains opaque JSON without a type-safe ATS representation.

Private DNS zones come from a different SDK: add `Aspire.Hosting.Azure.Provisioning.PrivateDns` to customize those models. Private endpoint targets, such as Storage or SQL resources, likewise need their own provisioning opt-ins for SDK-level customization. This overlay does not change the hosting integration's publish-only networking behavior.

## Additional documentation

* [Hosting integration](../Aspire.Hosting.Azure.Network/README.md)
* [Private DNS overlay](../Aspire.Hosting.Azure.Provisioning.PrivateDns/README.md)
* [Provisioning inventory and shared expressions](../Aspire.Hosting.Azure.Provisioning/README.md)
* https://aspire.dev/integrations/gallery/
* https://learn.microsoft.com/azure/networking/
* https://learn.microsoft.com/dotnet/azure/sdk/provisioning/

## Feedback & contributing

https://github.com/microsoft/aspire
