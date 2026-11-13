# Azure Provisioning Application Insights hosting integration

> [!WARNING]
> This package is experimental and emits `ASPIREAZUREPROVISIONING001`.

Use this integration to customize Azure Application Insights infrastructure from a polyglot Aspire AppHost.

## Getting started

### Prerequisites

An Azure subscription.

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure.Provisioning.ApplicationInsights` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.Provisioning.ApplicationInsights
```

## Usage example

Then, in a TypeScript AppHost, customize the Application Insights component created by Aspire:

```typescript
import { createBuilder } from "./.aspire/modules/aspire.mjs";

const builder = await createBuilder();
const insights = await builder.addAzureApplicationInsights("insights");

await insights.configureInfrastructure(async infrastructure => {
    const component = await infrastructure.getApplicationInsightsComponent();
    const tags = await component.tags.get();
    await tags.set("environment", "production");
});
```

The generated proxy exposes an intentionally bounded Azure Provisioning Application Insights API. Properties backed by `BicepValue<T>` accept either the corresponding language primitive or a shared Bicep expression.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://learn.microsoft.com/azure/azure-monitor/app/app-insights-overview
* https://learn.microsoft.com/dotnet/azure/sdk/provisioning/

## Feedback & contributing

https://github.com/microsoft/aspire
