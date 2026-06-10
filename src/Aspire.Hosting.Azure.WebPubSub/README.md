# Azure Web PubSub hosting integration

Use this integration to model, configure, and orchestrate Azure Web PubSub in an Aspire solution.

## Getting started

### Prerequisites

- Azure subscription - [create one for free](https://azure.microsoft.com/free/)

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure.WebPubSub` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.WebPubSub
```

## Usage example

In the AppHost, add a WebPubSub connection and reference it from another resource with either C# or TypeScript:

**C#**

```csharp
var wps = builder.AddAzureWebPubSub("wps1");

var web = builder.AddProject<Projects.WebPubSubWeb>("webfrontend")
                       .WithReference(wps);
```

**TypeScript**

```typescript
const wps = await builder.addAzureWebPubSub("wps1");

const web = await builder.addNodeApp("webfrontend", "../web-frontend", "server.js")
                       .withReference(wps);
```

## Connection Properties

When you reference an Azure Web PubSub resource using `WithReference`, the following connection properties are made available to the consuming project:

| Property Name | Description |
|---------------|-------------|
| `Uri` | The HTTPS endpoint for the Web PubSub service, typically `https://<resource-name>.webpubsub.azure.com/` |

Aspire exposes each property as an environment variable named `[RESOURCE]_[PROPERTY]`. For instance, the `Uri` property of a resource called `wps1` becomes `WPS1_URI`.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://aspire.dev/integrations/cloud/azure/azure-web-pubsub/azure-web-pubsub-host/
* https://learn.microsoft.com/azure/azure-web-pubsub/overview

## Feedback & contributing

https://github.com/microsoft/aspire
