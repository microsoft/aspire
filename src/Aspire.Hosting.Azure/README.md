# Aspire.Hosting.Azure library

Provides core extensions to the Aspire hosting model for Azure services.

## Getting started

### Prerequisites

- Azure subscription - [create one for free](https://azure.microsoft.com/free/)

### Install the package

In your AppHost project, install the Aspire Azure Hosting library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Azure
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

Then, in the _AppHost.cs_ file of `AppHost`, add a resource based on a Bicep template:

```csharp
var bicepResource = builder.AddBicepTemplate("bicep", "template.bicep")
                           .WithParameter("parametername", "parametervalue");
});

```

## Additional documentation

* https://aspire.dev/integrations/cloud/azure/overview/
* https://learn.microsoft.com/azure/azure-resource-manager/bicep/overview

## Feedback & contributing

https://github.com/microsoft/aspire
