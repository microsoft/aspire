# Azure Key Vault hosting integration

Use this integration to model, configure, and provision Azure Key Vault resources in an Aspire solution.

## Getting started

### Prerequisites

* Azure subscription - [create one for free](https://azure.microsoft.com/free/)
* An Aspire project based on the starter template.
 
### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure.KeyVault` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.KeyVault
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

## Usage examples

### Adding a Key Vault resource to the AppHost model

Add a Key Vault resource in the AppHost, then reference it from another resource with `WithReference`.

**C#**

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var keyVault = builder.AddAzureKeyVault("mykeyvault");

builder.AddProject<Projects.MyApp>("myapp")
       .WithReference(keyVault);
```

**TypeScript**

```typescript
import { createBuilder } from "./.aspire/modules/aspire.mjs";

const builder = await createBuilder();

const keyVault = await builder.addAzureKeyVault("mykeyvault");

await builder.addNodeApp("myapp", "../my-app", "server.js")
    .withReference(keyVault);
```

## Connection Properties

When you reference Azure Key Vault resources using `WithReference`, the following connection properties are made available to the consuming project:

| Property Name | Description |
|---------------|-------------|
| `Uri`         | The Key Vault endpoint URI, typically `https://<vault-name>.vault.azure.net/` |

Aspire exposes each property as an environment variable named `[RESOURCE]_[PROPERTY]`. For instance, the `Uri` property of a resource called `db1` becomes `DB1_URI`.

### Customizing the Azure Key Vault resource

The `builder.AddAzureKeyVault(...)` extension method has an overload that allows for customization of the Key Vault resource that is created. In the below example an Aspire parameter is defined which is then assigned to the value of a Key Vault secret which is created at provisioning time.

```csharp
var builder = DistributedApplication.CreateBuilder(args);
builder.AddAzureProvisioning();

var webhookSigningSharedSecret = builder.AddParameter("webhooksecret", secret: true);

var keyVault = builder.AddAzureKeyVault("mykeyvault", (_, construct, kv) => {

  // Create a secret and assign an parameter resource to its value.
  var secret = new KeyVaultSecret(construct, "secret");
  secret.AssignProperty(x => x.Properties.Value, webhookSigningSharedSecret);

});

builder.AddProject<Projects.MyApp>("myapp")
       .WithReference(keyVault);
```

## Additional documentation

https://aspire.dev/integrations/gallery/
https://aspire.dev/integrations/cloud/azure/azure-key-vault/azure-key-vault-host/

## Feedback & contributing

https://github.com/microsoft/aspire
