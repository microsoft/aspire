# Azure Key Vault extensions library for Aspire application model

Azure Key Vault is a cloud service that provides a secure storage of secrets, such as passwords and database connection strings.

The Azure Key Vault extensions library allows you to extend the Aspire application model to support to provisioning Key Vaults as part of application development and testing.

## Getting started

### Prerequisites

* Azure subscription - [create one for free](https://azure.microsoft.com/free/)
* An Aspire project based on the starter template.
 
### Install the package

Install the Azure Key Vault extensions library for Aspire application model with [NuGet](https://www.nuget.org/packages/Aspire.Hosting.Azure.KeyVault):

```dotnetcli
dotnet add package Aspire.Hosting.Azure.KeyVault
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

## Usage examples

### Adding a Key Vault resource to the Aspire application model

In order to provision a Key Vault resource as part of an Aspire application you need to add the resource via the `IDistributedApplicationBuilder` interface. The `builder.AddAzureKeyVault(...)` extension method is used to register a Key Vault resource with the application model. Then use the `WithReference` extension method on a resource to inject the necessary connection string information for accessing Key Vault into the application that depends on it.

```csharp
var builder = DistributedApplication.CreateBuilder(args);
builder.AddAzureProvisioner();

var keyVault = builder.AddAzureKeyVault("mykeyvault");

builder.AddProject<Projects.MyApp>("myapp")
       .WithReference(keyVault);
```

Inside the the implementation of the application that depends on Key Vault (MyApp in this case) add the `Aspire.Azure.Security.KeyVault` package and follow the instructions in that package README to use the connection string that was injected by the code above.

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
