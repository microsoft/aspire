# Azure Front Door hosting integration

Use this integration to model, configure, and orchestrate an Azure Front Door resource in an Aspire solution.

## Getting started

### Prerequisites

- An Azure subscription - [create one for free](https://azure.microsoft.com/free/)

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure.FrontDoor` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.FrontDoor
```

## Usage example

In the AppHost, add an Azure Front Door resource and configure origins with either C# or TypeScript:

**C#**

```csharp
var api = builder.AddProject<Projects.Api>("api")
    .WithExternalHttpEndpoints();

var frontDoor = builder.AddAzureFrontDoor("frontdoor")
    .WithOrigin(api);
```

**TypeScript**

```typescript
const api = await builder.addNodeApp("api", "../api", "server.js")
    .withExternalHttpEndpoints();

const frontDoor = await builder.addAzureFrontDoor("frontdoor")
    .withOrigin(api);
```

## Resource naming compatibility

The integration preserves the Azure resource naming rules from `Azure.Provisioning.Cdn` 1.0.0-beta.2 when using 1.0.0-beta.3. Profiles, endpoints, origin groups, origins, and routes created by the integration retain their generated names, avoiding resource replacement or endpoint hostname changes solely from this package update. Custom naming resolvers remain supported, and names assigned through `ConfigureInfrastructure` are not overwritten.

This compatibility behavior applies to resources created by the integration, not additional SDK resources created in `ConfigureInfrastructure` callbacks.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://aspire.dev/integrations/cloud/azure/azure-front-door/
* https://learn.microsoft.com/azure/frontdoor/

## Feedback & contributing

https://github.com/microsoft/aspire
