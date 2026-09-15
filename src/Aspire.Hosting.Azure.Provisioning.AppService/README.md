# Azure Provisioning App Service hosting integration

> [!WARNING]
> This package is experimental and emits `ASPIREAZUREPROVISIONING001`.

Use this integration to customize Azure App Service infrastructure from a polyglot Aspire AppHost. It references `Aspire.Hosting.Azure.AppService` and projects a bounded configuration surface from `Azure.Provisioning.AppService`; deployment and resource lifecycle remain with the hosting integration.

## Getting started

### Prerequisites

An Azure subscription when provisioning resources.

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure.Provisioning.AppService` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.Provisioning.AppService
```

## Usage example

Then, in a TypeScript AppHost, customize the App Service plan created by Aspire:

```typescript
import { createBuilder } from "./.aspire/modules/aspire.mjs";

const builder = await createBuilder();
const environment = await builder.addAzureAppServiceEnvironment("apps");
const planIdentifier = `${await environment.getBicepIdentifier()}_asplan`;

await environment.configureInfrastructure(async infrastructure => {
    const plan = await infrastructure.getAppServicePlanByIdentifier(planIdentifier);
    const tags = await plan.tags.get();
    await tags.set("environment", "production");
});
```

The plan's Bicep identifier has an `_asplan` suffix, so the no-argument root lookup is not appropriate here. Website customizations belong in the website publish callback, not the environment callback.

## Scope and limitations

`AppServicePlan` and `WebSite` are the selected roots. Compatible SDK models are projected, not every member. Supported `BicepValue<T>` properties accept the corresponding language value or a shared Bicep expression.

`IPAddresses`, `ExternalInboundIPAddresses`, `InternalInboundIPAddresses`, `LinuxOutboundIPAddresses`, and `WindowsOutboundIPAddresses` are exposed as IP address collection proxies. Writable lists accept IPv4/IPv6 strings or Bicep value handles for add, insert, and set operations. Element getters preserve literals, expressions, and references as Bicep value handles; invalid address strings fail validation rather than being silently dropped. Azure SDK read-only output restrictions still apply.

To customize supporting SDK resources, also opt into `Aspire.Hosting.Azure.Provisioning.ContainerRegistry`, `.ApplicationInsights`, or `.OperationalInsights` as needed. Shared secret infrastructure uses `.KeyVault`; separately modeled subnets use `.Network` (and private DNS uses `.PrivateDns`). These are separate proxy opt-ins, not additional App Service models.

## Additional documentation

* [Hosting integration](../Aspire.Hosting.Azure.AppService/README.md)
* [Provisioning inventory and shared expressions](../Aspire.Hosting.Azure.Provisioning/README.md)
* https://aspire.dev/integrations/gallery/
* https://learn.microsoft.com/azure/app-service/
* https://learn.microsoft.com/dotnet/azure/sdk/provisioning/

## Feedback & contributing

https://github.com/microsoft/aspire
