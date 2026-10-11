# Azure Provisioning Private DNS hosting integration

> [!WARNING]
> This package is experimental and emits `ASPIREAZUREPROVISIONING001`.

Use this integration to customize Azure Private DNS infrastructure from a polyglot Aspire AppHost. It references `Aspire.Hosting.Azure.Network`; the `PrivateDns` suffix follows the separate `Azure.Provisioning.PrivateDns` SDK.

## Getting started

### Prerequisites

An Azure subscription when provisioning resources.

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure.Provisioning.PrivateDns` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.Provisioning.PrivateDns
```

## Usage example

Then, in a TypeScript AppHost, create and customize a standalone private DNS zone:

```typescript
import { createBuilder } from "./.aspire/modules/aspire.mjs";

const builder = await createBuilder();
const dns = await builder.addAzureInfrastructure("privateDns", async infrastructure => {
    const zone = await infrastructure.addPrivateDnsZone("privateDns");
    await zone.location.set(infrastructure.bicep().location("global"));
});

await dns.configureInfrastructure(async infrastructure => {
    const zone = await infrastructure.getPrivateDnsZone();
    await zone.name.set("app.internal");
    const tags = await zone.tags.get();
    await tags.set("environment", "production");
});
```

The matching Aspire resource and Bicep identifiers make the no-argument root lookup valid. This example does not create a virtual network link or configure name resolution for workloads.

## Scope and limitations

`PrivateDnsZone` is the selected root; compatible models from the same SDK assembly are also projected, not every SDK member. Supported `BicepValue<T>` properties accept the corresponding language value or a shared Bicep expression.

The Network hosting integration creates its private endpoint DNS zones and links internally; there is no public `addAzurePrivateDnsZone` hosting factory. The example therefore uses `addAzureInfrastructure`, rather than assuming those internal resources are exposed as standalone builders.

Add `Aspire.Hosting.Azure.Provisioning.Network` when customizing virtual networks, private endpoints, or their DNS zone groups. Those models belong to `Azure.Provisioning.Network`, not this SDK. This overlay does not replace the hosting integration's private endpoint and DNS lifecycle.

## Additional documentation

* [Hosting integration](../Aspire.Hosting.Azure.Network/README.md)
* [Network overlay](../Aspire.Hosting.Azure.Provisioning.Network/README.md)
* [Provisioning inventory and shared expressions](../Aspire.Hosting.Azure.Provisioning/README.md)
* https://aspire.dev/integrations/gallery/
* https://learn.microsoft.com/azure/dns/private-dns-overview
* https://learn.microsoft.com/dotnet/azure/sdk/provisioning/

## Feedback & contributing

https://github.com/microsoft/aspire
