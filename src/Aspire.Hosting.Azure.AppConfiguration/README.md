# Aspire.Hosting.Azure.AppConfiguration library

Provides extension methods and resource definitions for an Aspire AppHost to configure Azure App Configuration.

## Getting started

### Prerequisites

- Azure subscription - [create one for free](https://azure.microsoft.com/free/)

### Install the package

In your AppHost project, install the Aspire Azure App Configuration Hosting library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Azure.AppConfiguration
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

Then, in the _AppHost.cs_ file of `AppHost`, add an App Configuration connection and consume the connection using the following methods:

```csharp
var appConfig = builder.AddAzureAppConfiguration("config");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(appConfig);
```

> NOTE: Consider setting the name of your resource to something other than "config" or "appconfig". Even though during deployment a random suffix will be added it is still possible to get a name collision.

## Additional documentation

* https://learn.microsoft.com/azure/azure-app-configuration/

## Feedback & contributing

https://github.com/microsoft/aspire
