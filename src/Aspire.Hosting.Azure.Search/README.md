# Azure AI Search hosting integration

Use this integration to model, configure, and orchestrate Azure AI Search Service in an Aspire solution.

## Getting started

### Prerequisites

- Azure subscription - [create one for free](https://azure.microsoft.com/free/)

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure.Search` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.Search
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

In the AppHost, add an Azure AI Search service and reference it from another resource with either C# or TypeScript:

**C#**

```csharp
var search = builder.AddAzureSearch("search");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(search);
```

**TypeScript**

```typescript
const search = await builder.addAzureSearch("search");

const myService = await builder.addNodeApp("myService", "../my-service", "server.js")
                       .withReference(search);
```

## Connection Properties

When you reference an Azure AI Search service using `WithReference`, the following connection properties are made available to the consuming project:

| Property Name | Description |
|---------------|-------------|
| `Uri` | The HTTPS endpoint of the Azure AI Search service in the format `https://{name}.search.windows.net`. |

Aspire exposes each property as an environment variable named `[RESOURCE]_[PROPERTY]`. For instance, the `Uri` property of a resource called `db1` becomes `DB1_URI`.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://aspire.dev/integrations/cloud/azure/azure-ai-search/azure-ai-search-host/
* https://learn.microsoft.com/azure/search/search-what-is-azure-search

## Feedback & contributing

https://github.com/microsoft/aspire
