# Azure Provisioning Front Door hosting integration

> [!WARNING]
> This package is experimental and emits `ASPIREAZUREPROVISIONING001`.

Use this integration to customize Azure Front Door infrastructure from a polyglot Aspire AppHost. It references `Aspire.Hosting.Azure.FrontDoor`; the `Cdn` package suffix follows the underlying `Azure.Provisioning.Cdn` SDK, not the hosting integration name.

## Getting started

### Prerequisites

An Azure subscription when provisioning resources.

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure.Provisioning.Cdn` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.Provisioning.Cdn
```

## Usage example

Then, in a TypeScript AppHost, customize the Front Door profile created by Aspire:

```typescript
import { createBuilder } from "./.aspire/modules/aspire.mjs";

const builder = await createBuilder();
const frontDoor = await builder.addAzureFrontDoor("frontdoor");

await frontDoor.configureInfrastructure(async infrastructure => {
    const profile = await infrastructure.getCdnProfile();
    const tags = await profile.tags.get();
    await tags.set("environment", "production");
});
```

Add origins through the Front Door hosting APIs when routing traffic; this example only configures the profile.

## Scope and limitations

`CdnProfile` is the selected infrastructure root. Compatible CDN/Front Door models, including endpoint, origin group, origin, and route resources, are projected from the same SDK assembly. Use identifier-based lookups for resources associated with individual origins.

This is a bounded SDK configuration overlay, not a separate lifecycle integration or a guarantee of support for every SDK member. Supported `BicepValue<T>` properties accept the corresponding language value or a shared Bicep expression.

## Additional documentation

* [Hosting integration](../Aspire.Hosting.Azure.FrontDoor/README.md)
* [Provisioning inventory and shared expressions](../Aspire.Hosting.Azure.Provisioning/README.md)
* https://aspire.dev/integrations/gallery/
* https://learn.microsoft.com/azure/frontdoor/
* https://learn.microsoft.com/dotnet/azure/sdk/provisioning/

## Feedback & contributing

https://github.com/microsoft/aspire
