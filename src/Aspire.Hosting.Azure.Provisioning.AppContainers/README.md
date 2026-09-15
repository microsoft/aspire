# Azure Provisioning Container Apps hosting integration

> [!WARNING]
> This package is experimental and emits `ASPIREAZUREPROVISIONING001`.

Use this integration to customize Azure Container Apps infrastructure from a polyglot Aspire AppHost. It references `Aspire.Hosting.Azure.AppContainers` and projects a bounded configuration surface from `Azure.Provisioning.AppContainers`; deployment and resource lifecycle remain with the hosting integration.

## Getting started

### Prerequisites

An Azure subscription when provisioning resources.

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure.Provisioning.AppContainers` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.Provisioning.AppContainers
```

## Usage example

Then, in a TypeScript AppHost, customize the managed environment created by Aspire:

```typescript
import { createBuilder } from "./.aspire/modules/aspire.mjs";

const builder = await createBuilder();
const environment = await builder.addAzureContainerAppEnvironment("apps");

await environment.configureInfrastructure(async infrastructure => {
    const managedEnvironment = await infrastructure.getContainerAppManagedEnvironment();
    const tags = await managedEnvironment.tags.get();
    await tags.set("environment", "production");
});
```

## Scope and limitations

The selected roots are `ContainerAppManagedEnvironment`, `ContainerApp`, and `ContainerAppJob`. App and job customizations belong in their publish callbacks; use identifier-based lookups when their Bicep identifiers differ from the callback's Aspire resource identifier. Compatible SDK models are projected, not every member. Supported `BicepValue<T>` properties accept the corresponding language value or a shared Bicep expression.

`OutboundIPAddressList` is exposed as an IP address collection proxy. IP address lists accept IPv4/IPv6 strings or Bicep value handles; element getters preserve literals, expressions, and references as Bicep value handles. Invalid address strings fail validation rather than being silently dropped. Azure SDK read-only output restrictions still apply.

To customize supporting SDK resources, opt into the corresponding `Aspire.Hosting.Azure.Provisioning.ContainerRegistry`, `.OperationalInsights`, `.Storage`, or `.KeyVault` integration. Add `.Network` and, when needed, `.PrivateDns` for separately modeled networking. Referencing their hosting dependencies alone does not enable those SDK proxies.

## Additional documentation

* [Hosting integration source](../Aspire.Hosting.Azure.AppContainers/)
* [Provisioning inventory and shared expressions](../Aspire.Hosting.Azure.Provisioning/README.md)
* https://aspire.dev/integrations/gallery/
* https://learn.microsoft.com/azure/container-apps/
* https://learn.microsoft.com/dotnet/azure/sdk/provisioning/

## Feedback & contributing

https://github.com/microsoft/aspire
