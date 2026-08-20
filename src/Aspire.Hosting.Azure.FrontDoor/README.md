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

### Active-active regional deployment

One logical compute resource can be deployed to several Azure Container Apps environments and
exposed through one Front Door endpoint. The environments must use one explicitly shared Azure
Container Registry. Add the `Aspire.Hosting.Azure.AppContainers` and
`Aspire.Hosting.Azure.ContainerRegistry` integrations before using this scenario.

**C#**

```csharp
#pragma warning disable ASPIRECOMPUTE003
#pragma warning disable ASPIRECOMPUTE004
#pragma warning disable ASPIREAZURERG001

var registry = builder.AddAzureContainerRegistry("registry");

var eastGroup = builder.AddAzureResourceGroup("app-east-rg", "eastus2");
var westGroup = builder.AddAzureResourceGroup("app-west-rg", "westus3");

var east = builder.AddAzureContainerAppEnvironment("east")
    .WithLocation("eastus2")
    .WithResourceGroup(eastGroup)
    .WithAzureContainerRegistry(registry);

var west = builder.AddAzureContainerAppEnvironment("west")
    .WithLocation("westus3")
    .WithResourceGroup(westGroup)
    .WithAzureContainerRegistry(registry);

var api = builder.AddProject<Projects.Api>("api")
    .WithExternalHttpEndpoints()
    .WithContainerRegistry(registry)
    .WithComputeEnvironments([east, west]);

var frontDoor = builder.AddAzureFrontDoor("frontdoor")
    .WithOrigin(api);
```

**TypeScript**

```typescript
const registry = await builder.addAzureContainerRegistry("registry");

const eastGroup = await builder.addAzureResourceGroup("app-east-rg", "eastus2");
const westGroup = await builder.addAzureResourceGroup("app-west-rg", "westus3");

const east = await builder.addAzureContainerAppEnvironment("east")
    .withLocation("eastus2")
    .withResourceGroup(eastGroup)
    .withAzureContainerRegistry(registry);

const west = await builder.addAzureContainerAppEnvironment("west")
    .withLocation("westus3")
    .withResourceGroup(westGroup)
    .withAzureContainerRegistry(registry);

const api = await builder.addNodeApp("api", "../api", "server.js")
    .withExternalHttpEndpoints()
    .withContainerRegistry(registry)
    .withComputeEnvironments([east, west]);

const frontDoor = await builder.addAzureFrontDoor("frontdoor")
    .withOrigin(api);
```

The app runs once during local development and is replicated only during publish and deploy.
Each regional resource group is owned by Aspire and removed by `aspire destroy`. The regional
Container Apps endpoints remain publicly reachable and can bypass Front Door. This integration
does not automatically configure Front Door-only ingress, WAF policies, custom domains, or
Azure Container Registry geo-replication.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://aspire.dev/integrations/cloud/azure/azure-front-door/
* https://learn.microsoft.com/azure/frontdoor/

## Feedback & contributing

https://github.com/microsoft/aspire
