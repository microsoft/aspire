# Azure provisioning integration

Provides core extensions to the Aspire hosting model for Azure services.

## Getting started

### Prerequisites

- Azure subscription - [create one for free](https://azure.microsoft.com/free/)

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure
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

In the AppHost, add a resource based on a Bicep template:

**C#**

```csharp
var bicepResource = builder.AddBicepTemplate("bicep", "template.bicep")
                           .WithParameter("parametername", "parametervalue");
```

**TypeScript**

```typescript
const bicepResource = await builder.addBicepTemplate("bicep", "template.bicep")
                           .withParameter("parametername", "parametervalue");
```

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://aspire.dev/integrations/cloud/azure/overview/
* https://learn.microsoft.com/azure/azure-resource-manager/bicep/overview

## Feedback & contributing

https://github.com/microsoft/aspire
