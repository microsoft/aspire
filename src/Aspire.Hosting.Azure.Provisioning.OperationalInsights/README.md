# Azure Provisioning Operational Insights hosting integration

> [!WARNING]
> This package is experimental and emits `ASPIREAZUREPROVISIONING001`.

Use this integration to customize Azure Log Analytics infrastructure from a polyglot Aspire AppHost.

## Getting started

### Prerequisites

An Azure subscription.

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure.Provisioning.OperationalInsights` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.Provisioning.OperationalInsights
```

## Usage example

Then, in a TypeScript AppHost, customize the Log Analytics workspace created by Aspire:

```typescript
import { createBuilder } from "./.aspire/modules/aspire.mjs";

const builder = await createBuilder();
const logs = await builder.addAzureLogAnalyticsWorkspace("logs");

await logs.configureInfrastructure(async infrastructure => {
    const workspace = await infrastructure.getOperationalInsightsWorkspace();
    const tags = await workspace.tags.get();
    await tags.set("environment", "production");
});
```

The generated proxy exposes an intentionally bounded Azure Provisioning Operational Insights API. Properties backed by `BicepValue<T>` accept either the corresponding language primitive or a shared Bicep expression.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://learn.microsoft.com/azure/azure-monitor/logs/log-analytics-workspace-overview
* https://learn.microsoft.com/dotnet/azure/sdk/provisioning/

## Feedback & contributing

https://github.com/microsoft/aspire
