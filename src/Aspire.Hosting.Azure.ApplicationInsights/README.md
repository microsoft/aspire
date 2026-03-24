# Aspire.Hosting.Azure.ApplicationInsights library

Provides extension methods and resource definitions for an Aspire AppHost to configure Azure Application Insights.

## Getting started

### Prerequisites

- Azure subscription - [create one for free](https://azure.microsoft.com/free/)

### Install the package

In your AppHost project, install the Aspire Azure Application Insights Hosting library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Azure.ApplicationInsights
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

Then, in the _AppHost.cs_ file of `AppHost`, add an Application Insights connection and consume the connection using the following methods:

```csharp
var appInsights = builder.AddAzureApplicationInsights("appInsights");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(appInsights);
```

## Additional documentation

* https://learn.microsoft.com/azure/azure-monitor/app/app-insights-overview

## Feedback & contributing

https://github.com/microsoft/aspire
