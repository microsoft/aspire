# Azure SignalR Service hosting integration

Use this integration to model, configure, and orchestrate Azure SignalR in an Aspire solution.

## Getting started

### Prerequisites

- Azure subscription - [create one for free](https://azure.microsoft.com/free/)

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure.SignalR` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.SignalR
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

In the AppHost, add a SignalR connection and reference it from another resource with either C# or TypeScript:

**C#**

```csharp
var signalR = builder.AddAzureSignalR("sr");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(signalR);
```

**TypeScript**

```typescript
const signalR = await builder.addAzureSignalR("sr");

const myService = await builder.addNodeApp("myService", "../my-service", "server.js")
                       .withReference(signalR);
```

## Connection Properties

When you reference an Azure SignalR resource using `WithReference`, the following connection properties are made available to the consuming project:

| Property Name | Description |
|---------------|-------------|
| `Uri` | The connection URI for the SignalR service, with the format `https://{host}` in Azure (typically `https://<resource-name>.service.signalr.net`) or the emulator-provided endpoint when running locally |

Aspire exposes each property as an environment variable named `[RESOURCE]_[PROPERTY]`. For instance, the `Uri` property of a resource called `sr` becomes `SR_URI`.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://aspire.dev/integrations/cloud/azure/azure-signalr/azure-signalr-host/
* https://learn.microsoft.com/azure/azure-signalr/signalr-overview

## Feedback & contributing

https://github.com/microsoft/aspire
