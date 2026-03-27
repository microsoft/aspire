# Aspire.Hosting.Azure.OperationalInsights library

Provides extension methods and resource definitions for an Aspire AppHost to configure Azure Log Analytics.

## Getting started

### Prerequisites

- Azure subscription - [create one for free](https://azure.microsoft.com/free/)

### Install the package

Install the Aspire Azure Operational Insights Hosting library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Azure.OperationalInsights
```

## Configure Azure Provisioning for local development

Adding Azure resources to the Aspire application model will automatically enable development-time provisioning
for Azure resources so that you don't need to configure them manually. Provisioning requires a number of settings
to be available via .NET configuration. Set these values in user secrets in order to allow resources to be configured
automatically.

```json
{
    "Azure": {
      "SubscriptionId": "<your subscription id>",
      "ResourceGroupPrefix": "<prefix for the resource group>",
      "Location": "<azure location>"
    }
}
```

> NOTE: Developers must have Owner access to the target subscription so that role assignments
> can be configured for the provisioned resources.

## Usage example

Then, in the _AppHost.cs_ file of `AppHost`, add an Azure Log Analytics workspace and pass the workspace ID via an environment variable:

```csharp
var laws = builder.AddAzureLogAnalyticsWorkspace("laws");

var myService = builder.AddProject<Projects.MyService>()
                       .WithEnvironment("LOG_ANALYTICS_WORKSPACE_ID", $"{laws.WorkspaceId}");
```

> NOTE: By default a log analytics workspace will be created automatically when deploying an Aspire application
> via the Azure Developer CLI. Use this resource only if your application code directly integrates with
> Azure Log Analytics.

## Additional documentation

* https://learn.microsoft.com/azure/azure-monitor/logs/log-analytics-workspace-overview

## Feedback & contributing

https://github.com/microsoft/aspire
