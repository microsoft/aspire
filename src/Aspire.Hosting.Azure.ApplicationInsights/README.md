# Azure Application Insights hosting integration

Use this integration to model, configure, and orchestrate Azure Application Insights in an Aspire solution.

## Getting started

### Prerequisites

- Azure subscription - [create one for free](https://azure.microsoft.com/free/)

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure.ApplicationInsights` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.ApplicationInsights
```

## Configure Azure Provisioning for local development

Adding Azure resources to the AppHost model will automatically enable development-time provisioning
for Azure resources so that you don't need to configure them manually. Provisioning requires a number of settings
to be available via AppHost configuration. From your AppHost directory, set these values with `aspire secret set`:

```bash
aspire secret set Azure:SubscriptionId "<your subscription id>"
aspire secret set Azure:ResourceGroupPrefix "<prefix for the resource group>"
aspire secret set Azure:Location "<azure location>"
```

> NOTE: Developers must have Owner access to the target subscription so that role assignments
> can be configured for the provisioned resources.

## Usage example

In the AppHost, add an Application Insights connection and reference it from another resource with either C# or TypeScript:

**C#**

```csharp
var appInsights = builder.AddAzureApplicationInsights("appInsights");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(appInsights);
```

**TypeScript**

```typescript
const appInsights = await builder.addAzureApplicationInsights("appInsights");

const myService = await builder.addNodeApp("myService", "../my-service", "server.js")
                       .withReference(appInsights);
```

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://aspire.dev/integrations/cloud/azure/azure-application-insights/
* https://learn.microsoft.com/azure/azure-monitor/app/app-insights-overview

## Feedback & contributing

https://github.com/microsoft/aspire
