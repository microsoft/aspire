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

## Deploying with short-lived federated credentials

Before provisioning Azure resources, Aspire acquires the ARM token and the token scopes
declared by selected Azure deployment steps. ACR login steps declare the ACR scope, including
for existing and automatically created registries. Provisioning-only targets do not acquire
ACR tokens unless registry login is in their selected dependency graph. The configured
principal provider also resolves the deployment identity before resource-group creation.

This reduces failures when a federated assertion expires during provisioning, provided the
configured credential caches audience-specific access tokens. `AzureCliCredential` asks the
Azure CLI for tokens; reuse depends on the CLI/MSAL token cache, not the credential object's
lifetime. Custom credentials retain their own caching behavior.

Pre-acquisition does not renew a federated assertion or extend an access token's lifetime.
If the assertion is already expired, or deployment outlasts the cached access token, fresh
authentication is still required. See [Azure CLI federated-token limitations](https://github.com/Azure/azure-cli/issues/28708).
Registry token exchange and container-runtime login still occur after provisioning, when
the registry endpoint is available. Offline publishing and resource destruction do not
perform this deployment pre-acquisition.

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
