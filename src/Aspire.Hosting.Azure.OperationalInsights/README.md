# Azure Log Analytics hosting integration

Use this integration to model, configure, and orchestrate Azure Log Analytics in an Aspire solution.

## Getting started

### Prerequisites

- Azure subscription - [create one for free](https://azure.microsoft.com/free/)

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure.OperationalInsights` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.OperationalInsights
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

In the AppHost, add an Azure Log Analytics workspace and pass the workspace ID via an environment variable:

```csharp
var laws = builder.AddAzureLogAnalyticsWorkspace("laws");

var myService = builder.AddProject<Projects.MyService>()
                       .WithEnvironment("LOG_ANALYTICS_WORKSPACE_ID", $"{laws.WorkspaceId}");
```

> NOTE: By default a log analytics workspace will be created automatically when deploying an Aspire application
> via the Azure Developer CLI. Use this resource only if your application code directly integrates with
> Azure Log Analytics.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://aspire.dev/integrations/cloud/azure/azure-log-analytics/
* https://learn.microsoft.com/azure/azure-monitor/logs/log-analytics-workspace-overview

## Feedback & contributing

https://github.com/microsoft/aspire
