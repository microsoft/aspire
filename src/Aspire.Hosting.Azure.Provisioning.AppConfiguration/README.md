# Azure Provisioning App Configuration hosting integration

> [!WARNING]
> This package is experimental and emits `ASPIREAZUREPROVISIONING001`.

Use this integration to customize Azure App Configuration infrastructure from a polyglot Aspire AppHost. It references `Aspire.Hosting.Azure.AppConfiguration` and projects a bounded configuration surface from `Azure.Provisioning.AppConfiguration`; it does not add a separate resource lifecycle.

## Getting started

### Prerequisites

An Azure subscription when provisioning resources.

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure.Provisioning.AppConfiguration` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.Provisioning.AppConfiguration
```

## Usage example

Then, in a TypeScript AppHost, customize the App Configuration store created by Aspire:

```typescript
import { createBuilder } from "./.aspire/modules/aspire.mjs";

const builder = await createBuilder();
const appConfig = await builder.addAzureAppConfiguration("appconfig");

await appConfig.configureInfrastructure(async infrastructure => {
    const store = await infrastructure.getAppConfigurationStore();
    const tags = await store.tags.get();
    await tags.set("environment", "production");
});
```

## Scope and limitations

`AppConfigurationStore` is the selected infrastructure root. Compatible models from the same SDK assembly are also projected, not every SDK member. Supported `BicepValue<T>` properties accept the corresponding language value or a shared Bicep expression. Customizations affect generated Azure infrastructure; local execution remains the hosting integration's responsibility.

## Additional documentation

* [Hosting integration](../Aspire.Hosting.Azure.AppConfiguration/README.md)
* [Provisioning inventory and shared expressions](../Aspire.Hosting.Azure.Provisioning/README.md)
* https://aspire.dev/integrations/gallery/
* https://learn.microsoft.com/azure/azure-app-configuration/
* https://learn.microsoft.com/dotnet/azure/sdk/provisioning/

## Feedback & contributing

https://github.com/microsoft/aspire
