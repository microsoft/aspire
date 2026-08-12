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

## Customize generated Azure infrastructure from TypeScript

TypeScript AppHosts can customize the Azure infrastructure that Aspire generates before deployment. Install the Azure provisioning packages for serialization and for each resource type that the callback modifies:

```bash
npm install @azure/provisioning-serialization @azure/provisioning-storage
```

Importing the service package registers its resource types with the deserializer. The callback can then locate typed resources, modify them, and return the serialized infrastructure:

```typescript
import { deserialize, serialize } from "@azure/provisioning-serialization";
import { StorageAccount } from "@azure/provisioning-storage";
import { createBuilder } from "./.aspire/modules/aspire.mjs";

const builder = await createBuilder();
const storage = await builder.addAzureStorage("storage");

await storage.configureInfrastructure(async ({ infrastructureJson }) => {
    if (!infrastructureJson) {
        throw new Error("Aspire did not provide the generated infrastructure.");
    }

    const [infrastructure] = deserialize(infrastructureJson);
    const storageAccount = infrastructure?.getResources(StorageAccount)[0];
    if (!storageAccount) {
        throw new Error("The generated infrastructure did not contain a storage account.");
    }

    storageAccount.sku = { name: "Standard_LRS" };
    storageAccount.properties.allowSharedKeyAccess = true;

    return { infrastructureJson: JSON.stringify(serialize([infrastructure])) };
});

await builder.build().run();
```

The callback runs when Aspire generates Bicep for deployment. Use compatible versions of the Azure provisioning npm packages and the `Aspire.Hosting.Azure` integration.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://aspire.dev/integrations/cloud/azure/overview/
* https://learn.microsoft.com/azure/azure-resource-manager/bicep/overview

## Feedback & contributing

https://github.com/microsoft/aspire
