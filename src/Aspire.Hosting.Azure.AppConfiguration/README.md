# Azure App Configuration hosting integration

Use this integration to model, configure, and orchestrate Azure App Configuration in an Aspire solution.

## Getting started

### Prerequisites

- Azure subscription - [create one for free](https://azure.microsoft.com/free/)

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure.AppConfiguration` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.AppConfiguration
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

In the AppHost, add an App Configuration connection and reference it from another resource with either C# or TypeScript:

**C#**

```csharp
var appConfig = builder.AddAzureAppConfiguration("config");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(appConfig);
```

**TypeScript**

```typescript
const appConfig = await builder.addAzureAppConfiguration("config");

const myService = await builder.addNodeApp("myService", "../my-service", "server.js")
                       .withReference(appConfig);
```

> NOTE: Consider setting the name of your resource to something other than "config" or "appconfig". Even though during deployment a random suffix will be added it is still possible to get a name collision.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://aspire.dev/integrations/cloud/azure/azure-app-configuration/azure-app-configuration-host/
* https://learn.microsoft.com/azure/azure-app-configuration/

## Feedback & contributing

https://github.com/microsoft/aspire
