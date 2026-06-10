# Azure OpenAI hosting integration

Use this integration to model, configure, and orchestrate Azure OpenAI in an Aspire solution.

## Getting started

### Prerequisites

- Azure subscription - [create one for free](https://azure.microsoft.com/free/)

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure.CognitiveServices` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.CognitiveServices
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

In the AppHost, add an Azure OpenAI service and reference it from another resource with either C# or TypeScript:

**C#**

```csharp
var openai = builder.AddAzureOpenAI("openai");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(openai);
```

**TypeScript**

```typescript
const openai = await builder.addAzureOpenAI("openai");

const myService = await builder.addNodeApp("myService", "../my-service", "server.js")
                       .withReference(openai);
```

## Connection Properties

When you reference Azure OpenAI resources using `WithReference`, the following connection properties are made available to the consuming project:

### Azure OpenAI resource

The Azure OpenAI resource exposes the following connection properties:

| Property Name | Description |
|---------------|-------------|
| `Uri`         | The endpoint URI for the Azure OpenAI resource, typically `https://<resource-name>.openai.azure.com/` |

### Azure OpenAI deployment

The Azure OpenAI deployment resource inherits all properties from its parent Azure OpenAI resource and adds:

| Property Name | Description |
|---------------|-------------|
| `ModelName`  | The name of the Azure OpenAI deployment, e.g., `chat` |

Aspire exposes each property as an environment variable named `[RESOURCE]_[PROPERTY]`. For instance, the `Uri` property of a resource called `chat` becomes `CHAT_URI`.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://aspire.dev/integrations/cloud/azure/azure-openai/azure-openai-host/
* https://learn.microsoft.com/azure/ai-services/openai/

## Feedback & contributing

https://github.com/microsoft/aspire
